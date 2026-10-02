namespace StorageInventory.Library;

/// <summary>SQL used only when a test asks for a real engine failure (<see cref="LibraryFaultInjection"/>); production never
/// reaches it. A fixed constant, because SQL text is never built from a value (A-05, A-22).</summary>
internal static class FaultSql
{
    /// <summary>The engine's own database size limit: growing past it fails with <c>SQLITE_FULL</c>, exactly as a full disk does,
    /// and rolls the transaction back.</summary>
    internal const string LimitToTwoThousandPages = "PRAGMA max_page_count = 2048";
}
