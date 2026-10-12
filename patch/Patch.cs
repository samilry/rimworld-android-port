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
	};

	private static int Main(string[] args)
	{
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
				if (changes > 0)
				{
					assembly.Write(file);
					filesChanged++;
					Console.WriteLine($"{name}: {changes} platform checks rewritten");
				}
			}
		}
		Console.WriteLine($"{filesChanged} assemblies changed");
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
