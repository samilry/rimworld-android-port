using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// At the main menu, once: how many textures and sounds each running mod's content holder found and how many of
	/// them came out empty (a path whose file could not be read: drawn red, or no sound), the official ones each, the
	/// others summed with the worst five named, and the asset packs mounted. Written for a tablet whose second start
	/// after a reboot lost every texture, vanilla's too, and its sounds (2026-10-27); its game log had grown to 372 MB and
	/// the report kept only its end, so it was not known where the content went. Port17.log is small and kept whole.
	/// </summary>
	internal static class ContentCheck
	{
		public static void Run()
		{
			try
			{
				var others = new List<(string Name, int Textures, int EmptyTextures, int Sounds, int EmptySounds)>();
				int textures = 0, emptyTextures = 0, sounds = 0, emptySounds = 0;
				foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
				{
					Count(mod.GetContentHolder<Texture2D>()?.contentList, out int t, out int te);
					Count(mod.GetContentHolder<AudioClip>()?.contentList, out int s, out int se);
					if (mod.IsOfficialMod)
					{
						Console.WriteLine("[mobile] content: " + mod.Name + " (" + mod.RootDir + "): " + t + " textures" + (te > 0 ? " (" + te + " empty)" : "") + ", " + s + " sounds" + (se > 0 ? " (" + se + " empty)" : ""));
					}
					else
					{
						others.Add((mod.Name, t, te, s, se));
						textures += t;
						emptyTextures += te;
						sounds += s;
						emptySounds += se;
					}
				}
				string worst = string.Join("; ", others.Where(o => o.EmptyTextures + o.EmptySounds > 0).OrderByDescending(o => o.EmptyTextures + o.EmptySounds).Take(5)
					.Select(o => o.Name + " " + o.EmptyTextures + "/" + o.Textures + " textures, " + o.EmptySounds + "/" + o.Sounds + " sounds empty"));
				Console.WriteLine("[mobile] content: " + others.Count + " other mods: " + textures + " textures" + (emptyTextures > 0 ? " (" + emptyTextures + " empty)" : "") + ", " + sounds + " sounds" + (emptySounds > 0 ? " (" + emptySounds + " empty)" : "")
					+ (worst.Length > 0 ? "; most empty: " + worst : ""));
				Console.WriteLine("[mobile] content: asset packs " + Packs());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] content check failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		private static void Count(IDictionary list, out int all, out int empty)
		{
			all = empty = 0;
			if (list == null)
			{
				return;
			}
			foreach (DictionaryEntry entry in list)
			{
				all++;
				if (entry.Value == null || (entry.Value is UnityEngine.Object o && (o == null || o == BaseContent.BadTex)))
				{
					empty++;
				}
			}
		}

		/// <summary>Waylith.Assets.ContentPack.Mounts: each pack's root and how many entries it shows (its manifest's).</summary>
		private static string Packs()
		{
			Type contentPack = typeof(Waylith.Rendering.GpuTextureService).Assembly.GetType("Waylith.Assets.ContentPack");
			if (!(contentPack?.GetProperty("Mounts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is IEnumerable mounts))
			{
				return "(not readable)";
			}
			var parts = new List<string>();
			string game = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory));
			foreach (object mount in mounts)
			{
				string root = mount.GetType().GetProperty("Root")?.GetValue(mount) as string ?? "?";
				object pack = mount.GetType().GetProperty("Pack")?.GetValue(mount);
				int shown = (pack?.GetType().GetProperty("ContentHashes")?.GetValue(pack) as ICollection)?.Count ?? -1;
				parts.Add((root.StartsWith(game, StringComparison.Ordinal) ? root.Substring(game.Length).TrimStart('/') : root) + " " + shown);
			}
			return parts.Count + ": " + string.Join(", ", parts.Take(12)) + (parts.Count > 12 ? ", …" : "");
		}
	}
}
