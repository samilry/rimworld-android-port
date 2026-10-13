using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Touch gestures. The Android GLFW (host/glfw) hands every finger event to this class and injects into the
	/// engine's input only what this class asks for, so the game sees a mouse that does what a finger means:
	///
	/// on the map or the world (a touch the interface did not take)
	///   tap            a left click
	///   drag           moves the camera (the ground follows the finger)
	///   hold           with something selected: a right click (orders); otherwise the selection box starts and
	///                  moving the finger then selects an area
	///   two fingers    pinch to zoom around the fingers, drag to move
	/// on the interface
	///   tap, drag      the left button, as a mouse
	///   two fingers    over a window: the wheel (pinch) and a middle-button drag (moving both fingers), which is
	///                  what moves and zooms a mod's own map or graph; the game's map is left alone meanwhile
	/// </summary>
	internal static unsafe class Touch
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct TouchEvent
		{
			public int type, id;
			public float x, y;
			public double time;
		}

		private const int Down = 1, Move = 2, Up = 3, Cancel = 4;
		private const int Press = 1, Release = 0;
		private const int Left = 0, Right = 1, Middle = 2;

		[DllImport("glfw")] private static extern void glfwAndroidSetTouchMode(int managed);
		[DllImport("glfw")] private static extern int glfwAndroidPollTouches(TouchEvent* events, int max);
		[DllImport("glfw")] private static extern void glfwAndroidInjectCursor(double x, double y);
		[DllImport("glfw")] private static extern void glfwAndroidInjectButton(int button, int action, int mods);
		[DllImport("glfw")] private static extern void glfwAndroidInjectScroll(double dx, double dy);
		[DllImport("glfw")] private static extern float glfwAndroidGetDensity();

		private sealed class Finger
		{
			public int id;
			public Vector2 pos, start;
			public double downTime;
			public bool seen; // Step has looked at it (a finger can land and lift between two frames)
		}

		private enum Mode
		{
			None,
			Pending,   // first finger down, the press is on its way to the game; not yet known what the touch will be
			Interface, // the interface took the press: a mouse
			Map,       // the map (or the world) took the press: a selection box is active
			Pan,
			Box,       // a hold on the map with nothing to order: the selection box stays and the finger drags it
			Tool,      // a designator or targeter took the press (as a mouse for now)
			Held,      // a hold on the interface: tooltips, no click
			Two,       // two fingers: zoom and pan (for the map) or wheel and middle drag (for a window)
			Finished   // nothing more to recognise in this touch; wait for all fingers to lift
		}

		private const double HoldSeconds = 0.45;
		// How far the wheel turns (GLFW units; a mouse notch is 1) when the fingers of a pinch double their distance.
		private const double WheelPerDoubling = 5.0;
		private static readonly AccessTools.FieldRef<CameraDriver, Vector2> MapDolly = AccessTools.FieldRefAccess<CameraDriver, Vector2>("desiredDollyRaw");
		private static readonly AccessTools.FieldRef<CameraDriver, float> MapSize = AccessTools.FieldRefAccess<CameraDriver, float>("desiredSize");
		private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector2> WorldRotation = AccessTools.FieldRefAccess<WorldCameraDriver, Vector2>("desiredRotationRaw");
		private static readonly AccessTools.FieldRef<WorldCameraDriver, float> WorldAltitude = AccessTools.FieldRefAccess<WorldCameraDriver, float>("desiredAltitude");

		private static readonly List<Finger> fingers = new List<Finger>();
		private static readonly TouchEvent[] buffer = new TouchEvent[512];
		private static Mode mode;
		private static int framesDown;
		private static Vector2 last;
		private static float density = 1f;
		// What the engine believes about the mouse buttons (injected), and what still has to be injected next frame.
		private static bool leftDown, rightDown, middleDown;
		private static int releaseRightNextFrame;
		private static float pinchDistance;
		private static Vector2 pinchCentre;
		private static Window gestureWindow;
		private static double pendingWheel;
		// A finger that landed and lifted between two frames: still a tap.
		private static Finger quickTap;

		public static bool Verbose;

		public static int FingerCount => fingers.Count;

		/// <summary>A touch is being delivered to the game: a finger is down, or the press of a finger that already lifted (a quick tap) is still on its way.</summary>
		public static bool TouchInProgress => fingers.Count > 0 || leftDown;

		public static void Start()
		{
			density = glfwAndroidGetDensity();
			Verbose = File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "touch-verbose.txt"));
			var harmony = new Harmony("port17.mobile.touch");
			harmony.Patch(AccessTools.Method(typeof(UIRoot), nameof(UIRoot.UIRootUpdate)), prefix: new HarmonyMethod(typeof(Touch), nameof(Update)));
			glfwAndroidSetTouchMode(1);
			Diag.Start(harmony);
			Interface.Start(harmony);
			MapTools.Start(harmony);
			DiagFloatMenu.Start(harmony);
			Log("touch layer on (density " + density.ToString("F2") + ", drag threshold " + DragThreshold.ToString("F0") + " px)");
		}

		private static void Log(string text)
		{
			Console.WriteLine("[mobile] " + text);
		}

		private static void Trace(string text)
		{
			if (Verbose)
			{
				Log(text);
			}
		}

		// A movement of about 2 mm separates a drag from an unsteady tap.
		private static float DragThreshold => Mathf.Max(12f, density * 160f * 0.08f);

		private static bool Playing => Current.ProgramState == ProgramState.Playing;

		private static bool OnWorld
		{
			get
			{
				World world = Current.Game != null ? Find.World : null;
				return world != null && world.renderer != null && WorldRendererUtility.WorldSelected;
			}
		}

		private static bool InGame => Playing || (Current.ProgramState == ProgramState.Entry && Current.Game != null && OnWorld);

		private static bool BoxActive
		{
			get
			{
				if (OnWorld)
				{
					return Find.WorldSelector != null && Find.WorldSelector.dragBox.active;
				}
				return Playing && Find.Selector != null && Find.Selector.dragBox.active;
			}
		}

		private static bool ToolActive
		{
			get
			{
				if (OnWorld)
				{
					return Find.WorldTargeter != null && Find.WorldTargeter.IsTargeting;
				}
				return Playing && (Find.DesignatorManager.SelectedDesignator != null || Find.Targeter.IsTargeting);
			}
		}

		private static bool SomethingToOrder
		{
			get
			{
				if (OnWorld)
				{
					return Find.WorldSelector != null && Find.WorldSelector.NumSelectedObjects > 0;
				}
				return Playing && Find.Selector.SelectedPawns.Count > 0;
			}
		}

		/// <summary>The window at a point of the screen (window pixels, y down), as the game's window stack sees it.</summary>
		private static Window WindowAt(Vector2 pixels)
		{
			if (Current.Root == null || Find.UIRoot == null || Find.WindowStack == null)
			{
				return null;
			}
			return Find.WindowStack.GetWindowAt(pixels / Prefs.UIScale);
		}

		// ---- what the engine is told ----

		private static void Cursor(Vector2 pixels)
		{
			glfwAndroidInjectCursor(pixels.x, pixels.y);
		}

		private static void Button(int button, bool down)
		{
			glfwAndroidInjectButton(button, down ? Press : Release, 0);
			if (button == Left) leftDown = down;
			else if (button == Right) rightDown = down;
			else middleDown = down;
		}

		// ---- per frame, before the engine dispatches this frame's input (prefix of UIRoot.UIRootUpdate) ----

		public static void Update()
		{
			try
			{
				Step();
			}
			catch (Exception e)
			{
				Log("touch update failed: " + e);
				mode = Mode.None;
			}
		}

		private static void Step()
		{
			if (releaseRightNextFrame > 0 && --releaseRightNextFrame == 0 && rightDown)
			{
				Button(Right, false);
			}
			Replay.Feed();
			Interface.Update();
			MapTools.Update();
			bool changed = Drain();
			int count = fingers.Count;
			if (count == 0)
			{
				if (mode != Mode.None)
				{
					Trace("touch ends (" + mode + ")");
				}
				if (mode == Mode.Two && gestureWindow != null)
				{
					EndWindowGesture();
				}
				if (mode == Mode.Held)
				{
					Interface.HoldEnded();
				}
				// The last finger has lifted: the release reaches the game where the finger was.
				if (leftDown) Button(Left, false);
				if (middleDown) Button(Middle, false);
				mode = Mode.None;
				gestureWindow = null;
				if (quickTap != null)
				{
					// The press now, the release next frame (above), a click for the game.
					Trace("quick tap at " + quickTap.pos);
					Cursor(quickTap.pos);
					Button(Left, true);
					quickTap = null;
				}
				return;
			}
			Finger first = fingers[0];
			if (mode == Mode.Finished)
			{
				KeepBoxOff();
				return;
			}
			if (count >= 2)
			{
				TwoFingers(first, fingers[1]);
				return;
			}
			if (mode == Mode.Two)
			{
				// One finger left of a two-finger gesture: not a click, not a pan jump.
				if (gestureWindow != null) EndWindowGesture();
				mode = Mode.Finished;
				KeepBoxOff();
				return;
			}
			if (mode == Mode.None)
			{
				// A new touch: the press goes to the game at once, so that the interface responds like to a mouse.
				mode = Mode.Pending;
				Interface.TouchBegan();
				first.seen = true;
				framesDown = 0;
				last = first.pos;
				Cursor(first.pos);
				Button(Left, true);
				Trace("touch begins at " + first.pos);
				return;
			}
			Vector2 delta = first.pos - last;
			last = first.pos;
			framesDown++;
			bool moved = (first.pos - first.start).magnitude > DragThreshold;
			bool held = Now - first.downTime > HoldSeconds;
			Mode was = mode;
			if (mode == Mode.Pending && framesDown >= 2)
			{
				// The game handled the press during the previous frame's dispatch: what did it do with it?
				if (InGame && BoxActive)
				{
					mode = Mode.Map;
				}
				else if (InGame && ToolActive && WindowAt(first.start) == null)
				{
					mode = Mode.Tool;
				}
				else
				{
					mode = Mode.Interface;
				}
			}
			switch (mode)
			{
				case Mode.Map:
					if (moved)
					{
						mode = Mode.Pan;
						KeepBoxOff();
						Pan(first.pos - first.start);
					}
					else if (held)
					{
						if (SomethingToOrder)
						{
							// A right click where the finger rests: orders for what is selected.
							KeepBoxOff();
							Button(Right, true);
							releaseRightNextFrame = 2;
							mode = Mode.Finished;
						}
						else
						{
							mode = Mode.Box; // the box stays, and moving the finger now selects an area
						}
					}
					break;
				case Mode.Pan:
					KeepBoxOff();
					Pan(delta);
					break;
				case Mode.Tool:
					if (MapTools.Deferred && moved)
					{
						// Looking for the place rather than pointing at it: the held-back press is dropped and the finger pans.
						MapTools.CancelDeferred();
						mode = Mode.Pan;
						KeepBoxOff();
						Pan(first.pos - first.start);
					}
					break;
				case Mode.Interface:
					if (held && !moved && !DragAndDropWidget.Dragging && !ReorderableWidget.Dragging && !Widgets.Painting)
					{
						// A finger resting on the interface is reading: the tooltips show now and nothing is clicked.
						mode = Mode.Held;
						Interface.HoldOnInterface();
					}
					break;
				default:
					break;
			}
			if (mode != Mode.Pan && changed)
			{
				Cursor(first.pos);
			}
			if (mode != was)
			{
				Trace("touch at " + first.pos + ": " + was + " -> " + mode + " (frame " + framesDown + ", box " + BoxActive + ", tool " + ToolActive + ", window " + (WindowAt(first.start)?.GetType().Name ?? "none") + ")");
			}
		}

		private static void TwoFingers(Finger a, Finger b)
		{
			float distance = (a.pos - b.pos).magnitude;
			Vector2 centre = (a.pos + b.pos) / 2f;
			if (mode != Mode.Two)
			{
				mode = Mode.Two;
				CancelWhatTheFirstFingerStarted();
				Window under = WindowAt(centre);
				if (under != null)
				{
					BeginWindowGesture(under, centre);
				}
				Trace("two fingers around " + centre + ": " + (under != null ? "for the window " + under.GetType().Name : "for the map"));
			}
			else if (gestureWindow != null)
			{
				Cursor(centre);
				if (pinchDistance > 1f && distance > 1f)
				{
					pendingWheel += WheelPerDoubling * Math.Log(distance / pinchDistance, 2.0);
				}
				if (Math.Abs(pendingWheel) >= 0.25)
				{
					glfwAndroidInjectScroll(0, pendingWheel);
					pendingWheel = 0;
				}
			}
			else if (InGame)
			{
				// The engine's mouse sits between the fingers: the game zooms towards it, so the ground stays under them.
				Cursor(centre);
				if (pinchDistance > 1f && distance > 1f)
				{
					Zoom(pinchDistance / distance);
				}
				Pan(centre - pinchCentre);
			}
			pinchDistance = distance;
			pinchCentre = centre;
			KeepBoxOff();
		}

		private static void BeginWindowGesture(Window window, Vector2 centre)
		{
			gestureWindow = window;
			pendingWheel = 0;
			Cursor(centre);
			Button(Middle, true);
		}

		private static void EndWindowGesture()
		{
			if (middleDown) Button(Middle, false);
			gestureWindow = null;
			pendingWheel = 0;
		}

		private static void CancelWhatTheFirstFingerStarted()
		{
			KeepBoxOff();
			MapTools.CancelDeferred();
			if (Playing && Find.DesignatorManager != null && Find.DesignatorManager.Dragger.Dragging)
			{
				Find.DesignatorManager.Dragger.EndDrag();
			}
			// Nothing may act on the release of the first finger's press (a button fires on the release).
			GUIUtility.hotControl = 0;
			if (leftDown) Button(Left, false);
		}

		private static void KeepBoxOff()
		{
			if (OnWorld)
			{
				if (Find.WorldSelector != null) Find.WorldSelector.dragBox.active = false;
			}
			else if (Playing && Find.Selector != null)
			{
				Find.Selector.dragBox.active = false;
			}
		}

		/// <summary>Moves the camera so that the ground follows the finger (window pixels, y down).</summary>
		private static void Pan(Vector2 pixels)
		{
			if (pixels == Vector2.zero)
			{
				return;
			}
			Vector2 logical = pixels / Prefs.UIScale;
			if (OnWorld)
			{
				WorldRotation(Find.WorldCameraDriver) += new Vector2(-logical.x, logical.y) / GenWorldUI.CurUITileSize() * 0.273f;
			}
			else if (Find.CurrentMap != null)
			{
				MapDolly(Find.CameraDriver) += new Vector2(-logical.x, logical.y) / UI.CurUICellSize();
			}
		}

		/// <summary>Factor above 1 zooms out.</summary>
		private static void Zoom(float factor)
		{
			if (OnWorld)
			{
				ref float altitude = ref WorldAltitude(Find.WorldCameraDriver);
				altitude = Mathf.Clamp(altitude * factor, WorldCameraDriver.MinAltitude, 1100f);
			}
			else if (Find.CurrentMap != null)
			{
				CameraDriver driver = Find.CameraDriver;
				ref float size = ref MapSize(driver);
				size = Mathf.Clamp(size * factor, driver.config.sizeRange.min, driver.config.sizeRange.max);
			}
		}

		private static double Now => Time.realtimeSinceStartupAsDouble;

		// ---- the fingers ----

		/// <summary>Takes the finger events the GLFW shim recorded since the last frame. True if anything moved.</summary>
		private static bool Drain()
		{
			bool changed = false;
			int n;
			fixed (TouchEvent* events = buffer)
			{
				while ((n = glfwAndroidPollTouches(events, buffer.Length)) > 0)
				{
					for (int i = 0; i < n; i++)
					{
						changed |= Apply(events[i].type, events[i].id, new Vector2(events[i].x, events[i].y));
					}
					if (n < buffer.Length) break;
				}
			}
			return changed;
		}

		internal static bool Apply(int type, int id, Vector2 pos)
		{
			Finger finger = fingers.Find(f => f.id == id);
			switch (type)
			{
				case Down:
					if (finger == null)
					{
						fingers.Add(new Finger { id = id, pos = pos, start = pos, downTime = Now });
					}
					return true;
				case Move:
					if (finger != null && finger.pos != pos)
					{
						finger.pos = pos;
						return true;
					}
					return false;
				case Up:
				case Cancel:
					if (finger != null)
					{
						finger.pos = pos;
						fingers.Remove(finger);
						if (!finger.seen && type == Up && fingers.Count == 0 && mode == Mode.None)
						{
							quickTap = finger;
						}
					}
					return true;
			}
			return false;
		}

		/// <summary>
		/// Scripted touches for testing from the PC (adb cannot do two fingers): a file gesture.txt next to the game,
		/// one event per line "seconds id type x y" (type: down, move, up), played from the moment the file is seen,
		/// then deleted.
		/// </summary>
		private static class Replay
		{
			private static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "gesture.txt");
			private static readonly Queue<(double at, int id, int type, Vector2 pos)> steps = new Queue<(double, int, int, Vector2)>();
			private static double started;
			private static int checkCountdown;

			public static void Feed()
			{
				if (steps.Count == 0)
				{
					if (--checkCountdown > 0)
					{
						return;
					}
					checkCountdown = 30;
					if (!File.Exists(FilePath))
					{
						return;
					}
					var culture = System.Globalization.CultureInfo.InvariantCulture;
					foreach (string line in File.ReadAllLines(FilePath))
					{
						string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
						if (parts.Length < 5 || parts[0].StartsWith("#"))
						{
							continue;
						}
						int type = parts[2] == "down" ? Down : parts[2] == "up" ? Up : Move;
						steps.Enqueue((double.Parse(parts[0], culture), int.Parse(parts[1]), type, new Vector2(float.Parse(parts[3], culture), float.Parse(parts[4], culture))));
					}
					File.Delete(FilePath);
					started = Now;
					Verbose = true;
					Log("replaying " + steps.Count + " scripted touch events");
					return;
				}
				while (steps.Count > 0 && Now - started >= steps.Peek().at)
				{
					var step = steps.Dequeue();
					Apply(step.type, 1000 + step.id, step.pos);
				}
			}
		}
	}
}
