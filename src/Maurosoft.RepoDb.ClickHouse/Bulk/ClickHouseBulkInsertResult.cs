namespace RepoDb.ClickHouse.Bulk;

/// <summary>
/// Statistics returned by a native bulk insert operation
/// (see <see cref="ClickHouseBulkInserter{TEntity}"/>).
/// </summary>
public sealed class ClickHouseBulkInsertResult
{
    /// <summary>Total number of rows sent to ClickHouse across all batches.</summary>
    public long RowsWritten { get; init; }

    /// <summary>Number of <c>InsertBinaryAsync</c> batches issued.</summary>
    public int BatchCount { get; init; }

    /// <summary>Wall-clock duration of the whole operation, from the first batch to the last.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Average throughput in rows per second, computed as <see cref="RowsWritten"/> divided by
    /// <see cref="Elapsed"/>. Returns 0 when <see cref="Elapsed"/> is zero (e.g. an empty input
    /// collection that produced no batches) to avoid a division-by-zero / infinity result.
    /// </summary>
    public double RowsPerSecond =>
        Elapsed.TotalSeconds > 0 ? RowsWritten / Elapsed.TotalSeconds : 0;

    /// <summary>Name of the destination table the rows were written to.</summary>
    public string TableName { get; init; } = "";

    /// <summary>
    /// Returns a one-line, human-readable summary of the operation, suitable for logging.
    /// </summary>
    /// <example>
    /// <c>BulkInsert 'cdr_events': 500,000 rows in 5 batches, 3.42s (146,199 rows/s)</c>
    /// </example>
    public override string ToString()
        => $"BulkInsert '{TableName}': {RowsWritten:N0} rows in {BatchCount} batches, " +
           $"{Elapsed.TotalSeconds:F2}s ({RowsPerSecond:N0} rows/s)";
}
