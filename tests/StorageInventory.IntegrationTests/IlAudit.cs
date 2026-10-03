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
/// <c>calli</c> (an indirect call cannot be resolved, so it counts as reaching a mutation: only a method with authority may make one);
/// and the invocation of a delegate that takes a <c>MutationLease</c> (<see cref="InvokesLeaseTakingDelegate"/>), outside the interlock.
/// Callees are matched by <see cref="MethodKey"/>: declaring type, name, generic arity and the full parameter signature, so an overload
/// cannot hide behind another; two methods that still share a key are rejected (<see cref="RuleAmbiguousMethod"/>).</para>
/// <para>The compiler's wrappers are folded back into the method the author wrote: an async method's state machine, a lambda and a
/// local function belong to the method that holds them. A lambda or local function that itself takes a lease is its own unit and has
/// authority for its own calls; one that merely captures a lease is part of its host, which then needs the parameter, <b>and which
/// must not leave the host</b>: <see cref="IlClosures"/> follows the delegate and rejects it where it escapes the host's call.</para>
/// <para>Part (c): a lease is constructed (<c>newobj</c>, <c>initobj</c>) only inside the interlock type.</para>
/// </summary>
internal static class IlAudit
{
    internal const string MutationLeaseType = "StorageInventory.Library.MutationLease";
    internal const string ObservationLeaseType = "StorageInventory.Library.ObservationLease";
    internal const string InterlockType = "StorageInventory.Library.LibraryInterlock";
    internal const string LibraryAssembly = "StorageInventory.Library";

    internal enum CallKind { Call, CallVirt, Newobj, Jmp, Ldftn, Ldvirtftn, Calli }

