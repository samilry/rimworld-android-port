using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Rewrites the staged game assemblies for the phone. Done once, on the PC, to the copies in build/game:
///   - platform checks: System.OperatingSystem.IsLinux() and RuntimeInformation.IsOSPlatform(OSPlatform) become
///     calls into PortHooks.PortPlatform, which says "Linux" for Android (the engine has no Android branch and
///     throws "Unsupported platform" otherwise).
/// usage: dotnet patch.dll <folder with the assemblies> [more search folders...]
/// </summary>
/// <summary>Reads resolved assemblies into memory, so that the files can be rewritten while others still refer to them.</summary>
internal sealed class InMemoryResolver : DefaultAssemblyResolver
{
	public override AssemblyDefinition Resolve(AssemblyNameReference name)
	{
		return Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = true });
	}
}

internal static class Patch
{
	private static readonly Dictionary<string, string> Rewrites = new Dictionary<string, string>
	{
		["System.Boolean System.OperatingSystem::IsLinux()"] = "IsLinux",
		["System.Boolean System.Runtime.InteropServices.RuntimeInformation::IsOSPlatform(System.Runtime.InteropServices.OSPlatform)"] = "IsOSPlatform",
		["System.Runtime.InteropServices.PosixSignalRegistration System.Runtime.InteropServices.PosixSignalRegistration::Create(System.Runtime.InteropServices.PosixSignal,System.Action`1<System.Runtime.InteropServices.PosixSignalContext>)"] = "RegisterPosixSignal",
		["System.Void System.Console::add_CancelKeyPress(System.ConsoleCancelEventHandler)"] = "AddCancelKeyPress",
		["System.Void System.Console::remove_CancelKeyPress(System.ConsoleCancelEventHandler)"] = "RemoveCancelKeyPress",
	};

	// Rewrites that apply inside one method only: method full name -> (callee full name -> replacement in PortPlatform).
	private static readonly Dictionary<string, Dictionary<string, string>> ScopedRewrites = new Dictionary<string, Dictionary<string, string>>
	{
		["System.String Waylith.Utils.ResourceFiles::ResolveCaseInsensitive(System.String,System.Boolean)"] = new Dictionary<string, string>
		{
			["Waylith.Utils.ResourceFiles/DirectoryListing Waylith.Utils.ResourceFiles::GetListing(System.String)"] = "GetListingForResolve",
		},
		["System.Void Verse.UIRoot_Entry::Init()"] = new Dictionary<string, string>
		{
			["System.Boolean Verse.Steam.SteamManager::get_Initialized()"] = "SteamInitializedForMenu",
		},
	};

	// String literals to replace wherever they appear (ldstr): the launcher looks for fallback fonts (CJK, symbols) at
	// Linux paths; these are the phone's.
	private static readonly Dictionary<string, string> StringRewrites = new Dictionary<string, string>
	{
		["/usr/share/fonts/TTF/DejaVuSans.ttf"] = "/system/fonts/DroidSans.ttf",
		["/usr/share/fonts/noto/NotoSansSymbols-Regular.ttf"] = "/system/fonts/NotoSansSymbols-Regular-Subsetted.ttf",
		["/usr/share/fonts/noto/NotoSansSymbols2-Regular.ttf"] = "/system/fonts/NotoSansSymbols-Regular-Subsetted2.ttf",
		["/usr/share/fonts/liberation/LiberationSans-Regular.ttf"] = "/system/fonts/Roboto-Regular.ttf",
		["/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc"] = "/system/fonts/NotoSansCJK-Regular.ttc",
		["/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf"] = "/system/fonts/DroidSansFallback.ttf",
		["/usr/share/fonts/noto/NotoSansThai-Regular.ttf"] = "/system/fonts/NotoSansThai-Regular.ttf",
	};

