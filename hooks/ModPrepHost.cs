using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
		// The mods met in this run, with what their stamps list (null: prepared here and now, nothing more to ask).
		private static readonly Dictionary<string, Dictionary<string, long>> seen = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
		// The folders outside the mods that assemblies came from in this run, with where their prepared copies are (null: none).
		private static readonly Dictionary<string, string> copies = new Dictionary<string, string>(StringComparer.Ordinal);
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

		/// <summary>
		/// Before a mod assembly loads. Once per mod and run the mod's stamp is read and held against the files it lists;
		/// then each assembly of the mod, the first one too, is looked up in that list (no file is touched for it). The
		/// mod's folder is prepared when the stamp is missing, of other rules or no longer true, or does not know the
		/// assembly (it lies in a folder that held no assembly when the stamp was written; until 2026-10-30 the first
		/// assembly of a run was not looked up, and a mod's only assembly in a new version folder loaded unprepared at
		/// every start). A file that preparation does not take (ModPrep.IsAssemblyFile) is no reason: the stamp written
		/// then would not list it either, and the folder was prepared again at every start.
		/// </summary>
		public static void BeforeLoad(string assemblyPath)
		{
			string root = ModRootOf(assemblyPath);
			if (root == null)
			{
				return;
			}
			lock (gate)
			{
				if (!seen.TryGetValue(root, out Dictionary<string, long> listed))
				{
					listed = ModPrep.ReadStamp(root);
					if (listed != null && !ModPrep.StampHolds(root, listed))
					{
						listed = null;
					}
					seen[root] = listed;
					if (listed == null)
					{
						PrepareNow(root);
						return;
					}
				}
				if (listed != null && ModPrep.IsAssemblyFile(assemblyPath) && !listed.ContainsKey(Path.GetRelativePath(root, Path.GetFullPath(assemblyPath)).Replace('\\', '/')))
				{
					seen[root] = null;
					PrepareNow(root);
				}
			}
		}

		private static void PrepareNow(string root)
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			ModPrep.Result result = Prepare(root, line => Console.WriteLine("[port17] mod prep: " + line));
			Console.WriteLine("[port17] mod prep: " + Path.GetFileName(root) + " prepared before loading: " + result + " (" + watch.ElapsedMilliseconds + " ms)");
		}

		/// <summary>
		/// A prepared copy to load instead of an assembly from outside the mod folders, the game's and the runtime's; null
		/// when there is none (the file loads as it is). RimUnity, which Nivarian Race embeds, unpacks its runtime
		/// (RimUnity, RimUnity.Core, RimUnity.Spine) into the data folder's RimUnity/Embedded/<hash>/Assemblies and loads it
		/// from there with Assembly.LoadFrom; Ludeon's runtime is as lenient with those as with a mod's assemblies, and here
		/// no mod folder holds them to be prepared. RimUnity compares them with its own copy at every start ("Corrupt
		/// embedded cache"), so they are not prepared in place: the assemblies of their folder are copied together (the
		/// runtime looks for a LoadFrom assembly's references beside it), with the PDBs beside them, into
		/// modcache/elsewhere/<checksum of the folder's path>/ and prepared there. The copies are made again when the
		/// folder's assemblies change (their names, sizes and times are kept with the copies) or the rules do.
		/// </summary>
		public static string PreparedCopyOf(string assemblyPath)
		{
			string file = Path.GetFullPath(assemblyPath);
			string folder = Path.GetDirectoryName(file);
			// The game folder first: it holds Mods, and this is asked for every mod assembly that loads.
			if (folder == null || !ModPrep.IsAssemblyFile(file) || Inside(folder, AppContext.BaseDirectory) || Inside(folder, CopiesRoot)
				|| Inside(folder, Path.GetDirectoryName(typeof(object).Assembly.Location)) || ModRootOf(file) != null)
			{
				return null;
			}
			lock (gate)
			{
				if (!copies.TryGetValue(folder, out string copied))
				{
					// Not tried again in this run if it fails.
					copies[folder] = null;
					copied = CopyPrepared(folder);
					copies[folder] = copied;
				}
				string copy = copied == null ? null : Path.Combine(copied, Path.GetFileName(file));
				if (copy == null || !File.Exists(copy))
				{
					return null;
				}
				Console.WriteLine("[port17] " + Path.GetFileName(file) + " is loaded from outside the mod folders; its prepared copy loads: " + copy);
				return copy;
			}
		}

		// Where the port keeps copies of mod assemblies (PortPlatform.FixedArchitectureCopy's too): beside the game folder.
		private static string CopiesRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "modcache"));

		// The copies' record of the files they were made from.
		private const string SourcesName = ".port17-sources";

		private static string CopyPrepared(string folder)
		{
			string[] sources = Directory.GetFiles(folder).Where(ModPrep.IsAssemblyFile).OrderBy(f => f, StringComparer.Ordinal).ToArray();
			string record = string.Join("\n", sources.Select(f => Path.GetFileName(f) + "\t" + new FileInfo(f).Length + "\t" + File.GetLastWriteTimeUtc(f).Ticks));
			string target = Path.Combine(CopiesRoot, "elsewhere", ModPrep.Crc32(System.Text.Encoding.UTF8.GetBytes(folder)).ToString("x8"));
			string recordFile = Path.Combine(target, SourcesName);
			if (File.Exists(recordFile) && File.ReadAllText(recordFile) == record && ModPrep.IsPrepared(target))
			{
				return target;
			}
			var watch = System.Diagnostics.Stopwatch.StartNew();
			if (Directory.Exists(target))
			{
				Directory.Delete(target, true);
			}
			Directory.CreateDirectory(target);
			foreach (string source in sources)
			{
				File.Copy(source, Path.Combine(target, Path.GetFileName(source)));
				string pdb = Path.ChangeExtension(source, ".pdb");
				if (File.Exists(pdb))
				{
					File.Copy(pdb, Path.Combine(target, Path.GetFileName(pdb)));
				}
			}
			ModPrep.Result result = Prepare(target, line => Console.WriteLine("[port17] mod prep: " + line));
			File.WriteAllText(recordFile, record);
			Console.WriteLine("[port17] mod prep: the assemblies of " + folder + " (outside the mod folders) prepared as copies in " + target + ": " + result + " (" + watch.ElapsedMilliseconds + " ms)");
			return target;
		}

		private static bool Inside(string folder, string root)
		{
			if (string.IsNullOrEmpty(root))
			{
				return false;
			}
			string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return folder.Equals(full, StringComparison.Ordinal) || folder.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
				seen[root] = null;
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
