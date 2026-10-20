using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// A wheel of keys for a finger: a round button ("按键") at the edge of the screen, shown while a game is played. A
	/// tap opens a ring of round keys around it (turned towards the middle of the screen, a second ring where one is not
	/// enough), another tap closes it. Each key is one of the game's key bindings (Options → Controls: pause, the speeds,
	/// rotating, drafting, the main tabs, the camera, and what mods add), shown by the key it is bound to: while the
	/// finger holds it, the engine sees that key held down (a real key event through the Android GLFW), so the game and
	/// mods do on it what they do on a PC keyboard. Four keys of the phone's own (the user's, 2026-10-20) switch on with a
	/// tap and stay on until tapped again: Shift (ShiftKey: the left Shift held down), and the mouse buttons left, middle
	/// and right (one of the three is on: which button a finger on the screen is, see Touch; the wheel itself is always
	/// tapped with the left). While the ring is closed the button shows what is on. The last key opens the setup: keys
	/// added from the full list, taken out, moved; the defaults back; the button back to its place. The button is moved by
	/// dragging it.
	/// mobile.txt: keyWheel=0 leaves it out (Options → Interface has the switch); keyWheelKeys=defName,@Shift,...;
	/// keyWheelAt=X,Y (the button's centre, interface units).
	/// </summary>
	internal static class KeyWheel
	{
		[DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);

		private const int Press = 1, Release = 0;
		private const int HubId = 73195801, SlotIdBase = 73195810;
		private const float HubSize = 54f, SlotSize = 64f, Spacing = 8f;
		private const int SetupSlot = -3, NoTouch = -2, HubTouch = -1;

		// The phone's own keys (their names in keyWheelKeys) and the mouse buttons (GLFW's numbers, as Touch injects them).
		internal const string ShiftName = "@Shift", MouseLeftName = "@MouseLeft", MouseMiddleName = "@MouseMiddle", MouseRightName = "@MouseRight";
		internal static readonly string[] Specials = { ShiftName, MouseLeftName, MouseRightName, MouseMiddleName };
		// The number keys 0 to 9 themselves (the user's, 2026-10-20), whatever is bound to them: held while the finger holds them.
		internal static readonly string[] Digits = Enumerable.Range(0, 10).Select(n => "@Key" + n).ToArray();
		internal const int MouseLeft = 0, MouseRight = 1, MouseMiddle = 2;

		public static readonly string[] Defaults = { "TogglePause", "TimeSpeed_Normal", "TimeSpeed_Fast", "TimeSpeed_Superfast", ShiftName, MouseLeftName, MouseRightName, MouseMiddleName, "Designator_RotateLeft", "Designator_RotateRight", "Command_ColonistDraft", "NextColonist" };

		private static bool enabled, expanded;
		private static Vector2 hubCenter = new Vector2(-1f, -1f);
		private static List<string> names = new List<string>();
		private static List<WheelKey> keys;
		private static readonly List<Vector2> slotCenters = new List<Vector2>();
		private static int drawnFrame = -100;
		private static int mouseButton = MouseLeft;

		private static int touching = NoTouch;
		private static Vector2 touchStart, grabOffset;
		private static bool dragged;
		private static int heldGlfwKey = -1;
		private static Texture2D disc;

		public static bool Enabled => enabled;

		public static void Start()
		{
			enabled = Settings.Get("keyWheel", true);
			string list = Settings.Get("keyWheelKeys", null);
			names = list != null ? list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToList() : Defaults.ToList();
			if (list != null && Settings.Get("keyWheelVersion", 1) < 2 && !names.Any(n => n.StartsWith("@")))
			{
				// Keys chosen before the wheel had keys of its own (the Shift button was apart then): those go in, once.
				names.InsertRange(Math.Min(4, names.Count), Specials);
				Settings.Set("keyWheelKeys", string.Join(",", names));
			}
			if (Settings.Get("keyWheelVersion", 1) < 2)
			{
				Settings.Set("keyWheelVersion", "2");
			}
			string at = Settings.Get("keyWheelAt", null);
			if (at != null)
			{
				string[] parts = at.Split(',');
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				if (parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float x) && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float y))
				{
					hubCenter = new Vector2(x, y);
				}
			}
			Console.WriteLine("[mobile] key wheel " + (enabled ? "on" : "off") + ", " + names.Count + " keys");
		}

		// ---- the keys ----

		internal static List<WheelKey> Keys
		{
			get
			{
				if (keys == null)
				{
					keys = names.Select(WheelKey.Named).Where(k => k != null).ToList();
				}
				return keys;
			}
		}

		internal static void SetKeys(List<WheelKey> wheelKeys)
		{
			keys = wheelKeys.ToList();
			names = keys.Select(k => k.Name).ToList();
			Settings.Set("keyWheelKeys", string.Join(",", names));
		}

		/// <summary>Which mouse button a finger on the screen at this point (window pixels) is: the one switched on in the wheel, except on the wheel itself.</summary>
		internal static int TapButton(Vector2 pixels)
		{
			if (mouseButton == MouseLeft || !enabled || Current.ProgramState != ProgramState.Playing || Time.frameCount - drawnFrame > 3)
			{
				return MouseLeft;
			}
			return OnWheel(pixels / Prefs.UIScale) ? MouseLeft : mouseButton;
		}

		private static bool OnWheel(Vector2 ui)
		{
			if ((ui - hubCenter).magnitude <= HubSize / 2f + 4f)
			{
				return true;
			}
			if (expanded)
			{
				foreach (Vector2 c in slotCenters)
				{
					if ((ui - c).magnitude <= SlotSize / 2f + 2f)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool IsOn(WheelKey key)
		{
			switch (key.Name)
			{
				case ShiftName: return ShiftKey.On;
				case MouseLeftName: return mouseButton == MouseLeft;
				case MouseRightName: return mouseButton == MouseRight;
				case MouseMiddleName: return mouseButton == MouseMiddle;
				default: return false;
			}
		}

		/// <summary>A tap on one of the phone's own keys: Shift on or off; a mouse button on (tapped again: back to the left one).</summary>
		private static void Toggle(WheelKey key)
		{
			bool on;
			if (key.Name == ShiftName)
			{
				on = !ShiftKey.On;
				ShiftKey.Set(on);
			}
			else
			{
				int button = key.Name == MouseRightName ? MouseRight : key.Name == MouseMiddleName ? MouseMiddle : MouseLeft;
				mouseButton = mouseButton == button ? MouseLeft : button;
				on = mouseButton == button;
				Console.WriteLine("[mobile] key wheel: a tap is the " + (mouseButton == MouseRight ? "right" : mouseButton == MouseMiddle ? "middle" : "left") + " mouse button");
			}
			(on ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
		}

		internal static void SetEnabled(bool value)
		{
			if (enabled == value)
			{
				return;
			}
			enabled = value;
			Settings.Set("keyWheel", value ? "1" : "0");
			if (!value)
			{
				ReleaseKey();
				expanded = false;
			}
		}

		internal static void ResetPlace()
		{
			hubCenter = DefaultCenter();
			Settings.Set("keyWheelAt", "");
		}

		private static Vector2 DefaultCenter()
		{
			// The left edge, a little below the middle.
			return new Vector2(14f + HubSize / 2f, Mathf.Round(UI.screenHeight * 0.55f));
		}

		/// <summary>What a key shows on the wheel: the key it is bound to ("" when none), or the phone key's name.</summary>
		internal static string KeyText(WheelKey key)
		{
			if (key.Digit >= 0)
			{
				return key.Digit.ToString();
			}
			if (key.Def == null)
			{
				switch (key.Name)
				{
					case ShiftName: return "Shift";
					case MouseLeftName: return Texts.Chinese ? "左键" : "LMB";
					case MouseRightName: return Texts.Chinese ? "右键" : "RMB";
					default: return Texts.Chinese ? "中键" : "MMB";
				}
			}
			KeyCode bound = BoundKey(key.Def);
			return bound == KeyCode.None ? "" : bound.ToStringReadable();
		}

		/// <summary>The key a binding answers to now (the player's first choice, else the second); None when it has none a finger can give.</summary>
		internal static KeyCode BoundKey(KeyBindingDef def)
		{
			KeyCode key = KeyCode.None;
			try
			{
				key = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.A);
				if (GlfwKey(key) < 0)
				{
					key = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.B);
				}
			}
			catch (Exception)
			{
			}
			return GlfwKey(key) < 0 ? KeyCode.None : key;
		}

		/// <summary>The GLFW key code of a Unity key code (what the engine turns back into the same KeyCode); -1 for none (mouse buttons, keys GLFW does not have).</summary>
		internal static int GlfwKey(KeyCode key)
		{
			int k = (int)key;
			if (key >= KeyCode.A && key <= KeyCode.Z) return 65 + (k - (int)KeyCode.A);
			if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 48 + (k - (int)KeyCode.Alpha0);
			if (key >= KeyCode.F1 && key <= KeyCode.F15) return 290 + (k - (int)KeyCode.F1);
			if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return 320 + (k - (int)KeyCode.Keypad0);
			switch (key)
			{
				case KeyCode.Space: return 32;
				case KeyCode.Quote: return 39;
				case KeyCode.Comma: return 44;
				case KeyCode.Minus: return 45;
				case KeyCode.Period: return 46;
				case KeyCode.Slash: return 47;
				case KeyCode.Semicolon: return 59;
				case KeyCode.Equals: return 61;
				case KeyCode.LeftBracket: return 91;
				case KeyCode.Backslash: return 92;
				case KeyCode.RightBracket: return 93;
				case KeyCode.BackQuote: return 96;
				case KeyCode.Escape: return 256;
				case KeyCode.Return: return 257;
				case KeyCode.Tab: return 258;
				case KeyCode.Backspace: return 259;
				case KeyCode.Insert: return 260;
				case KeyCode.Delete: return 261;
				case KeyCode.RightArrow: return 262;
				case KeyCode.LeftArrow: return 263;
				case KeyCode.DownArrow: return 264;
				case KeyCode.UpArrow: return 265;
				case KeyCode.PageUp: return 266;
				case KeyCode.PageDown: return 267;
				case KeyCode.Home: return 268;
				case KeyCode.End: return 269;
				case KeyCode.KeypadPeriod: return 330;
				case KeyCode.KeypadDivide: return 331;
				case KeyCode.KeypadMultiply: return 332;
				case KeyCode.KeypadMinus: return 333;
				case KeyCode.KeypadPlus: return 334;
				case KeyCode.KeypadEnter: return 335;
				case KeyCode.KeypadEquals: return 336;
				case KeyCode.LeftShift: return 340;
				case KeyCode.LeftControl: return 341;
				case KeyCode.LeftAlt: return 342;
				case KeyCode.RightShift: return 344;
				case KeyCode.RightControl: return 345;
				case KeyCode.RightAlt: return 346;
				default: return -1;
			}
		}

		private static void HoldKey(KeyBindingDef def)
		{
			ReleaseKey();
			int glfw = GlfwKey(BoundKey(def));
			if (glfw < 0)
			{
				Messages.Message(Texts.Chinese ? "“" + def.LabelCap + "”没有绑定可以用的键盘按键（在 选项 → 控制 里给它绑一个）。" : "\"" + def.LabelCap + "\" has no keyboard key bound to it (bind one in Options → Controls).", MessageTypeDefOf.RejectInput, false);
				return;
			}
			HoldGlfwKey(glfw);
		}

		private static void HoldGlfwKey(int glfw)
		{
			ReleaseKey();
			glfwAndroidInjectKey(glfw, Press, 0);
			heldGlfwKey = glfw;
		}

		private static void ReleaseKey()
		{
			if (heldGlfwKey >= 0)
			{
				glfwAndroidInjectKey(heldGlfwKey, Release, 0);
				heldGlfwKey = -1;
			}
		}

		// ---- every frame ----

		/// <summary>Once a frame: the finger that holds a key or drags the button followed; everything let go when no game is played.</summary>
		public static void Update()
		{
			if (mouseButton != MouseLeft && (!enabled || Current.ProgramState != ProgramState.Playing))
			{
				mouseButton = MouseLeft;
			}
			if (touching == NoTouch)
			{
				if (heldGlfwKey >= 0)
				{
					ReleaseKey();
				}
				return;
			}
			bool down = Input.GetMouseButton(0);
			if (touching == HubTouch && down)
			{
				Vector2 at = UI.MousePositionOnUIInverted;
				if (!dragged && (at - touchStart).magnitude > 14f)
				{
					dragged = true;
				}
				if (dragged)
				{
					hubCenter = Clamp(at + grabOffset, HubSize / 2f);
				}
				return;
			}
			if (down && Current.ProgramState == ProgramState.Playing)
			{
				return;
			}
			// The finger lifted.
			int was = touching;
			touching = NoTouch;
			ReleaseKey();
			if (was == HubTouch)
			{
				if (dragged)
				{
					var inv = System.Globalization.CultureInfo.InvariantCulture;
					Settings.Set("keyWheelAt", hubCenter.x.ToString("F0", inv) + "," + hubCenter.y.ToString("F0", inv));
				}
				else
				{
					expanded = !expanded;
					(expanded ? SoundDefOf.Tick_High : SoundDefOf.Tick_Low).PlayOneShotOnCamera();
				}
			}
			else if (was == SetupSlot && Current.ProgramState == ProgramState.Playing)
			{
				// The ring closes: it would lie over the setup window (it is drawn above every window).
				expanded = false;
				Find.WindowStack.Add(new Dialog_KeyWheel());
			}
		}

		private static Vector2 Clamp(Vector2 center, float radius)
		{
			return new Vector2(Mathf.Clamp(center.x, radius + 2f, UI.screenWidth - radius - 2f), Mathf.Clamp(center.y, radius + 2f, UI.screenHeight - radius - 2f));
		}

		/// <summary>From the end of the play screen's interface, every frame: the button's window, and the keys' while it is open.</summary>
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
			if (hubCenter.x < 0f)
			{
				hubCenter = DefaultCenter();
			}
			hubCenter = Clamp(hubCenter, HubSize / 2f);
			drawnFrame = Time.frameCount;
			if (expanded)
			{
				// One window for each key: the gaps between them stay the map's.
				List<WheelKey> defs = Keys;
				Layout(defs.Count + 1);
				for (int i = 0; i < slotCenters.Count; i++)
				{
					int index = i < defs.Count ? i : SetupSlot;
					Vector2 c = slotCenters[i];
					var rect = new Rect(c.x - SlotSize / 2f, c.y - SlotSize / 2f, SlotSize, SlotSize);
					Find.WindowStack.ImmediateWindow(SlotIdBase + i, rect, WindowLayer.Super, () => DrawSlot(index), doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
				}
			}
			var hubRect = new Rect(hubCenter.x - HubSize / 2f, hubCenter.y - HubSize / 2f, HubSize, HubSize);
			Find.WindowStack.ImmediateWindow(HubId, hubRect, WindowLayer.Super, DrawHub, doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
		}

		/// <summary>
		/// The keys' centres: on rings around the button, the first one beside the button and the rest alternately to
		/// either side of the direction of the middle of the screen; places off the screen are passed over, and a ring
		/// that is full gives way to a wider one.
		/// </summary>
		private static void Layout(int count)
		{
			slotCenters.Clear();
			var middle = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
			Vector2 towards = middle - hubCenter;
			float baseAngle = Mathf.Atan2(towards.y, towards.x);
			float margin = SlotSize / 2f + 4f;
			for (int ring = 0; ring < 5 && slotCenters.Count < count; ring++)
			{
				float radius = HubSize / 2f + SlotSize / 2f + 10f + ring * (SlotSize + Spacing);
				int places = Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * radius / (SlotSize + Spacing)));
				float step = 2f * Mathf.PI / places;
				for (int k = 0; k < places && slotCenters.Count < count; k++)
				{
					int side = (k % 2 == 1) ? (k + 1) / 2 : -(k / 2);
					float angle = baseAngle + side * step;
					Vector2 p = hubCenter + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
					if (p.x < margin || p.y < margin || p.x > UI.screenWidth - margin || p.y > UI.screenHeight - margin)
					{
						continue;
					}
					slotCenters.Add(p);
				}
			}
		}

		// ---- drawing ----

		private static Texture2D Disc
		{
			get
			{
				if (disc == null)
				{
					const int size = 96;
					disc = new Texture2D(size, size, TextureFormat.RGBA32, false);
					var pixels = new Color32[size * size];
					float r = size / 2f - 1f;
					for (int y = 0; y < size; y++)
					{
						for (int x = 0; x < size; x++)
						{
							float d = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).magnitude;
							float alpha = Mathf.Clamp01(r - d + 0.5f);
							// A lighter rim, three pixels wide.
							byte shade = (byte)(d > r - 3f ? 255 : 200);
							pixels[y * size + x] = new Color32(shade, shade, shade, (byte)(alpha * 255f));
						}
					}
					disc.SetPixels32(pixels);
					disc.Apply();
				}
				return disc;
			}
		}

		private static void DrawRound(Rect rect, Color fill, string label, GameFont font)
		{
			if (Event.current.type != EventType.Repaint)
			{
				return;
			}
			Color colour = GUI.color;
			TextAnchor anchor = Text.Anchor;
			GameFont oldFont = Text.Font;
			bool wrap = Text.WordWrap;
			GUI.color = fill;
			GUI.DrawTexture(rect, Disc);
			GUI.color = Color.white;
			Text.Font = font;
			Text.Anchor = TextAnchor.MiddleCenter;
			Text.WordWrap = true;
			Widgets.Label(rect.ContractedBy(rect.width * 0.12f), label);
			Text.WordWrap = wrap;
			Text.Anchor = anchor;
			Text.Font = oldFont;
			GUI.color = colour;
		}

		private static void DrawHub()
		{
			var rect = new Rect(0f, 0f, HubSize, HubSize);
			bool held = touching == HubTouch;
			// Closed, it says what is switched on: Shift, a mouse button other than the left.
			string status = "";
			if (!expanded)
			{
				if (ShiftKey.On)
				{
					status = "Shift";
				}
				if (mouseButton != MouseLeft)
				{
					status += (status.Length > 0 ? "\n" : "") + KeyText(WheelKey.Named(mouseButton == MouseRight ? MouseRightName : MouseMiddleName));
				}
			}
			Color fill = expanded ? new Color(0.35f, 0.45f, 0.6f, held ? 1f : 0.95f) : status.Length > 0 ? new Color(0.85f, 0.65f, 0.2f, held ? 1f : 0.95f) : new Color(0.25f, 0.27f, 0.3f, held ? 0.95f : 0.75f);
			DrawRound(rect, fill, expanded ? (Texts.Chinese ? "收起" : "Close") : status.Length > 0 ? status : (Texts.Chinese ? "按键" : "Keys"), status.Length > 0 ? GameFont.Tiny : GameFont.Small);
			if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(rect))
			{
				touching = HubTouch;
				touchStart = UI.MousePositionOnUIInverted;
				grabOffset = hubCenter - touchStart;
				dragged = false;
				Event.current.Use();
			}
		}

		private static void DrawSlot(int index)
		{
			var rect = new Rect(0f, 0f, SlotSize, SlotSize);
			bool held = touching == index;
			string label;
			Color fill;
			if (index == SetupSlot)
			{
				label = Texts.Chinese ? "设置" : "Setup";
				fill = new Color(0.3f, 0.3f, 0.32f, held ? 1f : 0.85f);
			}
			else
			{
				WheelKey key = Keys[index];
				label = KeyText(key);
				bool on = key.Def == null && key.Digit < 0 ? IsOn(key) : held;
				fill = label.Length == 0 ? new Color(0.3f, 0.2f, 0.2f, 0.7f) : on ? new Color(0.85f, 0.65f, 0.2f, 1f) : new Color(0.18f, 0.2f, 0.22f, 0.88f);
				if (label.Length == 0)
				{
					label = "—";
				}
			}
			DrawRound(rect, fill, label, label.Length <= 5 ? GameFont.Small : GameFont.Tiny);
			if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(rect))
			{
				touching = index;
				if (index >= 0)
				{
					WheelKey key = Keys[index];
					if (key.Def != null)
					{
						HoldKey(key.Def);
					}
					else if (key.Digit >= 0)
					{
						HoldGlfwKey(48 + key.Digit); // GLFW_KEY_0 ... GLFW_KEY_9
					}
					else
					{
						Toggle(key);
					}
				}
				Event.current.Use();
			}
		}

		// ---- Options → Interface ----

		public static void OptionRow(Listing_Standard listing)
		{
			bool check = enabled;
			listing.CheckboxLabeled(Texts.Chinese ? "显示自定义按键盘" : "Show the key wheel", ref check, Texts.Chinese
				? "游戏中屏幕边上的圆形“按键”按钮：点一下展开一圈按键，每个按键上写着它对应的键盘按键（按住就是按住那个键），再点一下收起；拖动可以移动位置。Shift 和鼠标左键、中键、右键是开关，点一下打开、再点一下关闭：开着右键时，在屏幕上点一下就是鼠标右键点击。展开后最后一个按键“设置”可以增删按键。"
				: "The round \"Keys\" button at the edge of the screen in a game: a tap opens a ring of keys, each showing the keyboard key it stands for (holding it holds the key); another tap closes it; drag it to move it. Shift and the left, middle and right mouse buttons are switches, on with a tap and off with another: with the right one on, a tap on the screen is a right click. The last key, \"Setup\", adds and removes keys.", 30f, 0.6f);
			SetEnabled(check);
		}
	}

	/// <summary>The setup of the key wheel: its keys (moved, taken out), the game's key bindings to add from, the defaults, the button's place.</summary>
	internal sealed class Dialog_KeyWheel : Window
	{
		private Vector2 chosenScroll, allScroll;
		private string search = "";

		public Dialog_KeyWheel()
		{
			doCloseX = true;
			doCloseButton = false;
			closeOnClickedOutside = true;
			absorbInputAroundWindow = true;
			forcePause = true;
		}

		public override Vector2 InitialSize => new Vector2(900f, 620f);

		public override void DoWindowContents(Rect inRect)
		{
			bool chinese = Texts.Chinese;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 36f), chinese ? "自定义按键盘" : "Key wheel");
			Text.Font = GameFont.Small;
			float top = inRect.y + 42f, bottom = inRect.yMax - 44f;
			float half = (inRect.width - 16f) / 2f;
			var left = new Rect(inRect.x, top, half, bottom - top);
			var right = new Rect(inRect.x + half + 16f, top, half, bottom - top);

			// The wheel's keys.
			List<WheelKey> keys = KeyWheel.Keys.ToList();
			Widgets.Label(new Rect(left.x, left.y, left.width, 26f), chinese ? "按键盘上的按键（按顺序从圆钮旁往两边排）" : "The wheel's keys (in order, from beside the button outwards)");
			var listRect = new Rect(left.x, left.y + 28f, left.width, left.height - 28f);
			float rowH = 38f;
			var view = new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, keys.Count * rowH));
			Widgets.DrawMenuSection(listRect);
			Widgets.BeginScrollView(listRect, ref chosenScroll, view);
			bool changed = false;
			for (int i = 0; i < keys.Count; i++)
			{
				var row = new Rect(0f, i * rowH, view.width, rowH);
				if (i % 2 == 1)
				{
					Widgets.DrawLightHighlight(row);
				}
				Widgets.Label(new Rect(row.x + 6f, row.y, row.width - 140f, rowH), keys[i].Label + "  <color=#999999>" + Bound(keys[i], chinese) + "</color>");
				float x = row.xMax - 132f;
				if (i > 0 && Widgets.ButtonText(new Rect(x, row.y + 4f, 40f, rowH - 8f), "↑"))
				{
					WheelKey d = keys[i];
					keys[i] = keys[i - 1];
					keys[i - 1] = d;
					changed = true;
				}
				if (i < keys.Count - 1 && Widgets.ButtonText(new Rect(x + 44f, row.y + 4f, 40f, rowH - 8f), "↓"))
				{
					WheelKey d = keys[i];
					keys[i] = keys[i + 1];
					keys[i + 1] = d;
					changed = true;
				}
				if (Widgets.ButtonText(new Rect(x + 88f, row.y + 4f, 40f, rowH - 8f), "×"))
				{
					keys.RemoveAt(i);
					changed = true;
					break;
				}
			}
			Widgets.EndScrollView();

			// The phone's own keys, then all of the game's key bindings, by category.
			Widgets.Label(new Rect(right.x, right.y, 120f, 26f), chinese ? "点一项添加：" : "Tap one to add:");
			search = Widgets.TextField(new Rect(right.x + 124f, right.y, right.width - 124f, 26f), search);
			var allRect = new Rect(right.x, right.y + 28f, right.width, right.height - 28f);
			string needle = search.Trim();
			var chosen = new HashSet<string>(keys.Select(k => k.Name));
			List<WheelKey> all = KeyWheel.Specials.Concat(KeyWheel.Digits).Select(WheelKey.Named)
				.Concat(DefDatabase<KeyBindingDef>.AllDefsListForReading.OrderBy(d => d.category != null ? d.category.index : 0).Select(d => new WheelKey(d.defName, d)))
				.Where(k => !chosen.Contains(k.Name) && (needle.Length == 0 || k.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || k.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
				.ToList();
			var allView = new Rect(0f, 0f, allRect.width - 16f, Math.Max(allRect.height, all.Count * 32f + 200f));
			Widgets.DrawMenuSection(allRect);
			Widgets.BeginScrollView(allRect, ref allScroll, allView);
			float y = 0f;
			string lastCategory = null;
			foreach (WheelKey key in all)
			{
				string category = key.Digit >= 0 ? (chinese ? "数字键" : "Number keys") : key.Def == null ? (chinese ? "手机按键（开关）" : "Phone keys (switches)") : key.Def.category != null ? key.Def.category.LabelCap.ToString() : "";
				if (category != lastCategory)
				{
					lastCategory = category;
					GUI.color = new Color(1f, 0.85f, 0.5f);
					Widgets.Label(new Rect(6f, y + 4f, allView.width, 26f), category);
					GUI.color = Color.white;
					y += 30f;
				}
				var row = new Rect(0f, y, allView.width, 32f);
				Widgets.Label(new Rect(row.x + 14f, row.y + 3f, row.width - 20f, 28f), key.Label + "  <color=#999999>" + Bound(key, chinese) + "</color>");
				if (Widgets.ButtonInvisible(row))
				{
					keys.Add(key);
					changed = true;
				}
				y += 32f;
			}
			Widgets.EndScrollView();

			if (changed)
			{
				KeyWheel.SetKeys(keys);
			}

			// The buttons at the bottom.
			float bw = 160f;
			if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 38f, bw, 36f), chinese ? "恢复默认按键" : "Default keys"))
			{
				KeyWheel.SetKeys(KeyWheel.Defaults.Select(WheelKey.Named).Where(k => k != null).ToList());
			}
			if (Widgets.ButtonText(new Rect(inRect.x + bw + 10f, inRect.yMax - 38f, bw, 36f), chinese ? "圆钮放回原处" : "Button back"))
			{
				KeyWheel.ResetPlace();
			}
			if (Widgets.ButtonText(new Rect(inRect.xMax - bw, inRect.yMax - 38f, bw, 36f), chinese ? "完成" : "Done"))
			{
				Close();
			}
		}

		/// <summary>The grey text after a key's name: the key it is bound to, or what one of the phone's own does.</summary>
		private static string Bound(WheelKey key, bool chinese)
		{
			if (key.Digit >= 0)
			{
				return chinese ? "按住就是按住键盘上的 " + key.Digit : "held: the key " + key.Digit + " held";
			}
			if (key.Def == null)
			{
				return key.Name == KeyWheel.ShiftName ? (chinese ? "开关，开着时一直按住" : "switch: held while on") : (chinese ? "开关，点屏幕即为此键" : "switch: taps are this button");
			}
			string text = KeyWheel.KeyText(key);
			return text.Length == 0 ? (chinese ? "未绑定" : "unbound") : text;
		}
	}

	/// <summary>A key of the wheel: one of the game's key bindings, or one of the phone's own (Def null; KeyWheel.Specials).</summary>
	internal sealed class WheelKey
	{
		public readonly string Name;
		public readonly KeyBindingDef Def;

		public WheelKey(string name, KeyBindingDef def)
		{
			Name = name;
			Def = def;
		}

		/// <summary>The key of that name (a def's name, one of KeyWheel.Specials or Digits); null when there is none (a mod's key, the mod gone).</summary>
		public static WheelKey Named(string name)
		{
			if (KeyWheel.Specials.Contains(name) || KeyWheel.Digits.Contains(name))
			{
				return new WheelKey(name, null);
			}
			KeyBindingDef def = DefDatabase<KeyBindingDef>.GetNamedSilentFail(name);
			return def != null ? new WheelKey(name, def) : null;
		}

		/// <summary>0 to 9 for a number key of KeyWheel.Digits, else -1.</summary>
		public int Digit => Def == null && Name.Length == 5 && Name.StartsWith("@Key", StringComparison.Ordinal) && char.IsDigit(Name[4]) ? Name[4] - '0' : -1;

		public string Label
		{
			get
			{
				if (Def != null)
				{
					return Def.LabelCap;
				}
				bool chinese = Texts.Chinese;
				if (Digit >= 0)
				{
					return (chinese ? "数字键 " : "Number key ") + Digit;
				}
				switch (Name)
				{
					case KeyWheel.ShiftName: return "Shift";
					case KeyWheel.MouseLeftName: return chinese ? "鼠标左键" : "Left mouse button";
					case KeyWheel.MouseRightName: return chinese ? "鼠标右键" : "Right mouse button";
					default: return chinese ? "鼠标中键" : "Middle mouse button";
				}
			}
		}
	}
}
