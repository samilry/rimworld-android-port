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

		private static MethodInfo open;

		public static void Open()
		{
			try
			{
				if (open == null)
				{
					string folder = Path.Combine(AppContext.BaseDirectory, "workshop");
					var context = new Context(folder);
					Assembly assembly = context.LoadFromAssemblyPath(Path.Combine(folder, "PortWorkshop.dll"));
					open = assembly.GetType("PortWorkshop.Entry", true).GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
				}
				open.Invoke(null, null);
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open: " + inner);
				Find.WindowStack.Add(new Dialog_MessageBox((Texts.Chinese ? "创意工坊打不开：" : "The Workshop could not be opened: ") + inner.Message));
			}
		}
	}
}
