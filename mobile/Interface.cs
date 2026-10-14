using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace PortMobile
{
	/// <summary>
	/// The interface under a finger:
	///   lists scroll under a dragging finger, with the game's own Steam Deck code (which it only runs on a Deck),
	///   and such a drag does not drag the window along;
	///   a hold shows the tooltips of what is under the finger at once, keeps them after the finger lifts until
	///   the next touch, and clicks nothing;
	///   the tooltips of a tap do not linger (the engine's mouse stays where the last finger was);
	///   the soft keyboard comes up while a text field has the focus.
	/// </summary>
	internal static class Interface
	{
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidShowKeyboard(int show);

		private static readonly IDictionary ActiveTips = (IDictionary)AccessTools.Field(typeof(TooltipHandler), "activeTips").GetValue(null);
		private static readonly FieldInfo TipFrame = AccessTools.Field(typeof(TooltipHandler), "frame");
		private static readonly FieldInfo ButtonDown = AccessTools.Field(typeof(GUI), "_buttonDownActive");
		private static readonly FieldInfo CurrentScrollView = AccessTools.Field(typeof(SteamDeck), "currentScrollView");

		// While a hold on the interface lasts, and a frame or two after it, the left button's events are swallowed.
		private static int suppressClickUntilFrame = -1;
		// What a hold showed stays until the next touch.
		private static bool keepTips;
		private static bool keyboardShown;

		// A finger that begins in a list with something to scroll presses nothing until it is known to be a tap (see
		// Touch): while it scrolls, the list that begins at this point of the screen follows it, and goes on for a
		// while after it lifts. mobile.txt: listTouch=0 gives the press at once again, and the scrolling of the game's
		// own Steam Deck code with it.
		private static bool listTouch = true;
		private static bool listScrolling, coasting;
		private static float activeX, activeY;

		public static bool ListTouch => listTouch && TapAssist.Ready;

		// The lists being drawn in the current press event (they nest): whether the press landed in each.
		private static readonly List<bool> open = new List<bool>();

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SteamDeck), nameof(SteamDeck.HandleTouchScreenScrollViewScroll)), prefix: new HarmonyMethod(typeof(Interface), nameof(FingerScroll)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginScrollView), new[] { typeof(Rect), typeof(Vector2).MakeByRefType(), typeof(Rect), typeof(bool) }), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.OnGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			// The engine hands a mouse event to the window under the mouse before the game's OnGUI sees it.
			harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.DispatchInputToWindows)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisibleDraggable)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeDraggableButton)));
			harmony.Patch(AccessTools.Method(typeof(TooltipHandler), nameof(TooltipHandler.DoTooltipGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTooltips)));
			listTouch = Settings.Get("listTouch", 1) != 0;
		}

		// ---- told by Touch ----

		/// <summary>A new touch begins: what the last hold showed goes away.</summary>
		public static void TouchBegan()
		{
			// The keyboard may have been dismissed by the system (Back) while the field kept the focus: a touch asks for it again.
			keyboardShown = false;
			if (keepTips)
			{
				keepTips = false;
				ActiveTips.Clear();
			}
		}

		/// <summary>A finger has rested on the interface: for reading, not for clicking.</summary>
		public static void HoldOnInterface()
		{
			ShowTipsNow();
			keepTips = true;
			GUIUtility.hotControl = 0;
			// The engine's button remembers its press by itself: forgotten, so that the release finds nothing to fire.
			ButtonDown?.SetValue(null, false);
			suppressClickUntilFrame = int.MaxValue;
		}

		/// <summary>A list still moving on its own stops under a finger. True if the list that begins at this point of the screen was moving.</summary>
		public static bool StopCoasting(float originX, float originY)
		{
			bool was = coasting && Mathf.Abs(originX - activeX) <= 1.5f && Mathf.Abs(originY - activeY) <= 1.5f;
			coasting = false;
			scrollViewVelocity = default(Vector2);
			return was;
		}

		/// <summary>A finger has rested on a list (nothing was pressed): the tooltips show now, and stay after it lifts.</summary>
		public static void HoldInList()
		{
			ShowTipsNow();
			keepTips = true;
		}

		/// <summary>The finger that rested on a list now drags what it is on: the tooltips of the rest go away.</summary>
		public static void HoldBecameDrag()
		{
			if (keepTips)
			{
				keepTips = false;
				ActiveTips.Clear();
			}
		}

		/// <summary>The finger drags the list that begins at this point of the screen.</summary>
		public static void BeginListScroll(float originX, float originY)
		{
			listScrolling = true;
			coasting = false;
			activeX = originX;
			activeY = originY;
			scrollVelocityRecords.Clear();
			scrollViewVelocity = default(Vector2);
		}

		/// <summary>The finger has lifted (keepSpeed), or the scrolling was broken off: the list keeps the speed of the last moments, as the game does it on a Steam Deck.</summary>
		public static void EndListScroll(bool keepSpeed)
		{
			if (!listScrolling)
			{
				return;
			}
			listScrolling = false;
			scrollViewVelocity = default(Vector2);
			if (keepSpeed)
			{
				for (int i = 0; i < scrollVelocityRecords.Count; i++)
				{
					if (Time.time - scrollVelocityRecords[i].time < 0.057f)
					{
						scrollViewVelocity += scrollVelocityRecords[i].delta;
					}
				}
			}
			coasting = scrollViewVelocity != default(Vector2);
		}

		/// <summary>The finger of a hold has lifted: its release (still on its way) is swallowed too.</summary>
		public static void HoldEnded()
		{
			if (suppressClickUntilFrame == int.MaxValue)
			{
				suppressClickUntilFrame = Time.frameCount + 2;
			}
		}

		/// <summary>Once per frame: the soft keyboard follows the text focus.</summary>
		public static void Update()
		{
			// A press that a control in a list used is no longer seen as a press when the list ends: nothing is carried over.
			open.Clear();
			bool wanted = UnityStubs.Internal.TextFocus.CapturesText;
			if (wanted != keyboardShown)
			{
				keyboardShown = wanted;
				if (Touch.Verbose) Console.WriteLine("[mobile] soft keyboard " + (wanted ? "wanted" : "not wanted") + " (control " + GUIUtility.keyboardControl + ")");
				glfwAndroidShowKeyboard(wanted ? 1 : 0);
			}
		}

		public static bool FingerDown => Touch.TouchInProgress;

		// ---- scrolling ----

		private static readonly List<(float time, Vector2 delta)> scrollVelocityRecords = new List<(float, Vector2)>();
		private static Rect? currentScrollView, scrollViewWithVelocity;
		private static Vector2 currentScrollViewStartMousePos, scrollViewVelocity;
		private static bool scrollMouseTraveledEnoughDist;
		private static int consumeAllMouseUpEventsOnFrame = -1;

		/// <summary>
		/// Stands in for SteamDeck.HandleTouchScreenScrollViewScroll (the game calls it from every scroll view and
		/// returns at once off a Deck): the list follows the finger, keeps some speed when it lifts, and the release
		/// after a scroll clicks nothing. The game's own logic, with the Deck check left out.
		/// </summary>
		public static bool FingerScroll(Rect outRect, ref Vector2 scrollPosition)
		{
			if (ListTouch)
			{
				// Only the list the finger began in moves; it is known by where it begins on the screen.
				if ((!listScrolling && !coasting) || !TapAssist.ScreenOrigin(outRect, out float x, out float y) || Mathf.Abs(x - activeX) > 1.5f || Mathf.Abs(y - activeY) > 1.5f)
				{
					return false;
				}
				if (listScrolling)
				{
					// How far the mouse (which is with the finger) moved in this frame; the game hands it out once a frame.
					Vector2 moved = UnityGUIBugsFixer.CurrentEventDelta;
					if (moved != Vector2.zero)
					{
						// The game counts in units of its scale; a window made smaller to fit has smaller ones.
						moved *= Prefs.UIScale / TapAssist.CurrentScale();
						scrollPosition -= moved;
						if (scrollVelocityRecords.Count > 60)
						{
							scrollVelocityRecords.RemoveRange(0, 40);
						}
						scrollVelocityRecords.Add((Time.time, -moved));
						Clamp(ref scrollPosition);
					}
				}
				else if (Event.current.type == EventType.Repaint)
				{
					scrollPosition += scrollViewVelocity;
					scrollViewVelocity = Vector2.MoveTowards(scrollViewVelocity, default(Vector2), Time.deltaTime * 90f);
					Clamp(ref scrollPosition);
					coasting = scrollViewVelocity != default(Vector2);
				}
				return false;
			}
			if (DragAndDropWidget.Dragging || Widgets.Painting || ReorderableWidget.Dragging)
			{
				return false;
			}
			if (Input.GetMouseButtonDown(0) && FingerDown && Mouse.IsOver(outRect) && !Mouse.IsOver(new Rect(outRect.xMax - 16f, outRect.y, 16f, outRect.height)) && !Mouse.IsOver(new Rect(outRect.x, outRect.yMax - 16f, outRect.width, 16f)))
			{
				currentScrollView = outRect;
				currentScrollViewStartMousePos = UI.MousePositionOnUIInverted;
				scrollMouseTraveledEnoughDist = false;
				scrollVelocityRecords.Clear();
				if (scrollViewWithVelocity == currentScrollView)
				{
					scrollViewWithVelocity = null;
					scrollViewVelocity = default(Vector2);
				}
			}
			else if (Input.GetMouseButton(0) && currentScrollView == outRect)
			{
				if (!scrollMouseTraveledEnoughDist && (UI.MousePositionOnUIInverted - currentScrollViewStartMousePos).sqrMagnitude > 100f)
				{
					scrollMouseTraveledEnoughDist = true;
				}
				if (scrollMouseTraveledEnoughDist)
				{
					Vector2 delta = UnityGUIBugsFixer.CurrentEventDelta;
					scrollPosition -= delta;
					scrollVelocityRecords.Add((Time.time, -delta));
					Clamp(ref scrollPosition);
				}
			}
			else if (!Input.GetMouseButton(0) && currentScrollView == outRect)
			{
				if (scrollMouseTraveledEnoughDist)
				{
					scrollViewWithVelocity = currentScrollView;
					scrollViewVelocity = default(Vector2);
					for (int i = 0; i < scrollVelocityRecords.Count; i++)
					{
						if (Time.time - scrollVelocityRecords[i].time < 0.057f)
						{
							scrollViewVelocity += scrollVelocityRecords[i].delta;
						}
					}
					consumeAllMouseUpEventsOnFrame = Time.frameCount;
				}
				currentScrollView = null;
			}
			if (Event.current.type == EventType.Repaint && scrollViewWithVelocity.HasValue && scrollViewWithVelocity.Value == outRect && scrollViewVelocity != default(Vector2))
			{
				scrollPosition += scrollViewVelocity;
				scrollViewVelocity = Vector2.MoveTowards(scrollViewVelocity, default(Vector2), Time.deltaTime * 90f);
				Clamp(ref scrollPosition);
			}
			if (consumeAllMouseUpEventsOnFrame == Time.frameCount && Event.current.type == EventType.MouseUp)
			{
				Event.current.Use();
			}
			return false;
		}

		private static void Clamp(ref Vector2 pos)
		{
			if (pos.x < 0f) pos.x = 0f;
			if (pos.y < 0f) pos.y = 0f;
		}

		/// <summary>Prefix of Widgets.BeginScrollView: remembers whether the press landed in a list with something to scroll.</summary>
		public static void BeforeScrollView(Rect outRect, Rect viewRect)
		{
			Event current = Event.current;
			if (current == null)
			{
				return;
			}
			bool down = viewRect.height > outRect.height + 1f, across = viewRect.width > outRect.width + 1f;
			bool scrolls = down || across;
			if (scrolls && listTouch && current.rawType == EventType.Repaint)
			{
				TapAssist.NoteList(outRect, down, across);
			}
			if (current.rawType != EventType.MouseDown)
			{
				return;
			}
			open.Add(scrolls && current.type == EventType.MouseDown && current.button == 0 && FingerDown && Mouse.IsOver(outRect));
		}

		/// <summary>
		/// Postfix of Widgets.EndScrollView: a press in a scrolling list that nothing in the list used (a button would
		/// have) is used now, so that whatever comes after the list (the window's own dragging) does not take it.
		/// </summary>
		public static void AfterScrollView()
		{
			Event current = Event.current;
			if (current == null || current.rawType != EventType.MouseDown || open.Count == 0)
			{
				return;
			}
			bool pressedHere = open[open.Count - 1];
			open.RemoveAt(open.Count - 1);
			if (pressedHere && current.type == EventType.MouseDown)
			{
				current.Use();
			}
		}

		// ---- holds and tooltips ----

		/// <summary>The tooltips of what is under the finger show at once, without their usual delay.</summary>
		private static void ShowTipsNow()
		{
			foreach (object value in ActiveTips.Values)
			{
				if (value is ActiveTip tip)
				{
					tip.firstTriggerTime = Time.realtimeSinceStartup - tip.signal.delay - 1f;
				}
			}
		}

		/// <summary>
		/// Prefix of Root.OnGUI and of the engine's GUI.DispatchInputToWindows (which runs the window under the mouse
		/// first): the left button's events are swallowed around a hold on the interface.
		/// </summary>
		public static void BeforeGui()
		{
			Event current = Event.current;
			if (current != null && current.button == 0 && (current.type == EventType.MouseDown || current.type == EventType.MouseUp) && Time.frameCount <= suppressClickUntilFrame)
			{
				current.Use();
			}
		}

		/// <summary>Prefix of Widgets.ButtonInvisibleDraggable (checkboxes): it reads the mouse straight from Input, so a hold is kept from it here.</summary>
		public static bool BeforeDraggableButton(ref Widgets.DraggableResult __result)
		{
			if (Time.frameCount <= suppressClickUntilFrame)
			{
				__result = Widgets.DraggableResult.Idle;
				return false;
			}
			return true;
		}

		/// <summary>
		/// Prefix of TooltipHandler.DoTooltipGUI. A tooltip lives only while its region is reported under the mouse on
		/// every repaint. With no finger down nothing should show (the engine's mouse stays where the last finger
		/// was), except what a hold showed, which is kept alive here until the next touch.
		/// </summary>
		public static void BeforeTooltips()
		{
			Event current = Event.current;
			if (current == null || current.type != EventType.Repaint || FingerDown || ActiveTips.Count == 0)
			{
				return;
			}
			if (!keepTips)
			{
				ActiveTips.Clear();
				return;
			}
			int frame = (int)TipFrame.GetValue(null);
			foreach (object value in ActiveTips.Values)
			{
				if (value is ActiveTip tip)
				{
					tip.lastTriggerFrame = frame;
				}
			}
		}
	}
}
