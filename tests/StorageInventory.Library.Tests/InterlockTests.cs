using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-W2: the interlock (OBS-07 to OBS-14) as a state machine. Every (state, request) pair is checked against an independent
/// model written from the specification's table; randomised sequences (deterministic seeds, failing seeds reported) are run
/// against the real interlock and the model in lock step with the invariant checked after every step; and concurrent callers
/// from several threads must never be granted two leases at once.
/// </summary>
public static class InterlockTests
{
    // ---------------------------------------------------------------- the independent model

    private enum K { Idle, Mutating, Observing, Faulted }

    /// <summary>What the specification says, restated as the smallest model: state, kind of the current mutation, its owner, the
    /// epoch, and which operations each state grants or refuses.</summary>
    private sealed class Model
    {
        internal K State = K.Mutating;                 // OBS-14: constructed in Mutating(Open)
        internal MutationKind? Kind = MutationKind.Open;
        internal long Owner;
        internal long Epoch = 1;                       // the start-up lease is a grant
        internal int Resources;
        internal bool Unclean;
        internal bool StartupTaken;

        internal bool BeginMutation(MutationKind kind, long owner)
        {
            if (State != K.Idle || kind == MutationKind.Save) return false;
            State = K.Mutating; Kind = kind; Owner = owner; Epoch++; Resources = 0; Unclean = false;
            return true;
        }

        internal bool BeginObservation(long run)
        {
            if (State != K.Idle) return false;
            State = K.Observing; Kind = null; Owner = run; Resources = 0; Unclean = false;
            return true;
        }

        /// <summary>Prepare → Observing by the same capture; anything else, including an open resource, faults.</summary>
        internal bool HandOffToObservation(bool leaseIsCurrentPrepare)
        {
            if (State == K.Faulted) return false;
            if (!leaseIsCurrentPrepare || State != K.Mutating || Kind != MutationKind.Prepare || Resources != 0 || Unclean) { Fault(); return false; }
            State = K.Observing; Kind = null;
            return true;
        }

        internal bool HandOffToSave(bool leaseIsCurrentObservation)
        {
            if (State == K.Faulted) return false;
            if (!leaseIsCurrentObservation || State != K.Observing || Resources != 0 || Unclean) { Fault(); return false; }
            State = K.Mutating; Kind = MutationKind.Save; Epoch++; Resources = 0; Unclean = false;
            return true;
        }

        internal void EndCurrent()
        {
            if (State is K.Faulted or K.Idle) return;
            if (Resources != 0 || Unclean) { Fault(); return; }
            State = K.Idle; Kind = null; Owner = 0;
        }

        internal void Fault() { State = K.Faulted; Kind = null; Owner = 0; }

        internal void OpenResource() { if (State is K.Mutating or K.Observing) Resources++; }

        internal void CloseResource() { if (State is K.Mutating or K.Observing && Resources > 0) Resources--; }

        internal void ClassC() { if (State is K.Mutating or K.Observing) Fault(); }

        internal void Catastrophic() => Fault();
    }

    private static void AssertSame(Model m, LibraryInterlock i, string step)
    {
        var s = i.Snapshot;
        var expected = m.State switch { K.Idle => InterlockStateKind.Idle, K.Mutating => InterlockStateKind.Mutating, K.Observing => InterlockStateKind.Observing, _ => InterlockStateKind.Faulted };
        Assert.Equal(expected, s.Kind, step + ": state");
        Assert.Equal(m.Kind, s.Mutation, step + ": mutation kind");
        Assert.Equal(m.Epoch, s.Epoch, step + ": epoch");
        if (m.State is K.Mutating or K.Observing) Assert.Equal(m.Owner, s.Owner, step + ": owner");
        // the invariant of OBS-07: a lease id is current only while Mutating or Observing; Faulted and Idle have none
        Assert.Equal(m.State is K.Mutating or K.Observing, s.LeaseId != 0, step + ": a current lease exists exactly while Mutating or Observing");
    }

    // ---------------------------------------------------------------- the constructed state (OBS-14)

