using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>Reads resolved assemblies into memory, so that the files can be rewritten while others still refer to them.</summary>
internal sealed class InMemoryResolver : DefaultAssemblyResolver
{
	// The base class caches only in the overload this one replaces, so both outcomes are remembered here: without that, every
	// reference re-read the assembly from disk (5 ms each; a small mod took 10 s to check) and every reference into an
	// assembly that is not there probed all the search directories again.
	private readonly Dictionary<string, AssemblyDefinition> resolved = new Dictionary<string, AssemblyDefinition>();
	private readonly HashSet<string> failed = new HashSet<string>();

	public override AssemblyDefinition Resolve(AssemblyNameReference name)
	{
		if (resolved.TryGetValue(name.FullName, out AssemblyDefinition assembly))
		{
			return assembly;
		}
		if (failed.Contains(name.FullName))
		{
			throw new AssemblyResolutionException(name);
		}
		try
		{
			assembly = Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = true });
			resolved[name.FullName] = assembly;
			return assembly;
		}
		catch (AssemblyResolutionException)
		{
			failed.Add(name.FullName);
			throw;
		}
	}

	protected override void Dispose(bool disposing)
	{
		foreach (AssemblyDefinition assembly in resolved.Values)
		{
			assembly.Dispose();
		}
		resolved.Clear();
		base.Dispose(disposing);
	}
}

/// <summary>
/// Prepares the assemblies of a mod for the phone's runtime. PC mods are built for Mono and for Ludeon's runtime fork,
/// which is lenient where the stock runtime is not; every .dll below the mod's folder gets, in place,
///   - the PE machine type ARM64 when it says AMD64 (pure IL, but the runtime refuses the mark on arm64);
///   - [assembly: IgnoresAccessChecksTo("...")] for every assembly it references plus the game's own: mods are built
///     against "publicized" game assemblies and touch private members straight from IL, which Mono let pass and
///     this runtime checks;
///   - static fields that are no longer initonly: the game fills DefOf fields by reflection, which this runtime
///     refuses for a readonly field once the type is initialized (it aborts the whole DefOf binding);
///   - calls to Mono's Array.UnsafeLoad<T>/UnsafeStore<T> (Fishery, Adaptive Storage) turned into ldelem/stelem: this
///     runtime's CoreLib has no such methods;
///   - the same for assemblies embedded as resources (a mod that loads its real code from memory never shows it as a file);
///   - Lunar Framework's checksum of the file (name.lfc beside name.dll) recomputed when the file changed.
/// A stamp file in the mod's folder records that this was done (and with which version of these rules).
///
/// One source for two places: patch.dll on the PC (host/mods-set.js stages a copy of a Workshop mod and runs
/// patch.dll --mods on it) and PortHooks.dll on the phone (a mod imported in the game, or copied into Mods by any
/// other way, is prepared before its first assembly loads). The PC's Workshop folders are never written to.
/// </summary>
internal static class ModPrep
{
	/// <summary>Bumped whenever what is done to an assembly changes; part of the stamp, so prepared mods are done again.</summary>
	public const int RulesVersion = 5;

	public const string StampName = ".port17-prepared";

	public sealed class Result
	{
		public int Files, Changed, Embedded, Checksums;
		/// <summary>The assemblies that could not be rewritten: path below the mod's folder, and why.</summary>
		public readonly List<KeyValuePair<string, string>> Problems = new List<KeyValuePair<string, string>>();
		/// <summary>The mod's version folders ("1.6", "v1.6"), as they are named.</summary>
		public readonly List<string> VersionFolders = new List<string>();

		/// <summary>
		/// The problems in assemblies the game may load: not those in the folder of another game version (a mod keeps
		/// its builds for 1.0 ... 1.6 side by side; the game takes the highest one not above its own version, and an old
		/// build that refers to something long gone from the game cannot be rewritten, which does not matter).
		/// LoadFolders.xml is not read: a folder that is not named like a version always counts.
		/// </summary>
		public List<string> ProblemsThatMatter(Version game)
		{
			Version best = null;
			foreach (string folder in VersionFolders)
			{
				Version v = VersionOf(folder);
				if (v != null && v <= game && (best == null || v > best))
				{
					best = v;
				}
			}
			var list = new List<string>();
			foreach (KeyValuePair<string, string> problem in Problems)
			{
				string first = problem.Key.Split('/', '\\')[0];
				Version v = VersionOf(first);
				if (v == null || v == best)
				{
					list.Add(problem.Key + ": " + problem.Value);
				}
			}
			return list;
		}

		public override string ToString()
		{
			return Files + " assemblies seen, " + Changed + " rewritten"
				+ (Embedded > 0 ? ", " + Embedded + " embedded" : "")
				+ (Checksums > 0 ? ", " + Checksums + " Lunar checksums refreshed" : "")
				+ (Problems.Count > 0 ? ", " + Problems.Count + " not patched" : "");
		}
	}

