using System;
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
		AssemblyLoadContext.Default.Resolving += Resolve;
		PretendProcessPath();
		Console.WriteLine("[port17] startup hook installed (Unity assembly names resolve to UnityStubs)");
		PortDiag.StartIfRequested();
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

	private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
	{
		string n = name.Name ?? "";
		bool unity = n.StartsWith("UnityEngine", StringComparison.Ordinal)
			|| (n.StartsWith("Unity.", StringComparison.Ordinal) && n != "Unity.Newtonsoft.Json")
			|| n == "Assembly-CSharp-firstpass";
		if (!unity)
		{
			return null;
		}
		if (stubs == null)
		{
			foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (loaded.GetName().Name == "UnityStubs")
				{
					stubs = loaded;
					break;
				}
			}
			if (stubs == null)
			{
				string path = Path.Combine(AppContext.BaseDirectory, "UnityStubs.dll");
				stubs = context.LoadFromAssemblyPath(path);
			}
		}
		return stubs;
	}
}
