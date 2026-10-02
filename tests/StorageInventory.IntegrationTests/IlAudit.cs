using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// A-25 by IL inspection (<c>System.Reflection.Metadata</c>, no package): which methods of an assembly call which members, read from
/// the compiled method bodies, so that the rule holds however the source was spelled. The compiler's own wrappers are folded back
/// into the method the author wrote: an async method's state machine, a lambda's closure and a local function all belong to the
/// method whose name they carry.
/// <para>The rule (A-25 a): a method that reaches a mutation needs authority. A <i>mutation</i> is a call to a leased operation (a
/// first-party method that takes a <c>MutationLease</c> parameter, other than the interlock's own authority methods) or to a
/// primitive (create-new, directory creation, the lock open, <c>File.Move</c>, and a command or connection of SQLite). A method has
/// <i>authority</i> if it takes a <c>MutationLease</c>, or obtains one from the interlock, or is an instance method of a
/// lease-bound type (one that holds a <c>MutationLease</c> field, set by a constructor that took it), or is private and called only
/// by methods that have authority. The read-only operations of A-25 (d) are an explicit allow-list.</para>
/// <para>Part (c): a lease is constructed (<c>newobj</c>, <c>initobj</c>) only inside the interlock type.</para>
/// </summary>
internal static class IlAudit
{
    internal const string MutationLeaseType = "StorageInventory.Library.MutationLease";
    internal const string ObservationLeaseType = "StorageInventory.Library.ObservationLease";
    internal const string InterlockType = "StorageInventory.Library.LibraryInterlock";

    /// <summary>One method as the compiler produced it.</summary>
    internal sealed class Physical
    {
        public required string Type { get; init; }          // full name, nested types as Outer+Inner
        public required string OuterType { get; init; }      // the outermost type
        public required string Name { get; init; }
        public required List<string> ParameterTypes { get; init; }
        public required bool IsPrivateLike { get; init; }
        public required bool IsStatic { get; init; }
        public required bool IsGenerated { get; init; }
        public required List<string> Calls { get; init; }    // "Type::Name"
        public required List<string> InitTypes { get; init; }
        public string Key => $"{Type}::{Name}";
    }

    /// <summary>A method as its author wrote it: the real method plus every compiler-generated body folded into it.</summary>
    internal sealed class Logical
    {
        public required string Type { get; init; }
        public required string Name { get; init; }
        public List<Physical> Parts { get; } = [];
        public string Key => $"{Type}::{Name}";
        public IEnumerable<Physical> Declared => Parts.Where(p => !p.IsGenerated);
        public bool TakesLease => Declared.Any() && Declared.All(p => p.ParameterTypes.Contains(MutationLeaseType));
        public bool IsPrivate => Declared.Any() && Declared.All(p => p.IsPrivateLike);
        public IEnumerable<string> Calls => Parts.SelectMany(p => p.Calls);
        public IEnumerable<string> InitTypes => Parts.SelectMany(p => p.InitTypes);
    }

    internal sealed class Model
    {
        public required Dictionary<string, Logical> Methods { get; init; }
        public required Dictionary<string, bool> LeaseBoundTypes { get; init; }
    }

