using System.Text.RegularExpressions;

namespace StorageInventory.Library.Tests;

/// <summary>
/// A-26 (SQLite side-file lifecycle) as a helper: a directory watcher over the app-data root and a names-only listing taken at every
/// interlock transition (through the interlock's test hook), so that every create, delete and rename in the Library directory is
/// attributed to the interlock state, and for a mutation to its LEASE KIND, in which it happened.
/// <para><b>Attribution without time margins (C4-M07).</b> The watcher delivers events asynchronously, so an event's delivery time says
/// nothing about when it happened. Each transition therefore takes a <i>fence</i> in the transition hook: it creates a marker file in
/// the app-data root (outside the Library directory) and waits until the watcher has delivered the marker's own event. A watcher
/// delivers in order, so every event of the Library directory that happened before the transition has been delivered by then, and the
/// number delivered so far is the transition's position in the event stream. A segment of the stream belongs to the state that
/// followed the transition at its start, exactly; no window is ignored, however short (the five reads of an observation are judged
/// like everything else).</para>
/// <para><b>Lost events.</b> <see cref="FileSystemWatcher.Error"/> (a buffer overflow, a lost watch) is recorded, and any error or a
/// fence that did not arrive is a violation: an audit that may have missed events proves nothing.</para>
/// </summary>
internal sealed class SideFileAudit : IDisposable
{
    internal sealed record Transition(int EventsBefore, InterlockSnapshot State, List<string> Names);

    private const string FenceName = "audit-fence-";

    private readonly World _world;
    private readonly FileSystemWatcher _watcher;
    private readonly List<string> _events = [];
    private readonly List<string> _errors = [];
    private readonly HashSet<string> _fences = [];
    private readonly object _lock = new();
    private int _fenceCount;

    internal List<Transition> Transitions { get; } = [];

    internal SideFileAudit(World world, LibrarySession session)
    {
        _world = world;
        Directory.CreateDirectory(world.AppData);
        _watcher = new FileSystemWatcher(world.AppData) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
        void Record(string verb, string? name)
        {
            var text = Normalise(name);
            lock (_lock)
            {
                if (text.StartsWith(FenceName, StringComparison.Ordinal)) { if (verb == "Created") _fences.Add(text); }
                else _events.Add(verb + " " + text);
            }
        }
        _watcher.Created += (_, e) => Record("Created", e.Name);
        _watcher.Deleted += (_, e) => Record("Deleted", e.Name);
        _watcher.Renamed += (_, e) => { lock (_lock) _events.Add("Renamed " + Normalise(e.OldName) + " -> " + Normalise(e.Name)); };
        _watcher.Error += (_, e) => { lock (_lock) _errors.Add("the watcher lost events: " + e.GetException().Message); };
        _watcher.EnableRaisingEvents = true;
        session.Interlock.TransitionHook = state =>
        {
            Fence();
            lock (_lock) Transitions.Add(new Transition(_events.Count, state, world.Names()));
        };
        // the construction state itself (OBS-14) is the first "transition"
        lock (_lock) Transitions.Add(new Transition(0, session.Interlock.Snapshot, world.Names()));
    }

    private static string Normalise(string? name) => (name ?? "").Replace("Library\\", "", StringComparison.Ordinal);

    /// <summary>Creates the fence marker and waits for the watcher to deliver it: everything that happened before this call has been
    /// delivered when it returns.</summary>
    private void Fence()
    {
        var name = FenceName + Interlocked.Increment(ref _fenceCount) + ".marker";
        var path = Path.Combine(_world.AppData, name);
        File.WriteAllBytes(path, []);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var delivered = false;
        while (!delivered && DateTime.UtcNow < deadline)
        {
            lock (_lock) delivered = _fences.Contains(name);
            if (!delivered) Thread.Sleep(1);
        }
        if (!delivered) lock (_lock) _errors.Add("the fence " + name + " was not delivered: the watcher missed events");
        File.Delete(path);
    }

    /// <summary>Every event delivered so far, after a final fence (events arrive on another thread).</summary>
    internal List<string> Events()
    {
        Fence();
        lock (_lock) return [.. _events];
    }

