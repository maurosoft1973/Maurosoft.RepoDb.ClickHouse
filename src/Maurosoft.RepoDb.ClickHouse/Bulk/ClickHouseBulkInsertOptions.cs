namespace RepoDb.ClickHouse.Bulk;

/// <summary>
/// Configuration for a native bulk insert performed through <c>ClickHouseClient.InsertBinaryAsync</c>
/// (the RowBinary wire protocol), as opposed to the textual, parameterized ADO.NET path used by
/// <c>InsertAllAsync</c>.
/// </summary>
/// <remarks>
/// This is a <see langword="record"/> so instances are immutable and can be customized with
/// non-destructive <c>with</c> expressions starting from <see cref="Default"/> or one of the presets
/// (<see cref="HighThroughput"/>, <see cref="Diagnostic"/>) without mutating the shared static instance.
/// </remarks>
/// <example>
/// <code>
/// var options = ClickHouseBulkInsertOptions.HighThroughput with { BatchSize = 250_000 };
/// var result = await connection.BulkInsertAsync(events, options);
/// </code>
/// </example>
public sealed record ClickHouseBulkInsertOptions
{
    /// <summary>
    /// Number of rows sent per <c>InsertBinaryAsync</c> call.
    /// </summary>
    /// <remarks>
    /// Recommended range: 50,000–500,000 rows. Larger batches amortize HTTP and RowBinary framing
    /// overhead better, but hold more data in memory per in-flight request and increase the amount of
    /// work lost if a single batch fails. Default: 100,000.
    /// </remarks>
    public int BatchSize { get; init; } = 100_000;

    /// <summary>
    /// Maximum number of batches sent concurrently.
    /// </summary>
    /// <remarks>
    /// Default: 1 (sequential). Increase with caution — each concurrent batch opens its own HTTP
    /// connection to ClickHouse, and ordering across batches is not preserved when this is greater
    /// than 1 (acceptable for ClickHouse inserts, since row order within a table is not guaranteed
    /// by the engine anyway).
    /// </remarks>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// When <see langword="true"/>, entity fields are resolved through RepoDb's own field cache,
    /// honoring <c>[Map]</c> and any registered <c>PropertyHandler</c>. When <see langword="false"/>,
    /// fields are resolved through direct reflection over the CLR properties, which is marginally
    /// faster but bypasses RepoDb's mapping annotations. Default: <see langword="true"/>.
    /// </summary>
    public bool UseRepoDbFieldMapping { get; init; } = true;

    /// <summary>
    /// Destination table name. When <see langword="null"/>, it is inferred from the entity's
    /// <c>[Map]</c> attribute, falling back to the CLR class name.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// Timeout, in seconds, applied to each individual bulk call. Default: 300 (5 minutes), which is
    /// generally sufficient for batches in the hundreds-of-thousands-of-rows range; increase it for
    /// very large batches or slow network links.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// When <see langword="true"/>, the elapsed time of each batch is logged via
    /// <see cref="System.Diagnostics.Debug"/>. Useful while tuning <see cref="BatchSize"/> and
    /// <see cref="MaxDegreeOfParallelism"/>; leave disabled in production to avoid the logging overhead.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool EnableDiagnostics { get; init; } = false;

    /// <summary>
    /// Conservative default configuration (sequential, 100,000-row batches, no diagnostics).
    /// </summary>
    public static ClickHouseBulkInsertOptions Default { get; } = new();

    /// <summary>
    /// Preset tuned for maximum throughput: large batches with a degree of parallelism of 2.
    /// Suitable for ingestion pipelines where per-batch latency is not a concern and the caller can
    /// tolerate the extra memory/connection overhead of concurrent batches.
    /// </summary>
    public static ClickHouseBulkInsertOptions HighThroughput { get; } = new()
    {
        BatchSize                = 500_000,
        MaxDegreeOfParallelism   = 2,
        UseRepoDbFieldMapping    = true,
        CommandTimeoutSeconds    = 600,
        EnableDiagnostics        = false,
    };

    /// <summary>
    /// Preset for local development and troubleshooting: small batches with per-batch diagnostics
    /// enabled, trading throughput for visibility into batch timing.
    /// </summary>
    public static ClickHouseBulkInsertOptions Diagnostic { get; } = new()
    {
        BatchSize             = 10_000,
        MaxDegreeOfParallelism = 1,
        EnableDiagnostics     = true,
        CommandTimeoutSeconds = 60,
    };
}