    // ---------------------------------------------------------------- reading an assembly

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    internal static Model Read(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var provider = new NameProvider(md);
        var physicals = new List<Physical>();
        var leaseBound = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var fullName = FullName(md, type);
            var outer = FullName(md, Outermost(md, type));
            // a lease-bound type holds a MutationLease in an instance field
            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) != 0) continue;
                if (field.DecodeSignature(provider, null) == MutationLeaseType) leaseBound[fullName] = true;
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(provider, null);
                var calls = new List<string>();
                var inits = new List<string>();
                if (method.RelativeVirtualAddress != 0)
                {
                    var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                    Decode(md, provider, il, calls, inits);
                }
                var name = md.GetString(method.Name);
                var attributes = method.Attributes;
                physicals.Add(new Physical
                {
                    Type = fullName,
                    OuterType = outer,
                    Name = name,
                    ParameterTypes = [.. signature.ParameterTypes],
                    IsPrivateLike = (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Private or MethodAttributes.PrivateScope,
                    IsStatic = (attributes & MethodAttributes.Static) != 0,
                    IsGenerated = IsCompilerGenerated(fullName, name),
                    Calls = calls,
                    InitTypes = inits,
                });
            }
        }

        // Fold the compiler's bodies into the methods that carry their names.
        var logical = new Dictionary<string, Logical>(StringComparer.Ordinal);
        foreach (var p in physicals)
        {
            var (type, name) = OwnerOf(p);
            var key = $"{type}::{name}";
            if (!logical.TryGetValue(key, out var l)) logical[key] = l = new Logical { Type = type, Name = name };
            l.Parts.Add(p);
        }
        return new Model { Methods = logical, LeaseBoundTypes = leaseBound };
    }

    private static bool IsCompilerGenerated(string type, string method) =>
        type.Contains('<', StringComparison.Ordinal) || method.StartsWith('<') || method.Contains('<', StringComparison.Ordinal);

    /// <summary>The (outermost user type, method name) a physical method belongs to: an async state machine's <c>MoveNext</c>
    /// belongs to the method it was generated from, a closure's lambda to the method that holds it, a local function to its host.</summary>
    private static (string Type, string Name) OwnerOf(Physical p)
    {
        if (!p.IsGenerated) return (p.Type, p.Name);
        // state machine: type "Outer+<Method>d__12"
        var nested = p.Type[(p.Type.LastIndexOf('+') + 1)..];
        if (nested.StartsWith('<') && nested.Contains(">d__", StringComparison.Ordinal))
        {
            return (p.OuterType, nested[1..nested.IndexOf('>')]);
        }
        // lambda or local function: method name "<Host>b__0_0" or "<Host>g__Local|1_0" (on the type itself or on a closure class)
        if (p.Name.StartsWith('<') && p.Name.Contains('>', StringComparison.Ordinal))
        {
            return (p.OuterType, p.Name[1..p.Name.IndexOf('>')]);
        }
        // closure constructors and cache classes: attribute to the outer type's static initialiser
        return (p.OuterType, "<generated>");
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(md, md.GetTypeDefinition(declaring)) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static TypeDefinition Outermost(MetadataReader md, TypeDefinition type)
    {
        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? type : Outermost(md, md.GetTypeDefinition(declaring));
    }

    private static void Decode(MetadataReader md, NameProvider provider, byte[] il, List<string> calls, List<string> inits)
    {
        var i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            var operand = Operands[code];
            var size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
            // call 0x28, callvirt 0x6F, newobj 0x73, jmp 0x27, ldftn 0xFE06, ldvirtftn 0xFE07, initobj 0xFE15
            if (code is 0x28 or 0x6F or 0x73 or 0x27 or unchecked((short)0xFE06) or unchecked((short)0xFE07))
            {
                var token = BitConverter.ToInt32(il, i);
                var name = provider.MethodName(MetadataTokens.EntityHandle(token));
                if (name is not null) calls.Add(name);
            }
            else if (code == unchecked((short)0xFE15))
            {
                var token = BitConverter.ToInt32(il, i);
                var name = provider.TypeName(MetadataTokens.EntityHandle(token));
                if (name is not null) inits.Add(name);
            }
            i += size;
        }
    }

    /// <summary>Names types and members from metadata tokens, and decodes signatures to type names.</summary>
    private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string? MethodName(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return $"{FullName(md, md.GetTypeDefinition(method.GetDeclaringType()))}::{md.GetString(method.Name)}";
                }
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                    var parent = reference.Parent.Kind switch
                    {
                        HandleKind.TypeReference => TypeName(reference.Parent),
                        HandleKind.TypeDefinition => TypeName(reference.Parent),
                        HandleKind.TypeSpecification => TypeName(reference.Parent),
                        _ => null,
                    };
                    return parent is null ? null : $"{parent}::{md.GetString(reference.Name)}";
                }
                case HandleKind.MethodSpecification:
                    return MethodName(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                default:
                    return null;
            }
        }

        public string? TypeName(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)handle));
                case HandleKind.TypeReference:
                {
                    var reference = md.GetTypeReference((TypeReferenceHandle)handle);
                    var name = md.GetString(reference.Name);
                    if (reference.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(reference.ResolutionScope) + "+" + name;
                    var ns = md.GetString(reference.Namespace);
                    return ns.Length == 0 ? name : ns + "." + name;
                }
                case HandleKind.TypeSpecification:
                {
                    var text = md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                    var cut = text.IndexOf('<', StringComparison.Ordinal);
                    return cut < 0 ? text : text[..cut];
                }
                default:
                    return null;
            }
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(handle) ?? "?";
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(handle) ?? "?";
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => TypeName(handle) ?? "?";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }

    // ---------------------------------------------------------------- the rules

    /// <summary>BCL and SQLite members that create, rename, delete or write (the primitives), as "Type::Name".</summary>
    private static readonly HashSet<string> Primitives =
    [
        "System.IO.Directory::CreateDirectory", "System.IO.File::Move", "System.IO.File::Delete",
        "System.IO.FileStream::.ctor",
        "Microsoft.Data.Sqlite.SqliteConnection::Open",
        "Microsoft.Data.Sqlite.SqliteCommand::ExecuteNonQuery", "Microsoft.Data.Sqlite.SqliteCommand::ExecuteScalar", "Microsoft.Data.Sqlite.SqliteCommand::ExecuteReader",
    ];

    /// <summary>A-25 (d): read-only operations deliberately not covered. <c>ReaderConnection</c> only ever wraps a
    /// <c>Mode=ReadOnly</c> connection (A-06); the others open a read-only <c>FileStream</c> or a fresh connection only to ask for
    /// the engine's version.</summary>
    internal static readonly string[] ReadOnlyAllowList =
    [
        "StorageInventory.Library.ReaderConnection::*",
        "StorageInventory.Library.LibraryStore::ReadHeader",
        "StorageInventory.Library.LibraryDatabase::OpenReader",
        "StorageInventory.Library.LibraryDatabase::AssertEngine",
    ];

    /// <summary>The interlock's authority: calls to these obtain a lease, so the caller has authority.</summary>
    private static readonly HashSet<string> LeaseProducers =
    [
        InterlockType + "::TakeStartupLease", InterlockType + "::TryBeginMutation", InterlockType + "::HandOffToSave",
        ObservationLeaseType + "::HandOffToSave",
    ];

    private static bool IsAllowListed(string key) => ReadOnlyAllowList.Any(a => a.EndsWith("::*", StringComparison.Ordinal) ? key.StartsWith(a[..^1], StringComparison.Ordinal) : key == a);

    private static bool IsAuthorityType(string type) => type is InterlockType or MutationLeaseType or ObservationLeaseType || type.StartsWith(InterlockType + "+", StringComparison.Ordinal);

    /// <summary>The leased operations of a model: methods that take a <c>MutationLease</c>, outside the interlock's own authority.</summary>
    internal static HashSet<string> LeasedOperations(Model model) =>
        [.. model.Methods.Values.Where(m => m.TakesLease && !IsAuthorityType(m.Type)).Select(m => m.Key)];

    /// <summary>A-25 (a). Returns a description of every method that reaches a mutation without authority. <paramref name="inScope"/>
    /// selects which types' methods are judged (so a negative self-test can judge only its fixtures);
    /// <paramref name="leasedOperations"/> is the set of leased operations the calls are matched against.</summary>
    internal static List<string> MutationViolations(Model model, Func<string, bool> inScope, HashSet<string> leasedOperations)
    {
        var judged = model.Methods.Values.Where(m => inScope(m.Type) && m.Name != "<generated>").ToList();

        bool NeedsAuthority(Logical m) => m.Calls.Any(c => leasedOperations.Contains(c) || Primitives.Contains(c));
        bool HasAuthority(Logical m) => m.TakesLease || m.Calls.Any(LeaseProducers.Contains) || (!m.Declared.All(p => p.IsStatic) && model.LeaseBoundTypes.ContainsKey(m.Type));

        var allowed = new HashSet<string>(model.Methods.Values.Where(HasAuthority).Select(m => m.Key), StringComparer.Ordinal);
        var callers = new Dictionary<string, List<Logical>>(StringComparer.Ordinal);
        foreach (var m in model.Methods.Values)
        {
            foreach (var call in m.Calls.Distinct())
            {
                if (!model.Methods.ContainsKey(call)) continue;
                if (!callers.TryGetValue(call, out var list)) callers[call] = list = [];
                list.Add(m);
            }
        }
        // a private helper is allowed when every caller is allowed and belongs to its own type
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in model.Methods.Values)
            {
                if (allowed.Contains(m.Key) || !m.IsPrivate) continue;
                if (callers.TryGetValue(m.Key, out var who) && who.Count > 0 && who.All(c => c.Type == m.Type && (allowed.Contains(c.Key) || c.Key == m.Key)))
                {
                    allowed.Add(m.Key);
                    changed = true;
                }
            }
        }

        var violations = new List<string>();
        foreach (var m in judged)
        {
            if (!NeedsAuthority(m) || allowed.Contains(m.Key) || IsAllowListed(m.Key)) continue;
            var reached = m.Calls.Where(c => leasedOperations.Contains(c) || Primitives.Contains(c)).Distinct().OrderBy(c => c, StringComparer.Ordinal);
            violations.Add($"{m.Key} reaches {string.Join(", ", reached)} without a MutationLease");
        }
        return violations;
    }

    /// <summary>A-25 (c): a lease is constructed (<c>newobj</c> or <c>initobj</c>, which is what <c>default</c> compiles to) only inside
    /// the interlock type.</summary>
    internal static List<string> ConstructionViolations(Model model, Func<string, bool> inScope)
    {
        var violations = new List<string>();
        foreach (var m in model.Methods.Values.Where(m => inScope(m.Type)))
        {
            if (m.Type == InterlockType || m.Type.StartsWith(InterlockType + "+", StringComparison.Ordinal)) continue;
            foreach (var call in m.Calls.Where(c => c is MutationLeaseType + "::.ctor" or ObservationLeaseType + "::.ctor").Distinct())
            {
                violations.Add($"{m.Key} constructs a lease ({call})");
            }
            foreach (var type in m.InitTypes.Where(t => t is MutationLeaseType or ObservationLeaseType).Distinct())
            {
                violations.Add($"{m.Key} creates a default value of {type}");
            }
        }
        return violations;
    }

    /// <summary>A-25 (c): no public or protected member of a visible type takes, returns or holds a lease.</summary>
    internal static List<string> PublicLeaseExposure(Assembly assembly, Func<Type, bool>? isLeaseType = null)
    {
        var violations = new List<string>();
        isLeaseType ??= t => t.FullName is MutationLeaseType or ObservationLeaseType;
        bool IsLease(Type t) => isLeaseType(t) || (t.HasElementType && IsLease(t.GetElementType()!))
            || (t.IsGenericType && t.GetGenericArguments().Any(IsLease));
        foreach (var type in assembly.GetExportedTypes())
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags).Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            {
                if (IsLease(method.ReturnType) || method.GetParameters().Any(p => IsLease(p.ParameterType))) violations.Add($"{type.FullName}.{method.Name} takes or returns a lease");
            }
            foreach (var ctor in type.GetConstructors(flags).Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly))
            {
                if (ctor.GetParameters().Any(p => IsLease(p.ParameterType))) violations.Add($"{type.FullName} constructor takes a lease");
            }
            foreach (var field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
            {
                if (IsLease(field.FieldType)) violations.Add($"{type.FullName}.{field.Name} holds a lease");
            }
            foreach (var property in type.GetProperties(flags))
            {
                if (IsLease(property.PropertyType)) violations.Add($"{type.FullName}.{property.Name} is a lease");
            }
        }
        return violations;
    }
}
