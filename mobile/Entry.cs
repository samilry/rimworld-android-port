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
		}

		public static void AfterUIRootOnGUI()
		{
			frames++;
			if (frames == 1 || frames == 600)
			{
				Console.WriteLine("[mobile] UIRootOnGUI postfix ran (" + frames + ")");
			}
		}
	}
}
