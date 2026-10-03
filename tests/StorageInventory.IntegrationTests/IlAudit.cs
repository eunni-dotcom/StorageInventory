using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// A-25 by IL inspection (<c>System.Reflection.Metadata</c>, no package): which methods call which members, read from the compiled
/// method bodies, so that the rule holds however the source was spelled.
/// <para><b>Part (a), exactly as frozen in §16.4:</b> every method that calls a mutation primitive takes a <c>MutationLease</c>
/// parameter, or is private and called only from methods of its own type that take one. There is no other authority: no
/// "lease-bound type" (a method does not borrow authority from a lease its object holds), no "lease producer" (a method does not
/// borrow it from having asked the interlock for one) and no transitive authority (the private helper's callers must themselves
/// take a lease: one hop, not a chain).</para>
/// <para>What counts as reaching a mutation: a call (<c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>jmp</c>, <c>ldftn</c>,
/// <c>ldvirtftn</c>) to a BCL or SQLite primitive (<see cref="IsPrimitive"/>); to a first-party method that takes a
/// <c>MutationLease</c> outside the interlock's own authority types (stricter than the literal list, and intended); through
/// interface or virtual dispatch, to any implementer or override in the model that itself directly reaches one; and any
/// <c>calli</c> (an indirect call cannot be resolved, so it counts as reaching a mutation: only a method with authority may make one).
/// Callees are matched by full signature (declaring type, name and parameter types), so an overload cannot hide behind another.</para>
/// <para>The compiler's wrappers are folded back into the method the author wrote: an async method's state machine, a lambda and a
/// local function belong to the method that holds them. A lambda or local function that itself takes a lease is its own unit and has
/// authority for its own calls; one that merely captures a lease is part of its host, which then needs the parameter.</para>
/// <para>Part (c): a lease is constructed (<c>newobj</c>, <c>initobj</c>) only inside the interlock type.</para>
/// </summary>
internal static class IlAudit
{
    internal const string MutationLeaseType = "StorageInventory.Library.MutationLease";
    internal const string ObservationLeaseType = "StorageInventory.Library.ObservationLease";
    internal const string InterlockType = "StorageInventory.Library.LibraryInterlock";
    internal const string LibraryAssembly = "StorageInventory.Library";

    internal enum CallKind { Call, CallVirt, Newobj, Jmp, Ldftn, Ldvirtftn, Calli }

    /// <summary>One call instruction: the callee as the metadata names it, and where it sits in the method body.</summary>
    internal sealed record CallSite(string Type, string Name, IReadOnlyList<string> Parameters, CallKind Kind, int Offset, string? Constrained)
    {
        internal string Short => Type + "::" + Name;

        internal string Key => $"{Type}::{Name}({string.Join(",", Parameters)})";

        internal bool Dispatches => Kind is CallKind.CallVirt or CallKind.Ldvirtftn;
    }

    /// <summary>One method body as the compiler produced it.</summary>
    internal sealed class Physical
    {
        public required string Assembly { get; init; }
        public required string Type { get; init; }           // full name, nested types as Outer+Inner
        public required string OwnerType { get; init; }      // the type with compiler-generated segments cut off (a closure belongs to its outer type)
        public required string Name { get; init; }
        public required IReadOnlyList<string> ParameterTypes { get; init; }
        public required bool IsPrivateLike { get; init; }
        public required bool IsStatic { get; init; }
        public required bool IsVirtual { get; init; }
        public required bool IsGenerated { get; init; }
        public bool IsExplicitImpl { get; set; }
        public required List<CallSite> Calls { get; init; }
        public required List<(string Type, int Offset)> InitTypes { get; init; }
        public required HashSet<string> Touched { get; init; }   // declaring types of the fields, types and methods the body names
        public string Key => $"{Type}::{Name}({string.Join(",", ParameterTypes)})";
        public bool TakesLease => ParameterTypes.Any(IsLeaseParameter);
    }

    private static bool IsLeaseParameter(string type) => type is MutationLeaseType or MutationLeaseType + "&";

    /// <summary>A method as its author wrote it (or a lambda or local function that takes a lease of its own): the entry body plus
    /// every compiler-generated body folded into it.</summary>
    internal sealed class Unit
    {
        public required Physical Entry { get; init; }
        public List<Physical> Parts { get; } = [];
        public string Key => Entry.Key;
        public string Type => Entry.OwnerType;
        public string Name => Entry.Name;
        public string Assembly => Entry.Assembly;
        public bool TakesLease => Entry.TakesLease;
        public bool IsPrivate => Entry.IsPrivateLike && !Entry.IsExplicitImpl;
        public IEnumerable<CallSite> Calls => Parts.SelectMany(p => p.Calls);
    }

    internal sealed class Model
    {
        public required Dictionary<string, Unit> Methods { get; init; }
        public required List<Physical> Physicals { get; init; }
        public required Dictionary<string, bool> LeaseBoundTypes { get; init; }
        /// <summary>Methods that implement or override a declared method, by the declared method's key and by "Type::Name/arity".</summary>
        public required Dictionary<string, List<Unit>> Overriders { get; init; }
        public required Dictionary<string, HashSet<string>> SuperTypes { get; init; }
        public IEnumerable<Unit> Units => Methods.Values;
    }

