// RefCheck <assembly> <folders searched first, ;-separated> <game copy>...: every type and member the assembly uses from
// the game's own assemblies (everything but the runtime, Harmony and Cecil) must resolve in each game copy.
// Run by host/compat-check.sh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

class RefCheck
{
    static bool Outside(IMetadataScope scope)
    {
        string n = scope is AssemblyNameReference a ? a.Name : scope.Name;
        return n.StartsWith("System") || n == "netstandard" || n == "mscorlib" || n.StartsWith("Microsoft.")
            || n == "0Harmony" || n.StartsWith("Mono.Cecil") || n == "Mono.Posix";
    }

    static int Main(string[] args)
    {
        int bad = 0;
        foreach (string dir in args.Skip(2))
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (string d in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(d);
            foreach (string extra in args[1].Split(';')) resolver.AddSearchDirectory(extra);
            resolver.AddSearchDirectory(dir);
            var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
            int types = 0, members = 0, missing = 0;
            var scopes = new SortedSet<string>();
            foreach (TypeReference t in module.GetTypeReferences())
            {
                if (Outside(t.Scope)) continue;
                types++;
                scopes.Add(t.Scope.Name);
                TypeDefinition def = null;
                try { def = t.Resolve(); } catch (Exception e) { Console.WriteLine("  " + e.Message); }
                if (def == null) { missing++; Console.WriteLine($"  {Path.GetFileName(dir)}: no type {t.FullName} [{t.Scope.Name}]"); }
            }
            foreach (MemberReference m in module.GetMemberReferences())
            {
                TypeReference owner = m.DeclaringType;
                if (owner == null || owner.IsArray || Outside(owner.GetElementType().Scope)) continue;
                members++;
                IMemberDefinition def = null;
                try { def = m is MethodReference mr ? mr.Resolve() : (IMemberDefinition)((FieldReference)m).Resolve(); }
                catch (Exception e) { Console.WriteLine("  " + e.Message); }
                if (def == null) { missing++; Console.WriteLine($"  {Path.GetFileName(dir)}: no member {m.FullName}"); }
            }
            Console.WriteLine($"{Path.GetFileName(args[0])} vs {Path.GetFileName(dir)}: {types} types, {members} members from [{string.Join(", ", scopes)}], {missing} missing");
            bad += missing;
        }
        return bad == 0 ? 0 : 1;
    }
}
