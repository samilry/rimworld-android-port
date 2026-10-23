using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// A mouse and a keyboard, or fingers (the user, 2026-10-23: a player with a mouse found the touch layer's ways; "在接入
	/// 键鼠时，自动改为原版键鼠操作逻辑，去掉键鼠改为当前的触屏逻辑"). A real mouse always reached the game as a mouse (the
	/// Android GLFW reports its pointer, buttons and wheel), but what the port changes in the game for a finger's sake
	/// applied to it as well. The phone's Java side keeps the list of external mice and keyboards (MainActivity.inputDevices,
	/// an InputDeviceListener: plugged in, paired, gone); this class asks for it about once a second.
	///
	/// With a mouse connected the game's own pointer handling comes back:
	///   tooltips show under the pointer (Interface.BeforeTooltips took every tooltip away while no finger was down);
	///   a building, a drag and a landing spot are placed by the click, without the confirm buttons (Settings.PlaceConfirm);
	///   float menus and thing filters have the game's row spacing (TouchSizes).
	/// With a keyboard connected:
	///   a text field does not force the soft keyboard up (the input method shows it if the player wants it with a
	///   hardware keyboard), and search boxes keep the focus the game gives them when their window opens (Interface);
	///   the key wheel, the typing bar and the keys to tap in the key binding dialog are left out (KeyWheel, TypingBar,
	///   BindKeys).
	/// Fingers keep working meanwhile (a tap, a drag of the map, a pinch, a list). A double click of the mouse is the
	/// engine's in any case (DoubleTap widens it for fingers only). What belongs to the screen stays: the interface scale,
	/// windows and grids fitted to it, the camera's hole, the round corners, the research tree's vertical scrolling, and
	/// the larger time buttons (TimeControls.TimeButSize is read-only data, set once at the start: code compiled after
	/// that may hold it as a constant).
	///
	/// Options → Interface "操作方式": automatic (the default), touch, or mouse and keyboard (mobile.txt inputMode=auto,
	/// touch, mouse).
	/// </summary>
	internal static class InputMode
	{
		[DllImport("glfw")] private static extern int glfwAndroidInputDevices(byte[] text, int size);

		private const string Auto = "auto", TouchOnly = "touch", MouseAndKeys = "mouse";

		private static string setting = Normalized(Settings.Get("inputMode", Auto));
		private static bool mouseConnected, keyboardConnected, unavailable, announced;
		private static string devices = "";
		private static readonly byte[] buffer = new byte[1024];
		private static int nextPoll;
		private static bool lastMouse, lastKeyboard;

		/// <summary>The game's own mouse handling (a mouse is connected, or chosen).</summary>
		public static bool Mouse => setting == MouseAndKeys || (setting == Auto && mouseConnected);

		/// <summary>The game's own keyboard handling (a keyboard is connected, or chosen).</summary>
		public static bool Keyboard => setting == MouseAndKeys || (setting == Auto && keyboardConnected);

		private static string Normalized(string value)
		{
			return value == TouchOnly || value == MouseAndKeys ? value : Auto;
		}

		/// <summary>Every frame, from Touch, before anything else of the touch layer: the devices, once a second.</summary>
		public static void Update()
		{
			if (unavailable || Time.frameCount < nextPoll)
			{
				return;
			}
			nextPoll = Time.frameCount + 60;
			string text;
			try
			{
				int length = glfwAndroidInputDevices(buffer, buffer.Length);
				text = length > 0 ? Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length)) : "";
			}
			catch (Exception e)
			{
				// An older APK (its GLFW has no such function): fingers, as before.
				unavailable = true;
				Console.WriteLine("[mobile] input devices: not known (" + e.GetType().Name + ")");
				Apply(false);
				return;
			}
			int bar = text.IndexOf('|');
			int bits = bar > 0 && int.TryParse(text.Substring(0, bar), out int b) ? b : 0;
			mouseConnected = (bits & 1) != 0;
			keyboardConnected = (bits & 2) != 0;
			devices = bar >= 0 ? text.Substring(bar + 1) : "";
			Apply(true);
		}

		/// <summary>After the devices or the choice changed: the parts that hold a state of their own are told.</summary>
		private static void Apply(bool tell)
		{
			bool mouse = Mouse, keyboard = Keyboard;
			if (announced && mouse == lastMouse && keyboard == lastKeyboard)
			{
				return;
			}
			bool first = !announced;
			announced = true;
			bool wasMouse = lastMouse;
			lastMouse = mouse;
			lastKeyboard = keyboard;
			Console.WriteLine("[mobile] input: " + (mouse ? "the game's mouse handling" : "touch") + ", " + (keyboard ? "the game's keyboard handling" : "touch keyboard") + " (" + setting + "; devices: " + (devices.Length > 0 ? devices : "none") + ")");
			Interface.KeyboardModeChanged(keyboard);
			if (tell && !first && setting == Auto && mouse != wasMouse)
			{
				Tell(mouse);
			}
		}

		private static void Tell(bool mouse)
		{
			try
			{
				if (Current.ProgramState == ProgramState.Playing || Current.ProgramState == ProgramState.Entry)
				{
					Messages.Message(Texts.Chinese
						? (mouse ? "接入了鼠标：改用电脑版的键鼠操作（设置 → 界面 →“操作方式”可以改）" : "鼠标已断开：改回触屏操作")
						: (mouse ? "A mouse is connected: the PC's mouse and keyboard handling (Options → Interface, \"Input\")" : "The mouse is gone: touch handling again"), MessageTypeDefOf.SilentInput, false);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] input: no message: " + e.Message);
			}
		}

		private static string Name(string value)
		{
			switch (value)
			{
				case TouchOnly: return Texts.Chinese ? "触屏" : "Touch";
				case MouseAndKeys: return Texts.Chinese ? "键鼠" : "Mouse and keyboard";
				default: return Texts.Chinese ? "自动" : "Automatic";
			}
		}

		private static string Now()
		{
			bool mouse = Mouse, keyboard = Keyboard;
			if (Texts.Chinese)
			{
				return mouse && keyboard ? "键鼠" : mouse ? "鼠标" : keyboard ? "键盘" : "触屏";
			}
			return mouse && keyboard ? "mouse and keyboard" : mouse ? "mouse" : keyboard ? "keyboard" : "touch";
		}

		/// <summary>The row on the interface page of the options (before the key wheel's).</summary>
		public static void OptionRow(Listing_Standard listing)
		{
			string shown = setting == Auto ? Name(Auto) + (Texts.Chinese ? "（现在：" + Now() + "）" : " (now: " + Now() + ")") : Name(setting);
			if (listing.ButtonTextLabeledPct(Texts.Chinese ? "操作方式" : "Input", shown, 0.6f, TextAnchor.MiddleLeft, null, Texts.Chinese
				? "自动：接入鼠标时用电脑版的鼠标操作（悬停显示提示、点击直接放置建筑和选降落点、不出确定按钮、菜单行距同电脑版），接入键盘时用电脑版的键盘操作（输入框不强制弹出屏幕键盘、搜索框打开就能直接打字、隐藏自定义按键盘）；拔掉后自动改回触屏操作。手指照样可以点、拖地图、双指缩放。\n触屏：始终按触屏操作。\n键鼠：始终按键鼠操作。" + (devices.Length > 0 ? "\n\n现在接着的：" + devices : "\n\n现在没有接鼠标或键盘。")
				: "Automatic: with a mouse connected the PC's mouse handling (tooltips under the pointer, a click places a building or a landing spot at once, no confirm buttons, the PC's menu spacing); with a keyboard connected the PC's keyboard handling (no forced soft keyboard, search boxes take typing at once, no key wheel); back to touch when they are gone. Fingers still tap, drag the map and pinch.\nTouch: always touch.\nMouse and keyboard: always the PC's." + (devices.Length > 0 ? "\n\nConnected now: " + devices : "\n\nNo mouse or keyboard is connected now.")))
			{
				var choices = new List<FloatMenuOption>();
				foreach (string value in new[] { Auto, TouchOnly, MouseAndKeys })
				{
					string chosen = value;
					choices.Add(new FloatMenuOption(Name(chosen), delegate
					{
						setting = chosen;
						Settings.Set("inputMode", chosen);
						Apply(false);
					}));
				}
				Find.WindowStack.Add(new FloatMenu(choices));
			}
		}
	}
}