    /// <summary>Which of the frozen rule's limits an audit run applies. The default is the frozen wording and nothing else. The
    /// three relaxations exist only so that a self-test can show what the earlier, relaxed audit accepted (reviewer mutants R-19, R-20).</summary>
    internal sealed record Options
    {
        /// <summary>REJECTED by the review (C4-H01): an instance method of a type that holds a lease has authority.</summary>
        internal bool LeaseBoundTypeExemption { get; init; }

        /// <summary>REJECTED by the review (C4-H01): a method that obtains a lease from the interlock has authority.</summary>
        internal bool LeaseProducerExemption { get; init; }

        /// <summary>REJECTED by the review (C4-H01): a private helper has authority when all its callers have (a chain).</summary>
        internal bool TransitivePrivateAuthority { get; init; }

        internal static readonly Options Frozen = new();
        internal static readonly Options Relaxed = new() { LeaseBoundTypeExemption = true, LeaseProducerExemption = true, TransitivePrivateAuthority = true };
    }

    /// <summary>A rejected method, with the specific rule that rejected it.</summary>
    internal sealed record Violation(string Method, string Rule, string Detail)
    {
        public override string ToString() => $"{Method} [{Rule}] {Detail}";
    }

    internal const string RuleNotPrivate = "A25a/not-private";
    internal const string RuleCallerWithoutLease = "A25a/caller-without-lease";
    internal const string RuleForeignCaller = "A25a/caller-of-another-type";
    internal const string RuleUncalled = "A25a/private-and-uncalled";
    internal const string RuleIndirectCall = "A25a/indirect-call";
    internal const string RuleConstructsLease = "A25c/constructs-lease";
    internal const string RuleDefaultLease = "A25c/default-lease";
    internal const string RuleFileSystemOutsideStore = "C4-M17/file-system-outside-LibraryStore";
    internal const string RuleBeforeLock = "C4-M17/member-touched-before-the-lock";
    internal const string RuleLockless = "C4-M17/member-touched-without-the-lock";

    // ---------------------------------------------------------------- reading assemblies

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    internal static Model Read(params string[] assemblyPaths)
    {
        var physicals = new List<Physical>();
        var leaseBound = new Dictionary<string, bool>(StringComparer.Ordinal);
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var explicitImpls = new List<(string DeclarationKey, string DeclarationShort, int Arity, Physical Body)>();
        foreach (var path in assemblyPaths) ReadOne(path, physicals, leaseBound, direct, explicitImpls);

        // Fold the compiler's bodies into the units they belong to.
        var units = new Dictionary<string, Unit>(StringComparer.Ordinal);
        var unitOf = new Dictionary<Physical, List<Unit>>();
        var byKey = physicals.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var referrers = new Dictionary<string, List<Physical>>(StringComparer.Ordinal);
        var touchers = new Dictionary<string, List<Physical>>(StringComparer.Ordinal);
        foreach (var p in physicals)
        {
            foreach (var call in p.Calls.Where(c => c.Kind != CallKind.Calli).Select(c => c.Key).Distinct(StringComparer.Ordinal))
            {
                if (!referrers.TryGetValue(call, out var list)) referrers[call] = list = [];
                list.Add(p);
            }
            foreach (var type in p.Touched)
            {
                if (type == p.Type) continue;
                if (!touchers.TryGetValue(type, out var list)) touchers[type] = list = [];
                list.Add(p);
            }
        }
        Unit UnitFor(Physical entry)
        {
            if (!units.TryGetValue(entry.Key, out var unit)) units[entry.Key] = unit = new Unit { Entry = entry };
            return unit;
        }
        List<Unit> Resolve(Physical p, HashSet<Physical> visiting)
        {
            if (unitOf.TryGetValue(p, out var known)) return known;
            if (!visiting.Add(p)) return [];
            List<Unit> result;
            if (!p.IsGenerated) result = [UnitFor(p)];
            else if (StateMachineOrigin(p, byKey, physicals) is { Count: > 0 } origins) result = origins.SelectMany(o => Resolve(o, visiting)).Distinct().ToList();
            else if (p.TakesLease && !IsStateMachine(p)) result = [UnitFor(p)];
            else
            {
                var hostName = HostName(p);
                var refs = new List<Physical>();
                if (referrers.TryGetValue(p.Key, out var byCall)) refs.AddRange(byCall);
                if (IsGeneratedType(p.Type) && touchers.TryGetValue(p.Type, out var byType)) refs.AddRange(byType);
                refs = refs.Where(r => r != p && r.Type != p.Type && OuterOf(r.Type) == OuterOf(p.Type)).Distinct().ToList();
                if (refs.Count == 0 && hostName is not null) refs = physicals.Where(h => !h.IsGenerated && h.OwnerType == p.OwnerType && h.Name == hostName).ToList();
                result = refs.Count == 0 ? [UnitFor(p)] : refs.SelectMany(r => Resolve(r, visiting)).Distinct().ToList();
            }
            visiting.Remove(p);
            unitOf[p] = result;
            return result;
        }
        foreach (var p in physicals)
        {
            foreach (var unit in Resolve(p, [])) if (!unit.Parts.Contains(p)) unit.Parts.Add(p);
        }

        // Implementers and overriders: by the declared method's full key, and by name and arity (generic interfaces).
        var overriders = new Dictionary<string, List<Unit>>(StringComparer.Ordinal);
        void AddOverrider(string key, Unit unit)
        {
            if (!overriders.TryGetValue(key, out var list)) overriders[key] = list = [];
            if (!list.Contains(unit)) list.Add(unit);
        }
        var supers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Closure(string type)
        {
            if (supers.TryGetValue(type, out var known)) return known;
            var all = new HashSet<string>(StringComparer.Ordinal);
            supers[type] = all;   // cycle guard
            if (direct.TryGetValue(type, out var parents))
            {
                foreach (var parent in parents)
                {
                    all.Add(parent);
                    all.UnionWith(Closure(parent));
                }
            }
            return all;
        }
        foreach (var type in direct.Keys.ToList()) Closure(type);
        foreach (var p in physicals.Where(p => !p.IsGenerated && !p.IsStatic && p.IsVirtual))
        {
            var unit = units[p.Key];
            foreach (var super in Closure(p.Type))
            {
                AddOverrider($"{super}::{p.Name}({string.Join(",", p.ParameterTypes)})", unit);
                AddOverrider($"{super}::{p.Name}/{p.ParameterTypes.Count}", unit);
            }
        }
        foreach (var (declarationKey, declarationShort, arity, body) in explicitImpls)
        {
            if (!units.TryGetValue(body.Key, out var unit)) continue;
            AddOverrider(declarationKey, unit);
            AddOverrider($"{declarationShort}/{arity}", unit);
        }
        return new Model { Methods = units, Physicals = physicals, LeaseBoundTypes = leaseBound, Overriders = overriders, SuperTypes = supers };
    }

