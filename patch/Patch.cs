using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

/// <summary>
/// Rewrites the staged game assemblies for the phone. Done once, on the PC, to the copies in build/game:
///   - platform checks: System.OperatingSystem.IsLinux() and RuntimeInformation.IsOSPlatform(OSPlatform) become
///     calls into PortHooks.PortPlatform, which says "Linux" for Android (the engine has no Android branch and
///     throws "Unsupported platform" otherwise).
/// usage: dotnet patch.dll <folder with the assemblies> [more search folders...]
/// </summary>
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

	// Rewrites that apply inside 0Harmony.dll's own HarmonyLib types (not MonoMod's, which probe the runtime's internals by
	// exact name to tell runtimes apart): Harmony's field lookups by name tolerate Mono's private field names
	// (PortPlatform.GetFieldLax, the stand-in for the runtime fork's WaylithClr.LaxPrivateFieldNames).
	private static readonly Dictionary<string, string> HarmonyRewrites = new Dictionary<string, string>
	{
		["System.Reflection.FieldInfo System.Type::GetField(System.String,System.Reflection.BindingFlags)"] = "GetFieldLax",
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
		// ModAssemblyHandler.ReloadAll keeps its Assembly.LoadFrom, as on the PC: PurePatcher points that call at its own
		// loader; the port's handling of mod assemblies is a prefix on Assembly.LoadFrom (hooks/StartupHook.cs, BeforeLoadFrom).
		// A mod folder whose files the game may not read (copied in by another app) is left out of the list of mods
		// instead of ending the whole scan (hooks/ModFolders.cs).
		["System.Void Verse.ModLister::RebuildModList()"] = new Dictionary<string, string>
		{
			["System.IO.DirectoryInfo[] System.IO.DirectoryInfo::GetDirectories()"] = "ReadableModFolders",
		},
	};

	// MonoMod's native exception helper (trampolines that keep the runtime's C++ exceptions from unwinding through the
	// managed JIT hook) comes from the APK as libmm_exhelper.so (native/exhelper) instead of the embedded
	// exhelper_linux_arm64.so dropped under /tmp: there is no /tmp here, and that copy wants _Unwind_* from glibc's libgcc_s.
	private const string CreateNativeExceptionHelper = "MonoMod.Core.Platforms.Systems.PosixExceptionHelper MonoMod.Core.Platforms.Systems.LinuxSystem::CreateNativeExceptionHelper()";
	private const string ExceptionHelperLibrary = "libmm_exhelper.so";

	// Methods of 0Harmony.dll with the arguments (OpCode opcode, MethodInfo method) whose opcode first passes through
	// PortPlatform.LaxCallOpcode: Harmony's emitter for the instructions of a patched method (the stand-in for the runtime
	// fork's WaylithClr.LaxCallvirt).
	// (DynEmit takes the operand as an object: it is what emits the instructions a transpiler hands back.)
	private static readonly Dictionary<string, string> CallOpcodeFilters = new Dictionary<string, string>
	{
		["System.Void HarmonyLib.Emitter::Emit(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
		["System.Void HarmonyLib.Emitter::EmitCall(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
		["System.Void HarmonyLib.Emitter::DynEmit(System.Reflection.Emit.OpCode,System.Object)"] = "LaxCallOpcodeForOperand",
	};

	// Harmony's emission of a replacement method's instruction list: the list first passes through
	// PortPlatform.NormalizeHarmonyStack (hooks/HarmonyCompat.cs: values a transpiler left on the stack at a jump).
	private const string EmitCodes = "System.Void HarmonyLib.MethodCreatorTools::EmitCodes(HarmonyLib.MethodCreator,HarmonyLib.Emitter,System.Collections.Generic.List`1<HarmonyLib.CodeInstruction>)";

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
		// From 1.7.4979 the fallbacks are looked up by file name among the fonts found under the system's font folders
		// (UnityStubs' Font.GetPathsToOSFonts, Linux: /usr/share/fonts and others): the phone keeps its fonts in
		// /system/fonts, and some of them under other names.
		["/usr/share/fonts"] = "/system/fonts",
		["DejaVuSans.ttf"] = "DroidSans.ttf",
		["NotoSansSymbols-Regular.ttf"] = "NotoSansSymbols-Regular-Subsetted.ttf",
		["NotoSansSymbols2-Regular.ttf"] = "NotoSansSymbols-Regular-Subsetted2.ttf",
		["LiberationSans-Regular.ttf"] = "Roboto-Regular.ttf",
		["DroidSansFallbackFull.ttf"] = "DroidSansFallback.ttf",
		// MonoMod (inside 0Harmony.dll) calls the phone "Android" when this file exists and then has no system support for it; as "Linux" its LinuxSystem works here (bionic has the same mmap/mprotect, arm64 ABI is implemented).
		["/system/build.prop"] = "/system/build.prop.treated-as-linux",
	};

	/// <summary>Called as the rewriting of the game's assemblies takes the next one: its name, its place, how many.</summary>
	internal static Action<string, int, int> FileStarted;

	internal static int Main(string[] args)
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
		if (args[0] == "--mods-import")
		{
			return PrepareLikeImport(args[1]);
		}
		if (args[0] == "--mods-version")
		{
			Console.WriteLine(ModPrep.RulesVersion);
			return 0;
		}
		if (args[0] == "--check")
		{
			return CheckReferences(args[1]);
		}
		if (args[0] == "--corelib")
		{
			return PatchCoreLib(args[1], args[2]);
		}
		string folder = args[0];
		var resolver = new InMemoryResolver();
		foreach (string dir in args)
		{
			resolver.AddSearchDirectory(dir);
		}
		var reader = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
		int filesChanged = 0;
		string[] files = Directory.GetFiles(folder, "*.dll").OrderBy(f => f).ToArray();
		for (int index = 0; index < files.Length; index++)
		{
			string file = files[index];
			string name = Path.GetFileName(file);
			if (name == "PortHooks.dll")
			{
				continue;
			}
			// The phone's setup shows this (setup/Setup.cs): on a phone where it stalled, which assembly it stalled on.
			FileStarted?.Invoke(name, index + 1, files.Length);
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
						if (method.FullName == CreateNativeExceptionHelper && !(method.Body.Instructions.Count == 6 && method.Body.Instructions[2].Operand is string lib && lib == ExceptionHelperLibrary))
						{
							// return PosixExceptionHelper.CreateHelper(this.arch, "libmm_exhelper.so", deleteFile: false)
							FieldDefinition arch = type.Fields.First(f => f.Name == "arch");
							MethodDefinition createHelper = module.GetType("MonoMod.Core.Platforms.Systems.PosixExceptionHelper").Methods.First(m => m.Name == "CreateHelper" && m.Parameters.Count == 3);
							method.Body.Instructions.Clear();
							method.Body.ExceptionHandlers.Clear();
							method.Body.Variables.Clear();
							ILProcessor il = method.Body.GetILProcessor();
							il.Emit(OpCodes.Ldarg_0);
							il.Emit(OpCodes.Ldfld, arch);
							il.Emit(OpCodes.Ldstr, ExceptionHelperLibrary);
							il.Emit(OpCodes.Ldc_I4_0);
							il.Emit(OpCodes.Call, createHelper);
							il.Emit(OpCodes.Ret);
							changes++;
						}
						if (CallOpcodeFilters.TryGetValue(method.FullName, out string filterName))
						{
							if (!imported.TryGetValue(filterName, out MethodReference filter))
							{
								imported[filterName] = filter = Import(module, resolver, filterName);
							}
							Instruction first = method.Body.Instructions[0];
							bool done = method.Body.Instructions.Count > 2 && method.Body.Instructions[2].Operand is MethodReference there && there.FullName == filter.FullName;
							if (!done)
							{
								// opcode = PortPlatform.LaxCallOpcode(opcode, method);   (an instance method: this, opcode, method)
								ILProcessor il = method.Body.GetILProcessor();
								il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
								il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
								il.InsertBefore(first, Instruction.Create(OpCodes.Call, filter));
								il.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, method.Parameters[0]));
								changes++;
							}
						}
						if (method.FullName == EmitCodes)
						{
							if (!imported.TryGetValue("NormalizeHarmonyStack", out MethodReference normalize))
							{
								imported["NormalizeHarmonyStack"] = normalize = Import(module, resolver, "NormalizeHarmonyStack");
							}
							Instruction first = method.Body.Instructions[0];
							bool done = method.Body.Instructions.Count > 2 && method.Body.Instructions[2].Operand is MethodReference there && there.FullName == normalize.FullName;
							if (!done)
							{
								// PortPlatform.NormalizeHarmonyStack(creator, codes);   (static: creator, emitter, codes)
								ILProcessor il = method.Body.GetILProcessor();
								il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
								il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
								il.InsertBefore(first, Instruction.Create(OpCodes.Call, normalize));
								changes++;
							}
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
								&& !(ScopedRewrites.TryGetValue(method.FullName, out Dictionary<string, string> scoped) && scoped.TryGetValue(target.FullName, out replacement))
								&& !(name == "0Harmony.dll" && type.FullName.StartsWith("HarmonyLib.", StringComparison.Ordinal) && HarmonyRewrites.TryGetValue(target.FullName, out replacement)))
							{
								continue;
							}
							if (!imported.TryGetValue(replacement, out MethodReference reference))
							{
								imported[replacement] = reference = Import(module, resolver, replacement);
							}
							// The stand-ins are static methods: an instance method's "this" becomes the first argument, and a
							// callvirt has to become a call (this runtime refuses callvirt to a static method).
							i.OpCode = OpCodes.Call;
							i.Operand = reference;
							changes++;
						}
					}
				}
				// Mods set read-only static fields of the game by reflection ([NL] Custom Portraits writes ColonistBar.BaseSize
				// on every frame): Mono lets that pass, Ludeon's runtime fork too (WaylithClr.AllowInitOnlyStaticWrites), the
				// stock runtime throws "Cannot set initonly static field ... after type ... is initialized". The game
				// assembly's static fields are no longer initonly (the same that is done to the mods' own, for DefOf).
				if (name == "Assembly-CSharp.dll")
				{
					foreach (TypeDefinition type in module.GetTypes())
					{
						foreach (FieldDefinition field in type.Fields)
						{
							if (field.IsStatic && field.IsInitOnly && !field.HasConstant)
							{
								field.IsInitOnly = false;
								changes++;
							}
						}
					}
				}
				if (name == "0Harmony.dll")
				{
					changes += FlushAfterPatchData(module);
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
	/// MonoMod writes code through LinuxSystem.PatchData (a detour's jump over a compiled method, the stubs it builds): an
	/// mprotect and a plain copy. Its Windows and macOS systems flush the instruction cache after writing executable memory,
	/// the Linux one, made for x86-64, does not; on arm64 a core may go on running the old instructions of a method patched
	/// after it had run. Here PatchData ends with
	///   if (patchKind == PatchTargetKind.Executable) Unix.ClearCache(patchTarget, (nuint)data.Length);
	/// (mm_clear_cache in libmonomodc.so, native/monomodc), in a try: an APK whose libmonomodc.so is older than this leaves
	/// the flush out, as before, instead of failing every patch with EntryPointNotFoundException.
	/// </summary>
	private static int FlushAfterPatchData(ModuleDefinition module)
	{
		TypeDefinition unix = module.GetType("MonoMod.Core.Interop.Unix");
		MethodDefinition patchData = module.GetType("MonoMod.Core.Platforms.Systems.LinuxSystem")?.Methods.SingleOrDefault(m => m.Name == "PatchData" && m.Parameters.Count == 4);
		if (unix == null || patchData == null || unix.Methods.Any(m => m.Name == "ClearCache"))
		{
			return 0;
		}
		MethodBody body = patchData.Body;
		List<Instruction> rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
		MethodReference dataLength = body.Instructions.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "get_Length" && m.DeclaringType.Name == "ReadOnlySpan`1");
		if (rets.Count != 1 || body.HasExceptionHandlers || dataLength == null)
		{
			Console.WriteLine("0Harmony.dll: LinuxSystem.PatchData is not as expected; no instruction cache flush after it");
			return 0;
		}

		// [DllImport("monomodc", EntryPoint = "mm_clear_cache", CallingConvention = CallingConvention.Cdecl)]
		// public static extern void ClearCache(nint start, nuint length);   (as MonoMod's other P/Invokes here)
		ModuleReference shim = module.ModuleReferences.FirstOrDefault(m => m.Name == "monomodc");
		if (shim == null)
		{
			module.ModuleReferences.Add(shim = new ModuleReference("monomodc"));
		}
		var clearCache = new MethodDefinition("ClearCache", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, module.TypeSystem.Void)
		{
			ImplAttributes = MethodImplAttributes.PreserveSig,
			PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "mm_clear_cache", shim),
		};
		clearCache.Parameters.Add(new ParameterDefinition("start", ParameterAttributes.None, module.TypeSystem.IntPtr));
		clearCache.Parameters.Add(new ParameterDefinition("length", ParameterAttributes.None, module.TypeSystem.UIntPtr));
		unix.Methods.Add(clearCache);

		// Before the return: the flush, in a try that catches EntryPointNotFoundException.
		Instruction ret = rets[0];
		Instruction flush = Instruction.Create(OpCodes.Ldarg_1);
		Instruction tryStart = Instruction.Create(OpCodes.Ldarg_2);
		Instruction catchStart = Instruction.Create(OpCodes.Pop);
		foreach (Instruction branch in body.Instructions.Where(i => i.Operand == ret).ToList())
		{
			branch.Operand = flush;
		}
		ILProcessor il = body.GetILProcessor();
		il.InsertBefore(ret, flush);
		il.InsertBefore(ret, Instruction.Create(OpCodes.Brtrue, ret));
		il.InsertBefore(ret, tryStart);
		il.InsertBefore(ret, Instruction.Create(OpCodes.Ldarga, patchData.Parameters[2]));
		il.InsertBefore(ret, Instruction.Create(OpCodes.Call, dataLength));
		il.InsertBefore(ret, Instruction.Create(OpCodes.Conv_U));
		il.InsertBefore(ret, Instruction.Create(OpCodes.Call, clearCache));
		il.InsertBefore(ret, Instruction.Create(OpCodes.Leave, ret));
		il.InsertBefore(ret, catchStart);
		il.InsertBefore(ret, Instruction.Create(OpCodes.Leave, ret));
		body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
		{
			TryStart = tryStart,
			TryEnd = catchStart,
			HandlerStart = catchStart,
			HandlerEnd = ret,
			CatchType = new TypeReference("System", "EntryPointNotFoundException", module, module.TypeSystem.CoreLibrary),
		});
		return 1;
	}

	/// <summary>
	/// Writes type-forwarding facades: an assembly of each given name (UnityEngine.CoreModule, ...) whose every type is
	/// forwarded to the stub assembly (UnityStubs.dll). Mods were compiled against Unity's assemblies; Ludeon's runtime
	/// fork redirects those names to UnityStubs, the stock runtime refuses a resolver that answers with another name,
	/// and type forwarding is the standard way to say the same thing.
	/// </summary>
	internal static int WriteForwarders(string stubsPath, string outDir, string[] names)
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
	/// Prepares the copy of a mod that goes to the phone (host/mods-set.js stages it first): what is done to its assemblies
	/// is in ModPrep.cs, which the phone runs too for mods that reach it some other way.
	/// </summary>
	private static int PatchModAssemblies(string root)
	{
		using (InMemoryResolver resolver = ModPrep.NewResolver(new[] { "build/game", "build/runtime", "dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0" }))
		{
			Console.WriteLine(ModPrep.PrepareFolder(root, resolver, Console.WriteLine));
		}
		return 0;
	}

	/// <summary>
	/// Every mod folder under a folder prepared in turn with one resolver, as the phone's mod import does it
	/// (mobile/ModImport.cs Install: PortPlatform.PrepareModFolder per mod, ReleaseModPrep at the end), with the live
	/// heap after each: for measuring what an import of many mods holds. Changes the folders' assemblies in place: copies.
	/// </summary>
	private static int PrepareLikeImport(string folder)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		using (InMemoryResolver resolver = ModPrep.NewResolver(new[] { "build/game", "build/runtime", "dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0" }))
		{
			int n = 0;
			foreach (string root in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
			{
				ModPrep.Result result = ModPrep.PrepareFolder(root, resolver, line => { });
				n++;
				Console.WriteLine(n + " " + Path.GetFileName(root) + ": " + result.Files + " files; live heap " + (GC.GetTotalMemory(true) >> 20) + " MiB, " + resolver.Cached + " assemblies held, " + watch.ElapsedMilliseconds + " ms");
			}
		}
		Console.WriteLine("released: live heap " + (GC.GetTotalMemory(true) >> 20) + " MiB");
		return 0;
	}

	/// <summary>
	/// The runtime fork's WaylithClr.MonoStringHashCode, which the game's runtimeconfig turns on: string.GetHashCode()
	/// is Mono's hash (the same for a string on every launch) instead of the stock runtime's, which is seeded at random
	/// per process. The game derives things from it, e.g. which of first and last name a colonist without a nickname is
	/// called (NameTriple.Nick), so these changed on every launch and differed from the PC. Ludeon's System.Private.CoreLib
	/// computes it in String.GetMonoHashCode; this gives the runtime pack's CoreLib the same method and makes
	/// GetHashCode() call it, always (this runtime only runs the game). Nothing else changes: the dictionaries'
	/// non-randomized string comparer, GetHashCode(ReadOnlySpan) and the ignore-case hashes stay as they are, as in the fork.
	/// The pack's CoreLib is ReadyToRun; the output is IL only (host/corelib.sh compiles it again with crossgen2).
	/// usage: --corelib <the pack's System.Private.CoreLib.dll> <output>
	/// </summary>
	private static int PatchCoreLib(string input, string output)
	{
		ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true });
		TypeDefinition str = module.GetType("System.String");
		if (str.Methods.Any(m => m.Name == "GetMonoHashCode"))
		{
			Console.WriteLine("already has String.GetMonoHashCode");
			return 1;
		}
		FieldDefinition firstChar = str.Fields.First(f => f.Name == "_firstChar");
		MethodDefinition getHashCode = str.Methods.Single(m => m.Name == "GetHashCode" && !m.IsStatic && m.Parameters.Count == 0);
		TypeReference int32 = module.TypeSystem.Int32;

		// private unsafe int GetMonoHashCode(): Ludeon's, which is the old .NET Framework 64-bit string hash (up to the first '\0').
		//   fixed (char* p = &_firstChar) { int h1 = 5381, h2 = h1; char* s = p; int c;
		//     while ((c = s[0]) != 0) { h1 = ((h1 << 5) + h1) ^ c; c = s[1]; if (c == 0) break; h2 = ((h2 << 5) + h2) ^ c; s += 2; }
		//     return h1 + h2 * 1566083941; }
		var mono = new MethodDefinition("GetMonoHashCode", MethodAttributes.Private | MethodAttributes.HideBySig, int32);
		MethodBody body = mono.Body;
		var pinned = new VariableDefinition(new PinnedType(new ByReferenceType(module.TypeSystem.Char)));
		var h1 = new VariableDefinition(int32);
		var h2 = new VariableDefinition(int32);
		var s = new VariableDefinition(new PointerType(module.TypeSystem.Char));
		var c = new VariableDefinition(int32);
		foreach (VariableDefinition v in new[] { pinned, h1, h2, s, c })
		{
			body.Variables.Add(v);
		}
		body.InitLocals = true;
		ILProcessor il = body.GetILProcessor();
		Instruction loopTest = Instruction.Create(OpCodes.Ldloc, s);
		Instruction done = Instruction.Create(OpCodes.Ldloc, h1);
		Instruction loopBody = Instruction.Create(OpCodes.Ldloc, h1);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldflda, firstChar);
		il.Emit(OpCodes.Stloc, pinned);
		il.Emit(OpCodes.Ldloc, pinned);
		il.Emit(OpCodes.Conv_U);
		il.Emit(OpCodes.Stloc, s);
		il.Emit(OpCodes.Ldc_I4, 5381);
		il.Emit(OpCodes.Stloc, h1);
		il.Emit(OpCodes.Ldloc, h1);
		il.Emit(OpCodes.Stloc, h2);
		il.Emit(OpCodes.Br, loopTest);
		// h1 = ((h1 << 5) + h1) ^ c
		il.Append(loopBody);
		il.Emit(OpCodes.Ldc_I4_5);
		il.Emit(OpCodes.Shl);
		il.Emit(OpCodes.Ldloc, h1);
		il.Emit(OpCodes.Add);
		il.Emit(OpCodes.Ldloc, c);
		il.Emit(OpCodes.Xor);
		il.Emit(OpCodes.Stloc, h1);
		// c = s[1]; if (c == 0) break;
		il.Emit(OpCodes.Ldloc, s);
		il.Emit(OpCodes.Ldc_I4_2);
		il.Emit(OpCodes.Add);
		il.Emit(OpCodes.Ldind_U2);
		il.Emit(OpCodes.Stloc, c);
		il.Emit(OpCodes.Ldloc, c);
		il.Emit(OpCodes.Brfalse, done);
		// h2 = ((h2 << 5) + h2) ^ c
		il.Emit(OpCodes.Ldloc, h2);
		il.Emit(OpCodes.Ldc_I4_5);
		il.Emit(OpCodes.Shl);
		il.Emit(OpCodes.Ldloc, h2);
		il.Emit(OpCodes.Add);
		il.Emit(OpCodes.Ldloc, c);
		il.Emit(OpCodes.Xor);
		il.Emit(OpCodes.Stloc, h2);
		// s += 2
		il.Emit(OpCodes.Ldloc, s);
		il.Emit(OpCodes.Ldc_I4_4);
		il.Emit(OpCodes.Add);
		il.Emit(OpCodes.Stloc, s);
		// while ((c = s[0]) != 0)
		il.Append(loopTest);
		il.Emit(OpCodes.Ldind_U2);
		il.Emit(OpCodes.Dup);
		il.Emit(OpCodes.Stloc, c);
		il.Emit(OpCodes.Brtrue, loopBody);
		// return h1 + h2 * 1566083941
		il.Append(done);
		il.Emit(OpCodes.Ldloc, h2);
		il.Emit(OpCodes.Ldc_I4, 1566083941);
		il.Emit(OpCodes.Mul);
		il.Emit(OpCodes.Add);
		il.Emit(OpCodes.Ret);
		str.Methods.Add(mono);

		// GetHashCode() { return GetMonoHashCode(); }   (stays AggressiveInlining, as in the fork)
		getHashCode.Body = new MethodBody(getHashCode);
		ILProcessor gh = getHashCode.Body.GetILProcessor();
		gh.Emit(OpCodes.Ldarg_0);
		gh.Emit(OpCodes.Call, mono);
		gh.Emit(OpCodes.Ret);

		AddLudeonMembers(module);
		AddLudeonSwitches(module);

		// Written as plain IL for arm64 (the ReadyToRun code is gone; crossgen2 wants IL-only input).
		module.Attributes = (module.Attributes & ~ModuleAttributes.ILLibrary) | ModuleAttributes.ILOnly;
		module.Architecture = TargetArchitecture.ARM64;
		module.Write(output);
		Console.WriteLine("String.GetHashCode() -> GetMonoHashCode(): " + output);
		return 0;
	}

	/// <summary>
	/// The public members Ludeon's System.Private.CoreLib (1.7.4984) has and the runtime pack's has not, found by comparing
	/// the two (every public type and member, and all that is marked [Experimental("WAYLITHCLR_SPECIFIC")]); the rest
	/// of the difference is Windows interop and WaylithClr.* (hooks/WaylithClr.cs stands in for those):
	///   - X86Base.ReadTimeStampCounter(), which the engine's profiler clock calls when the CPU has an invariant TSC (an
	///     x86 one only). The JIT needs it to exist to compile FrameProfiler.BeginFrame, where it is inlined: without
	///     it, opening the frame profiler (Dubs Performance Analyzer, the game's dev window) threw MissingMethodException
	///     every frame and the game ended (a tester's HONOR). Here it throws PlatformNotSupportedException, as every
	///     X86Base method does on arm64; it is never reached (ProfilerClock uses it only when X86Base.IsSupported).
	///   - AppDomain.DefineDynamicAssembly(name, access[, attributes]) and AssemblyBuilder.DefineDynamicModule(name,
	///     fileName), the .NET Framework calls old mods and libraries make, as Ludeon's: the access made Run, or
	///     RunAndCollect when it asks for collection.
	///   - JitHelpers.UnsafeEnumCast / UnsafeEnumCastLong (Mono's, which mods reach through a publicizer: Adaptive
	///     Storage's Fishery compares enum keys with them): an enum's (or integer's) bits as int / long. Ludeon's keeps the
	///     type code of T in a static class; this asks for it each call (the same result).
	/// </summary>
	private static void AddLudeonMembers(ModuleDefinition module)
	{
		TypeSystem ts = module.TypeSystem;
		TypeDefinition notSupported = module.GetType("System.PlatformNotSupportedException");
		MethodReference notSupportedCtor = notSupported.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);

		// X86Base.ReadTimeStampCounter(): (uint, uint)
		TypeDefinition x86 = module.GetType("System.Runtime.Intrinsics.X86.X86Base");
		var pair = new GenericInstanceType(module.GetType("System.ValueTuple`2"));
		pair.GenericArguments.Add(ts.UInt32);
		pair.GenericArguments.Add(ts.UInt32);
		var tsc = new MethodDefinition("ReadTimeStampCounter", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, pair);
		tsc.Body.GetILProcessor().Emit(OpCodes.Newobj, notSupportedCtor);
		tsc.Body.GetILProcessor().Emit(OpCodes.Throw);
		x86.Methods.Add(tsc);

		// AppDomain.DefineDynamicAssembly
		TypeDefinition appDomain = module.GetType("System.AppDomain");
		TypeDefinition builder = module.GetType("System.Reflection.Emit.AssemblyBuilder");
		TypeDefinition access = module.GetType("System.Reflection.Emit.AssemblyBuilderAccess");
		MethodDefinition define2 = builder.Methods.Single(m => m.Name == "DefineDynamicAssembly" && m.IsStatic && m.Parameters.Count == 2);
		MethodDefinition define3 = builder.Methods.Single(m => m.Name == "DefineDynamicAssembly" && m.IsStatic && m.Parameters.Count == 3);
		// private static AssemblyBuilderAccess WaylithNormalizeAccess(AssemblyBuilderAccess access)
		//   => access == Run || access == RunAndCollect ? access : (access & 8) == 0 ? Run : RunAndCollect;
		var normalize = new MethodDefinition("WaylithNormalizeAccess", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, access);
		normalize.Parameters.Add(new ParameterDefinition("access", ParameterAttributes.None, access));
		ILProcessor n = normalize.Body.GetILProcessor();
		Instruction keep = Instruction.Create(OpCodes.Ldarg_0);
		Instruction collect = Instruction.Create(OpCodes.Ldc_I4, 9);
		n.Emit(OpCodes.Ldarg_0);
		n.Emit(OpCodes.Ldc_I4_1);
		n.Emit(OpCodes.Beq, keep);
		n.Emit(OpCodes.Ldarg_0);
		n.Emit(OpCodes.Ldc_I4, 9);
		n.Emit(OpCodes.Beq, keep);
		n.Emit(OpCodes.Ldarg_0);
		n.Emit(OpCodes.Ldc_I4_8);
		n.Emit(OpCodes.And);
		n.Emit(OpCodes.Brtrue, collect);
		n.Emit(OpCodes.Ldc_I4_1);
		n.Emit(OpCodes.Ret);
		n.Append(collect);
		n.Emit(OpCodes.Ret);
		n.Append(keep);
		n.Emit(OpCodes.Ret);
		appDomain.Methods.Add(normalize);
		foreach (MethodDefinition target in new[] { define2, define3 })
		{
			var define = new MethodDefinition("DefineDynamicAssembly", MethodAttributes.Public | MethodAttributes.HideBySig, builder);
			foreach (ParameterDefinition p in target.Parameters)
			{
				define.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));
			}
			ILProcessor d = define.Body.GetILProcessor();
			d.Emit(OpCodes.Ldarg_1);
			d.Emit(OpCodes.Ldarg_2);
			d.Emit(OpCodes.Call, normalize);
			if (target.Parameters.Count == 3)
			{
				d.Emit(OpCodes.Ldarg_3);
			}
			d.Emit(OpCodes.Call, target);
			d.Emit(OpCodes.Ret);
			appDomain.Methods.Add(define);
		}

		// AssemblyBuilder.DefineDynamicModule(string name, string fileName) => DefineDynamicModule(name)
		MethodDefinition defineModule = builder.Methods.Single(m => m.Name == "DefineDynamicModule" && !m.IsStatic && m.Parameters.Count == 1);
		var defineModule2 = new MethodDefinition("DefineDynamicModule", MethodAttributes.Public | MethodAttributes.HideBySig, defineModule.ReturnType);
		defineModule2.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, ts.String));
		defineModule2.Parameters.Add(new ParameterDefinition("fileName", ParameterAttributes.None, ts.String));
		ILProcessor dm = defineModule2.Body.GetILProcessor();
		dm.Emit(OpCodes.Ldarg_0);
		dm.Emit(OpCodes.Ldarg_1);
		dm.Emit(OpCodes.Call, defineModule);
		dm.Emit(OpCodes.Ret);
		builder.Methods.Add(defineModule2);

		// public static class System.Runtime.CompilerServices.JitHelpers
		var jitHelpers = new TypeDefinition("System.Runtime.CompilerServices", "JitHelpers",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, ts.Object);
		module.Types.Add(jitHelpers);
		TypeDefinition type = module.GetType("System.Type");
		TypeDefinition enumType = module.GetType("System.Enum");
		MethodReference fromHandle = type.Methods.Single(m => m.Name == "GetTypeFromHandle" && m.Parameters.Count == 1);
		MethodReference isEnum = type.Methods.Single(m => m.Name == "get_IsEnum");
		MethodReference typeCode = type.Methods.Single(m => m.Name == "GetTypeCode" && m.IsStatic && m.Parameters.Count == 1);
		MethodReference underlying = enumType.Methods.Single(m => m.Name == "GetUnderlyingType" && m.IsStatic && m.Parameters.Count == 1);
		foreach (bool wide in new[] { false, true })
		{
			var cast = new MethodDefinition(wide ? "UnsafeEnumCastLong" : "UnsafeEnumCast", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, wide ? ts.Int64 : ts.Int32);
			var t = new GenericParameter("T", cast) { Attributes = GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint };
			t.Constraints.Add(new GenericParameterConstraint(module.GetType("System.ValueType")));
			cast.GenericParameters.Add(t);
			cast.Parameters.Add(new ParameterDefinition("val", ParameterAttributes.None, t));
			var local = new VariableDefinition(type);
			cast.Body.Variables.Add(local);
			ILProcessor c = cast.Body.GetILProcessor();
			// Type u = typeof(T); if (u.IsEnum) u = Enum.GetUnderlyingType(u); switch (Type.GetTypeCode(u) - TypeCode.SByte)
			Instruction code = Instruction.Create(OpCodes.Ldloc, local);
			c.Emit(OpCodes.Ldtoken, t);
			c.Emit(OpCodes.Call, fromHandle);
			c.Emit(OpCodes.Stloc, local);
			c.Emit(OpCodes.Ldloc, local);
			c.Emit(OpCodes.Callvirt, isEnum);
			c.Emit(OpCodes.Brfalse, code);
			c.Emit(OpCodes.Ldloc, local);
			c.Emit(OpCodes.Call, underlying);
			c.Emit(OpCodes.Stloc, local);
			c.Append(code);
			c.Emit(OpCodes.Call, typeCode);
			c.Emit(OpCodes.Ldc_I4_5);
			c.Emit(OpCodes.Sub);
			// SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64: the value's bits read at its own width
			OpCode[] loads = { OpCodes.Ldind_I1, OpCodes.Ldind_U1, OpCodes.Ldind_I2, OpCodes.Ldind_U2, OpCodes.Ldind_I4, OpCodes.Ldind_U4, OpCodes.Ldind_I8, OpCodes.Ldind_I8 };
			bool[] unsigned = { false, true, false, true, false, true, false, true };
			var cases = new Instruction[loads.Length];
			for (int i = 0; i < cases.Length; i++)
			{
				cases[i] = Instruction.Create(OpCodes.Ldarga, cast.Parameters[0]);
			}
			Instruction fallback = Instruction.Create(OpCodes.Ldarga, cast.Parameters[0]);
			c.Emit(OpCodes.Switch, cases);
			c.Emit(OpCodes.Br, fallback);
			for (int i = 0; i < cases.Length; i++)
			{
				c.Append(cases[i]);
				c.Emit(loads[i]);
				if (wide && i < 6)
				{
					c.Emit(unsigned[i] ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
				}
				else if (!wide && i >= 6)
				{
					c.Emit(OpCodes.Conv_I4);
				}
				c.Emit(OpCodes.Ret);
			}
			// Anything else (Ludeon's default): int, or long for the wide one.
			c.Append(fallback);
			c.Emit(wide ? OpCodes.Ldind_I8 : OpCodes.Ldind_I4);
			c.Emit(OpCodes.Ret);
			jitHelpers.Methods.Add(cast);
		}
		Console.WriteLine("Ludeon's additions: X86Base.ReadTimeStampCounter, AppDomain.DefineDynamicAssembly (2), AssemblyBuilder.DefineDynamicModule(name, fileName), JitHelpers.UnsafeEnumCast / UnsafeEnumCastLong");
	}

	/// <summary>
	/// The WaylithClr switches that Ludeon's runtime fork implements in its System.Private.CoreLib (1.7.4990; the game's
	/// runtimeconfig turns all of them on, the stock runtime knows none), done here as there, always on:
	///   - MonoFloatFormatPrecision: Number.FormatFloat gives "G" without a precision (ToString(), "G", string
	///     interpolation) the significant digits of a custom format, 7 for float and 15 for double, as Mono did: 0.8 where
	///     the stock runtime writes the shortest round trip, 0.8000001 (mods' labels, saved values; "R" is unchanged).
	///   - DynamicMethodOwnerIsDeclaringType: DynamicMethod.DeclaringType and ReflectedType are the owner type it was made
	///     for instead of null (Harmony's replacements are dynamic methods; mods that name a method by its type found null).
	///   - LaxDynamicMethodAttributes: DynamicMethod.Init takes any attributes and calling convention as public static
	///     standard instead of throwing NotSupportedException (Mono accepted them).
	///   - IsolatedLoadsResolveByName: an assembly loaded by Assembly.Load(byte[]) or Assembly.LoadFile, each in a load
	///     context of its own, is remembered by simple name, and a reference by that name that nothing else resolves finds
	///     it (AssemblyLoadContext.ResolveUsingEvent asks first, then the Resolving handlers), as Mono's one domain did.
	///   - LaxPrivateFieldNames: Type.GetField(name, flags with NonPublic) on a type of the framework's assemblies (System,
	///     System.*, mscorlib, netstandard) that has no field of that name takes the non-public one Mono's name stands for
	///     here: "_name", else "m_name" (for "m_name": "_name"). Humanoid Alien Races reads XmlElement.name, _name here.
	///     Harmony's lookups by name had this before (PortPlatform.GetFieldLax); now every caller has it, as on the PC.
	///   - AllowInitOnlyStaticWrites: reflection writes a static readonly field after its class was initialized instead of
	///     throwing FieldAccessException (FieldAccessor.VerifyInitOnly). The same check in libcoreclr.so, which the first
	///     SetValue of a field and those of struct-typed fields go through, is taken out by host/patch-coreclr.js.
	/// </summary>
	private static void AddLudeonSwitches(ModuleDefinition module)
	{
		TypeSystem ts = module.TypeSystem;

		// MonoFloatFormatPrecision. In FormatFloat(ref ValueListBuilder<TChar>, TNumber, ReadOnlySpan<char>, NumberFormatInfo):
		//   char c = ParseFormatSpecifier(format, out int digits); ...
		//   if (c == '\0') digits = TNumber.MaxPrecisionCustomFormat;
		// becomes
		//   if (c == '\0' || ((c | 0x20) == 'g' && digits <= 0)) digits = TNumber.MaxPrecisionCustomFormat;
		TypeDefinition number = module.GetType("System.Number");
		MethodDefinition formatFloat = number.Methods.Single(m => m.Name == "FormatFloat" && m.Parameters.Count == 4 && m.Parameters[0].ParameterType.IsByReference);
		MethodBody body = formatFloat.Body;
		body.SimplifyMacros();
		Instruction parse = body.Instructions.Single(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "ParseFormatSpecifier");
		var c = (VariableDefinition)parse.Next.Operand;
		var digits = (VariableDefinition)parse.Previous.Operand;
		Instruction test = parse.Next;
		while (!(test.OpCode == OpCodes.Ldloc && test.Operand == c && test.Next.OpCode == OpCodes.Brtrue))
		{
			test = test.Next;
		}
		Instruction branch = test.Next;
		Instruction set = branch.Next;
		var skip = (Instruction)branch.Operand;
		if (!(set.OpCode == OpCodes.Constrained && ((MethodReference)set.Next.Operand).Name == "get_MaxPrecisionCustomFormat" && set.Next.Next.OpCode == OpCodes.Stloc && set.Next.Next.Operand == digits && set.Next.Next.Next == skip))
		{
			throw new InvalidOperationException("FormatFloat is not as expected");
		}
		ILProcessor il = body.GetILProcessor();
		branch.OpCode = OpCodes.Brfalse;
		branch.Operand = set;
		il.InsertBefore(set, Instruction.Create(OpCodes.Ldloc, c));
		il.InsertBefore(set, Instruction.Create(OpCodes.Ldc_I4, 0x20));
		il.InsertBefore(set, Instruction.Create(OpCodes.Or));
		il.InsertBefore(set, Instruction.Create(OpCodes.Ldc_I4, (int)'g'));
		il.InsertBefore(set, Instruction.Create(OpCodes.Bne_Un, skip));
		il.InsertBefore(set, Instruction.Create(OpCodes.Ldloc, digits));
		il.InsertBefore(set, Instruction.Create(OpCodes.Ldc_I4_0));
		il.InsertBefore(set, Instruction.Create(OpCodes.Bgt, skip));
		body.OptimizeMacros();

		// DynamicMethodOwnerIsDeclaringType: get_DeclaringType / get_ReflectedType { return _typeOwner; }
		TypeDefinition dynamicMethod = module.GetType("System.Reflection.Emit.DynamicMethod");
		FieldDefinition typeOwner = dynamicMethod.Fields.Single(f => f.Name == "_typeOwner");
		foreach (string getter in new[] { "get_DeclaringType", "get_ReflectedType" })
		{
			MethodDefinition get = dynamicMethod.Methods.Single(m => m.Name == getter && m.Parameters.Count == 0);
			get.Body = new MethodBody(get);
			ILProcessor g = get.Body.GetILProcessor();
			g.Emit(OpCodes.Ldarg_0);
			g.Emit(OpCodes.Ldfld, typeOwner);
			g.Emit(OpCodes.Ret);
		}

		// LaxDynamicMethodAttributes: at the start of Init(name, attributes, callingConvention, ...),
		//   attributes = MethodAttributes.Public | MethodAttributes.Static; callingConvention = CallingConventions.Standard;
		MethodDefinition init = dynamicMethod.Methods.Single(m => m.Name == "Init" && m.Parameters.Count >= 3 && m.Parameters[1].ParameterType.FullName == "System.Reflection.MethodAttributes" && m.Parameters[2].ParameterType.FullName == "System.Reflection.CallingConventions");
		ILProcessor ii = init.Body.GetILProcessor();
		Instruction first = init.Body.Instructions[0];
		ii.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4, 0x16));
		ii.InsertBefore(first, Instruction.Create(OpCodes.Starg, init.Parameters[1]));
		ii.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_1));
		ii.InsertBefore(first, Instruction.Create(OpCodes.Starg, init.Parameters[2]));

		// IsolatedLoadsResolveByName.
		TypeDefinition alc = module.GetType("System.Runtime.Loader.AssemblyLoadContext");
		TypeDefinition assemblyType = module.GetType("System.Reflection.Assembly");
		TypeDefinition assemblyName = module.GetType("System.Reflection.AssemblyName");
		// A synchronized Hashtable (CoreLib has no ConcurrentDictionary): simple name -> the first assembly of that name.
		TypeDefinition hashtable = module.GetType("System.Collections.Hashtable");
		MethodDefinition hashtableCtor = hashtable.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
		MethodDefinition synchronized = hashtable.Methods.Single(m => m.Name == "Synchronized" && m.IsStatic);
		MethodDefinition containsKey = hashtable.Methods.Single(m => m.Name == "ContainsKey" && m.Parameters.Count == 1);
		MethodDefinition getItem = hashtable.Methods.Single(m => m.Name == "get_Item" && m.Parameters.Count == 1);
		MethodDefinition setItem = hashtable.Methods.Single(m => m.Name == "set_Item" && m.Parameters.Count == 2);
		var isolated = new FieldDefinition("s_waylithIsolatedLoads", FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, hashtable);
		alc.Fields.Add(isolated);
		MethodDefinition cctor = alc.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
		if (cctor == null)
		{
			cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, ts.Void);
			cctor.Body.GetILProcessor().Emit(OpCodes.Ret);
			alc.Methods.Add(cctor);
		}
		ILProcessor cc = cctor.Body.GetILProcessor();
		Instruction cctorFirst = cctor.Body.Instructions[0];
		cc.InsertBefore(cctorFirst, Instruction.Create(OpCodes.Newobj, hashtableCtor));
		cc.InsertBefore(cctorFirst, Instruction.Create(OpCodes.Call, synchronized));
		cc.InsertBefore(cctorFirst, Instruction.Create(OpCodes.Stsfld, isolated));

		// internal static void WaylithRegisterIsolatedLoad(Assembly assembly)
		//   { string name = assembly?.GetName().Name; if (name != null && !s_waylithIsolatedLoads.ContainsKey(name)) s_waylithIsolatedLoads[name] = assembly; }
		var register = new MethodDefinition("WaylithRegisterIsolatedLoad", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
		register.Parameters.Add(new ParameterDefinition("assembly", ParameterAttributes.None, assemblyType));
		var registerName = new VariableDefinition(ts.String);
		register.Body.Variables.Add(registerName);
		ILProcessor r = register.Body.GetILProcessor();
		Instruction registerEnd = Instruction.Create(OpCodes.Ret);
		r.Emit(OpCodes.Ldarg_0);
		r.Emit(OpCodes.Brfalse, registerEnd);
		r.Emit(OpCodes.Ldarg_0);
		r.Emit(OpCodes.Callvirt, assemblyType.Methods.Single(m => m.Name == "GetName" && m.Parameters.Count == 0));
		r.Emit(OpCodes.Callvirt, assemblyName.Methods.Single(m => m.Name == "get_Name"));
		r.Emit(OpCodes.Stloc, registerName);
		r.Emit(OpCodes.Ldloc, registerName);
		r.Emit(OpCodes.Brfalse, registerEnd);
		r.Emit(OpCodes.Ldsfld, isolated);
		r.Emit(OpCodes.Ldloc, registerName);
		r.Emit(OpCodes.Callvirt, containsKey);
		r.Emit(OpCodes.Brtrue, registerEnd);
		r.Emit(OpCodes.Ldsfld, isolated);
		r.Emit(OpCodes.Ldloc, registerName);
		r.Emit(OpCodes.Ldarg_0);
		r.Emit(OpCodes.Callvirt, setItem);
		r.Append(registerEnd);
		alc.Methods.Add(register);

		// private Assembly WaylithResolveFirst(AssemblyName name)
		//   { the isolated load of that simple name, or else GetFirstResolvedAssemblyFromResolvingEvent(name) }
		MethodDefinition resolveUsingEvent = alc.Methods.Single(m => m.Name == "ResolveUsingEvent" && !m.IsStatic);
		Instruction firstResolved = resolveUsingEvent.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "GetFirstResolvedAssemblyFromResolvingEvent");
		var getFirst = (MethodReference)firstResolved.Operand;
		var resolveFirst = new MethodDefinition("WaylithResolveFirst", MethodAttributes.Private | MethodAttributes.HideBySig, assemblyType);
		resolveFirst.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, assemblyName));
		var found = new VariableDefinition(assemblyType);
		var simple = new VariableDefinition(ts.String);
		resolveFirst.Body.Variables.Add(found);
		resolveFirst.Body.Variables.Add(simple);
		resolveFirst.Body.InitLocals = true;
		ILProcessor f = resolveFirst.Body.GetILProcessor();
		Instruction fallback = Instruction.Create(OpCodes.Ldarg_0);
		f.Emit(OpCodes.Ldarg_1);
		f.Emit(OpCodes.Callvirt, assemblyName.Methods.Single(m => m.Name == "get_Name"));
		f.Emit(OpCodes.Stloc, simple);
		f.Emit(OpCodes.Ldloc, simple);
		f.Emit(OpCodes.Brfalse, fallback);
		f.Emit(OpCodes.Ldsfld, isolated);
		f.Emit(OpCodes.Ldloc, simple);
		f.Emit(OpCodes.Callvirt, getItem);
		f.Emit(OpCodes.Isinst, assemblyType);
		f.Emit(OpCodes.Stloc, found);
		f.Emit(OpCodes.Ldloc, found);
		f.Emit(OpCodes.Brfalse, fallback);
		f.Emit(OpCodes.Ldloc, found);
		f.Emit(OpCodes.Ret);
		f.Append(fallback);
		f.Emit(OpCodes.Ldarg_1);
		f.Emit(OpCodes.Call, getFirst);
		f.Emit(OpCodes.Ret);
		alc.Methods.Add(resolveFirst);
		firstResolved.Operand = resolveFirst;

		// Assembly.Load(byte[], byte[]) and Assembly.LoadFile(string) register what they return.
		foreach (MethodDefinition load in assemblyType.Methods.Where(m => m.IsStatic && ((m.Name == "Load" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Byte[]") || (m.Name == "LoadFile" && m.Parameters.Count == 1))).ToList())
		{
			ILProcessor l = load.Body.GetILProcessor();
			foreach (Instruction ret in load.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
			{
				// Branches that went to the ret go to the dup now.
				Instruction dup = Instruction.Create(OpCodes.Dup);
				foreach (Instruction i in load.Body.Instructions.Where(i => i.Operand == ret).ToList())
				{
					i.Operand = dup;
				}
				foreach (ExceptionHandler handler in load.Body.ExceptionHandlers)
				{
					if (handler.TryEnd == ret) handler.TryEnd = dup;
					if (handler.HandlerEnd == ret) handler.HandlerEnd = dup;
				}
				l.InsertBefore(ret, dup);
				l.InsertBefore(ret, Instruction.Create(OpCodes.Call, register));
			}
			Console.WriteLine("  registers isolated loads: Assembly." + load.Name + "(" + string.Join(", ", load.Parameters.Select(p => p.ParameterType.Name)) + ")");
		}

		// LaxPrivateFieldNames. RuntimeType.GetField(name, bindingAttr) without its null check becomes GetFieldExact, and
		// GetField is Ludeon's:
		//   ArgumentNullException.ThrowIfNull(name);
		//   FieldInfo field = GetFieldExact(name, bindingAttr);
		//   if (field == null && (bindingAttr & BindingFlags.NonPublic) != 0 && name.Length > 0 && name[0] != '_' && IsWaylithFrameworkType())
		//   { bool mono = name.StartsWith("m_", StringComparison.Ordinal);
		//     FieldInfo lax = GetFieldExact("_" + (mono ? name.Substring(2) : name), bindingAttr);
		//     if (lax == null && !mono) lax = GetFieldExact("m_" + name, bindingAttr);
		//     if (lax != null && !lax.IsPublic) field = lax; }
		//   return field;
		TypeDefinition runtimeType = module.GetType("System.RuntimeType");
		TypeDefinition stringType = module.GetType("System.String");
		MethodDefinition getField = runtimeType.Methods.Single(m => m.Name == "GetField" && !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String");
		MethodBody exactBody = getField.Body;
		Instruction[] nullCheck = exactBody.Instructions.Take(3).ToArray();
		if (!(nullCheck[0].OpCode == OpCodes.Ldarg_1 && nullCheck[1].OpCode == OpCodes.Ldstr && nullCheck[2].OpCode == OpCodes.Call && ((MethodReference)nullCheck[2].Operand).Name == "ThrowIfNull"))
		{
			throw new InvalidOperationException("RuntimeType.GetField is not as expected");
		}
		var exact = new MethodDefinition("GetFieldExact", MethodAttributes.Private | MethodAttributes.HideBySig, getField.ReturnType);
		foreach (ParameterDefinition parameter in getField.Parameters)
		{
			exact.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, parameter.ParameterType));
		}
		// The rest of the body moves over as it is; the arguments its instructions name are the new method's.
		exact.Body.InitLocals = exactBody.InitLocals;
		foreach (VariableDefinition local in exactBody.Variables)
		{
			exact.Body.Variables.Add(local);
		}
		foreach (Instruction moved in exactBody.Instructions.Skip(3).ToList())
		{
			if (moved.Operand is ParameterDefinition argument)
			{
				moved.Operand = argument == exactBody.ThisParameter ? exact.Body.ThisParameter : exact.Parameters[argument.Index];
			}
			exact.Body.Instructions.Add(moved);
		}
		exact.Body.Instructions[0].Previous = null;
		foreach (ExceptionHandler handler in exactBody.ExceptionHandlers)
		{
			exact.Body.ExceptionHandlers.Add(handler);
		}
		runtimeType.Methods.Add(exact);

		// private bool IsWaylithFrameworkType()
		//   { string name = GetRuntimeModule().GetRuntimeAssembly().GetSimpleName();
		//     return name != null && (name.StartsWith("System.", StringComparison.Ordinal) || name == "System" || name == "mscorlib" || name == "netstandard"); }
		MethodDefinition startsWith = stringType.Methods.Single(m => m.Name == "StartsWith" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String" && m.Parameters[1].ParameterType.FullName == "System.StringComparison");
		MethodDefinition stringEquals = stringType.Methods.Single(m => m.Name == "op_Equality" && m.Parameters.Count == 2);
		var isFramework = new MethodDefinition("IsWaylithFrameworkType", MethodAttributes.Private | MethodAttributes.HideBySig, ts.Boolean);
		var simpleName = new VariableDefinition(ts.String);
		isFramework.Body.Variables.Add(simpleName);
		ILProcessor fw = isFramework.Body.GetILProcessor();
		Instruction framework = Instruction.Create(OpCodes.Ldc_I4_1);
		Instruction notFramework = Instruction.Create(OpCodes.Ldc_I4_0);
		fw.Emit(OpCodes.Ldarg_0);
		fw.Emit(OpCodes.Call, runtimeType.Methods.Single(m => m.Name == "GetRuntimeModule" && !m.IsStatic && m.Parameters.Count == 0));
		fw.Emit(OpCodes.Call, module.GetType("System.Reflection.RuntimeModule").Methods.Single(m => m.Name == "GetRuntimeAssembly" && m.Parameters.Count == 0));
		fw.Emit(OpCodes.Call, module.GetType("System.Reflection.RuntimeAssembly").Methods.Single(m => m.Name == "GetSimpleName" && !m.IsStatic && m.Parameters.Count == 0));
		fw.Emit(OpCodes.Stloc, simpleName);
		fw.Emit(OpCodes.Ldloc, simpleName);
		fw.Emit(OpCodes.Brfalse, notFramework);
		fw.Emit(OpCodes.Ldloc, simpleName);
		fw.Emit(OpCodes.Ldstr, "System.");
		fw.Emit(OpCodes.Ldc_I4_4); // StringComparison.Ordinal
		fw.Emit(OpCodes.Callvirt, startsWith);
		fw.Emit(OpCodes.Brtrue, framework);
		foreach (string frameworkName in new[] { "System", "mscorlib" })
		{
			fw.Emit(OpCodes.Ldloc, simpleName);
			fw.Emit(OpCodes.Ldstr, frameworkName);
			fw.Emit(OpCodes.Call, stringEquals);
			fw.Emit(OpCodes.Brtrue, framework);
		}
		fw.Emit(OpCodes.Ldloc, simpleName);
		fw.Emit(OpCodes.Ldstr, "netstandard");
		fw.Emit(OpCodes.Call, stringEquals);
		fw.Emit(OpCodes.Ret);
		fw.Append(framework);
		fw.Emit(OpCodes.Ret);
		fw.Append(notFramework);
		fw.Emit(OpCodes.Ret);
		runtimeType.Methods.Add(isFramework);

		// GetField itself, new.
		MethodDefinition concat = stringType.Methods.Single(m => m.Name == "Concat" && m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.FullName == "System.String"));
		getField.Body = new MethodBody(getField);
		var fieldFound = new VariableDefinition(getField.ReturnType);
		var lax = new VariableDefinition(getField.ReturnType);
		var mono = new VariableDefinition(ts.Boolean);
		foreach (VariableDefinition v in new[] { fieldFound, lax, mono })
		{
			getField.Body.Variables.Add(v);
		}
		getField.Body.InitLocals = true;
		ILProcessor gf = getField.Body.GetILProcessor();
		Instruction result = Instruction.Create(OpCodes.Ldloc, fieldFound);
		Instruction wholeName = Instruction.Create(OpCodes.Ldarg_1);
		Instruction underscore = Instruction.Create(OpCodes.Call, concat);
		Instruction checkLax = Instruction.Create(OpCodes.Ldloc, lax);
		foreach (Instruction check in nullCheck)
		{
			gf.Append(check);
		}
		gf.Emit(OpCodes.Ldarg_0);
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Ldarg_2);
		gf.Emit(OpCodes.Call, exact);
		gf.Emit(OpCodes.Stloc, fieldFound);
		gf.Emit(OpCodes.Ldloc, fieldFound);
		gf.Emit(OpCodes.Brtrue, result);
		gf.Emit(OpCodes.Ldarg_2);
		gf.Emit(OpCodes.Ldc_I4, 0x20); // BindingFlags.NonPublic
		gf.Emit(OpCodes.And);
		gf.Emit(OpCodes.Brfalse, result);
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Callvirt, stringType.Methods.Single(m => m.Name == "get_Length"));
		gf.Emit(OpCodes.Brfalse, result);
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Ldc_I4_0);
		gf.Emit(OpCodes.Callvirt, stringType.Methods.Single(m => m.Name == "get_Chars" && m.Parameters.Count == 1));
		gf.Emit(OpCodes.Ldc_I4, (int)'_');
		gf.Emit(OpCodes.Beq, result);
		gf.Emit(OpCodes.Ldarg_0);
		gf.Emit(OpCodes.Call, isFramework);
		gf.Emit(OpCodes.Brfalse, result);
		// bool mono = name.StartsWith("m_", StringComparison.Ordinal);
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Ldstr, "m_");
		gf.Emit(OpCodes.Ldc_I4_4);
		gf.Emit(OpCodes.Callvirt, startsWith);
		gf.Emit(OpCodes.Stloc, mono);
		// lax = GetFieldExact("_" + (mono ? name.Substring(2) : name), bindingAttr);
		gf.Emit(OpCodes.Ldarg_0);
		gf.Emit(OpCodes.Ldstr, "_");
		gf.Emit(OpCodes.Ldloc, mono);
		gf.Emit(OpCodes.Brfalse, wholeName);
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Ldc_I4_2);
		gf.Emit(OpCodes.Callvirt, stringType.Methods.Single(m => m.Name == "Substring" && m.Parameters.Count == 1));
		gf.Emit(OpCodes.Br, underscore);
		gf.Append(wholeName);
		gf.Append(underscore);
		gf.Emit(OpCodes.Ldarg_2);
		gf.Emit(OpCodes.Call, exact);
		gf.Emit(OpCodes.Stloc, lax);
		// if (lax == null && !mono) lax = GetFieldExact("m_" + name, bindingAttr);
		gf.Emit(OpCodes.Ldloc, lax);
		gf.Emit(OpCodes.Brtrue, checkLax);
		gf.Emit(OpCodes.Ldloc, mono);
		gf.Emit(OpCodes.Brtrue, checkLax);
		gf.Emit(OpCodes.Ldarg_0);
		gf.Emit(OpCodes.Ldstr, "m_");
		gf.Emit(OpCodes.Ldarg_1);
		gf.Emit(OpCodes.Call, concat);
		gf.Emit(OpCodes.Ldarg_2);
		gf.Emit(OpCodes.Call, exact);
		gf.Emit(OpCodes.Stloc, lax);
		// if (lax != null && !lax.IsPublic) field = lax;
		gf.Append(checkLax);
		gf.Emit(OpCodes.Brfalse, result);
		gf.Emit(OpCodes.Ldloc, lax);
		gf.Emit(OpCodes.Callvirt, module.GetType("System.Reflection.FieldInfo").Methods.Single(m => m.Name == "get_IsPublic"));
		gf.Emit(OpCodes.Brtrue, result);
		gf.Emit(OpCodes.Ldloc, lax);
		gf.Emit(OpCodes.Stloc, fieldFound);
		gf.Append(result);
		gf.Emit(OpCodes.Ret);

		// AllowInitOnlyStaticWrites: FieldAccessor.VerifyInitOnly() { }   (Ludeon's throws only with the switch off)
		MethodDefinition verifyInitOnly = module.GetType("System.Reflection.FieldAccessor").Methods.Single(m => m.Name == "VerifyInitOnly" && m.Parameters.Count == 0);
		verifyInitOnly.Body = new MethodBody(verifyInitOnly);
		verifyInitOnly.Body.GetILProcessor().Emit(OpCodes.Ret);
		Console.WriteLine("Ludeon's switches: MonoFloatFormatPrecision, DynamicMethodOwnerIsDeclaringType, LaxDynamicMethodAttributes, IsolatedLoadsResolveByName, LaxPrivateFieldNames, AllowInitOnlyStaticWrites");
	}

	/// <summary>
	/// Lists the types and members that the assemblies below a folder reference but that neither the staged game folder,
	/// the runtime pack nor the other assemblies there define: Mono-only APIs (Array.UnsafeLoad), members gone from the
	/// game's current version, libraries a mod forgot to ship. usage: --check <root>  (e.g. build/modstage)
	/// </summary>
	private static int CheckReferences(string root)
	{
		var resolver = new InMemoryResolver();
		foreach (string dir in new[] { "build/game", "build/runtime", "dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0" })
		{
			if (Directory.Exists(dir))
			{
				resolver.AddSearchDirectory(dir);
			}
		}
		string[] allFiles = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories).OrderBy(f => f).ToArray();
		foreach (string dir in allFiles.Select(Path.GetDirectoryName).Distinct())
		{
			resolver.AddSearchDirectory(dir);
		}
		string[] files = LoadedVersionOnly(root, allFiles).ToArray();
		var reader = new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver };
		int problems = 0, seen = 0;
		foreach (string file in files)
		{
			string fileName = Path.GetFileName(file);
			if (fileName == "0Harmony.dll" || fileName == "1Harmony.dll")
			{
				continue; // a mod's bundled Harmony is never loaded: the game folder's serves
			}
			AssemblyDefinition assembly;
			try { assembly = AssemblyDefinition.ReadAssembly(file, reader); }
			catch { continue; }
			seen++;
			var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
			using (assembly)
			{
				ModuleDefinition module = assembly.MainModule;
				foreach (TypeReference type in module.GetTypeReferences())
				{
					if (!Resolves(() => type.Resolve()))
					{
						Count(missing, "type " + type.FullName + " [" + (type.Scope == null ? "?" : type.Scope.Name) + "]");
					}
				}
				// Overrides that override nothing any more: a virtual method that reuses a slot (no newslot) and finds no
				// method of its signature in the base types, though one of its name is there (the game changed the
				// signature; the game calls its own method and the mod's never runs).
				foreach (TypeDefinition type in module.GetTypes())
				{
					foreach (MethodDefinition method in type.Methods)
					{
						if (!method.IsVirtual || method.IsNewSlot || method.IsStatic || type.BaseType == null)
						{
							continue;
						}
						bool matched = false, unknown = false;
						string sameName = null;
						TypeDefinition baseType = null;
						try { baseType = type.BaseType.Resolve(); } catch { }
						if (baseType == null) unknown = true;
						for (TypeDefinition t = baseType; t != null && !matched; )
						{
							foreach (MethodDefinition candidate in t.Methods)
							{
								if (!candidate.IsVirtual || candidate.Name != method.Name) continue;
								bool same = candidate.Parameters.Count == method.Parameters.Count && candidate.ReturnType.FullName == method.ReturnType.FullName;
								for (int i = 0; same && i < method.Parameters.Count; i++) same = candidate.Parameters[i].ParameterType.FullName == method.Parameters[i].ParameterType.FullName;
								if (same) { matched = true; break; }
								if (!candidate.HasGenericParameters && !t.HasGenericParameters && t.FullName != "System.Object") sameName = candidate.FullName;
							}
							if (matched || t.BaseType == null) break;
							TypeDefinition next = null;
							try { next = t.BaseType.Resolve(); } catch { }
							if (next == null) { unknown = true; break; }
							t = next;
						}
						if (!matched && !unknown && sameName != null)
						{
							Count(missing, "override of nothing: " + method.FullName + "   (the base has " + sameName + ")");
						}
					}
				}
				foreach (MemberReference member in module.GetMemberReferences())
				{
					if (member.DeclaringType is ArrayType)
					{
						continue; // Get/Set/.ctor of multi-dimensional arrays have no definition to resolve to
					}
					if (member.DeclaringType == null || !Resolves(() => member.DeclaringType.Resolve()))
					{
						continue; // reported as a type
					}
					bool found = member is MethodReference method ? Resolves(() => method.Resolve()) : !(member is FieldReference field) || Resolves(() => field.Resolve());
					if (!found)
					{
						Count(missing, member.FullName);
					}
				}
			}
			if (missing.Count == 0)
			{
				continue;
			}
			Console.WriteLine(Path.GetRelativePath(root, file) + ":");
			foreach (KeyValuePair<string, int> pair in missing)
			{
				Console.WriteLine("  " + pair.Value + "x " + pair.Key);
				problems++;
			}
		}
		Console.WriteLine(problems + " unresolved references in " + seen + " assemblies");
		return 0;
	}

	private static readonly Version GameVersion = new Version(1, 7);

	/// <summary>
	/// Only the assemblies the game would load: of a mod's version folders (1.0 ... 1.7, or v1.6), the highest one not
	/// above the game's version, plus everything outside version folders except a copy of an assembly that folder has (the
	/// root folder loads too, but the version folder loads first and a second assembly of the same name is skipped). The
	/// default rule; LoadFolders.xml is not read.
	/// </summary>
	private static IEnumerable<string> LoadedVersionOnly(string root, IEnumerable<string> files)
	{
		foreach (IGrouping<string, string> mod in files.GroupBy(f => PathParts(root, f)[0]))
		{
			Version best = null;
			foreach (string file in mod)
			{
				Version v = VersionFolderOf(root, file);
				if (v != null && v <= GameVersion && (best == null || v > best))
				{
					best = v;
				}
			}
			var inVersionFolder = new HashSet<string>(mod.Where(f => VersionFolderOf(root, f) == best).Select(f => Path.GetFileName(f).ToLowerInvariant()));
			foreach (string file in mod)
			{
				Version v = VersionFolderOf(root, file);
				if (v == best || (v == null && !inVersionFolder.Contains(Path.GetFileName(file).ToLowerInvariant())))
				{
					yield return file;
				}
			}
		}
	}

	private static string[] PathParts(string root, string file)
	{
		return Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	private static Version VersionFolderOf(string root, string file)
	{
		string[] parts = PathParts(root, file);
		for (int i = 1; i < parts.Length - 1; i++)
		{
			string name = parts[i].StartsWith("v", StringComparison.OrdinalIgnoreCase) ? parts[i].Substring(1) : parts[i];
			if (name.Length <= 5 && name.Contains('.') && Version.TryParse(name, out Version v))
			{
				return v;
			}
		}
		return null;
	}

	private static bool Resolves(Func<object> resolve)
	{
		try { return resolve() != null; }
		catch { return false; }
	}

	private static void Count(SortedDictionary<string, int> counts, string key)
	{
		counts.TryGetValue(key, out int n);
		counts[key] = n + 1;
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