    [Test]
    public static void A_new_interlock_is_Mutating_Open_and_refuses_everything_until_the_startup_lease_ends_cleanly()
    {
        var interlock = new LibraryInterlock();
        var s = interlock.Snapshot;
        Assert.Equal(InterlockStateKind.Mutating, s.Kind, "never briefly Idle");
        Assert.Equal(MutationKind.Open, s.Mutation);
        Assert.Equal(LibraryInterlock.OpeningReason, s.Reason, "a scan request is refused with 'Opening the Library…'");

        foreach (var kind in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside })
        {
            Assert.False(interlock.TryBeginMutation(kind, 1, out _, out var why), $"{kind} must be refused before the start-up lease ends");
            Assert.Equal(LibraryInterlock.OpeningReason, why);
        }
        Assert.False(interlock.TryBeginObservation(1, out _, out var observationWhy), "a scan is refused while the start-up open runs");
        Assert.Equal(LibraryInterlock.OpeningReason, observationWhy);

        var lease = interlock.TakeStartupLease();
        Assert.True(interlock.IsCurrent(lease));
        Assert.Equal(1L, interlock.MutationEpoch);
        lease.Dispose();
        Assert.Equal(InterlockStateKind.Idle, interlock.Snapshot.Kind, "Idle is reached for the first time only when the lease ends cleanly");