    /// <summary>The identity of a method, the one string every matching in the audit uses (calls to definitions, overriders, the
    /// allow-lists): declaring type, name, <b>generic arity</b> (a backtick and the number, for a generic method) and the full parameter
    /// signature. Parameter types are decoded exactly: generic instantiations with their arguments, by-reference, pointer, array (with
    /// its rank), function-pointer (with its signature) and modified (<c>modreq</c>, <c>modopt</c>) shapes. Two overloads that differ
    /// only by generic arity, or by any of those shapes, therefore never share a key. The one thing a signature alone does not
    /// distinguish is the return type of a conversion operator (C# lets <c>op_Implicit</c> overloads differ only by it), so those carry it.
    /// Anything else that still shares a key is rejected by <see cref="MutationViolations"/> (<see cref="RuleAmbiguousMethod"/>).</summary>
    internal static string MethodKey(string type, string name, int genericArity, IReadOnlyList<string> parameters, string returnType) =>
        $"{type}::{name}{(genericArity > 0 ? "`" + genericArity : "")}({string.Join(",", parameters)}){(name is "op_Implicit" or "op_Explicit" ? "->" + returnType : "")}";

    /// <summary>One call instruction: the callee as the metadata names it, and where it sits in the method body.</summary>
    internal sealed record CallSite(string Type, string Name, IReadOnlyList<string> Parameters, CallKind Kind, int Offset, string? Constrained)
    {
        /// <summary>Type parameters of a generic method (0 for a method that is not generic).</summary>
        internal int GenericArity { get; init; }

        /// <summary>The callee is an instance method (its signature has <c>this</c>).</summary>
        internal bool HasThis { get; init; }

        internal string ReturnType { get; init; } = "Void";

        /// <summary>Assembly and metadata token of the callee when the call names a method DEFINITION of the assembly being read (a
        /// reference to another assembly's method has only its signature).</summary>
        internal string? Definition { get; init; }

        /// <summary>The type arguments of the declaring type when the callee is a member of a generic instantiation
        /// (<c>Func&lt;MutationLease,Boolean&gt;::Invoke</c> has <c>MutationLease</c> and <c>Boolean</c>).</summary>
        internal IReadOnlyList<string> ParentArguments { get; init; } = [];

        internal string Short => Type + "::" + Name;

        internal string Key => MethodKey(Type, Name, GenericArity, Parameters, ReturnType);

        internal bool Dispatches => Kind is CallKind.CallVirt or CallKind.Ldvirtftn;
    }

    /// <summary>One instruction of a method body with its operand resolved: a <see cref="CallSite"/> for a method token, a
    /// <see cref="FieldRef"/> for a field token, a type name (<c>string</c>) for a type token, the absolute target offset (<c>int</c>) of a
    /// branch, the absolute targets (<c>int[]</c>) of a <c>switch</c>, the index of an argument or local (<c>int</c>), or a
    /// <see cref="CalliSite"/>.</summary>
    internal readonly record struct Instr(int Offset, int Next, OpCode Op, object? Operand);

    internal sealed record FieldRef(string Owner, string Name, string Type);

    internal sealed record CalliSite(int ParameterCount, bool HasThis, bool ReturnsValue);

    /// <summary>One entry of a method body's exception table, as absolute IL offsets (end exclusive).</summary>
    internal readonly record struct Region(ExceptionRegionKind Kind, int TryStart, int TryEnd, int HandlerStart, int HandlerEnd, int FilterStart);

    /// <summary>One method body as the compiler produced it.</summary>
    internal sealed class Physical
    {
        public required string Assembly { get; init; }
        public required string Type { get; init; }           // full name, nested types as Outer+Inner
        public required string OwnerType { get; init; }      // the type with compiler-generated segments cut off (a closure belongs to its outer type)
        public required string Name { get; init; }
        public required IReadOnlyList<string> ParameterTypes { get; init; }
        public int GenericArity { get; init; }
        public string ReturnType { get; init; } = "Void";
        public bool HasThis { get; init; }
        public int Token { get; init; }                      // the MethodDef row (metadata token) in its assembly
        public required bool IsPrivateLike { get; init; }
        public required bool IsStatic { get; init; }
        public required bool IsVirtual { get; init; }
        public bool IsAbstract { get; init; }
        public required bool IsGenerated { get; init; }
        public bool IsExplicitImpl { get; set; }
        public required List<CallSite> Calls { get; init; }
        public required List<(string Type, int Offset)> InitTypes { get; init; }
        public required HashSet<string> Touched { get; init; }   // declaring types of the fields, types and methods the body names
        public List<Instr> Instructions { get; init; } = [];
        public List<Region> Regions { get; init; } = [];
        public bool Decoded { get; set; }                        // false when the body could not be decoded (the escape analysis then fails closed)
        public string Key => MethodKey(Type, Name, GenericArity, ParameterTypes, ReturnType);
        public string Definition => $"{Assembly}:0x{Token:X8}";
        public bool TakesLease => ParameterTypes.Any(IsLeaseParameter);
    }

    private static readonly Regex ModifierText = new(@" mod(?:req|opt)\([^)]*\)", RegexOptions.Compiled);

    /// <summary>A decoded type without its custom modifiers (<c>in MutationLease</c> is <c>MutationLease&amp; modreq(InAttribute)</c>).</summary>
    internal static string Unmodified(string type) => type.Contains(" mod", StringComparison.Ordinal) ? ModifierText.Replace(type, "") : type;

    internal static bool IsLeaseParameter(string type) => Unmodified(type) is MutationLeaseType or MutationLeaseType + "&";

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
        /// <summary>Method keys that more than one non-generated method of one assembly carries (see <see cref="MethodKey"/>).</summary>
        public required List<(string Key, List<Physical> Methods)> AmbiguousKeys { get; init; }
        /// <summary>The unit each body belongs to (a closure, local function or state machine to the method that holds it).</summary>
        public required Dictionary<Physical, List<Unit>> UnitOf { get; init; }
        /// <summary>The fields of every type read, by type full name (for the escape analysis's "may this type hold a delegate").</summary>
        public required Dictionary<string, List<(string Name, string Type, bool IsStatic)>> Fields { get; init; }
        /// <summary>Types that are delegates (they derive from <c>MulticastDelegate</c>), by full name.</summary>
        public required HashSet<string> DelegateTypes { get; init; }
        /// <summary>Interface types of the assemblies read.</summary>
        public required HashSet<string> InterfaceTypes { get; init; }
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
    internal const string RuleAmbiguousMethod = "A25a/ambiguous-method-identity";
    internal const string RuleClosureEscapes = "A25a/closure-escapes";
    internal const string RuleUnresolvedFlow = "A25a/unresolved-closure-flow";
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

    private static readonly Dictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    internal static Model Read(params string[] assemblyPaths)
    {
        var physicals = new List<Physical>();
        var leaseBound = new Dictionary<string, bool>(StringComparer.Ordinal);
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var explicitImpls = new List<(string DeclarationKey, string DeclarationShort, int Arity, Physical Body)>();
        var fields = new Dictionary<string, List<(string Name, string Type, bool IsStatic)>>(StringComparer.Ordinal);
        var delegateTypes = new HashSet<string>(StringComparer.Ordinal);
        var interfaceTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths) ReadOne(path, physicals, leaseBound, direct, explicitImpls, fields, delegateTypes, interfaceTypes);

        // Two non-generated methods of one assembly with one key cannot be told apart by anything the audit matches on: refuse them.
        var ambiguous = physicals.Where(p => !p.IsGenerated).GroupBy(p => p.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => (g.Key, g.ToList())).ToList();

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
                AddOverrider(MethodKey(super, p.Name, p.GenericArity, p.ParameterTypes, p.ReturnType), unit);
                AddOverrider(OverrideShape(super, p.Name, p.GenericArity, p.ParameterTypes.Count), unit);
            }
        }
        foreach (var (declarationKey, declarationShort, arity, body) in explicitImpls)
        {
            if (!units.TryGetValue(body.Key, out var unit)) continue;
            AddOverrider(declarationKey, unit);
            AddOverrider(OverrideShape(declarationShort, body.GenericArity, arity), unit);
        }
        return new Model
        {
            Methods = units, Physicals = physicals, LeaseBoundTypes = leaseBound, Overriders = overriders, SuperTypes = supers, AmbiguousKeys = ambiguous, UnitOf = unitOf,
            Fields = fields, DelegateTypes = delegateTypes, InterfaceTypes = interfaceTypes,
        };
    }

    /// <summary>The looser key dispatch falls back on (a generic interface's signature names <c>!0</c> where the implementer names the
    /// concrete type): declaring type, name, generic arity and parameter COUNT.</summary>
    internal static string OverrideShape(string declaringType, string name, int genericArity, int parameterCount) => OverrideShape(declaringType + "::" + name, genericArity, parameterCount);

    internal static string OverrideShape(string shortName, int genericArity, int parameterCount) => $"{shortName}{(genericArity > 0 ? "`" + genericArity : "")}/{parameterCount}";

