using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// A-25 (a) for delegates, lambdas, local functions and closures: authority borrowed by capture must not outlive its host.
/// <para><b>The problem.</b> <see cref="IlAudit"/> folds the body of a lambda or local function into the method that holds it, so a
/// lease-taking host "owns" the mutation its closure performs. That is sound only while the closure runs inside the host's call: a
/// delegate that is stored (a field, a static, an array element), returned, or handed to code that keeps it can be invoked later, by
/// a method that holds no lease, and an invocation of a delegate is not a call the audit can resolve. So the audit must prove that
/// such a delegate does not escape.</para>
/// <para><b>What the compiler emits</b> (all handled here). A lambda or local function converted to a delegate is a method
/// (<c>&lt;Host&gt;b__N_M</c>, <c>&lt;Host&gt;g__Name|N_M</c>): an instance method of the singleton <c>&lt;&gt;c</c> when it captures
/// nothing (its delegate is cached in a <c>&lt;&gt;9__N_M</c> static field: a store to a static, which is a sink), an instance method of
/// the host when it captures only <c>this</c>, and an instance method of a <c>&lt;&gt;c__DisplayClassN_M</c> object when it captures
/// locals or parameters (a lease is a captured local: it becomes a field of the display class). An async lambda is a stub that starts a
/// state machine (<c>&lt;&lt;Host&gt;b__N_M&gt;d</c>), whose <c>MoveNext</c> holds the work. The delegate is built by <c>ldftn</c> (or
/// <c>ldvirtftn</c>) and <c>newobj Delegate::.ctor(object, native int)</c>, and invoked by <c>callvirt Delegate::Invoke</c>. A local
/// function that is only ever called (never converted to a delegate) is a plain <c>call</c>, with its captures in a struct display class
/// passed by <c>ref</c>: it cannot escape and needs no analysis. A method group is the same <c>ldftn</c> of an ordinary method.</para>
/// <para><b>The rule.</b> An <i>authority closure</i> is a method that is bound to a delegate (<c>ldftn</c>/<c>ldvirtftn</c> +
/// <c>newobj</c>), takes no lease parameter itself, and reaches a mutation: a primitive, a leased operation, an indirect call, the
/// invocation of a lease-taking delegate, or any such callee. Its authority is borrowed from the host that built the delegate, so
/// the delegate (and anything that holds it) must not escape that host's call. This is checked by following the value through
/// every method body it can reach: <b>a value is tainted from the <c>newobj</c> that creates the delegate; taint follows the
/// evaluation stack, locals, arguments, casts, boxing and field loads; it ends at a sink</b>: a store to a static, an array, an
/// instance field other than the field of the object under construction in a constructor, a store through a pointer, a return, a
/// throw, a call the audit cannot read (external code) and a call whose callee lets that parameter escape. A call to a first-party
/// method is judged by a summary of that method's parameter (does it let the value escape? does a constructor keep it in the
/// object it builds, which then carries the taint?), computed by the same analysis to a fixed point; a call through an interface or a
/// virtual is judged against every implementer, and a call through a delegate against every method ever bound to that delegate type.
/// The invocation of a tainted delegate is not a sink: it runs inside the call that holds it, which is the point. What the Library's
/// verification does is exactly this: the host that takes the lease builds a <c>DelegateQueryRunner</c> out of two closures that
/// capture it and hands the runner to <c>SnapshotVerifier.Verify</c>; the analysis shows that the constructor keeps the closures only in
/// the runner's own two fields, and that the verifier and everything it passes the runner to only run it.</para>
/// <para><b>Precision.</b> Taint carries the path of fields through which a value holds the delegate (three deep, then "anywhere"):
/// an object that merely sits beside a delegate in a carrier is not treated as the delegate, and a carrier handed to code that reads
/// its other fields is not an escape. A summary is computed per (method, parameter, shape of what the parameter carries).</para>
/// <para><b>Fail closed.</b> Whatever the analysis cannot resolve with confidence is a violation, never a pass: a call into code it
/// cannot read (any BCL method that is handed the delegate: <c>Task.Run</c>, <c>Delegate.Combine</c>, a collection's <c>Add</c>), a
/// body it cannot decode, a stack it cannot reconcile, a store through a pointer, a store into an object the audit does not own, a
/// delegate kept across an <c>await</c> (the state machine's field), an analysis that runs past its budget. The message names the
/// method and the form. No compiler-generated method is exempt: they are analysed like any other. <b>Limits, stated:</b> a delegate
/// that is merely invoked is not followed into what the closure does (that is the host's authority, judged by the frozen wording); the
/// BCL is treated as external code, never as a safe sink; a value is tracked by its fields, not by general points-to (a store into any
/// object other than the one under construction is a sink); iterators are rejected rather than followed; function pointers are
/// <c>calli</c>, which the frozen rule already confines to methods with authority; reflection and dynamic code are excluded by
/// <c>SecurityAuditTests</c>.</para>
/// </summary>
internal sealed class IlClosures
{
    /// <summary>Marks the parameter whose fate a summary computes. It never appears in a report.</summary>
    private const string Marker = "\u0001parameter";

