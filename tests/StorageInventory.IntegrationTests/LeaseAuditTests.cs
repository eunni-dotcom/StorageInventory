using System.Reflection;
using System.Text.RegularExpressions;
using StorageInventory.Core;
using StorageInventory.IntegrationTests.LeaseAuditFixtures;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// A-25 parts (a) and (c) over every first-party assembly (Core, History, Library, App), and the C4 repair audits that go with it:
/// C4-M13 (constant SQL) and C4-M17 (lock first). Every rule has negative self-tests (A-21): fixtures in
/// <c>LeaseAuditFixtures.cs</c> and IL written by <see cref="SyntheticIl"/>, each of which must be rejected by the rule it names,
/// and compliant fixtures that must be accepted. The audit engine is <see cref="IlAudit"/>.
/// </summary>
public static class LeaseAuditTests
{
    private const string Ns = "StorageInventory.IntegrationTests.LeaseAuditFixtures.";
    private const string Store = "StorageInventory.Library.LibraryStore";

    private static string[] FirstPartyAssemblies() =>
    [
        typeof(StorageScanResult).Assembly.Location,
        typeof(StorageInventory.History.Identity.IdentityMatching).Assembly.Location,
        typeof(StorageInventory.Library.LibraryNames).Assembly.Location,
#if !AUDIT_WITHOUT_APP
        typeof(StorageInventory.App.App).Assembly.Location,
#endif
    ];

    private static readonly Lazy<IlAudit.Model> FirstParty = new(() => IlAudit.Read(FirstPartyAssemblies()));

    private static readonly Lazy<IlAudit.Model> Fixtures = new(() =>
    {
        var synthetic = SyntheticIl.Build(Path.GetTempPath());
        return IlAudit.Read(typeof(StorageInventory.Library.LibraryNames).Assembly.Location, typeof(LeaseAuditTests).Assembly.Location, synthetic);
    });

    private static bool InFixtures(string type) => type.StartsWith(Ns, StringComparison.Ordinal) || type.StartsWith(SyntheticIl.Namespace, StringComparison.Ordinal);

    private static bool InLibrary(string type) => type.StartsWith("StorageInventory.Library.", StringComparison.Ordinal);

    /// <summary>The v1 report output, which A-01 and A-04 confine to <c>ReportRun</c> and its readers and writers: file-system primitives of
    /// the Core assembly that are not Library operations (the Library is not reachable from Core). Pinned exactly: a new method of Core,
    /// History or the App that creates, renames, deletes or opens a file is an A-25 violation until it is added here, in review.</summary>
    internal static readonly string[] V1ReportOutput =
    [
        "StorageInventory.Core.Reports.ReportCsvReader::.ctor(String)",
        "StorageInventory.Core.Reports.ReportRun::EnsureOutputFolder()",
        "StorageInventory.Core.Reports.ReportRun::CreateNew(String)",
        "StorageInventory.Core.Reports.ReportRun::DeleteOwnTemporaryFile(String)",
        "StorageInventory.Core.Reports.ReportRun::PublishOwnWorkbook()",
        "StorageInventory.Core.Reports.ReportWriters::WriteSortedFiles(StorageInventory.Core.Reports.ReportRun,System.Collections.Generic.List`1<StorageInventory.Core.Reports.SortEntry>,Int32,System.Action`2<Int64,Int64>,System.Threading.CancellationToken)",
    ];

    private static List<IlAudit.Violation> Real(IlAudit.Options? options = null, IEnumerable<string>? allow = null)
    {
        var model = FirstParty.Value;
        return IlAudit.MutationViolations(model, _ => true, IlAudit.LeasedOperations(model), options, allow ?? IlAudit.ReadOnlyAllowList.Concat(V1ReportOutput));
    }

    // ================================================================ A-25 (a), the real assemblies

