using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace PortHooks
{
	/// <summary>
	/// What the game's platform checks are rewritten to call (patch/Patch.cs): the runtime says "Android", which the
	/// engine does not know; it is treated as Linux, the Unix platform the engine has code for.
	/// </summary>
	public static class PortPlatform
	{
		/// <summary>Environment.TickCount64 when the startup hook ran: the earliest moment of the game's process that managed code sees (for timing the start).</summary>
		public static long StartedAtTick;

		public static bool IsLinux()
		{
			return true;
		}

		public static bool IsOSPlatform(System.Runtime.InteropServices.OSPlatform platform)
		{
			return platform == System.Runtime.InteropServices.OSPlatform.Linux || platform.ToString().Equals("ANDROID", StringComparison.OrdinalIgnoreCase);
		}

		private static MethodInfo getListing, addDirectory;
		private static FieldInfo unlistableField, diskDirectoriesField;
		private static Type listingType;

		/// <summary>
		/// Stands in for ResourceFiles.GetListing inside ResolveCaseInsensitive (patch/Patch.cs rewrites that one call).
		/// The engine resolves a path case-insensitively by listing every ancestor directory; on Android an app cannot
		/// list "Android/data" (nor some directories above it), the listing comes back "unlistable" and the resolve
		/// fails for everything below, which is how whole texture folders went missing. For an unlistable directory
		/// a listing is made up from the paths this process knows it lives in.
		/// </summary>
		public static object GetListingForResolve(string directory)
		{
			if (getListing == null)
			{
				Type files = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Waylith").GetType("Waylith.Utils.ResourceFiles");
				const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
				getListing = files.GetMethod("GetListing", any);
				listingType = files.GetNestedType("DirectoryListing", BindingFlags.Public | BindingFlags.NonPublic);
				unlistableField = listingType.GetField("Unlistable", any);
				diskDirectoriesField = listingType.GetField("DiskDirectories", any);
				addDirectory = listingType.GetMethod("AddDirectory", any);
			}
			object listing = getListing.Invoke(null, new object[] { directory });
			if (listing == null || !ReferenceEquals(listing, unlistableField.GetValue(null)))
			{
				return listing;
			}
			object made = Activator.CreateInstance(listingType, true);
			var diskDirectories = (System.Collections.Generic.HashSet<string>)diskDirectoriesField.GetValue(made);
			string prefix = directory.TrimEnd('/') + "/";
			foreach (string known in new[] { AppContext.BaseDirectory, Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("TMPDIR"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") })
			{
				if (string.IsNullOrEmpty(known) || !known.StartsWith(prefix, StringComparison.Ordinal))
				{
					continue;
				}
				string rest = known.Substring(prefix.Length);
				int slash = rest.IndexOf('/');
				string child = slash < 0 ? rest : rest.Substring(0, slash);
				if (child.Length == 0)
				{
					continue;
				}
				addDirectory.Invoke(made, new object[] { child, Path.Combine(directory, child) });
				diskDirectories.Add(child);
			}
			return made;
		}

		/// <summary>Console.CancelKeyPress registers a POSIX signal handler underneath, which Android's runtime does not support; there is no console to press keys in anyway.</summary>
		public static void AddCancelKeyPress(ConsoleCancelEventHandler handler)
		{
		}

		public static void RemoveCancelKeyPress(ConsoleCancelEventHandler handler)
		{
		}

		/// <summary>POSIX signal registration is not supported by the runtime on Android; the engine keeps the registrations as nullable and never needs them here.</summary>
		/// <summary>Stands in for SteamManager.Initialized inside UIRoot_Entry.Init only: without it the main menu opens a "Could not initialize Steam API" box on every start (there is no Steam client on the phone). Everything else still sees Steam as absent.</summary>
		/// <summary>Called at the start of UIRoot_Entry.Init (patch/Patch.cs inserts the call): the game is fully loaded, the phone side (PortMobile.dll next to the game) starts here.</summary>
		public static void OnEntryUIInit()
		{
			Console.WriteLine("[port17] " + modLoads + " mod assemblies asked for: checking that the mods are prepared " + Ms(ticksPrepared) + ", looking for one of the same name " + Ms(ticksNames) + ", checking the architecture mark " + Ms(ticksArchitecture) + ", loading " + Ms(ticksLoad) + "; the stack check of " + stackChecks + " patched methods " + Ms(ticksStackChecks));
			try
			{
				ModPrepHost.Release();
			}
			catch (Exception)
			{
				// Mono.Cecil.dll is not there; said already when the first mod loaded
			}
			try
			{
				string path = Path.Combine(AppContext.BaseDirectory, "PortMobile.dll");
				if (!File.Exists(path))
				{
					Console.WriteLine("[port17] no PortMobile.dll next to the game; running without the phone layer");
					return;
				}
				Assembly mobile = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
				mobile.GetType("PortMobile.Entry").GetMethod("Start").Invoke(null, null);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the phone layer failed to start: " + e);
			}
		}

		/// <summary>Stands in for DirectoryInfo.GetDirectories inside ModLister.RebuildModList: the mod folders the game may read (ModFolders).</summary>
		public static DirectoryInfo[] ReadableModFolders(DirectoryInfo parent)
		{
			return ModFolders.Readable(parent);
		}

		/// <summary>
		/// Stands in for Assembly.LoadFrom inside ModAssemblyHandler.ReloadAll. A mod's own copy of 0Harmony.dll (the Harmony
		/// mod ships one, others bundle it) is not loaded: the copy in the game folder, patched for this runtime (MonoMod's
		/// Android and glibc assumptions, see patch/Patch.cs), is the one every mod gets. Loading a second copy of the same
		/// assembly name into the default context would fail anyway.
		/// </summary>
		public static Assembly LoadModAssembly(string path)
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			PrepareModBeforeLoad(path);
			long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
			modLoads++;
			ticksPrepared += t1 - t0;
			string name = Path.GetFileNameWithoutExtension(path);
			foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
			{
				// (Each assembly is asked for its name once: that makes an AssemblyName object every time.) A stand-in
				// (MissingAssemblies) is not the mod's assembly: a mod loaded earlier asked for this one before it was
				// there (Rimtalk - Secrets and side quest expansion before RimTalk), and the real one loads now, as on
				// the PC. Until 2026-10-28 the stand-in was taken for it and RimTalk with its 15 add-ons had no types.
				if (namesSeen.Add(candidate) && !candidate.IsDynamic && !MissingAssemblies.IsStandIn(candidate))
				{
					loadedByName[candidate.GetName().Name] = candidate;
				}
			}
			if (loadedByName.TryGetValue(name, out Assembly loaded))
			{
				// Already loaded (the game folder's patched 0Harmony.dll, or a library another mod bundles too): the
				// runtime would refuse a second assembly of the same name in its default context anyway.
				Console.WriteLine("[port17] " + name + " is already loaded; " + path + " skipped");
				ticksNames += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
				return loaded;
			}
			long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
			ticksNames += t2 - t1;
			if (name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase))
			{
				string ours = Path.Combine(AppContext.BaseDirectory, "0Harmony.dll");
				if (File.Exists(ours))
				{
					Console.WriteLine("[port17] mod asked for " + path + "; loading the game folder's 0Harmony.dll instead");
					return Assembly.LoadFrom(ours);
				}
			}
			string fixedCopy = FixedArchitectureCopy(path);
			long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
			ticksArchitecture += t3 - t2;
			Assembly assembly = Assembly.LoadFrom(fixedCopy ?? path);
			ticksLoad += System.Diagnostics.Stopwatch.GetTimestamp() - t3;
			return assembly;
		}

		private static readonly HashSet<Assembly> namesSeen = new HashSet<Assembly>();
		private static readonly Dictionary<string, Assembly> loadedByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

		// How long the parts of LoadModAssembly took over all mod assemblies of this start (said once, in OnEntryUIInit).
		private static int modLoads, stackChecks;
		private static long ticksStackChecks;
		private static long ticksPrepared, ticksNames, ticksArchitecture, ticksLoad;

		private static string Ms(long ticks)
		{
			return (ticks * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms";
		}

		/// <summary>
		/// A mod that was not prepared for this runtime yet (imported raw, or copied into Mods by hand) has its assemblies
		/// prepared in place now (ModPrepHost, patch/ModPrep.cs). A failure, Mono.Cecil.dll missing from the game folder
		/// included, is reported and the mod loads as it is.
		/// </summary>
		private static void PrepareModBeforeLoad(string path)
		{
			try
			{
				ModPrepHost.BeforeLoad(path);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] mod prep failed for " + path + ": " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// For the in-game mod import (mobile/ModImport.cs): prepares the assemblies of a mod folder that was just installed
		/// and returns a one-line report ("12 assemblies seen, 12 rewritten"); problems lists the assemblies that could not
		/// be rewritten and that the game of the given version may load (not those in the folders of other game versions).
		/// Throws when it cannot prepare at all.
		/// </summary>
		public static string PrepareModFolder(string root, int gameMajor, int gameMinor, out string[] problems)
		{
			return ModPrepHost.PrepareForImport(root, new Version(gameMajor, gameMinor), out problems);
		}

		/// <summary>The import calls this when it is done: the preparation's copies of the game assemblies are let go.</summary>
		public static void ReleaseModPrep()
		{
			ModPrepHost.Release();
		}

		/// <summary>
		/// A mod assembly built for x64 only (PE machine type AMD64, pure IL all the same), or for x86 only (32BITREQUIRED),
		/// is refused by the runtime on arm64; a copy made loadable (ModPrep.FixMachineType) is kept next to the game
		/// (modcache/<mod folder>/) and loaded instead. Mods prepared before the x86 case was known get it here. Returns
		/// null when the file needs no change.
		/// </summary>
		/// <summary>Whether an I386 image's CLR header asks for a 32-bit process only (the header's flags read from the file; the section table is in the head).</summary>
		private static bool Only32Bit(string path, byte[] head, int got, int pe)
		{
			int offset = ModPrep.ClrFlagsOffset(head.Length == got ? head : head.AsSpan(0, got).ToArray(), pe);
			if (offset < 0)
			{
				return false;
			}
			var flags = new byte[4];
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, false))
			{
				stream.Position = offset;
				if (stream.Read(flags, 0, 4) != 4)
				{
					return false;
				}
			}
			uint value = BitConverter.ToUInt32(flags, 0);
			return (value & 0x1) != 0 && (value & 0x2) != 0 && (value & 0x20000) == 0;
		}

		private static string FixedArchitectureCopy(string path)
		{
			try
			{
				// The mark is in the file's first kilobyte; the whole file is read only when a copy has to be made
				// (never for a prepared mod: preparation changes the mark in place).
				var head = new byte[1024];
				int got;
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, false))
				{
					got = stream.Read(head, 0, head.Length);
				}
				if (got < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z')
				{
					return null;
				}
				int pe = BitConverter.ToInt32(head, 0x3c);
				if (pe < 0 || pe + 24 > got || head[pe] != (byte)'P' || head[pe + 1] != (byte)'E')
				{
					return null;
				}
				ushort machine = BitConverter.ToUInt16(head, pe + 4);
				if (machine != 0x8664 && !(machine == 0x14c && Only32Bit(path, head, got, pe)))
				{
					return null;
				}
				byte[] bytes = File.ReadAllBytes(path);
				if (!ModPrep.FixMachineType(bytes))
				{
					return null;
				}
				string modFolder = "x";
				try
				{
					// <Mods>/<id>/<version>/Assemblies/<file>.dll (or <Mods>/<id>/Assemblies/...): the mod's folder name keeps copies apart.
					string assemblies = Path.GetDirectoryName(path);
					string above = Path.GetDirectoryName(assemblies);
					string aboveThat = Path.GetDirectoryName(above);
					modFolder = Path.GetFileName(Path.GetFileName(above).Contains(".") ? aboveThat : above);
				}
				catch (Exception)
				{
				}
				string folder = Path.Combine(AppContext.BaseDirectory, "..", "modcache", modFolder);
				Directory.CreateDirectory(folder);
				string copy = Path.Combine(folder, Path.GetFileName(path));
				if (!File.Exists(copy) || File.GetLastWriteTimeUtc(copy) < File.GetLastWriteTimeUtc(path) || new FileInfo(copy).Length != bytes.Length)
				{
					File.WriteAllBytes(copy, bytes);
				}
				Console.WriteLine("[port17] " + Path.GetFileName(path) + " is marked " + (machine == 0x8664 ? "AMD64" : "x86 only") + "; loading a copy marked for this runtime from " + copy);
				return copy;
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] could not check the architecture of " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>
		/// Stands in for Type.GetField(name, flags) inside Harmony (patch/Patch.cs rewrites the calls in 0Harmony.dll's
		/// HarmonyLib types). Mods written for Mono reach into the framework's private fields by Mono's names; this
		/// runtime's framework names many of them differently (Mono's XmlElement.name is _name here, m_value is _value,
		/// and so on). Ludeon's runtime fork tolerates that (WaylithClr.LaxPrivateFieldNames in the game's
		/// runtimeconfig); the stock runtime finds nothing, and Humanoid Alien Races for one fails to initialize.
		/// When the exact name is not there and the type is one of the framework's, a non-public field whose name is the
		/// same apart from a leading "_" or "m_" is taken, if there is exactly one.
		/// </summary>
		public static FieldInfo GetFieldLax(Type type, string name, BindingFlags flags)
		{
			FieldInfo field = type.GetField(name, flags);
			if (field != null || string.IsNullOrEmpty(name) || (flags & BindingFlags.NonPublic) == 0 || !IsFrameworkType(type))
			{
				return field;
			}
			string wanted = WithoutFieldPrefix(name);
			FieldInfo found = null;
			foreach (FieldInfo candidate in type.GetFields(flags))
			{
				if (!candidate.IsPublic && string.Equals(WithoutFieldPrefix(candidate.Name), wanted, StringComparison.Ordinal))
				{
					if (found != null)
					{
						return null; // two fields answer to the name: not guessed
					}
					found = candidate;
				}
			}
			if (found != null)
			{
				lock (laxFieldsReported)
				{
					if (laxFieldsReported.Add(type.FullName + "." + name))
					{
						Console.WriteLine("[port17] " + type.FullName + " has no field '" + name + "'; '" + found.Name + "' serves (private field names differ between Mono's framework and this one)");
					}
				}
			}
			return found;
		}

		private static readonly System.Collections.Generic.HashSet<string> laxFieldsReported = new System.Collections.Generic.HashSet<string>();

		private static bool IsFrameworkType(Type type)
		{
			string assembly = type.Assembly.GetName().Name ?? "";
			return assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("System", StringComparison.Ordinal);
		}

		private static string WithoutFieldPrefix(string name)
		{
			if (name.StartsWith("m_", StringComparison.Ordinal))
			{
				return name.Substring(2);
			}
			return name.StartsWith("_", StringComparison.Ordinal) ? name.Substring(1) : name;
		}

		/// <summary>
		/// The opcode Harmony emits for a call instruction of a patched method (patch/Patch.cs passes the arguments of
		/// HarmonyLib.Emitter.Emit(OpCode, MethodInfo) and EmitCall through here). Transpilers written for Mono use
		/// callvirt for static methods here and there (Toddlers' call of its static settings getter): Mono runs that,
		/// Ludeon's runtime fork too (WaylithClr.LaxCallvirt), the stock runtime throws "Method not found: '?'" or
		/// "invalid program" when it compiles the patched method. A callvirt to a static method becomes a call.
		/// </summary>
		public static System.Reflection.Emit.OpCode LaxCallOpcode(System.Reflection.Emit.OpCode opcode, MethodInfo method)
		{
			if (method != null && method.IsStatic && opcode == System.Reflection.Emit.OpCodes.Callvirt)
			{
				lock (laxFieldsReported)
				{
					if (laxFieldsReported.Add("callvirt " + method.DeclaringType?.FullName + "." + method.Name))
					{
						Console.WriteLine("[port17] a patch calls the static method " + method.DeclaringType?.FullName + "." + method.Name + " with callvirt; emitted as call");
					}
				}
				return System.Reflection.Emit.OpCodes.Call;
			}
			return opcode;
		}

		/// <summary>LaxCallOpcode for Harmony's Emitter.DynEmit(OpCode, object), which emits the instructions transpilers hand back: the operand is a method only for some.</summary>
		public static System.Reflection.Emit.OpCode LaxCallOpcodeForOperand(System.Reflection.Emit.OpCode opcode, object operand)
		{
			return operand is MethodInfo method ? LaxCallOpcode(opcode, method) : opcode;
		}

		/// <summary>
		/// Called at the start of HarmonyLib.MethodCreatorTools.EmitCodes (patch/Patch.cs adds the call) with Harmony's
		/// instruction list for a replacement method: values a transpiler left on the stack at a jump are popped, as
		/// Mono in effect does (hooks/HarmonyCompat.cs). Never lets anything escape into Harmony.
		/// </summary>
		public static void NormalizeHarmonyStack(object creator, object codes)
		{
			long began = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				NormalizeStack(creator, codes);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the stack check of a patched method failed (the method is emitted as it is): " + e.GetType().Name + ": " + e.Message);
			}
			stackChecks++;
			ticksStackChecks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
		}

		// Apart, and not inlined: HarmonyCompat names Harmony's types, which are only there once Harmony is loaded.
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static void NormalizeStack(object creator, object codes)
		{
			HarmonyCompat.Normalize(creator, codes as System.Collections.Generic.List<HarmonyLib.CodeInstruction>);
		}

		public static bool SteamInitializedForMenu()
		{
			return true;
		}

		public static System.Runtime.InteropServices.PosixSignalRegistration RegisterPosixSignal(System.Runtime.InteropServices.PosixSignal signal, Action<System.Runtime.InteropServices.PosixSignalContext> handler)
		{
			return null;
		}
	}
}

