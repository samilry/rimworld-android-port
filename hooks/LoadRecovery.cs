using System;
using System.Reflection;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// When the game's load fails with mods on, it switches every mod off, loads again and shows "some active mods caused
	/// errors" (PlayDataLoader.LoadAllPlayData). The exception that made it do so goes to Log.Warning, which writes nothing
	/// once the game has logged its maximum of messages; with many mods that have errors it has by then (a tester's
	/// report of 2026-10-27: the reason was lost, only the switched-off mod list was left). These warnings are copied
	/// to Port17.log whatever the game's log does.
	/// </summary>
	public static class LoadRecovery
	{
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
				Console.WriteLine("[port17] load recovery: not watched: " + e.Message);
			}
		}

		private static void TryPatch(Assembly assembly)
		{
			if (patched || assembly.GetName().Name != "Assembly-CSharp")
			{
				return;
			}
			patched = true;
			try
			{
				MethodInfo warning = assembly.GetType("Verse.Log")?.GetMethod("Warning", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
				if (warning == null)
				{
					return;
				}
				new Harmony("port17.loadrecovery").Patch(warning, prefix: new HarmonyMethod(typeof(LoadRecovery), nameof(BeforeWarning)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] load recovery: not watched: " + e.Message);
			}
		}

		public static void BeforeWarning(string text)
		{
			if (text != null && (text.StartsWith("Caught exception while loading play data", StringComparison.Ordinal)
				|| text.StartsWith("Could not recover from errors", StringComparison.Ordinal)
				|| text.StartsWith("Caught exception while recovering from errors", StringComparison.Ordinal)))
			{
				Console.WriteLine("[port17] the game's load failed; it switches the mods off and loads again: " + (text.Length > 6000 ? text.Substring(0, 6000) + " …" : text));
			}
		}
	}
}
