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
		private static bool started;

		public static void Start()
		{
			// The hook that calls this sits in UIRoot_Entry.Init, and that runs again every time the game comes back to the main
			// menu. The patches are put in once: put in again, every one of them would run twice from then on.
			if (started)
			{
				MemTrim.AfterReturnToMenu();
				UnreadableMods.Tell();
				return;
			}
			started = true;
			Console.WriteLine("[mobile] starting; Harmony " + typeof(Harmony).Assembly.GetName().Version + ", runtime " + Environment.Version);
			var harmony = new Harmony("port17.mobile");
			// Before any of the port's patches: they are watched like every later one (see InlineGuard).
			InlineGuard.Start(harmony);
			harmony.Patch(AccessTools.Method(typeof(Verse.UIRoot), "UIRootOnGUI"), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterUIRootOnGUI)));
			Console.WriteLine("[mobile] patched UIRoot.UIRootOnGUI");
			// First of the rest: what goes wrong while the others start is in the record too.
			Recorder.Start(harmony);
			try
			{
				string missing = PortHooks.EngineTypes.Verify();
				Console.WriteLine("[mobile] engine types: " + (missing == null ? "all found" : "NOT FOUND: " + missing));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] engine types not checked: " + e.Message);
			}
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
			if (!Compat.On)
			{
				ResearchTree.Start(harmony);
			}
			Squad.Start(harmony);
			DoubleTap.Start(harmony);
			Counters.Start(harmony);
			MemTrim.Start(harmony);
			BakeDepth.Start(harmony);
			Temporaries.Start(harmony);
			if (!Compat.On)
			{
				Cutout.Start(harmony);
			}
			else
			{
				Console.WriteLine("[mobile] " + Compat.Describe());
			}
			ColdTextures.Start(harmony);
			AtlasSeams.Start(harmony);
			PlainIcons.Start(harmony);
			BindKeys.Start(harmony);
			if (!Compat.On)
			{
				MintMenus.Start(harmony);
				FactionList.Start(harmony);
			}
			WinterShadow.Start(harmony);
			MultiplayerFiles.Start(harmony);
			VideoUrls.Start(harmony);
			SaveImport.Start(harmony);
			HugsLogFile.Start(harmony);
			ResolutionChoice.Start(harmony);
			// The atlases are baked by now (once, a second at most; then by cache key).
			AtlasCheck.Start(harmony);
			// Last: the mods' replacements that hold their own copy of a method patched above.
			InlineGuard.EndOfStart();
			UnreadableMods.Tell();
			VersionNotice.Tell();
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

		/// <summary>
		/// What the runtime itself can say about the time until now: how long the compiler ran (on every thread together)
		/// and for how many methods, how long the collector held the program, and the processor time of the whole process
		/// (more than the wall time where threads worked side by side).
		/// </summary>
		private static string WhereTheStartWent()
		{
			try
			{
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				// /proc/self/stat: after the name in parentheses, the 12th and 13th fields are the user and system time in 1/100 s.
				string stat = System.IO.File.ReadAllText("/proc/self/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				TimeSpan cpu = TimeSpan.FromSeconds((long.Parse(fields[11]) + long.Parse(fields[12])) / 100.0);
				return "compiling " + System.Runtime.JitInfo.GetCompilationTime().TotalSeconds.ToString("F1", inv) + " s (" + System.Runtime.JitInfo.GetCompilationTime(true).TotalSeconds.ToString("F1", inv) + " s of it on this thread) for " + System.Runtime.JitInfo.GetCompiledMethodCount() + " methods (" + (System.Runtime.JitInfo.GetCompiledILBytes() / 1048576.0).ToString("F1", inv) + " MB of IL)"
					+ ", collector pauses " + GC.GetTotalPauseDuration().TotalSeconds.ToString("F1", inv) + " s in " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + " collections of " + (GC.GetTotalAllocatedBytes() / 1073741824.0).ToString("F1", inv) + " GB allocated"
					+ ", processor time " + cpu.TotalSeconds.ToString("F1", inv) + " s, " + AppDomain.CurrentDomain.GetAssemblies().Length + " assemblies";
			}
			catch (Exception e)
			{
				return e.GetType().Name;
			}
		}

		/// <summary>
		/// mobile.txt avTest=1: List.Add on an object reference that points nowhere, inside a try, the way a 1.6 mod's patch
		/// did it (Shella Backgrounds, in the options window: "Fatal error. System.AccessViolationException ... List`1.Add").
		/// The log says whether the runtime let the catch have it (host.c: legacyCorruptedStateExceptionsPolicy) or the
		/// process ends here.
		/// </summary>
		private static void AccessViolationTest()
		{
			if (Settings.Get("avTest", 0) != 1)
			{
				return;
			}
			try
			{
				IntPtr nowhere = new IntPtr(0x5A5A5A5A0000);
				System.Collections.Generic.List<object> list = System.Runtime.CompilerServices.Unsafe.As<IntPtr, System.Collections.Generic.List<object>>(ref nowhere);
				list.Add(null);
				Console.WriteLine("[mobile] access violation test: no fault");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] access violation test: caught " + e.GetType().FullName);
			}
		}

		public static void AfterUIRootOnGUI()
		{
			KeyWheel.OnGui();
			TypingBar.OnGui();
			if (!menuTimed && Verse.Current.ProgramState == Verse.ProgramState.Entry && !Verse.LongEventHandler.AnyEventNowOrWaiting)
			{
				// Once: how long the start took, for comparing builds and settings.
				menuTimed = true;
				Console.WriteLine("[mobile] main menu " + SecondsSinceProcessStart().ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s after the runtime started");
				ConflictTag.SelfTest();
				Console.WriteLine("[mobile] of that: " + WhereTheStartWent());
				Temporaries.Test();
				AccessViolationTest();
				MemTrim.AtMainMenu();
				PortHooks.JitProfile.AtMainMenu();
				PortHooks.InlineWatch.Report("main menu", Harmony.GetAllPatchedMethods());
			}
			frames++;
			MemTrim.OnFrame();
			ColdTextures.OnFrame();
			if (frames == 1 || frames == 600)
			{
				Console.WriteLine("[mobile] UIRootOnGUI postfix ran (" + frames + ")");
			}
		}
	}
}
