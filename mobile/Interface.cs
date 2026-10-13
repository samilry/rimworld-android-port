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
		private static readonly FieldInfo CurrentScrollView = AccessTools.Field(typeof(SteamDeck), "currentScrollView");

		// While a hold on the interface lasts, and a frame or two after it, the left button's events are swallowed.
		private static int suppressClickUntilFrame = -1;
		// What a hold showed stays until the next touch.
		private static bool keepTips;
		private static bool keyboardShown;

		// The lists being drawn in the current press event (they nest): whether the press landed in each.
		private static readonly List<bool> open = new List<bool>();

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SteamDeck), nameof(SteamDeck.HandleTouchScreenScrollViewScroll)), prefix: new HarmonyMethod(typeof(Interface), nameof(FingerScroll)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginScrollView), new[] { typeof(Rect), typeof(Vector2).MakeByRefType(), typeof(Rect), typeof(bool) }), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.OnGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisibleDraggable)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeDraggableButton)));
			harmony.Patch(AccessTools.Method(typeof(TooltipHandler), nameof(TooltipHandler.DoTooltipGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTooltips)));
		}

		// ---- told by Touch ----

		/// <summary>A new touch begins: what the last hold showed goes away.</summary>
		public static void TouchBegan()
		{
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
			suppressClickUntilFrame = int.MaxValue;
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
			bool wanted = UnityStubs.Internal.TextFocus.CapturesText;
			if (wanted != keyboardShown)
			{
				keyboardShown = wanted;
				if (Touch.Verbose) Console.WriteLine("[mobile] soft keyboard " + (wanted ? "wanted" : "not wanted") + " (control " + GUIUtility.keyboardControl + ")");
				glfwAndroidShowKeyboard(wanted ? 1 : 0);
			}
		}

		public static bool FingerDown => Touch.FingerCount > 0;

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
			if (current == null || current.rawType != EventType.MouseDown)
			{
				return;
			}
			bool scrolls = viewRect.height > outRect.height + 1f || viewRect.width > outRect.width + 1f;
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

		/// <summary>Prefix of Root.OnGUI: the left button's events are swallowed around a hold on the interface.</summary>
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