    private static readonly Regex StateMachineType = new(@"^<(?<m>.+)>d(__\d+)?(`\d+)?$", RegexOptions.Compiled);

    private static bool IsStateMachine(Physical p) => StateMachineType.IsMatch(Last(p.Type));

    /// <summary>The method a state machine body (an async method or lambda, an iterator) was made out of: the type that holds the state
    /// machine (its nested type is named <c>&lt;Origin&gt;d__N</c>) and the name of the origin; null for any other body.</summary>
    internal static (string Type, string Name)? StateMachineOriginOf(Physical body)
    {
        var match = StateMachineType.Match(Last(body.Type));
        return match.Success && body.IsGenerated ? (Parent(body.Type), match.Groups["m"].Value) : null;
    }

    /// <summary>Whether a type (by full name) is the compiler's state machine of an async method or an iterator.</summary>
    internal static bool IsStateMachineType(string type) => StateMachineType.IsMatch(Last(type));

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
        List<(string, string, int, Physical)> explicitImpls, Dictionary<string, List<(string Name, string Type, bool IsStatic)>> fields, HashSet<string> delegateTypes, HashSet<string> interfaceTypes)
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
            if (!type.BaseType.IsNil && provider.TypeName(type.BaseType) is { } baseName)
            {
                supers.Add(baseName);
                if (baseName is "System.MulticastDelegate" or "System.Delegate") delegateTypes.Add(fullName);
            }
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                if (provider.TypeName(md.GetInterfaceImplementation(implementation).Interface) is { } interfaceName) supers.Add(interfaceName);
            }
            direct[fullName] = supers;
            if ((type.Attributes & TypeAttributes.Interface) != 0) interfaceTypes.Add(fullName);

            var typeFields = new List<(string, string, bool)>();
            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                var fieldType = field.DecodeSignature(provider, null);
                var isStatic = (field.Attributes & FieldAttributes.Static) != 0;
                typeFields.Add((md.GetString(field.Name), fieldType, isStatic));
                if (isStatic) continue;
                if (IsLeaseParameter(fieldType)) leaseBound[fullName] = true;
            }
            fields[fullName] = typeFields;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(provider, null);
                var calls = new List<CallSite>();
                var inits = new List<(string, int)>();
                var touched = new HashSet<string>(StringComparer.Ordinal);
                var instructions = new List<Instr>();
                var regions = new List<Region>();
                var decoded = true;
                if (method.RelativeVirtualAddress != 0)
                {
                    var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                    var il = body.GetILBytes()!;
                    Decode(provider, il, calls, inits, touched, instructions);
                    foreach (var region in body.ExceptionRegions)
                    {
                        regions.Add(new Region(region.Kind, region.TryOffset, region.TryOffset + region.TryLength, region.HandlerOffset, region.HandlerOffset + region.HandlerLength,
                            region.Kind == ExceptionRegionKind.Filter ? region.FilterOffset : -1));
                    }
                }
                else decoded = false;   // abstract, extern or runtime-implemented (a delegate's Invoke): no body to read
                var name = md.GetString(method.Name);
                var attributes = method.Attributes;
                var physical = new Physical
                {
                    Assembly = assembly,
                    Type = fullName,
                    OwnerType = OwnerOf(fullName),
                    Name = name,
                    ParameterTypes = [.. signature.ParameterTypes],
                    GenericArity = signature.GenericParameterCount,
                    ReturnType = signature.ReturnType,
                    HasThis = signature.Header.IsInstance,
                    Token = MetadataTokens.GetToken(methodHandle),
                    IsPrivateLike = (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Private or MethodAttributes.PrivateScope,
                    IsStatic = (attributes & MethodAttributes.Static) != 0,
                    IsVirtual = (attributes & MethodAttributes.Virtual) != 0,
                    IsAbstract = (attributes & MethodAttributes.Abstract) != 0,
                    IsGenerated = IsGeneratedType(fullName) || name.StartsWith('<'),
                    Calls = calls,
                    InitTypes = inits,
                    Touched = touched,
                    Instructions = instructions,
                    Regions = regions,
                    Decoded = decoded,
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

    private static void Decode(NameProvider provider, byte[] il, List<CallSite> calls, List<(string, int)> inits, HashSet<string> touched, List<Instr> instructions)
    {
        var i = 0;
        string? constrained = null;
        while (i < il.Length)
        {
            var start = i;
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            var op = OpCodeByValue[code];
            var operand = op.OperandType;
            var size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
            object? value = null;
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
                        value = call;
                    }
                    constrained = null;
                    break;
                }
                case OperandType.InlineSig:
                    // calli: an indirect call through a function pointer; the target cannot be known
                    calls.Add(new CallSite("<function pointer>", "calli", [], CallKind.Calli, start, null));
                    value = provider.Calli(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i)));
                    constrained = null;
                    break;
                case OperandType.InlineField:
                {
                    var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                    if (provider.FieldType(handle) is { } owner) touched.Add(owner);
                    value = provider.Field(handle);
                    break;
                }
                case OperandType.InlineType or OperandType.InlineTok:
                {
                    var name = provider.TypeName(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i)));
                    if (name is not null)
                    {
                        touched.Add(name);
                        value = name;
                        if (code == unchecked((short)0xFE15)) inits.Add((name, start));        // initobj: what 'default(T)' compiles to
                        if (code == unchecked((short)0xFE16)) constrained = name;               // constrained. (a call through a type parameter)
                    }
                    break;
                }
                case OperandType.ShortInlineBrTarget:
                    value = i + 1 + (sbyte)il[i];                              // relative to the end of the instruction
                    break;
                case OperandType.InlineBrTarget:
                    value = i + 4 + BitConverter.ToInt32(il, i);
                    break;
                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, i);
                    var end = i + 4 + 4 * count;
                    var targets = new int[count];
                    for (var t = 0; t < count; t++) targets[t] = end + BitConverter.ToInt32(il, i + 4 + 4 * t);
                    value = targets;
                    break;
                }
                case OperandType.ShortInlineVar:
                    value = (int)il[i];
                    break;
                case OperandType.InlineVar:
                    value = (int)BitConverter.ToUInt16(il, i);
                    break;
            }
            i += size;
            instructions.Add(new Instr(start, i, op, value));
        }
    }

    /// <summary>Names types and members from metadata tokens, and decodes signatures to type names.</summary>
    private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        private readonly string _assembly = md.GetString(md.GetAssemblyDefinition().Name);

        public CallSite? Method(EntityHandle handle, CallKind kind, int offset, string? constrained)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    var signature = method.DecodeSignature(this, null);
                    return new CallSite(FullName(md, md.GetTypeDefinition(method.GetDeclaringType())), md.GetString(method.Name), [.. signature.ParameterTypes], kind, offset, constrained)
                    {
                        GenericArity = signature.GenericParameterCount,
                        HasThis = signature.Header.IsInstance,
                        ReturnType = signature.ReturnType,
                        Definition = $"{_assembly}:0x{MetadataTokens.GetToken((MethodDefinitionHandle)handle):X8}",
                    };
                }
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (reference.GetKind() != MemberReferenceKind.Method) return null;
                    var parent = reference.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition or HandleKind.TypeSpecification ? TypeName(reference.Parent) : null;
                    if (parent is null) return null;
                    var signature = reference.DecodeMethodSignature(this, null);
                    return new CallSite(parent, md.GetString(reference.Name), [.. signature.ParameterTypes], kind, offset, constrained)
                    {
                        GenericArity = signature.GenericParameterCount,
                        HasThis = signature.Header.IsInstance,
                        ReturnType = signature.ReturnType,
                        ParentArguments = reference.Parent.Kind == HandleKind.TypeSpecification ? TypeArguments(md.GetTypeSpecification((TypeSpecificationHandle)reference.Parent).DecodeSignature(this, null)) : [],
                    };
                }
                case HandleKind.MethodSpecification:
                    return Method(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method, kind, offset, constrained);
                default:
                    return null;
            }
        }

        /// <summary>The indirect call of a <c>calli</c>: how many values it takes off the stack, and whether it leaves one.</summary>
        public CalliSite? Calli(EntityHandle handle)
        {
            if (handle.Kind != HandleKind.StandaloneSignature) return null;
            var signature = md.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(this, null);
            return new CalliSite(signature.ParameterTypes.Length, signature.Header.IsInstance, Unmodified(signature.ReturnType) != "Void");
        }

        /// <summary>The top-level type arguments of a decoded instantiation (<c>Name&lt;A,B&lt;C&gt;&gt;</c> gives <c>A</c> and <c>B&lt;C&gt;</c>).</summary>
        internal static IReadOnlyList<string> TypeArguments(string text)
        {
            var open = text.IndexOf('<', StringComparison.Ordinal);
            var close = text.LastIndexOf('>');
            if (open < 0 || close < open) return [];
            var arguments = new List<string>();
            int depth = 0, start = open + 1;
            for (var i = start; i < close; i++)
            {
                if (text[i] is '<' or '(') depth++;
                else if (text[i] is '>' or ')') depth--;
                else if (text[i] == ',' && depth == 0) { arguments.Add(text[start..i]); start = i + 1; }
            }
            arguments.Add(text[start..close]);
            return arguments;
        }

        public FieldRef? Field(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition:
                {
                    var field = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return new FieldRef(FullName(md, md.GetTypeDefinition(field.GetDeclaringType())), md.GetString(field.Name), field.DecodeSignature(this, null));
                }
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (reference.GetKind() != MemberReferenceKind.Field || TypeName(reference.Parent) is not { } owner) return null;
                    return new FieldRef(owner, md.GetString(reference.Name), reference.DecodeFieldSignature(this, null));
                }
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
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + (shape.Rank == 1 ? "[*]" : "[" + new string(',', shape.Rank - 1) + "]");
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            $"fnptr({signature.Header.CallingConvention} {signature.ReturnType}({string.Join(",", signature.ParameterTypes)}){(signature.GenericParameterCount > 0 ? "`" + signature.GenericParameterCount : "")})";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";
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

    internal static bool IsAuthorityType(string type) => type is InterlockType or MutationLeaseType or ObservationLeaseType || type.StartsWith(InterlockType + "+", StringComparison.Ordinal);

    /// <summary>The leased operations of a model: methods an author wrote that take a <c>MutationLease</c>, outside the interlock's
    /// own authority. Keyed by full signature.</summary>
    internal static HashSet<string> LeasedOperations(Model model) =>
        [.. model.Units.Where(u => u.TakesLease && !u.Entry.IsGenerated && !IsAuthorityType(u.Type)).Select(u => u.Key)];

    private static readonly HashSet<string> LeaseProducers =
    [
        InterlockType + "::TakeStartupLease", InterlockType + "::TryBeginMutation", InterlockType + "::HandOffToSave", ObservationLeaseType + "::HandOffToSave",
    ];

    private static readonly string[] BclDelegatePrefixes =
    [
        "System.Action", "System.Func", "System.Predicate", "System.Comparison", "System.Converter", "System.EventHandler", "System.AsyncCallback", "System.Delegate", "System.MulticastDelegate",
        "System.Threading.ThreadStart", "System.Threading.ParameterizedThreadStart", "System.Threading.WaitCallback", "System.Threading.TimerCallback", "System.Threading.SendOrPostCallback",
        "System.Threading.ContextCallback", "System.Threading.WaitOrTimerCallback", "System.Threading.IOCompletionCallback",
    ];

    /// <summary>Whether a type (by the name a call site gives it) is a delegate: one of the model's, or a BCL delegate type.</summary>
    internal static bool IsDelegateType(Model model, string typeName) =>
        model.DelegateTypes.Contains(typeName) || BclDelegatePrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>A call to <c>Invoke</c> of a delegate whose signature takes a <c>MutationLease</c> (<c>Func&lt;MutationLease,T&gt;</c>, a declared
    /// delegate with a lease parameter). It is a leased operation like any method that takes a lease: its caller must hold a lease of its
    /// own, because a delegate's target is not a call the audit can resolve, and the lease handed to it could be a retained one. Only the
    /// interlock, which issues leases, is exempt (<see cref="IsAuthorityType"/>: <c>RunStartup</c> and <c>TryRunMutation</c> hand the lease
    /// they just granted to the lambda as a parameter).</summary>
    internal static bool InvokesLeaseTakingDelegate(Model model, CallSite c)
    {
        if (c.Name != "Invoke" || !c.HasThis || !IsDelegateType(model, c.Type)) return false;
        if (c.Parameters.Any(IsLeaseParameter)) return true;
        if (c.ParentArguments.Count == 0) return false;
        // Func<A, B, TResult> has parameters A and B; every other generic delegate: all its arguments
        var parameters = c.Type.StartsWith("System.Func", StringComparison.Ordinal) ? c.ParentArguments.Take(c.ParentArguments.Count - 1) : c.ParentArguments;
        return parameters.Any(IsLeaseParameter);
    }

    /// <summary>What a unit reaches directly (not through dispatch): primitives, leased operations, indirect calls, and the invocation of
    /// a delegate that takes a lease.</summary>
    private static List<string> DirectReach(Model model, Unit unit, HashSet<string> leased)
    {
        var reached = new List<string>();
        var interlock = IsAuthorityType(unit.Type);
        foreach (var call in unit.Calls)
        {
            if (call.Kind == CallKind.Calli) reached.Add("calli (an indirect call)");
            else if (IsPrimitive(call)) reached.Add(call.Short);
            else if (leased.Contains(call.Key)) reached.Add(call.Short + " (takes a lease)");
            else if (!interlock && InvokesLeaseTakingDelegate(model, call)) reached.Add(call.Short + " (a delegate that takes a lease)");
        }
        return reached;
    }

    internal static IEnumerable<Unit> DispatchTargets(Model model, CallSite call)
    {
        if (!call.Dispatches) yield break;
        var seen = new HashSet<Unit>();
        foreach (var key in new[] { call.Key, OverrideShape(call.Type, call.Name, call.GenericArity, call.Parameters.Count) })
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
        var direct = model.Units.ToDictionary(u => u.Key, u => DirectReach(model, u, leasedOperations), StringComparer.Ordinal);

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

        // methods that cannot be told apart are not judged at all: refuse them (a missing or aliased unit would hide a violation)
        foreach (var (key, methods) in model.AmbiguousKeys.Where(a => a.Methods.Any(m => inScope(m.OwnerType))))
        {
            var which = string.Join(", ", methods.Select(m => $"{m.Definition}{(m.IsStatic ? " static" : "")}{(m.IsPrivateLike ? " private" : "")} returning {m.ReturnType}"));
            violations.Add(new Violation(key, RuleAmbiguousMethod, $"{methods.Count} methods share this identity ({which}), so the audit cannot tell them apart and would judge them as one"));
        }

        // a delegate that borrows its host's authority must stay inside the host's call
        violations.AddRange(IlClosures.Violations(model, inScope, leasedOperations, allow));
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