	/// <summary>The game version a folder name stands for ("1.6", "v1.6"), or null.</summary>
	private static Version VersionOf(string folder)
	{
		string name = folder.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? folder.Substring(1) : folder;
		return name.Length <= 5 && name.Contains(".") && System.Version.TryParse(name, out Version v) ? v : null;
	}

	/// <summary>A resolver over the given folders (the game's assemblies and the runtime's); dispose it when done.</summary>
	public static InMemoryResolver NewResolver(IEnumerable<string> searchDirs)
	{
		var resolver = new InMemoryResolver();
		foreach (string dir in searchDirs)
		{
			if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
			{
				resolver.AddSearchDirectory(dir);
			}
		}
		return resolver;
	}

	private static IEnumerable<string> AssembliesBelow(string root)
	{
		// The extension is compared here: file name patterns are case-sensitive on the phone and not on the PC.
		return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>What the stamp of a prepared folder says: the rules' version, and how many assemblies of what total size.</summary>
	public static string StampOf(string root)
	{
		int count = 0;
		long bytes = 0;
		foreach (string file in AssembliesBelow(root))
		{
			count++;
			bytes += new FileInfo(file).Length;
		}
		return "v" + RulesVersion + ":" + count + ":" + bytes;
	}

	public static bool IsPrepared(string root)
	{
		try
		{
			string stamp = Path.Combine(root, StampName);
			return File.Exists(stamp) && File.ReadAllText(stamp).Trim() == StampOf(root);
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void WriteStamp(string root)
	{
		File.WriteAllText(Path.Combine(root, StampName), StampOf(root));
	}

	/// <summary>Prepares every assembly below the folder in place and stamps the folder.</summary>
	public static Result PrepareFolder(string root, InMemoryResolver resolver, Action<string> log)
	{
		var result = new Result();
		foreach (string folder in Directory.GetDirectories(root))
		{
			if (VersionOf(Path.GetFileName(folder)) != null)
			{
				result.VersionFolders.Add(Path.GetFileName(folder));
			}
		}
		foreach (string file in AssembliesBelow(root).ToList())
		{
			result.Files++;
			string name = Path.GetFileName(file);
			try
			{
				byte[] original = File.ReadAllBytes(file);
				byte[] data = (byte[])original.Clone();
				byte[] output = null;
				if (FixMachineType(data))
				{
					output = data;
					log(name + ": AMD64 -> ARM64");
				}
				resolver.AddSearchDirectory(Path.GetDirectoryName(file));
				AssemblyDefinition assembly;
				try { assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(data), new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver }); }
				catch { assembly = null; } // a native library, or not an image at all
				if (assembly != null)
				{
					using (assembly)
					{
						ModuleDefinition module = assembly.MainModule;
						if (module.Kind == ModuleKind.Dll && module.AssemblyReferences.Count > 0 && PrepareAssembly(assembly, resolver, name, result, log) > 0)
						{
							var stream = new MemoryStream();
							assembly.Write(stream);
							output = stream.ToArray();
						}
					}
				}
				if (output != null)
				{
					// To a temporary file first: a failed write must not leave half an assembly.
					string temp = file + ".patched";
					File.WriteAllBytes(temp, output);
					File.Copy(temp, file, true);
					File.Delete(temp);
					result.Changed++;
					if (RefreshLunarChecksum(file, original, output))
					{
						result.Checksums++;
					}
				}
			}
			catch (Exception e)
			{
				string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
				result.Problems.Add(new KeyValuePair<string, string>(relative, e.GetType().Name + ": " + e.Message));
				log(relative + ": not patched (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}
		WriteStamp(root);
		return result;
	}

	/// <summary>The edits on one assembly, the assemblies in its resources included; returns how many were made.</summary>
	private static int PrepareAssembly(AssemblyDefinition assembly, InMemoryResolver resolver, string label, Result result, Action<string> log)
	{
		ModuleDefinition module = assembly.MainModule;
		int edits = AddAccessCheckExemptions(assembly);
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
			foreach (MethodDefinition method in type.Methods)
			{
				if (method.HasBody)
				{
					edits += ReplaceMonoArrayHelpers(method.Body);
				}
			}
		}
		return edits + PrepareEmbeddedAssemblies(module, resolver, label, result, log);
	}

	/// <summary>
	/// Some mods keep their real code as an assembly in a resource and load it with Assembly.Load(byte[]) (Brrainz's
	/// CrossPromotion.dll holds two and picks one by game version): such code never passes through the files prepared
	/// here, so its private-member access failed on the phone (the prefix on Page_ModsConfig.DoModInfo threw every frame
	/// and the mod page's detail pane stayed empty). Each embedded resource that is a managed assembly is prepared in
	/// place. Compressed or encrypted ones (Costura's *.compressed) are not recognised.
	/// </summary>
	private static int PrepareEmbeddedAssemblies(ModuleDefinition module, InMemoryResolver resolver, string label, Result result, Action<string> log)
	{
		int replaced = 0;
		for (int i = 0; i < module.Resources.Count; i++)
		{
			if (!(module.Resources[i] is EmbeddedResource resource))
			{
				continue;
			}
			byte[] data = resource.GetResourceData();
			if (data.Length < 0x80 || data[0] != (byte)'M' || data[1] != (byte)'Z')
			{
				continue;
			}
			string name = label + " :: " + resource.Name;
			bool changed = FixMachineType(data);
			try
			{
				using (AssemblyDefinition embedded = AssemblyDefinition.ReadAssembly(new MemoryStream(data), new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver }))
				{
					if (embedded.MainModule.Kind == ModuleKind.Dll && embedded.MainModule.AssemblyReferences.Count > 0 && PrepareAssembly(embedded, resolver, name, result, log) > 0)
					{
						var output = new MemoryStream();
						embedded.Write(output);
						data = output.ToArray();
						changed = true;
					}
				}
			}
			catch (BadImageFormatException)
			{
				// a native library or something else that starts like one
			}
			catch (Exception e)
			{
				log(name + ": embedded assembly not patched (" + e.GetType().Name + ": " + e.Message + ")");
				continue;
			}
			if (changed)
			{
				module.Resources[i] = new EmbeddedResource(resource.Name, resource.Attributes, data);
				log(name + ": embedded assembly prepared");
				result.Embedded++;
				replaced++;
			}
		}
		return replaced;
	}

	/// <summary>
	/// Lunar Framework mods (Map Preview and its kin) check each component assembly before loading it:
	/// name.lfc = MD5(name.dll) XOR MD5(mod version). A rewritten assembly gets MD5(new) XOR the version term, which is
	/// the old checksum XOR MD5(original) XOR MD5(new).
	/// </summary>
	private static bool RefreshLunarChecksum(string file, byte[] original, byte[] output)
	{
		string checksum = Path.ChangeExtension(file, ".lfc");
		if (!File.Exists(checksum))
		{
			return false;
		}
		byte[] old = File.ReadAllBytes(checksum);
		byte[] before, after;
		using (MD5 md5 = MD5.Create())
		{
			before = md5.ComputeHash(original);
			after = md5.ComputeHash(output);
		}
		if (old.Length != before.Length)
		{
			return false;
		}
		for (int i = 0; i < old.Length; i++)
		{
			old[i] = (byte)(old[i] ^ before[i] ^ after[i]);
		}
		File.WriteAllBytes(checksum, old);
		return true;
	}

	/// <summary>Changes the PE machine type AMD64 to ARM64 in place; false when the bytes are not such an image.</summary>
	public static bool FixMachineType(byte[] bytes)
	{
		if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return false;
		int pe = BitConverter.ToInt32(bytes, 0x3c);
		if (pe < 0 || pe + 24 > bytes.Length || bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E') return false;
		if (BitConverter.ToUInt16(bytes, pe + 4) != 0x8664) return false;
		bytes[pe + 4] = 0x64;
		bytes[pe + 5] = 0xaa;
		return true;
	}

	/// <summary>
	/// Mono's CoreLib has internal T Array.UnsafeLoad<T>(T[], int) and void Array.UnsafeStore<T>(T[], int, T), which some
	/// performance-minded mods call straight from IL; the stack layout is that of ldelem/stelem, so the calls become those.
	/// </summary>
	private static int ReplaceMonoArrayHelpers(MethodBody body)
	{
		int replaced = 0;
		foreach (Instruction i in body.Instructions)
		{
			if (i.OpCode != OpCodes.Call || !(i.Operand is GenericInstanceMethod call) || call.DeclaringType.FullName != "System.Array" || call.GenericArguments.Count != 1)
			{
				continue;
			}
			TypeReference element = call.GenericArguments[0];
			if (call.Name == "UnsafeLoad" && call.Parameters.Count == 2)
			{
				i.OpCode = OpCodes.Ldelem_Any;
				i.Operand = element;
				replaced++;
			}
			else if (call.Name == "UnsafeStore" && call.Parameters.Count == 3)
			{
				i.OpCode = OpCodes.Stelem_Any;
				i.Operand = element;
				replaced++;
			}
		}
		return replaced;
	}

	private static readonly string[] GameAssemblies = { "Assembly-CSharp", "UnityStubs", "Waylith", "Waylith.UnityCompat", "RimWorld", "0Harmony" };

	// The framework assemblies whose private members mods reach into (Fishery reads List<T>._items): the runtime checks the
	// attribute against the defining assembly, System.Private.CoreLib and friends, not the facade the mod references.
	private static readonly string[] FrameworkInternals = { "System.Private.CoreLib", "System.Private.Xml", "System.Private.Xml.Linq", "System.Private.Uri", "System.Collections", "System.Collections.Concurrent", "System.Linq", "System.Text.RegularExpressions" };

	/// <summary>Adds [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("name")] for the game's assemblies and everything referenced (the attribute type is declared in the assembly itself, as the runtime expects).</summary>
	private static int AddAccessCheckExemptions(AssemblyDefinition assembly)
	{
		ModuleDefinition module = assembly.MainModule;
		var wanted = new HashSet<string>(GameAssemblies.Concat(FrameworkInternals));
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
}