    private readonly IlAudit.Model _model;
    private readonly HashSet<string> _leased;
    private readonly HashSet<string> _allow;
    private readonly Dictionary<string, List<IlAudit.Physical>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IlAudit.Physical> _byDefinition = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IlAudit.Physical>> _delegateTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<IlAudit.Physical, bool> _reach = [];
    private readonly Dictionary<(IlAudit.Physical, int, string), Summary> _summaries = [];
    private readonly Dictionary<string, bool> _mayHold = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, string Name), List<IlAudit.Physical>> _stateMachines = [];

    private IlClosures(IlAudit.Model model, HashSet<string> leased, HashSet<string> allow)
    {
        _model = model;
        _leased = leased;
        _allow = allow;
        foreach (var p in model.Physicals)
        {
            if (!_byKey.TryGetValue(p.Key, out var list)) _byKey[p.Key] = list = [];
            list.Add(p);
            _byDefinition[p.Definition] = p;
        }
        foreach (var p in model.Physicals)
        {
            if (IlAudit.StateMachineOriginOf(p) is not { } origin) continue;
            if (!_stateMachines.TryGetValue(origin, out var machines)) _stateMachines[origin] = machines = [];
            machines.Add(p);
        }
        // every method ever bound to a delegate of each delegate type, by the generic type's name (variance and instantiation ignored: a superset)
        foreach (var p in model.Physicals)
        {
            foreach (var (call, _, delegateType) in DelegateCreations(p))
            {
                foreach (var target in Targets(call))
                {
                    if (!_delegateTargets.TryGetValue(delegateType, out var list)) _delegateTargets[delegateType] = list = [];
                    if (!list.Contains(target)) list.Add(target);
                }
            }
        }
    }

    // ---------------------------------------------------------------- the public entry point

    /// <summary>Every delegate that borrows a lease-taking host's authority and escapes it (<see cref="IlAudit.RuleClosureEscapes"/>), and
    /// every case the analysis could not decide (<see cref="IlAudit.RuleUnresolvedFlow"/>).</summary>
    internal static List<IlAudit.Violation> Violations(IlAudit.Model model, Func<string, bool> inScope, HashSet<string> leased, HashSet<string> allow) =>
        new IlClosures(model, leased, allow).Analyse(inScope);

    /// <summary>The authority closures of a model: every method bound to a delegate that borrows authority (for the tests'
    /// non-vacuity checks), with the physical bodies that build them.</summary>
    internal static List<(IlAudit.Physical Host, IlAudit.Physical Closure)> AuthorityClosures(IlAudit.Model model, HashSet<string> leased, HashSet<string> allow)
    {
        var analysis = new IlClosures(model, leased, allow);
        return [.. analysis.Sources().SelectMany(s => s.Targets.Select(t => (s.Host, t))).Distinct()];
    }

    /// <summary>The bodies of the model the simulation cannot read to the end (a stack it cannot reconcile, an instruction it does not
    /// model, a token it cannot resolve). For the engine's own self-test: the product's code must be fully readable, or the analysis
    /// would be silently blind where it matters.</summary>
    internal static List<string> UnreadableBodies(IlAudit.Model model, HashSet<string> leased, HashSet<string> allow, Func<string, bool> inScope, bool taintFirstArgument = false)
    {
        var analysis = new IlClosures(model, leased, allow);
        var found = new List<string>();
        foreach (var body in model.Physicals.Where(p => p.Decoded && p.Instructions.Count > 0 && inScope(p.OwnerType)))
        {
            Dictionary<int, Taint>? seeds = taintFirstArgument && (body.HasThis || body.ParameterTypes.Count > 0) ? new Dictionary<int, Taint> { [0] = Taint.Marked("") } : null;
            foreach (var problem in analysis.Simulate(body, seeds, createSources: false).Unresolved.Distinct()) found.Add($"{body.Key}: {problem}");
        }
        return found;
    }

    private List<IlAudit.Violation> Analyse(Func<string, bool> inScope)
    {
        var sources = Sources().Where(s => inScope(s.Host.OwnerType)).ToList();
        var found = new List<IlAudit.Violation>(LazyBodies(inScope));
        if (sources.Count == 0) return found;

        // summaries and source runs to a fixed point: a summary only ever gains escapes, so this ends
        var results = new Dictionary<IlAudit.Physical, Run>();
        var converged = false;
        for (var round = 0; round < 50 && !converged; round++)
        {
            var before = Fingerprint();
            results.Clear();
            foreach (var host in sources.Select(s => s.Host).Distinct()) results[host] = Simulate(host, null, createSources: true);
            foreach (var key in _summaries.Keys.ToList()) Recompute(key);
            converged = Fingerprint() == before;
        }
        if (!converged)
        {
            foreach (var host in sources.Select(s => s.Host).Distinct())
            {
                found.Add(new IlAudit.Violation(HostName(host), IlAudit.RuleUnresolvedFlow, $"the escape analysis did not converge (in {host.Name}), so it fails closed"));
            }
        }

        foreach (var (host, run) in results)
        {
            var unit = HostName(host);
            var type = host.OwnerType;
            if (!inScope(type)) continue;
            foreach (var sink in run.Sinks.DistinctBy(s => (s.Kind, s.Offset, s.Detail)))
            {
                var many = sink.Labels.Count > 1;
                var closure = sink.Labels.Count > 0 ? string.Join(" and ", sink.Labels) : "a delegate";
                found.Add(new IlAudit.Violation(unit, IlAudit.RuleClosureEscapes,
                    $"{closure} {(many ? "reach a mutation with the authority they borrow from their host" : "reaches a mutation with the authority it borrows from its host")}, and {sink.Detail} (IL_{sink.Offset:X4} of {host.Name})"));
            }
            foreach (var problem in run.Unresolved.Distinct())
            {
                found.Add(new IlAudit.Violation(unit, IlAudit.RuleUnresolvedFlow, $"{problem} (in {host.Name}): the audit cannot show that a lease-capturing delegate stays inside its host, so it fails closed"));
            }
        }
        return found;
    }

    private string Fingerprint() => string.Join("|", _summaries.OrderBy(kv => kv.Key.Item1.Definition, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key.Item1.Definition}/{kv.Key.Item2}/{kv.Key.Item3}={kv.Value.Escape}:{string.Join(",", kv.Value.ToThis.Order(StringComparer.Ordinal))}"));

    private string HostName(IlAudit.Physical p) => _model.UnitOf.TryGetValue(p, out var units) && units.Count > 0 ? units[0].Key : p.Key;

    // ---------------------------------------------------------------- sources: delegates that borrow authority

    /// <summary>One <c>newobj Delegate::.ctor</c> that follows an <c>ldftn</c>/<c>ldvirtftn</c>: the target, where, and the delegate type.</summary>
    private IEnumerable<(IlAudit.CallSite Target, int Offset, string DelegateType)> DelegateCreations(IlAudit.Physical p)
    {
        var list = p.Instructions;
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Operand is not IlAudit.CallSite { Kind: IlAudit.CallKind.Newobj, Name: ".ctor", Parameters: [var a, var b] } ctor || Unmod(a) != "Object" || Unmod(b) != "IntPtr") continue;
            if (list[i - 1].Operand is IlAudit.CallSite { Kind: IlAudit.CallKind.Ldftn or IlAudit.CallKind.Ldvirtftn } target) yield return (target, list[i].Offset, ctor.Type);
        }
    }

    private static string Unmod(string type) => IlAudit.Unmodified(type);

    /// <summary>Physical methods a call site can reach: the definition itself (exact within an assembly, by key across assemblies) and,
    /// for a virtual or interface call or <c>ldvirtftn</c>, every override or implementation in the model.</summary>
    private List<IlAudit.Physical> Targets(IlAudit.CallSite call)
    {
        var found = new List<IlAudit.Physical>();
        if (call.Definition is { } definition && _byDefinition.TryGetValue(definition, out var exact)) found.Add(exact);
        else if (_byKey.TryGetValue(call.Key, out var byKey)) found.AddRange(byKey);
        if (call.Dispatches)
        {
            foreach (var unit in IlAudit.DispatchTargets(_model, call))
            {
                foreach (var part in unit.Parts.Where(p => p.Key == unit.Key && !p.IsGenerated)) if (!found.Contains(part)) found.Add(part);
            }
        }
        return found;
    }

    /// <summary>Whether a method reaches a mutation: directly (a primitive, a leased operation, a <c>calli</c>, the invocation of a
    /// delegate that takes a lease) or through anything it calls, interface and virtual dispatch included. Allow-listed read-only
    /// methods (A-25 d) end the search.</summary>
    private bool Reaches(IlAudit.Physical method)
    {
        if (_reach.TryGetValue(method, out var known)) return known;
        _reach[method] = false;   // a cycle is judged by its other edges
        var result = false;
        foreach (var call in Bodies(method).SelectMany(b => b.Calls))
        {
            if (call.Kind == IlAudit.CallKind.Calli || IlAudit.IsPrimitive(call) || (_leased.Contains(call.Key) && !_allow.Contains(call.Key))
                || (IlAudit.InvokesLeaseTakingDelegate(_model, call) && !IlAudit.IsAuthorityType(method.OwnerType)))
            {
                result = true;
                break;
            }
            if (call.Kind is IlAudit.CallKind.Ldftn or IlAudit.CallKind.Ldvirtftn) continue;   // building a delegate is not running it
            foreach (var target in Targets(call))
            {
                if (_allow.Contains(target.Key) || target == method) continue;
                if (!target.TakesLease && Reaches(target)) { result = true; break; }
            }
            if (result) break;
        }
        _reach[method] = result;
        return result;
    }

    /// <summary>A method and the bodies the compiler moved out of it: an async method or lambda keeps its work in the MoveNext of a state
    /// machine, so the method that is bound to a delegate (or called) is only a stub that starts it.</summary>
    private IEnumerable<IlAudit.Physical> Bodies(IlAudit.Physical method)
    {
        yield return method;
        if (_stateMachines.TryGetValue((method.Type, method.Name), out var machines)) foreach (var body in machines) yield return body;
    }

    /// <summary>An iterator that reaches a mutation does it lazily, when somebody enumerates it, which can be after the call that held the
    /// lease returned: its state machine holds the lease and runs on the enumerator's schedule. It is a deferred body like a closure, but
    /// it is not a delegate, so the escape analysis cannot follow it; it is rejected outright (none exists in the product).</summary>
    private IEnumerable<IlAudit.Violation> LazyBodies(Func<string, bool> inScope)
    {
        foreach (var body in _model.Physicals.Where(p => p.IsGenerated && p.Name == "MoveNext" && IlAudit.IsStateMachineType(p.Type) && inScope(p.OwnerType)))
        {
            if (!_model.SuperTypes.TryGetValue(body.Type, out var supers) || !supers.Any(s => s.StartsWith("System.Collections.IEnumer", StringComparison.Ordinal)
                || s.StartsWith("System.Collections.Generic.IEnumer", StringComparison.Ordinal) || s.StartsWith("System.Collections.Generic.IAsyncEnumer", StringComparison.Ordinal))) continue;
            if (!Reaches(body)) continue;
            yield return new IlAudit.Violation(HostName(body), IlAudit.RuleClosureEscapes,
                $"the iterator {body.Type.Split('+')[^1]} reaches a mutation when it is enumerated, which can be after the call that held the lease has returned: a deferred body that the escape analysis cannot follow");
        }
    }

    private bool IsAuthorityTarget(IlAudit.Physical target) => !target.TakesLease && !_allow.Contains(target.Key) && Reaches(target);

    private List<(IlAudit.Physical Host, int Offset, List<IlAudit.Physical> Targets)> Sources()
    {
        var list = new List<(IlAudit.Physical, int, List<IlAudit.Physical>)>();
        foreach (var p in _model.Physicals)
        {
            foreach (var (target, offset, _) in DelegateCreations(p))
            {
                var authority = AuthorityTargetsOf(target);
                if (authority.Count > 0) list.Add((p, offset, authority));
            }
        }
        return list;
    }

    private List<IlAudit.Physical> AuthorityTargetsOf(IlAudit.CallSite target) => [.. Targets(target).Where(IsAuthorityTarget)];

    // ---------------------------------------------------------------- may a value of this type hold a delegate?

    private static readonly HashSet<string> Primitive = new(StringComparer.Ordinal)
    {
        "Boolean", "Char", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "String", "IntPtr", "UIntPtr", "Void", "TypedReference",
    };

    private bool IsDelegate(string typeName) => IlAudit.IsDelegateType(_model, typeName);

    /// <summary>Whether a value of this (decoded) type could be, or hold, a delegate: a delegate type, <c>object</c>, an interface, a
    /// generic parameter, a first-party type with a field that may, or a generic instantiation with an argument that may. Other BCL
    /// types are assumed not to retain first-party delegates (the calls that do take a delegate are sinks by themselves).</summary>
    private bool MayHold(string type)
    {
        type = Unmod(type);
        if (_mayHold.TryGetValue(type, out var known)) return known;
        _mayHold[type] = false;   // a cycle of types holds nothing by itself
        var result = MayHoldCore(type);
        _mayHold[type] = result;
        return result;
    }

    private bool MayHoldCore(string type)
    {
        while (type.EndsWith('&') || type.EndsWith('*')) type = type[..^1];
        if (type.EndsWith(']')) type = type[..type.LastIndexOf('[')];
        if (type.StartsWith('!')) return true;
        if (type.StartsWith("fnptr", StringComparison.Ordinal)) return false;
        var cut = type.IndexOf('<', StringComparison.Ordinal);
        var name = cut < 0 ? type : type[..cut];
        if (cut >= 0 && TypeArgumentsOf(type).Any(MayHold)) return true;
        if (Primitive.Contains(name)) return false;
        if (name == "Object" || IsDelegate(name) || _model.InterfaceTypes.Contains(name)) return true;
        if (_model.Fields.TryGetValue(name, out var fields)) return fields.Any(f => !f.IsStatic && MayHold(f.Type));
        // other BCL types: interfaces by the naming convention (they could hold anything), the non-generic collections and the tasks, which keep what they are given
        var last = name[(name.LastIndexOf('.') + 1)..];
        if (last.Length > 1 && last[0] == 'I' && char.IsUpper(last[1])) return true;
        if (name.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal)) return true;
        return cut < 0 && name.StartsWith("System.Collections.", StringComparison.Ordinal) && !name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> TypeArgumentsOf(string text)
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

    // ---------------------------------------------------------------- taint

    /// <summary>What a value carries: a set of labels, each naming a delegate that borrows authority (or, in a summary run, the
    /// parameter whose fate is computed) and the PATH of fields through which this value holds it. A label with no path is the delegate
    /// itself; <c>label/scalar</c> is an object that holds the delegate in its field <c>scalar</c>. The path makes the analysis
    /// field-sensitive: loading a field of a carrier yields the delegate only when that field is where it was stored, so an object that
    /// merely sits beside a delegate in a carrier is not itself treated as one.</summary>
    private sealed class Taint
    {
        private const char Separator = '\u0002';
        internal static readonly Taint None = new([]);
        private readonly string[] _labels;

        private Taint(string[] labels) => _labels = labels;

        internal bool IsNone => _labels.Length == 0;

        internal static Taint Of(string label) => new([label]);

        internal bool HasMarker => _labels.Any(l => BaseOf(l) == Marker);

        internal Taint Union(Taint other)
        {
            if (other.IsNone || ReferenceEquals(this, other)) return this;
            if (IsNone) return other;
            var merged = _labels.Union(other._labels).Order(StringComparer.Ordinal).ToArray();
            return merged.Length == _labels.Length ? this : new Taint(merged);
        }

        internal bool SameAs(Taint other) => _labels.AsSpan().SequenceEqual(other._labels);

        internal static string BaseOf(string label) { var cut = label.IndexOf(Separator, StringComparison.Ordinal); return cut < 0 ? label : label[..cut]; }

        private static string PathOf(string label) { var cut = label.IndexOf(Separator, StringComparison.Ordinal); return cut < 0 ? "" : label[(cut + 1)..]; }

        /// <summary>The same labels, held one field deeper: this value was stored in <paramref name="field"/> of a new object.</summary>
        internal Taint Held(string field) =>
            IsNone ? this : new Taint([.. _labels.Select(l => Deeper(l, field)).Distinct().Order(StringComparer.Ordinal)]);

        private const int MaxDepth = 3;
        private const string Anywhere = "*";

        /// <summary>One field deeper; beyond <see cref="MaxDepth"/> fields (a recursive structure) the label is held "anywhere", which
        /// every field load yields: less precise, never less safe, and it keeps the number of shapes finite.</summary>
        private static string Deeper(string label, string field)
        {
            var path = PathOf(label);
            if (path == Anywhere || path.Split(Separator).Length >= MaxDepth) return BaseOf(label) + Separator + Anywhere;
            return BaseOf(label) + Separator + field + (path.Length > 0 ? Separator + path : "");
        }

        /// <summary>What loading <paramref name="field"/> yields: the labels held in that field, and (when the value is itself a delegate, with no path, and the
        /// field's type may hold one) the label itself.</summary>
        internal Taint Load(string field, bool mayHoldDirect)
        {
            if (IsNone) return this;
            var result = new List<string>();
            foreach (var l in _labels)
            {
                var path = PathOf(l);
                if (path.Length == 0) { if (mayHoldDirect) result.Add(l); continue; }
                if (path == Anywhere) { result.Add(l); continue; }
                var cut = path.IndexOf(Separator, StringComparison.Ordinal);
                var first = cut < 0 ? path : path[..cut];
                if (first == field) result.Add(BaseOf(l) + (cut < 0 ? "" : Separator + path[(cut + 1)..]));
            }
            return result.Count == 0 ? None : new Taint([.. result.Distinct().Order(StringComparer.Ordinal)]);
        }

        /// <summary>The paths of the labels (empty for a delegate itself): the shape of a value, which a summary is computed for.</summary>
        internal string Shape() => string.Join("|", _labels.Select(PathOf).Distinct().Order(StringComparer.Ordinal));

        /// <summary>The parameter marker with each path of a shape: what a summary run starts from.</summary>
        internal static Taint Marked(string shape) =>
            new([.. (shape.Length == 0 ? [""] : shape.Split('|')).Select(path => path.Length == 0 ? Marker : Marker + Separator + path)]);

        internal Taint WithoutMarkers() => new([.. _labels.Where(l => BaseOf(l) != Marker)]);

        internal IEnumerable<string> Descriptions => _labels.Select(BaseOf).Where(b => b != Marker).Distinct();
    }

    private enum Tag { None, This, FnPtr, LocalAddress, ArgumentAddress }

    private readonly record struct Val(Taint Taint, Tag Tag = Tag.None, int Index = -1, IlAudit.CallSite? Fn = null);

    private sealed record Sink(string Kind, int Offset, string Detail, IReadOnlyList<string> Labels);

    private sealed class Run
    {
        internal readonly List<Sink> Sinks = [];
        internal readonly List<string> Unresolved = [];
        /// <summary>The fields of the object under construction that the seeded parameter was stored in.</summary>
        internal readonly HashSet<string> ToThis = [];
    }

    /// <summary>What a method does with one parameter that carries a delegate (in a given shape): whether it lets it escape, and which
    /// fields of the object it builds (a constructor) keep it.</summary>
    private sealed class Summary
    {
        internal bool Escape;
        internal string? Reason;
        internal readonly HashSet<string> ToThis = [];
    }

    /// <summary>The most (method, parameter, shape) summaries one audit run computes: far above any real call graph; past it a summary
    /// says "escapes", so an analysis that has run away fails closed instead of hanging.</summary>
    private const int MaxSummaries = 20_000;

    private Summary Get(IlAudit.Physical method, int index, string shape)
    {
        if (_summaries.TryGetValue((method, index, shape), out var known)) return known;
        if (_summaries.Count >= MaxSummaries) return new Summary { Escape = true, Reason = $"is beyond the analysis budget of {MaxSummaries} summaries (reached at {method.Key})" };
        var summary = new Summary();
        _summaries[(method, index, shape)] = summary;
        Recompute((method, index, shape));
        return summary;
    }

    private void Recompute((IlAudit.Physical Method, int Index, string Shape) key)
    {
        var summary = _summaries[key];
        var run = Simulate(key.Method, new Dictionary<int, Taint> { [key.Index] = Taint.Marked(key.Shape) }, createSources: false);
        if (run.Sinks.Count > 0 || run.Unresolved.Count > 0)
        {
            summary.Escape = true;
            summary.Reason ??= run.Sinks.Count > 0 ? run.Sinks[0].Detail : run.Unresolved[0];
        }
        summary.ToThis.UnionWith(run.ToThis);
    }

    // ---------------------------------------------------------------- the simulation of one method body

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
        _ => -1,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => -1,
    };

    private const int MaxSteps = 400_000;

    /// <summary>Simulates one method body. With <paramref name="seeds"/>, those arguments start tainted (a summary run); with
    /// <paramref name="createSources"/>, every delegate built from an authority closure is a taint source (a host run).</summary>
    private Run Simulate(IlAudit.Physical method, Dictionary<int, Taint>? seeds, bool createSources)
    {
        var run = new Run();
        if (method.Instructions.Count == 0 || !method.Decoded)
        {
            run.Unresolved.Add($"the body of {method.Key} cannot be read");
            return run;
        }
        var instructions = method.Instructions;
        var indexOf = new Dictionary<int, int>();
        for (var i = 0; i < instructions.Count; i++) indexOf[instructions[i].Offset] = i;
        var argumentCount = method.ParameterTypes.Count + (method.HasThis ? 1 : 0);
        var arguments = new Taint[Math.Max(argumentCount, 1)];
        for (var i = 0; i < arguments.Length; i++) arguments[i] = seeds is not null && seeds.TryGetValue(i, out var seeded) ? seeded : Taint.None;
        var locals = new Dictionary<int, Taint>();
        var isConstructor = method.Name == ".ctor" && method.HasThis;
        var returnsValue = Unmod(method.ReturnType) != "Void";

        for (var round = 0; round < 12; round++)
        {
            var changed = false;
            run.Sinks.Clear();
            run.Unresolved.Clear();
            var entry = new Dictionary<int, Val[]>();
            var work = new Queue<int>();
            var steps = 0;

            void Visit(int index, Val[] stack)
            {
                if (entry.TryGetValue(index, out var old))
                {
                    if (old.Length != stack.Length)
                    {
                        run.Unresolved.Add($"the evaluation stack has {old.Length} and {stack.Length} values where two paths meet at IL_{instructions[index].Offset:X4}");
                        return;
                    }
                    var merged = new Val[old.Length];
                    var grew = false;
                    for (var k = 0; k < old.Length; k++)
                    {
                        var taint = old[k].Taint.Union(stack[k].Taint);
                        if (!taint.SameAs(old[k].Taint)) grew = true;
                        merged[k] = new Val(taint, old[k].Tag == stack[k].Tag ? old[k].Tag : Tag.None, old[k].Index == stack[k].Index ? old[k].Index : -1, old[k].Tag == Tag.FnPtr && stack[k].Tag == Tag.FnPtr && Equals(old[k].Fn, stack[k].Fn) ? old[k].Fn : null);
                        if (merged[k].Tag != old[k].Tag) grew = true;
                    }
                    if (!grew) return;
                    entry[index] = merged;
                }
                else entry[index] = stack;
                work.Enqueue(index);
            }

            Visit(0, []);
            foreach (var region in method.Regions)
            {
                if (indexOf.TryGetValue(region.HandlerStart, out var handler)) Visit(handler, region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter ? [new Val(Taint.None)] : []);
                if (region.Kind == ExceptionRegionKind.Filter && indexOf.TryGetValue(region.FilterStart, out var filter)) Visit(filter, [new Val(Taint.None)]);
            }

            while (work.Count > 0)
            {
                if (++steps > MaxSteps)
                {
                    run.Unresolved.Add($"the analysis of {method.Key} did not finish within {MaxSteps} steps");
                    break;
                }
                var index = work.Dequeue();
                var stack = new List<Val>(entry[index]);
                var instruction = instructions[index];
                var successors = Step(method, instruction, stack, arguments, locals, run, createSources, isConstructor, returnsValue, ref changed, out var fallsThrough);
                var after = stack.ToArray();
                if (fallsThrough && index + 1 < instructions.Count) Visit(index + 1, after);
                foreach (var offset in successors)
                {
                    if (indexOf.TryGetValue(offset, out var target)) Visit(target, after);
                    else run.Unresolved.Add($"a branch at IL_{instruction.Offset:X4} targets IL_{offset:X4}, which is not an instruction");
                }
            }
            if (!changed) break;
        }
        return run;
    }

    /// <summary>The effect of one instruction on <paramref name="stack"/>; returns the branch targets (absolute offsets) and whether
    /// execution can continue with the next instruction.</summary>
    private IEnumerable<int> Step(IlAudit.Physical method, IlAudit.Instr instruction, List<Val> stack, Taint[] arguments, Dictionary<int, Taint> locals, Run run, bool createSources,
        bool isConstructor, bool returnsValue, ref bool changed, out bool fallsThrough)
    {
        var op = instruction.Op;
        var name = op.Name!;
        fallsThrough = true;
        var offset = instruction.Offset;

        Val Pop()
        {
            if (stack.Count == 0)
            {
                run.Unresolved.Add($"the evaluation stack is empty at IL_{offset:X4} ({name})");
                return new Val(Taint.None);
            }
            var v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }
        void Push(Val v) => stack.Add(v);
        void Report(string kind, string detail, Taint taint)
        {
            if (!taint.IsNone) run.Sinks.Add(new Sink(kind, offset, detail, [.. taint.Descriptions]));
        }
        int Index() => instruction.Operand is int i ? i : name.Length > 0 && char.IsDigit(name[^1]) ? name[^1] - '0' : -1;

        // loads and stores of arguments and locals
        if (name.StartsWith("ldarga", StringComparison.Ordinal)) { var i = Index(); Push(new Val(Arg(arguments, i), Tag.ArgumentAddress, i)); return []; }
        if (name.StartsWith("ldarg", StringComparison.Ordinal)) { var i = Index(); Push(new Val(Arg(arguments, i), i == 0 && method.HasThis ? Tag.This : Tag.None, i)); return []; }
        if (name.StartsWith("starg", StringComparison.Ordinal))
        {
            var i = Index();
            var v = Pop();
            if (i >= 0 && i < arguments.Length && !v.Taint.IsNone && !arguments[i].Union(v.Taint).SameAs(arguments[i])) { arguments[i] = arguments[i].Union(v.Taint); changed = true; }
            return [];
        }
        if (name.StartsWith("ldloca", StringComparison.Ordinal)) { var i = Index(); Push(new Val(Local(locals, i), Tag.LocalAddress, i)); return []; }
        if (name.StartsWith("ldloc", StringComparison.Ordinal)) { var i = Index(); Push(new Val(Local(locals, i))); return []; }
        if (name.StartsWith("stloc", StringComparison.Ordinal))
        {
            var i = Index();
            var v = Pop();
            if (!v.Taint.IsNone && !Local(locals, i).Union(v.Taint).SameAs(Local(locals, i))) { locals[i] = Local(locals, i).Union(v.Taint); changed = true; }
            return [];
        }

        switch (name)
        {
            case "nop" or "break" or "unaligned." or "volatile." or "tail." or "constrained." or "readonly." or "no.":
                return [];
            case "ldnull" or "ldstr" or "ldtoken" or "sizeof" or "arglist" or "ldc.i4" or "ldc.i4.s" or "ldc.i8" or "ldc.r4" or "ldc.r8" or "ldc.i4.m1"
                or "ldc.i4.0" or "ldc.i4.1" or "ldc.i4.2" or "ldc.i4.3" or "ldc.i4.4" or "ldc.i4.5" or "ldc.i4.6" or "ldc.i4.7" or "ldc.i4.8":
                Push(new Val(Taint.None));
                return [];
            case "dup":
            {
                var v = Pop();
                Push(v);
                Push(v);
                return [];
            }
            case "pop":
                Pop();
                return [];
            case "ldfld" or "ldflda":
            {
                var obj = Pop();
                var field = instruction.Operand as IlAudit.FieldRef;
                // what the field holds: the labels stored in it, and (for a delegate itself) the delegate when the field's type could hold one
                var taint = obj.Taint.IsNone ? Taint.None : field is null ? obj.Taint : obj.Taint.Load(field.Name, MayHold(field.Type));
                Push(new Val(taint));
                return [];
            }
            case "ldsfld" or "ldsflda":
                Push(new Val(Taint.None));   // a static can hold a tainted value only through a store, which is a sink
                return [];
            case "stfld":
            {
                var value = Pop();
                var obj = Pop();
                if (value.Taint.IsNone) return [];
                var field = instruction.Operand as IlAudit.FieldRef;
                if (obj.Tag == Tag.This && isConstructor)
                {
                    // a constructor keeps the value in a field of the object it builds: the object carries the taint, in that field, from now on
                    var fieldName = field?.Name ?? "?";
                    if (value.Taint.HasMarker) run.ToThis.Add(fieldName);
                    Report("field", $"is stored in the field {field?.Owner}::{field?.Name} of an object under construction, which then holds it", value.Taint.WithoutMarkers());
                    var held = value.Taint.Held(fieldName);
                    if (arguments.Length > 0 && !arguments[0].Union(held).SameAs(arguments[0])) { arguments[0] = arguments[0].Union(held); changed = true; }
                }
                else if (obj.Tag == Tag.LocalAddress)
                {
                    var i = obj.Index;
                    if (!Local(locals, i).Union(value.Taint).SameAs(Local(locals, i))) { locals[i] = Local(locals, i).Union(value.Taint); changed = true; }
                }
                else if (field is not null && field.Owner.Contains("<>c__DisplayClass", StringComparison.Ordinal))
                    Report("field", $"is captured by another closure (the field {field.Owner}::{field.Name}), which can outlive the call", value.Taint);
                else if (field is not null && IlAudit.IsStateMachineType(field.Owner))
                    Report("field", $"is kept across an await or a yield (in the field {field.Owner}::{field.Name} of the state machine), which outlives the call", value.Taint);
                else Report("field", $"is stored in the field {field?.Owner}::{field?.Name}{(obj.Tag == Tag.This ? " of the instance (outside a constructor)" : "")}, where it outlives the call", value.Taint);
                return [];
            }
            case "stsfld":
            {
                var value = Pop();
                var field = instruction.Operand as IlAudit.FieldRef;
                Report("static", $"is stored in the static field {field?.Owner}::{field?.Name}", value.Taint);
                return [];
            }
            case "ldelem" or "ldelem.ref" or "ldelem.i" or "ldelem.i1" or "ldelem.i2" or "ldelem.i4" or "ldelem.i8" or "ldelem.u1" or "ldelem.u2" or "ldelem.u4" or "ldelem.r4" or "ldelem.r8" or "ldelema":
            {
                Pop();
                var array = Pop();
                Push(new Val(name is "ldelem.ref" or "ldelem" or "ldelema" ? array.Taint : Taint.None));
                return [];
            }
            case "stelem" or "stelem.ref" or "stelem.i" or "stelem.i1" or "stelem.i2" or "stelem.i4" or "stelem.i8" or "stelem.r4" or "stelem.r8":
            {
                var value = Pop();
                Pop();
                Pop();
                Report("array", "is stored in an array element", value.Taint);
                return [];
            }
            case "ldind.ref" or "ldobj":
            {
                var pointer = Pop();
                Push(new Val(pointer.Taint));
                return [];
            }
            case "stind.ref" or "stind.i" or "stind.i1" or "stind.i2" or "stind.i4" or "stind.i8" or "stind.r4" or "stind.r8" or "stobj":
            {
                var value = Pop();
                Pop();
                Report("pointer", "is stored through a pointer (a ref or out parameter, or a field address), which the audit does not follow", value.Taint);
                return [];
            }
            case "cpobj":
            {
                var source = Pop();
                Pop();
                Report("pointer", "is copied through a pointer, which the audit does not follow", source.Taint);
                return [];
            }
            case "box" or "unbox.any" or "castclass" or "isinst" or "unbox":
            {
                var v = Pop();
                Push(new Val(v.Taint, name == "castclass" ? v.Tag : Tag.None, name == "castclass" ? v.Index : -1));
                return [];
            }
            case "ldftn":
                Push(new Val(Taint.None, Tag.FnPtr, -1, instruction.Operand as IlAudit.CallSite));
                return [];
            case "ldvirtftn":
            {
                Pop();
                Push(new Val(Taint.None, Tag.FnPtr, -1, instruction.Operand as IlAudit.CallSite));
                return [];
            }
            case "call" or "callvirt" or "newobj":
                Call(method, instruction, stack, run, createSources, isConstructor, arguments, ref changed);
                return [];
            case "calli":
            {
                if (instruction.Operand is not IlAudit.CalliSite site)
                {
                    run.Unresolved.Add($"an indirect call at IL_{offset:X4} whose signature cannot be read");
                    return [];
                }
                Pop();
                var taint = Taint.None;
                for (var i = 0; i < site.ParameterCount + (site.HasThis ? 1 : 0); i++) taint = taint.Union(Pop().Taint);
                Report("calli", "is passed to an indirect call (calli), whose target the audit cannot know", taint);
                if (site.ReturnsValue) Push(new Val(Taint.None));
                return [];
            }
            case "ret":
            {
                if (returnsValue) Report("return", "is returned to the caller", Pop().Taint);
                fallsThrough = false;
                return [];
            }
            case "throw":
            {
                Report("throw", "is thrown", Pop().Taint);
                fallsThrough = false;
                return [];
            }
            case "rethrow" or "endfinally":
                fallsThrough = false;
                return [];
            case "endfilter":
                Pop();
                fallsThrough = false;
                return [];
            case "jmp":
            {
                var taint = arguments.Aggregate(Taint.None, (a, b) => a.Union(b));
                Report("jmp", "is forwarded by a jmp", taint);
                fallsThrough = false;
                return [];
            }
            case "leave" or "leave.s":
                stack.Clear();
                fallsThrough = false;
                return [(int)instruction.Operand!];
            case "br" or "br.s":
                fallsThrough = false;
                return [(int)instruction.Operand!];
            case "switch":
                Pop();
                return (int[])instruction.Operand!;
        }
        if (op.FlowControl == FlowControl.Cond_Branch)
        {
            for (var i = PopCount(op.StackBehaviourPop); i > 0; i--) Pop();
            return [(int)instruction.Operand!];
        }

        // everything else: arithmetic, comparison, conversion, newarr, ldlen, localloc, cpblk ...: the values consumed, one produced, no taint
        var pops = PopCount(op.StackBehaviourPop);
        var pushes = PushCount(op.StackBehaviourPush);
        if (pops < 0 || pushes < 0)
        {
            run.Unresolved.Add($"the instruction {name} at IL_{offset:X4} has a stack effect the audit does not model");
            fallsThrough = op.FlowControl is not (FlowControl.Return or FlowControl.Throw);
            return [];
        }
        var consumed = Taint.None;
        for (var i = 0; i < pops; i++) consumed = consumed.Union(Pop().Taint);
        for (var i = 0; i < pushes; i++) Push(new Val(Taint.None));
        if (op.FlowControl is FlowControl.Return or FlowControl.Throw) fallsThrough = false;
        return [];
    }

    private static Taint Arg(Taint[] arguments, int index) => index >= 0 && index < arguments.Length ? arguments[index] : Taint.None;

    private static Taint Local(Dictionary<int, Taint> locals, int index) => locals.TryGetValue(index, out var t) ? t : Taint.None;

    // ---------------------------------------------------------------- calls

    /// <summary>Calls that retain nothing a delegate could be reached through (a lock, a null check, identity), by exact type and name.</summary>
    private static readonly HashSet<string> Benign =
    [
        "System.Object::.ctor", "System.Object::GetType", "System.Object::ToString", "System.Object::Equals", "System.Object::GetHashCode", "System.Object::ReferenceEquals",
        "System.Threading.Monitor::Enter", "System.Threading.Monitor::Exit", "System.Threading.Monitor::TryEnter", "System.ArgumentNullException::ThrowIfNull", "System.GC::KeepAlive",
    ];

    private void Call(IlAudit.Physical method, IlAudit.Instr instruction, List<Val> stack, Run run, bool createSources, bool isConstructor, Taint[] callerArguments, ref bool changed)
    {
        var offset = instruction.Offset;
        if (instruction.Operand is not IlAudit.CallSite call)
        {
            run.Unresolved.Add($"a call at IL_{offset:X4} whose target cannot be read");
            return;
        }
        var isNew = call.Kind == IlAudit.CallKind.Newobj;
        var hasReceiver = call.HasThis && !isNew;
        var count = call.Parameters.Count + (hasReceiver ? 1 : 0);
        var args = new Val[count];
        for (var i = count - 1; i >= 0; i--)
        {
            if (stack.Count == 0)
            {
                run.Unresolved.Add($"the evaluation stack is empty at IL_{offset:X4} (a call to {call.Short})");
                args[i] = new Val(Taint.None);
                continue;
            }
            args[i] = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
        var returns = isNew || Unmod(call.ReturnType) != "Void";
        var result = Taint.None;

        void Report(string kind, string detail, Taint taint)
        {
            if (!taint.IsNone) run.Sinks.Add(new Sink(kind, offset, detail, [.. taint.Descriptions]));
        }

        // a delegate is built: the target object's taint carries over, and a method that borrows authority is a source
        if (isNew && call.Name == ".ctor" && call.Parameters is [var first, var second] && Unmod(first) == "Object" && Unmod(second) == "IntPtr" && count == 2)
        {
            var taint = args[0].Taint;
            if (args[1].Fn is { } fn && createSources)
            {
                var closures = AuthorityTargetsOf(fn);
                if (closures.Count > 0) taint = taint.Union(Taint.Of(Describe(closures[0], call.Type, method, offset)));
            }
            stack.Add(new Val(taint));
            return;
        }

        var anyTaint = args.Any(a => !a.Taint.IsNone);
        if (!anyTaint)
        {
            if (returns) stack.Add(new Val(Taint.None));
            return;
        }

        if (call.Kind == IlAudit.CallKind.Jmp)
        {
            Report("jmp", "is forwarded by a jmp", args.Aggregate(Taint.None, (a, b) => a.Union(b.Taint)));
            return;
        }

        if (hasReceiver && call.Name == "Invoke" && IsDelegate(call.Type))
        {
            // running the delegate is what it is for; what it is given is judged against every method ever bound to this delegate type
            var receiverIndex = 1;
            for (var a = receiverIndex; a < args.Length; a++)
            {
                if (args[a].Taint.IsNone) continue;
                var targets = _delegateTargets.TryGetValue(call.Type, out var bound) ? bound : [];
                if (targets.Count == 0)
                {
                    Report("unresolved", $"is passed as an argument of {call.Short}, and the audit knows no method bound to that delegate type", args[a].Taint);
                    continue;
                }
                foreach (var target in targets)
                {
                    var s = Get(target, target.HasThis ? a : a - 1, args[a].Taint.Shape());
                    if (s.Escape) Report("call", $"is passed to {call.Short}, which can run {target.Name} ({target.Type}), and that {s.Reason}", args[a].Taint);
                }
            }
            if (returns) stack.Add(new Val(Taint.None));
            return;
        }

        var callees = Targets(call).Where(t => !(t.IsAbstract && call.Dispatches && !t.Decoded)).ToList();
        var benign = Benign.Contains(call.Short) || (call.Name == ".ctor" && call.Type == "System.Object");
        if (callees.Count == 0 || callees.Any(c => !c.Decoded && !c.IsAbstract) )
        {
            if (!benign) Report("external", $"is passed to {call.Short}, which the audit cannot read (external code), so it cannot show that the value is not kept", args.Aggregate(Taint.None, (a, b) => a.Union(b.Taint)));
        }
        foreach (var callee in callees.Where(c => c.Decoded))
        {
            for (var a = 0; a < args.Length; a++)
            {
                if (args[a].Taint.IsNone) continue;
                var parameter = isNew ? a + 1 : a;
                var s = Get(callee, parameter, args[a].Taint.Shape());
                if (s.Escape) Report("call", $"is passed to {callee.Type}::{callee.Name} (parameter {a}), which {s.Reason}", args[a].Taint);
                foreach (var field in s.ToThis)
                {
                    // the callee keeps the value in that field of the object it works on
                    var held = args[a].Taint.Held(field);
                    if (isNew) result = result.Union(held);
                    else if (hasReceiver && args[0].Tag == Tag.This && isConstructor)
                    {
                        // a chained or base constructor: the same object, so this constructor keeps it too
                        if (args[a].Taint.HasMarker) run.ToThis.Add(field);
                        Report("field", $"is kept by {callee.Type}::{callee.Name} in the object under construction", args[a].Taint.WithoutMarkers());
                        if (callerArguments.Length > 0 && !callerArguments[0].Union(held).SameAs(callerArguments[0])) { callerArguments[0] = callerArguments[0].Union(held); changed = true; }
                    }
                    else Report("field", $"is kept by {callee.Type}::{callee.Name} in an object that outlives the call", args[a].Taint);
                }
            }
        }
        if (returns) stack.Add(new Val(isNew ? result : Taint.None));
    }

    private static string Describe(IlAudit.Physical closure, string delegateType, IlAudit.Physical host, int offset)
    {
        var kind = closure.Name.Contains(">g__", StringComparison.Ordinal) ? "the local function" : closure.Name.StartsWith('<') ? "the lambda" : "the method group";
        var shown = closure.Name.StartsWith('<') ? closure.Name : closure.Type.Split('+')[^1] + "::" + closure.Name;
        return $"{kind} {shown} [a {delegateType.Split('.')[^1]} built at IL_{offset:X4}]";
    }
}
