using RepoDb;
using RepoDb.DbSettings;

namespace RepoDb.ClickHouse.DbSettings;

/// <summary>
/// SQL dialect configuration for ClickHouse, consumed by RepoDb's statement builders and command
/// pipeline via the <see cref="IDbSetting"/> contract.
/// </summary>
/// <remarks>
/// Inherits <see cref="BaseDbSetting"/> instead of implementing <see cref="IDbSetting"/> directly.
/// <see cref="BaseDbSetting"/> supplies a <c>GetHashCode()</c> that combines every setting via
/// <c>HashCode.Combine()</c>; RepoDb relies on structural equality for its mapper lookups and
/// execution-context cache keys, and a type without a proper hash code is vulnerable to race
/// conditions and silently-failing lookups under concurrent access (this was the subject of upstream
/// PR #1153).
/// </remarks>
internal sealed class ClickHouseDbSetting : BaseDbSetting
{
    /// <summary>
    /// Initializes every dialect setting ClickHouse requires that differs from
    /// <see cref="BaseDbSetting"/>'s defaults.
    /// </summary>
    public ClickHouseDbSetting()
        : base()
    {
        // ClickHouse has no SQL Server-style table hints (e.g. WITH (NOLOCK)).
        AreTableHintsSupported = false;

        // .NET type produced by AVG(): ClickHouse returns Float64 for numeric averages.
        AverageableType = typeof(double);

        // ClickHouse quotes identifiers with backticks, not square brackets or double quotes.
        OpeningQuote = "`";
        ClosingQuote = "`";

        // ClickHouse has no SQL-standard notion of schema; "database.table" is the qualified form,
        // with '.' as the separator and no meaningful "default schema" concept to fall back to.
        DefaultSchema = null!;
        SchemaSeparator = ".";

        // ClickHouse has no ADO.NET ParameterDirection support (INPUT/OUTPUT/INOUT parameters).
        IsDirectionSupported = false;

        // ClickHouse.Driver's DbDataReader must be disposed explicitly by the caller.
        IsExecuteReaderDisposable = true;

        // ClickHouse cannot execute multiple ';'-separated statements within a single HTTP request/command.
        // This forces RepoDb's own batch loop (e.g. InsertAllAsync) down to one execution per row rather
        // than concatenating several full statements into one command. ClickHouseStatementBuilder
        // compensates for the batch INSERT case by overriding CreateInsertAll/CreateMergeAll to build a
        // single INSERT with multiple VALUES tuples instead — a genuinely single statement, so it is
        // unaffected by this restriction and remains fully batched.
        IsMultiStatementExecutable = false;

        // ClickHouse has no PREPARE/EXECUTE statement support.
        IsPreparable = false;

        // RepoDb's built-in "upsert" mode is not used; ClickHouse's upsert story is ReplacingMergeTree
        // (deduplicated on merge) or CollapsingMergeTree, both reached through MergeAsync's INSERT
        // fallback (see ClickHouseStatementBuilder.CreateMerge).
        IsUseUpsert = false;

        // RepoDb always generates "@name"-style ADO.NET parameter placeholders; ClickHouseParameterizedCommand
        // rewrites them into ClickHouse.Driver's native "{name:Type}" syntax before execution.
        ParameterPrefix = "@";
    }
}
