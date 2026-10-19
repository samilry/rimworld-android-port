using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using HarmonyLib;
using Waylith;
using Waylith.Rendering;

namespace PortHooks
{
	/// <summary>
	/// Block-compressed textures on a GPU that cannot sample them.
	///
	/// The engine's textures are BC formats, the ones every PC graphics card reads: the game's own 3 308 textures in
	/// Assets.zip are BC7 (and 99 are BC4), a mod's .dds files are BC1 / BC3 / BC7, and every other texture is encoded
	/// to BC7 when it loads (Bootstrap.ConfigureTextureCompression: "-texFormat", BC7 when not given). Qualcomm's Adreno
	/// reads them; ARM's Mali (Dimensity, Exynos before the Xclipse, Tensor, Kirin) does not: there the very first
	/// image fails ("VMA failed to allocate image ... Bc7UNormBlock: ErrorInitializationFailed", a Redmi K80 Ultra) and
	/// with it every window of the game.
	///
	/// When the device has no textureCompressionBC: the encoding at load time is switched off (textures then load as
	/// RGBA, as with "-texFormat=none"), and an image the engine asks for in a BC format is made in the plain format
	/// with the same channels instead (BC1/BC2/BC3/BC7 -> RGBA8, BC4 -> R8, BC5 -> RG8, sRGB kept), its data decoded
	/// on the processor with the engine's own block decoders before the upload. A decoded texture takes 4 times the
	/// memory of a BC7 one (8 times a BC1 one); the engine loads textures when they are first drawn.
	///
	/// mobile.txt: bcDecode=1 decodes on any GPU (to try this on a phone that reads BC), bcDecode=0 never.
	/// </summary>
	internal static class Textures
	{
		private static Harmony harmony;
		private static long decodedTextures, decodedBytes, decodeTicks;
		private static int unhandledLogged;

