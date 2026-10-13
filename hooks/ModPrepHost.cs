using System;
using System.Collections.Generic;
using System.IO;

namespace PortHooks
{
	/// <summary>
	/// The phone's side of the mod preparation (patch/ModPrep.cs, compiled into this assembly too): a mod whose folder has
	/// no stamp of the current rules gets its assemblies prepared in place before the first of them loads. That covers
	/// every way a mod can arrive: imported in the game (which prepares it at once and shows progress), pushed from the PC
	/// (prepared and stamped there, nothing to do here), or copied into Mods with a file manager.
	///
	/// Everything that names a Mono.Cecil type stays in this class and in ModPrep: PortPlatform is used from the first
	/// moments of the process and must not depend on Mono.Cecil.dll being present.
	/// </summary>
	internal static class ModPrepHost
	{
		private static readonly object gate = new object();
		private static readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static InMemoryResolver resolver;

		/// <summary>The folder of the mod an assembly belongs to: the nearest one above it that has About/About.xml.</summary>
		public static string ModRootOf(string assemblyPath)
		{
			try
			{
				string dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
				for (int up = 0; up < 6 && !string.IsNullOrEmpty(dir); up++)
				{
					if (File.Exists(Path.Combine(dir, "About", "About.xml")) || File.Exists(Path.Combine(dir, "about", "about.xml")))
					{
						return dir;
					}
					dir = Path.GetDirectoryName(dir);
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		/// <summary>Before a mod assembly loads: once per mod and run, prepares the mod's folder unless its stamp says it is.</summary>
		public static void BeforeLoad(string assemblyPath)
		{
			string root = ModRootOf(assemblyPath);
			if (root == null)
			{
				return;
			}
			lock (gate)
			{
				if (!seen.Add(root) || ModPrep.IsPrepared(root))
				{
					return;
				}
				var watch = System.Diagnostics.Stopwatch.StartNew();
				ModPrep.Result result = Prepare(root, line => Console.WriteLine("[port17] mod prep: " + line));
				Console.WriteLine("[port17] mod prep: " + Path.GetFileName(root) + " prepared before loading: " + result + " (" + watch.ElapsedMilliseconds + " ms)");
			}
		}

		/// <summary>Prepares a mod's folder now (the import does this for each mod it installs).</summary>
		public static ModPrep.Result Prepare(string root, Action<string> log)
		{
			lock (gate)
			{
				if (resolver == null)
				{
					// The game's assemblies and the runtime's: Cecil resolves a mod's references against them while it writes.
					resolver = ModPrep.NewResolver(new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location) });
				}
				seen.Add(root);
				return ModPrep.PrepareFolder(root, resolver, log);
			}
		}

		/// <summary>Prepare, with the report as text for the game side (which knows no type of this assembly but PortPlatform).</summary>
		public static string PrepareForImport(string root, Version game, out string[] problems)
		{
			ModPrep.Result result = Prepare(root, line => Console.WriteLine("[port17] mod prep: " + line));
			problems = result.ProblemsThatMatter(game).ToArray();
			return result.ToString();
		}

		/// <summary>Lets go of the game and runtime assemblies the resolver read (tens of megabytes); it is made again when needed.</summary>
		public static void Release()
		{
			lock (gate)
			{
				resolver?.Dispose();
				resolver = null;
			}
		}
	}
}
