using ClickHouse.Driver.ADO;
using RepoDb.ClickHouse.Bulk;

namespace RepoDb.ClickHouse.Extensions;

/// <summary>
/// Extension methods that expose native bulk insert (<see cref="ClickHouseBulkInserter{TEntity}"/>) as
/// fluent calls on <see cref="ClickHouseConnection"/> and <see cref="ClickHouseRepository{TEntity}"/>.
/// </summary>
/// <remarks>
/// These methods use <c>ClickHouseClient.InsertBinaryAsync</c> (the RowBinary binary protocol) instead
/// of RepoDb's textual, parameterized ADO.NET batching, yielding roughly 3–5x higher throughput on
/// volumes above ~100,000 rows. They all take the base driver's <see cref="ClickHouseConnection"/> —
/// not <see cref="RepoDbClickHouseConnection"/> — because the RowBinary protocol never goes through
/// SQL text with parameter placeholders, so the parameter-rewriting layer that
/// <see cref="RepoDbClickHouseConnection"/> exists for is not needed here; a
/// <see cref="RepoDbClickHouseConnection"/> instance is still accepted by every overload, since it is
/// itself a <see cref="ClickHouseConnection"/>.
/// </remarks>
/// <example>
/// <code>
/// await using var conn = new RepoDbClickHouseConnection(connectionString);
/// var result = await conn.BulkInsertAsync(events, ClickHouseBulkInsertOptions.HighThroughput);
/// </code>
/// </example>
public static class ClickHouseBulkInsertExtensions
{
    // ─── On ClickHouseConnection ─────────────────────────────────────────────

    /// <summary>
    /// Performs a native bulk insert from an in-memory <see cref="IEnumerable{TEntity}"/>, splitting
    /// it automatically into batches of <see cref="ClickHouseBulkInsertOptions.BatchSize"/> rows.
    /// </summary>
    /// <typeparam name="TEntity">The entity type; resolves its table via <c>[Map]</c>, falling back to the class name.</typeparam>
    /// <param name="connection">The ClickHouse connection; only its connection string is used, to build an independent native <c>ClickHouseClient</c>.</param>
    /// <param name="entities">The collection to insert.</param>
    /// <param name="options">Bulk-insert options. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used.</param>
    /// <param name="cancellationToken">Token observed between (and, with parallel options, during) batches.</param>
    /// <returns>Statistics describing the completed operation.</returns>
    public static Task<ClickHouseBulkInsertResult> BulkInsertAsync<TEntity>(
        this ClickHouseConnection connection,
        IEnumerable<TEntity> entities,
        ClickHouseBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var inserter = new ClickHouseBulkInserter<TEntity>(
            connection.ConnectionString, options);
        return inserter.InsertAsync(entities, cancellationToken);
    }

    /// <summary>
    /// Performs a native bulk insert from an <see cref="IAsyncEnumerable{TEntity}"/>, streaming rows
    /// without materializing the whole source in memory.
    /// </summary>
    /// <remarks>Ideal for large data sources: files, Kafka/RabbitMQ queues, or a paged upstream query.</remarks>
    /// <typeparam name="TEntity">The entity type; resolves its table via <c>[Map]</c>, falling back to the class name.</typeparam>
    /// <param name="connection">The ClickHouse connection; only its connection string is used, to build an independent native <c>ClickHouseClient</c>.</param>
    /// <param name="entities">The asynchronous entity stream to insert.</param>
    /// <param name="options">Bulk-insert options. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used.</param>
    /// <param name="cancellationToken">Token observed while draining <paramref name="entities"/> and between/during batches.</param>
    /// <returns>Statistics describing the completed operation.</returns>
    public static Task<ClickHouseBulkInsertResult> BulkInsertAsync<TEntity>(
        this ClickHouseConnection connection,
        IAsyncEnumerable<TEntity> entities,
        ClickHouseBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var inserter = new ClickHouseBulkInserter<TEntity>(
            connection.ConnectionString, options);
        return inserter.InsertAsync(entities, cancellationToken);
    }

