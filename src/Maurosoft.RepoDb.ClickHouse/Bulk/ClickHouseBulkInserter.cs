using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using RepoDb.Attributes;
using System.Diagnostics;

namespace RepoDb.ClickHouse.Bulk;

/// <summary>
/// Native bulk-insert engine for ClickHouse.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>ClickHouseClient.InsertBinaryAsync</c> (the RowBinary wire protocol over HTTP) instead of
/// RepoDb's standard textual, parameterized ADO.NET batching (<c>InsertAllAsync</c>).
/// </para>
/// <para>Advantages over ADO.NET <c>InsertAllAsync</c>:</para>
/// <list type="bullet">
///   <item>Binary RowBinary serialization: roughly 3–5x less network overhead than textual SQL.</item>
///   <item>No server-side SQL parsing per row.</item>
///   <item>Native <see cref="IAsyncEnumerable{T}"/> support: true streaming, without buffering the whole source in memory.</item>
///   <item>Direct support for ClickHouse-specific types (<c>DateTime64</c>, <c>UUID</c>, <c>LowCardinality</c>).</item>
/// </list>
/// <para>When to prefer this over <c>InsertAllAsync</c>:</para>
/// <list type="bullet">
///   <item>Batches larger than roughly 100,000 rows.</item>
///   <item>High-volume, near-real-time CDR/event ingestion pipelines.</item>
///   <item>Nightly batch imports from files or message queues.</item>
/// </list>
/// <para>
/// This type does not go through <see cref="RepoDbClickHouseConnection"/>'s parameter-rewriting layer:
/// the RowBinary protocol never embeds parameter placeholders in SQL text, so it is unaffected by
/// ClickHouse.Driver's <c>{name:Type}</c> substitution syntax requirement (see
/// <see cref="ClickHouseParameterizedCommand"/>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var inserter = new ClickHouseBulkInserter&lt;CdrEvent&gt;(connectionString, ClickHouseBulkInsertOptions.HighThroughput);
/// var result = await inserter.InsertAsync(events, cancellationToken);
/// Console.WriteLine(result); // BulkInsert 'cdr_events': 500,000 rows in 2 batches, 2.81s (177,935 rows/s)
/// </code>
/// </example>
/// <param name="connectionString">
/// The ClickHouse ADO.NET connection string; reused both for schema introspection (when
/// <see cref="ClickHouseBulkInsertOptions.UseRepoDbFieldMapping"/> is enabled) and to build the
/// native <see cref="ClickHouseClient"/> used for the actual inserts.
/// </param>
/// <param name="options">
/// Bulk-insert tuning options. When <see langword="null"/>, <see cref="ClickHouseBulkInsertOptions.Default"/> is used.
/// </param>
/// <exception cref="ArgumentNullException"><paramref name="connectionString"/> is <see langword="null"/>.</exception>
public sealed class ClickHouseBulkInserter<TEntity>(
    string connectionString,
    ClickHouseBulkInsertOptions? options = null) where TEntity : class
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));
    private readonly ClickHouseBulkInsertOptions _options = options ?? ClickHouseBulkInsertOptions.Default;
    private EntityFieldMapper<TEntity>? _mapper;

    // ─── IEnumerable overload (materialize + chunk) ───────────────────────────

    /// <summary>
    /// Inserts an in-memory collection, splitting it into batches of
    /// <see cref="ClickHouseBulkInsertOptions.BatchSize"/> rows.
    /// </summary>
    /// <param name="entities">The entities to insert. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">
    /// Token observed between batches (and, in parallel mode, before each batch starts); a cancelled
    /// token surfaces as an <see cref="OperationCanceledException"/> from the awaited task.
    /// </param>
    /// <returns>Aggregate statistics for the whole operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is <see langword="null"/>.</exception>
    public async Task<ClickHouseBulkInsertResult> InsertAsync(
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return await InsertAsync(entities.ToAsyncEnumerable(), cancellationToken);
    }

    // ─── IAsyncEnumerable overload (true streaming) ───────────────────────────

    /// <summary>
    /// Inserts an asynchronous stream of entities without materializing it in memory ahead of time.
    /// </summary>
    /// <remarks>
    /// Ideal for large or unbounded sources — files, message queues, an upstream query result — where
    /// buffering everything into a <see cref="List{T}"/> first would be wasteful or infeasible. Items
    /// are still buffered one <see cref="ClickHouseBulkInsertOptions.BatchSize"/>-sized batch at a
    /// time (see <see cref="ChunkAsync"/>).
    /// </remarks>
    /// <param name="entities">The asynchronous entity stream to insert. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">
    /// Token observed while draining <paramref name="entities"/> and between/during batches.
    /// </param>
    /// <returns>Aggregate statistics for the whole operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is <see langword="null"/>.</exception>
    public async Task<ClickHouseBulkInsertResult> InsertAsync(
        IAsyncEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var sw = Stopwatch.StartNew();
        var tableName = ResolveTableName();
        var mapper = await GetOrBuildMapperAsync(tableName, cancellationToken);
        var columns = mapper.Bindings.Select(b => b.ColumnName).ToArray();

        long totalRows = 0;
        var batchCount = 0;

        // ClickHouseClient is thread-safe and cheap to keep open for the duration of the operation.
        using var client = BuildClient();

        if (_options.MaxDegreeOfParallelism <= 1)
        {
            // ─── Sequential mode (default) ────────────────────────────────────
            await foreach (var batch in ChunkAsync(entities, _options.BatchSize, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchSw = _options.EnableDiagnostics ? Stopwatch.StartNew() : null;

                await client.InsertBinaryAsync(
                    tableName,
                    columns,
                    batch.Select(row => mapper.Bindings.Select(b => b.ValueGetter(row)).ToArray()),
                    cancellationToken: cancellationToken);

                totalRows += batch.Count;
                batchCount++;

                if (_options.EnableDiagnostics)
                    Debug.WriteLine(
                        $"[BulkInsert] Batch {batchCount}: {batch.Count:N0} rows in {batchSw!.ElapsedMilliseconds}ms");
            }
        }
        else
        {
            // ─── Parallel mode ─────────────────────────────────────────────────
            // Batches are drained and dispatched with a bounded degree of parallelism. Completion
            // order across batches is not preserved — acceptable for ClickHouse, which does not
            // guarantee row order within a table regardless of insertion order.
            var semaphore = new SemaphoreSlim(_options.MaxDegreeOfParallelism);
            var tasks = new List<Task>();
            var rowsLock = new object();

            await foreach (var batch in ChunkAsync(entities, _options.BatchSize, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localBatch = batch; // captured for the closure below

                await semaphore.WaitAsync(cancellationToken);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var batchSw = _options.EnableDiagnostics ? Stopwatch.StartNew() : null;

                        await client.InsertBinaryAsync(
                            tableName,
                            columns,
                            localBatch.Select(row => mapper.Bindings.Select(b => b.ValueGetter(row)).ToArray()),
                            cancellationToken: cancellationToken);

                        lock (rowsLock)
                        {
                            totalRows += localBatch.Count;
                            batchCount++;
                        }

                        if (_options.EnableDiagnostics)
                            Debug.WriteLine(
                                $"[BulkInsert||] Batch {batchCount}: {localBatch.Count:N0} rows " +
                                $"in {batchSw!.ElapsedMilliseconds}ms");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        sw.Stop();

        var result = new ClickHouseBulkInsertResult
        {
            RowsWritten = totalRows,
            BatchCount = batchCount,
            Elapsed = sw.Elapsed,
            TableName = tableName,
        };

        if (_options.EnableDiagnostics)
            Debug.WriteLine($"[BulkInsert] Completed: {result}");

        return result;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the destination table name: <see cref="ClickHouseBulkInsertOptions.TableName"/> when
    /// explicitly set, otherwise <typeparamref name="TEntity"/>'s <c>[Map]</c> attribute, otherwise
    /// the CLR class name unchanged.
    /// </summary>
    /// <returns>The table name to insert into.</returns>
    private string ResolveTableName()
    {
        if (_options.TableName is not null)
            return _options.TableName;

        var mapAttr = typeof(TEntity)
            .GetCustomAttributes(typeof(MapAttribute), inherit: true)
            .OfType<MapAttribute>()
            .FirstOrDefault();

        return mapAttr?.Name ?? typeof(TEntity).Name;
    }

    /// <summary>
    /// Lazily builds and caches (for the lifetime of this inserter instance) the
    /// <see cref="EntityFieldMapper{TEntity}"/> used to project each entity into a row of values.
    /// </summary>
    /// <remarks>
    /// When <see cref="ClickHouseBulkInsertOptions.UseRepoDbFieldMapping"/> is enabled, this opens a
    /// short-lived connection to read <paramref name="tableName"/>'s real column types from
    /// <c>system.columns</c> via the registered <see cref="DbHelpers.ClickHouseDbHelper"/>, so that
    /// values are serialized using ClickHouse's actual column types rather than the CLR property
    /// types. If that introspection fails for any reason — most commonly because the destination
    /// table does not exist yet — the mapper falls back silently to reflection-only resolution rather
    /// than failing the whole insert.
    /// </remarks>
    /// <param name="tableName">The destination table to introspect.</param>
    /// <param name="cancellationToken">Token observed while querying <c>system.columns</c>.</param>
    /// <returns>The mapper to use for this and all subsequent inserts on this instance.</returns>
    private async Task<EntityFieldMapper<TEntity>> GetOrBuildMapperAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        if (_mapper is not null)
            return _mapper;

        IEnumerable<DbField>? dbFields = null;

        if (_options.UseRepoDbFieldMapping)
        {
            try
            {
                await using var conn = new RepoDbClickHouseConnection(_connectionString);
                var helper = DbHelperMapper.Get<RepoDbClickHouseConnection>();
                if (helper is not null)
                    dbFields = await helper.GetFieldsAsync(conn, tableName, null!, cancellationToken);
            }
            catch
            {
                // Schema introspection can legitimately fail (e.g. the table has not been created
                // yet); fall back to reflection-only mapping rather than aborting the insert.
            }
        }

        _mapper = new EntityFieldMapper<TEntity>(dbFields);
        return _mapper;
    }

    /// <summary>
    /// Builds the native <see cref="ClickHouseClient"/> used for RowBinary inserts.
    /// </summary>
    /// <remarks>
    /// <see cref="ClickHouseClient"/> accepts the same connection string format as the ADO.NET driver,
    /// so no translation is needed between the two.
    /// </remarks>
    /// <returns>A new client bound to this inserter's connection string.</returns>
    private ClickHouseClient BuildClient()
        => new(_connectionString);

    /// <summary>
    /// Splits an <see cref="IAsyncEnumerable{T}"/> into fixed-size batches without materializing the
    /// whole source sequence at once.
    /// </summary>
    /// <param name="source">The sequence to chunk.</param>
    /// <param name="chunkSize">The maximum number of items per yielded batch.</param>
    /// <param name="cancellationToken">
    /// Token observed while draining <paramref name="source"/>; annotated with
    /// <see cref="System.Runtime.CompilerServices.EnumeratorCancellationAttribute"/> so it is honored
    /// automatically when this method is consumed via <c>await foreach</c> with a token supplied
    /// through <see cref="AsyncEnumerable.WithCancellation{T}"/> upstream.
    /// </param>
    /// <returns>
    /// An asynchronous sequence of lists, each containing up to <paramref name="chunkSize"/> items;
    /// the final list may be smaller when <paramref name="source"/>'s length is not an exact multiple
    /// of <paramref name="chunkSize"/>.
    /// </returns>
    private static async IAsyncEnumerable<List<TEntity>> ChunkAsync(
        IAsyncEnumerable<TEntity> source,
        int chunkSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var buffer = new List<TEntity>(chunkSize);

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            buffer.Add(item);
            if (buffer.Count >= chunkSize)
            {
                yield return buffer;
                buffer = new List<TEntity>(chunkSize);
            }
        }

        if (buffer.Count > 0)
            yield return buffer;
    }
}

// ─── IEnumerable → IAsyncEnumerable adapter for internal reuse ───────────────

/// <summary>
/// Internal helper extension used only by <see cref="ClickHouseBulkInserter{TEntity}"/> to let the
/// synchronous <see cref="IEnumerable{T}"/> overload of <c>InsertAsync</c> reuse the same streaming
/// implementation as the <see cref="IAsyncEnumerable{T}"/> overload.
/// </summary>
file static class EnumerableExtensions
{
    /// <summary>
    /// Wraps a synchronous <see cref="IEnumerable{T}"/> as an <see cref="IAsyncEnumerable{T}"/> that
    /// yields the same items without introducing any actual asynchrony.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The sequence to wrap.</param>
    /// <returns>An asynchronous view over <paramref name="source"/>.</returns>
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return item;

        await Task.CompletedTask; // suppresses CS1998 ("no await" warning) for this intentionally synchronous iterator
    }
}
