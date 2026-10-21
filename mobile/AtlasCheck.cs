using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using HarmonyLib;
using Verse;
using Waylith.Graphics;
using Waylith.Rendering;

namespace PortMobile
{
	/// <summary>
	/// Every static atlas page checked once, level by level, and a level that lost what the level above it shows made
	/// again from that level.
	///
	/// A tester's Adreno 720 (vivo, Snapdragon 7 Gen 3) had an Item page of 8192x4096 whose level 1 was empty while
	/// levels 0, 2 and 3 were whole (记录问题 with the thing selected: MarkThings): everything on that page vanished at
	/// the zoom that samples level 1, the closest one, and came back further out. The page came from the engine's atlas
	/// cache, so it stayed that way at every start; the OnePlus with the same mods makes the same page correctly. Which
	/// step failed there (the mip chain the engine computes for a drawn page, its BC7 encoding on the GPU, or the cache)
	/// is not known yet: the log says whether a broken page was made in this run or came from the cache.
	///
	/// When the game has loaded (the atlases are baked by then): each page is read back from the GPU (BC7, ASTC 4x4 or
	/// RGBA8), and at 128x64 points spread over it every level is compared with the one above: a level is broken when,
	/// of the points that the level above covers (alpha over half), more than 5% are empty in it (alpha under 1/16).
	/// A broken level is made again on the processor from the level above (decoded, halved, encoded as the page is:
	/// the engine's CPU BC7 encoder or astcenc), the page's image replaced and read back once more for the log, and the
	/// cache entry written again, so the next start finds it whole. A page found whole is noted by its cache key
	/// (TextureCache/atlas-checked.txt) and not read again. On the OnePlus, 23 pages (8192x4096 and smaller): 0.6 s
	/// for BC7, 0.9 s for ASTC, once; a level of 4096x2048 made again in 0.3 s (BC7) / 0.5 s (ASTC).
	/// mobile.txt: atlasCheck=0 leaves it out; atlasCheckTest=Item empties level 1 of the first Item page first, to
	/// see the repair work.
	/// </summary>
	internal static class AtlasCheck
	{
		[DllImport("portastc")] private static extern int port_astc_decode(byte[] blocks, UIntPtr blocksLength, int width, int height, int srgb, byte[] rgba);

		private const int SamplesX = 128, SamplesY = 64;
		private static byte[][] readback;

		public static void Start()
		{
			if (Settings.Get("atlasCheck", 1) == 0)
			{
				return;
			}
			try
			{
				var atlases = AccessTools.Field(typeof(GlobalTextureAtlasManager), "staticTextureAtlases")?.GetValue(null) as List<StaticTextureAtlas>;
				if (atlases == null)
				{
					Console.WriteLine("[mobile] atlas check: the game's list of static atlases is not there; nothing checked");
					return;
				}
				string root = Waylith.Runtime.WaylithRuntime.Current?.App.CacheDataPath;
				string list = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "TextureCache", "atlas-checked.txt");
				var checkedKeys = new HashSet<string>(list != null && File.Exists(list) ? File.ReadAllLines(list) : new string[0]);
				string test = Settings.Get("atlasCheckTest", "");
				if (test.Length > 0)
				{
					checkedKeys.Clear();
					BreakForTest(atlases, test);
				}
				var stopwatch = Stopwatch.StartNew();
				int pages = 0, skipped = 0, repaired = 0;
				var added = new List<string>();
				foreach (StaticTextureAtlas atlas in atlases)
				{
					foreach (bool mask in new[] { false, true })
					{
						UnityEngine.Texture2D page = mask ? atlas.MaskTexture : atlas.ColorTexture;
						if (page?.CoreTexture?.GpuResource == null)
						{
							continue;
						}
						string key = atlas.CacheKey(mask);
						string id = key == null ? null : Hash(key + " " + page.CoreTexture.GpuResource.Format + " " + page.CoreTexture.GpuResource.MipCount);
						if (id != null && checkedKeys.Contains(id))
						{
							skipped++;
							continue;
						}
						pages++;
						bool whole = Check(page, key, atlas.AdoptedFromCache, ref repaired);
						if (whole && id != null)
						{
							added.Add(id);
						}
					}
				}
				readback = null;
				if (added.Count > 0 && list != null)
				{
					File.AppendAllLines(list, added);
				}
				if (pages > 0)
				{
					Console.WriteLine("[mobile] atlas check: " + pages + " pages checked (" + skipped + " known whole), " + repaired + " levels made again, " + stopwatch.ElapsedMilliseconds + " ms");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] atlas check failed: " + e);
			}
		}

