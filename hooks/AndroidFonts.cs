using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The fallback fonts for glyphs the game's own fonts lack (symbols, CJK, Thai). Up to 1.7.4985 the launcher looked
	/// for Linux font files by name, and patch/Patch.cs pointed the names at the phone's (/system/fonts/DroidSans.ttf for
	/// DejaVuSans.ttf, Roboto for Liberation Sans...). From 4990 it asks for font families instead
	/// (UnityStubs.Internal.LinuxFontconfig.TryFind: fontconfig, which a phone does not have, then the names inside the OS's
	/// font files): "Noto Sans CJK SC", "Noto Sans Symbols", "Noto Sans Thai" are found under their own names, but the
	/// Linux ones with no such family on a phone were left out (DejaVu Sans, Liberation Sans, Noto Sans Symbols2, Droid
	/// Sans Fallback), and with them the symbols and scripts they covered. Each is the phone's file it was before, when the
	/// lookup by family finds nothing. Nothing is patched on an engine without TryFind.
	/// </summary>
	public static class AndroidFonts
	{
		private static readonly (string Family, string[] Files)[] Stand =
		{
			("DejaVu Sans", new[] { "DroidSans.ttf", "Roboto-Regular.ttf" }),
			("Liberation Sans", new[] { "Roboto-Regular.ttf", "DroidSans.ttf" }),
			("Noto Sans Symbols2", new[] { "NotoSansSymbols-Regular-Subsetted2.ttf" }),
			("Droid Sans Fallback", new[] { "DroidSansFallbackFull.ttf", "DroidSansFallback.ttf" }),
		};

		private static bool patched;

		public static void Start()
		{
			try
			{
				AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => TryPatch(args.LoadedAssembly);
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					TryPatch(assembly);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: fallback families left as they are: " + e.Message);
			}
		}

		private static void TryPatch(Assembly assembly)
		{
			if (patched || assembly.GetName().Name != "UnityStubs")
			{
				return;
			}
			patched = true;
			try
			{
				Type fontconfig = assembly.GetType("UnityStubs.Internal.LinuxFontconfig");
				MethodInfo find = fontconfig?.GetMethod("TryFind", BindingFlags.Public | BindingFlags.Static);
				if (find == null)
				{
					return;
				}
				new Harmony("port17.fonts").Patch(find, postfix: new HarmonyMethod(typeof(AndroidFonts), nameof(AfterTryFind)));
				Console.WriteLine("[port17] fonts: Linux fallback families without a phone font get the phone's files");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: fallback families left as they are: " + e.Message);
			}
		}

		public static void AfterTryFind(string family, ref string path, ref uint fontNo, ref bool __result)
		{
			if (__result || family == null)
			{
				return;
			}
			foreach ((string name, string[] files) in Stand)
			{
				if (!string.Equals(name, family, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				foreach (string file in files)
				{
					string full = Path.Combine("/system/fonts", file);
					if (File.Exists(full))
					{
						path = full;
						fontNo = 0;
						__result = true;
						return;
					}
				}
			}
		}
	}
}