        // and a scan is now granted
        Assert.True(interlock.TryBeginObservation(1, out var observation, out _));
        observation.Dispose();
    }

    [Test]
    public static void The_startup_lease_is_handed_out_once_and_a_second_request_faults_the_interlock()
    {
        var interlock = new LibraryInterlock();
        var first = interlock.TakeStartupLease();
        Assert.Throws<LeaseViolationException>(() => interlock.TakeStartupLease());
        Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind);
        first.Dispose();
        Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, "ending a lease in Faulted changes nothing");
    }

    [Test]
    public static void A_startup_open_that_ends_with_a_resource_still_open_or_a_class_C_failure_ends_in_Faulted_not_Idle()
    {
        var withResource = new LibraryInterlock();
        var lease = withResource.TakeStartupLease();
        lease.ResourceOpened();
        lease.Dispose();
        Assert.Equal(InterlockStateKind.Faulted, withResource.Snapshot.Kind, "the writer was not closed");

        var classC = new LibraryInterlock();
        var second = classC.TakeStartupLease();
        second.ClassCFailure(new OutOfMemoryException());
        Assert.Equal(InterlockStateKind.Faulted, classC.Snapshot.Kind, "class C: Faulted in the same monitor section, before the lease is even disposed");
        second.Dispose();

        var released = new LibraryInterlock();
        var third = released.TakeStartupLease();
        third.ResourceOpened();
        third.ResourceReleased();
        third.Dispose();
        Assert.Equal(InterlockStateKind.Idle, released.Snapshot.Kind, "a resource opened and released is a clean end");
    }

    // ---------------------------------------------------------------- every (state, request) pair

    private static LibraryInterlock InState(string state, out object? lease)
    {
        var interlock = new LibraryInterlock();
        interlock.TakeStartupLease().Dispose();           // Idle
        lease = null;
        switch (state)
        {
            case "Idle": break;
            case "Mutating(Open)": interlock.TryBeginMutation(MutationKind.Open, 0, out var a, out _); lease = a; break;
            case "Mutating(Create)": interlock.TryBeginMutation(MutationKind.Create, 0, out var b, out _); lease = b; break;
            case "Mutating(Prepare)": interlock.TryBeginMutation(MutationKind.Prepare, 7, out var c, out _); lease = c; break;
            case "Mutating(Delete)": interlock.TryBeginMutation(MutationKind.Delete, 0, out var d, out _); lease = d; break;
            case "Mutating(SetAside)": interlock.TryBeginMutation(MutationKind.SetAside, 0, out var e, out _); lease = e; break;
            case "Mutating(Save)":
                interlock.TryBeginObservation(7, out var o, out _);
                lease = o.HandOffToSave(out _);
                break;
            case "Observing": interlock.TryBeginObservation(7, out var f, out _); lease = f; break;
            case "Faulted": interlock.ReportCatastrophic("test"); break;
            default: throw new ArgumentException(state);
        }
        return interlock;
    }

    private static readonly string[] States = ["Idle", "Mutating(Open)", "Mutating(Create)", "Mutating(Prepare)", "Mutating(Delete)", "Mutating(SetAside)", "Mutating(Save)", "Observing", "Faulted"];

    [Test]
    public static void Every_state_and_mutation_request_pair_grants_only_from_Idle()
    {
        foreach (var state in States)
        {
            foreach (var kind in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside })
            {
                var interlock = InState(state, out _);
                var before = interlock.Snapshot;
                var granted = interlock.TryBeginMutation(kind, 9, out var lease, out var why);
                Assert.Equal(state == "Idle", granted, $"{kind} requested in {state}");
                if (granted)
                {
                    Assert.Equal(InterlockStateKind.Mutating, interlock.Snapshot.Kind);
                    Assert.Equal(before.Epoch + 1, interlock.Snapshot.Epoch, "a grant advances the mutation epoch");
                    lease.Dispose();
                    Assert.Equal(InterlockStateKind.Idle, interlock.Snapshot.Kind);
                }
                else
                {
                    Assert.True(why.Length > 0, $"a refusal in {state} names its reason");
                    Assert.Equal(before, interlock.Snapshot, $"a refusal changes nothing ({state}, {kind})");
                }
            }
            var save = InState(state, out _);
            Assert.False(save.TryBeginMutation(MutationKind.Save, 0, out _, out _), $"Save is never granted directly ({state})");
        }
    }

    [Test]
    public static void Every_state_and_observation_request_pair_grants_only_from_Idle()
    {
        foreach (var state in States)
        {
            var interlock = InState(state, out _);
            var before = interlock.Snapshot;
            var granted = interlock.TryBeginObservation(11, out var lease, out var why);
            Assert.Equal(state == "Idle", granted, $"an observation requested in {state}");
            if (granted)
            {
                Assert.Equal(before.Epoch, interlock.Snapshot.Epoch, "an observation is not a mutation: the epoch does not advance");
                lease.Dispose();
            }
            else
            {
                Assert.True(why.Length > 0);
                Assert.Equal(before, interlock.Snapshot, "a refusal changes nothing");
            }
        }
    }

    [Test]
    public static void Hand_offs_are_atomic_by_the_same_capture_and_nothing_else_may_hand_off()
    {
        // The only legal hand-offs: Prepare → Observing and Observing → Save, each by the current lease.
        var interlock = new LibraryInterlock();
        interlock.TakeStartupLease().Dispose();
        Assert.True(interlock.TryBeginMutation(MutationKind.Prepare, 5, out var prepare, out _));
        var epochAtPrepare = interlock.MutationEpoch;
        var observation = prepare.HandOffToObservation();
        var s = interlock.Snapshot;
        Assert.Equal(InterlockStateKind.Observing, s.Kind, "atomic: Mutating(Prepare) → Observing, never through Idle");
        Assert.Equal(5L, s.Owner, "the same capture owns the window");
        Assert.Equal(epochAtPrepare, s.Epoch, "a hand-off into a window grants no mutation");
        Assert.False(interlock.TryBeginMutation(MutationKind.Create, 0, out _, out _), "no mutation while observing");

        var save = observation.HandOffToSave(out var close);
        Assert.Equal(InterlockStateKind.Mutating, interlock.Snapshot.Kind);
        Assert.Equal(MutationKind.Save, interlock.Snapshot.Mutation);
        Assert.Equal(5L, interlock.Snapshot.Owner, "the same capture owns its Save lease");
        Assert.True(close.Unchanged, "no mutation epoch advance and no other transition during the window");
        Assert.Equal(epochAtPrepare, close.EpochAtOpen);
        Assert.Equal(epochAtPrepare, close.EpochAtClose);
        Assert.Equal(epochAtPrepare + 1, interlock.MutationEpoch, "the Save grant advances the epoch after M2 was read");
        Assert.False(interlock.TryBeginObservation(6, out _, out _), "a scan cannot start while a capture is saving");
        save.Dispose();
        Assert.Equal(InterlockStateKind.Idle, interlock.Snapshot.Kind, "the next scan is eligible exactly when the interlock is Idle again");
    }

    [Test]
    public static void Illegal_hand_offs_fault_before_anything_changes()
    {
        // a Delete lease cannot be handed off
        var a = InState("Mutating(Delete)", out var delete);
        Assert.Throws<LeaseViolationException>(() => ((MutationLease)delete!).HandOffToObservation());
        Assert.Equal(InterlockStateKind.Faulted, a.Snapshot.Kind);

        // a Prepare lease still holding the writer cannot hand off (OBS-12)
        var b = InState("Mutating(Prepare)", out var prepare);
        ((MutationLease)prepare!).ResourceOpened();
        Assert.Throws<LeaseViolationException>(() => ((MutationLease)prepare).HandOffToObservation());
        Assert.Equal(InterlockStateKind.Faulted, b.Snapshot.Kind);

        // a second hand-off of the same lease
        var c = new LibraryInterlock();
        c.TakeStartupLease().Dispose();
        c.TryBeginMutation(MutationKind.Prepare, 1, out var once, out _);
        once.HandOffToObservation();
        Assert.Throws<LeaseViolationException>(() => once.HandOffToObservation());
        Assert.Equal(InterlockStateKind.Faulted, c.Snapshot.Kind);

        // a default lease value
        var d = new LibraryInterlock();
        d.TakeStartupLease().Dispose();
        Assert.Throws<LeaseViolationException>(() => default(MutationLease).HandOffToObservation());
        Assert.Equal(InterlockStateKind.Idle, d.Snapshot.Kind, "a default lease has no interlock to fault (nothing was granted by anyone)");
    }

    // ---------------------------------------------------------------- Faulted is terminal (OBS-13)

    [Test]
    public static void Faulted_is_entered_from_every_lease_kind_by_a_class_C_exception_and_has_no_exit()
    {
        foreach (var state in States.Where(s => s != "Idle" && s != "Faulted"))
        {
            var interlock = InState(state, out var lease);
            switch (lease)
            {
                case MutationLease m: m.ClassCFailure(new OutOfMemoryException()); break;
                case ObservationLease o: o.ClassCFailure(new InsufficientExecutionStackException()); break;
            }
            Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, $"class C in {state}");
            Assert.Equal(0L, interlock.Snapshot.LeaseId, "no lease is current in Faulted");
            AssertNoWayOut(interlock, lease);
        }
    }

    [Test]
    public static void Faulted_is_entered_when_a_release_fails_and_by_an_App_report_from_any_state()
    {
        foreach (var state in States.Where(s => s != "Idle" && s != "Faulted"))
        {
            var interlock = InState(state, out var lease);
            switch (lease)
            {
                case MutationLease m: m.ReleaseFailed("writer"); break;
                case ObservationLease o: o.ReleaseFailed("root handle"); break;
            }
            Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, $"a failed release in {state}");
        }
        foreach (var state in States)
        {
            var interlock = InState(state, out var lease);
            interlock.ReportCatastrophic("an App service saw OutOfMemoryException");
            Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, $"a class C failure outside any lease, while {state}");
            AssertNoWayOut(interlock, lease);
        }
    }

    private static void AssertNoWayOut(LibraryInterlock interlock, object? lease)
    {
        foreach (var kind in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside, MutationKind.Save })
        {
            Assert.False(interlock.TryBeginMutation(kind, 1, out _, out var why), $"Faulted grants no {kind} lease");
            Assert.Equal(LibraryInterlock.FaultedReason, why);
        }
        Assert.False(interlock.TryBeginObservation(1, out _, out _), "Faulted grants no observation");
        switch (lease)
        {
            case MutationLease m: m.Dispose(); m.Dispose(); break;
            case ObservationLease o: o.Dispose(); o.Dispose(); break;
        }
        Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, "disposing a lease while Faulted leaves it Faulted");
        Assert.Equal(0L, interlock.Snapshot.LeaseId);
    }

    // ---------------------------------------------------------------- randomised sequences against the model

    [Test]
    public static void Random_request_sequences_agree_with_the_independent_model_after_every_step()
    {
        for (var seed = 1; seed <= 400; seed++)
        {
            try { RunRandomSequence(seed, steps: 120); }
            catch (Exception ex) when (ex is AssertionException)
            {
                throw new AssertionException($"FAILING SEED {seed}: {ex.Message}");
            }
        }
    }

    private static void RunRandomSequence(int seed, int steps)
    {
        var random = new Random(seed);
        var interlock = new LibraryInterlock();
        var model = new Model();
        MutationLease? mutation = null;
        ObservationLease? observation = null;
        var stale = new List<MutationLease>();
        var startup = interlock.TakeStartupLease();
        mutation = startup;
        model.StartupTaken = true;
        AssertSame(model, interlock, $"seed {seed} constructed");
        var previousEpoch = interlock.MutationEpoch;
        var faultedSeen = false;

        for (var step = 1; step <= steps; step++)
        {
            var action = random.Next(14);
            var label = $"seed {seed} step {step} action {action}";
            switch (action)
            {
                case 0: case 1: case 2:
                {
                    var kind = new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside }[random.Next(5)];
                    var owner = random.Next(1, 4);
                    var expected = model.BeginMutation(kind, owner);
                    var granted = interlock.TryBeginMutation(kind, owner, out var lease, out _);
                    Assert.Equal(expected, granted, label + $" BeginMutation({kind})");
                    if (granted) { if (mutation is { } old) stale.Add(old); mutation = lease; observation = null; }
                    break;
                }
                case 3:
                {
                    var owner = random.Next(1, 4);
                    var expected = model.BeginObservation(owner);
                    var granted = interlock.TryBeginObservation(owner, out var lease, out _);
                    Assert.Equal(expected, granted, label + " BeginObservation");
                    if (granted) { if (mutation is { } old) stale.Add(old); observation = lease; mutation = null; }
                    break;
                }
                case 4:   // hand a Prepare lease to observation (the current one if any, else a stale one)
                {
                    var useStale = stale.Count > 0 && random.Next(3) == 0;
                    var lease = useStale ? stale[random.Next(stale.Count)] : mutation;
                    if (lease is not { } l) break;
                    var current = !useStale && model.State == K.Mutating;
                    var ok = model.HandOffToObservation(current);
                    try { var next = l.HandOffToObservation(); Assert.True(ok, label + " hand-off should have faulted"); observation = next; stale.Add(l); mutation = null; }
                    catch (LeaseViolationException) { Assert.False(ok, label + " hand-off should have succeeded"); }
                    break;
                }
                case 5:   // hand the observation to Save
                {
                    if (observation is not { } o) break;
                    var current = model.State == K.Observing;
                    var ok = model.HandOffToSave(current);
                    try { var next = o.HandOffToSave(out _); Assert.True(ok, label + " save hand-off should have faulted"); mutation = next; observation = null; }
                    catch (LeaseViolationException) { Assert.False(ok, label + " save hand-off should have succeeded"); }
                    break;
                }
                case 6: case 7:   // a clean end of whatever is current
                {
                    if (mutation is { } m) { m.Dispose(); model.EndCurrent(); }
                    else if (observation is { } o2) { o2.Dispose(); model.EndCurrent(); }
                    break;
                }
                case 8:   // dispose a stale lease: harmless
                {
                    if (stale.Count > 0) stale[random.Next(stale.Count)].Dispose();
                    break;
                }
                case 9:
                {
                    if (mutation is { } m) { m.ResourceOpened(); model.OpenResource(); }
                    else if (observation is { } o3) { o3.ResourceOpened(); model.OpenResource(); }
                    break;
                }
                case 10:
                {
                    if (mutation is { } m) { m.ResourceReleased(); model.CloseResource(); }
                    else if (observation is { } o4) { o4.ResourceReleased(); model.CloseResource(); }
                    break;
                }
                case 11:
                {
                    if (random.Next(4) != 0) break;   // class C is rare
                    if (mutation is { } m) { m.ClassCFailure(new OutOfMemoryException()); model.ClassC(); }
                    else if (observation is { } o5) { o5.ClassCFailure(new OutOfMemoryException()); model.ClassC(); }
                    break;
                }
                case 12:
                {
                    if (random.Next(6) != 0) break;
                    interlock.ReportCatastrophic("random");
                    model.Catastrophic();
                    break;
                }
                default:  // 13: Save requested directly
                {
                    var granted = interlock.TryBeginMutation(MutationKind.Save, 1, out _, out _);
                    Assert.False(granted, label + " Save is never granted directly");
                    break;
                }
            }
            AssertSame(model, interlock, label);
            Assert.True(interlock.MutationEpoch >= previousEpoch, label + ": the epoch never decreases");
            previousEpoch = interlock.MutationEpoch;
            if (interlock.IsFaulted) faultedSeen = true;
            if (faultedSeen) Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind, label + ": Faulted never returns to another state");
        }
    }

    // ---------------------------------------------------------------- concurrent callers

    [Test]
    public static void Concurrent_callers_are_never_granted_two_leases_and_a_window_never_overlaps_a_mutation()
    {
        for (var round = 0; round < 20; round++)
        {
            var interlock = new LibraryInterlock();
            interlock.TakeStartupLease().Dispose();
            var holders = 0;           // leases held right now, any kind
            var mutating = 0;
            var observing = 0;
            var violations = 0;
            var grants = 0L;
            var mutationGrants = 0L;
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                var random = new Random(round * 100 + t);
                for (var i = 0; i < 3000; i++)
                {
                    if (random.Next(2) == 0)
                    {
                        var kind = (MutationKind)random.Next(1, 7);
                        if (kind == MutationKind.Save) kind = MutationKind.Create;
                        if (!interlock.TryBeginMutation(kind, t, out var lease, out _)) continue;
                        Interlocked.Increment(ref grants);
                        Interlocked.Increment(ref mutationGrants);
                        if (Interlocked.Increment(ref holders) != 1) Interlocked.Increment(ref violations);
                        Interlocked.Increment(ref mutating);
                        if (Volatile.Read(ref observing) != 0) Interlocked.Increment(ref violations);
                        Thread.SpinWait(random.Next(50));
                        Interlocked.Decrement(ref mutating);
                        Interlocked.Decrement(ref holders);
                        lease.Dispose();
                    }
                    else
                    {
                        if (!interlock.TryBeginObservation(t, out var lease, out _)) continue;
                        Interlocked.Increment(ref grants);
                        if (Interlocked.Increment(ref holders) != 1) Interlocked.Increment(ref violations);
                        Interlocked.Increment(ref observing);
                        if (Volatile.Read(ref mutating) != 0) Interlocked.Increment(ref violations);
                        Thread.SpinWait(random.Next(50));
                        Interlocked.Decrement(ref observing);
                        Interlocked.Decrement(ref holders);
                        lease.Dispose();
                    }
                }
            })).ToList();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            Assert.Equal(0, violations, $"round {round}: two leases were held at once or a window overlapped a mutation");
            Assert.True(Interlocked.Read(ref grants) > 100, "the threads were granted leases");
            Assert.Equal(InterlockStateKind.Idle, interlock.Snapshot.Kind, $"round {round}: everything ended cleanly");
            Assert.Equal(1L + Interlocked.Read(ref mutationGrants), interlock.MutationEpoch, "the epoch is 1 plus the number of mutation grants; observations do not advance it");
        }
    }

    [Test]
    public static void Concurrent_class_C_reports_and_requests_leave_Faulted_terminal()
    {
        for (var round = 0; round < 30; round++)
        {
            var interlock = new LibraryInterlock();
            interlock.TakeStartupLease().Dispose();
            var granted = 0;
            var late = 0;
            var faulted = 0;
            var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
            {
                var random = new Random(round * 10 + t);
                for (var i = 0; i < 2000; i++)
                {
                    if (t == 0 && i == 500) { interlock.ReportCatastrophic("concurrent"); Volatile.Write(ref faulted, 1); }
                    var wasFaulted = Volatile.Read(ref faulted) == 1;
                    if (interlock.TryBeginMutation(MutationKind.Delete, t, out var lease, out _))
                    {
                        Interlocked.Increment(ref granted);
                        if (wasFaulted) Interlocked.Increment(ref late);   // granted after Faulted was entered: forbidden
                        lease.Dispose();
                    }
                }
            })).ToList();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            Assert.Equal(0, late, $"round {round}: a lease was granted after Faulted");
            Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind);
        }
    }
}
