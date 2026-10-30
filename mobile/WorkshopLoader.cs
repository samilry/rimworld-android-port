using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Opens the Steam Workshop window (workshop/PortWorkshop.dll next to the game). It and the libraries it uses
	/// (SteamKit2, protobuf-net, ZstdSharp, System.IO.Hashing, QRCoder) live in workshop/ and are loaded into a load
	/// context of their own, on first use: a mod that brings its own copy of one of those libraries keeps its copy,
	/// and nothing of the Workshop is loaded unless the player asks for it.
	/// </summary>
	internal static class WorkshopLoader
	{
		private sealed class Context : AssemblyLoadContext
		{
			private readonly string folder;

			public Context(string folder) : base("PortWorkshop")
			{
				this.folder = folder;
			}

			// The game's and the runtime's assemblies (and this one) come from the default context.
			protected override Assembly Load(AssemblyName name)
			{
				string path = Path.Combine(folder, name.Name + ".dll");
				return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
			}
		}

		private static Type entry;

		/// <summary>PortWorkshop.Entry, loaded on first use.</summary>
		private static Type Entry
		{
			get
			{
				if (entry == null)
				{
					string folder = Path.Combine(AppContext.BaseDirectory, "workshop");
					var context = new Context(folder);
					Assembly assembly = context.LoadFromAssemblyPath(Path.Combine(folder, "PortWorkshop.dll"));
					entry = assembly.GetType("PortWorkshop.Entry", true);
				}
				return entry;
			}
		}

		public static void Open()
		{
			try
			{
				Entry.GetMethod("Open", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open: " + inner);
				Find.WindowStack.Add(new Dialog_MessageBox((Texts.Chinese ? "创意工坊打不开：" : "The Workshop could not be opened: ") + inner.Message));
			}
		}

		/// <summary>
		/// The window for a Workshop link (OpenUrl): the item goes into its list, and its page is offered for the browser.
		/// False when the Workshop could not be opened (the caller then opens the page in the browser).
		/// </summary>
		public static bool OpenItem(string item, string page)
		{
			try
			{
				// A PortWorkshop.dll from before links (put on the phone by itself) has no OpenItem.
				MethodInfo openItem = Entry.GetMethod("OpenItem", BindingFlags.Public | BindingFlags.Static);
				if (openItem == null)
				{
					return false;
				}
				openItem.Invoke(null, new object[] { item, page });
				return true;
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open for a link: " + inner);
				return false;
			}
		}
	}
}
