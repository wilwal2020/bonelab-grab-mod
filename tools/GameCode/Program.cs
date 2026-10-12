using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// Reads the game's own code, from what Cpp2IL (bundled with MelonLoader) makes of it.
//
//   fields <cpp2il_out>/<Assembly>.dll <Namespace.Type>...
//       Each field of the types with its offset in the object, to read the disassembly by.
//   method <cpp2il_out> <ISIL dump file> <part of the method's signature>
//       The method's disassembly, with each call's target named where it's known.
//
// <cpp2il_out> is MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out in the game folder. The ISIL dump
// comes from running that Cpp2IL with --output-as isil (see CLAUDE.md).
if (args.Length >= 3 && args[0] == "fields")
    Fields(args[1], args.Skip(2).ToList());
else if (args.Length == 4 && args[0] == "method")
    Method(args[1], args[2], args[3]);
else
    Console.WriteLine("usage: fields <assembly.dll> <Namespace.Type>... | method <cpp2il_out> <isil file> <signature part>");

static void Fields(string path, List<string> wanted)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var names = new Names(md);
    foreach (var th in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(th);
        string full = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
        if (!wanted.Contains(full))
            continue;
        Console.WriteLine("== " + full + (t.BaseType.IsNil ? "" : " : " + names.TypeName(t.BaseType)));
        foreach (var fh in t.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            string offset = "";
            foreach (var ah in f.GetCustomAttributes())
            {
                var a = md.GetCustomAttribute(ah);
                if (AttributeName(md, a).Contains("FieldOffset"))
                    offset = string.Join(", ", a.DecodeValue(new AttributeTypes()).NamedArguments.Select(n => n.Value));
            }
            string isStatic = (f.Attributes & FieldAttributes.Static) != 0 ? "static " : "";
            Console.WriteLine($"  {offset,-8} {isStatic}{f.DecodeSignature(names, null)} {md.GetString(f.Name)}");
        }
    }
}

static void Method(string cpp2ilOut, string dump, string wanted)
{
    Dictionary<ulong, string> addresses = Addresses(cpp2ilOut);
    var call = new Regex(@"(call|jmp)\s+(near ptr\s+)?0*([0-9A-F]+)h");
    bool inMethod = false, inDisassembly = false;
    foreach (string line in File.ReadLines(dump))
    {
        if (line.StartsWith("Method:"))
        {
            inMethod = line.Contains(wanted);
            inDisassembly = false;
            if (inMethod)
                Console.WriteLine(line);
            continue;
        }
        if (!inMethod)
            continue;
        if (line.StartsWith("Disassembly:") || line.StartsWith("ISIL:"))
        {
            inDisassembly = line.StartsWith("Disassembly:");
            continue;
        }
        if (!inDisassembly)
            continue;
        Match m = call.Match(line);
        string name = m.Success && addresses.TryGetValue(Convert.ToUInt64(m.Groups[3].Value, 16), out string n) ? n : null;
        Console.WriteLine(name == null ? line : $"{line}    ; {(name.Length > 120 ? name.Substring(0, 120) + "..." : name)}");
    }
}

// Every method's address in GameAssembly.dll (loaded at 0x180000000), from Cpp2IL's Address attributes. Kept in a file
// next to the tool after the first run, since reading them all takes a while.
static Dictionary<ulong, string> Addresses(string cpp2ilOut)
{
    string cache = Path.Combine(AppContext.BaseDirectory, "addresses.txt");
    var map = new Dictionary<ulong, string>();
    if (File.Exists(cache))
    {
        foreach (string line in File.ReadLines(cache))
        {
            int space = line.IndexOf(' ');
            map[Convert.ToUInt64(line.Substring(0, space), 16)] = line.Substring(space + 1);
        }
        return map;
    }
    foreach (string file in Directory.GetFiles(cpp2ilOut, "*.dll"))
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string type = md.GetString(t.Name);
            if (!t.GetDeclaringType().IsNil)
                type = md.GetString(md.GetTypeDefinition(t.GetDeclaringType()).Name) + "+" + type;
            foreach (var mh in t.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                foreach (var ah in method.GetCustomAttributes())
                {
                    var a = md.GetCustomAttribute(ah);
                    if (!AttributeName(md, a).Contains("Address"))
                        continue;
                    foreach (var arg in a.DecodeValue(new AttributeTypes()).NamedArguments)
                    {
                        if (arg.Name != "RVA" || arg.Value is not string rva || !rva.StartsWith("0x"))
                            continue;
                        ulong address = 0x180000000UL + Convert.ToUInt64(rva.Substring(2), 16);
                        string name = $"{type}.{md.GetString(method.Name)}/{method.GetParameters().Count}";
                        map[address] = map.TryGetValue(address, out string other) && !other.Contains(name) ? other + " | " + name : name;
                    }
                }
            }
        }
    }
    File.WriteAllLines(cache, map.Select(kv => kv.Key.ToString("X") + " " + kv.Value));
    return map;
}

static string AttributeName(MetadataReader md, CustomAttribute a)
{
    if (a.Constructor.Kind == HandleKind.MemberReference)
    {
        var member = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
        if (member.Parent.Kind == HandleKind.TypeReference)
            return md.GetString(md.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
    }
    else if (a.Constructor.Kind == HandleKind.MethodDefinition)
    {
        var method = md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor);
        return md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name);
    }
    return "";
}

sealed class AttributeTypes : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSystemType() => "Type";
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSerializedName(string n) => n;
    public PrimitiveTypeCode GetUnderlyingEnumType(string t) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string t) => t == "Type";
}

sealed class Names : ISignatureTypeProvider<string, object>
{
    private readonly MetadataReader _md;
    public Names(MetadataReader md) => _md = md;
    public string TypeName(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(_md, (TypeDefinitionHandle)h, 0),
        HandleKind.TypeReference => GetTypeFromReference(_md, (TypeReferenceHandle)h, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(_md, null, (TypeSpecificationHandle)h, 0),
        _ => "?",
    };
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(", ", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);
}
