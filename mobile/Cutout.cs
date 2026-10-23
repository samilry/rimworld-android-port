using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The camera's hole (the display cutout) covers no part of the interface; the picture still fills the screen.
	///
	/// The window is laid out over the hole (MainActivity: the map is drawn under it). Where the hole is, the activity
	/// reads from the system's insets for the screen's present turn (any phone: a hole in the middle of a side, in a
	/// corner, a notch, two holes; none on a phone with the camera under the screen) and the GLFW shim hands it over
	/// (glfwAndroidCutout, asked once a second). Only what would lie over the hole is moved, away from the edge the hole
	/// is at:
	///   - windows (the architect's menu, the main tabs, the inspect pane, float menus, dialogs), before the window stack
	///     draws them: moved aside, made narrower where there is no room;
	///   - the resource readout at the top left, and the column at the bottom right (time controls, date and weather,
	///     letters, alerts), as a whole, when they reach the hole.
	/// Nothing on the map moves: the game and the mods place labels there by the camera's screen position.
	/// A black band beside the hole (the window kept clear of it, LAYOUT_IN_DISPLAY_CUTOUT_MODE_NEVER) was tried first and
	/// turned down by the user ("太难看了").
	/// Options, interface page: "避开摄像头挖孔", on by default, at once (mobile.txt avoidCutout=0 switches it off;
	/// cutoutMirror=1 puts the hole on the other side, for trying the right column without turning the phone over).
	/// </summary>
	internal static class Cutout
	{
		[DllImport("glfw")] private static extern int glfwAndroidCutout(byte[] buffer, int size);

		private const float Margin = 6f;

		private static bool on = Settings.Get("avoidCutout", true);
		// The holes in interface units (origin top left), with the margin around them.
		private static readonly List<Rect> holes = new List<Rect>();
		private static readonly byte[] buffer = new byte[512];
		private static string lastAsked;
		private static int nextPoll;
		private static float scaleAtPoll;
		private static FieldInfo readoutHeight;
		// Whether each patched drawing call (in the order they run) opened a group; the finalizer closes it.
		private static readonly Stack<bool> groups = new Stack<bool>();
		private static bool movedSaid;

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeWindows)));
				HarmonyMethod end = new HarmonyMethod(typeof(Cutout), nameof(EndShift));
				harmony.Patch(AccessTools.Method(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeReadout)), finalizer: end);
				HarmonyMethod right = new HarmonyMethod(typeof(Cutout), nameof(BeforeRightColumn));
				harmony.Patch(AccessTools.Method(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI)), prefix: right, finalizer: end);
				harmony.Patch(AccessTools.Method(typeof(RimWorld.Planet.WorldGlobalControls), nameof(RimWorld.Planet.WorldGlobalControls.WorldGlobalControlsOnGUI)), prefix: right, finalizer: end);
				harmony.Patch(AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutOnGUI)), prefix: right, finalizer: end);
				readoutHeight = AccessTools.Field(typeof(ResourceReadout), "lastDrawnHeight");
				Console.WriteLine("[mobile] cutout: the interface keeps clear of the camera's hole" + (on ? "" : " (switched off in the options)"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] cutout: not patched: " + e.Message);
			}
		}

		/// <summary>About once a second, and when the interface scale changed: where the holes are now.</summary>
		private static void Poll()
		{
			float scale = Prefs.UIScale > 0f ? Prefs.UIScale : 1f;
			if (Time.frameCount < nextPoll && scale == scaleAtPoll)
			{
				return;
			}
			nextPoll = Time.frameCount + 60;
			scaleAtPoll = scale;
			string raw;
			try
			{
				int length = glfwAndroidCutout(buffer, buffer.Length);
				raw = length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : "";
			}
			catch (Exception)
			{
				// An APK from before the shim had it: nothing to ask.
				raw = "";
				nextPoll = int.MaxValue;
			}
			string asked = raw + "@" + scale.ToString(CultureInfo.InvariantCulture);
			if (asked == lastAsked)
			{
				return;
			}
			lastAsked = asked;
			holes.Clear();
			foreach (string part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string[] edges = part.Split(',');
				if (edges.Length == 4 && int.TryParse(edges[0], out int left) && int.TryParse(edges[1], out int top) && int.TryParse(edges[2], out int rightEdge) && int.TryParse(edges[3], out int bottom))
				{
					if (Settings.Get("cutoutMirror", 0) == 1)
					{
						// Test: the hole on the other side, as with the phone turned over (mobile.txt cutoutMirror=1).
						int width = Screen.width;
						(left, rightEdge) = (width - rightEdge, width - left);
					}
					holes.Add(new Rect(left / scale - Margin, top / scale - Margin, (rightEdge - left) / scale + 2f * Margin, (bottom - top) / scale + 2f * Margin));
				}
			}
			Console.WriteLine("[mobile] cutout: " + (holes.Count == 0 ? "no hole" : string.Join(", ", holes.Select(h => "(" + h.xMin.ToString("0") + "," + h.yMin.ToString("0") + ")-(" + h.xMax.ToString("0") + "," + h.yMax.ToString("0") + ")")) + " in interface units of " + UI.screenWidth + "x" + UI.screenHeight));
		}

		/// <summary>Moves rect off the holes it overlaps, away from the edge each hole is at; narrower (or lower) where there is no room. Whether it changed.</summary>
		private static bool KeepClear(ref Rect rect)
		{
			bool changed = false;
			float width = UI.screenWidth, height = UI.screenHeight;
			foreach (Rect hole in holes)
			{
				if (!rect.Overlaps(hole))
				{
					continue;
				}
				changed = true;
				if (hole.center.x < width * 0.3f)
				{
					float need = hole.xMax - rect.xMin, room = Mathf.Max(0f, width - rect.xMax);
					rect.x += Mathf.Min(need, room);
					if (need > room)
					{
						rect.xMin = hole.xMax;
					}
				}
				else if (hole.center.x > width * 0.7f)
				{
					float need = rect.xMax - hole.xMin, room = Mathf.Max(0f, rect.xMin);
					rect.x -= Mathf.Min(need, room);
					if (need > room)
					{
						rect.xMax = hole.xMin;
					}
				}
				else if (hole.center.y < height * 0.5f)
				{
					float need = hole.yMax - rect.yMin, room = Mathf.Max(0f, height - rect.yMax);
					rect.y += Mathf.Min(need, room);
					if (need > room)
					{
						rect.yMin = hole.yMax;
					}
				}
				else
				{
					float need = rect.yMax - hole.yMin, room = Mathf.Max(0f, rect.yMin);
					rect.y -= Mathf.Min(need, room);
					if (need > room)
					{
						rect.yMax = hole.yMin;
					}
				}
			}
			return changed;
		}

		/// <summary>Prefix of WindowStack.WindowStackOnGUI: every window that would lie over a hole is moved before it is drawn (and hit).</summary>
		public static void BeforeWindows(WindowStack __instance)
		{
			if (!on)
			{
				return;
			}
			Poll();
			if (holes.Count == 0)
			{
				return;
			}
			foreach (Window window in __instance.Windows)
			{
				Rect rect = window.windowRect;
				if (KeepClear(ref rect))
				{
					window.windowRect = rect;
					if (!movedSaid)
					{
						movedSaid = true;
						Console.WriteLine("[mobile] cutout: " + window.GetType().Name + " moved off the hole");
					}
				}
			}
		}

		/// <summary>Prefix of ResourceReadout.ResourceReadoutOnGUI: to the right of a hole at the left edge the list reaches.</summary>
		public static void BeforeReadout(ResourceReadout __instance)
		{
			float dx = 0f;
			if (on && holes.Count > 0)
			{
				float drawn = readoutHeight != null && readoutHeight.GetValue(__instance) is float h ? h : UI.screenHeight;
				Rect area = new Rect(0f, 0f, 130f, 7f + Mathf.Max(drawn, 30f));
				foreach (Rect hole in holes)
				{
					if (hole.center.x < UI.screenWidth * 0.3f && area.Overlaps(hole))
					{
						dx = Mathf.Max(dx, hole.xMax);
					}
				}
			}
			Push(dx);
		}

		/// <summary>
		/// Prefix of the bottom right column's parts (GlobalControls, with the letters, and AlertsReadout above them): to the
		/// left of a hole at the right edge, all of them together, when the column (as high as it was the frame before) reaches it.
		/// </summary>
		public static void BeforeRightColumn()
		{
			float dx = 0f;
			if (on && holes.Count > 0 && Current.ProgramState == ProgramState.Playing)
			{
				float top = 0f;
				try
				{
					top = Find.LetterStack.LastTopY - Find.Alerts.AlertsHeight;
				}
				catch (Exception)
				{
				}
				Rect area = new Rect(UI.screenWidth - 200f, top, 200f, UI.screenHeight - top);
				foreach (Rect hole in holes)
				{
					if (hole.center.x > UI.screenWidth * 0.7f && area.Overlaps(hole))
					{
						dx = Mathf.Min(dx, hole.xMin - UI.screenWidth);
					}
				}
			}
			Push(dx);
		}

		private static void Push(float dx)
		{
			bool shift = dx != 0f;
			if (shift)
			{
				GUI.BeginGroup(new Rect(dx, 0f, UI.screenWidth, UI.screenHeight));
			}
			groups.Push(shift);
		}

		/// <summary>Finalizer of the shifted drawing calls: the group their prefix opened is closed, whatever happened inside.</summary>
		public static void EndShift()
		{
			if (groups.Count > 0 && groups.Pop())
			{
				GUI.EndGroup();
			}
		}

		public static void OptionRow(Listing_Standard listing)
		{
			bool check = on;
			listing.CheckboxLabeled(Texts.Chinese ? "避开摄像头挖孔" : "Keep clear of the camera cutout", ref check, Texts.Chinese
				? "画面仍然铺满屏幕，但会和摄像头挖孔重叠的界面（窗口、左上角资源、右下角时间和信件等）会自动挪开。挖孔的位置由手机系统提供，手机翻转时跟着换边。"
				: "The picture still fills the screen, but the parts of the interface that would lie over the camera's hole (windows, the resources at the top left, the time controls and letters at the bottom right) move aside. Where the hole is comes from the phone; it changes side when the phone is turned over.", 30f, 0.6f);
			if (check != on)
			{
				on = check;
				Settings.Set("avoidCutout", on ? "1" : "0");
			}
		}
	}
}
