using System;
using System.IO;
using System.Reflection;
using Brutal.Numerics;
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
	/// No limits (the user's choice, 2026-10-20, after testers still could not change it): every scale of the list is
	/// taken whatever the screen, from 0.5 up; the game's own "keep / revert" confirmation is the safety net (not kept
	/// within its countdown, the old scale comes back).
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
							preferred = Mathf.Clamp(value, MinScale, MaxScale);
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] uiScale not read: " + e.Message);
			}
			OfferInOptions();
			// 1.7.4980 caps the scale at what keeps a 1024 x 768 window on the screen (ResolutionUtility.ResolveUIScale:
			// at most the larger of the smallest choice and min(width / 1024, height / 768)), 1.66 on this screen: the
			// phone's 1.85 was cut down, and Update below would have taken that for the player's choice and saved it.
			// Here the windows that do not fit are made smaller one by one (WindowFit), so the scale is the one set.
			MethodInfo resolve = AccessTools.Method(typeof(ResolutionUtility), "ResolveUIScale");
			if (resolve != null)
			{
				harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(UiScale), nameof(Uncapped)));
			}
			MethodInfo safe = AccessTools.Method(typeof(ResolutionUtility), "UIScaleSafeWithResolution");
			if (safe != null)
			{
				harmony.Patch(safe, prefix: new HarmonyMethod(typeof(UiScale), nameof(FitsWithWindowFit)));
			}
			harmony.Patch(AccessTools.Method(typeof(ResolutionUtility), nameof(ResolutionUtility.Update)), prefix: new HarmonyMethod(typeof(UiScale), nameof(WithoutScaleReset)));
			harmony.Patch(AccessTools.Method(typeof(Window), nameof(Window.WindowOnGUI)), prefix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.Before)), finalizer: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.After)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawShadowAround)), prefix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.BeforeShadow)));
			try
			{
				Type backend = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiGuiBackend");
				harmony.Patch(AccessTools.Method(backend, "GuiToScreenPoint"), postfix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.AfterGuiToScreen)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] window fit: positions inside a fitted window are left as the engine gives them: " + e.Message);
			}
			Console.WriteLine("[mobile] UI scale " + preferred.ToString("F2") + " (" + Screen.width + "x" + Screen.height + " -> " + Mathf.RoundToInt(Screen.width / preferred) + "x" + Mathf.RoundToInt(Screen.height / preferred) + ")");
		}

		/// <summary>The phone's default scale.</summary>
		private const float PhoneDefault = 1.85f;

		/// <summary>Only against nonsense (0, a typo in mobile.txt): the scale itself is the player's.</summary>
		private const float MinScale = 0.5f, MaxScale = 5f;

		/// <summary>Steps between the game's choices (0.75, 1, 1.25, 1.5, 1.75, 2, 2.5...), for a screen they all miss.</summary>
		private static readonly float[] FinerSteps = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.1f, 1.2f, 1.3f, 1.4f, 1.6f, 1.7f, 1.8f, 1.9f, 2.1f, 2.2f, 2.25f, 2.3f, 2.4f, 2.75f };

		/// <summary>
		/// The choices of the options dialog: the game's, the finer steps above, the phone's default (1.85) and the scale in
		/// use, whatever was chosen since (once a smaller scale was chosen, 1.85 was no longer in the list: it was put in
		/// only while it was the scale in use).
		/// </summary>
		private static void OfferInOptions()
		{
			try
			{
				float[] scales = Dialog_Options.UIScales;
				if (scales == null || scales.Length < 2)
				{
					return;
				}
				var list = new System.Collections.Generic.List<float>(scales);
				var extras = new System.Collections.Generic.List<float>(FinerSteps) { PhoneDefault, preferred };
				foreach (float extra in extras)
				{
					if (!list.Exists(s => Mathf.Abs(s - extra) < 0.001f))
					{
						list.Add(extra);
					}
				}
				list.Sort();
				Dialog_Options.UIScales = list.ToArray();
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] could not add the phone's UI scale to the options: " + e.Message);
			}
		}

		/// <summary>
		/// Prefix of ResolutionUtility.UIScaleSafeWithResolution, which the options dialog asks before it takes a scale: the
		/// game wants 1024 x 768 units left on the screen, at most 1.66 on a phone 1272 pixels high (1.4 on one 1080 high),
		/// so a player who chose a smaller scale could not go back ("screen resolution too small"). Every scale is taken:
		/// a window that does not fit is drawn smaller on its own (WindowFit), and a scale the player cannot work with is
		/// not kept in the game's confirmation.
		/// </summary>
		public static bool FitsWithWindowFit(float scale, int w, int h, ref bool __result)
		{
			__result = scale > 0f;
			return false;
		}

		/// <summary>
		/// Prefix of ResolutionUtility.Update, in place of it: the same, without "Resolution too small (WxH), resetting UI
		/// scale": every 30 frames, when the scale leaves less than 1024 units across and 768 down, the game puts back its
		/// recommended scale (1.3 on this phone at 3x; at 1.75 already on a phone 1600 x 720) — taken here for the player's
		/// choice and saved, so such a phone could never keep a larger scale.
		/// </summary>
		public static bool WithoutScaleReset()
		{
			if (RealTime.frameCount % 30 != 0 || LongEventHandler.AnyEventNowOrWaiting)
			{
				return false;
			}
			bool changed = false;
			if (Screen.fullScreenMode != Prefs.FullScreenMode)
			{
				Prefs.FullScreenMode = Screen.fullScreenMode;
				if (Screen.fullScreen)
				{
					Resolution native = ResolutionUtility.NativeResolution;
					Prefs.ScreenWidth = native.width;
					Prefs.ScreenHeight = native.height;
				}
				changed = true;
			}
			if (!Screen.fullScreen)
			{
				if (Screen.windowWidth != Prefs.ScreenWidth)
				{
					Prefs.ScreenWidth = Screen.windowWidth;
					changed = true;
				}
				if (Screen.windowHeight != Prefs.ScreenHeight)
				{
					Prefs.ScreenHeight = Screen.windowHeight;
					changed = true;
				}
			}
			if (changed)
			{
				Prefs.Save();
			}
			return false;
		}

		/// <summary>
		/// Prefix of ResolutionUtility.ResolveUIScale (1.7.4980): the scale set, times the system's display scaling as the
		/// game counts it (1 on the phone: the GLFW shim's content scale is 1), without the cap.
		/// </summary>
		public static bool Uncapped(float preference, ref float __result)
		{
			__result = preference * ResolutionUtility.DpiScale;
			return false;
		}

		/// <summary>Every frame.</summary>
		public static void Update()
		{
			float current = Prefs.UIScale;
			if (lastSet > 0f && Mathf.Abs(current - lastSet) > 0.001f)
			{
				// Someone else set it: the player, in the options dialog.
				preferred = Mathf.Clamp(current, MinScale, MaxScale);
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

		/// <summary>Where a window that does not fit the screen is drawn, smaller by f; false when it is drawn as it is.</summary>
		private static bool Fitted(Window window, out Rect fitted, out float f)
		{
			fitted = default(Rect);
			f = 1f;
			Rect rect = window.windowRect;
			float width = UI.screenWidth, height = UI.screenHeight;
			if (rect.width < 1f || rect.height < 1f)
			{
				return false;
			}
			bool sizeFits = rect.width <= width + 0.5f && rect.height <= height + 0.5f;
			if (sizeFits && (window.draggable || window.resizeable))
			{
				return false; // the player may have moved it there
			}
			float fx = Axis(rect.xMin, rect.width, width, out int px), fy = Axis(rect.yMin, rect.height, height, out int py);
			f = Mathf.Min(fx, fy);
			if (f > 0.999f || f < SmallestFactor)
			{
				return false;
			}
			fitted = new Rect(Place(rect.xMin, rect.width, width, px, f), Place(rect.yMin, rect.height, height, py, f), rect.width * f, rect.height * f);
			return true;
		}

		/// <summary>
		/// Prefix of Widgets.DrawShadowAround. The window stack draws a window's shadow before the window, around its
		/// rectangle as designed (WindowStack.WindowStackOnGUI); the middle of the shadow, which the window's background
		/// covers, was left showing beside a window drawn smaller: a dark box over the map (the user, 2026-10-22: the log
		/// tab's 630 x 510 window above RimHUD's taller inspect pane). The shadow goes around the window as it is drawn.
		/// </summary>
		public static void BeforeShadow(ref Rect rect)
		{
			WindowStack stack = Find.WindowStack;
			if (stack == null)
			{
				return;
			}
			foreach (Window window in stack.Windows)
			{
				if (window.windowRect == rect)
				{
					if (Fitted(window, out Rect fitted, out _))
					{
						rect = fitted;
					}
					return;
				}
			}
		}

		/// <summary>Prefix of Window.WindowOnGUI.</summary>
		public static void Before(Window __instance)
		{
			active = false;
			Rect rect = __instance.windowRect;
			if (!Fitted(__instance, out Rect fitted, out float f))
			{
				return;
			}
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

		/// <summary>
		/// Postfix of the backend's GuiToScreenPoint. The game asks where on the screen a point of the interface being
		/// drawn is by handing over the point divided by its scale, and wants the answer in units of that scale; the
		/// engine answers with the offset of the open groups plus the matrix times what it was handed. That is the
		/// right answer while the matrix is the game's scale. Inside a window drawn smaller the offset is in the
		/// window's smaller units and comes out too large (or, in a list scrolled far, too negative): a row being
		/// dragged to another place in such a window (the mod page) then lands some rows away from the finger, the
		/// further the list is scrolled. Here the point is put through the matrix as it is drawn.
		/// </summary>
		public static void AfterGuiToScreen(Vector2 guiPoint, ref Vector2 __result, float ___m00, float ___m11, bool ___rotated, float2 ___translate, float2 ___offset)
		{
			float scale = Prefs.UIScale;
			if (___rotated || scale <= 0f || (Mathf.Abs(___m00 - scale) < 0.0001f && Mathf.Abs(___m11 - scale) < 0.0001f))
			{
				return;
			}
			__result = new Vector2((___m00 * (___offset.X + guiPoint.x * scale) + ___translate.X) / scale, (___m11 * (___offset.Y + guiPoint.y * scale) + ___translate.Y) / scale);
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