    /// <summary>
    /// Performs a native bulk insert with an explicit destination table override.
    /// </summary>
    /// <remarks>
    /// Useful when the destination table name does not match the entity's <c>[Map]</c> attribute or
    /// class name — for example, inserting into a per-tenant or per-date-partitioned table computed at
    /// runtime.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="connection">The ClickHouse connection; only its connection string is used, to build an independent native <c>ClickHouseClient</c>.</param>
    /// <param name="entities">The collection to insert.</param>
    /// <param name="tableName">The destination table name, overriding whatever <paramref name="baseOptions"/> or entity metadata would otherwise resolve.</param>
    /// <param name="baseOptions">The options to layer <paramref name="tableName"/> onto via a non-destructive <c>with</c> expression. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used as the base.</param>
    /// <param name="cancellationToken">Token observed between (and, with parallel options, during) batches.</param>
    /// <returns>Statistics describing the completed operation.</returns>
    /// <example>
    /// <code>
    /// await conn.BulkInsertAsync(events, tableName: $"cdr_events_{DateTime.UtcNow:yyyyMM}");
    /// </code>
    /// </example>
    public static Task<ClickHouseBulkInsertResult> BulkInsertAsync<TEntity>(
        this ClickHouseConnection connection,
        IEnumerable<TEntity> entities,
        string tableName,
        ClickHouseBulkInsertOptions? baseOptions = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var options = (baseOptions ?? ClickHouseBulkInsertOptions.Default)
            with { TableName = tableName };
        var inserter = new ClickHouseBulkInserter<TEntity>(
            connection.ConnectionString, options);
        return inserter.InsertAsync(entities, cancellationToken);
    }

    // ─── On ClickHouseRepository<TEntity> ───────────────────────────────────

    /// <summary>
    /// Performs a native bulk insert directly from a typed repository, without requiring callers to
    /// instantiate <see cref="ClickHouseBulkInserter{TEntity}"/> manually.
    /// </summary>
    /// <typeparam name="TEntity">The entity type the repository is bound to.</typeparam>
    /// <param name="repository">The repository whose <see cref="ClickHouseRepository{TEntity}.ConnectionString"/> is used to build an independent native <c>ClickHouseClient</c>.</param>
    /// <param name="entities">The collection to insert.</param>
    /// <param name="options">Bulk-insert options. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used.</param>
    /// <param name="cancellationToken">Token observed between (and, with parallel options, during) batches.</param>
    /// <returns>Statistics describing the completed operation.</returns>
    public static Task<ClickHouseBulkInsertResult> BulkInsertAsync<TEntity>(
        this ClickHouseRepository<TEntity> repository,
        IEnumerable<TEntity> entities,
        ClickHouseBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var inserter = new ClickHouseBulkInserter<TEntity>(
            repository.ConnectionString, options);
        return inserter.InsertAsync(entities, cancellationToken);
    }

    /// <summary>
    /// Performs a streaming native bulk insert directly from a typed repository.
    /// </summary>
    /// <typeparam name="TEntity">The entity type the repository is bound to.</typeparam>
    /// <param name="repository">The repository whose <see cref="ClickHouseRepository{TEntity}.ConnectionString"/> is used to build an independent native <c>ClickHouseClient</c>.</param>
    /// <param name="entities">The asynchronous entity stream to insert.</param>
    /// <param name="options">Bulk-insert options. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used.</param>
    /// <param name="cancellationToken">Token observed while draining <paramref name="entities"/> and between/during batches.</param>
    /// <returns>Statistics describing the completed operation.</returns>
    public static Task<ClickHouseBulkInsertResult> BulkInsertAsync<TEntity>(
        this ClickHouseRepository<TEntity> repository,
        IAsyncEnumerable<TEntity> entities,
        ClickHouseBulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var inserter = new ClickHouseBulkInserter<TEntity>(
            repository.ConnectionString, options);
        return inserter.InsertAsync(entities, cancellationToken);
    }
}
