using ClickHouse.Driver.ADO;
using System.Data;
using System.Data.Common;

namespace RepoDb.ClickHouse;

/// <summary>
/// A <see cref="ClickHouseConnection"/> subclass that adapts the driver to RepoDb's assumptions about
/// ADO.NET parameter syntax and transaction support.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parameter syntax.</b> Every command created through this connection is a
/// <see cref="ClickHouseParameterizedCommand"/>, which rewrites the ADO.NET-style <c>@name</c>
/// parameter placeholders RepoDb always generates into ClickHouse.Driver 1.3+'s native
/// <c>{name:Type}</c> substitution syntax immediately before execution — the driver does not
/// understand <c>@name</c> at all and fails such commands with a server-side syntax error.
/// </para>
/// <para>
/// <b>Transactions.</b> <see cref="BeginDbTransaction"/> is overridden to return a
/// <see cref="NoOpDbTransaction"/> instead of calling the base implementation, which throws
/// <see cref="NotSupportedException"/> (ClickHouse has no ACID transactions). Without this override,
/// RepoDb's implicit transaction handling around batch operations would surface a misleading
/// <see cref="NullReferenceException"/> instead — see <see cref="NoOpDbTransaction"/> for the full
/// explanation.
/// </para>
/// <para>
/// <b>Why this must be used explicitly.</b> RepoDb resolves the registered <c>IDbSetting</c>,
/// <c>IDbHelper</c>, and <c>IStatementBuilder</c> for a call based on the compile-time type of the
/// connection variable at the call site, not its runtime type — see <see cref="ClickHouseBootstrap"/>.
/// Always declare connection variables as <see cref="RepoDbClickHouseConnection"/> (not the driver's
/// own <see cref="ClickHouseConnection"/>) when calling RepoDb extension methods
/// (<c>InsertAsync</c>, <c>UpdateAsync</c>, <c>QueryAsync</c>, …). This is <em>not</em> required for
/// the native bulk-insert path (<see cref="Bulk.ClickHouseBulkInserter{TEntity}"/>, reached via
/// <see cref="Extensions.ClickHouseBulkInsertExtensions"/>), which uses the RowBinary binary protocol
/// and never emits parameterized SQL text in the first place.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var conn = new RepoDbClickHouseConnection(connectionString);
/// var active = await conn.QueryAsync&lt;MyEntity&gt;(e =&gt; e.Status == "active");
/// </code>
/// </example>
public sealed class RepoDbClickHouseConnection : ClickHouseConnection
{
    /// <summary>
    /// Creates an unconfigured connection. <see cref="DbConnection.ConnectionString"/> must be set
    /// before <see cref="DbConnection.Open"/> is called.
    /// </summary>
    public RepoDbClickHouseConnection() { }

    /// <summary>
    /// Creates a connection configured with the given connection string.
    /// </summary>
    /// <param name="connectionString">The ClickHouse ADO.NET connection string.</param>
    public RepoDbClickHouseConnection(string connectionString) : base(connectionString) { }

    /// <inheritdoc cref="DbConnection.CreateDbCommand"/>
    /// <remarks>Returns a <see cref="ClickHouseParameterizedCommand"/> so every command issued through this connection gets its <c>@name</c> parameters rewritten before execution.</remarks>
    protected override DbCommand CreateDbCommand() => new ClickHouseParameterizedCommand(this);

    /// <inheritdoc cref="DbConnection.BeginDbTransaction"/>
    /// <remarks>
    /// Returns a <see cref="NoOpDbTransaction"/> instead of delegating to the base implementation,
    /// which throws <see cref="NotSupportedException"/> because ClickHouse has no transaction support.
    /// See <see cref="NoOpDbTransaction"/> for why this is necessary rather than merely convenient.
    /// </remarks>
    /// <param name="isolationLevel">Requested isolation level; recorded on the returned transaction but not enforced.</param>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new NoOpDbTransaction(this, isolationLevel);
}
