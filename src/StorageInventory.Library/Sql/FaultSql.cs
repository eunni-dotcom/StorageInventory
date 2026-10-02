namespace StorageInventory.Library;

/// <summary>SQL used only when a test asks for a real engine failure (<see cref="LibraryFaultInjection"/>); production never
/// reaches it. A fixed constant, because SQL text is never built from a value (A-05, A-22).</summary>
internal static class FaultSql
{
    /// <summary>The engine's own database size limit: growing past it fails with <c>SQLITE_FULL</c>, exactly as a full disk does,
    /// and rolls the transaction back.</summary>
    internal const string LimitToTwoThousandPages = "PRAGMA max_page_count = 2048";

    /// <summary>The same at benchmark scale: 20,000 pages (80 MiB), so that a multi-million-row import fails with <c>SQLITE_FULL</c>
    /// after most of its work is done and its rollback can be measured (TEST-P1).</summary>
    internal const string LimitToTwentyThousandPages = "PRAGMA max_page_count = 20000";
}
