using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Application.OpenURL on the phone. The game's Linux path starts xdg-open, which Android has not, so nothing opened:
	/// the main menu's links (wiki, Reddit, Ludeon's blog...), the mod page's Workshop and author links, mods' "open the
	/// page" buttons (2026-10-27, from comparing with the PC, whose Windows path opens the browser). A web address (or any
	/// other link with a scheme: mailto:, steam://) goes to the phone's app for it (MainActivity.openUrlUtf8); a folder or
	/// a file of the game's own, which no other app may open on Android, is named in a message instead.
	/// </summary>
	public static class OpenUrl
	{
		[DllImport("glfw")] private static extern int glfwAndroidOpenUrl(byte[] url);

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(UnityEngine.Application), nameof(UnityEngine.Application.OpenURL), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(OpenUrl), nameof(BeforeOpenURL)));
				Console.WriteLine("[mobile] links open in the phone's apps (Application.OpenURL)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: Application.OpenURL left as it is: " + e.Message);
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(System.Diagnostics.Process), nameof(System.Diagnostics.Process.Start), Type.EmptyTypes), prefix: new HarmonyMethod(typeof(OpenUrl), nameof(BeforeProcessStart)));
				Console.WriteLine("[mobile] links: what a mod hands to the shell opens as with Application.OpenURL (Process.Start)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: Process.Start left as it is: " + e.Message);
			}
		}

		/// <summary>
		/// Prefix of Process.Start(), which every Process.Start overload ends in: a link, folder or file a mod hands to the
		/// shell (UseShellExecute; RimTalk's "open folder" of its prompt presets, 2026-10-29) is opened on Windows, in the
		/// browser, Explorer or the file's app. Android has no shell for it, nor .NET's Linux way (xdg-open): it goes the way
		/// of Application.OpenURL here. Programs (UseShellExecute false: MonoMod asks uname) start as they do; a mod's
		/// Process.Start(folder) without the shell fails on the PC's .NET as well, and is left to fail.
		/// </summary>
		public static bool BeforeProcessStart(System.Diagnostics.Process __instance, ref bool __result)
		{
			System.Diagnostics.ProcessStartInfo info;
			try
			{
				info = __instance.StartInfo;
			}
			catch (Exception)
			{
				return true;
			}
			if (info == null || !info.UseShellExecute || string.IsNullOrWhiteSpace(info.FileName))
			{
				return true;
			}
			string target = info.FileName.Trim().Trim('"');
			int colon = target.IndexOf(':');
			bool link = colon > 1 && target.Substring(0, colon).IndexOfAny(new[] { '/', '\\', ' ' }) < 0;
			if (!link && !System.IO.Directory.Exists(target) && !System.IO.File.Exists(target))
			{
				return true; // a program by its name: the runtime says why it does not start
			}
			Console.WriteLine("[mobile] links: " + target + " handed to the shell (Process.Start)" + (UnityData.IsInMainThread ? "" : ", off the main thread: not shown"));
			if (UnityData.IsInMainThread)
			{
				BeforeOpenURL(target);
			}
			__result = false;
			return false;
		}

		public static bool BeforeOpenURL(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return false;
			}
			string link = url.Trim();
			int colon = link.IndexOf(':');
			bool web = colon > 1 && link.Substring(0, colon).IndexOfAny(new[] { '/', '\\', ' ' }) < 0 && !link.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
			if (web)
			{
				bool asked = false;
				try
				{
					asked = glfwAndroidOpenUrl(System.Text.Encoding.UTF8.GetBytes(link + "\0")) != 0;
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] links: " + e.Message);
				}
				Console.WriteLine("[mobile] link " + (asked ? "opened: " : "not opened (no app for it): ") + link);
				if (!asked)
				{
					Messages.Message(Texts.Chinese ? "手机上没有能打开这个链接的应用：" + link : "No app on the phone opens this link: " + link, MessageTypeDefOf.RejectInput, false);
				}
				return false;
			}
			string path = link.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(link.Substring(5).TrimStart('/')) : link;
			if (!path.StartsWith("/", StringComparison.Ordinal) && link.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
			{
				path = "/" + path;
			}
			Console.WriteLine("[mobile] link to a folder or file, named instead: " + path);
			Messages.Message(Texts.Chinese ? "手机上不能直接打开游戏的文件夹，它在：" + path : "The game's folders cannot be opened on the phone; this one is " + path, MessageTypeDefOf.NeutralEvent, false);
			return false;
		}
	}
}
