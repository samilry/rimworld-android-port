using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// The Shift key for a finger: a button at the left edge of the screen, shown while a game is played. A tap
	/// switches it on, another tap off; while it is on the engine sees the left Shift key held down (a real key
	/// event through the Android GLFW, so the game, and any mod that asks for the key, sees what it sees on a PC):
	///   a tap on a colonist, an animal, a thing or a portrait of the colonist bar adds it to the selection, or
	///   takes it out again; a tap on the ground leaves the selection as it is;
	///   an order (a hold on the map, or a choice from the menu it opens) is queued behind what the colonists are
	///   doing instead of replacing it;
	///   whatever else the game does with Shift (ten at a time in the trade and transfer dialogs).
	/// It goes off by itself when the game is left. mobile.txt: shiftButton=0 leaves the button out;
	/// shiftButtonAt=X,Y puts its top left corner there (interface units; default: the left edge, 38 % down).
	/// </summary>
	internal static class ShiftKey
	{
		[DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);

		private const int GlfwLeftShift = 340, Press = 1, Release = 0;
		private const float Width = 56f, Height = 48f;

		private static bool enabled, on;
		private static float atX = -1f, atY = -1f;
		private static int injectedFrame = -1;

		public static void Start()
		{
			enabled = Settings.Get("shiftButton", 1) != 0;
			string at = Settings.Get("shiftButtonAt", null);
			if (at != null)
			{
				string[] parts = at.Split(',');
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				if (parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float x) && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float y))
				{
					atX = x;
					atY = y;
				}
			}
			if (enabled)
			{
				Console.WriteLine("[mobile] shift button on" + (atX >= 0f ? " at " + atX + "," + atY : ""));
			}
		}

		private static void Set(bool value)
		{
			on = value;
			glfwAndroidInjectKey(GlfwLeftShift, value ? Press : Release, 0);
			injectedFrame = Time.frameCount;
		}

		/// <summary>Once a frame: off when no game is played; and held down again if the engine let go of the key (it does when the window comes back).</summary>
		public static void Update()
		{
			if (!on)
			{
				return;
			}
			if (Current.ProgramState != ProgramState.Playing)
			{
				Set(false);
			}
			else if (Time.frameCount > injectedFrame + 3 && !Input.GetKey(KeyCode.LeftShift))
			{
				Set(true);
			}
		}

		// The button is a window of the top layer (as tooltips are): the inspector's tabs open over the left edge whenever
		// a colonist is selected, and a button under them could not be pressed just when it is wanted. It is not put over
		// what such a window shows either (a storage filter's expand arrows are just there): where a window covers its
		// place it moves to that window's right edge, and where there is no room for it beside the windows (a window as
		// wide as the screen) it is not shown.
		private const int WindowId = 73195733;
		private const float Gap = 6f;

		/// <summary>From the end of the play screen's interface, every frame: the button's window is asked for again.</summary>
		public static void OnGui()
		{
			if (!enabled || Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.UIRoot == null || LongEventHandler.ShouldWaitForEvent)
			{
				return;
			}
			if (Find.UIRoot.screenshotMode != null && Find.UIRoot.screenshotMode.FiltersCurrentEvent)
			{
				return;
			}
			var screenRect = new Rect(atX >= 0f ? atX : 14f, atY >= 0f ? atY : Mathf.Round(UI.screenHeight * 0.38f), Width, Height);
			if (!BesideWindows(ref screenRect))
			{
				return;
			}
			Find.WindowStack.ImmediateWindow(WindowId, screenRect, WindowLayer.Super, DrawButton, doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
		}

		/// <summary>Moves the rectangle right of every window over it; false when that leaves the left half of the screen.</summary>
		private static bool BesideWindows(ref Rect rect)
		{
			IList<Window> windows = Find.WindowStack.Windows;
			for (int pass = 0; pass < 8; pass++)
			{
				bool moved = false;
				for (int i = 0; i < windows.Count; i++)
				{
					Window window = windows[i];
					if (window.ID == -WindowId || window.layer == WindowLayer.Super || !window.windowRect.Overlaps(rect))
					{
						continue;
					}
					rect.x = window.windowRect.xMax + Gap;
					moved = true;
				}
				if (!moved)
				{
					return rect.xMax <= UI.screenWidth / 2f;
				}
			}
			return false;
		}

		private static void DrawButton()
		{
			var rect = new Rect(0f, 0f, Width, Height);
			if (Event.current.type == EventType.Repaint)
			{
				Color colour = GUI.color;
				TextAnchor anchor = Text.Anchor;
				GameFont font = Text.Font;
				GUI.color = on ? Color.white : new Color(1f, 1f, 1f, 0.6f);
				Widgets.DrawAtlas(rect, on ? Widgets.ButtonBGAtlasClick : Widgets.ButtonBGAtlas);
				if (on)
				{
					GUI.color = new Color(1f, 0.9f, 0.3f);
					Widgets.DrawBox(rect, 2);
				}
				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(rect, "Shift");
				Text.Anchor = anchor;
				Text.Font = font;
				GUI.color = colour;
			}
			if (Widgets.ButtonInvisible(rect, false))
			{
				Set(!on);
				(on ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
			}
		}
	}
}
