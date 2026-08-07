using ClickHouse.Driver.ADO;
using RepoDb.Interfaces;
using System.Data;

namespace RepoDb.ClickHouse.DbHelpers;

/// <summary>
/// RepoDb <see cref="IDbHelper"/> implementation for ClickHouse schema introspection.
/// </summary>
/// <remarks>
/// Reads column metadata from ClickHouse's <c>system.columns</c> table rather than the standard
/// <c>INFORMATION_SCHEMA</c> views, since ClickHouse's own metadata (in particular
/// <c>is_in_primary_key</c>, <c>default_kind</c>, and <c>default_expression</c>) is more directly
/// usable here than the generic SQL-standard views would be.
/// </remarks>
internal sealed class ClickHouseDbHelper : IDbHelper
{
    /// <summary>
    /// Creates a helper with the default <see cref="ClickHouseDbTypeNameToClientTypeResolver"/>.
    /// </summary>
    public ClickHouseDbHelper()
    {
        DbTypeResolver = new ClickHouseDbTypeNameToClientTypeResolver();
    }

    /// <summary>
    /// The resolver RepoDb uses to map a ClickHouse type name (as read from <c>system.columns</c>)
    /// to the corresponding .NET <see cref="Type"/>.
    /// </summary>
    public IResolver<string, Type> DbTypeResolver { get; }

    /// <summary>
    /// Synchronous entry point for <see cref="GetFieldsAsync"/>, required by <see cref="IDbHelper"/>.
    /// </summary>
    /// <remarks>
    /// Blocks on the asynchronous implementation via <c>GetAwaiter().GetResult()</c>. Prefer
    /// <see cref="GetFieldsAsync"/> directly wherever an asynchronous call site is available, to avoid
    /// the thread-pool starvation risk inherent to blocking on async work.
    /// </remarks>
    /// <param name="connection">The connection to query. Its <see cref="IDbConnection.ConnectionString"/> is used even when a separate connection ends up being opened (see <see cref="GetFieldsAsync"/>).</param>
    /// <param name="tableName">The table name, optionally schema-qualified as <c>database.table</c>.</param>
    /// <param name="transaction">Unused by this implementation; ClickHouse has no transactional DDL/metadata semantics to enlist in.</param>
    /// <returns>The resolved column metadata for <paramref name="tableName"/>.</returns>
    public IEnumerable<DbField> GetFields(IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null)
        => GetFieldsAsync(connection, tableName, transaction)
               .GetAwaiter().GetResult();

    /// <summary>
    /// Retrieves a table's column metadata by querying <c>system.columns</c>.
    /// </summary>
    /// <remarks>
    /// If <paramref name="connection"/> is not currently <see cref="ConnectionState.Closed"/> (i.e. it
    /// is already open and potentially in use by another in-flight command on the same connection
    /// object), a separate connection is opened instead of sharing the caller's one. Issuing a second
    /// command on a connection that is concurrently executing another one is unsafe for most ADO.NET
    /// providers, including ClickHouse.Driver's HTTP-based implementation; opening a short-lived
    /// sibling connection sidesteps that race entirely at the cost of one extra connection setup.
    /// </remarks>
    /// <param name="connection">The connection whose type and connection string are used to resolve the schema.</param>
    /// <param name="tableName">The table name, optionally schema-qualified as <c>database.table</c>.</param>
    /// <param name="transaction">Unused by this implementation; ClickHouse has no transactional DDL/metadata semantics to enlist in.</param>
    /// <param name="cancellationToken">Token observed while querying <c>system.columns</c>.</param>
    /// <returns>The resolved column metadata for <paramref name="tableName"/>, in <c>system.columns</c> <c>position</c> order.</returns>
    public async Task<IEnumerable<DbField>> GetFieldsAsync(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Closed)
        {
            await using var retryConn = CreateSameTypeConnection(connection.ConnectionString);
            return await GetFieldsCoreAsync(retryConn, tableName, openClose: true, cancellationToken);
        }

