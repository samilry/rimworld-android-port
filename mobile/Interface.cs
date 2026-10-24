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
	///   the soft keyboard comes up while a text field has the focus;
	///   a search box (the game's QuickSearchWidget: the info card's, the load dialog's; a mod's, by "search" in its name),
	///   and the name field of a save dialog (the game's, a scenario's, an ideoligion's, a mod list's: Dialog_FileList),
	///   take the focus only from a finger on them: given the focus by code (as when their window opens), it is let go
	///   at once, so that the keyboard does not come up by itself (testers, 2026-10-22: saving by hand brought it up over
	///   the save dialog; the name is typed only to change it). Other text fields (a name to type in a rename window)
	///   keep such a focus. mobile.txt: searchFocusOnTap=0 lets them all keep it;
	///   a finger that lands anywhere but on a text field takes the focus from the field that has it, and the keyboard
	///   goes down (the game means to do that for its search boxes, but under this engine the press has already been
	///   used by the window when the search box looks at it);
	///   the soft keyboard's Done (or Enter) finishes the typing: the field lets go of the focus and the keyboard goes
	///   down. It is not the game's Enter, which accepts or closes the window the field is in (testers, 2026-10-22: a
	///   number typed in the caravan's window cancelled it, a search box kept the keyboard, the debug actions' search ran
	///   the first action). Only a chat's field ("chat_input": Multiplayer's) gets the Enter, which sends.
	/// With a mouse or a keyboard connected the tooltips, the search boxes' focus and the soft keyboard are the game's
	/// again (InputMode).
	/// </summary>
	internal static class Interface
	{
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidShowKeyboard(int show);
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidKeyboardForced(int forced);

		private const int GlfwKeyEnter = 257, GlfwPress = 1, GlfwRelease = 0;
		private static readonly HashSet<string> EnterFields = new HashSet<string> { "chat_input" };

		/// <summary>The soft keyboard is up (asked for: a text field has the focus).</summary>
		public static bool KeyboardShown => keyboardShown;

		/// <summary>The soft keyboard's action key: typing is finished, or, in a chat's field, the Enter that sends.</summary>
		public static void EditorAction()
		{
			string name = GUI.GetNameOfFocusedControl();
			if (Touch.Verbose) Console.WriteLine("[mobile] the keyboard's Done (control " + GUIUtility.keyboardControl + ", " + name + ")");
			if (name != null && EnterFields.Contains(name))
			{
				glfwAndroidInjectKey(GlfwKeyEnter, GlfwPress, 0);
				glfwAndroidInjectKey(GlfwKeyEnter, GlfwRelease, 0);
				return;
			}
			GUIUtility.keyboardControl = 0;
		}

		private static readonly IDictionary ActiveTips = (IDictionary)AccessTools.Field(typeof(TooltipHandler), "activeTips").GetValue(null);
		private static readonly FieldInfo TipFrame = AccessTools.Field(typeof(TooltipHandler), "frame");
		private static readonly FieldInfo DraggingWindow = AccessTools.Field(typeof(GUI), "_draggingWindowId");
		private static readonly FieldInfo ButtonDown = AccessTools.Field(typeof(GUI), "_buttonDownActive");
		private static readonly FieldInfo CurrentScrollView = AccessTools.Field(typeof(SteamDeck), "currentScrollView");

		// While a hold on the interface lasts, and a frame or two after it, the left button's events are swallowed.
		private static int suppressClickUntilFrame = -1;
		// What a hold showed stays until the next touch.
		private static bool keepTips;
		private static bool keyboardShown, askKeyboardAgain;
		// The text field that took the focus from a press on it, and the focus as last seen.
		private static int tappedFocus, lastFocus;
		private static bool searchOnlyFromTap = true;

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
		// The scroll views being drawn in the current repaint (they nest): the number TapAssist gave each (-1: not noted).
		private static readonly List<int> drawn = new List<int>();

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SteamDeck), nameof(SteamDeck.HandleTouchScreenScrollViewScroll)), prefix: new HarmonyMethod(typeof(Interface), nameof(FingerScroll)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginScrollView), new[] { typeof(Rect), typeof(Vector2).MakeByRefType(), typeof(Rect), typeof(bool) }), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
			// The developer tools' windows (debug actions, the log, tweak values) have scroll views of their own: they call the
			// finger scrolling above but were not noted as lists, so a swipe dragged instead (a tester, 2026-10-24: "开发者选项有些
			// 在下面划不动"; he swiped at the scrollbar on the screen's right edge).
			harmony.Patch(AccessTools.Method(typeof(LudeonTK.DevGUI), nameof(LudeonTK.DevGUI.BeginScrollView)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			harmony.Patch(AccessTools.Method(typeof(LudeonTK.DevGUI), nameof(LudeonTK.DevGUI.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
			harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.OnGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			// The engine hands a mouse event to the window under the mouse before the game's OnGUI sees it.
			harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.DispatchInputToWindows)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisibleDraggable)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeDraggableButton)));
			harmony.Patch(AccessTools.Method(typeof(TooltipHandler), nameof(TooltipHandler.DoTooltipGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTooltips)));
			listTouch = Settings.Get("listTouch", 1) != 0;
			searchOnlyFromTap = Settings.Get("searchFocusOnTap", 1) != 0;
			harmony.Patch(AccessTools.Method(typeof(GUI), "TextFieldImpl"), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTextField)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterTextField)));
		}

		// ---- told by Touch ----

		/// <summary>A new touch begins: what the last hold showed goes away.</summary>
		public static void TouchBegan()
		{
			// The keyboard may have been dismissed by the system (Back) while the field kept the focus: a touch asks for it
			// again. (Forgetting that it was shown would also keep it up when the touch takes the focus away.)
			askKeyboardAgain = true;
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
			drawn.Clear();
			LetGoOfGivenSearchFocus();
			bool wanted = UnityStubs.Internal.TextFocus.CapturesText;
			bool again = askKeyboardAgain && wanted;
			askKeyboardAgain = false;
			if (wanted != keyboardShown || again)
			{
				keyboardShown = wanted;
				if (Touch.Verbose) Console.WriteLine("[mobile] soft keyboard " + (wanted ? "wanted" : "not wanted") + " (control " + GUIUtility.keyboardControl + ")");
				if (wanted)
				{
					KeyboardForced(!InputMode.Keyboard);
				}
				glfwAndroidShowKeyboard(wanted ? 1 : 0);
			}
		}

		private static bool forcedUnknown;

		/// <summary>Whether the soft keyboard is forced up for the field (no hardware keyboard), or left to the input method.</summary>
		private static void KeyboardForced(bool forced)
		{
			if (forcedUnknown)
			{
				return;
			}
			try
			{
				glfwAndroidKeyboardForced(forced ? 1 : 0);
			}
			catch (Exception e)
			{
				// An older APK: always forced, as before.
				forcedUnknown = true;
				Console.WriteLine("[mobile] soft keyboard: always forced up (" + e.GetType().Name + ")");
			}
		}

		/// <summary>
		/// A hardware keyboard came or went (InputMode): a soft keyboard that is up for a field is asked for again the new
		/// way (without one forced up, with one left to the input method, which usually puts it away).
		/// </summary>
		public static void KeyboardModeChanged(bool keyboard)
		{
			KeyboardForced(!keyboard);
			if (keyboardShown)
			{
				glfwAndroidShowKeyboard(0);
				keyboardShown = false;
			}
		}

		/// <summary>
		/// A second finger has landed: the window the first one began to drag stays where it is. (The engine moves a
		/// dragged window with the mouse on every pass until the button is up, and the mouse is about to jump to the
		/// middle of the two fingers: the window jumped with it.)
		/// </summary>
		public static void StopWindowDrag()
		{
			if (DraggingWindow != null && (int)DraggingWindow.GetValue(null) != -1)
			{
				DraggingWindow.SetValue(null, -1);
				if (Touch.Verbose) Console.WriteLine("[mobile] a second finger: the window being dragged stays");
			}
		}

		/// <summary>Told by Touch when a finger lands (window pixels, y down).</summary>
		public static void FingerLanded(Vector2 at)
		{
			if (GUIUtility.keyboardControl != 0 && TapAssist.Ready && !TapAssist.FieldAt(at))
			{
				if (Touch.Verbose) Console.WriteLine("[mobile] a finger away from the text field: its focus goes (" + GUI.GetNameOfFocusedControl() + ")");
				GUI.FocusControl(null);
			}
		}

		/// <summary>Prefix of the engine's TextFieldImpl: the focus before the field had its turn.</summary>
		public static void BeforeTextField(out int __state)
		{
			__state = GUIUtility.keyboardControl;
		}

		/// <summary>Postfix of the engine's TextFieldImpl: a field that took the focus while handling a press took it from a finger.</summary>
		public static void AfterTextField(int __state)
		{
			int focus = GUIUtility.keyboardControl;
			if (focus != __state && focus != 0 && OriginalEventUtility.EventType == EventType.MouseDown)
			{
				tappedFocus = focus;
			}
		}

		/// <summary>Once a frame, before the keyboard follows the focus: a search box or a save dialog's name that got the focus some other way than from a finger lets it go.</summary>
		private static void LetGoOfGivenSearchFocus()
		{
			int focus = GUIUtility.keyboardControl;
			if (focus == lastFocus)
			{
				return;
			}
			lastFocus = focus;
			if (Touch.Verbose) Console.WriteLine("[mobile] the keyboard focus is now " + focus + " (" + GUI.GetNameOfFocusedControl() + ", from a finger: " + (focus == tappedFocus) + ")");
			// With a hardware keyboard the focus stays where the game puts it, so that typing goes there at once.
			if (!searchOnlyFromTap || InputMode.Keyboard || focus == 0 || focus == tappedFocus)
			{
				return;
			}
			string name = GUI.GetNameOfFocusedControl();
			if (IsSearchBox(name) || name == SaveNameField)
			{
				GUI.FocusControl(null);
				lastFocus = 0;
				if (Touch.Verbose) Console.WriteLine("[mobile] the field " + name + " was given the focus without a finger on it: let go");
			}
		}

		/// <summary>The name field of the game's save dialogs (Dialog_FileList.DoTypeInField), which focus it when they open.</summary>
		private const string SaveNameField = "MapNameField";

		/// <summary>
		/// A search box by the name it was given: the game's QuickSearchWidget, and the mods' own (Dubs Mint Menus focuses
		/// "Search", "BuildSearch", "BenchSearch", "ReSearch" when its windows open), and filter boxes (the debug actions'
		/// "DebugFilter", focused when the window opens: the soft keyboard covered half of it).
		/// </summary>
		private static bool IsSearchBox(string name)
		{
			return name != null && (name.StartsWith("QuickSearchWidget_", StringComparison.Ordinal) || name.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("filter", StringComparison.OrdinalIgnoreCase) >= 0);
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
			if (current.rawType == EventType.Repaint)
			{
				drawn.Add(scrolls && listTouch ? TapAssist.NoteList(outRect, down, across) : -1);
			}
			if (current.rawType != EventType.MouseDown)
			{
				return;
			}
			// Only the list the touch began in (Touch) keeps the press: a list that something drawn after it covers there (the
			// resource readout under the inspect pane's tabs) took the tab's press (a tester, 2026-10-24: "日志" did nothing).
			open.Add(scrolls && current.type == EventType.MouseDown && current.button == 0 && FingerDown && Mouse.IsOver(outRect) && Touch.BeganInList(outRect));
		}

		/// <summary>
		/// Postfix of Widgets.EndScrollView: a press in a scrolling list that nothing in the list used (a button would
		/// have) is used now, so that whatever comes after the list (the window's own dragging) does not take it.
		/// </summary>
		public static void AfterScrollView()
		{
			Event current = Event.current;
			if (current != null && current.rawType == EventType.Repaint && drawn.Count > 0)
			{
				TapAssist.EndList(drawn[drawn.Count - 1]);
				drawn.RemoveAt(drawn.Count - 1);
			}
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
		/// was), except what a hold showed, which is kept alive here until the next touch. A real mouse (InputMode)
		/// shows the tooltips under it as on a PC, unless a finger moved the pointer last.
		/// </summary>
		public static void BeforeTooltips()
		{
			Event current = Event.current;
			if (current == null || current.type != EventType.Repaint || FingerDown || ActiveTips.Count == 0 || (InputMode.Mouse && !Touch.PointerFromFinger))
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
