// NameCheck <assembly> <folders searched first, ;-separated> <game copy>... <the current game>: members the assembly looks
// up by name (AccessTools.*, Type.GetMethod/GetField/GetProperty, [HarmonyPatch]) on the game's types, and the copies that
// lack them. Which type a name is looked up on is guessed from the nearest typeof before the string: a name the current
// game lacks as well is a wrong guess, not a missing member, so only the others fail the check. Run by host/compat-check.sh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class NameCheck
{
    static readonly HashSet<string> Lookups = new HashSet<string> {
        "Method", "DeclaredMethod", "Field", "DeclaredField", "Property", "DeclaredProperty", "PropertyGetter",
        "PropertySetter", "DeclaredPropertyGetter", "DeclaredPropertySetter", "FieldRefAccess", "StaticFieldRefAccess",
        "GetMethod", "GetField", "GetProperty", "MethodDelegate", "Constructor", "TypeByName", "Inner" };
    static readonly string[] GameAssemblies = { "Assembly-CSharp", "UnityStubs", "Waylith", "Waylith.UnityCompat", "RimWorld",
        "Brutal.Vulkan", "Brutal.Core.Common", "Brutal.Core.Numerics", "Brutal.Vulkan.Abstractions", "Brutal.Vulkan.Vma" };

    static bool Has(TypeDefinition t, string name)
    {
        for (; t != null; t = t.BaseType == null ? null : Safe(t.BaseType))
        {
            if (name == ".ctor" && t.Methods.Any(m => m.IsConstructor)) return true;
            if (t.Methods.Any(m => m.Name == name) || t.Fields.Any(f => f.Name == name) || t.Properties.Any(p => p.Name == name)
                || t.NestedTypes.Any(n => n.Name == name)) return true;
        }
        return false;
    }

    static TypeDefinition Safe(TypeReference r) { try { return r.Resolve(); } catch { return null; } }

    static int Main(string[] args)
    {
        var copies = args.Skip(2).ToArray();
        var lookups = new SortedSet<string>();   // "Type::name" or "Type" (by full name)
        var selfResolver = new DefaultAssemblyResolver();
        foreach (string extra in args[1].Split(';')) selfResolver.AddSearchDirectory(extra);
        selfResolver.AddSearchDirectory(args[2]);
        var self = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = selfResolver });
        foreach (TypeDefinition type in self.GetTypes())
        {
            foreach (var attr in type.CustomAttributes.Concat(type.Methods.SelectMany(m => m.CustomAttributes)))
            {
                if (attr.AttributeType.Name != "HarmonyPatch") continue;
                var a = attr.ConstructorArguments;
                if (a.Count >= 2 && a[0].Value is TypeReference pt && a[1].Value is string pn) lookups.Add(pt.FullName + "::" + pn);
            }
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                var code = method.Body.Instructions;
                for (int i = 0; i < code.Count; i++)
                {
                    if (!(code[i].Operand is MethodReference call) || !Lookups.Contains(call.Name)) continue;
                    string owner = call.DeclaringType.FullName;
                    if (owner != "HarmonyLib.AccessTools" && owner != "System.Type") continue;
                    // the nearest string before the call, and the nearest typeof before that string
                    string name = null; TypeReference on = null;
                    for (int j = i - 1; j >= Math.Max(0, i - 12); j--)
                    {
                        if (name == null && code[j].OpCode == OpCodes.Ldstr) name = (string)code[j].Operand;
                        else if (name != null && code[j].OpCode == OpCodes.Ldtoken && code[j].Operand is TypeReference t) { on = t; break; }
                    }
                    if (name == null) continue;
                    if (call.Name == "TypeByName") lookups.Add(name);
                    else if (on != null) lookups.Add(on.FullName + "::" + name);
                    else if (name.Contains(':')) lookups.Add(name.Replace(":", "::").Replace("::::", "::"));
                }
            }
        }
        var missing = new Dictionary<string, List<string>>();
        int checkedCount = 0;
        foreach (string dir in copies)
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (string d in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(d);
            foreach (string extra in args[1].Split(';')) resolver.AddSearchDirectory(extra);
            resolver.AddSearchDirectory(dir);
            var mods = GameAssemblies.Where(n => File.Exists(Path.Combine(dir, n + ".dll")))
                .Select(n => ModuleDefinition.ReadModule(Path.Combine(dir, n + ".dll"), new ReaderParameters { AssemblyResolver = resolver })).ToList();
            TypeDefinition Find(string full)
            {
                foreach (var m in mods)
                {
                    var t = m.GetType(full.Replace('+', '/'));
                    if (t != null) return t;
                }
                return null;
            }
            var gone = new List<string>();
            checkedCount = 0;
            foreach (string l in lookups)
            {
                int sep = l.IndexOf("::", StringComparison.Ordinal);
                string typeName = sep < 0 ? l : l.Substring(0, sep);
                var t = Find(typeName);
                if (t == null) continue;          // not a game type (the runtime, Harmony, a mod): not this check
                checkedCount++;
                if (sep >= 0 && !Has(t, l.Substring(sep + 2))) gone.Add(l);
            }
            missing[Path.GetFileName(dir)] = gone;
            Console.WriteLine($"{Path.GetFileName(args[0])} vs {Path.GetFileName(dir)}: {checkedCount} lookups on game types, {gone.Count} not found");
        }
        var all = missing.Values.SelectMany(x => x).Distinct().OrderBy(x => x);
        foreach (string l in all)
            Console.WriteLine("  not in " + string.Join(", ", missing.Where(kv => kv.Value.Contains(l)).Select(kv => kv.Key)) + ": " + l);
        string current = Path.GetFileName(copies[copies.Length - 1]);
        return all.Any(l => !missing[current].Contains(l)) ? 1 : 0;
    }
}
