using System;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// The phone side of the game: loaded by PortHooks when the entry UI initializes (every def is loaded by then),
	/// patches the game with Harmony (the 1.7 build from the Harmony mod, 0Harmony.dll in the game folder).
	/// </summary>
	public static class Entry
	{
		private static int frames;

		public static void Start()
		{
			Console.WriteLine("[mobile] starting; Harmony " + typeof(Harmony).Assembly.GetName().Version + ", runtime " + Environment.Version);
			var harmony = new Harmony("port17.mobile");
			harmony.Patch(AccessTools.Method(typeof(Verse.UIRoot), "UIRootOnGUI"), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterUIRootOnGUI)));
			Console.WriteLine("[mobile] patched UIRoot.UIRootOnGUI");
			Touch.Start();
			ModImport.Start(harmony);
			ModSort.Start(harmony);
			BundleDiag.Start();
			HarmonyDiag.Start();
			SpawnDiag.Start(harmony);
			Sched.Start(harmony);
			FrameRate.Start(harmony);
			Perf.Start(harmony);
			WorldLabels.Start(harmony);
		}

		private static bool menuTimed;

		/// <summary>
		/// Seconds since the startup hook ran. (The kernel's start time of the process is no use: Android hands an app a
		/// process it forked minutes earlier.)
		/// </summary>
		internal static double SecondsSinceProcessStart()
		{
			return PortHooks.PortPlatform.StartedAtTick > 0 ? (Environment.TickCount64 - PortHooks.PortPlatform.StartedAtTick) / 1000.0 : -1;
		}

		public static void AfterUIRootOnGUI()
		{
			if (!menuTimed && Verse.Current.ProgramState == Verse.ProgramState.Entry && !Verse.LongEventHandler.AnyEventNowOrWaiting)
			{
				// Once: how long the start took, for comparing builds and settings.
				menuTimed = true;
				Console.WriteLine("[mobile] main menu " + SecondsSinceProcessStart().ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s after the runtime started");
			}
			frames++;
			if (frames == 1 || frames == 600)
			{
				Console.WriteLine("[mobile] UIRootOnGUI postfix ran (" + frames + ")");
			}
		}
	}
}