        return await GetFieldsCoreAsync(connection, tableName, openClose: true, cancellationToken);
    }

    /// <summary>
    /// Executes the actual <c>system.columns</c> query and materializes the result as
    /// <see cref="DbField"/> instances.
    /// </summary>
    /// <param name="connection">The connection to run the query on.</param>
    /// <param name="tableName">The table name, optionally schema-qualified as <c>database.table</c>.</param>
    /// <param name="openClose">
    /// When <see langword="true"/>, the connection is opened before the query and closed again in a
    /// <see langword="finally"/> block, regardless of outcome — appropriate for the short-lived
    /// connections this helper manages internally, but would be wrong for a caller-owned, already-open
    /// connection.
    /// </param>
    /// <param name="cancellationToken">Token observed while executing the query and reading results.</param>
    /// <returns>The resolved column metadata.</returns>
    private async Task<IEnumerable<DbField>> GetFieldsCoreAsync(
        IDbConnection connection,
        string tableName,
        bool openClose,
        CancellationToken cancellationToken)
    {
        var (database, table) = ParseTableName(tableName, connection);

        // ClickHouse.Driver uses ClickHouse's own native parameter substitution syntax
        // ({name:Type} embedded in the SQL text, with a ParameterName that carries no "@" prefix) —
        // not the "@name" placeholder style common to other ADO.NET providers. See
        // ClickHouseParameterizedCommand for how RepoDb-generated "@name" SQL is adapted to this.
        const string sql = """
            SELECT
                name,
                type,
                is_in_primary_key,
                default_kind,
                default_expression
            FROM system.columns
            WHERE database = {database:String}
              AND table    = {table:String}
            ORDER BY position
            """;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;

        AddParameter(cmd, "database", database);
        AddParameter(cmd, "table", table);

        if (openClose)
            connection.Open();

        try
        {
            var fields = new List<DbField>();

            // `connection.CreateCommand()` returns a plain IDbCommand, which has no async execution
            // members; the dynamic cast recovers ClickHouseCommand's ExecuteReaderAsync without a
            // compile-time dependency on that concrete type here.
            using var reader = await ((dynamic)cmd).ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(0);
                var typeName = reader.GetString(1);
                // is_in_primary_key is UInt8 on the ClickHouse side, which the driver surfaces as
                // System.Byte rather than Int32 — GetInt32 would throw an InvalidCastException here.
                var isPrimaryKey = Convert.ToInt32(reader.GetValue(2)) == 1;
                var defaultKind = reader.GetString(3);
                var defaultExpr = reader.GetString(4);

                var dotnetType = DbTypeResolver.Resolve(typeName);
                var isNullable = typeName.StartsWith("Nullable(", StringComparison.OrdinalIgnoreCase);
                var isIdentity = isPrimaryKey && defaultKind == "DEFAULT" &&
                    (defaultExpr.Contains("generateUUIDv4", StringComparison.OrdinalIgnoreCase) ||
                     defaultExpr.Contains("snowflakeIDToDateTime", StringComparison.OrdinalIgnoreCase));

                fields.Add(new DbField(
                    name: columnName,
                    isPrimary: isPrimaryKey,
                    isIdentity: isIdentity,
                    isNullable: isNullable,
                    type: dotnetType,
                    size: null,
                    precision: null,
                    scale: null,
                    databaseType: null
                    ));
            }

            return fields;
        }
        finally
        {
            if (openClose)
                connection.Close();
        }
    }

    /// <summary>
    /// Creates a new <see cref="RepoDbClickHouseConnection"/> for the given connection string.
    /// </summary>
    /// <remarks>
    /// The retry connection opened by <see cref="GetFieldsAsync"/> must itself be a
    /// <see cref="RepoDbClickHouseConnection"/> (not the bare driver connection) so that, should any
    /// future change to this helper start emitting <c>@name</c>-style parameters again, they would
    /// still be rewritten correctly. The current query already uses native <c>{name:Type}</c> syntax
    /// directly, so this is a consistency safeguard rather than a strict current-day requirement.
    /// </remarks>
    /// <param name="connectionString">The connection string to open the new connection with.</param>
    /// <returns>A new, unopened connection.</returns>
    private static RepoDbClickHouseConnection CreateSameTypeConnection(string connectionString)
        => new RepoDbClickHouseConnection(connectionString);

    /// <summary>
    /// Splits a possibly schema-qualified table name into its database and table components.
    /// </summary>
    /// <remarks>
    /// Backtick-quoted identifiers (e.g. <c>`my_table`</c>) are unquoted first. When
    /// <paramref name="tableName"/> has no <c>.</c> separator, the database is taken from the
    /// <c>Database</c> key of <paramref name="connection"/>'s connection string, falling back to
    /// ClickHouse's own default database name, <c>"default"</c>, when that key is absent.
    /// </remarks>
    /// <param name="tableName">The (optionally qualified, optionally backtick-quoted) table name.</param>
    /// <param name="connection">The connection whose connection string supplies the default database when <paramref name="tableName"/> is unqualified.</param>
    /// <returns>The resolved <c>(database, table)</c> pair.</returns>
    private static (string database, string table) ParseTableName(
        string tableName, IDbConnection connection)
    {
        tableName = tableName.Replace("`", "");

        if (tableName.Contains('.'))
        {
            var parts = tableName.Split('.', 2);
            return (parts[0], parts[1]);
        }

        var database = TryExtractDatabase(connection.ConnectionString) ?? "default";
        return (database, tableName);
    }

    /// <summary>
    /// Extracts the value of the <c>Database</c> key from a <c>;</c>-delimited, <c>key=value</c>-style
    /// ADO.NET connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    /// <returns>The database name, or <see langword="null"/> when no <c>Database</c> key is present.</returns>
    private static string? TryExtractDatabase(string connectionString)
    {
        foreach (var part in connectionString.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 &&
                kv[0].Trim().Equals("Database", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }

        return null;
    }

    /// <summary>
    /// Adds a named, value-bound parameter to an <see cref="IDbCommand"/> using the provider's own
    /// <see cref="IDbCommand.CreateParameter"/> factory.
    /// </summary>
    /// <param name="cmd">The command to add the parameter to.</param>
    /// <param name="name">The parameter name, without any provider-specific prefix (see the remarks on <see cref="GetFieldsCoreAsync"/> regarding ClickHouse's native parameter syntax).</param>
    /// <param name="value">The parameter's value.</param>
    private static void AddParameter(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Always returns <see langword="default"/>: ClickHouse has no server-generated <c>IDENTITY</c>-style
    /// auto-increment columns, so there is no scope identity value to retrieve after an insert.
    /// </summary>
    /// <typeparam name="T">The identity value's expected type.</typeparam>
    /// <param name="connection">Unused.</param>
    /// <param name="transaction">Unused.</param>
    /// <returns><see langword="default"/>(<typeparamref name="T"/>).</returns>
    public T GetScopeIdentity<T>(IDbConnection connection, IDbTransaction? transaction = null) => default!;

    /// <summary>
    /// Asynchronous counterpart of <see cref="GetScopeIdentity{T}"/>; always completes synchronously
    /// with <see langword="default"/>(<typeparamref name="T"/>) for the same reason.
    /// </summary>
    /// <typeparam name="T">The identity value's expected type.</typeparam>
    /// <param name="connection">Unused.</param>
    /// <param name="transaction">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task carrying <see langword="default"/>(<typeparamref name="T"/>).</returns>
    public Task<T> GetScopeIdentityAsync<T>(IDbConnection connection, IDbTransaction? transaction = null, CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);

    /// <summary>
    /// RepoDb's extensibility hook for provider-specific handling of dynamic entity events.
    /// Intentionally a no-op: ClickHouse requires no custom logic at this extension point.
    /// </summary>
    /// <typeparam name="TEventInstance">The event payload type.</typeparam>
    /// <param name="instance">Unused.</param>
    /// <param name="key">Unused.</param>
    public void DynamicHandler<TEventInstance>(TEventInstance instance, string key) { }
}

/// <summary>
/// Resolves a ClickHouse type name — as read from <c>system.columns.type</c> — to the corresponding
/// .NET <see cref="Type"/>.
/// </summary>
/// <remarks>
/// <c>Nullable(...)</c> and <c>LowCardinality(...)</c> wrappers are unwrapped before resolution, so
/// e.g. <c>LowCardinality(Nullable(String))</c> and <c>String</c> both resolve to
/// <see langword="string"/>. <c>Array(...)</c> columns resolve to the non-generic
/// <see cref="System.Array"/> type, since the element type cannot be reliably distinguished here from
/// the raw type name alone. Types with no direct .NET counterpart (unrecognized names) resolve to
/// <see langword="object"/> rather than throwing, so schema introspection degrades gracefully for
/// ClickHouse types this resolver does not yet know about.
/// </remarks>
internal sealed class ClickHouseDbTypeNameToClientTypeResolver : IResolver<string, Type>
{
    /// <summary>
    /// Resolves a single ClickHouse type name to its corresponding .NET <see cref="Type"/>.
    /// </summary>
    /// <param name="input">The raw type name as reported by <c>system.columns.type</c> (e.g. <c>"UInt64"</c>, <c>"Nullable(DateTime)"</c>, <c>"LowCardinality(String)"</c>).</param>
    /// <returns>The resolved .NET type, or <see langword="typeof(object)"/> when <paramref name="input"/> is empty/whitespace or not recognized.</returns>
    public Type Resolve(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return typeof(object);

        if (input.StartsWith("Nullable(") && input.EndsWith(')'))
            input = input[9..^1];

        if (input.StartsWith("LowCardinality(") && input.EndsWith(')'))
            input = input[15..^1];

        if (input.StartsWith("Array("))
            return typeof(Array);

        return input switch
        {
            "UInt8" => typeof(byte),
            "UInt16" => typeof(ushort),
            "UInt32" => typeof(uint),
            "UInt64" => typeof(ulong),
            "UInt128" => typeof(decimal),
            "UInt256" => typeof(decimal),
            "Int8" => typeof(sbyte),
            "Int16" => typeof(short),
            "Int32" => typeof(int),
            "Int64" => typeof(long),
            "Int128" => typeof(decimal),
            "Int256" => typeof(decimal),
            "Float32" => typeof(float),
            "Float64" => typeof(double),
            "Decimal" => typeof(decimal),
            "Decimal32" => typeof(decimal),
            "Decimal64" => typeof(decimal),
            "Decimal128" => typeof(decimal),
            "Decimal256" => typeof(decimal),
            "String" => typeof(string),
            "FixedString" => typeof(string),
            "UUID" => typeof(Guid),
            // DateOnly is fully supported by RepoDb core as of v1.14.0 (upstream PR #1184),
            // including LINQ predicates such as `e => e.Date == new DateOnly(2026, 1, 1)`.
            "Date" => typeof(DateOnly),
            "Date32" => typeof(DateOnly),
            "DateTime" => typeof(DateTime),
            "DateTime64" => typeof(DateTime),
            "Bool" => typeof(bool),
            "IPv4" => typeof(string),
            "IPv6" => typeof(string),
            _ => typeof(object)
        };
    }
}
