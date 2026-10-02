namespace StorageInventory.Library;

/// <summary>Transaction control, as constant text. Every write transaction is <c>BEGIN IMMEDIATE</c> ... <c>COMMIT</c> on the one
/// writer connection with <c>synchronous = FULL</c> (CONC-03).</summary>
internal static class TransactionSql
{
    internal const string BeginImmediate = "BEGIN IMMEDIATE";
    internal const string Commit = "COMMIT";
    internal const string Rollback = "ROLLBACK";
}
