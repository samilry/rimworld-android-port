using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PortHooks
{
	/// <summary>
	/// Windows libraries that PC mods call directly, answered by the phone's stand-ins where there is one. Only when an
	/// assembly asks for one and nothing else gave it (ResolvingUnmanagedDll comes after the normal search), so code that
	/// looks for Windows by trying a library finds none as before. user32: the clipboard and MessageBox
	/// (host/stubs/user32.c, libportuser32.so in the APK): Character Editor's "copy" was a DllNotFoundException.
	/// </summary>
	public static class WindowsLibraries
	{
		private static IntPtr user32;
		private static bool told;

		public static void Start()
		{
			try
			{
				AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] Windows libraries: no stand-ins: " + e.Message);
			}
		}

		private static IntPtr Resolve(Assembly assembly, string name)
		{
			string bare = Path.GetFileNameWithoutExtension(name ?? "");
			if (!bare.Equals("user32", StringComparison.OrdinalIgnoreCase))
			{
				return IntPtr.Zero;
			}
			if (user32 == IntPtr.Zero && !NativeLibrary.TryLoad("libportuser32.so", out user32))
			{
				user32 = IntPtr.Zero;
			}
			if (!told)
			{
				told = true;
				Console.WriteLine("[port17] Windows libraries: " + (assembly?.GetName().Name ?? "?") + " asked for " + name + (user32 != IntPtr.Zero ? ": the phone's stand-in (clipboard, MessageBox)" : ": the stand-in did not load"));
			}
			return user32;
		}
	}
}
