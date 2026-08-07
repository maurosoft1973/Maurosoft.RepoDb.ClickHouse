using RepoDb.ClickHouse.Bulk;
using RepoDb.Interfaces;

namespace RepoDb.ClickHouse;

/// <summary>
/// Typed repository base class for a single ClickHouse entity.
/// </summary>
/// <remarks>
/// Extends RepoDb's <see cref="BaseRepository{TEntity, TDbConnection}"/> using
/// <see cref="RepoDbClickHouseConnection"/> as the ADO.NET provider, so every inherited operation
/// (<c>InsertAsync</c>, <c>QueryAsync</c>, <c>MergeAsync</c>, …) automatically benefits from that
/// connection's <c>@name</c> → <c>{name:Type}</c> parameter rewriting and no-op transaction handling.
/// </remarks>
/// <example>
/// <code>
/// public sealed class TrunkProfileRepository : ClickHouseRepository&lt;TrunkProfile&gt;
/// {
///     public TrunkProfileRepository(string connectionString) : base(connectionString) { }
///
///     public Task&lt;IEnumerable&lt;TrunkProfile&gt;&gt; GetByDirectionAsync(string direction)
///         =&gt; QueryAsync(p =&gt; p.Direction == direction);
/// }
/// </code>
/// </example>
public abstract class ClickHouseRepository<TEntity> :
    BaseRepository<TEntity, RepoDbClickHouseConnection>
    where TEntity : class
{
    /// <summary>
    /// The connection string this repository was constructed with.
    /// </summary>
    /// <remarks>
    /// Exposed so that the native bulk-insert extensions (<see cref="Extensions.ClickHouseBulkInsertExtensions"/>)
    /// can build a <c>ClickHouseClient</c> against the same endpoint, without requiring callers to keep
    /// a separate copy of the connection string around.
    /// </remarks>
    public new string ConnectionString { get; }

    /// <summary>
    /// Creates a repository bound to the given connection string, using RepoDb's default command
    /// timeout and no result cache.
    /// </summary>
    /// <param name="connectionString">The ClickHouse ADO.NET connection string.</param>
    protected ClickHouseRepository(string connectionString)
        : base(connectionString)
    {
        ConnectionString = connectionString;
        ClickHouseBootstrap.Initialize();
    }

    /// <summary>
    /// Creates a repository bound to the given connection string with an explicit command timeout.
    /// </summary>
    /// <param name="connectionString">The ClickHouse ADO.NET connection string.</param>
    /// <param name="commandTimeout">Command timeout, in seconds, applied to every operation.</param>
    protected ClickHouseRepository(string connectionString, int commandTimeout)
        : base(connectionString, commandTimeout)
    {
        ConnectionString = connectionString;
        ClickHouseBootstrap.Initialize();
    }

    /// <summary>
    /// Creates a repository bound to the given connection string with an explicit command timeout and
    /// result cache.
    /// </summary>
    /// <param name="connectionString">The ClickHouse ADO.NET connection string.</param>
    /// <param name="cache">The RepoDb <see cref="ICache"/> implementation to use for cached queries.</param>
    /// <param name="commandTimeout">Command timeout, in seconds, applied to every operation. Default: 30.</param>
    /// <param name="cacheItemExpirationInMinutes">Expiration, in minutes, applied to cached entries. Default: 180.</param>
    protected ClickHouseRepository(string connectionString,
        ICache cache,
        int commandTimeout = 30,
        int cacheItemExpirationInMinutes = 180)
        : base(connectionString, commandTimeout: commandTimeout, cache: cache, cacheItemExpiration: cacheItemExpirationInMinutes)
    {
        ConnectionString = connectionString;
        ClickHouseBootstrap.Initialize();
    }
}

/// <summary>
/// Untyped, multi-entity repository base class for ClickHouse, mirroring RepoDb's
/// <see cref="DbRepository{TDbConnection}"/>.
/// </summary>
/// <remarks>
/// Prefer <see cref="ClickHouseRepository{TEntity}"/> for the common case of one repository per
/// entity; use this base class only when a single repository genuinely needs to operate across
/// multiple entity types.
/// </remarks>
public abstract class ClickHouseDbRepository : DbRepository<RepoDbClickHouseConnection>
{
    /// <summary>
    /// Creates a repository bound to the given connection string, using RepoDb's default command
    /// timeout and no result cache.
    /// </summary>
    /// <param name="connectionString">The ClickHouse ADO.NET connection string.</param>
    protected ClickHouseDbRepository(string connectionString)
        : base(connectionString)
    {
        ClickHouseBootstrap.Initialize();
    }
}