    /// <summary>The A-26 rule over what was recorded, plus A-23 over the final directory listing.</summary>
    internal List<string> Violations()
    {
        var events = Events();
        List<Transition> transitions;
        List<string> errors;
        lock (_lock)
        {
            transitions = [.. Transitions];
            errors = [.. _errors];
        }
        return Evaluate(events, transitions, errors, _world.Names());
    }

    private static readonly Regex Quarantine = new(
        @"^Renamed library\.(sqlite3|sqlite3-journal|sqlite3-wal|sqlite3-shm) -> library\.damaged-\d{8}_\d{6}-[0-9a-f]{6}\.(sqlite3|sqlite3-journal|sqlite3-wal|sqlite3-shm)$",
        RegexOptions.CultureInvariant);

    /// <summary>Whether an event is one that the product may cause under a mutation lease of this kind (§6.3, A-26). Everything else, in
    /// any state, is a violation.</summary>
    internal static bool Permitted(MutationKind kind, string text) => text switch
    {
        "Created Library" => kind is MutationKind.Create or MutationKind.Prepare,                                   // LIB-07 step 2
        "Created library.lock" => kind is MutationKind.Open or MutationKind.Create or MutationKind.Prepare or MutationKind.SetAside,   // LIB-07 step 3, LIB-08 step 2, LIB-13 step 2
        "Created library.sqlite3" => kind is MutationKind.Create or MutationKind.Prepare,                           // LIB-07 step 5, CreateNew
        "Created library.sqlite3-journal" => kind is MutationKind.Open or MutationKind.Create or MutationKind.Prepare or MutationKind.Save or MutationKind.Delete,   // SQLite, at its first write
        "Deleted library.sqlite3-journal" => kind is MutationKind.Open or MutationKind.Create or MutationKind.Prepare,   // a hot journal rolled back while opening an existing Library (LIB-08 step 5)
        _ => kind == MutationKind.SetAside && Quarantine.IsMatch(text),                                             // LIB-13: the only renames
    };

    /// <summary>The rule as a pure function of an event stream and its transitions (so that a test can feed it a violation that the
    /// product cannot be made to commit): events in a segment of an Idle, Observing or Faulted state are all violations; in a Mutating
    /// segment each must be permitted for that lease kind; watcher errors and a lost fence are violations; the final directory holds
    /// only owned members.</summary>
    internal static List<string> Evaluate(IReadOnlyList<string> events, IReadOnlyList<Transition> transitions, IReadOnlyList<string> watcherErrors, IReadOnlyList<string> finalNames)
    {
        var problems = new List<string>(watcherErrors);
        for (var i = 0; i < transitions.Count; i++)
        {
            var state = transitions[i].State;
            var from = transitions[i].EventsBefore;
            var to = i + 1 < transitions.Count ? transitions[i + 1].EventsBefore : events.Count;
            for (var e = from; e < to && e < events.Count; e++)
            {
                if (state.Kind == InterlockStateKind.Mutating)
                {
                    if (!Permitted(state.Mutation!.Value, events[e])) problems.Add($"transition {i} (Mutating {state.Mutation}): an event this lease may not cause: {events[e]}");
                }
                else
                {
                    problems.Add($"transition {i} ({state.Kind}): event while the interlock is not Mutating: {events[e]}");
                }
            }
            // the names-only listing as a second, independent witness
            if (state.Kind != InterlockStateKind.Mutating && i + 1 < transitions.Count && !transitions[i].Names.SequenceEqual(transitions[i + 1].Names))
            {
                problems.Add($"transition {i} ({state.Kind}): the names changed from [{string.Join(",", transitions[i].Names)}] to [{string.Join(",", transitions[i + 1].Names)}]");
            }
        }
        foreach (var name in finalNames)   // A-23: the Library directory holds only the owned members
        {
            if (!(name is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile || name.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)))
            {
                problems.Add("a file outside the owned set: " + name);
            }
        }
        return problems;
    }

    public void Dispose() => _watcher.Dispose();
}