/// <summary>
/// Runs before the launcher's Main (CoreCLR's STARTUP_HOOKS). It stands in for the assembly-binding behaviour of
/// Ludeon's runtime fork that a stock runtime does not have: references to the Unity assemblies (mods were
/// compiled against UnityEngine.CoreModule and friends) resolve to UnityStubs.dll, the engine's compatibility
/// layer, the way "WaylithClr.AssemblyNameRedirects" in the game's runtimeconfig says.
/// </summary>
internal static class StartupHook
{
	private static Assembly stubs;

	public static void Initialize()
	{
		PortHooks.PortPlatform.StartedAtTick = Environment.TickCount64;
		PortHooks.PortLog.Start();
		// Memory and the game's last log line while it loads (the record starts only with the main menu), stalls later.
		PortHooks.Watch.Start();
		// Before anything of Harmony's runs: a kernel that names its CPU oddly left MonoMod without detours (a Kirin 8000).
		PortHooks.MonoModArch.Check();
		PortHooks.JitProfile.Start();
		PortHooks.InlineWatch.Start();
		AssemblyLoadContext.Default.Resolving += Resolve;
		PretendProcessPath();
		LimitGraphicsMemory();
		DropAtlasCacheIfAsked();
		PortHooks.Textures.Start();
		PortHooks.UiDescriptors.Start();
		PortHooks.AndroidFonts.Start();
		PortHooks.WindowsLibraries.Start();
		PortHooks.MissingAssemblies.Start(new HarmonyLib.Harmony("port17.missingassemblies"));
		PortHooks.LoadRecovery.Start();
		Console.WriteLine("[port17] startup hook installed (Unity assembly names resolve to UnityStubs)");
		PortDiag.StartIfRequested();
	}

