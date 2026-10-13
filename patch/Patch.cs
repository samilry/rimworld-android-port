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
		["System.Void Verse.ModAssemblyHandler::ReloadAll()"] = new Dictionary<string, string>
		{
			["System.Reflection.Assembly System.Reflection.Assembly::LoadFrom(System.String)"] = "LoadModAssembly",
		},
	};

	// Methods whose body becomes "return null": MonoMod's native exception helper (a trampoline library it drops under /tmp,
	// which does not exist here) is optional, its users check for null.
	private static readonly HashSet<string> NullBodies = new HashSet<string>
	{
		"MonoMod.Core.Platforms.INativeExceptionHelper MonoMod.Core.Platforms.Systems.LinuxSystem::get_NativeExceptionHelper()",
	};

	// Calls inserted at the start of a method: method full name -> PortPlatform method (static, no arguments, void).
	private static readonly Dictionary<string, string> EntryCalls = new Dictionary<string, string>
	{
		["System.Void Verse.UIRoot_Entry::Init()"] = "OnEntryUIInit",
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
		// MonoMod (inside 0Harmony.dll) calls the phone "Android" when this file exists and then has no system support for it; as "Linux" its LinuxSystem works here (bionic has the same mmap/mprotect, arm64 ABI is implemented).
		["/system/build.prop"] = "/system/build.prop.treated-as-linux",
	};

	private static int Main(string[] args)
	{
		if (args[0] == "--members")
		{
			return ListMembers(args[1], args[2]);
		}
		if (args[0] == "--refs")
		{
			return ListReferences(args[1]);
		}
		if (args[0] == "--forwarders")
		{
			return WriteForwarders(args[1], args[2], args.Skip(3).ToArray());
		}
		if (args[0] == "--mods")
		{
			return PatchModAssemblies(args[1]);
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
						// MonoMod's P/Invokes into glibc (libc, libdl.so.2) go to the bionic shim libmonomodc.so (native/monomodc), prefixed mm_.
						if (name == "0Harmony.dll" && method.HasPInvokeInfo && (method.PInvokeInfo.Module.Name == "libc" || method.PInvokeInfo.Module.Name == "libdl.so.2"))
						{
							ModuleReference shim = module.ModuleReferences.FirstOrDefault(m => m.Name == "monomodc");
							if (shim == null)
							{
								module.ModuleReferences.Add(shim = new ModuleReference("monomodc"));
							}
							method.PInvokeInfo.EntryPoint = "mm_" + (string.IsNullOrEmpty(method.PInvokeInfo.EntryPoint) ? method.Name : method.PInvokeInfo.EntryPoint);
							method.PInvokeInfo.Module = shim;
							changes++;
						}
						if (!method.HasBody)
						{
							continue;
						}
						if (NullBodies.Contains(method.FullName) && !(method.Body.Instructions.Count == 2 && method.Body.Instructions[0].OpCode == OpCodes.Ldnull))
						{
							method.Body.Instructions.Clear();
							method.Body.ExceptionHandlers.Clear();
							method.Body.Variables.Clear();
							method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
							method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
							changes++;
						}
						if (EntryCalls.TryGetValue(method.FullName, out string entryCall))
						{
							if (!imported.TryGetValue(entryCall, out MethodReference entry))
							{
								imported[entryCall] = entry = Import(module, resolver, entryCall);
							}
							Instruction first = method.Body.Instructions[0];
							if (!(first.OpCode == OpCodes.Call && first.Operand is MethodReference already && already.FullName == entry.FullName))
							{
								method.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Call, entry));
								changes++;
							}
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

	/// <summary>
	/// Writes type-forwarding facades: an assembly of each given name (UnityEngine.CoreModule, ...) whose every type is
	/// forwarded to the stub assembly (UnityStubs.dll). Mods were compiled against Unity's assemblies; Ludeon's runtime
	/// fork redirects those names to UnityStubs, the stock runtime refuses a resolver that answers with another name,
	/// and type forwarding is the standard way to say the same thing.
	/// </summary>
	private static int WriteForwarders(string stubsPath, string outDir, string[] names)
	{
		AssemblyDefinition stubs = AssemblyDefinition.ReadAssembly(stubsPath, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true });
		var publicTypes = new List<TypeDefinition>();
		foreach (TypeDefinition type in stubs.MainModule.Types)
		{
			if (type.IsPublic)
			{
				publicTypes.Add(type);
			}
		}
		Directory.CreateDirectory(outDir);
		foreach (string name in names)
		{
			var assemblyName = new AssemblyNameDefinition(name, new Version(0, 0, 0, 0));
			AssemblyDefinition facade = AssemblyDefinition.CreateAssembly(assemblyName, name + ".dll", ModuleKind.Dll);
			ModuleDefinition module = facade.MainModule;
			var scope = new AssemblyNameReference(stubs.Name.Name, stubs.Name.Version);
			module.AssemblyReferences.Add(scope);
			int count = 0;
			foreach (TypeDefinition type in publicTypes)
			{
				count += Forward(module, scope, type, null);
			}
			facade.Write(Path.Combine(outDir, name + ".dll"));
			Console.WriteLine(name + ".dll: " + count + " types forwarded");
		}
		return 0;
	}

	private static int Forward(ModuleDefinition module, AssemblyNameReference scope, TypeDefinition type, ExportedType declaring)
	{
		var exported = new ExportedType(declaring == null ? type.Namespace : null, type.Name, module, scope) { IsForwarder = true, DeclaringType = declaring };
		module.ExportedTypes.Add(exported);
		int count = 1;
		foreach (TypeDefinition nested in type.NestedTypes)
		{
			if (nested.IsNestedPublic)
			{
				count += Forward(module, scope, nested, exported);
			}
		}
		return count;
	}

	/// <summary>
	/// Prepares the copies of mod assemblies that go to the phone (host/mods-set.js stages them first): every .dll below
	/// the folder gets
	///   - the PE machine type ARM64 when it says AMD64 (pure IL, but the runtime refuses the mark on arm64);
	///   - [assembly: IgnoresAccessChecksTo("...")] for every assembly it references plus the game's own: mods are built
	///     against "publicized" game assemblies and touch private members straight from IL, which Mono let pass and
	///     this runtime checks;
	///   - static fields that are no longer initonly: the game fills DefOf fields by reflection, which this runtime
	///     refuses for a readonly field once the type is initialized (it aborts the whole DefOf binding).
	/// </summary>
	private static int PatchModAssemblies(string root)
	{
		int changed = 0, files = 0;
		var resolver = new InMemoryResolver();
		foreach (string dir in new[] { "build/game", "dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0" })
		{
			if (Directory.Exists(dir))
			{
				resolver.AddSearchDirectory(dir);
			}
		}
		foreach (string file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
		{
			files++;
			try
			{
				if (FixMachineType(file))
				{
					Console.WriteLine(Path.GetFileName(file) + ": AMD64 -> ARM64");
				}
				AssemblyDefinition assembly;
				resolver.AddSearchDirectory(Path.GetDirectoryName(file));
				try { assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver }); }
				catch { continue; }
				int edits = 0;
				using (assembly)
				{
					ModuleDefinition module = assembly.MainModule;
					if (module.Kind != ModuleKind.Dll || module.AssemblyReferences.Count == 0)
					{
						continue;
					}
					edits += AddAccessCheckExemptions(assembly);
					foreach (TypeDefinition type in module.GetTypes())
					{
						foreach (FieldDefinition field in type.Fields)
						{
							if (field.IsStatic && field.IsInitOnly && !field.HasConstant)
							{
								field.IsInitOnly = false;
								edits++;
							}
						}
					}
					if (edits > 0)
					{
						string temp = file + ".patched";
						assembly.Write(temp);
						File.Copy(temp, file, true);
						File.Delete(temp);
						changed++;
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine(Path.GetFileName(file) + ": not patched (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}
		Console.WriteLine(files + " assemblies seen, " + changed + " rewritten");
		return 0;
	}

	private static bool FixMachineType(string file)
	{
		byte[] bytes = File.ReadAllBytes(file);
		if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return false;
		int pe = BitConverter.ToInt32(bytes, 0x3c);
		if (pe + 24 > bytes.Length || bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E') return false;
		if (BitConverter.ToUInt16(bytes, pe + 4) != 0x8664) return false;
		bytes[pe + 4] = 0x64;
		bytes[pe + 5] = 0xaa;
		File.WriteAllBytes(file, bytes);
		return true;
	}

	private static readonly string[] GameAssemblies = { "Assembly-CSharp", "UnityStubs", "Waylith", "Waylith.UnityCompat", "RimWorld", "0Harmony" };

	/// <summary>Adds [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("name")] for the game's assemblies and everything referenced (the attribute type is declared in the assembly itself, as the runtime expects).</summary>
	private static int AddAccessCheckExemptions(AssemblyDefinition assembly)
	{
		ModuleDefinition module = assembly.MainModule;
		var wanted = new HashSet<string>(GameAssemblies);
		foreach (AssemblyNameReference r in module.AssemblyReferences)
		{
			if (!r.Name.StartsWith("System", StringComparison.Ordinal) && r.Name != "mscorlib" && r.Name != "netstandard" && !r.Name.StartsWith("Microsoft.", StringComparison.Ordinal))
			{
				wanted.Add(r.Name);
			}
		}
		foreach (CustomAttribute existing in assembly.CustomAttributes)
		{
			if (existing.AttributeType.Name == "IgnoresAccessChecksToAttribute" && existing.ConstructorArguments.Count == 1)
			{
				wanted.Remove(existing.ConstructorArguments[0].Value as string ?? "");
			}
		}
		if (wanted.Count == 0)
		{
			return 0;
		}
		TypeDefinition attribute = module.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute");
		MethodDefinition ctor;
		if (attribute == null)
		{
			TypeReference attributeBase = module.ImportReference(typeof(Attribute));
			TypeReference stringType = module.TypeSystem.String;
			attribute = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, attributeBase);
			var field = new FieldDefinition("assemblyName", FieldAttributes.Private | FieldAttributes.InitOnly, stringType);
			attribute.Fields.Add(field);
			ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
			ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, stringType));
			MethodReference baseCtor = module.ImportReference(typeof(Attribute).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null));
			ILProcessor il = ctor.Body.GetILProcessor();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Call, baseCtor);
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Stfld, field);
			il.Emit(OpCodes.Ret);
			attribute.Methods.Add(ctor);
			var getter = new MethodDefinition("get_AssemblyName", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, stringType);
			ILProcessor gil = getter.Body.GetILProcessor();
			gil.Emit(OpCodes.Ldarg_0);
			gil.Emit(OpCodes.Ldfld, field);
			gil.Emit(OpCodes.Ret);
			attribute.Methods.Add(getter);
			var property = new PropertyDefinition("AssemblyName", PropertyAttributes.None, stringType) { GetMethod = getter };
			attribute.Properties.Add(property);
			module.Types.Add(attribute);
		}
		else
		{
			ctor = attribute.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1);
		}
		int added = 0;
		foreach (string name in wanted.OrderBy(n => n))
		{
			var custom = new CustomAttribute(ctor);
			custom.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name));
			assembly.CustomAttributes.Add(custom);
			added++;
		}
		return added;
	}

	/// <summary>Counts the assembly references of every .dll below a folder (which Unity modules the mods were compiled against).</summary>
	private static int ListReferences(string root)
	{
		var counts = new Dictionary<string, int>();
		int files = 0;
		foreach (string file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
		{
			AssemblyDefinition assembly;
			try { assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true }); } catch { continue; }
			files++;
			using (assembly)
			{
				foreach (AssemblyNameReference r in assembly.MainModule.AssemblyReferences)
				{
					counts[r.Name] = counts.TryGetValue(r.Name, out int n) ? n + 1 : 1;
				}
			}
		}
		Console.WriteLine(files + " assemblies");
		foreach (var pair in counts.OrderByDescending(p => p.Value))
		{
			Console.WriteLine(pair.Value + "	" + pair.Key);
		}
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