    [Test]
    public static void A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a()
    {
        var model = FirstParty.Value;
        var leased = IlAudit.LeasedOperations(model);
        var assemblies = model.Units.Select(u => u.Assembly).Distinct().Order(StringComparer.Ordinal).ToList();
        Console.WriteLine($"A-25: {model.Methods.Count} methods in {string.Join(", ", assemblies)}; {leased.Count} leased operations");
#if !AUDIT_WITHOUT_APP
        Assert.SequenceEqual(["StorageInventory", "StorageInventory.Core", "StorageInventory.History", "StorageInventory.Library"], assemblies, "the audit reads Core, History, Library and the App");
#endif
        Assert.True(assemblies.Contains("StorageInventory.Core") && assemblies.Contains("StorageInventory.History") && assemblies.Contains("StorageInventory.Library"), "Core, History and Library were read");
        Assert.True(leased.Count >= 30, $"{leased.Count} leased operations were found: the analysis sees the Library's lease-taking API");
        foreach (var expected in new[]
        {
            "StorageInventory.Library.LibraryStore::CreateEmptyDatabase(", "StorageInventory.Library.LibraryStore::QuarantineSet(", "StorageInventory.Library.LibraryStore::AcquireWriterLock(",
            "StorageInventory.Library.LibraryStore::EnsureLibraryFolder(", "StorageInventory.Library.LibraryDatabase::OpenWriter(", "StorageInventory.Library.WriterConnection::Begin(",
            "StorageInventory.Library.WriterConnection::Commit(", "StorageInventory.Library.WriterConnection::Rollback(", "StorageInventory.Library.WriterConnection::Configure(",
            "StorageInventory.Library.WriterConnection::AssertEngine(", "StorageInventory.Library.WriterConnection::Scalar(", "StorageInventory.Library.WriterConnection::Rows(",
            "StorageInventory.Library.WriterConnection::Prepare(", "StorageInventory.Library.WriterConnection::PageCount(", "StorageInventory.Library.WriterConnection::PageSize(",
            "StorageInventory.Library.WriterConnection::BeginCancellationScope(", "StorageInventory.Library.WriterStatement::ExecuteNonQuery(", "StorageInventory.Library.WriterStatement::ExecuteRows(",
            "StorageInventory.Library.LibrarySession::DeleteSnapshotAsync(", "StorageInventory.Library.LibrarySession::ImportSnapshotAsync(", "StorageInventory.Library.SnapshotImporter::Run(",
            "StorageInventory.Library.SnapshotImporter+NameCache::Intern(",
        })
        {
            Assert.True(leased.Any(k => k.StartsWith(expected, StringComparison.Ordinal)), "the analysis does not see " + expected);
        }

        var violations = Real();
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));

        // the frozen wording is enough: the production code needs none of the relaxations of the earlier audit (C4-H01)
        Assert.Equal(0, Real(IlAudit.Options.Relaxed).Count, "(sanity) the relaxed audit passes too");
    }

    [Test]
    public static void A_25_the_allow_lists_are_narrow_exact_and_load_bearing()
    {
        var model = FirstParty.Value;
        var everything = IlAudit.ReadOnlyAllowList.Concat(V1ReportOutput).ToList();
        // every entry names a method that exists, and is needed: without it the audit rejects exactly that method
        foreach (var entry in everything)
        {
            Assert.True(model.Methods.ContainsKey(entry), "the allow-list names a method that does not exist: " + entry);
            var found = Real(allow: everything.Where(e => e != entry));
            Assert.True(found.Any(v => v.Method == entry), "the allow-list entry is not needed: " + entry);
            // a method that dispatches to the entry (an IQueryRunner consumer, with ReaderConnection's Scalar and Rows) is rejected with it, and only that
            Assert.True(found.All(v => v.Method == entry || v.Detail.Contains("(dispatch)", StringComparison.Ordinal) && v.Detail.Contains(entry, StringComparison.Ordinal)),
                "removing one entry rejects more than that method and its dispatchers: " + entry + " -> " + string.Join(" | ", found));
        }
        // nothing that runs a command on the WRITER is allow-listed: the writer's members all take the lease (A-25 d)
        Assert.False(IlAudit.ReadOnlyAllowList.Any(e => e.Contains("WriterConnection", StringComparison.Ordinal) || e.Contains("WriterStatement", StringComparison.Ordinal)), "no writer member is allow-listed");
        Assert.True(IlAudit.ReadOnlyAllowList.All(e => !e.Contains('*', StringComparison.Ordinal)), "no wildcard");
        Assert.Equal(8, IlAudit.ReadOnlyAllowList.Length, "the read-only allow-list is exactly the eight reviewed methods");
        Assert.Equal(6, V1ReportOutput.Length, "the v1 report output is exactly the six reviewed methods");
    }

    [Test]
    public static void A_25_the_primitives_have_exactly_the_callers_the_design_says()
    {
        var model = FirstParty.Value;
        bool IsFile(IlAudit.CallSite c) => IlAudit.IsPrimitive(c) && c.Type.StartsWith("System.IO.", StringComparison.Ordinal);
        bool IsEngine(IlAudit.CallSite c) => IlAudit.IsPrimitive(c) && !IsFile(c);

        // the Library's file-system primitives: create-new, directory creation, the lock open, File.Move, and the two read-only opens
        string[] fileCallers = [.. model.Units.Where(u => InLibrary(u.Type) && u.Calls.Any(IsFile)).Select(u => $"{u.Type}::{u.Name}").Order(StringComparer.Ordinal)];
        Assert.SequenceEqual(
            ["StorageInventory.Library.LibraryStore::AcquireWriterLock", "StorageInventory.Library.LibraryStore::CreateEmptyDatabase", "StorageInventory.Library.LibraryStore::EnsureLibraryFolder",
             "StorageInventory.Library.LibraryStore::HandleLength", "StorageInventory.Library.LibraryStore::QuarantineSet", "StorageInventory.Library.LibraryStore::ReadHeader"],
            fileCallers, "the only Library methods that create, rename or open files");
        Assert.False(model.Units.Where(u => InLibrary(u.Type)).Any(u => u.Calls.Any(c => c.Short is "System.IO.File::Delete" or "System.IO.Directory::Delete")), "nothing in the Library deletes a file or a folder (A-04)");

        // outside the Library, the same primitives belong to the v1 report output only
        string[] outside = [.. model.Units.Where(u => !InLibrary(u.Type) && u.Calls.Any(IsFile)).Select(u => u.Key).Order(StringComparer.Ordinal)];
        Assert.SequenceEqual([.. V1ReportOutput.Order(StringComparer.Ordinal)], outside, "the file-system primitives of Core, History and the App are the v1 report output and nothing else");

        // the engine's primitives (a statement, a transaction, a connection) belong to the adapter types of LibraryDatabase.cs
        string[] engineTypes = [.. model.Units.Where(u => u.Calls.Any(IsEngine)).Select(u => u.Type).Distinct().Order(StringComparer.Ordinal)];
        Assert.SequenceEqual(["StorageInventory.Library.LibraryDatabase", "StorageInventory.Library.ReaderConnection", "StorageInventory.Library.WriterConnection", "StorageInventory.Library.WriterStatement"], engineTypes,
            "only the adapter types call the engine's primitives");
        // every entry point of the writer's types that reaches the engine takes a lease (not vacuous: the writer has many)
        var writerEntries = model.Units.Where(u => u.Type is "StorageInventory.Library.WriterConnection" or "StorageInventory.Library.WriterStatement" && u.Calls.Any(IsEngine) && !u.IsPrivate).ToList();
        Assert.True(writerEntries.Count >= 10, "the analysis sees the writer's entry points: " + writerEntries.Count);
        Assert.True(writerEntries.All(u => u.TakesLease), "every non-private writer member that runs a command takes a lease: " + string.Join(", ", writerEntries.Where(u => !u.TakesLease).Select(u => u.Key)));
        // and there is no indirect call anywhere in first-party code
        Assert.Equal(0, model.Units.Count(u => u.Calls.Any(c => c.Kind == IlAudit.CallKind.Calli)), "no first-party method makes an indirect call");

        // the other first-party assemblies cannot see the lease types at all
        foreach (var assembly in new[] { typeof(StorageScanResult).Assembly, typeof(StorageInventory.History.Identity.IdentityMatching).Assembly })
        {
            Assert.False(assembly.GetReferencedAssemblies().Any(a => a.Name == "StorageInventory.Library"), assembly.GetName().Name + " does not reference the Library");
        }
    }

    [Test]
    public static void A_25_the_lease_less_members_of_WriterConnection_reach_no_mutation()
    {
        // WriterConnection holds the lease it was opened under, so the earlier audit exempted every instance member of it (R-20: a
        // lease-less member that runs a command passed). Under the frozen wording each lease-less non-private member is judged.
        var model = FirstParty.Value;
        var leased = IlAudit.LeasedOperations(model);
        Assert.True(model.LeaseBoundTypes.ContainsKey("StorageInventory.Library.WriterConnection"), "WriterConnection is a lease-bound type: the old exemption applied to it");
        var leaseless = model.Units.Where(u => u.Type == "StorageInventory.Library.WriterConnection" && !u.TakesLease && !u.Entry.IsStatic && !u.IsPrivate).ToList();
        Console.WriteLine("lease-less non-private instance members of WriterConnection: " + string.Join(", ", leaseless.Select(u => u.Name).Order(StringComparer.Ordinal)));
        Assert.True(leaseless.Count >= 4, "the analysis sees them");
        foreach (var unit in leaseless)
        {
            Assert.False(unit.Calls.Any(c => IlAudit.IsPrimitive(c) || leased.Contains(c.Key)), $"the lease-less member {unit.Name} of WriterConnection reaches a mutation");
        }
    }

    [Test]
    public static void A_25_the_primitive_list_names_every_operation_of_the_review()
    {
        IlAudit.CallSite Call(string type, string name, params string[] parameters) => new(type, name, parameters, IlAudit.CallKind.Call, 0, null);
        var must = new List<IlAudit.CallSite>();
        foreach (var n in new[] { "Open", "OpenWrite", "OpenHandle", "Create", "CreateText", "Delete", "Move", "Copy", "Replace", "WriteAllBytes", "WriteAllText", "WriteAllLines", "WriteAllBytesAsync", "WriteAllTextAsync",
                     "AppendAllText", "AppendAllLines", "AppendAllBytes", "AppendText", "SetAttributes", "SetCreationTime", "SetLastWriteTimeUtc", "SetUnixFileMode" }) must.Add(Call("System.IO.File", n));
        foreach (var n in new[] { "CreateDirectory", "Delete", "Move" }) must.Add(Call("System.IO.Directory", n));
        foreach (var t in new[] { "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileSystemInfo" })
        {
            foreach (var n in new[] { "Delete", "set_Attributes", "set_LastWriteTime" }) must.Add(Call(t, n));
        }
        foreach (var n in new[] { "Create", "MoveTo", "CopyTo", "Replace", "Open", "OpenWrite" }) must.Add(Call("System.IO.FileInfo", n));
        foreach (var n in new[] { "Create", "CreateSubdirectory", "MoveTo" }) must.Add(Call("System.IO.DirectoryInfo", n));
        must.Add(Call("System.IO.FileStream", ".ctor", "String", "System.IO.FileMode"));
        must.Add(Call("System.IO.StreamWriter", ".ctor", "String"));
        foreach (var n in new[] { "ExecuteNonQuery", "ExecuteReader", "ExecuteScalar", "ExecuteNonQueryAsync", "ExecuteReaderAsync", "ExecuteScalarAsync" })
        {
            must.Add(Call("Microsoft.Data.Sqlite.SqliteCommand", n));
            must.Add(Call("System.Data.Common.DbCommand", n));
        }
        foreach (var n in new[] { "Open", "OpenAsync", "BeginTransaction" }) must.Add(Call("Microsoft.Data.Sqlite.SqliteConnection", n));
        foreach (var n in new[] { "Commit", "Rollback" }) must.Add(Call("Microsoft.Data.Sqlite.SqliteTransaction", n));
        foreach (var n in new[] { "sqlite3_step", "sqlite3_prepare_v2", "sqlite3_prepare_v3", "sqlite3_exec", "sqlite3_open", "sqlite3_open_v2", "sqlite3_backup_init", "sqlite3_backup_step", "sqlite3_backup_finish" }) must.Add(Call("SQLitePCL.raw", n));
        foreach (var call in must) Assert.True(IlAudit.IsPrimitive(call), $"the primitive list misses {call.Short}");

        // not primitives: reads, pure string functions, a stream-taking writer
        foreach (var call in new[] { Call("System.IO.File", "Exists"), Call("System.IO.File", "OpenRead"), Call("System.IO.File", "ReadAllText"), Call("System.IO.Directory", "Exists"), Call("System.IO.Path", "Combine"),
                     Call("System.IO.StreamWriter", ".ctor", "System.IO.Stream"), Call("Microsoft.Data.Sqlite.SqliteConnection", "CreateCommand"), Call("SQLitePCL.raw", "sqlite3_bind_int64"), Call("SQLitePCL.raw", "sqlite3_reset") })
        {
            Assert.False(IlAudit.IsPrimitive(call), $"{call.Short} is not a mutation primitive");
        }
    }

    // ================================================================ A-25 (a), the negative self-tests

    private sealed record Expect(string Method, string Rule, string Detail = "");

    private static readonly Expect[] MustReject =
    [
        new("LeaselessPublicWrapper::Make(", IlAudit.RuleNotPrivate, "Directory::CreateDirectory"),
        new("LeaselessInternalWrapper::Create(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("PrivateHelperFromLeaselessMethod::Make(", IlAudit.RuleCallerWithoutLease, "PrivateHelperFromLeaselessMethod::Public("),
        new("PrivateHelperFromANestedType::Make(", IlAudit.RuleForeignCaller, "PrivateHelperFromANestedType+Inner"),
        new("PrivateHelperNobodyCalls::Orphan(", IlAudit.RuleUncalled),
        new("PrivateHelperTwoHops::Bottom(", IlAudit.RuleCallerWithoutLease, "PrivateHelperTwoHops::Middle("),
        new("DispatchImplementation::Run(", IlAudit.RuleNotPrivate),
        new("DispatchExplicitImplementation::StorageInventory.IntegrationTests.LeaseAuditFixtures.IFixtureOp.Run(", IlAudit.RuleNotPrivate),
        new("DispatchThroughInterface::Use(", IlAudit.RuleNotPrivate, "IFixtureOp::Run -> "),
        new("FixtureDerived::Do(", IlAudit.RuleNotPrivate),
        new("DispatchThroughBaseClass::Use(", IlAudit.RuleNotPrivate, "FixtureBase::Do -> "),
        new("DispatchThroughBaseClass::Delegate(", IlAudit.RuleNotPrivate, "FixtureDerived::Do(String) (dispatch)"),
        new("DispatchThroughGenericHelper::Run", IlAudit.RuleNotPrivate, "IFixtureOp::Run -> "),
        new("LeaseRetainingQueryRunner::Scalar(", IlAudit.RuleNotPrivate, "WriterConnection::Scalar (takes a lease)"),
        new("LeaseRetainingQueryRunner::Rows(", IlAudit.RuleNotPrivate, "WriterConnection::Rows (takes a lease)"),
        new("UsesQueryRunner::Count(", IlAudit.RuleNotPrivate, "IQueryRunner::Scalar -> "),
        new("LambdaCapturingALeaseInALeaselessHost::Make(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("LambdaWithoutALease::Make(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("MethodGroupOfALeasedOperation::Bind(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("RetainedLease::Create(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("RetainedLeasePrimitive::Poke(", IlAudit.RuleNotPrivate, "Directory::CreateDirectory"),
        new("RetainedWriter::Peek(", IlAudit.RuleNotPrivate, "WriterConnection::Scalar"),
        new("UsedAfterItsLease::Late(", IlAudit.RuleNotPrivate, "WriterConnection::Commit"),
        new("ProducerWithoutAParameter::Run(", IlAudit.RuleNotPrivate, "CreateEmptyDatabase"),
        new("WriterWithoutALease::Poke(", IlAudit.RuleNotPrivate, "WriterConnection::Begin"),
        new("OverloadHiding::Make(String)", IlAudit.RuleNotPrivate, "Directory::CreateDirectory"),
        new("AsyncWithoutALease::Delete(", IlAudit.RuleNotPrivate, "DeleteSnapshotAsync"),
        new("DirectFileSystemMutation::Make(", IlAudit.RuleNotPrivate, "Directory::CreateDirectory"),
        new("DirectFileSystemMutation::Rename(", IlAudit.RuleNotPrivate, "File::Move"),
        new("DirectFileSystemMutation::Open(", IlAudit.RuleNotPrivate, "FileStream::.ctor"),
        new("DirectFileSystemMutation::Write(", IlAudit.RuleNotPrivate, "File::WriteAllText"),
        new("DirectFileSystemMutation::Info(", IlAudit.RuleNotPrivate, "FileSystemInfo::Delete"),
        new("Cases::CalliNoLease(", IlAudit.RuleIndirectCall, "calli"),
        new("Cases::CommandNoLease(", IlAudit.RuleNotPrivate, "SqliteCommand::ExecuteNonQuery"),
        new("Cases::CommitNoLease(", IlAudit.RuleNotPrivate, "SqliteTransaction::Commit"),
        new("Cases::BeginTransactionNoLease(", IlAudit.RuleNotPrivate, "SqliteConnection::BeginTransaction"),
        new("Cases::StepNoLease(", IlAudit.RuleNotPrivate, "raw::sqlite3_step"),
        new("LeaseBoundCommandRunner::Run(", IlAudit.RuleNotPrivate, "SqliteCommand::ExecuteNonQuery"),
    ];

    private static string Qualified(string method) =>
        (method.StartsWith("Cases::", StringComparison.Ordinal) || method.StartsWith("LeaseBoundCommandRunner::", StringComparison.Ordinal) ? SyntheticIl.Namespace : Ns) + method;

    private static bool Matches(IlAudit.Violation v, Expect e) => v.Method.Contains(Qualified(e.Method), StringComparison.Ordinal);

    [Test]
    public static void A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule()
    {
        var model = Fixtures.Value;
        var violations = IlAudit.MutationViolations(model, InFixtures, IlAudit.LeasedOperations(model));
        var all = string.Join("\n", violations);
        foreach (var expected in MustReject)
        {
            var hits = violations.Where(v => Matches(v, expected)).ToList();
            Assert.True(hits.Count >= 1, $"A-25 (a) misses {expected.Method}:\n{all}");
            Assert.True(hits.Any(v => v.Rule == expected.Rule), $"{expected.Method} is rejected, but not by {expected.Rule}: {string.Join(" | ", hits)}");
            Assert.True(hits.Any(v => v.Detail.Contains(expected.Detail, StringComparison.Ordinal)), $"{expected.Method}: the reason does not mention '{expected.Detail}': {string.Join(" | ", hits)}");
        }
        // and nothing else is rejected: the compliant fixtures (and the other tests' fixtures, which take their leases) are accepted
        var unexpected = violations.Where(v => !MustReject.Any(e => Matches(v, e))).ToList();
        Assert.Equal(0, unexpected.Count, "rejected, but expected to be accepted:\n" + string.Join("\n", unexpected));
    }

    [Test]
    public static void A_25_part_a_accepts_the_compliant_fixtures()
    {
        var model = Fixtures.Value;
        var violations = IlAudit.MutationViolations(model, InFixtures, IlAudit.LeasedOperations(model));
        string[] accepted =
        [
            "CompliantLeaseParameter::Create(", "CompliantPrivateHelper::Public(", "CompliantPrivateHelper::Make(", "CompliantLambdaWithItsOwnLease::Build(", "CompliantLambdaWithItsOwnLease::BuildAsync(",
            "CompliantLambdaCapturingAHostLease::Build(", "CompliantLocalFunction::Run(", "CompliantAsync::Delete(", "CompliantLeasedImplementation::Run(", "CompliantLeasedDispatch::Use(",
            "OverloadHiding::Make(String,StorageInventory.Library.MutationLease)", "OverloadedCallee::Do(", "CompliantHarmlessOverloadCaller::Harmless(", "CompliantReadOnly::Header(", "InspectsAfterTheLock::Derive(",
            "Cases::CalliWithLease(", "Cases::CommandWithLease(",
        ];
        foreach (var method in accepted)
        {
            var full = Qualified(method);
            Assert.True(model.Methods.Values.Any(u => u.Key.StartsWith(full, StringComparison.Ordinal)), "the fixture exists: " + method);
            Assert.False(violations.Any(v => v.Method.StartsWith(full, StringComparison.Ordinal)), $"A-25 (a) wrongly rejects {method}:\n{string.Join("\n", violations)}");
        }
        // a lambda that takes a lease of its own is a unit of its own, accepted for its own calls; its host reaches nothing
        Assert.True(model.Methods.Values.Any(u => u.Entry.IsGenerated && u.TakesLease && u.Type == Ns + "CompliantLambdaWithItsOwnLease" && u.Calls.Any(c => c.Short.EndsWith("::CreateEmptyDatabase", StringComparison.Ordinal))),
            "the analysis sees a lambda unit that takes its own lease and creates the database");
    }

    [Test]
    public static void A_25_the_earlier_relaxed_audit_accepted_what_the_frozen_wording_rejects_R_19_and_R_20()
    {
        var model = Fixtures.Value;
        var leased = IlAudit.LeasedOperations(model);
        var frozen = IlAudit.MutationViolations(model, InFixtures, leased);
        var relaxed = IlAudit.MutationViolations(model, InFixtures, leased, IlAudit.Options.Relaxed);
        bool Has(List<IlAudit.Violation> list, string method) => list.Any(v => Matches(v, new Expect(method, "")));

        // R-20: a lease-less instance method of a lease-bound type runs a command / primitive / leased statement: the earlier audit passed it
        string[] leaseBound =
        [
            "RetainedLease::Create(", "RetainedLeasePrimitive::Poke(", "RetainedWriter::Peek(", "UsedAfterItsLease::Late(", "LeaseRetainingQueryRunner::Scalar(", "LeaseRetainingQueryRunner::Rows(", "LeaseBoundCommandRunner::Run(",
        ];
        foreach (var method in leaseBound)
        {
            Assert.True(Has(frozen, method), "the frozen rule rejects " + method);
            Assert.False(Has(relaxed, method), "the earlier, relaxed audit accepted " + method + " (R-19/R-20)");
        }
        // the producer exception: a method that merely asks the interlock for a lease
        Assert.True(Has(frozen, "ProducerWithoutAParameter::Run(") && !Has(relaxed, "ProducerWithoutAParameter::Run("), "the producer exemption");
        // transitive private authority
        Assert.True(Has(frozen, "PrivateHelperTwoHops::Bottom(") && !Has(relaxed, "PrivateHelperTwoHops::Bottom("), "the transitive exemption");

        // each relaxation is separately responsible for its own cases and no others
        var onlyBound = IlAudit.MutationViolations(model, InFixtures, leased, new IlAudit.Options { LeaseBoundTypeExemption = true });
        Assert.False(Has(onlyBound, "RetainedLease::Create(") || Has(onlyBound, "LeaseBoundCommandRunner::Run("), "the lease-bound exemption alone accepts the lease-bound types");
        Assert.True(Has(onlyBound, "ProducerWithoutAParameter::Run(") && Has(onlyBound, "PrivateHelperTwoHops::Bottom("), "...and nothing else");
        var onlyProducer = IlAudit.MutationViolations(model, InFixtures, leased, new IlAudit.Options { LeaseProducerExemption = true });
        Assert.False(Has(onlyProducer, "ProducerWithoutAParameter::Run("), "the producer exemption alone accepts the producer");
        Assert.True(Has(onlyProducer, "RetainedLease::Create(") && Has(onlyProducer, "PrivateHelperTwoHops::Bottom("), "...and nothing else");
        var onlyTransitive = IlAudit.MutationViolations(model, InFixtures, leased, new IlAudit.Options { TransitivePrivateAuthority = true });
        Assert.False(Has(onlyTransitive, "PrivateHelperTwoHops::Bottom("), "the transitive exemption alone accepts the two-hop helper");
        Assert.True(Has(onlyTransitive, "RetainedLease::Create(") && Has(onlyTransitive, "ProducerWithoutAParameter::Run("), "...and nothing else");
        // and even the relaxed audit rejects a plain lease-less wrapper
        Assert.True(Has(relaxed, "LeaselessPublicWrapper::Make(") && Has(relaxed, "DirectFileSystemMutation::Rename(") && Has(relaxed, "OverloadHiding::Make(String)"), "even the relaxed audit rejects a plain lease-less wrapper");
    }

    // ================================================================ A-25 (c)

    [Test]
    public static void A_25_part_c_leases_are_constructed_only_by_the_interlock_and_no_public_member_exposes_one()
    {
        var model = FirstParty.Value;
        var violations = IlAudit.ConstructionViolations(model, _ => true);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));
        Assert.True(model.Units.Where(u => u.Type == IlAudit.InterlockType).SelectMany(u => u.Calls).Any(c => c.Short == IlAudit.MutationLeaseType + "::.ctor"), "the interlock does construct leases (the check is not vacuous)");
        Assert.True(model.Physicals.Any(p => p.Assembly == "StorageInventory.Library" && p.InitTypes.Any(t => t.Type == IlAudit.MutationLeaseType)), "the interlock's default values are seen (initobj)");

        // no public or internal constructor outside the interlock type: the lease constructors are internal and called only from the interlock
        foreach (var type in new[] { typeof(StorageInventory.Library.MutationLease), typeof(StorageInventory.Library.ObservationLease) })
        {
            Assert.False(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Any(), type.Name + " has no public constructor");
            Assert.True(type.IsValueType && !type.IsVisible, type.Name + " is an internal value type");
        }

        // no public member of any first-party assembly takes or returns a lease
        foreach (var assembly in new[]
        {
            typeof(StorageScanResult).Assembly, typeof(StorageInventory.History.Identity.IdentityMatching).Assembly, typeof(StorageInventory.Library.LibraryNames).Assembly,
#if !AUDIT_WITHOUT_APP
            typeof(StorageInventory.App.App).Assembly,
#endif
        })
        {
            var exposed = IlAudit.PublicLeaseExposure(assembly);
            Assert.Equal(0, exposed.Count, assembly.GetName().Name + ": " + string.Join("; ", exposed));
        }
        Assert.Equal(0, typeof(StorageInventory.Library.LibraryNames).Assembly.GetExportedTypes().Length, "the Library exposes nothing publicly in C4 (its surface arrives with the capture wiring)");
    }

    [Test]
    public static void A_25_part_c_rejects_forged_leases_and_public_exposure()
    {
        var model = Fixtures.Value;
        var violations = IlAudit.ConstructionViolations(model, InFixtures);
        bool Find(string method, string rule) => violations.Any(v => v.Method.Contains(method, StringComparison.Ordinal) && v.Rule == rule);
        Assert.True(Find("ForgesALease::Forge(", IlAudit.RuleConstructsLease), "a constructor call: " + string.Join("; ", violations));
        Assert.True(Find("ForgesALease::Zero(", IlAudit.RuleDefaultLease), "default(MutationLease) (initobj outside the interlock): " + string.Join("; ", violations));
        Assert.True(Find("ForgesALease::ForgeObservation(", IlAudit.RuleConstructsLease), "an observation lease constructor call");
        Assert.True(Find("LeaselessInternalWrapper::Create(", IlAudit.RuleDefaultLease), "default(MutationLease) passed as an argument");
        Assert.True(Find("LambdaWithoutALease+", IlAudit.RuleDefaultLease), "default(MutationLease) inside a lambda");
        Assert.False(violations.Any(v => v.Method.Contains("::Compliant", StringComparison.Ordinal) || v.Method.Contains(".Compliant", StringComparison.Ordinal) || v.Method.Contains("InspectsAfterTheLock", StringComparison.Ordinal)), "compliant fixtures are not reported");

        var exposed = IlAudit.PublicLeaseExposure(typeof(LeaseAuditTests).Assembly, t => t == typeof(LeaseSurrogate));
        Assert.True(exposed.Any(e => e.Contains("ExposesALease.Get")), "a public method returning a lease: " + string.Join("; ", exposed));
        Assert.True(exposed.Any(e => e.Contains("ExposesALease.Take")), "a public method taking a lease: " + string.Join("; ", exposed));
    }

    // ================================================================ C4-M17 (1): the file system is LibraryStore's alone

    [Test]
    public static void C4_M17_only_LibraryStore_touches_the_file_system()
    {
        var model = FirstParty.Value;
        var violations = IlAudit.FileSystemViolations(model, InLibrary, Store);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));
        // not vacuous: LibraryStore does use them, and the analysis sees the whole Library
        var storeCalls = model.Physicals.Where(p => p.OwnerType == Store).SelectMany(p => p.Calls).Where(IlAudit.IsFileSystemApi).Select(c => c.Short).Distinct().Order(StringComparer.Ordinal).ToList();
        Console.WriteLine("LibraryStore's file-system APIs: " + string.Join(", ", storeCalls));
        foreach (var api in new[] { "System.IO.Directory::CreateDirectory", "System.IO.Directory::Exists", "System.IO.File::Exists", "System.IO.File::Move", "System.IO.FileStream::.ctor", "System.IO.FileSystemInfo::get_Exists" })
        {
            Assert.True(storeCalls.Contains(api), "LibraryStore calls " + api);
        }
        Assert.True(model.Physicals.Count(p => InLibrary(p.Type) && p.OwnerType != Store) > 500, "the rest of the Library was judged");

        // the member paths are private: no code outside the class can even name a Library member (C4-M17)
        foreach (var name in new[] { "LockPath", "MainPath", "JournalPath", "WalPath", "ShmPath" })
        {
            var property = typeof(StorageInventory.Library.LibraryNames).Assembly.GetType(Store)!.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            Assert.True(property.GetMethod!.IsPrivate, $"LibraryStore.{name} is private");
        }
        // LibraryStore.Directory stays reachable for status text only: its callers are LibrarySession's status builders, which call no file-system API
        var callers = model.Units.Where(u => InLibrary(u.Type) && u.Type != Store && u.Calls.Any(c => c.Short == Store + "::get_Directory")).ToList();
        Assert.True(callers.Count >= 3 && callers.All(u => u.Type == "StorageInventory.Library.LibrarySession"), "LibraryStore.Directory is used only by LibrarySession: " + string.Join(", ", callers.Select(u => u.Key)));
        Assert.True(callers.All(u => !u.Calls.Any(IlAudit.IsFileSystemApi)), "and those methods touch no file");
    }

    [Test]
    public static void C4_M17_file_system_rule_rejects_a_call_outside_LibraryStore()
    {
        var model = Fixtures.Value;
        var violations = IlAudit.FileSystemViolations(model, InFixtures, Store);
        foreach (var (method, api) in new[]
        {
            ("FileSystemOutsideTheStore::Exists(", "System.IO.File::Exists"), ("FileSystemOutsideTheStore::DirectoryExists(", "System.IO.Directory::Exists"),
            ("FileSystemOutsideTheStore::Length(", "System.IO.FileInfo"), ("FileSystemOutsideTheStore::PathExists(", "System.IO.Path::Exists"),
            ("DirectFileSystemMutation::Open(", "System.IO.FileStream::.ctor"), ("DirectFileSystemMutation::Make(", "System.IO.Directory::CreateDirectory"),
        })
        {
            Assert.True(violations.Any(v => v.Method.Contains(method, StringComparison.Ordinal) && v.Rule == IlAudit.RuleFileSystemOutsideStore && v.Detail.Contains(api, StringComparison.Ordinal)),
                $"the file-system rule misses {method} ({api}):\n{string.Join("\n", violations)}");
        }
        Assert.False(violations.Any(v => v.Method.Contains("InspectsAfterTheLock", StringComparison.Ordinal)), "a fixture that uses only LibraryStore is not reported");
        // the exemption is the named type and nothing else: declaring the fixture the "store" accepts it, any other name does not
        Assert.Equal(0, IlAudit.FileSystemViolations(model, t => t == Ns + "FileSystemOutsideTheStore", Ns + "FileSystemOutsideTheStore").Count, "the store type itself is exempt");
        Assert.True(IlAudit.FileSystemViolations(model, t => t == Ns + "FileSystemOutsideTheStore", Ns + "SomethingElse").Count >= 4, "another type is not");
        // a stream-taking writer or reader is not a file-system API; a path-taking one is
        Assert.True(IlAudit.IsFileSystemApi(new IlAudit.CallSite("System.IO.StreamReader", ".ctor", ["String"], IlAudit.CallKind.Newobj, 0, null)), "StreamReader(path)");
        Assert.False(IlAudit.IsFileSystemApi(new IlAudit.CallSite("System.IO.StreamReader", ".ctor", ["System.IO.Stream"], IlAudit.CallKind.Newobj, 0, null)), "StreamReader(stream)");
        Assert.False(IlAudit.IsFileSystemApi(new IlAudit.CallSite("System.IO.Path", "Combine", ["String", "String"], IlAudit.CallKind.Call, 0, null)), "Path.Combine is a string function");
    }

    // ================================================================ C4-M17 (2): the writer lock comes first

    private static readonly string[] TouchingCalls =
    [
        Store + "::InspectMembersUnderLock", Store + "::ReadHeaderUnderLock", Store + "::ReadHeader", Store + "::CreateEmptyDatabase", Store + "::QuarantineSet",
        Store + "::MainPathUnderLock", Store + "::MeasureLengthsUnderLock",
    ];

    /// <summary>Methods of <c>LibrarySession</c> that touch a member and take the lock nowhere: each is an entry that runs only on an open
    /// Library (the member accessors it calls throw without the lock, which Library.Tests' LockFirstTests proves). The writer lock is held
    /// from the start-up open until the process exits (CONC-01), so a lease-taking method of an open session is always under it.</summary>
    private static readonly string[] RuntimeGuardedEntries =
    [
        "StorageInventory.Library.LibrarySession::RecordAttemptStartAsync", "StorageInventory.Library.LibrarySession::RecordAttemptOutcomeAsync",
        "StorageInventory.Library.LibrarySession::ImportSnapshotAsync", "StorageInventory.Library.LibrarySession::DeleteSnapshotAsync", "StorageInventory.Library.LibrarySession::ReadAsync",
        "StorageInventory.Library.LibrarySession::Read", "StorageInventory.Library.LibrarySession::ReadDictionaryFootprintAsync",
    ];

    private static IlAudit.LockPolicy Policy(IEnumerable<string>? entries = null) =>
        new(Store + "::AcquireWriterLock", new HashSet<string>(TouchingCalls), Store, new HashSet<string>(entries ?? RuntimeGuardedEntries));

    [Test]
    public static void C4_M17_every_member_access_follows_the_writer_lock()
    {
        var model = FirstParty.Value;
        var policy = Policy();
        var violations = IlAudit.LockFirstViolations(model, InLibrary, policy);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));

        // the policy names real methods (a typo would make the rule vacuous)
        foreach (var touch in TouchingCalls) Assert.True(model.Units.Any(u => $"{u.Type}::{u.Name}" == touch), "LibraryStore has " + touch);
        Assert.True(model.Units.Any(u => $"{u.Type}::{u.Name}" == policy.Acquire), "LibraryStore has " + policy.Acquire);
        foreach (var entry in RuntimeGuardedEntries) Assert.True(model.Units.Any(u => $"{u.Type}::{u.Name}" == entry), "the declared entry exists: " + entry);
        var requiring = IlAudit.LockRequiring(model, InLibrary, policy).Select(k => k[..k.IndexOf('(', StringComparison.Ordinal)]).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.SequenceEqual([.. RuntimeGuardedEntries.Order(StringComparer.Ordinal)], requiring, "the entries that touch a member without taking the lock are exactly the declared ones");

        // the two methods the review names: the lock call comes first in IL, and every member call follows it
        foreach (var method in new[] { "DeriveCore", "SetAsideAsync" })
        {
            var unit = model.Units.Single(u => u.Type == "StorageInventory.Library.LibrarySession" && u.Name == method);
            var parts = unit.Parts.Where(p => p.Calls.Any(c => c.Short == policy.Acquire)).ToList();
            Assert.Equal(1, parts.Count, method + " takes the lock in exactly one body");
            var acquire = parts[0].Calls.First(c => c.Short == policy.Acquire).Offset;
            var touching = parts[0].Calls.Where(c => policy.Touching.Contains(c.Short)).ToList();
            Assert.True(touching.Count >= 2, $"{method} touches members ({touching.Count} calls): the check is not vacuous");
            Assert.True(touching.All(c => c.Offset > acquire), $"{method}: every member call follows AcquireWriterLock (IL offset {acquire}): {string.Join(", ", touching.Select(c => c.Short.Split("::")[1] + "@" + c.Offset))}");
            Console.WriteLine($"{method}: AcquireWriterLock@{acquire}, then {string.Join(", ", touching.Select(c => c.Short.Split("::")[1] + "@" + c.Offset))}");
        }
        // only the two opening paths take the lock
        Assert.SequenceEqual(["DeriveCore", "SetAsideAsync"], [.. model.Units.Where(u => InLibrary(u.Type) && u.Calls.Any(c => c.Short == policy.Acquire)).Select(u => u.Name).Order(StringComparer.Ordinal)], "only DeriveCore and SetAsideAsync take the writer lock");
    }

    [Test]
    public static void C4_M17_lock_first_rule_rejects_an_inspection_before_the_lock()
    {
        var model = Fixtures.Value;
        var violations = IlAudit.LockFirstViolations(model, InFixtures, Policy([]));
        foreach (var (method, call) in new[]
        {
            ("InspectsBeforeTheLock::Derive(", "ReadHeader"), ("InspectsBeforeTheLock::Derive(", "InspectMembersUnderLock"), ("InspectsBeforeTheLock::ReadHeaderFirst(", "ReadHeaderUnderLock"),
            ("InspectsBeforeTheLock::CreateFirst(", "CreateEmptyDatabase"), ("InspectsBeforeTheLock::SetAside(", "QuarantineSet"), ("InspectsBeforeTheLock::Path(", "MainPathUnderLock"),
            ("CallsALockRequiringMethodBeforeTheLock::Derive(", "CallsALockRequiringMethodBeforeTheLock::Check"),
        })
        {
            Assert.True(violations.Any(v => v.Method.Contains(method, StringComparison.Ordinal) && v.Rule == IlAudit.RuleBeforeLock && v.Detail.Contains(call, StringComparison.Ordinal)),
                $"the lock-first rule misses {method} -> {call}:\n{string.Join("\n", violations)}");
        }
        Assert.True(violations.Any(v => v.Method.Contains("InspectsWithoutTheLock::Exists(", StringComparison.Ordinal) && v.Rule == IlAudit.RuleLockless), "a method that inspects a member and never takes the lock, and is not a declared entry:\n" + string.Join("\n", violations));
        // declaring it an entry (the policy's explicit list) is what accepts it
        Assert.False(IlAudit.LockFirstViolations(model, InFixtures, Policy([Ns + "InspectsWithoutTheLock::Exists"])).Any(v => v.Rule == IlAudit.RuleLockless && v.Method.Contains("InspectsWithoutTheLock", StringComparison.Ordinal)), "a declared entry is accepted");
        Assert.False(violations.Any(v => v.Method.Contains("InspectsAfterTheLock", StringComparison.Ordinal)), "the lock first, then the members, is accepted:\n" + string.Join("\n", violations));
        // the first fixture really has ReadHeader at a lower IL offset than the lock
        var unit = model.Units.Single(u => u.Key.StartsWith(Ns + "InspectsBeforeTheLock::Derive(", StringComparison.Ordinal));
        var lockAt = unit.Calls.First(c => c.Short == Store + "::AcquireWriterLock").Offset;
        Assert.True(unit.Calls.First(c => c.Short == Store + "::ReadHeader").Offset < lockAt, "the fixture's ReadHeader is at a lower IL offset than the lock");
    }

    // ================================================================ C4-M13: constant SQL

    /// <summary>Every member of every <c>*Sql</c> type is a <c>const string</c>: no <c>static readonly</c> field, no property, no method, no
    /// static constructor (a built string would escape the plan check and the text rules).</summary>
    internal static List<string> SqlFieldViolations(IEnumerable<Type> types)
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(all))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string)) violations.Add($"{type.Name}.{field.Name} is not a const string ({(field.IsStatic ? "static " : "")}{(field.IsInitOnly ? "readonly " : "")}{field.FieldType.Name})");
            }
            if (type.TypeInitializer is not null) violations.Add($"{type.Name} has a static constructor");
            foreach (var property in type.GetProperties(all)) violations.Add($"{type.Name}.{property.Name} is a property");
            foreach (var method in type.GetMethods(all).Where(m => !m.IsSpecialName)) violations.Add($"{type.Name}.{method.Name} is a method");
        }
        return violations;
    }

    internal static IEnumerable<Type> SqlTypes(Assembly assembly) =>
        assembly.GetTypes().Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.Namespace == "StorageInventory.Library");

    /// <summary>The text shape of <c>Sql/*.cs</c>: after comments and literals are removed, nothing but <c>const string</c> declarations.</summary>
    internal static List<string> SqlFileShapeViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources.Where(kv => kv.Key.Replace('\\', '/').Contains("/Sql/", StringComparison.Ordinal)))
        {
            var code = string.Join("\n", raw.Split('\n').Select(SecurityAuditTests.StripComment));
            code = Regex.Replace(code, "\\$?\"\"\"[\\s\\S]*?\"\"\"", "\"\"");               // raw string literals
            code = Regex.Replace(code, "\\$?\"(\\\\.|[^\"\\\\\\n])*\"", m => m.Value.StartsWith('$') ? "$\"\"" : "\"\"");   // ordinary and interpolated literals
            code = Regex.Replace(code, @"^\s*(namespace\s+[\w.]+;|using\s+[\w.]+;|internal\s+static\s+class\s+\w+Sql)\s*$", "", RegexOptions.Multiline);
            code = code.Replace("{", "").Replace("}", "");
            foreach (var statement in code.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                if (!Regex.IsMatch(statement, @"^(internal|public|private)?\s*const\s+string\s+\w+\s*=\s*""""(\s*\+\s*"""")*$")) violations.Add($"{file}: '{Regex.Replace(statement, @"\s+", " ")}' is not a const string declaration");
            }
        }
        return violations;
    }

    private static readonly Regex SqlParameterName = new(@"^(sql|setSql|getSql|\w+Sql)$", RegexOptions.Compiled);

    /// <summary>Every call, in text, of a method that takes SQL text (found from the IL: a <c>string</c> parameter named sql, setSql, getSql or
    /// ending in Sql) passes at that position a constant of a <c>*Sql</c> class or a bare forwarded SQL parameter; and no local or field is
    /// named like a SQL parameter. A call is matched to an overload by its argument count.</summary>
    internal static List<string> ForwarderCallViolations(IReadOnlyDictionary<string, string> sources, IReadOnlyList<IlAudit.SqlForwarder> forwarders)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources.Where(kv => !kv.Key.Replace('\\', '/').Contains("/Sql/", StringComparison.Ordinal)))
        {
            var code = string.Join("\n", raw.Split('\n').Select(SecurityAuditTests.StripComment));
            foreach (Match m in Regex.Matches(code, @"\b(?:string|var)\??\s+(sql|setSql|getSql)\s*(=|;)")) violations.Add($"{file}: a local or field named '{m.Groups[1].Value}' ('{m.Value.Trim()}'): SQL text is a Sql constant or a parameter");
            foreach (var group in forwarders.GroupBy(f => f.Method))
            {
                foreach (Match m in Regex.Matches(code, $@"(?:(?<dot>\.)|(?<![\w.])){Regex.Escape(group.Key)}\s*\("))
                {
                    if (!m.Groups["dot"].Success)
                    {
                        // a declaration (a type, then the name) is not a call
                        var before = code[..m.Index].TrimEnd();
                        var declaration = before.Length > 0 && (char.IsLetterOrDigit(before[^1]) || before[^1] is '_' or '>' or ']' or '?')
                            && !Regex.IsMatch(before, @"\b(return|await|else|case|yield|new|in|is|when|and|or|not|do)$");
                        if (declaration) continue;
                    }
                    var arguments = SplitTop(CallText(code, m.Index + m.Length));
                    var overloads = group.Where(f => arguments.Count == f.ParameterCount || (f.HasParams && arguments.Count >= f.ParameterCount - 1)).ToList();
                    if (overloads.Count == 0) continue;
                    // the call is judged against the overloads its argument count fits: it is clean when one of them is satisfied
                    var failures = overloads.Select(f => f.Indexes.Select(i => arguments[i].Trim()).FirstOrDefault(a => !Regex.IsMatch(a, @"^(sql|setSql|getSql|[A-Za-z]+Sql\.[A-Za-z0-9]+)$"))).ToList();
                    if (failures.All(f => f is not null)) violations.Add($"{file}: {group.Key}(...{failures[0]}...) does not pass a Sql constant or a forwarded SQL parameter");
                }
            }
        }
        return violations;
    }

    private static string CallText(string code, int start)
    {
        int depth = 1, i = start;
        var quoted = false;
        for (; i < code.Length && depth > 0; i++)
        {
            if (code[i] == '"') quoted = !quoted;
            if (quoted) continue;
            if (code[i] == '(') depth++;
            else if (code[i] == ')') depth--;
        }
        return code[start..Math.Max(start, i - 1)];
    }

    private static List<string> SplitTop(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            if (quoted) continue;
            if (text[i] is '(' or '[' or '{') depth++;
            else if (text[i] is ')' or ']' or '}') depth--;
            else if (text[i] == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static List<IlAudit.SqlForwarder> Forwarders() => IlAudit.SqlForwarders(typeof(StorageInventory.Library.LibraryNames).Assembly.Location, SqlParameterName.IsMatch);

    private static Dictionary<string, string> LibrarySources() => SecurityAuditTests.Sources.Value
        .Where(kv => kv.Key.Replace('\\', '/').StartsWith("src/StorageInventory.Library/", StringComparison.Ordinal) && kv.Key.EndsWith(".cs", StringComparison.Ordinal))
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    [Test]
    public static void C4_M13_every_field_of_the_Sql_classes_is_a_constant_and_every_forwarder_is_fed_a_constant()
    {
        var assembly = typeof(StorageInventory.Library.LibraryNames).Assembly;
        var types = SqlTypes(assembly).ToList();
        Assert.True(types.Count >= 7, $"{types.Count} Sql classes were found: {string.Join(", ", types.Select(t => t.Name))}");
        var fields = SqlFieldViolations(types);
        Assert.Equal(0, fields.Count, string.Join(Environment.NewLine, fields));
        Assert.True(types.Sum(t => t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Count(f => f.IsLiteral)) >= 100, "the constants were counted");

        var sources = LibrarySources();
        var shape = SqlFileShapeViolations(sources);
        Assert.Equal(0, shape.Count, string.Join(Environment.NewLine, shape));
        Assert.True(sources.Keys.Count(k => k.Replace('\\', '/').Contains("/Sql/", StringComparison.Ordinal)) >= 7, "the Sql files were found");

        // the forwarders are found from the IL, not from a list in this test: every method that takes SQL text
        var forwarders = Forwarders();
        var names = forwarders.Select(f => f.Method).Distinct().Order(StringComparer.Ordinal).ToList();
        Console.WriteLine("methods that take SQL text: " + string.Join(", ", names));
        foreach (var expected in new[] { "Execute", "Count", "CheckPermutation", "Scalar", "Rows", "Prepare", "Query", "Long", "PragmaText", "SetAndReadBack", "Delete", "ReadBack", "Build" })
        {
            Assert.True(names.Contains(expected), "the analysis finds the forwarder " + expected);
        }
        var violations = ForwarderCallViolations(sources, forwarders);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));
        var checkedCalls = names.Sum(n => sources.Where(kv => !kv.Key.Replace('\\', '/').Contains("/Sql/", StringComparison.Ordinal)).Sum(kv => Regex.Matches(kv.Value, $@"\b{n}\(").Count));
        Assert.True(checkedCalls > 50, $"{checkedCalls} call sites of the forwarders exist (the rule is not vacuous)");
    }

    [Test]
    public static void C4_M13_rejects_a_static_readonly_statement_a_built_string_and_a_non_constant_forwarder_argument()
    {
        // a static readonly string, a built string, a static constructor
        var fixtureTypes = typeof(LeaseAuditTests).Assembly.GetTypes().Where(t => t.Namespace == "StorageInventory.IntegrationTests.LeaseAuditFixtures" && t.Name.StartsWith("Fixture", StringComparison.Ordinal) && t.Name.EndsWith("Sql", StringComparison.Ordinal)).ToList();
        var built = SqlFieldViolations(fixtureTypes.Where(t => t.Name == "FixtureBuiltSql"));
        Assert.True(built.Any(v => v.Contains("Table is not a const string", StringComparison.Ordinal) && v.Contains("static readonly", StringComparison.Ordinal)), "a static readonly string: " + string.Join("; ", built));
        Assert.True(built.Any(v => v.Contains("Select is not a const string", StringComparison.Ordinal)), "a static readonly built string: " + string.Join("; ", built));
        Assert.True(built.Any(v => v.Contains("static constructor", StringComparison.Ordinal)), "a built string needs a static constructor: " + string.Join("; ", built));
        Assert.Equal(0, SqlFieldViolations(fixtureTypes.Where(t => t.Name == "FixtureConstantSql")).Count, "a class of constants is accepted");

        // the text shape of a Sql file
        var shape = SqlFileShapeViolations(new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/Sql/A.cs"] = "internal static class ASql { internal static readonly string X = \"SELECT 1\"; }",
            ["src/StorageInventory.Library/Sql/B.cs"] = "internal static class BSql { internal const string X = \"SELECT \" + Table; }",
            ["src/StorageInventory.Library/Sql/C.cs"] = "internal static class CSql { internal static string X => \"SELECT 1\"; }",
            ["src/StorageInventory.Library/Sql/D.cs"] = "internal static class DSql { internal const string X = $\"SELECT {Y}\"; }",
            ["src/StorageInventory.Library/Sql/E.cs"] = "internal static class ESql { internal static readonly string[] All = [A, B]; }",
        });
        foreach (var file in new[] { "A.cs", "B.cs", "C.cs", "D.cs", "E.cs" }) Assert.True(shape.Any(v => v.Contains("/Sql/" + file, StringComparison.Ordinal)), $"the Sql file shape rule misses {file}: {string.Join(" | ", shape)}");
        var finePlain = SqlFileShapeViolations(new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/Sql/F.cs"] = "namespace X;\n// SELECT in a comment\n/// <summary>doc</summary>\ninternal static class FSql\n{\n    internal const string A = \"SELECT 1\";\n    internal const string B = \"\"\"\n        SELECT 2\n        \"\"\";\n    internal const string C = \"PRAGMA a\" + \"b\";\n}",
        });
        Assert.Equal(0, finePlain.Count, "a class of const strings (raw literals and a constant concatenation) is accepted: " + string.Join(" | ", finePlain));

        // forwarders: a non-constant argument, an interpolated string, a concatenation, a loop variable, a local named sql
        var forwarders = new List<IlAudit.SqlForwarder>
        {
            new("T", "Execute", 3, false, [2]), new("T", "Count", 3, false, [1]), new("T", "Prepare", 3, true, [1]), new("T", "Scalar", 2, true, [0]), new("T", "CheckPermutation", 5, false, [1]),
        };
        var bad = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/F1.cs"] = "Execute(writer, lease, ddl);",
            ["src/StorageInventory.Library/F2.cs"] = "var n = Count(q, $\"SELECT count(*) FROM {table}\", p);",
            ["src/StorageInventory.Library/F3.cs"] = "using var s = writer.Prepare(lease, ImportSql.A + name);",
            ["src/StorageInventory.Library/F4.cs"] = "var x = q.Scalar(text);",
            ["src/StorageInventory.Library/F5.cs"] = "return CheckPermutation(q, ReadSql.A.ToString(), p, 1, c);",
            ["src/StorageInventory.Library/F6.cs"] = "string sql = Build(); writer.Prepare(lease, sql);",
            ["src/StorageInventory.Library/F7.cs"] = "Execute(writer, lease, name + \"x\");",
        };
        var found = ForwarderCallViolations(bad, forwarders);
        foreach (var file in new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7" }) Assert.True(found.Any(v => v.Contains(file + ".cs", StringComparison.Ordinal)), $"the forwarder rule misses {file}: {string.Join(" | ", found)}");
        var fine = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/G1.cs"] = "Execute(writer, lease, SchemaSql.CreateName); var n = Count(q, ImportSql.VerifyFileNames, p); using var s = writer.Prepare(lease, ImportSql.InsertName, \"$utf16\");",
            ["src/StorageInventory.Library/G2.cs"] = "private static long Count(IQueryRunner q, string sql, (string, object?)[] p) => Convert.ToInt64(q.Scalar(sql, p));\nprivate static void Execute(WriterConnection w, MutationLease lease, string sql) { using var s = w.Prepare(lease, sql); }\nvar a = Count(q, sql, p);",
        };
        Assert.Equal(0, ForwarderCallViolations(fine, forwarders).Count, string.Join(" | ", ForwarderCallViolations(fine, forwarders)));
    }
}