    private static readonly Regex StateMachineType = new(@"^<(?<m>.+)>d(__\d+)?(`\d+)?$", RegexOptions.Compiled);

    private static bool IsStateMachine(Physical p) => StateMachineType.IsMatch(Last(p.Type));

    private static string Last(string type) => type[(type.LastIndexOf('+') + 1)..];

    private static string Parent(string type) => type.Contains('+', StringComparison.Ordinal) ? type[..type.LastIndexOf('+')] : "";

    private static string OuterOf(string type) => type.Contains('+', StringComparison.Ordinal) ? type[..type.IndexOf('+', StringComparison.Ordinal)] : type;

    private static bool IsGeneratedType(string type) => type.Split('+').Any(s => s.StartsWith('<'));

    /// <summary>The type with every generated nested segment (<c>&lt;&gt;c__DisplayClass0_0</c>, <c>&lt;Run&gt;d__3</c>) cut off.</summary>
    private static string OwnerOf(string type)
    {
        var parts = type.Split('+');
        var kept = parts.TakeWhile(s => !s.StartsWith('<')).ToArray();
        return kept.Length == 0 ? parts[0] : string.Join("+", kept);
    }

    /// <summary>The author's method an async or iterator state machine's body came from: the method named in the state machine's type
    /// name, on the type that holds the state machine (and that names the state machine's type in its body).</summary>
    private static List<Physical>? StateMachineOrigin(Physical p, Dictionary<string, List<Physical>> byKey, List<Physical> all)
    {
        var match = StateMachineType.Match(Last(p.Type));
        if (!match.Success) return null;
        var parent = Parent(p.Type);
        var name = match.Groups["m"].Value;
        var candidates = all.Where(o => o.Type == parent && o.Name == name).ToList();
        if (candidates.Count > 1)
        {
            var touching = candidates.Where(c => c.Touched.Contains(p.Type)).ToList();
            if (touching.Count > 0) candidates = touching;
        }
        return candidates;
    }

    /// <summary>The method a lambda (<c>&lt;Host&gt;b__0_0</c>) or local function (<c>&lt;Host&gt;g__Local|0_0</c>) was written in.</summary>
    private static string? HostName(Physical p) =>
        p.Name.StartsWith('<') && p.Name.Contains('>', StringComparison.Ordinal) ? p.Name[1..p.Name.IndexOf('>')] : null;

