# C4R-M05: the A-25 audit and the two genuine violations it passed

Branch `v1.1/c4-fixes-m05`, created from the review commit `7b41022`. The finding is section 10.3 of `docs/v1.1-c4-repair-review.md`
(section 25.5; item 4 of section 27; observations C4R-O06 and C4R-O02 are its neighbours). **Nothing in `src/` changed**: the product already
passes the repaired audit. What changed is the audit (`tests/StorageInventory.IntegrationTests`: `IlAudit.cs`, the new `IlClosures.cs`, the
fixtures, `SyntheticIl.cs`), the run-time tests (`tests/StorageInventory.Library.Tests/LeaseRuntimeAbuseTests.cs`) and the mutant scripts
(`tests/mutation`). No `.csproj`, lock file or workflow was touched; the specification was not edited.

## 1. The reviewer's mutants, reproduced first (before any change)

Environment: the worktree `C:\sw\m05` at `7b41022`, with junctions to the main checkout's `packages`, `tools\dotnet` and `tools\dotnet-state`
(`packages` is needed by A-11/A-20; the worktree's `packages` folder is a link, nothing is copied). Audit run: the test executable of
`tests\StorageInventory.IntegrationTests` with the filters `LeaseAuditTests SecurityAuditTests LibrarySecurityAuditTests` (51 tests; the
unmutated baseline is **51 PASS, 0 FAIL**).

**Mutant 1, the generic-arity alias** (`LibraryDatabase.cs`, class `WriterConnection`): after `internal bool InTransaction => _inTransaction;`

```csharp
    private T? RvH<T>(string sql) where T : class => null;

    internal object? RvH(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
```

and, in `Configure(MutationLease lease, bool importCache)` right after `Guard(lease, "configure the connection");`: `_ = RvH<object>("x");`.
Build, run: **51 PASS, 0 FAIL**. The audit was evaded: `CallSite.Key` and `Physical.Key` carried no generic arity, so both `RvH` methods were one
unit, which took `IsPrivate` and `TakesLease` from the first and the calls of both.

**Mutant 2, the retained delegate**: in `WriterConnection`, the same anchor, `private Func<string, object?>? _retained;` and
`internal object? Poke(string sql) => _retained!(sql);`, and in `Configure` after the guard `_retained = sql => Scalar(lease, sql);`. The
`LibraryStore` variant: `private Func<bool>? _retainedCreate;`, `internal bool PokeCreate() => _retainedCreate!();` before
`AcquireWriterLock(MutationLease lease)`, and `_retainedCreate = () => CreateEmptyDatabase(lease);` as its second statement. Both together:
**51 PASS, 0 FAIL**. The lambda's body is attributed to its host (a method that takes the lease), and an invocation of a delegate is not a call
the audit resolves.

After the repair both are killed (section 6: C4R-M05-A01 and A05/A06 against the final tree), and each, run against the earlier audit
(`7b41022`), survives (section 6, "before").

## 2. Method identity (review section 10.3, first half)

One function, `IlAudit.MethodKey(type, name, genericArity, parameters, returnType)`, builds the key of a definition (`Physical.Key`), of a call
(`CallSite.Key`) and of the dispatch tables. It is the declaring type, the name, the **generic arity** (a backtick and the number, for a generic
method), and the **full parameter signature decoded exactly**: generic instantiations with their arguments, by-reference, pointer, array **with
its rank** (`T[]`, `T[*]`, `T[,]`, `T[,,]`; before, every multi-dimensional array was written `[,]`), function pointers **with their
signature** (before, a constant `fnptr`), and custom modifiers (`modreq`, `modopt`). The one thing a signature of parameters does not
distinguish is the return type of a conversion operator, which C# allows to differ (`op_Implicit` to `int` and to `long`): those two names carry
the return type. The metadata token is kept (`Physical.Definition` = assembly and `MethodDef` token; `CallSite.Definition` for a call that names a
definition of the assembly being read), and a call to a definition of the same assembly resolves by token.

**Two non-generated methods that still share a key are an audit failure** (`A25a/ambiguous-method-identity`), reported by
`MutationViolations`, never merged into one unit. In the four first-party assemblies there are none (checked; the test is
`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts`). Written as IL, two methods of one type that differ only
by return type, and a static and an instance method with the same parameters, are both rejected (`Synth.Cases::Dup`, `Twin`).

Fixtures (negative): `ArityAliasNonGenericAfterGeneric` and `ArityAliasGenericAfterNonGeneric` (the reviewer's shape, both orders),
`ArityDiffers` (`Make<T>` against `Make<T,U>`), `GenericPathOnly` (the primitive reachable only through the generic overload),
`ParameterTypesDiffer` (same parameter count; a string, an int, two instantiations of `List<>`, two array ranks), `ByReferenceDiffers`;
compliant: `CompliantGenericOverloads` (three overloads, all private, called under the lease), `ConversionSource`. The test
`A_25_overloads_that_differ_only_by_generic_arity_or_shape_are_different_methods` shows that under the earlier key each fixture pair is one
method, and that each is now a unit of its own with its own privacy and callers.

## 3. Closures (review section 10.3, second half)

### How the C# compiler represents the constructs (read from the product's IL; all handled)

| Source construct | IL |
|---|---|
| Lambda that captures nothing | an instance method `<Host>b__N_M` of the singleton `<>c`; the delegate is cached in the static field `<>9__N_M` (a store to a static: a sink) |
| Lambda that captures only `this` | an instance method `<Host>b__N_M` of the host's own type |
| Lambda that captures locals or parameters (a lease is one) | an instance method of a `<>c__DisplayClassN_M` object; the captured values are fields of it (`stfld` into the display class) |
| Local function converted to a delegate | the same as a lambda, named `<Host>g__Name\|N_M` |
| Local function only ever called | a plain `call`; its captures are a **struct** display class passed by `ref` (cannot escape; no analysis needed) |
| Method group | `ldftn` (`ldvirtftn` for a virtual) of an ordinary method |
| Delegate creation | `ldftn`/`ldvirtftn` then `newobj Delegate::.ctor(object, native int)` |
| Delegate invocation | `callvirt Delegate::Invoke` |
| Async lambda | a stub that starts the state machine `<<Host>b__N_M>d`; the work is in its `MoveNext` |
| Closure held across an `await` | a field of the state machine (`stfld` on `this` outside a constructor) |
| Event subscription | `callvirt add_Event(value)`, whose body combines it with `Delegate.Combine` |

### The rule (`IlClosures.cs`)

An **authority closure** is a method that is bound to a delegate, takes no lease parameter itself, and reaches a mutation (a primitive, a leased
operation, a `calli`, the invocation of a delegate that takes a lease, or any callee that does; allow-listed read-only methods end the search; an
async lambda includes its `MoveNext`). Its authority is borrowed from the host that built the delegate (the frozen wording accepts the host
because the closure's calls are part of the host's unit), which is sound only while the delegate stays inside the host's call. So:

* The delegate is **tainted** from the `newobj`. Taint follows the evaluation stack, locals, arguments, casts, boxing and field loads (with a
  **path of fields**: an object that keeps the delegate in one field carries it only in that field, to depth three, then "anywhere").
* It must not reach a **sink**: a store to a static, to an array element, to an instance field of any object other than the one a constructor is
  building (a display class, a state machine, an object of the host), a store through a pointer, a return, a throw, a call the audit cannot read
  (any BCL method that is handed it: `Task.Run`, `Delegate.Combine`, `DynamicInvoke`, a collection's `Add`), an indirect call, or a call whose callee
  **lets that parameter escape**.
* A call to a first-party method is judged by a **summary** of that method's parameter, computed by the same analysis to a fixed point (a summary
  only gains escapes): does it let the value escape? which fields of the object it builds (a constructor) keep it, so that the new object carries the
  taint? Interface and virtual calls are judged against every implementer in the model; a call through a delegate against every method ever bound to
  that delegate type. **The invocation of a tainted delegate is not a sink**: it runs inside the call that holds it.
* The Library's accepted shape therefore passes, and is *proved*, not exempted: `SnapshotImporter.Execute` and `LibrarySession.CheckIdentity` each
  build a `DelegateQueryRunner` from two closures that capture the lease (`<Execute>b__0/b__1`, `<CheckIdentity>b__0/b__1`; these four are the
  only authority closures of the product, pinned by the test); the constructor keeps them only in its own two fields; the runner is used by the
  host and handed to `SnapshotVerifier.Verify`, which passes it to private helpers and calls its `Scalar` and `Rows`; nothing stores, returns or
  forwards it. `RunStartup` and `TryRunMutation` hand the lease to a lambda **as a parameter**: that lambda takes a lease, so it is a unit of its own
  and borrows nothing. A delegate that **takes** a lease is stored freely; whoever invokes it must hold a lease: **the invocation of a delegate
  that takes a `MutationLease` is a call to a leased operation** (`IlAudit.InvokesLeaseTakingDelegate`), except inside the interlock, which
  issues leases (the only two invokers in the product are `RunStartup` and `TryRunMutation`, pinned by the test).
* An **iterator** that reaches a mutation is rejected outright: it runs lazily, after the host returned, and is not a delegate the analysis can
  follow (none exists in the product).
* No compiler-generated method is exempt: they are analysed like any other. Production code was **not** changed to fit the rule.

### Fail closed

Reported as `A25a/unresolved-closure-flow` (the method and the form are named): a body the simulation cannot read to the end (a stack it cannot
reconcile where two paths meet, an instruction without a modelled stack effect, an unresolvable token), an analysis that does not converge or runs past
its budget (20,000 summaries, 400,000 steps). A call into code the audit cannot read with the delegate as an argument or receiver is a violation
(`A25a/closure-escapes`, "the audit cannot read (external code)"). The engine's own self-test simulates every body of the four assemblies, plain and
with its first argument carrying a delegate: 3,664 bodies, none unreadable (it found one real bug while being written: the `modreq(IsExternalInit)`
on an init setter's return type made the simulation think the setter returned a value).

### Stated limits (nothing is claimed beyond these)

* A delegate that is merely **invoked** is not followed into what the closure does; that is the host's authority, judged by the frozen wording.
* The BCL is external code, never a safe sink: `Task.Run(() => ...)`, an event, a collection, `DynamicInvoke`, a tuple of a delegate are all
  rejected even where harmless (use `Invoke`, a local variable, a first-party helper that only runs it).
* A value is tracked by its fields, not by general points-to: a store into any object other than the one under construction is a sink, so an
  `init` property or an object initializer that receives an authority closure is rejected (conservative).
* Arguments of a delegate invocation are judged against every method bound to that delegate type (by generic type, variance ignored): coarse.
* `Reaches` treats a cycle by its other edges (a recursion that mutates only through itself is not seen as reaching).
* The set of authority closures is **pinned** (four): adding one, even a safe one, fails
  `A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` until a reviewer extends the pin (the controls of
  section 6 show it is the only test that fails for them).
* Not changed, still as the review states (C4R-O06): nothing forbids lease **producers**; part (c) is name-based and forgeable; a hand-written
  producer that runs a lease-taking lambda with a fresh lease is met literally. The run-time check remains the final defence.

## 4. Tests added (all in `tests/StorageInventory.IntegrationTests` unless noted)

* `A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` (positive: no ambiguous key, generic methods carry
  their arity, exactly the four authority closures and no escape, the interlock is the only invoker of a lease-taking delegate);
  `A_25_the_closure_analysis_reads_every_method_body_of_the_product` (engine self-test).
* `A_25_overloads_that_differ_only_by_generic_arity_or_shape_are_different_methods`;
  `A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` (every closure fixture is rejected only by the closure rule: the earlier
  rules accepted it).
* `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule` and `A_25_part_a_accepts_the_compliant_fixtures` (extended): each genuine
  violation fails for **its own rule and reason**. Rules: `A25a/not-private`, `A25a/caller-without-lease`, `A25a/ambiguous-method-identity`,
  `A25a/closure-escapes`, `A25a/unresolved-closure-flow`. New negative fixtures: a closure stored in an instance field, a static field, an array
  element, through a property setter, returned (by a lease-taking method and by a lease-less one), handed to a method that keeps it, to one that
  returns it, to the thread pool, to a delegate whose target keeps it, subscribed to an event, captured by a second closure, kept across an `await`,
  a stored local function, a stored method group of a private helper, a stored async lambda, an iterator, run by `DynamicInvoke`, a private lease-less
  host that stores one, the Library's own runner kept in a field, returned, or handed to a first-party method that keeps it, a carrier whose delegate
  field is returned, a lease-taking delegate invoked with a retained lease, and `Closures::Unresolved` written as IL. Compliant: a closure invoked in
  the host (`CompliantLambdaCapturingAHostLease`, **changed**: it used to return its closure, which is now the violation), the production runner shape
  (`CompliantRunnerForTheVerifier`), a closure handed to a helper that only runs it, to a declared delegate that only runs it, a carrier handed to
  code that reads another field (`CompliantCarrierFieldSensitivity`), a lambda that takes a lease, `Closures::InvokedInTheHost`.
* The A-21 registry has two new rows (`A-25 (a) method identity`, `A-25 (a) closures`).
* Library.Tests, `LeaseRuntimeAbuseTests` (7 new): a delegate that captured the opener's lease **works** while the lease is current for every
  writer operation; after the lease was **disposed**, after it was **handed off**, after the interlock granted a **newer lease** of the same kind,
  and when a delegate that **takes** a lease is run by a caller that has none (a default lease), with a lease of another session, or with a stale lease,
  each of the 20 writer operations throws `LeaseViolationException` ("Refused before any I/O"), with the same side channels as the existing cases
  (no statement, no BEGIN/COMMIT/ROLLBACK, no scope, no file changed) and the interlock Faulted. `RunAbuse` now takes the form of the call.
* Changed existing tests: `C4_M17_every_member_access_follows_the_writer_lock` strips the arity from the keys it compares with the policy's
  `Type::Name` entries; `A_25_the_allow_lists_are_narrow_exact_and_load_bearing` accepts that removing an allow-list entry also makes a closure that
  calls it an authority closure (`ReadDictionaryFootprintAsync`'s lambda reaches `ReaderConnection.Rows`; it is then rejected as stored in a state
  machine), so the allow-list is load-bearing for closures too.

## 5. Mutants

All runs are on the final tree (`a921077`, plus the untracked notes), in a scratch copy (the source tree is never modified), in Release. A mutant is
KILLED only when a test of its filter fails that passes in the unmutated baseline, **and** that test is one the mutant is meant to be caught by
(Intended); KILLED (UNINTENDED) when only other tests fail. The baseline of the new run is the audit filter (`LeaseAuditTests`, `SecurityAuditTests`,
`LibrarySecurityAuditTests`): 55 PASS, 0 FAIL.

### Totals

| Group | Mutants | Result on the final tree |
|---|---|---|
| **Genuine violations** written into the product (C4R-M05-A01 to A20): the reviewer's generic-arity mutant (A01) and its order, arity and generic-path variants (A02 to A04), the reviewer's retained delegate in `WriterConnection` (A05) and the `LibraryStore` variant (A06), a static field, a returned closure (lease-taking and lease-less host), a closure handed to a helper that keeps it, a local function, a lambda in a private lease-less host, the runner kept by `CheckIdentity` and by the verifier, an iterator, a lease-taking delegate run with the writer's kept lease, an event, the thread pool, an async lambda, an array | 20 | **20 KILLED**, each by `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a` (its intended test), 0 survived, 0 killed for another reason |
| **Defects in the audit itself** (S01 to S15): the key without arity, no ambiguity check, the closure rule off, each sink removed in turn, summaries ignored, authority closures not recognised, async bodies and iterators not followed, lease-taking delegate invocation not counted | 15 | **15 KILLED** by the fixture tests (`A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`, `A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects`, `A_25_overloads_that_differ_only_by_generic_arity_or_shape_are_different_methods`) |
| **Controls**, shapes the audit must ACCEPT (E01 to E04): a closure handed to a helper that only runs it, built and run in the host, generic overloads that are all private, a lease-taking lambda stored and run by a method that takes the lease | 4 | **4 EQUIVALENT** (accepted: not kills, not counted as kills). The one test that fails for them is the pin of the authority-closure set, which is tolerated by design; the test that asserts the audit of the product finds no violation passes for all four |
| The original audit and lease mutants RA-01 to RA-27 (`Invoke-C4RepairMutants.ps1`) | 27 | **27 KILLED**, 0 unintended, 0 survived, 0 not compiled (5.2) |
| The original C4 audit mutants C4-A1 to C4-A4 and the lease guards C4-01, C4-05, C4-07, C4-13 (`Invoke-C4Mutants.ps1 -Set Original`) | 8 | **8 KILLED**, 0 survived (5.3) |

None regressed. The new delegate tests also fail for the run-time mutants RA-16, RA-18, RA-19, RA-20 and C4-01, C4-05, C4-07, C4-13 (for
example C4-01 and C4-05 by `A_delegate_that_captured_the_lease_is_refused_after_the_interlock_granted_a_newer_lease...`), so they are load-bearing
for the run-time defence; RA-17, RA-23 and RA-27 are killed by the existing tests only (the first five failing tests of each are listed below).
Unmutated whole suites on the same tree: Library.Tests 206 PASS (199 + the 7 new); the audit filter 55 PASS.

### What the earlier audit (`7b41022`) did with the same mutants

The 24 genuine and control mutants were run against the tree at `7b41022` (a scratch copy of its `git archive`, with the new mutant scripts; the
audit tests are the earlier ones). **17 of the 20 genuine mutants SURVIVED the earlier audit**, among them the reviewer's two: the generic-arity
mutant (A01) and the retained delegate in `WriterConnection` (A05), and the `LibraryStore` variant (A06). Three were rejected by it:
A04 (a lease-less method calling a private generic helper: the aliased key collected both callers, so the lease-less one was seen by luck, by the
union of the callers), A09 (a lease-less method returning a closure: the host rule, as the review's table says) and A08 (killed only by an unrelated
A-05 text rule that dislikes the mutant's `string sql` parameter; the A-25 audit accepted it). The four controls were accepted, as they must be. The
repaired audit kills all 20 for their own reason.

### Notes on the runs (honest account)

* The first full run of the new set found three defects **in the mutant definitions**, not in the audit: A16 did not compile (a field named like an
  existing one); E01, E02 and E04 were "rejected" only because they add an authority closure to the product and so fail the **pin** of the set of four
  (a designed review gate: any new delegate that borrows authority needs a reviewer to extend the pin), and E04's text also tripped the A-05 SQL text rule.
  The definitions were corrected (a `Tolerated` list for the pin; no SQL call in the controls) and the whole set re-run: the table below is that run.
* **No genuine mutant was equivalent or non-applicable.** The equivalents are the four controls, which are accepted by design.
* `Invoke-C4Mutants.ps1` gained `-ToolRoot`, `Intended`, `Equivalent` and `Tolerated` (existing mutants have none and behave as before).
  `Invoke-C4RepairMutants.ps1`: RA-09 now edits `IlAudit.MethodKey` (both keys are built there), the same defect as before.

### 5.1 The 39 new mutants, on the final tree (`tests/mutation/Invoke-C4Mutants.ps1 -Set Repair`, suite Audit; before = the earlier audit at `7b41022`)

| Mutant | Kind | Defect | Earlier audit (`7b41022`) | Repaired audit | Tests that fail (repaired audit) |
|---|---|---|---|---|---|
| C4R-M05-A01 | genuine violation | the reviewer's arity mutant: a private generic RvH&lt;T&gt; that Configure calls, then an internal NON-generic RvH(string) that runs a command on the writer without a lease | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_lease_less_members_of_WriterConnection_reach_no_mutation`<br>(+1 more) |
| C4R-M05-A02 | genuine violation | the same with the order reversed: a private non-generic RvH(string) that Configure calls, then an internal GENERIC RvH&lt;T&gt;(string) that runs a command without a lease | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_lease_less_members_of_WriterConnection_reach_no_mutation`<br>(+1 more) |
| C4R-M05-A03 | genuine violation | overloads that differ only by HOW MANY type parameters: a private RvH&lt;T&gt; that Configure calls, an internal RvH&lt;T, U&gt; that runs a command without a lease | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_lease_less_members_of_WriterConnection_reach_no_mutation`<br>(+1 more) |
| C4R-M05-A04 | genuine violation | a lease-less WriterConnection.Poke runs a command through a private generic helper that is called only under a lease, while an internal method reaches the primitive only through the generic overload | killed | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing` |
| C4R-M05-A05 | genuine violation | the reviewer's retained-delegate mutant: Configure stores sql =&gt; Scalar(lease, sql) in a field, the lease-less Poke runs it | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A06 | genuine violation | the same with LibraryStore.CreateEmptyDatabase(lease) retained by AcquireWriterLock and run by a lease-less PokeCreate | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A07 | genuine violation | the delegate is stored in a STATIC field | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A08 | genuine violation | a lease-taking method RETURNS the closure it built (Detach); any caller can run it after the lease ended | killed only by an unrelated text rule | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts`<br>`LibrarySecurityAuditTests.A_05_sql_is_constant_text_in_Sql_files_and_every_value_is_a_bound_parameter` |
| C4R-M05-A09 | genuine violation | a lease-LESS method returns a closure that uses the lease the writer kept (the earlier audit rejected this by its host rule) | killed | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_lease_less_members_of_WriterConnection_reach_no_mutation`<br>(+1 more) |
| C4R-M05-A10 | genuine violation | the closure is handed to another lease-less method that keeps it (Keep) | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A11 | genuine violation | a LOCAL FUNCTION that captures the lease is converted to a delegate and stored | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A12 | genuine violation | a lambda in a lease-less PRIVATE host (called only by Configure, so the frozen wording accepts it) stores a closure that uses the writer's kept lease | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A13 | genuine violation | the runner built from lease-capturing closures is kept in a field by CheckIdentity and used by a lease-less method | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A14 | genuine violation | the verifier keeps the query runner it is handed in a static (the importer's runner, built from lease-capturing closures, now outlives the import) | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_part_a_accepts_the_compliant_fixtures`<br>`A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>(+1 more) |
| C4R-M05-A15 | genuine violation | an ITERATOR that holds the lease and mutates when somebody enumerates it, possibly after the lease ended | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A16 | genuine violation | a delegate that takes a lease is invoked by a lease-less method with the lease the writer kept | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_allow_lists_are_narrow_exact_and_load_bearing`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A17 | genuine violation | the closure is subscribed to an event | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A18 | genuine violation | the closure is handed to the thread pool | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A19 | genuine violation | an ASYNC lambda that captures the lease is stored (its work is in a state machine) | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-A20 | genuine violation | the closure is put in an array | **SURVIVED** | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-E01 | control (must be accepted) | CONTROL: the closure is handed to a helper that only runs it | accepted | **EQUIVALENT** | `A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-E02 | control (must be accepted) | CONTROL: the closure is built and run inside the host | accepted | **EQUIVALENT** | `A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-E03 | control (must be accepted) | CONTROL: overloads that differ by generic arity, all private and called only under the lease | accepted | **EQUIVALENT** |  |
| C4R-M05-E04 | control (must be accepted) | CONTROL: a lambda that takes the lease itself is stored and run by a method that takes one | accepted | **EQUIVALENT** | `A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-S01 | defect in the audit itself | the audit's method key drops the generic arity again | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_overloads_that_differ_only_by_generic_arity_or_shape_are_different_methods`<br>`A_25_part_a_accepts_the_compliant_fixtures`<br>`A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>(+1 more) |
| C4R-M05-S02 | defect in the audit itself | two methods with one identity are not rejected | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_overloads_that_differ_only_by_generic_arity_or_shape_are_different_methods`<br>`A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule` |
| C4R-M05-S03 | defect in the audit itself | the closure rule is not run | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S04 | defect in the audit itself | a store to an instance field is no longer a sink | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S05 | defect in the audit itself | a store to a static field is no longer a sink | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S06 | defect in the audit itself | a store to an array element is no longer a sink | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S07 | defect in the audit itself | a returned delegate is no longer a sink | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S08 | defect in the audit itself | a call into code the audit cannot read is no longer a sink | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S09 | defect in the audit itself | a callee whose summary lets the parameter escape is ignored | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S10 | defect in the audit itself | an object built from a delegate by a constructor no longer carries it (the constructor's stores are forgotten) | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a`<br>`A_25_part_a_accepts_the_compliant_fixtures`<br>`A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>(+1 more) |
| C4R-M05-S11 | defect in the audit itself | the arguments of a delegate invocation are not judged against the targets of that delegate type | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S12 | defect in the audit itself | no method counts as an authority closure | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |
| C4R-M05-S13 | defect in the audit itself | the work of an async lambda (its state machine) is not part of what the lambda reaches | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S14 | defect in the audit itself | an iterator that reaches a mutation is accepted | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_frozen_wording_alone_accepted_what_the_closure_rule_rejects` |
| C4R-M05-S15 | defect in the audit itself | the invocation of a delegate that takes a lease is not a leased operation | not run (the earlier audit has no such rule or file) | **KILLED** | `A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule`<br>`A_25_the_real_assemblies_have_unambiguous_methods_and_closures_that_stay_in_their_hosts` |

### 5.2 The original 27 audit and lease mutants RA-01 to RA-27 (`tests/mutation/Invoke-C4RepairMutants.ps1`), on the final tree

| Mutant | Suite | Defect | Result |
|---|---|---|---|
| RA-01 | Audit | the lease-bound-type exemption is back in the A-25 audit (any instance method of a type that holds a lease has authority) | **KILLED** |
| RA-02 | Audit | the lease-producer exemption is back (a method that asks the interlock for a lease has authority) | **KILLED** |
| RA-03 | Audit | transitive private authority is back (a private helper is allowed when its callers are, a chain) | **KILLED** |
| RA-04 | Audit | File.Move is dropped from the primitive list | **KILLED** |
| RA-05 | Audit | sqlite3_step is dropped from the primitive list | **KILLED** |
| RA-06 | Audit | SqliteCommand.ExecuteNonQuery (any command on a connection) is dropped from the primitive list | **KILLED** |
| RA-07 | Audit | the audit stops following interface and virtual dispatch | **KILLED** |
| RA-08 | Audit | a private helper may be called from a method of another type that takes a lease (the "own type" clause is dropped) | **KILLED** |
| RA-09 | Audit | callees are matched by name only (overloads hide behind each other) | **KILLED** |
| RA-10 | Audit | the lock-first ordering check never fires (offsets are not compared) | **KILLED** |
| RA-11 | Audit | WriterConnection.Configure is lease-less again (the earlier shape) | **KILLED** |
| RA-12 | Audit | R-20: a lease-less instance method of WriterConnection executes a command on the writer | **KILLED** |
| RA-13 | Audit | R-19: a class that holds a lease and creates the database from an instance method without a lease parameter | **KILLED** |
| RA-14 | Audit | an implementation of IQueryRunner keeps a writer and a lease and runs statements with them (the earlier WriterQueryRunner) | **KILLED** |
| RA-15 | Audit | a default(MutationLease) outside the interlock (A-25 part c) | **KILLED** |
| RA-16 | Library | Guard accepts a default lease and asks the lease's own interlock (lease.Owner?.RequireOpenedBy) | **KILLED** |
| RA-17 | Library | RequireOpenedBy does not compare the opener's id (any current lease of the interlock of an allowed kind passes) | **KILLED** |
| RA-18 | Library | Rollback no longer checks that the lease is the opener's | **KILLED** |
| RA-19 | Library | BeginCancellationScope does not check the lease | **KILLED** |
| RA-20 | Library | WriterStatement.ExecuteScalar does not check the lease | **KILLED** |
| RA-21 | Audit | R-03: the header of library.sqlite3 is read BEFORE the writer lock in DeriveCore | **KILLED** |
| RA-22 | Audit | R-03: File.Exists(library.sqlite3) is called BEFORE the writer lock in DeriveCore | **KILLED** |
| RA-23 | Library | the header of library.sqlite3 is read BEFORE the writer lock (run-time view of R-03) | **KILLED** |
| RA-24 | Audit | a new method of LibrarySession inspects the members and never takes the lock | **KILLED** |
| RA-25 | Audit | a static readonly built SQL string in Sql/OpenSql.cs | **KILLED** |
| RA-26 | Audit | a forwarder (Execute) is fed a built string | **KILLED** |
| RA-27 | Library | a DML statement moves into a class whose name ends in Sql but is not static (the plan check would not see it) | **KILLED** |

### 5.3 The original C4 audit and lease-guard mutants (`Invoke-C4Mutants.ps1 -Set Original`: C4-A1 to C4-A4, and the lease guards C4-01, C4-05, C4-07, C4-13)

| Mutant | Suite | Defect | Result | Failing tests that are not failing without it |
|---|---|---|---|---|
| C4-01 | Library | a stale lease id still authorises a mutation (OBS-15) | **KILLED** | `LeaseRuntimeAbuseTests.A_delegate_that_captured_the_lease_is_refused_after_the_interlock_granted_a_newer_lease_of_the_same_kind_by_every_writer_operation_before_any_io`<br>`LeaseRuntimeAbuseTests.A_delegate_that_takes_a_lease_is_refused_when_a_caller_offers_a_stale_lease_of_an_earlier_grant_for_every_writer_operation_before_any_io`<br>(+2 more) |
| C4-05 | Library | a mutation lease id is reused | **KILLED** | `InterlockTests.Random_request_sequences_agree_with_the_independent_model_after_every_step`<br>`LeaseRuntimeAbuseTests.A_delegate_that_captured_the_lease_is_refused_after_the_interlock_granted_a_newer_lease_of_the_same_kind_by_every_writer_operation_before_any_io`<br>(+2 more) |
| C4-07 | Library | a lease of another session authorises a mutation | **KILLED** | `LeaseRuntimeAbuseTests.A_delegate_that_takes_a_lease_is_refused_when_a_caller_passes_the_lease_of_another_session_for_every_writer_operation_before_any_io`<br>`LeaseRuntimeAbuseTests.A_lease_of_another_LibrarySession_with_a_different_id_is_refused_by_every_writer_operation_before_any_io`<br>(+2 more) |
| C4-13 | Library | COMMIT is not guarded by the lease (OBS-15 before COMMIT) | **KILLED** | `LeaseRuntimeAbuseTests.A_current_lease_of_the_same_interlock_that_did_not_open_the_writer_is_refused_by_every_writer_operation_before_any_io`<br>`LeaseRuntimeAbuseTests.A_current_lease_of_the_wrong_kind_is_refused_by_every_writer_operation_before_any_io`<br>(+2 more) |
| C4-A1 | Audit | a seventh first-party kernel32 P/Invoke in the Library (A-09, A-25) | **KILLED** | `SecurityAuditTests.The_only_native_calls_are_six_read_only_kernel32_functions` |
| C4-A2 | Audit | a File.Move outside the set-aside (A-25, the only rename) | **KILLED** | `SecurityAuditTests.Deletes_and_renames_happen_only_in_ReportRun_and_the_set_aside_and_nothing_deletes_folders` |
| C4-A3 | Audit | the Library stops hiding SQLite types from its consumers (PrivateAssets removed) | **KILLED** | `LibrarySecurityAuditTests.A_11_package_references_are_exactly_the_approved_set_pinned_by_the_lock_files` |
| C4-A4 | Audit | an ATTACH statement appears in the Library SQL (A-06) | **KILLED** | `LibrarySecurityAuditTests.A_05_sql_is_constant_text_in_Sql_files_and_every_value_is_a_bound_parameter` |


## 6. Suites (Release, the final tree, `C:\sw\m05` with links to the main checkout's `packages` and `tools`)

| Suite | Result |
|---|---|
| `tests/StorageInventory.IntegrationTests`, whole project | **200 PASS, 0 FAIL, 4 SKIP** (baseline 196 pass / 4 skip; +4: the positive test of method identity and closures, the engine self-test, the identity fixtures test, the closure load-bearing test). The 4 skips are the same four as before: the opt-in large parity test (`SI_LARGE_PARITY`), two symbolic-link tests (no Developer Mode) and the 8.3 short-name test |
| `tests/StorageInventory.Library.Tests`, whole project | **206 PASS, 0 FAIL, 0 SKIP** (baseline 199; +7: the new delegate cases of `LeaseRuntimeAbuseTests`) |
| Audit filter (`LeaseAuditTests`, `SecurityAuditTests`, `LibrarySecurityAuditTests`) | 55 PASS (was 51 at `7b41022`) |

## 7. Not closed, surprising, and what a re-review should check

* **Closed:** both genuine violations of section 10.3 fail the audit for their own rule, with fixtures and mutants for both, and the two production
  mutants of section 10.2 are killed; the audit is green on production with `Options.Frozen`; the run-time checks are unchanged and tested through a
  captured delegate.
* **Not closed, stated rather than hidden (section 3, "Stated limits"):** the analysis is an escape analysis by value flow, not a proof of the whole
  program: an authority closure that is only *invoked* is judged by the frozen wording; the BCL is never a safe sink, so some harmless uses are
  rejected (use a local variable, a first-party helper that only runs it); the arguments of a delegate invocation are judged coarsely against every
  method bound to that delegate type; iterators are rejected rather than followed; the set of four authority closures is pinned. C4R-O06's other edges
  (producers, part (c) being name-based, `LibraryStore`'s own methods) are unchanged and out of scope.
* **Surprising:** (1) the earlier audit also confused every multi-dimensional array parameter (all written `[,]`) and every function-pointer parameter
  (a constant `fnptr`), a sibling of the arity alias, fixed by the same exact decoding. (2) An init-only setter's return type carries
  `modreq(IsExternalInit)`, which broke a naive "returns a value" test (the engine's self-test over every product body found it). (3) With the
  allow-list entry for `ReaderConnection.Rows` removed (the allow-list test does this), `ReadDictionaryFootprintAsync`'s lambda becomes an authority
  closure that is stored in an async state machine: the read-only allow-list is load-bearing for the closure rule too. (4) The first version of the
  analysis was not field-sensitive and rejected the production runner when a fixture type implementing `IQueryRunner` existed in the same model; the
  paths of fields fixed that, and a recursion over `Held` once exhausted memory until the path depth was bounded (three, then "anywhere").
* **Production code:** unchanged. Existing tests changed: `CompliantLambdaCapturingAHostLease` (it returned its closure, which is now the violation),
  `C4_M17_every_member_access_follows_the_writer_lock` (arity in keys), `A_25_the_allow_lists_are_narrow_exact_and_load_bearing` (see section 4).
* **Merge note:** `tests/mutation/Invoke-C4Mutants.ps1` is edited here (the `-ToolRoot` parameter, `Intended`, `Equivalent`, `Tolerated`, and the
  `Invoke-Native` helper moved above the tool-root logic); the main branch also edits that file.

