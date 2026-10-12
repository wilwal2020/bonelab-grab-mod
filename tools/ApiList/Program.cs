using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Lists the public members of chosen types in a .NET assembly with their types, by reading its metadata only.
// Usage: apilist <assembly> <Namespace.Type>... ; a type name ending in * lists every type starting with it,
// and "?word" lists type names containing word.
string path = args[0];
var wanted = args.Skip(1).ToList();
using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var provider = new Names(md);

foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    string full = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
    if (wanted.Any(w => w.StartsWith("+")))
    {
        foreach (var ih in t.GetInterfaceImplementations())
        {
            string iname = provider.TypeName(md.GetInterfaceImplementation(ih).Interface);
            if (wanted.Contains("+" + iname)) Console.WriteLine(full + " implements " + iname);
        }
        continue;
    }
    if (wanted.Any(w => w.StartsWith("?")))
    {
        foreach (var w in wanted.Where(w => w.StartsWith("?")))
            if (full.Contains(w.Substring(1), StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(full);
        continue;
    }
    if (!wanted.Any(w => w.EndsWith("*") ? full.StartsWith(w.TrimEnd('*')) : w == full))
        continue;
    string baseName = t.BaseType.IsNil ? "" : " : " + provider.TypeName(t.BaseType);
    Console.WriteLine("== " + full + baseName);
    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        if ((f.Attributes & FieldAttributes.Public) == 0) continue;
        string name = md.GetString(f.Name);
        if (name.StartsWith("NativeFieldInfoPtr") || name.StartsWith("NativeMethodInfoPtr")) continue;
        Console.WriteLine($"  field {f.DecodeSignature(provider, null)} {name}");
    }
    foreach (var ph in t.GetProperties())
    {
        var p = md.GetPropertyDefinition(ph);
        var sig = p.DecodeSignature(provider, null);
        Console.WriteLine($"  prop {sig.ReturnType} {md.GetString(p.Name)}");
    }
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if ((m.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
        string name = md.GetString(m.Name);
        if (name.StartsWith("get_") || name.StartsWith("set_") || name == ".cctor") continue;
        var sig = m.DecodeSignature(provider, null);
        var names = m.GetParameters().Select(p => md.GetParameter(p)).Where(p => p.SequenceNumber > 0)
            .OrderBy(p => p.SequenceNumber).Select(p => md.GetString(p.Name)).ToList();
        var ps = sig.ParameterTypes.Select((pt, i) => pt + " " + (i < names.Count ? names[i] : "?"));
        string isStatic = (m.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
        Console.WriteLine($"  {isStatic}{sig.ReturnType} {name}({string.Join(", ", ps)})");
    }
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
