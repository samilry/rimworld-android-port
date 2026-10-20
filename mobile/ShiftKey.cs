using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The Shift key for a finger, switched on and off by the key wheel's Shift key (KeyWheel; it was a button of its own
	/// at the left edge until 2026-10-20, the user's choice). While it is on the engine sees the left Shift key held down
	/// (a real key event through the Android GLFW, so the game, and any mod that asks for the key, sees what it sees on a
	/// PC):
	///   a tap on a colonist, an animal, a thing or a portrait of the colonist bar adds it to the selection, or
	///   takes it out again; a tap on the ground leaves the selection as it is;
	///   an order (a hold on the map, or a choice from the menu it opens) is queued behind what the colonists are
	///   doing instead of replacing it;
	///   whatever else the game does with Shift (ten at a time in the trade and transfer dialogs).
	/// It goes off by itself when the game is left.
	/// </summary>
	internal static class ShiftKey
	{
		[DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);

		private const int GlfwLeftShift = 340, Press = 1, Release = 0;

		private static bool on;
		private static int injectedFrame = -1;

		public static bool On => on;

		public static void Set(bool value)
		{
			on = value;
			glfwAndroidInjectKey(GlfwLeftShift, value ? Press : Release, 0);
			injectedFrame = Time.frameCount;
			Console.WriteLine("[mobile] shift " + (value ? "held" : "let go"));
		}

		/// <summary>Once a frame: off when no game is played; and held down again if the engine let go of the key (it does when the window comes back).</summary>
		public static void Update()
		{
			if (!on)
			{
				return;
			}
			if (Current.ProgramState != ProgramState.Playing || !KeyWheel.Enabled)
			{
				Set(false);
			}
			else if (Time.frameCount > injectedFrame + 3 && !Input.GetKey(KeyCode.LeftShift))
			{
				Set(true);
			}
		}
	}
}
