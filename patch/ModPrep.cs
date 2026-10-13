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
///   - attributes whose type does not exist removed (Mono passes over them, this runtime throws from every attribute query);
///   - calls to a method that the game's current version has only with more optional parameters at the end, or with
///     another return type that converts implicitly to the old one, adapted the way a recompilation would;
///   - an override of a game method by its old return type, where the old type converts implicitly to the new one,
///     joined by the override the game does call, which returns the mod's result;
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
	public const int RulesVersion = 8;

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
				// (PORT17_PREP_TRACE=1: with the stack, for finding out which rule could not cope.)
				log(relative + ": not patched (" + e.GetType().Name + ": " + e.Message + ")" + (Environment.GetEnvironmentVariable("PORT17_PREP_TRACE") == "1" ? " " + e : ""));
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
		edits += DropAttributesOfMissingTypes(module, label, log);
		edits += AdaptChangedSignatures(module, label, log);
		edits += BridgeChangedOverrides(module, label, log);
		return edits + PrepareEmbeddedAssemblies(module, resolver, label, result, log);
	}

	/// <summary>
	/// A mod built for the game's previous version calls methods by their old signature, and the game's developers
	/// change signatures in ways that cost a mod's author nothing but a recompilation: a new optional parameter at the
	/// end (DamageInfo's constructor, GenExplosion.DoExplosion, TraverseParms.For and GenClosest.ClosestThingReachable
	/// all got one or more in 1.7), or another return type that converts implicitly to the old one (Ability.CanCast:
	/// bool became AcceptanceReport). Unrecompiled, the old call is a MissingMethodException the first time the method
	/// around it runs. Here the call is rewritten as the compiler would have written it: the new parameters' default
	/// values are pushed, the new method is called, the result is converted.
	///
	/// Only for methods of the game's own assembly, where the method by its old signature is not found and exactly one method of that name fits:
	/// the old parameters in their order among the new ones, every other new one optional with a default that can be
	/// loaded, the same return type or one with an op_Implicit to the old. The old parameters are taken to be the
	/// leading ones when that fits (new parameters are added at the end); otherwise there has to be exactly one way to
	/// line them up (GenExplosion.DoExplosion got two in the middle: a float? and an int between a GasType? and a
	/// bool), and the arguments already on the stack are set aside in locals to push the defaults between them. A
	/// changed parameter type, two ways to line up, a method that is gone: those are left as they are.
	/// </summary>
	private static int AdaptChangedSignatures(ModuleDefinition module, string label, Action<string> log)
	{
		var verdicts = new Dictionary<string, MethodDefinition>();
		var lineUps = new Dictionary<string, int[]>();
		var names = new List<string>();
		int adapted = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				Mono.Cecil.Cil.MethodBody body = method.Body;
				bool widened = false;
				for (int i = 0; i < body.Instructions.Count; i++)
				{
					Instruction instruction = body.Instructions[i];
					if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj)
					{
						continue;
					}
					if (!(instruction.Operand is MethodReference called) || called is GenericInstanceMethod || called.HasGenericParameters
						|| called.DeclaringType == null || called.DeclaringType is GenericInstanceType || called.DeclaringType is ArrayType || called.DeclaringType.Scope == module)
					{
						continue;
					}
					string key = called.FullName;
					if (!verdicts.TryGetValue(key, out MethodDefinition replacement))
					{
						try
						{
							replacement = FindChangedMethod(called, out int[] found);
							lineUps[key] = found;
						}
						catch (Exception)
						{
							replacement = null;
						}
						verdicts[key] = replacement;
						if (replacement != null)
						{
							names.Add(called.DeclaringType.Name + "." + called.Name + " (" + called.Parameters.Count + " -> " + replacement.Parameters.Count + " parameters" + (replacement.ReturnType.FullName != called.ReturnType.FullName ? ", returns " + replacement.ReturnType.Name : "") + ")");
						}
					}
					if (replacement == null)
					{
						continue;
					}
					// A prefix (constrained., tail.) belongs to the call it stands before: nothing may come between them.
					if (replacement.Parameters.Count > called.Parameters.Count && i > 0 && body.Instructions[i - 1].OpCode.OpCodeType == OpCodeType.Prefix)
					{
						continue;
					}
					if (!widened)
					{
						WidenBranches(body);
						widened = true;
					}
					// source[j]: which of the old arguments the new parameter j gets, or -1 for its default. The arguments from
					// the first default on are taken off the stack into locals (last one first) and pushed again in place.
					int[] source = lineUps[key];
					int first = 0;
					while (first < source.Length && source[first] == first)
					{
						first++;
					}
					var sequence = new List<Instruction>();
					var kept = new VariableDefinition[called.Parameters.Count];
					for (int n = called.Parameters.Count - 1; n >= first; n--)
					{
						kept[n] = new VariableDefinition(module.ImportReference(called.Parameters[n].ParameterType));
						body.Variables.Add(kept[n]);
						sequence.Add(Instruction.Create(OpCodes.Stloc, kept[n]));
					}
					for (int n = first; n < source.Length; n++)
					{
						if (source[n] < 0)
						{
							PushDefault(module, body, replacement.Parameters[n], sequence);
						}
						else
						{
							sequence.Add(Instruction.Create(OpCodes.Ldloc, kept[source[n]]));
						}
					}
					sequence.Add(Instruction.Create(instruction.OpCode, module.ImportReference(replacement)));
					if (instruction.OpCode != OpCodes.Newobj && replacement.ReturnType.FullName != called.ReturnType.FullName)
					{
						sequence.Add(Instruction.Create(OpCodes.Call, module.ImportReference(ImplicitConversion(replacement.ReturnType, called.ReturnType))));
					}
					// The call's own instruction becomes the first of the sequence, so that jumps to it still arrive before
					// the pushes.
					instruction.OpCode = sequence[0].OpCode;
					instruction.Operand = sequence[0].Operand;
					for (int n = 1; n < sequence.Count; n++)
					{
						body.Instructions.Insert(i + n, sequence[n]);
					}
					i += sequence.Count - 1;
					adapted++;
				}
			}
		}
		if (adapted > 0)
		{
			log(label + ": " + adapted + " call(s) adapted to a signature the game changed: " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
		}
		return adapted;
	}

	/// <summary>The one method that a call by an old signature can be adapted to, or null (also when the call resolves as it is).</summary>
	private static MethodDefinition FindChangedMethod(MethodReference called, out int[] lineUp)
	{
		lineUp = null;
		TypeDefinition declaring = called.DeclaringType.Resolve();
		// The game's own methods only. Another mod's assembly may be found here in the build for another game version
		// (its 1.5 folder beside its 1.6 one), where a method "missing" means nothing.
		if (declaring == null || declaring.Module.Assembly.Name.Name != "Assembly-CSharp" || called.Resolve() != null)
		{
			return null;
		}
		MethodDefinition found = null;
		for (TypeDefinition type = declaring; type != null; type = type.BaseType?.Resolve())
		{
			foreach (MethodDefinition candidate in type.Methods)
			{
				if (candidate.Name != called.Name || candidate.HasThis != called.HasThis || candidate.HasGenericParameters || candidate.Parameters.Count < called.Parameters.Count)
				{
					continue;
				}
				bool sameReturn = candidate.ReturnType.FullName == called.ReturnType.FullName;
				if (candidate.Parameters.Count == called.Parameters.Count && sameReturn)
				{
					continue; // the same signature: the call fails to resolve for some other reason
				}
				int[] source = LineUp(called, candidate);
				if (source == null || (!sameReturn && (candidate.IsConstructor || ImplicitConversion(candidate.ReturnType, called.ReturnType) == null)))
				{
					continue;
				}
				if (found != null)
				{
					lineUp = null;
					return null; // two that fit: not for this place to choose
				}
				found = candidate;
				lineUp = source;
			}
			if (found != null || called.Name == ".ctor")
			{
				break;
			}
		}
		return found;
	}

	/// <summary>
	/// For each parameter of the new method the old argument it gets (-1: its default), or null when the old parameters
	/// do not line up with the new ones: as the leading ones if that fits, else in the one way there is.
	/// </summary>
	private static int[] LineUp(MethodReference called, MethodDefinition candidate)
	{
		int m = called.Parameters.Count, n = candidate.Parameters.Count;
		var same = new bool[m, n];
		var optional = new bool[n];
		for (int j = 0; j < n; j++)
		{
			optional[j] = DefaultCanBePushed(candidate.Parameters[j]);
			for (int i = 0; i < m; i++)
			{
				same[i, j] = candidate.Parameters[j].ParameterType.FullName == called.Parameters[i].ParameterType.FullName;
			}
		}
		var source = new int[n];
		bool leading = true;
		for (int j = 0; j < n && leading; j++)
		{
			leading = j < m ? same[j, j] : optional[j];
			source[j] = j < m ? j : -1;
		}
		if (leading)
		{
			return source;
		}
		// ways[i, j]: in how many ways the old parameters from i on line up with the new ones from j on (2 stands for "more than one").
		var ways = new int[m + 1, n + 1];
		ways[m, n] = 1;
		for (int j = n - 1; j >= 0; j--)
		{
			for (int i = m; i >= 0; i--)
			{
				int count = optional[j] ? ways[i, j + 1] : 0;
				if (i < m && same[i, j])
				{
					count += ways[i + 1, j + 1];
				}
				ways[i, j] = Math.Min(count, 2);
			}
		}
		if (ways[0, 0] != 1)
		{
			return null;
		}
		for (int i = 0, j = 0; j < n; j++)
		{
			if (i < m && same[i, j] && ways[i + 1, j + 1] == 1)
			{
				source[j] = i++;
			}
			else
			{
				source[j] = -1;
			}
		}
		return source;
	}

	private static MethodDefinition ImplicitConversion(TypeReference from, TypeReference to)
	{
		foreach (TypeReference holder in new[] { from, to })
		{
			TypeDefinition type;
			try { type = holder.Resolve(); }
			catch (Exception) { type = null; }
			if (type == null)
			{
				continue;
			}
			foreach (MethodDefinition method in type.Methods)
			{
				if (method.Name == "op_Implicit" && method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == from.FullName && method.ReturnType.FullName == to.FullName)
				{
					return method;
				}
			}
		}
		return null;
	}

	private static bool IsStruct(TypeReference type)
	{
		// (Enums are value types too; their defaults come as constants.)
		return type.IsValueType && !type.IsPrimitive;
	}

	private static bool DefaultCanBePushed(ParameterDefinition parameter)
	{
		TypeReference type = parameter.ParameterType;
		if (!parameter.IsOptional || type.IsByReference || type.IsPointer || type.IsGenericParameter)
		{
			return false;
		}
		object constant = parameter.HasConstant ? parameter.Constant : null;
		if (constant == null)
		{
			return true; // null for a reference, default(T) for a value
		}
		if (type is GenericInstanceType)
		{
			return false; // "int? x = 5": a constant that needs wrapping
		}
		return constant is bool || constant is char || constant is sbyte || constant is byte || constant is short || constant is ushort || constant is int || constant is uint
			|| constant is long || constant is ulong || constant is float || constant is double || constant is string;
	}

	private static void PushDefault(ModuleDefinition module, Mono.Cecil.Cil.MethodBody body, ParameterDefinition parameter, List<Instruction> into)
	{
		TypeReference type = parameter.ParameterType;
		object constant = parameter.HasConstant ? parameter.Constant : null;
		if (constant == null)
		{
			if (type.IsValueType)
			{
				// default(T): through a local of the method.
				var local = new VariableDefinition(module.ImportReference(type));
				body.Variables.Add(local);
				body.InitLocals = true;
				into.Add(Instruction.Create(OpCodes.Ldloca, local));
				into.Add(Instruction.Create(OpCodes.Initobj, local.VariableType));
				into.Add(Instruction.Create(OpCodes.Ldloc, local));
			}
			else
			{
				into.Add(Instruction.Create(OpCodes.Ldnull));
			}
			return;
		}
		switch (constant)
		{
			case bool b: into.Add(Instruction.Create(OpCodes.Ldc_I4, b ? 1 : 0)); break;
			case char c: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)c)); break;
			case sbyte v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case byte v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case short v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case ushort v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case int v: into.Add(Instruction.Create(OpCodes.Ldc_I4, v)); break;
			case uint v: into.Add(Instruction.Create(OpCodes.Ldc_I4, unchecked((int)v))); break;
			case long v: into.Add(Instruction.Create(OpCodes.Ldc_I8, v)); break;
			case ulong v: into.Add(Instruction.Create(OpCodes.Ldc_I8, unchecked((long)v))); break;
			case float v: into.Add(Instruction.Create(OpCodes.Ldc_R4, v)); break;
			case double v: into.Add(Instruction.Create(OpCodes.Ldc_R8, v)); break;
			case string v: into.Add(Instruction.Create(OpCodes.Ldstr, v)); break;
			default: throw new InvalidOperationException("a default of type " + constant.GetType().Name);
		}
	}

	/// <summary>Short jumps reach 127 bytes; a method that gets instructions inserted gets the long forms.</summary>
	private static void WidenBranches(Mono.Cecil.Cil.MethodBody body)
	{
		foreach (Instruction instruction in body.Instructions)
		{
			switch (instruction.OpCode.Code)
			{
				case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
				case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
				case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
				case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
				case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
				case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
				case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
				case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
				case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
				case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
				case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
				case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
				case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
				case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
			}
		}
	}

	/// <summary>
	/// The other side of a changed return type: a class of the mod overrides a method of the game by the old signature
	/// (Miho's MihochanPsycast has "override bool CanCast", and Ability.CanCast returns AcceptanceReport now). The
	/// runtime finds nothing for it to override and gives it a slot of its own: the game calls its own method and the
	/// mod's never runs (Miho's psycasts could be cast without psyfocus and past the heat limit). Unlike a call, this
	/// would not recompile either; but where the old return type converts implicitly to the new one, what the author
	/// has to write is plain: the override by the new signature, returning what the old one returns. That method is
	/// added: it calls the mod's method (virtually: a class derived from this one overrides the old signature) and
	/// converts the result. The mod's method is marked as what it already is in effect, a new virtual method.
	///
	/// Only for a virtual method of the game's own assembly, taken from the nearest base type that has a virtual method
	/// of that name and those parameters, in a class that is not generic and has no method by the new signature. (Two
	/// methods of one name that differ in the return type only: a lookup by name alone, Type.GetMethod("get_CanCast")
	/// on the mod's class, finds two now. Nothing among the mods here does that.)
	/// </summary>
	private static int BridgeChangedOverrides(ModuleDefinition module, string label, Action<string> log)
	{
		var names = new List<string>();
		foreach (TypeDefinition type in module.GetTypes().ToArray())
		{
			if (type.BaseType == null || type.HasGenericParameters || type.BaseType is GenericInstanceType)
			{
				continue;
			}
			foreach (MethodDefinition method in type.Methods.ToArray())
			{
				if (!method.IsVirtual || method.IsNewSlot || method.IsStatic || method.HasGenericParameters || method.HasOverrides || method.ReturnType.MetadataType == MetadataType.Void)
				{
					continue;
				}
				MethodDefinition changed;
				try
				{
					changed = FindChangedBase(type, method);
				}
				catch (Exception)
				{
					changed = null;
				}
				if (changed == null || changed.IsFinal || changed.ReturnType.MetadataType == MetadataType.Void)
				{
					continue;
				}
				MethodDefinition conversion = ImplicitConversion(method.ReturnType, changed.ReturnType);
				if (conversion == null || type.Methods.Any(other => SameNameAndParameters(other, method) && other.ReturnType.FullName == changed.ReturnType.FullName))
				{
					continue;
				}
				var bridge = new MethodDefinition(method.Name, (method.Attributes & ~(MethodAttributes.NewSlot | MethodAttributes.Abstract)) | MethodAttributes.HideBySig, module.ImportReference(changed.ReturnType));
				foreach (ParameterDefinition parameter in method.Parameters)
				{
					bridge.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
				}
				ILProcessor il = bridge.Body.GetILProcessor();
				il.Emit(OpCodes.Ldarg_0);
				foreach (ParameterDefinition parameter in bridge.Parameters)
				{
					il.Emit(OpCodes.Ldarg, parameter);
				}
				il.Emit(OpCodes.Callvirt, method);
				il.Emit(OpCodes.Call, module.ImportReference(conversion));
				il.Emit(OpCodes.Ret);
				type.Methods.Add(bridge);
				method.IsNewSlot = true;
				names.Add(type.Name + "." + method.Name + " (" + method.ReturnType.Name + " -> " + changed.ReturnType.Name + ")");
			}
		}
		if (names.Count > 0)
		{
			log(label + ": " + names.Count + " override(s) of a method whose return type the game changed got the override by the new one: " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
		}
		return names.Count;
	}

	/// <summary>
	/// The game's method that the mod's override was written for, where it has another return type now; null when the
	/// override still overrides something, or when that cannot be told (a base type that is not found, or generic).
	/// </summary>
	private static MethodDefinition FindChangedBase(TypeDefinition type, MethodDefinition method)
	{
		for (TypeReference reference = type.BaseType; reference != null; )
		{
			if (reference is GenericInstanceType)
			{
				return null;
			}
			TypeDefinition t = reference.Resolve();
			if (t == null || t.HasGenericParameters)
			{
				return null;
			}
			MethodDefinition changed = null;
			foreach (MethodDefinition candidate in t.Methods)
			{
				if (!candidate.IsVirtual || candidate.HasGenericParameters || !SameNameAndParameters(candidate, method))
				{
					continue;
				}
				if (candidate.ReturnType.FullName == method.ReturnType.FullName)
				{
					return null; // it overrides this one
				}
				changed = candidate;
			}
			if (changed != null)
			{
				return t.Module.Assembly.Name.Name == "Assembly-CSharp" ? changed : null;
			}
			reference = t.BaseType;
		}
		return null;
	}

	private static bool SameNameAndParameters(MethodDefinition a, MethodDefinition b)
	{
		if (a.Name != b.Name || a.HasThis != b.HasThis || a.Parameters.Count != b.Parameters.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Parameters.Count; i++)
		{
			if (a.Parameters[i].ParameterType.FullName != b.Parameters[i].ParameterType.FullName)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// A mod built against the real Unity may carry attributes the engine's stand-in for Unity does not have (Ariandel
	/// Library's LumiParticle: [UnityEngine.ImageEffectAllowedInSceneView]). Mono passes over an attribute whose type it
	/// cannot find; this runtime throws TypeLoadException from every GetCustomAttributes and IsDefined on the member that
	/// carries it, whatever attribute was asked for: Harmony's PatchAll failed in the mod, and the game's own search for
	/// [DefOf] types (GenTypes.AllTypesWithAttribute) failed for everybody, which ended the loading of all mods. Such
	/// attributes are removed. Only where the assembly the attribute should be in is found and has no such type: an
	/// attribute from an assembly that is not found here (another mod's) stays.
	/// </summary>
	private static int DropAttributesOfMissingTypes(ModuleDefinition module, string label, Action<string> log)
	{
		var missing = new Dictionary<string, bool>();
		var names = new List<string>();
		int dropped = 0;
		Action<ICustomAttributeProvider> clean = delegate(ICustomAttributeProvider provider)
		{
			if (provider == null || !provider.HasCustomAttributes)
			{
				return;
			}
			for (int i = provider.CustomAttributes.Count - 1; i >= 0; i--)
			{
				TypeReference type = provider.CustomAttributes[i].AttributeType;
				if (type == null || !(type.Scope is AssemblyNameReference scope))
				{
					continue;
				}
				string key = scope.Name + "/" + type.FullName;
				if (!missing.TryGetValue(key, out bool gone))
				{
					gone = false;
					try
					{
						// The assembly first: when that is not found, nothing is known about the type.
						if (module.AssemblyResolver.Resolve(scope) != null)
						{
							gone = type.Resolve() == null;
						}
					}
					catch (Exception)
					{
					}
					missing[key] = gone;
					if (gone)
					{
						names.Add(type.FullName);
					}
				}
				if (gone)
				{
					provider.CustomAttributes.RemoveAt(i);
					dropped++;
				}
			}
		};
		clean(module.Assembly);
		clean(module);
		foreach (TypeDefinition type in module.GetTypes())
		{
			clean(type);
			foreach (GenericParameter parameter in type.GenericParameters) clean(parameter);
			foreach (FieldDefinition field in type.Fields) clean(field);
			foreach (PropertyDefinition property in type.Properties) clean(property);
			foreach (EventDefinition evt in type.Events) clean(evt);
			foreach (MethodDefinition method in type.Methods)
			{
				clean(method);
				clean(method.MethodReturnType);
				foreach (ParameterDefinition parameter in method.Parameters) clean(parameter);
				foreach (GenericParameter parameter in method.GenericParameters) clean(parameter);
			}
		}
		if (dropped > 0)
		{
			log(label + ": " + dropped + " attribute(s) of types that do not exist removed (" + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : "") + ")");
		}
		return dropped;
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