	/// <summary>
	/// PortMobile's atlas check found textures missing from a static atlas page that came from the engine's cache
	/// (TextureCache/atlas-rebuild.txt says which): the cached pages (TextureCache/v*/*/pages.*.zip) are deleted before
	/// the engine opens them, so that this start bakes every atlas again from its textures. The pages are baked before
	/// PortMobile loads, so it cannot do this itself.
	/// </summary>
	private static void DropAtlasCacheIfAsked()
	{
		try
		{
			string home = Environment.GetEnvironmentVariable("HOME");
			if (string.IsNullOrEmpty(home))
			{
				return;
			}
			string cache = Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "TextureCache");
			string flag = Path.Combine(cache, "atlas-rebuild.txt");
			if (!File.Exists(flag))
			{
				return;
			}
			int deleted = 0;
			foreach (string file in Directory.EnumerateFiles(cache, "pages.*", SearchOption.AllDirectories))
			{
				File.Delete(file);
				deleted++;
			}
			foreach (string file in Directory.EnumerateFiles(cache, "atlas-checked*.txt"))
			{
				File.Delete(file);
			}
			string why = File.ReadAllText(flag).Trim();
			File.Delete(flag);
			Console.WriteLine("[port17] atlas cache dropped (" + deleted + " files), the atlases are baked again: " + why);
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] atlas cache not dropped: " + e.Message);
		}
	}

	/// <summary>
	/// The engine sizes its texture budget by the Vulkan memory heap, which on a phone is most of the RAM (11 GiB on
	/// a 12 GB phone): its watermark for putting unused textures back to the disk cache, about 7 GiB there, is never
	/// reached, because the phone's own watchdog kills an app whose graphics memory passes 6 GiB ("process memory is
	/// leaking"; seen while loading 106 mods, 14 of them race mods with 12 000 textures). WAYLITH_VRAM_BUDGET_MB is the
	/// engine's own switch for a smaller heap: past its watermark the textures nothing has drawn stay in the disk
	/// cache until they are first used. It is set here from the phone's RAM (three eighths, 4 GiB at most) unless it
	/// is set already; game/vram.txt holds another number of MB for it (0: no limit).
	/// </summary>
	private static void LimitGraphicsMemory()
	{
		const string name = "WAYLITH_VRAM_BUDGET_MB";
		try
		{
			if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
			{
				return;
			}
			long megabytes = 0;
			string file = Path.Combine(AppContext.BaseDirectory, "vram.txt");
			if (File.Exists(file))
			{
				if (!long.TryParse(File.ReadAllText(file).Trim(), out megabytes) || megabytes <= 0)
				{
					Console.WriteLine("[port17] graphics memory: no limit (vram.txt)");
					return;
				}
			}
			else
			{
				foreach (string line in File.ReadLines("/proc/meminfo"))
				{
					if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
					{
						string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
						megabytes = long.Parse(parts[1]) / 1024 * 3 / 8;
						break;
					}
				}
				if (megabytes <= 0)
				{
					return;
				}
				megabytes = Math.Max(1536, Math.Min(4096, megabytes));
			}
			Environment.SetEnvironmentVariable(name, megabytes.ToString());
			Console.WriteLine("[port17] graphics memory: the engine's heap is limited to " + megabytes + " MB (" + name + ")");
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] graphics memory: no limit set: " + e.Message);
		}
	}

	/// <summary>
	/// The launcher takes the game folder from Environment.ProcessPath, which in an Android app is the system's
	/// app_process. The runtime caches the path in a private field; it is set here to a name in the game folder
	/// before anyone reads it.
	/// </summary>
	private static void PretendProcessPath()
	{
		try
		{
			FieldInfo field = typeof(Environment).GetField("s_processPath", BindingFlags.NonPublic | BindingFlags.Static);
			if (field == null)
			{
				Console.WriteLine("[port17] Environment.s_processPath not found; the launcher will look for RimWorld_Data next to app_process");
				return;
			}
			string pretend = Path.Combine(AppContext.BaseDirectory, "RimWorld.exe");
			field.SetValue(null, pretend);
			Console.WriteLine("[port17] process path pretends to be " + Environment.ProcessPath);
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] could not set the process path: " + e.Message);
		}
	}

	/// <summary>
	/// Unity assembly names (UnityEngine.CoreModule and the other modules mods were compiled against) are served by
	/// type-forwarding facades in the game folder (patch/Patch.cs --forwarders); the runtime refuses a resolver that
	/// answers a request with an assembly of another name, so nothing can be done here but say what was asked for.
	/// </summary>
	private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
	{
		string n = name.Name ?? "";
		foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (loaded.GetName().Name == n)
			{
				// Loaded under another version (mods bundle different versions of the same library): the loaded one serves.
				return loaded;
			}
		}
		if (n.StartsWith("UnityEngine", StringComparison.Ordinal) || n.StartsWith("Unity.", StringComparison.Ordinal))
		{
			Console.WriteLine("[port17] no facade for " + n + " (add it to the forwarders list in host/stage.sh)");
		}
		// Not loaded yet, but there under an older version than asked for (a mod built against a newer library): that one
		// serves, as on the PC (WaylithClr.LaxAssemblyBinding, measured 2026-10-27: a reference to 3.0 bound to the 1.0
		// there, FileNotFoundException here).
		if (name.Version != null && !byNameOnly && n.Length > 0)
		{
			byNameOnly = true;
			try
			{
				return context.LoadFromAssemblyName(new AssemblyName(n));
			}
			catch (Exception)
			{
			}
			finally
			{
				byNameOnly = false;
			}
		}
		return null;
	}

	[ThreadStatic] private static bool byNameOnly;
}