    private static void ReadOne(string assemblyPath, List<Physical> physicals, Dictionary<string, bool> leaseBound, Dictionary<string, HashSet<string>> direct,
        List<(string, string, int, Physical)> explicitImpls)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var provider = new NameProvider(md);
        var assembly = md.GetString(md.GetAssemblyDefinition().Name);
        var local = new Dictionary<MethodDefinitionHandle, Physical>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var fullName = FullName(md, type);
            var supers = new HashSet<string>(StringComparer.Ordinal);
            if (!type.BaseType.IsNil && provider.TypeName(type.BaseType) is { } baseName) supers.Add(baseName);
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                if (provider.TypeName(md.GetInterfaceImplementation(implementation).Interface) is { } interfaceName) supers.Add(interfaceName);
            }
            direct[fullName] = supers;

            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) != 0) continue;
                if (IsLeaseParameter(field.DecodeSignature(provider, null))) leaseBound[fullName] = true;
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(provider, null);
                var calls = new List<CallSite>();
                var inits = new List<(string, int)>();
                var touched = new HashSet<string>(StringComparer.Ordinal);
                if (method.RelativeVirtualAddress != 0)
                {
                    var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                    Decode(provider, il, calls, inits, touched);
                }
                var name = md.GetString(method.Name);
                var attributes = method.Attributes;
                var physical = new Physical
                {
                    Assembly = assembly,
                    Type = fullName,
                    OwnerType = OwnerOf(fullName),
                    Name = name,
                    ParameterTypes = [.. signature.ParameterTypes],
                    IsPrivateLike = (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Private or MethodAttributes.PrivateScope,
                    IsStatic = (attributes & MethodAttributes.Static) != 0,
                    IsVirtual = (attributes & MethodAttributes.Virtual) != 0,
                    IsGenerated = IsGeneratedType(fullName) || name.StartsWith('<'),
                    Calls = calls,
                    InitTypes = inits,
                    Touched = touched,
                };
                physicals.Add(physical);
                local[methodHandle] = physical;
            }
        }
        foreach (var typeHandle in md.TypeDefinitions)
        {
            foreach (var implHandle in md.GetTypeDefinition(typeHandle).GetMethodImplementations())
            {
                var impl = md.GetMethodImplementation(implHandle);
                if (impl.MethodBody.Kind != HandleKind.MethodDefinition || !local.TryGetValue((MethodDefinitionHandle)impl.MethodBody, out var body)) continue;
                if (provider.Method(impl.MethodDeclaration, CallKind.Call, 0, null) is not { } declaration) continue;
                body.IsExplicitImpl = true;
                explicitImpls.Add((declaration.Key, declaration.Short, declaration.Parameters.Count, body));
            }
        }
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(md, md.GetTypeDefinition(declaring)) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static void Decode(NameProvider provider, byte[] il, List<CallSite> calls, List<(string, int)> inits, HashSet<string> touched)
    {
        var i = 0;
        string? constrained = null;
        while (i < il.Length)
        {
            var start = i;
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
            switch (operand)
            {
                case OperandType.InlineMethod:
                {
                    var kind = code switch
                    {
                        0x28 => CallKind.Call,
                        0x6F => CallKind.CallVirt,
                        0x73 => CallKind.Newobj,
                        0x27 => CallKind.Jmp,
                        unchecked((short)0xFE06) => CallKind.Ldftn,
                        unchecked((short)0xFE07) => CallKind.Ldvirtftn,
                        _ => CallKind.Call,
                    };
                    var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                    if (provider.Method(handle, kind, start, constrained) is { } call)
                    {
                        calls.Add(call);
                        touched.Add(call.Type);
                    }
                    constrained = null;
                    break;
                }
                case OperandType.InlineSig:
                    // calli: an indirect call through a function pointer; the target cannot be known
                    calls.Add(new CallSite("<function pointer>", "calli", [], CallKind.Calli, start, null));
                    constrained = null;
                    break;
                case OperandType.InlineField:
                    if (provider.FieldType(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i))) is { } owner) touched.Add(owner);
                    break;
                case OperandType.InlineType or OperandType.InlineTok:
                {
                    var name = provider.TypeName(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i)));
                    if (name is not null)
                    {
                        touched.Add(name);
                        if (code == unchecked((short)0xFE15)) inits.Add((name, start));        // initobj: what 'default(T)' compiles to
                        if (code == unchecked((short)0xFE16)) constrained = name;               // constrained. (a call through a type parameter)
                    }
                    break;
                }
            }
            i += size;
        }
    }

    /// <summary>Names types and members from metadata tokens, and decodes signatures to type names.</summary>
    private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public CallSite? Method(EntityHandle handle, CallKind kind, int offset, string? constrained)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    var signature = method.DecodeSignature(this, null);
                    return new CallSite(FullName(md, md.GetTypeDefinition(method.GetDeclaringType())), md.GetString(method.Name), [.. signature.ParameterTypes], kind, offset, constrained);
                }
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (reference.GetKind() != MemberReferenceKind.Method) return null;
                    var parent = reference.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition or HandleKind.TypeSpecification ? TypeName(reference.Parent) : null;
                    if (parent is null) return null;
                    var signature = reference.DecodeMethodSignature(this, null);
                    return new CallSite(parent, md.GetString(reference.Name), [.. signature.ParameterTypes], kind, offset, constrained);
                }
                case HandleKind.MethodSpecification:
                    return Method(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method, kind, offset, constrained);
                default:
                    return null;
            }
        }

        public string? FieldType(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.FieldDefinition => FullName(md, md.GetTypeDefinition(md.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType())),
            HandleKind.MemberReference => TypeName(md.GetMemberReference((MemberReferenceHandle)handle).Parent),
            _ => null,
        };

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

    // ---------------------------------------------------------------- the primitives

    private static readonly string[] FilePrimitives =
    [
        "Open", "OpenWrite", "OpenHandle", "Create", "CreateText", "Delete", "Move", "Copy", "Replace", "Encrypt", "Decrypt",
        "WriteAllBytes", "WriteAllText", "WriteAllLines", "WriteAllBytesAsync", "WriteAllTextAsync", "WriteAllLinesAsync",
        "AppendAllText", "AppendAllLines", "AppendAllBytes", "AppendAllTextAsync", "AppendAllLinesAsync", "AppendAllBytesAsync", "AppendText",
        "SetAttributes", "SetCreationTime", "SetCreationTimeUtc", "SetLastAccessTime", "SetLastAccessTimeUtc", "SetLastWriteTime", "SetLastWriteTimeUtc",
        "SetUnixFileMode", "CreateSymbolicLink",
    ];

    private static readonly string[] DirectoryPrimitives =
    [
        "CreateDirectory", "CreateTempSubdirectory", "Delete", "Move", "SetCreationTime", "SetCreationTimeUtc", "SetLastAccessTime", "SetLastAccessTimeUtc",
        "SetLastWriteTime", "SetLastWriteTimeUtc", "CreateSymbolicLink",
    ];

    private static readonly string[] InfoPrimitives =
    [
        "Create", "CreateText", "AppendText", "Delete", "MoveTo", "CopyTo", "Replace", "Open", "OpenWrite", "Encrypt", "Decrypt", "CreateSubdirectory", "CreateAsSymbolicLink",
        "set_Attributes", "set_IsReadOnly", "set_CreationTime", "set_CreationTimeUtc", "set_LastAccessTime", "set_LastAccessTimeUtc", "set_LastWriteTime", "set_LastWriteTimeUtc", "set_UnixFileMode",
    ];

    private static readonly string[] CommandPrimitives = ["ExecuteNonQuery", "ExecuteScalar", "ExecuteReader", "ExecuteNonQueryAsync", "ExecuteScalarAsync", "ExecuteReaderAsync", "ExecuteDbDataReader", "ExecuteDbDataReaderAsync", "Prepare"];

    private static readonly string[] RawPrimitives =
    [
        "sqlite3_step", "sqlite3_prepare", "sqlite3_prepare_v2", "sqlite3_prepare_v3", "sqlite3_prepare16", "sqlite3_prepare16_v2", "sqlite3_prepare16_v3", "sqlite3_exec",
        "sqlite3_open", "sqlite3_open16", "sqlite3_open_v2", "sqlite3_backup_init", "sqlite3_backup_step", "sqlite3_backup_finish", "sqlite3_blob_open", "sqlite3_blob_write",
        "sqlite3_wal_checkpoint", "sqlite3_wal_checkpoint_v2", "sqlite3_deserialize",
    ];

    private static readonly HashSet<string> Primitives = BuildPrimitives();

    private static HashSet<string> BuildPrimitives()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in FilePrimitives) set.Add("System.IO.File::" + n);
        foreach (var n in DirectoryPrimitives) set.Add("System.IO.Directory::" + n);
        foreach (var n in InfoPrimitives) { set.Add("System.IO.FileInfo::" + n); set.Add("System.IO.DirectoryInfo::" + n); set.Add("System.IO.FileSystemInfo::" + n); }
        set.Add("System.IO.FileStream::.ctor");
        foreach (var n in new[] { "Write", "WriteAsync", "WriteByte", "SetLength" }) set.Add("System.IO.FileStream::" + n);
        set.Add("System.IO.RandomAccess::Write");
        set.Add("System.IO.RandomAccess::SetLength");
        // the writer's connection and its commands: ADO.NET and the provider's own types, and the interfaces they sit behind
        foreach (var type in new[] { "Microsoft.Data.Sqlite.SqliteCommand", "System.Data.Common.DbCommand", "System.Data.IDbCommand" })
        {
            foreach (var n in CommandPrimitives) set.Add($"{type}::{n}");
        }
        foreach (var type in new[] { "Microsoft.Data.Sqlite.SqliteConnection", "System.Data.Common.DbConnection", "System.Data.IDbConnection" })
        {
            foreach (var n in new[] { "Open", "OpenAsync", "BeginTransaction", "BeginTransactionAsync", "BeginDbTransaction", "BeginDbTransactionAsync", "BackupDatabase" }) set.Add($"{type}::{n}");
        }
        foreach (var type in new[] { "Microsoft.Data.Sqlite.SqliteTransaction", "System.Data.Common.DbTransaction", "System.Data.IDbTransaction" })
        {
            foreach (var n in new[] { "Commit", "Rollback", "CommitAsync", "RollbackAsync" }) set.Add($"{type}::{n}");
        }
        set.Add("Microsoft.Data.Sqlite.SqliteBlob::.ctor");
        set.Add("Microsoft.Data.Sqlite.SqliteBlob::Write");
        // the engine's own API: stepping or preparing a statement, opening a database, backing one up
        foreach (var type in new[] { "SQLitePCL.raw", "SQLitePCL.ISQLite3Provider" })
        {
            foreach (var n in RawPrimitives) set.Add($"{type}::{n}");
        }
        return set;
    }

    /// <summary>Whether a call is a mutation primitive of A-25 (a). <c>StreamWriter</c> and <c>StreamReader</c> constructors count only
    /// when they take a path (a stream-taking one writes through a stream somebody else opened).</summary>
    internal static bool IsPrimitive(CallSite call) =>
        Primitives.Contains(call.Short)
        || (call.Short is "System.IO.StreamWriter::.ctor" && call.Parameters.Count > 0 && call.Parameters[0] == "String");

    /// <summary>A-25 (d): read-only operations deliberately not covered, by exact signature. <c>ReaderConnection</c> only ever wraps
    /// a <c>Mode=ReadOnly</c> connection (A-06, A-03); the others open a read-only <c>FileStream</c>, or a read-only connection. Nothing
    /// that runs a command on the WRITER is here: <c>WriterConnection.AssertEngine</c> takes the lease.</summary>
    internal static readonly string[] ReadOnlyAllowList =
    [
        "StorageInventory.Library.ReaderConnection::AssertEngine(StorageInventory.Library.LibraryFaultInjection)",
        "StorageInventory.Library.ReaderConnection::SetAndReadBack(String,String,String,String)",
        "StorageInventory.Library.ReaderConnection::ReadBack(String)",
        "StorageInventory.Library.ReaderConnection::Scalar(String,System.ValueTuple`2<String,Object>[])",
        "StorageInventory.Library.ReaderConnection::Rows(String,System.Action`1<StorageInventory.Library.IRowReader>,System.ValueTuple`2<String,Object>[])",
        "StorageInventory.Library.LibraryDatabase::OpenReader(String,StorageInventory.Library.LibraryFaultInjection)",
        "StorageInventory.Library.LibraryStore::ReadHeader()",
        "StorageInventory.Library.LibraryStore::HandleLength(String,Boolean)",
    ];

    // ---------------------------------------------------------------- A-25 (a)

    private static bool IsAuthorityType(string type) => type is InterlockType or MutationLeaseType or ObservationLeaseType || type.StartsWith(InterlockType + "+", StringComparison.Ordinal);

    /// <summary>The leased operations of a model: methods an author wrote that take a <c>MutationLease</c>, outside the interlock's
    /// own authority. Keyed by full signature.</summary>
    internal static HashSet<string> LeasedOperations(Model model) =>
        [.. model.Units.Where(u => u.TakesLease && !u.Entry.IsGenerated && !IsAuthorityType(u.Type)).Select(u => u.Key)];

    private static readonly HashSet<string> LeaseProducers =
    [
        InterlockType + "::TakeStartupLease", InterlockType + "::TryBeginMutation", InterlockType + "::HandOffToSave", ObservationLeaseType + "::HandOffToSave",
    ];

    /// <summary>What a unit reaches directly (not through dispatch): primitives, leased operations, indirect calls.</summary>
    private static List<string> DirectReach(Unit unit, HashSet<string> leased)
    {
        var reached = new List<string>();
        foreach (var call in unit.Calls)
        {
            if (call.Kind == CallKind.Calli) reached.Add("calli (an indirect call)");
            else if (IsPrimitive(call)) reached.Add(call.Short);
            else if (leased.Contains(call.Key)) reached.Add(call.Short + " (takes a lease)");
        }
        return reached;
    }

    private static IEnumerable<Unit> DispatchTargets(Model model, CallSite call)
    {
        if (!call.Dispatches) yield break;
        var seen = new HashSet<Unit>();
        foreach (var key in new[] { call.Key, $"{call.Short}/{call.Parameters.Count}" })
        {
            if (!model.Overriders.TryGetValue(key, out var list)) continue;
            foreach (var unit in list) if (seen.Add(unit)) yield return unit;
        }
    }

    /// <summary>A-25 (a). Returns every method that reaches a mutation without authority, with the rule that rejected it.
    /// <paramref name="inScope"/> selects which units are judged (so a negative self-test can judge only its fixtures);
    /// <paramref name="leasedOperations"/> is the set of leased operations the calls are matched against.</summary>
    internal static List<Violation> MutationViolations(Model model, Func<string, bool> inScope, HashSet<string> leasedOperations, Options? options = null, IEnumerable<string>? allowList = null)
    {
        options ??= Options.Frozen;
        var allow = new HashSet<string>(allowList ?? ReadOnlyAllowList, StringComparer.Ordinal);
        var direct = model.Units.ToDictionary(u => u.Key, u => DirectReach(u, leasedOperations), StringComparer.Ordinal);

        // callers by unit, resolving dispatch: a call through an interface or a virtual reaches every implementer
        var callers = new Dictionary<string, List<Unit>>(StringComparer.Ordinal);
        void AddCaller(string key, Unit caller)
        {
            if (!callers.TryGetValue(key, out var list)) callers[key] = list = [];
            if (!list.Contains(caller)) list.Add(caller);
        }
        foreach (var u in model.Units)
        {
            foreach (var call in u.Calls.Where(c => c.Kind != CallKind.Calli))
            {
                AddCaller(call.Key, u);
                foreach (var target in DispatchTargets(model, call)) AddCaller(target.Key, u);
            }
        }

        bool Authority(Unit unit, HashSet<string> chain)
        {
            if (unit.TakesLease) return true;
            if (options.LeaseBoundTypeExemption && !unit.Entry.IsStatic && model.LeaseBoundTypes.ContainsKey(unit.Entry.Type)) return true;
            if (options.LeaseProducerExemption && unit.Calls.Any(c => LeaseProducers.Contains(c.Short))) return true;
            if (!unit.IsPrivate) return false;
            if (!callers.TryGetValue(unit.Key, out var who) || who.Count == 0) return false;
            if (options.TransitivePrivateAuthority)
            {
                if (!chain.Add(unit.Key)) return true;   // a cycle of private helpers is judged by its other callers
                return who.All(c => c.Type == unit.Type && (c.Key == unit.Key || Authority(c, chain)));
            }
            return who.All(c => c.Type == unit.Type && c.TakesLease);
        }

        var violations = new List<Violation>();
        foreach (var unit in model.Units.Where(u => inScope(u.Type)))
        {
            var reached = new List<string>(direct[unit.Key]);
            foreach (var call in unit.Calls.Where(c => c.Dispatches))
            {
                foreach (var target in DispatchTargets(model, call))
                {
                    if (target == unit || allow.Contains(target.Key) || direct[target.Key].Count == 0) continue;
                    reached.Add($"{call.Short} -> {target.Key} (dispatch)");
                }
            }
            if (reached.Count == 0 || allow.Contains(unit.Key) || Authority(unit, [])) continue;

            var reachedText = string.Join(", ", reached.Distinct().Order(StringComparer.Ordinal));
            var hasIndirect = reached.All(r => r.StartsWith("calli", StringComparison.Ordinal));
            string rule, why;
            if (!unit.IsPrivate)
            {
                rule = hasIndirect ? RuleIndirectCall : RuleNotPrivate;
                why = "it takes no MutationLease parameter and is not private";
            }
            else if (!callers.TryGetValue(unit.Key, out var who) || who.Count == 0)
            {
                rule = RuleUncalled;
                why = "it is private but called by no method";
            }
            else if (who.FirstOrDefault(c => c.Type != unit.Type) is { } foreign)
            {
                rule = RuleForeignCaller;
                why = $"it is private but called from {foreign.Key}, a method of another type";
            }
            else
            {
                var offender = who.First(c => !c.TakesLease);
                rule = RuleCallerWithoutLease;
                why = $"it is private but called by {offender.Key}, which takes no MutationLease";
            }
            violations.Add(new Violation(unit.Key, rule, $"reaches {reachedText} without a MutationLease: {why}"));
        }
        return violations;
    }

    // ---------------------------------------------------------------- C4-M13: methods that take SQL text

    /// <summary>A method of the assembly that takes SQL text: the positions (0-based, <c>this</c> excluded) of its <c>string</c> parameters
    /// whose names say so, its parameter count, and whether the last one is <c>params</c>.</summary>
    internal sealed record SqlForwarder(string Type, string Method, int ParameterCount, bool HasParams, IReadOnlyList<int> Indexes);

    internal static List<SqlForwarder> SqlForwarders(string assemblyPath, Func<string, bool> isSqlParameterName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var provider = new NameProvider(md);
        var result = new List<SqlForwarder>();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(provider, null);
                var parameters = method.GetParameters().Select(h => md.GetParameter(h)).Where(p => p.SequenceNumber > 0).OrderBy(p => p.SequenceNumber).ToList();
                var indexes = parameters.Where(p => p.SequenceNumber <= signature.ParameterTypes.Length && signature.ParameterTypes[p.SequenceNumber - 1] == "String" && isSqlParameterName(md.GetString(p.Name)))
                    .Select(p => p.SequenceNumber - 1).ToList();
                if (indexes.Count == 0) continue;
                var hasParams = parameters.Count > 0 && parameters[^1].GetCustomAttributes().Any(h =>
                {
                    var attribute = md.GetCustomAttribute(h);
                    var owner = attribute.Constructor.Kind == HandleKind.MemberReference ? md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent : default;
                    return !owner.IsNil && provider.TypeName(owner) == "System.ParamArrayAttribute";
                });
                result.Add(new SqlForwarder(FullName(md, type), md.GetString(method.Name), signature.ParameterTypes.Length, hasParams, indexes));
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- A-25 (c)

    /// <summary>A-25 (c): a lease is constructed (<c>newobj</c> or <c>initobj</c>, which is what <c>default</c> compiles to) only inside
    /// the interlock type.</summary>
    internal static List<Violation> ConstructionViolations(Model model, Func<string, bool> inScope)
    {
        var violations = new List<Violation>();
        foreach (var p in model.Physicals.Where(p => inScope(p.Type)))
        {
            if (p.Type == InterlockType || p.Type.StartsWith(InterlockType + "+", StringComparison.Ordinal)) continue;
            foreach (var call in p.Calls.Where(c => c.Short is MutationLeaseType + "::.ctor" or ObservationLeaseType + "::.ctor").Select(c => c.Short).Distinct())
            {
                violations.Add(new Violation(p.Key, RuleConstructsLease, $"constructs a lease ({call})"));
            }
            foreach (var type in p.InitTypes.Select(t => t.Type).Where(t => t is MutationLeaseType or ObservationLeaseType).Distinct())
            {
                violations.Add(new Violation(p.Key, RuleDefaultLease, $"creates a default value of {type}"));
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

    // ---------------------------------------------------------------- C4-M17 (1): the file system is LibraryStore's alone

    private static readonly string[] FileSystemTypes =
    [
        "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileSystemInfo", "System.IO.FileStream",
        "System.IO.FileSystemWatcher", "System.IO.RandomAccess", "System.IO.MemoryMappedFiles.MemoryMappedFile", "System.IO.Enumeration.FileSystemEnumerable`1",
        "System.IO.Enumeration.FileSystemEnumerator`1", "Microsoft.Win32.SafeHandles.SafeFileHandle",
    ];

    /// <summary>Whether a call is a file-system API of C4-M17: any member of the file types, a path-taking stream constructor, and the
    /// path-based existence check <c>Path.Exists</c>. (<c>Path.Combine</c> and the other pure string functions are not.)</summary>
    internal static bool IsFileSystemApi(CallSite call) =>
        FileSystemTypes.Contains(call.Type)
        || (call.Type is "System.IO.StreamWriter" or "System.IO.StreamReader" && call.Name == ".ctor" && call.Parameters.Count > 0 && call.Parameters[0] == "String")
        || call.Short is "System.IO.Path::Exists" or "System.IO.Path::GetTempFileName";

    /// <summary>C4-M17: inside the judged types, no method outside <paramref name="storeType"/> (and its closures) calls a file-system
    /// API. Every member access is therefore through <c>LibraryStore</c>, whose members are lock-checked.</summary>
    internal static List<Violation> FileSystemViolations(Model model, Func<string, bool> inScope, string storeType)
    {
        var violations = new List<Violation>();
        foreach (var p in model.Physicals.Where(p => inScope(p.Type) && p.OwnerType != storeType))
        {
            foreach (var call in p.Calls.Where(IsFileSystemApi).Select(c => c.Short).Distinct(StringComparer.Ordinal))
            {
                violations.Add(new Violation(p.Key, RuleFileSystemOutsideStore, $"calls {call}; only {storeType} may touch the file system"));
            }
        }
        return violations;
    }

    // ---------------------------------------------------------------- C4-M17 (2): the writer lock comes first

    /// <summary>What the lock-first analysis looks for: the call that takes the lock, and the calls that inspect, create, rename or
    /// open a Library member (short names, <c>Type::Name</c>).</summary>
    internal sealed record LockPolicy(string Acquire, IReadOnlySet<string> Touching, string StoreType, IReadOnlySet<string> RuntimeGuardedEntries);

    /// <summary>C4-M17, CONC-01, LIB-07 step 3: in every judged method, every call that inspects, creates, renames or opens a member
    /// sits at an IL offset after that method's own call to the lock. A method that touches a member and never takes the lock is
    /// "lock-requiring": each of its callers must call it after the caller's own lock call, or be lock-requiring itself, up the call
    /// chain; a lock-requiring method that is not private is a root, and must be one of the policy's runtime-guarded entries (whose
    /// member accessors throw without the lock) or it is rejected. <c>LibraryStore</c> itself is not judged: it is the guard.</summary>
    internal static List<Violation> LockFirstViolations(Model model, Func<string, bool> inScope, LockPolicy policy)
    {
        var violations = new List<Violation>();
        var judged = model.Units.Where(u => inScope(u.Type) && u.Type != policy.StoreType).ToList();
        var requiring = new HashSet<string>(StringComparer.Ordinal);

        bool IsTouch(CallSite c) => policy.Touching.Contains(c.Short) || requiring.Contains(c.Key);

        // fixpoint: a unit that touches (directly or through a lock-requiring callee) and takes the lock nowhere is lock-requiring
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var u in judged)
            {
                if (requiring.Contains(u.Key)) continue;
                if (u.Calls.Any(c => c.Short == policy.Acquire)) continue;
                if (u.Calls.Any(IsTouch)) { requiring.Add(u.Key); changed = true; }
            }
        }
        foreach (var u in judged)
        {
            var acquiring = u.Parts.Where(p => p.Calls.Any(c => c.Short == policy.Acquire)).ToList();
            foreach (var part in u.Parts)
            {
                var lockAt = part.Calls.Where(c => c.Short == policy.Acquire).Select(c => (int?)c.Offset).Min();
                foreach (var call in part.Calls.Where(IsTouch))
                {
                    if (acquiring.Count == 0) continue;   // lock-requiring unit: judged through its callers
                    if (lockAt is null) violations.Add(new Violation(u.Key, RuleBeforeLock, $"calls {call.Short} in a body ({part.Name}) that never takes the lock; the lock is taken in {acquiring[0].Name}"));
                    else if (call.Offset < lockAt) violations.Add(new Violation(u.Key, RuleBeforeLock, $"calls {call.Short} at IL offset {call.Offset}, before {policy.Acquire} at IL offset {lockAt}"));
                }
            }
            if (requiring.Contains(u.Key) && !u.IsPrivate && !policy.RuntimeGuardedEntries.Contains($"{u.Type}::{u.Name}"))
            {
                var touched = u.Calls.Where(IsTouch).Select(c => c.Short).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                violations.Add(new Violation(u.Key, RuleLockless, $"touches a Library member ({string.Join(", ", touched)}) and never takes the writer lock, and is not a declared runtime-guarded entry"));
            }
        }
        return violations;
    }

    /// <summary>The lock-requiring methods of the judged scope (for the test's own non-vacuity checks).</summary>
    internal static List<string> LockRequiring(Model model, Func<string, bool> inScope, LockPolicy policy)
    {
        var all = LockFirstViolations(model, inScope, policy with { RuntimeGuardedEntries = new HashSet<string>() });
        return [.. all.Where(v => v.Rule == RuleLockless).Select(v => v.Method)];
    }
}