		/// <summary>From the startup hook: the decision waits for the GPU, which exists when the engine configures the texture compression.</summary>
		public static void Start()
		{
			try
			{
				Type bootstrap = AccessTools.TypeByName("RimWorldWaylith.Bootstrap");
				var configure = bootstrap == null ? null : AccessTools.Method(bootstrap, "ConfigureTextureCompression");
				if (configure == null)
				{
					Console.WriteLine("[port17] textures: Bootstrap.ConfigureTextureCompression not found; BC textures are left to the GPU");
					return;
				}
				harmony = new Harmony("port17.textures");
				harmony.Patch(configure, postfix: new HarmonyMethod(typeof(Textures), nameof(AfterCompressionConfigured)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] textures: not installed: " + e.Message);
			}
		}

		private static string Setting()
		{
			try
			{
				string file = Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				if (File.Exists(file))
				{
					foreach (string line in File.ReadLines(file))
					{
						string[] parts = line.Split('=');
						if (parts.Length == 2 && parts[0].Trim() == "bcDecode")
						{
							return parts[1].Trim();
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return "";
		}

		public static void AfterCompressionConfigured()
		{
			try
			{
				string setting = Setting();
				bool decode;
				string why;
				if (setting == "1" || setting == "0")
				{
					decode = setting == "1";
					why = "bcDecode=" + setting + " in mobile.txt";
				}
				else
				{
					PhysicalDevice gpu = EngineApplication.Instance?.PhysicalDevice;
					if (gpu == null)
					{
						Console.WriteLine("[port17] textures: no GPU to ask yet; BC textures are left to the GPU");
						return;
					}
					decode = !gpu.GetFeatures().TextureCompressionBC;
					why = decode ? "the GPU cannot sample BC textures" : "the GPU samples BC textures";
				}
				if (!decode)
				{
					Console.WriteLine("[port17] textures: " + why + "; left as they are");
					return;
				}
				TextureCompressionSettings.Configure(BcEncodeTarget.None, TextureCompressionSettings.Quality);
				var service = typeof(GpuTextureService);
				harmony.Patch(AccessTools.Method(service, "CreateTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(BeforeCreateTexture2D)));
				harmony.Patch(AccessTools.Method(service, "AllocateTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateEmptyTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateTexture2DArrayCore"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateTexture3D"), prefix: new HarmonyMethod(typeof(Textures), nameof(BeforeCreateTexture3D)));
				Console.WriteLine("[port17] textures: " + why + ": BC encoding at load time is off, BC textures are decoded before they go to the GPU");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] textures: the BC fallback failed to install: " + e);
			}
		}

		/// <summary>The plain format with the channels of a BC one, and the kind of block (0: not BC; -1: a BC format not decoded here).</summary>
		private static VkFormat Plain(VkFormat format, out int kind)
		{
			switch (format)
			{
			case VkFormat.Bc1RGBUNormBlock: kind = 11; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc1RGBSRGBBlock: kind = 11; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc1RGBAUNormBlock: kind = 1; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc1RGBASRGBBlock: kind = 1; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc2UNormBlock: kind = 2; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc2SRGBBlock: kind = 2; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc3UNormBlock: kind = 3; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc3SRGBBlock: kind = 3; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc4UNormBlock: kind = 4; return VkFormat.R8UNorm;
			case VkFormat.Bc5UNormBlock: kind = 5; return VkFormat.R8G8UNorm;
			case VkFormat.Bc7UNormBlock: kind = 7; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc7SRGBBlock: kind = 7; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc4SNormBlock:
			case VkFormat.Bc5SNormBlock:
			case VkFormat.Bc6HUfloatBlock:
			case VkFormat.Bc6HSFloatBlock:
				kind = -1;
				return format;
			default:
				kind = 0;
				return format;
			}
		}

		/// <summary>Prefix of the image allocations that take no data (the data, if any, is plain then too: the engine's own uploads of RGBA).</summary>
		public static void PlainFormat(ref VkFormat format, string label)
		{
			VkFormat plain = Plain(format, out int kind);
			if (kind > 0)
			{
				format = plain;
			}
			else if (kind < 0)
			{
				Unhandled(format, label);
			}
		}

		/// <summary>Prefix of GpuTextureService.CreateTexture2D: a BC texture becomes a plain one, every level decoded.</summary>
		public static void BeforeCreateTexture2D(string label, ref VkFormat format, ref ReadOnlySpan<TextureLevelUpload> levels, ref TextureLevelFill fill)
		{
			VkFormat plain = Plain(format, out int kind);
			if (kind == 0)
			{
				return;
			}
			if (kind < 0)
			{
				Unhandled(format, label);
				return;
			}
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			var decoded = new TextureLevelUpload[levels.Length];
			long bytes = 0;
			for (int i = 0; i < levels.Length; i++)
			{
				TextureLevelUpload level = levels[i];
				if (level.Size == 0)
				{
					decoded[i] = level;
					continue;
				}
				byte[] blocks = level.Bytes;
				if (fill != null)
				{
					blocks = new byte[level.Size];
					fill(level.Level, blocks);
				}
				byte[] texels = Decode(blocks, level.Width, level.Height, kind);
				bytes += texels.Length;
				decoded[i] = new TextureLevelUpload(level.Level, level.Width, level.Height, level.Depth, 0, texels, texels.Length);
			}
			format = plain;
			levels = decoded;
			fill = null;
			long count = Interlocked.Increment(ref decodedTextures);
			Interlocked.Add(ref decodedBytes, bytes);
			long ticks = Interlocked.Add(ref decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
			if (count <= 3 || count % 500 == 0)
			{
				Console.WriteLine("[port17] textures: " + count + " BC textures decoded so far, " + (Interlocked.Read(ref decodedBytes) / 1048576.0).ToString("F0") + " MB, " + (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + " ms (latest: " + label + ")");
			}
		}

		/// <summary>Prefix of GpuTextureService.CreateTexture3D: the engine makes no BC volume textures; one from elsewhere is only reported.</summary>
		public static void BeforeCreateTexture3D(string label, VkFormat format)
		{
			Plain(format, out int kind);
			if (kind != 0)
			{
				Unhandled(format, label + " (3D)");
			}
		}

		private static void Unhandled(VkFormat format, string label)
		{
			if (Interlocked.Increment(ref unhandledLogged) <= 5)
			{
				Console.WriteLine("[port17] textures: " + format + " is not decoded here; '" + label + "' will fail on this GPU");
			}
		}

		/// <summary>One level of BC blocks to texels of 4, 2 or 1 bytes (RGBA, RG, R), rows tightly packed.</summary>
		private static byte[] Decode(byte[] blocks, int width, int height, int kind)
		{
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			int channels = kind == 4 ? 1 : kind == 5 ? 2 : 4;
			int blockBytes = kind == 1 || kind == 11 || kind == 4 ? 8 : 16;
			int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
			if (blocks.Length < blocksX * blocksY * blockBytes)
			{
				throw new InvalidDataException("BC level of " + width + "x" + height + " has " + blocks.Length + " bytes, " + blocksX * blocksY * blockBytes + " expected");
			}
			byte[] texels = new byte[width * height * channels];
			if (blocksX * blocksY >= 4096)
			{
				Parallel.For(0, blocksY, row => DecodeRow(blocks, texels, row, width, height, blocksX, blockBytes, channels, kind));
			}
			else
			{
				for (int row = 0; row < blocksY; row++)
				{
					DecodeRow(blocks, texels, row, width, height, blocksX, blockBytes, channels, kind);
				}
			}
			return texels;
		}

		private static void DecodeRow(byte[] blocks, byte[] texels, int row, int width, int height, int blocksX, int blockBytes, int channels, int kind)
		{
			Span<byte> rgba = stackalloc byte[64];
			Span<byte> alpha = stackalloc byte[64];
			for (int column = 0; column < blocksX; column++)
			{
				ReadOnlySpan<byte> block = new ReadOnlySpan<byte>(blocks, (row * blocksX + column) * blockBytes, blockBytes);
				switch (kind)
				{
				case 7:
					Bc7BlockEncoder.DecodeBc7Block(block, rgba);
					break;
				case 4:
					Bc7BlockEncoder.DecodeBc4Block(block, rgba);
					break;
				case 5:
					Bc7BlockEncoder.DecodeBc5Block(block, rgba);
					break;
				case 1:
					Bc7BlockEncoder.DecodeBc1Block(block, rgba);
					break;
				case 11:
					// BC1 in an RGB format: no alpha, the "transparent" colour is black.
					Bc7BlockEncoder.DecodeBc1Block(block, rgba);
					for (int i = 3; i < 64; i += 4)
					{
						rgba[i] = 255;
					}
					break;
				case 2:
				{
					// BC2: 4 bits of alpha per texel, then a BC1 colour block.
					Bc7BlockEncoder.DecodeBc1Block(block.Slice(8), rgba);
					ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block);
					for (int i = 0; i < 16; i++)
					{
						rgba[i * 4 + 3] = (byte)(((bits >> (i * 4)) & 0xF) * 17);
					}
					break;
				}
				case 3:
					// BC3: a BC4 block of alpha, then a BC1 colour block.
					Bc7BlockEncoder.DecodeBc1Block(block.Slice(8), rgba);
					Bc7BlockEncoder.DecodeBc4Block(block.Slice(0, 8), alpha);
					for (int i = 0; i < 16; i++)
					{
						rgba[i * 4 + 3] = alpha[i * 4];
					}
					break;
				}
				for (int y = 0; y < 4; y++)
				{
					int ty = row * 4 + y;
					if (ty >= height)
					{
						break;
					}
					for (int x = 0; x < 4; x++)
					{
						int tx = column * 4 + x;
						if (tx >= width)
						{
							break;
						}
						int source = (y * 4 + x) * 4, target = (ty * width + tx) * channels;
						for (int c = 0; c < channels; c++)
						{
							texels[target + c] = rgba[source + c];
						}
					}
				}
			}
		}
	}
}
