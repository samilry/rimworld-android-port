using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// A reference to an assembly that is not there (a mod's optional integration with a mod that is not installed) fails
	/// on the PC with TypeLoadException, on this runtime with FileNotFoundException (WaylithClr.MissingAssemblyTypeLoadException,
	/// measured on the PC 2026-10-27). Mods written for Mono catch TypeLoadException around such calls; here the
	/// exception went past their catch and their constructor or patch failed. When nothing (no handler of the game's or a
	/// mod's) resolves such a reference, an empty assembly of that name and version stands in, and the type looked for in
	/// it fails with TypeLoadException, word for word as on the PC ("Could not load type 'X' from assembly 'Y'"). Only for
	/// references the runtime follows itself: an explicit Assembly.Load, Type.GetType or LoadFromAssemblyName fails as
	/// before. Stand-ins are left out of AppDomain.GetAssemblies, so a mod that looks for another mod's assembly there
	/// still finds none.
	/// </summary>
	public static class MissingAssemblies
	{
		private static readonly object gate = new object();
		private static readonly Dictionary<string, Assembly> standIns = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<Assembly> hidden = new HashSet<Assembly>();
		[ThreadStatic] private static bool making;

		public static void Start(Harmony harmony)
		{
			try
			{
				MethodInfo resolve = typeof(AssemblyLoadContext).GetMethod("OnAssemblyResolve", BindingFlags.NonPublic | BindingFlags.Static);
				// AppDomain.GetAssemblies only calls this (and may be inlined past a patch of its own).
				MethodInfo list = typeof(AssemblyLoadContext).GetMethod("GetLoadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
				if (resolve == null || list == null)
				{
					Console.WriteLine("[port17] missing assemblies: this runtime's resolver is not as expected, left as it is");
					return;
				}
				harmony.Patch(resolve, postfix: new HarmonyMethod(typeof(MissingAssemblies), nameof(AfterResolve)));
				harmony.Patch(list, postfix: new HarmonyMethod(typeof(MissingAssemblies), nameof(HideStandIns)));
				Console.WriteLine("[port17] missing assemblies: references to one fail with TypeLoadException, as on the PC");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] missing assemblies left as they are: " + e.Message);
			}
		}

		// The result as the runtime's own type (RuntimeAssembly): what is stored is always one of those (LoadFromStream's).
		public static void AfterResolve(object assembly, string assemblyFullName, ref Assembly __result)
		{
			if (__result != null || making || string.IsNullOrEmpty(assemblyFullName))
			{
				return;
			}
			AssemblyName name;
			try
			{
				name = new AssemblyName(assemblyFullName);
			}
			catch (Exception)
			{
				return;
			}
			string simple = name.Name ?? "";
			if (simple.Length == 0 || simple.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) || Explicit())
			{
				return;
			}
			lock (gate)
			{
				if (!standIns.TryGetValue(simple, out Assembly standIn))
				{
					standIn = Make(name);
					standIns[simple] = standIn;
					if (standIn != null)
					{
						hidden.Add(standIn);
					}
					string by = assembly is Assembly asking ? asking.GetName().Name : "?";
					Console.WriteLine("[port17] missing assembly " + simple + " (" + name.Version + ", asked for by " + by + ")" + (standIn != null ? ": an empty stand-in, its types fail with TypeLoadException as on the PC" : ": no stand-in could be made"));
				}
				if (standIn != null)
				{
					__result = standIn;
				}
			}
		}

		/// <summary>An explicit load in progress (Assembly.Load, Type.GetType...): those fail as they always did.</summary>
		private static bool Explicit()
		{
			StackFrame[] frames = new StackTrace(2, false).GetFrames();
			foreach (StackFrame frame in frames)
			{
				MethodBase method = frame.GetMethod();
				Type type = method?.DeclaringType;
				if (type == null)
				{
					continue;
				}
				string owner = type.FullName ?? "";
				string m = method.Name;
				if ((owner == "System.Reflection.Assembly" || owner == "System.Reflection.RuntimeAssembly") && m.StartsWith("Load", StringComparison.Ordinal)
					|| owner == "System.Runtime.Loader.AssemblyLoadContext" && m.StartsWith("LoadFrom", StringComparison.Ordinal)
					|| (owner == "System.Type" || owner == "System.RuntimeType" || owner == "System.Reflection.TypeNameResolver" || owner == "System.Reflection.TypeNameParser") && (m == "GetType" || m.StartsWith("Resolve", StringComparison.Ordinal))
					|| owner == "System.AppDomain" && m == "Load"
					|| owner == "System.Activator" && m.StartsWith("Create", StringComparison.Ordinal))
				{
					return true;
				}
				if (owner.StartsWith("System.", StringComparison.Ordinal) || owner.StartsWith("PortHooks.", StringComparison.Ordinal) || owner.StartsWith("HarmonyLib.", StringComparison.Ordinal))
				{
					continue;
				}
				// The first frame outside the framework: the code whose reference is being followed.
				return false;
			}
			return false;
		}

		private static Assembly Make(AssemblyName wanted)
		{
			making = true;
			try
			{
				var name = new AssemblyName(wanted.Name) { Version = wanted.Version ?? new Version(0, 0, 0, 0) };
				var builder = new PersistedAssemblyBuilder(name, typeof(object).Assembly);
				builder.DefineDynamicModule(wanted.Name);
				var bytes = new MemoryStream();
				builder.Save(bytes);
				bytes.Position = 0;
				return AssemblyLoadContext.Default.LoadFromStream(bytes);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] missing assembly " + wanted.Name + ": " + e.GetType().Name + ": " + e.Message);
				return null;
			}
			finally
			{
				making = false;
			}
		}

		public static void HideStandIns(ref Assembly[] __result)
		{
			if (hidden.Count == 0 || __result == null)
			{
				return;
			}
			lock (gate)
			{
				__result = __result.Where(a => !hidden.Contains(a)).ToArray();
			}
		}
	}
}