		/// <summary>
		/// mobile.txt atlasCheckTest=Item (a part of a page's name): level 1 of the first such block-compressed page is
		/// emptied on the GPU, as the tester's was, for the check to find and make again.
		/// </summary>
		private static void BreakForTest(List<StaticTextureAtlas> atlases, string name)
		{
			foreach (StaticTextureAtlas atlas in atlases)
			{
				UnityEngine.Texture2D page = atlas.ColorTexture;
				GpuTextureAllocation gpu = page?.CoreTexture?.GpuResource;
				if (gpu == null || page.name == null || !page.name.Contains(name) || gpu.MipCount < 2 || (gpu.Format != VkFormat.Bc7UNormBlock && gpu.Format != VkFormat.Astc4X4UNormBlock && gpu.Format != VkFormat.Astc4X4SRGBBlock))
				{
					continue;
				}
				int width = page.width, height = page.height, mipCount = gpu.MipCount;
				byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, 1);
				var uploads = new TextureLevelUpload[mipCount];
				for (int i = 0; i < mipCount; i++)
				{
					int w = Math.Max(1, width >> i), h = Math.Max(1, height >> i);
					byte[] level = levels[i].AsSpan(0, ((w + 3) / 4) * ((h + 3) / 4) * 16).ToArray();
					if (i == 1)
					{
						// BC7 all zero is a block of mode "none": transparent black. ASTC needs a block of its own: the
						// void-extent block of transparent black (0xFC 0xFD then all ones, colour zero).
						Array.Clear(level, 0, level.Length);
						if (gpu.Format != VkFormat.Bc7UNormBlock)
						{
							for (int b = 0; b < level.Length; b += 16)
							{
								level[b] = 0xFC;
								level[b + 1] = 0xFD;
								for (int k = 2; k < 8; k++)
								{
									level[b + k] = 0xFF;
								}
							}
						}
					}
					uploads[i] = new TextureLevelUpload(i, w, h, 1, 0, level, level.Length);
				}
				GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.name, width, height, mipCount, gpu.Format, uploads, GpuMemoryCategory.Atlas);
				if (image != null)
				{
					page.CoreTexture.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, mipCount);
					Console.WriteLine("[mobile] atlas check: test: level 1 of " + page.name + " emptied");
				}
				return;
			}
		}

		private static string Hash(string text)
		{
			var hash = new System.IO.Hashing.XxHash3();
			hash.Append(System.Text.Encoding.UTF8.GetBytes(text));
			return hash.GetCurrentHashAsUInt64().ToString("x16");
		}

		private enum Kind { Bc7, Astc, Rgba }

		/// <summary>Whether the page is whole (after any repair).</summary>
		private static bool Check(UnityEngine.Texture2D page, string key, bool fromCache, ref int repaired)
		{
			Waylith.Rendering.Texture2D core = page.CoreTexture;
			GpuTextureAllocation gpu = core.GpuResource;
			Kind kind;
			switch (gpu.Format)
			{
			case VkFormat.Bc7UNormBlock:
			case VkFormat.Bc7SRGBBlock:
				kind = Kind.Bc7;
				break;
			case VkFormat.Astc4X4UNormBlock:
			case VkFormat.Astc4X4SRGBBlock:
				kind = Kind.Astc;
				break;
			case VkFormat.R8G8B8A8UNorm:
			case VkFormat.R8G8B8A8SRGB:
				kind = Kind.Rgba;
				break;
			default:
				return true;
			}
			bool srgb = gpu.Format == VkFormat.Bc7SRGBBlock || gpu.Format == VkFormat.Astc4X4SRGBBlock || gpu.Format == VkFormat.R8G8B8A8SRGB;
			int width = page.width, height = page.height, mipCount = gpu.MipCount;
			if (mipCount < 2 || (width >> (mipCount - 1)) < 4 || (height >> (mipCount - 1)) < 4)
			{
				return true;
			}
			byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, kind == Kind.Rgba ? 4 : 1, readback);
			if (levels == null)
			{
				return false;
			}
			readback = levels;
			bool changed = false;
			byte[][] fixedLevels = null;
			for (int level = 1; level < mipCount; level++)
			{
				int lost = Lost(levels, level, width, height, kind, srgb, out int covered);
				if (covered < 20 || lost * 20 <= covered)
				{
					continue;
				}
				var stopwatch = Stopwatch.StartNew();
				int aboveW = Math.Max(1, width >> (level - 1)), aboveH = Math.Max(1, height >> (level - 1));
				int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
				byte[] above = Decode(levels[level - 1], aboveW, aboveH, kind, srgb);
				var rgba = new byte[w * h * 4];
				CpuBlockEncoder.Downsample(above, aboveW, aboveH, rgba, w, h, parallel: true);
				byte[] remade = kind == Kind.Bc7 ? CpuBlockEncoder.EncodeBc7Levels(new[] { rgba }, w, h, 1, parallel: true)[0] : kind == Kind.Astc ? PortHooks.Astc.EncodeAtlasLevel(rgba, w, h, srgb) : rgba;
				if (remade == null)
				{
					Console.WriteLine("[mobile] atlas check: " + page.name + " level " + level + " could not be encoded again");
					return false;
				}
				// The readback arrays are reused for the next page: the page's own levels are copies.
				fixedLevels = fixedLevels ?? Array.ConvertAll(levels, l => l);
				fixedLevels[level] = remade;
				levels = fixedLevels;
				changed = true;
				repaired++;
				Console.WriteLine("[mobile] atlas check: " + page.name + " (" + width + "x" + height + ", " + gpu.Format + ", " + (fromCache ? "from the engine's cache" : "made in this run") + "): level " + level + " had lost " + (lost * 100 / covered)
					+ "% of what level " + (level - 1) + " covers; made again from it in " + stopwatch.ElapsedMilliseconds + " ms");
			}
			if (!changed)
			{
				return true;
			}
			// Copies: the readback arrays are used again for the next page, and the cache is written while that goes on.
			var exact = new byte[mipCount][];
			for (int i = 0; i < mipCount; i++)
			{
				int length = kind == Kind.Rgba ? Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4 : ((Math.Max(1, width >> i) + 3) / 4) * ((Math.Max(1, height >> i) + 3) / 4) * 16;
				exact[i] = levels[i].AsSpan(0, length).ToArray();
			}
			if (kind == Kind.Rgba)
			{
				// UploadRgba32 takes the level's own size (the extent of its copy).
				for (int i = 1; i < mipCount; i++)
				{
					GpuTextureService.UploadRgba32(gpu, Math.Max(1, width >> i), Math.Max(1, height >> i), i, exact[i]);
				}
				return true;
			}
			var uploads = new TextureLevelUpload[mipCount];
			for (int i = 0; i < mipCount; i++)
			{
				uploads[i] = new TextureLevelUpload(i, Math.Max(1, width >> i), Math.Max(1, height >> i), 1, 0, exact[i], exact[i].Length);
			}
			GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.name ?? "TextureAtlas", width, height, mipCount, gpu.Format, uploads, GpuMemoryCategory.Atlas);
			if (image == null)
			{
				return false;
			}
			core.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, mipCount);
			// Read back again: what the GPU has now (one more readback, only for a page that was made again).
			byte[][] now = GpuTextureService.ReadbackTexture2DLevels(image, width, height, mipCount, 1);
			var result = new System.Text.StringBuilder();
			for (int level = 1; now != null && level < mipCount; level++)
			{
				int lost = Lost(now, level, width, height, kind, srgb, out int covered);
				result.Append(" level " + level + " " + (covered == 0 ? "-" : (lost * 100 / covered) + "% lost"));
			}
			Console.WriteLine("[mobile] atlas check: " + page.name + " as the GPU has it now:" + (now == null ? " (no readback)" : result.ToString()));
			if (key != null)
			{
				if (kind == Kind.Bc7)
				{
					GpuTextureService.BeginWriteKeyedTextureCache(key, new LoadedTextureData(width, height, mipCount, LoadedTextureFormat.Bc7, exact));
				}
				else
				{
					PortHooks.Astc.RewriteAtlasPage(key, width, height, srgb, exact);
				}
			}
			return true;
		}

		/// <summary>Of the sample points the level above covers, how many are empty in this level.</summary>
		private static int Lost(byte[][] levels, int level, int width, int height, Kind kind, bool srgb, out int covered)
		{
			covered = 0;
			int lost = 0;
			var texels = new byte[64];
			for (int sy = 0; sy < SamplesY; sy++)
			{
				for (int sx = 0; sx < SamplesX; sx++)
				{
					float u = (sx + 0.5f) / SamplesX, v = (sy + 0.5f) / SamplesY;
					if (Alpha(levels[level - 1], level - 1, width, height, u, v, kind, srgb, texels) > 128)
					{
						covered++;
						if (Alpha(levels[level], level, width, height, u, v, kind, srgb, texels) < 16)
						{
							lost++;
						}
					}
				}
			}
			return lost;
		}

		private static int Alpha(byte[] data, int level, int width, int height, float u, float v, Kind kind, bool srgb, byte[] texels)
		{
			int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
			int x = Math.Min(w - 1, (int)(u * w)), y = Math.Min(h - 1, (int)(v * h));
			if (kind == Kind.Rgba)
			{
				return data[(y * w + x) * 4 + 3];
			}
			int offset = ((y / 4) * ((w + 3) / 4) + x / 4) * 16;
			DecodeBlock(data, offset, kind, srgb, texels);
			return texels[((y % 4) * 4 + x % 4) * 4 + 3];
		}

		private static void DecodeBlock(byte[] data, int offset, Kind kind, bool srgb, byte[] texels)
		{
			if (kind == Kind.Bc7)
			{
				Bc7BlockEncoder.DecodeBc7Block(new ReadOnlySpan<byte>(data, offset, 16), texels);
			}
			else
			{
				byte[] block = new ReadOnlySpan<byte>(data, offset, 16).ToArray();
				port_astc_decode(block, (UIntPtr)16, 4, 4, srgb ? 1 : 0, texels);
			}
		}

		/// <summary>A whole level to RGBA8.</summary>
		private static byte[] Decode(byte[] data, int w, int h, Kind kind, bool srgb)
		{
			if (kind == Kind.Rgba)
			{
				return data.Length == w * h * 4 ? data : data.AsSpan(0, w * h * 4).ToArray();
			}
			var rgba = new byte[w * h * 4];
			int blocksX = (w + 3) / 4, blocksY = (h + 3) / 4;
			if (kind == Kind.Astc)
			{
				port_astc_decode(data, (UIntPtr)(blocksX * blocksY * 16), w, h, srgb ? 1 : 0, rgba);
				return rgba;
			}
			Parallel.For(0, blocksY, by =>
			{
				var texels = new byte[64];
				for (int bx = 0; bx < blocksX; bx++)
				{
					Bc7BlockEncoder.DecodeBc7Block(new ReadOnlySpan<byte>(data, (by * blocksX + bx) * 16, 16), texels);
					for (int y = 0; y < 4 && by * 4 + y < h; y++)
					{
						int count = Math.Min(4, w - bx * 4) * 4;
						Buffer.BlockCopy(texels, y * 16, rgba, ((by * 4 + y) * w + bx * 4) * 4, count);
					}
				}
			});
			return rgba;
		}
	}
}
