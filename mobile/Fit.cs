using System;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The interface scale on the phone: fixed, from mobile.txt ("uiScale=1.85"; 1.85 is where the Architect menu
	/// just fits the screen height). The game's options dialog can still change it: the new value is kept and
	/// written back. Nothing rescales by itself while playing (the user wants the interface to hold still);
	/// a window that does not fit is drawn smaller on its own (WindowFit).
	/// </summary>
	internal static class UiScale
	{
		private static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
		private static float preferred = 1.85f;
		private static float lastSet;

		public static void Start(Harmony harmony)
		{
			try
			{
				if (File.Exists(FilePath))
				{
					foreach (string line in File.ReadAllLines(FilePath))
					{
						string[] parts = line.Split('=');
						if (parts.Length == 2 && parts[0].Trim() == "uiScale" && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
						{
							preferred = Mathf.Clamp(value, 1f, 4f);
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] uiScale not read: " + e.Message);
			}
			OfferInOptions();
			harmony.Patch(AccessTools.Method(typeof(Window), nameof(Window.WindowOnGUI)), prefix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.Before)), finalizer: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.After)));
			Console.WriteLine("[mobile] UI scale " + preferred.ToString("F2") + " (" + Screen.width + "x" + Screen.height + " -> " + Mathf.RoundToInt(Screen.width / preferred) + "x" + Mathf.RoundToInt(Screen.height / preferred) + ")");
		}

		/// <summary>The phone's scale appears among the choices of the options dialog (the game's list stops at 1.75, 2, 2.5...).</summary>
		private static void OfferInOptions()
		{
			try
			{
				float[] scales = Dialog_Options.UIScales;
				if (scales == null || scales.Length < 2 || Array.Exists(scales, s => Mathf.Abs(s - preferred) < 0.001f))
				{
					return;
				}
				scales[scales.Length - 1] = preferred;
				Array.Sort(scales);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] could not add the phone's UI scale to the options: " + e.Message);
			}
		}

		/// <summary>Every frame.</summary>
		public static void Update()
		{
			float current = Prefs.UIScale;
			if (lastSet > 0f && Mathf.Abs(current - lastSet) > 0.001f)
			{
				// Someone else set it: the player, in the options dialog.
				preferred = Mathf.Clamp(current, 1f, 4f);
				Save();
			}
			if (Mathf.Abs(preferred - current) > 0.004f)
			{
				Prefs.UIScale = preferred;
			}
			lastSet = preferred;
		}

		private static void Save()
		{
			try
			{
				string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
				var lines = new System.Collections.Generic.List<string>(text.Split('\n'));
				lines.RemoveAll(l => l.TrimStart().StartsWith("uiScale=") || l.Trim().Length == 0);
				lines.Add("uiScale=" + preferred.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
				File.WriteAllText(FilePath, string.Join("\n", lines) + "\n");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] uiScale not saved: " + e.Message);
			}
		}
	}

	/// <summary>
	/// Many dialogs are a fixed 1024x768, and panels attached to something (the tabs above the inspect pane) can
	/// reach past the screen edge: more than a phone has at a readable scale. Such a window is drawn smaller,
	/// by itself; nothing else on the screen moves or changes size.
	///
	/// It works through the two things the game hands to the GUI for each window: its rectangle, and the GUI
	/// matrix in force at that moment, which the backend keeps for the window's contents and its mouse
	/// coordinates. Around Window.WindowOnGUI the matrix gets an extra factor and the rectangle is expressed in
	/// that smaller unit; afterwards both are put back, so that the game goes on seeing the window at the size
	/// it was made for.
	/// </summary>
	internal static class WindowFit
	{
		// Smaller than this and a window would be unreadable anyway; it is then left to stick out.
		private const float SmallestFactor = 0.5f;

		private static bool active;
		private static Rect design, passed;
		private static float factor;
		private static Matrix4x4 matrix;

		// One axis of a window: how much it has to shrink to fit, and where the shrunk window goes.
		// position: -1 keep the low edge, 1 keep the high edge, 0 centre on the screen, 2 nothing to do.
		private static float Axis(float min, float size, float limit, out int position)
		{
			bool low = min < -0.5f, high = min + size > limit + 0.5f;
			if (!low && !high)
			{
				position = 2;
				return 1f;
			}
			if ((low && high) || size > limit)
			{
				position = 0;
				return limit / size;
			}
			if (low)
			{
				// Attached with its high edge (a panel growing upwards from something): that edge stays.
				position = 1;
				return (min + size) / size;
			}
			position = -1;
			return (limit - min) / size;
		}

		private static float Place(float min, float size, float limit, int position, float f)
		{
			float fitted = size * f;
			switch (position)
			{
				case -1: return min;
				case 1: return min + size - fitted;
				case 0: return (limit - fitted) / 2f;
			}
			// This axis fitted; the other one made the window smaller. Stay at the screen edge it sat on, else centred on itself.
			if (min <= 1f)
			{
				return min;
			}
			if (min + size >= limit - 1f)
			{
				return min + size - fitted;
			}
			return min + (size - fitted) / 2f;
		}

		/// <summary>Prefix of Window.WindowOnGUI.</summary>
		public static void Before(Window __instance)
		{
			active = false;
			Rect rect = __instance.windowRect;
			float width = UI.screenWidth, height = UI.screenHeight;
			if (rect.width < 1f || rect.height < 1f)
			{
				return;
			}
			bool sizeFits = rect.width <= width + 0.5f && rect.height <= height + 0.5f;
			if (sizeFits && (__instance.draggable || __instance.resizeable))
			{
				return; // the player may have moved it there
			}
			float fx = Axis(rect.xMin, rect.width, width, out int px), fy = Axis(rect.yMin, rect.height, height, out int py);
			float f = Mathf.Min(fx, fy);
			if (f > 0.999f || f < SmallestFactor)
			{
				return;
			}
			var fitted = new Rect(Place(rect.xMin, rect.width, width, px, f), Place(rect.yMin, rect.height, height, py, f), rect.width * f, rect.height * f);
			design = rect;
			factor = f;
			// The same window in units that are f times smaller, rounded as the game is about to round it.
			passed = new Rect(fitted.x / f, fitted.y / f, rect.width, rect.height).Rounded();
			matrix = GUI.matrix;
			float scale = Prefs.UIScale * f;
			GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
			__instance.windowRect = passed;
			active = true;
		}

		/// <summary>Finalizer of Window.WindowOnGUI: runs whether or not the window's code threw.</summary>
		public static void After(Window __instance)
		{
			if (!active)
			{
				return;
			}
			active = false;
			GUI.matrix = matrix;
			// The GUI hands back the rectangle, moved if the window was dragged by its title.
			Vector2 moved = (__instance.windowRect.position - passed.position) * factor;
			design.position += moved;
			__instance.windowRect = design;
		}
	}
}
