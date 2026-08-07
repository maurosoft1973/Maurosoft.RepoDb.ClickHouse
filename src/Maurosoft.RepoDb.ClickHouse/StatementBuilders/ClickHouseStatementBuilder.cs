using RepoDb.Exceptions;
using RepoDb.Extensions;
using RepoDb.Interfaces;
using RepoDb.StatementBuilders;

namespace RepoDb.ClickHouse.StatementBuilders;

/// <summary>
/// Generates ClickHouse-compatible SQL for every RepoDb operation.
/// </summary>
/// <remarks>
/// <para>Key differences from standard SQL that this builder accounts for:</para>
/// <list type="bullet">
///   <item>UPDATE and DELETE are asynchronous "mutations" (<c>ALTER TABLE ... UPDATE/DELETE</c>) — see <see cref="CreateUpdate"/>.</item>
///   <item>ClickHouse has no ACID transactions (handled at the connection level; see <see cref="RepoDbClickHouseConnection"/>).</item>
///   <item>Correlated subqueries are not supported in every context SQL-standard databases allow.</item>
///   <item>There is no <c>MERGE</c> statement: upserts fall back to <c>INSERT</c> against a <c>ReplacingMergeTree</c>/<c>CollapsingMergeTree</c> table — see <see cref="CreateMerge"/>.</item>
///   <item>Pagination uses <c>LIMIT n [OFFSET m]</c> rather than <c>TOP</c>.</item>
///   <item>There is no server-generated <c>IDENTITY</c> column type; typical primary keys are <c>UInt64</c> or <c>UUID</c> with a <c>DEFAULT</c> expression.</item>
/// </list>
/// <para>
/// As of RepoDb 1.15.1, <c>IStatementBuilder</c> no longer takes a <c>QueryBuilder</c> parameter: each
/// method builds its own SQL text internally from just <c>tableName</c>/<c>fields</c>/<c>where</c>/etc.
/// Most methods here call the corresponding <see cref="BaseStatementBuilder"/> implementation first
/// (which already applies backtick quoting per <see cref="IDbSetting"/>) and then adapt the result to
/// ClickHouse-specific syntax, rather than generating SQL from scratch.
/// </para>
/// </remarks>
/// <param name="dbSetting">The ClickHouse dialect configuration (quoting, parameter prefix, etc.) — see <see cref="DbSettings.ClickHouseDbSetting"/>.</param>
/// <param name="convertFieldResolver">Optional custom field-to-SQL-fragment resolver, forwarded unchanged to <see cref="BaseStatementBuilder"/>.</param>
/// <param name="averageableClientTypeResolver">Optional custom averageable-type resolver, forwarded unchanged to <see cref="BaseStatementBuilder"/>.</param>
internal sealed class ClickHouseStatementBuilder(IDbSetting dbSetting,
                                  IResolver<Field, IDbSetting, string>? convertFieldResolver = null,
                                  IResolver<Type, Type>? averageableClientTypeResolver = null)
    : BaseStatementBuilder(dbSetting, convertFieldResolver!, averageableClientTypeResolver!)
{
    // ─── SELECT: ClickHouse uses LIMIT instead of TOP ────────────────────────────

    /// <summary>
    /// Builds a <c>SELECT</c> statement, using <c>LIMIT n</c> instead of the SQL Server-style
    /// <c>TOP (n)</c> the base implementation would otherwise emit.
    /// </summary>
    /// <remarks>
    /// RepoDb passes <paramref name="top"/> as <c>0</c> (not <see langword="null"/>) to mean "no
    /// limit" whenever the caller does not specify one; both <see langword="null"/> and <c>0</c> are
    /// therefore treated as "no <c>LIMIT</c> clause" here to avoid emitting an unconditional
    /// <c>LIMIT 0</c> (which would return zero rows on every unfiltered query).
    /// </remarks>
    /// <param name="tableName">The table to select from.</param>
    /// <param name="fields">The columns to select.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="orderBy">Optional ordering.</param>
    /// <param name="top">Maximum number of rows to return; <see langword="null"/> or <c>0</c> means unlimited.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT ... [WHERE ...] [ORDER BY ...] [LIMIT n]</c> statement.</returns>
    public override string CreateQuery(string tableName, IEnumerable<Field> fields, QueryGroup? where = null, IEnumerable<OrderField>? orderBy = null, int? top = null, string? hints = null)
    {
        var sql = base.CreateQuery(tableName, fields, where!, orderBy!, null, hints!);
        return top is null or 0 ? sql : AppendClause(sql, $"LIMIT {top.Value}");
    }

    /// <summary>
    /// Builds a paginated <c>SELECT</c> statement for a given page number and page size, using
    /// <c>LIMIT n OFFSET m</c>.
    /// </summary>
    /// <param name="tableName">The table to select from.</param>
    /// <param name="fields">The columns to select.</param>
    /// <param name="page">The zero-based page number.</param>
    /// <param name="rowsPerBatch">The number of rows per page.</param>
    /// <param name="orderBy">Optional ordering; should generally be provided so pagination is stable across calls.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated statement, offset by <c>page * rowsPerBatch</c> rows and limited to <paramref name="rowsPerBatch"/>.</returns>
    public override string CreateBatchQuery(string tableName, IEnumerable<Field> fields, int page, int rowsPerBatch, IEnumerable<OrderField>? orderBy = null, QueryGroup? where = null, string? hints = null)
    {
        var sql = base.CreateQuery(tableName, fields, where!, orderBy!, null, hints!);
        return AppendClause(sql, $"LIMIT {rowsPerBatch} OFFSET {page * rowsPerBatch}");
    }

    /// <summary>
    /// Builds a <c>SELECT</c> statement that skips a fixed number of rows and takes a fixed number
    /// after that, using <c>LIMIT n OFFSET m</c>.
    /// </summary>
    /// <param name="tableName">The table to select from.</param>
    /// <param name="fields">The columns to select.</param>
    /// <param name="skip">The number of leading rows to skip.</param>
    /// <param name="take">The number of rows to return after skipping.</param>
    /// <param name="orderBy">Optional ordering; should generally be provided so the skip/take window is stable across calls.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated statement, offset by <paramref name="skip"/> rows and limited to <paramref name="take"/>.</returns>
    public override string CreateSkipQuery(string tableName, IEnumerable<Field> fields, int skip, int take, IEnumerable<OrderField>? orderBy = null, QueryGroup? where = null, string? hints = null)
    {
        var sql = base.CreateQuery(tableName, fields, where!, orderBy!, null, hints!);
        return AppendClause(sql, $"LIMIT {take} OFFSET {skip}");
    }

    // ─── DELETE ALL: lightweight DELETE with a tautological WHERE ─────────────

    /// <summary>
    /// Builds a lightweight <c>DELETE</c> statement that removes every row from a table.
    /// </summary>
    /// <remarks>
    /// ClickHouse's lightweight <c>DELETE FROM table</c> syntax (available since 22.8) always requires
    /// an explicit <c>WHERE</c> clause — even to delete unconditionally — unlike standard SQL, where a
    /// bare <c>DELETE FROM table</c> is valid. This appends a tautological <c>WHERE 1 = 1</c> to
    /// satisfy that requirement while still deleting every row. The base implementation instead emits
    /// an <c>ALTER TABLE ... DELETE</c> mutation with no <c>WHERE</c>, which ClickHouse rejects
    /// outright with a syntax error demanding <c>ON</c>/<c>IN PARTITION</c>/<c>WHERE</c>.
    /// </remarks>
    /// <param name="tableName">The table to delete every row from.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>DELETE FROM table WHERE 1 = 1</c> statement.</returns>
    public override string CreateDeleteAll(string tableName, string? hints = null)
        => AppendClause(CreateDelete(tableName, null!, hints!), "WHERE 1 = 1");

    // ─── UPDATE: ALTER TABLE ... UPDATE mutation (ClickHouse has no classic UPDATE) ──

    /// <summary>
    /// Builds an <c>ALTER TABLE ... UPDATE</c> mutation — ClickHouse's equivalent of a standard SQL
    /// <c>UPDATE</c> statement.
    /// </summary>
    /// <remarks>
    /// Mutations run asynchronously on the server: the affected rows are not guaranteed to be visible
    /// to subsequent reads immediately after this statement completes (see
    /// <c>system.mutations</c> to poll for completion, as done in the integration test suite's
    /// <c>WaitForMutationAsync</c> helper). Delegates field selection, primary-key exclusion from the
    /// <c>SET</c> list, and empty-field-list validation (throwing <see cref="EmptyException"/> when
    /// every field is the primary key) to <see cref="BaseStatementBuilder.CreateUpdate"/>, then
    /// rewrites the resulting <c>UPDATE table SET ...</c> text into the <c>ALTER TABLE table UPDATE ...</c>
    /// mutation form via <see cref="ConvertToUpdateMutation"/>.
    /// </remarks>
    /// <param name="tableName">The table to update.</param>
    /// <param name="fields">The fields to include in the <c>SET</c> clause; the primary key, if present in this list, is excluded automatically by the base implementation.</param>
    /// <param name="where">Optional filter restricting which rows are updated.</param>
    /// <param name="primaryField">The table's primary key field, used to exclude it from the generated <c>SET</c> clause.</param>
    /// <param name="identityField">Unused by ClickHouse (no server-generated identities), forwarded for interface compatibility.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>ALTER TABLE table UPDATE col = @col, ... [WHERE ...]</c> mutation.</returns>
    /// <exception cref="EmptyException">Every field in <paramref name="fields"/> is the primary key, leaving nothing to update.</exception>
    public override string CreateUpdate(string tableName, IEnumerable<Field> fields, QueryGroup? where = null, DbField? primaryField = null, DbField? identityField = null, string? hints = null)
    {
        var sql = base.CreateUpdate(tableName, fields, where!, primaryField!, identityField!, hints!);
        return ConvertToUpdateMutation(sql);
    }

    // ─── MERGE: ClickHouse has no MERGE; falls back to INSERT (ReplacingMergeTree/CollapsingMergeTree) ──

    /// <summary>
    /// Builds an <c>INSERT</c> statement in place of a <c>MERGE</c>, since ClickHouse has no
    /// <c>MERGE</c> statement at all.
    /// </summary>
    /// <remarks>
    /// This only produces correct upsert semantics against a <c>ReplacingMergeTree</c> table (where
    /// the most recently inserted row for a given sort key eventually wins on merge) or a
    /// <c>CollapsingMergeTree</c> table (where sign-column rows are used for logical deletion). Using
    /// it against a plain <c>MergeTree</c> table simply inserts a duplicate row rather than updating an
    /// existing one — ClickHouse has no way to perform a true conditional upsert.
    /// </remarks>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="fields">The fields to insert.</param>
    /// <param name="qualifiers">Ignored: ClickHouse has no <c>MERGE</c> matching condition to key on.</param>
    /// <param name="primaryField">Forwarded to <see cref="CreateInsert"/>.</param>
    /// <param name="identityField">Forwarded to <see cref="CreateInsert"/>; unused by ClickHouse (no server-generated identities).</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>INSERT INTO table (...) VALUES (...)</c> statement.</returns>
    public override string CreateMerge(string tableName, IEnumerable<Field> fields, IEnumerable<Field>? qualifiers = null, DbField? primaryField = null, DbField? identityField = null, string? hints = null)
        => CreateInsert(tableName, fields, primaryField!, identityField!, hints!);

    /// <summary>
    /// Builds a batched <c>INSERT</c> statement in place of a batched <c>MERGE</c>, delegating to
    /// <see cref="CreateInsertAll"/> for the same reasons documented on <see cref="CreateMerge"/>.
    /// </summary>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="fields">The fields to insert for each row.</param>
    /// <param name="qualifiers">Ignored: ClickHouse has no <c>MERGE</c> matching condition to key on.</param>
    /// <param name="batchSize">The number of value tuples to generate — see <see cref="CreateInsertAll"/>.</param>
    /// <param name="primaryField">Forwarded to <see cref="CreateInsertAll"/>.</param>
    /// <param name="identityField">Forwarded to <see cref="CreateInsertAll"/>; unused by ClickHouse (no server-generated identities).</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated single-statement, multi-row <c>INSERT</c>.</returns>
    public override string CreateMergeAll(string tableName, IEnumerable<Field> fields, IEnumerable<Field>? qualifiers = null, int batchSize = 10, DbField? primaryField = null, DbField? identityField = null, string? hints = null)
        => CreateInsertAll(tableName, fields, batchSize, primaryField, identityField, hints);

    // ─── INSERT ALL: a single INSERT with multiple VALUES tuples ────────────────

    /// <summary>
    /// Builds a single <c>INSERT</c> statement containing one <c>VALUES</c> tuple per row in the
    /// batch, using suffixed parameter names (<c>@field_0</c>, <c>@field_1</c>, …) to disambiguate
    /// each row's parameters.
    /// </summary>
    /// <remarks>
    /// <see cref="DbSettings.ClickHouseDbSetting.IsMultiStatementExecutable"/> is <see langword="false"/>
    /// (ClickHouse cannot execute multiple <c>;</c>-separated statements within a single command), so
    /// <see cref="BaseStatementBuilder"/>'s default implementation — which concatenates several
    /// complete <c>INSERT</c> statements into one command — cannot be reused here; RepoDb would refuse
    /// to build it and fall back to a batch size of 1. A single statement with multiple <c>VALUES</c>
    /// tuples is genuinely one statement, so it is unaffected by that restriction and is also the
    /// idiomatic way to batch an ADO.NET insert against ClickHouse.
    /// </remarks>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="fields">The fields to insert for each row. Must be non-empty.</param>
    /// <param name="batchSize">The number of value tuples to generate; values below 1 are treated as 1.</param>
    /// <param name="primaryField">Unused: unlike <see cref="CreateUpdate"/>, the primary key is included in the <c>INSERT</c> column list like any other field.</param>
    /// <param name="identityField">Unused by ClickHouse (no server-generated identities), forwarded for interface compatibility.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>INSERT INTO table (...) VALUES (@f_0, ...), (@f_1, ...), ...</c> statement.</returns>
    /// <exception cref="EmptyException"><paramref name="fields"/> is <see langword="null"/> or empty.</exception>
    public override string CreateInsertAll(string tableName, IEnumerable<Field>? fields = null, int batchSize = 1, DbField? primaryField = null, DbField? identityField = null, string? hints = null)
    {
        GuardTableName(tableName);
        GuardHints(hints!);

        var fieldList = fields?.ToList();
        if (fieldList is null || fieldList.Count == 0)
            throw new EmptyException("The list of insertable fields cannot be null or empty.");

        var quotedTable = tableName.AsQuoted(DbSetting);
        var columns = string.Join(", ", fieldList.Select(f => f.Name.AsQuoted(DbSetting)));

        var rows = Enumerable.Range(0, Math.Max(batchSize, 1))
            .Select(i => "(" + string.Join(", ", fieldList.Select(f => $"{DbSetting.ParameterPrefix}{f.Name}_{i}")) + ")");

        return $"INSERT INTO {quotedTable} ( {columns} ) VALUES {string.Join(", ", rows)} ;";
    }

    // ─── Aggregate functions: FUNC(col) syntax with no space, unquoted alias ──

    /// <summary>
    /// Builds a <c>SELECT COUNT(*)</c> statement.
    /// </summary>
    /// <remarks>
    /// The base implementation emits <c>COUNT (*)</c> (with a space before the parenthesis) and a
    /// backtick-quoted alias; ClickHouse's SQL parser is fine with either form, but this keeps the
    /// output format consistent with the other aggregate builders below and closer to idiomatic
    /// ClickHouse SQL.
    /// </remarks>
    /// <param name="tableName">The table to count rows in.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT COUNT(*) AS CountValue FROM table [WHERE ...]</c> statement.</returns>
    public override string CreateCount(string tableName, QueryGroup? where = null, string? hints = null)
        => CreateScalar(tableName, "COUNT(*)", where, "CountValue", hints);

    /// <summary>Builds a <c>SELECT SUM(field)</c> statement. See the remarks on <see cref="CreateCount"/> regarding the output format.</summary>
    /// <param name="tableName">The table to aggregate.</param>
    /// <param name="field">The field to sum.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT SUM(field) AS SumValue FROM table [WHERE ...]</c> statement.</returns>
    public override string CreateSum(string tableName, Field field, QueryGroup? where = null, string? hints = null)
        => CreateScalar(tableName, $"SUM({field.Name.AsQuoted(DbSetting)})", where, "SumValue", hints);

    /// <summary>Builds a <c>SELECT AVG(field)</c> statement. See the remarks on <see cref="CreateCount"/> regarding the output format.</summary>
    /// <param name="tableName">The table to aggregate.</param>
    /// <param name="field">The field to average.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT AVG(field) AS AverageValue FROM table [WHERE ...]</c> statement.</returns>
    public override string CreateAverage(string tableName, Field field, QueryGroup? where = null, string? hints = null)
        => CreateScalar(tableName, $"AVG({field.Name.AsQuoted(DbSetting)})", where, "AverageValue", hints);

    /// <summary>Builds a <c>SELECT MIN(field)</c> statement. See the remarks on <see cref="CreateCount"/> regarding the output format.</summary>
    /// <param name="tableName">The table to aggregate.</param>
    /// <param name="field">The field to find the minimum of.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT MIN(field) AS MinValue FROM table [WHERE ...]</c> statement.</returns>
    public override string CreateMin(string tableName, Field field, QueryGroup? where = null, string? hints = null)
        => CreateScalar(tableName, $"MIN({field.Name.AsQuoted(DbSetting)})", where, "MinValue", hints);

    /// <summary>Builds a <c>SELECT MAX(field)</c> statement. See the remarks on <see cref="CreateCount"/> regarding the output format.</summary>
    /// <param name="tableName">The table to aggregate.</param>
    /// <param name="field">The field to find the maximum of.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT MAX(field) AS MaxValue FROM table [WHERE ...]</c> statement.</returns>
    public override string CreateMax(string tableName, Field field, QueryGroup? where = null, string? hints = null)
        => CreateScalar(tableName, $"MAX({field.Name.AsQuoted(DbSetting)})", where, "MaxValue", hints);

    // ─── EXISTS: SELECT 1 ... LIMIT 1 instead of SELECT TOP (1) 1 ───────────────

    /// <summary>
    /// Builds a row-existence check statement.
    /// </summary>
    /// <remarks>
    /// The base implementation emits <c>SELECT TOP (1) 1 ...</c>, which is SQL Server syntax that
    /// ClickHouse does not understand; this emits <c>SELECT 1 ... LIMIT 1</c> instead, which is the
    /// ClickHouse-native equivalent.
    /// </remarks>
    /// <param name="tableName">The table to check.</param>
    /// <param name="where">Optional filter identifying the row(s) whose existence is being checked.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT 1 FROM table [WHERE ...] LIMIT 1</c> statement.</returns>
    public override string CreateExists(string tableName, QueryGroup? where = null, string? hints = null)
    {
        GuardTableName(tableName);
        GuardHints(hints!);

        var quotedTable = tableName.AsQuoted(DbSetting);
        var whereClause = where is not null ? $" WHERE {where.GetString(DbSetting)}" : "";
        return $"SELECT 1 FROM {quotedTable}{whereClause} LIMIT 1;";
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared implementation for the single-value aggregate builders (<see cref="CreateCount"/>,
    /// <see cref="CreateSum"/>, <see cref="CreateAverage"/>, <see cref="CreateMin"/>, <see cref="CreateMax"/>).
    /// </summary>
    /// <param name="tableName">The table to aggregate.</param>
    /// <param name="function">The already-formatted, already-quoted function call expression, e.g. <c>"SUM(`Duration`)"</c>.</param>
    /// <param name="where">Optional filter.</param>
    /// <param name="alias">The unquoted result column alias, e.g. <c>"SumValue"</c>.</param>
    /// <param name="hints">Unused table hints (ClickHouse has none), forwarded for interface compatibility.</param>
    /// <returns>The generated <c>SELECT function AS alias FROM table [WHERE ...]</c> statement.</returns>
    private string CreateScalar(string tableName, string function, QueryGroup? where, string alias, string? hints)
    {
        GuardTableName(tableName);
        GuardHints(hints!);

        var quotedTable = tableName.AsQuoted(DbSetting);
        var whereClause = where is not null ? $" WHERE {where.GetString(DbSetting)}" : "";
        return $"SELECT {function} AS {alias} FROM {quotedTable}{whereClause};";
    }

    /// <summary>
    /// Inserts a clause (e.g. <c>"LIMIT n OFFSET m"</c>) immediately before the trailing
    /// <c>" ;"</c> terminator that <see cref="BaseStatementBuilder"/> appends to its generated SQL.
    /// </summary>
    /// <param name="sql">The SQL text produced by a base-class builder method.</param>
    /// <param name="clause">The clause to append, without surrounding whitespace or a trailing semicolon.</param>
    /// <returns><paramref name="sql"/> with <paramref name="clause"/> inserted before its terminating semicolon (or appended, with a new one added, if <paramref name="sql"/> had none).</returns>
    private static string AppendClause(string sql, string clause)
    {
        var trimmed = sql.TrimEnd();
        return trimmed.EndsWith(';')
            ? $"{trimmed[..^1].TrimEnd()} {clause} ;"
            : $"{trimmed} {clause} ;";
    }

    /// <summary>
    /// Rewrites a standard <c>UPDATE table SET col = @col ...</c> statement (as produced by
    /// <see cref="BaseStatementBuilder.CreateUpdate"/>) into ClickHouse's <c>ALTER TABLE table UPDATE
    /// col = @col ...</c> mutation syntax.
    /// </summary>
    /// <param name="updateSql">The standard-syntax <c>UPDATE</c> statement to rewrite.</param>
    /// <returns>
    /// The rewritten <c>ALTER TABLE table UPDATE ...</c> statement, or <paramref name="updateSql"/>
    /// unchanged if it does not contain the expected <c>" SET "</c> marker (defensive fallback; should
    /// not occur given <see cref="BaseStatementBuilder"/>'s current output format).
    /// </returns>
    private static string ConvertToUpdateMutation(string updateSql)
    {
        const string setMarker = " SET ";
        var setIndex = updateSql.IndexOf(setMarker, StringComparison.Ordinal);
        if (setIndex < 0)
            return updateSql;

        var head = updateSql[..setIndex];                        // "UPDATE `table`"
        var tail = updateSql[(setIndex + setMarker.Length)..];    // "col = @col, ... [WHERE ...] ;"
        var alteredHead = "ALTER TABLE" + head["UPDATE".Length..];

        return $"{alteredHead} UPDATE {tail}";
    }
}