	private static int Main(string[] args)
	{
		if (args[0] == "--members")
		{
			return ListMembers(args[1], args[2]);
		}
		string folder = args[0];
		var resolver = new InMemoryResolver();
		foreach (string dir in args)
		{
			resolver.AddSearchDirectory(dir);
		}
		var reader = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
		int filesChanged = 0;
		foreach (string file in Directory.GetFiles(folder, "*.dll").OrderBy(f => f))
		{
			string name = Path.GetFileName(file);
			if (name == "PortHooks.dll")
			{
				continue;
			}
			AssemblyDefinition assembly;
			try
			{
				assembly = AssemblyDefinition.ReadAssembly(file, reader);
			}
			catch (Exception e)
			{
				Console.WriteLine($"{name}: not read ({e.GetType().Name})");
				continue;
			}
			int changes = 0;
			using (assembly)
			{
				ModuleDefinition module = assembly.MainModule;
				var imported = new Dictionary<string, MethodReference>();
				foreach (TypeDefinition type in module.GetTypes())
				{
					foreach (MethodDefinition method in type.Methods)
					{
						if (!method.HasBody)
						{
							continue;
						}
						foreach (Instruction i in method.Body.Instructions)
						{
							if (i.OpCode == OpCodes.Ldstr && i.Operand is string literal && StringRewrites.TryGetValue(literal, out string replacementLiteral))
							{
								i.Operand = replacementLiteral;
								changes++;
								continue;
							}
							if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) || !(i.Operand is MethodReference target))
							{
								continue;
							}
							if (!Rewrites.TryGetValue(target.FullName, out string replacement)
								&& !(ScopedRewrites.TryGetValue(method.FullName, out Dictionary<string, string> scoped) && scoped.TryGetValue(target.FullName, out replacement)))
							{
								continue;
							}
							if (!imported.TryGetValue(replacement, out MethodReference reference))
							{
								imported[replacement] = reference = Import(module, resolver, replacement);
							}
							i.Operand = reference;
							changes++;
						}
					}
				}
				// The runtime fork's own types (WaylithClr.*, referenced as living in System.Runtime) come from PortHooks instead.
				foreach (TypeReference t in module.GetTypeReferences())
				{
					if (t.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal) && t.Scope is AssemblyNameReference scope && scope.Name == "System.Runtime")
					{
						t.Scope = Import(module, resolver, "IsLinux").DeclaringType.Scope;
						changes++;
					}
				}
				if (changes > 0)
				{
					assembly.Write(file);
					filesChanged++;
					Console.WriteLine($"{name}: {changes} call sites or literals rewritten");
				}
			}
		}
		Console.WriteLine($"{filesChanged} assemblies changed");
		return 0;
	}

	/// <summary>Lists every type and member reference into the given namespace (e.g. WaylithClr, the runtime fork's own types) across the assemblies in the folder.</summary>
	private static int ListMembers(string ns, string folder)
	{
		var reader = new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true };
		foreach (string file in Directory.GetFiles(folder, "*.dll").OrderBy(f => f))
		{
			AssemblyDefinition assembly;
			try { assembly = AssemblyDefinition.ReadAssembly(file, reader); } catch { continue; }
			using (assembly)
			{
				ModuleDefinition module = assembly.MainModule;
				foreach (TypeReference t in module.GetTypeReferences().Where(t => t.FullName.StartsWith(ns + ".")))
				{
					Console.WriteLine($"{Path.GetFileName(file)}: type {t.FullName} in {t.Scope}");
				}
				foreach (MemberReference m in module.GetMemberReferences().Where(m => m.DeclaringType != null && m.DeclaringType.FullName.StartsWith(ns + ".")))
				{
					Console.WriteLine($"{Path.GetFileName(file)}: member {m.FullName}");
				}
			}
		}
		return 0;
	}

	private static MethodReference Import(ModuleDefinition module, IAssemblyResolver resolver, string methodName)
	{
		AssemblyDefinition hooks = resolver.Resolve(new AssemblyNameReference("PortHooks", new Version(1, 0, 0, 0)));
		TypeDefinition platform = hooks.MainModule.GetType("PortHooks.PortPlatform");
		MethodDefinition method = platform.Methods.First(m => m.Name == methodName);
		return module.ImportReference(method);
	}
}
