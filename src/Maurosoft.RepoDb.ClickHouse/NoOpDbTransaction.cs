using System.Data;
using System.Data.Common;

namespace RepoDb.ClickHouse;

/// <summary>
/// A <see cref="DbTransaction"/> whose <see cref="Commit"/>, <see cref="Rollback"/>, and disposal are
/// all no-ops, used as ClickHouse's stand-in for ADO.NET transactional semantics it does not support.
/// </summary>
/// <remarks>
/// <para>
/// ClickHouse has no ACID transactions — every statement is implicitly auto-committed by the engine —
/// yet RepoDb unconditionally opens an implicit transaction around its batch operations
/// (<c>InsertAllAsync</c>, <c>DeleteAllAsync</c>, <c>UpdateAllAsync</c>, etc.) whenever the caller does
/// not supply one explicitly.
/// </para>
/// <para>
/// The underlying driver's <c>ClickHouseConnection.BeginTransaction()</c> throws
/// <see cref="NotSupportedException"/>. Left unhandled, that exception is masked by a
/// <see cref="NullReferenceException"/> inside RepoDb's own <see langword="catch"/> block, which calls
/// <c>transaction.Rollback()</c> on a variable that was never assigned because the throwing call
/// happened before the assignment completed — surfacing a confusing, unrelated exception instead of
/// the real cause. Returning this no-op transaction from
/// <see cref="RepoDbClickHouseConnection"/>'s <c>BeginDbTransaction</c> override avoids that failure
/// mode entirely while still accurately reflecting ClickHouse's actual (transaction-less) behavior.
/// </para>
/// </remarks>
/// <param name="connection">The connection this transaction is reported as belonging to.</param>
/// <param name="isolationLevel">The isolation level to report via <see cref="IsolationLevel"/>; not enforced in any way, since no real transaction is opened.</param>
internal sealed class NoOpDbTransaction(DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    private readonly DbConnection _connection = connection;

    /// <summary>The isolation level this transaction was created with. Purely informational; ClickHouse enforces no isolation semantics.</summary>
    public override IsolationLevel IsolationLevel { get; } = isolationLevel;

    /// <inheritdoc />
    protected override DbConnection DbConnection => _connection;

    /// <summary>No-op: ClickHouse auto-commits every statement, so there is nothing to commit.</summary>
    public override void Commit() { }

    /// <summary>No-op: ClickHouse has no transaction to roll back.</summary>
    public override void Rollback() { }

    /// <summary>No-op: there is no underlying transactional resource to release.</summary>
    /// <param name="disposing">Unused.</param>
    protected override void Dispose(bool disposing) { }
}
