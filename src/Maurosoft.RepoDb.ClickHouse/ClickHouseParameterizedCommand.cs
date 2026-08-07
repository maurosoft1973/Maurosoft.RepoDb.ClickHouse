using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace RepoDb.ClickHouse;

/// <summary>
/// A <see cref="ClickHouseCommand"/> that rewrites ADO.NET-style <c>@name</c> parameter placeholders
/// — the syntax RepoDb always generates, driven by <see cref="DbSettings.ClickHouseDbSetting.ParameterPrefix"/> —
/// into ClickHouse.Driver's native <c>{name:Type}</c> substitution syntax immediately before execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> ClickHouse.Driver 1.3+ does not recognize <c>@name</c> tokens in SQL text at
/// all — a command containing one fails server-side with a syntax error — regardless of whether a
/// matching <see cref="IDbDataParameter"/> was added to <see cref="DbCommand.Parameters"/>. It only
/// understands its own <c>{name:Type}</c> substitution syntax, where <c>Type</c> is a ClickHouse type
/// name inferred from the parameter's <see cref="ClickHouseDbParameter.Value"/>. RepoDb, however, has
/// no notion of this syntax: every statement builder in the framework (including
/// <see cref="StatementBuilders.ClickHouseStatementBuilder"/>, which only customizes SQL shape, not
/// parameter syntax) emits <c>@name</c> unconditionally. This type is the seam where that mismatch is
/// resolved, so the rest of the library — and application code — never has to deal with it.
/// </para>
/// <para>
/// Every ADO.NET execution entry point (synchronous and asynchronous, reader/scalar/non-query) is
/// overridden to call <see cref="RewriteParameterSyntax"/> first, so the rewrite is applied no matter
/// which path RepoDb (or user code) takes to run the command.
/// </para>
/// </remarks>
/// <param name="connection">The owning connection; forwarded unchanged to the base constructor.</param>
internal sealed partial class ClickHouseParameterizedCommand(ClickHouseConnection connection) : ClickHouseCommand(connection)
{
    /// <inheritdoc cref="DbCommand.ExecuteNonQuery"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    public override int ExecuteNonQuery()
    {
        RewriteParameterSyntax();
        return base.ExecuteNonQuery();
    }

    /// <inheritdoc cref="DbCommand.ExecuteScalar"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    public override object? ExecuteScalar()
    {
        RewriteParameterSyntax();
        return base.ExecuteScalar();
    }

    /// <inheritdoc cref="DbCommand.ExecuteDbDataReader(CommandBehavior)"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        RewriteParameterSyntax();
        return base.ExecuteDbDataReader(behavior);
    }

    /// <inheritdoc cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        RewriteParameterSyntax();
        return base.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc cref="DbCommand.ExecuteScalarAsync(CancellationToken)"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        RewriteParameterSyntax();
        return base.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc cref="DbCommand.ExecuteDbDataReaderAsync(CommandBehavior, CancellationToken)"/>
    /// <remarks>Rewrites <c>@name</c> parameter syntax before delegating to the base implementation.</remarks>
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        RewriteParameterSyntax();
        return base.ExecuteDbDataReaderAsync(behavior, cancellationToken);
    }

    /// <summary>
    /// Renames every parameter whose name starts with <c>@</c> — stripping the prefix so its
    /// <see cref="ClickHouseDbParameter.QueryForm"/> matches what gets written into the command text —
    /// and replaces the corresponding <c>@name</c> occurrence in <see cref="DbCommand.CommandText"/>
    /// with that form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parameter renaming always runs, even when the command text no longer contains any <c>@</c>
    /// character. This is required because RepoDb reuses a single <see cref="IDbCommand"/> instance
    /// across multiple executions inside a batch loop — for example <c>InsertAllAsync</c>, which (since
    /// <see cref="DbSettings.ClickHouseDbSetting.IsMultiStatementExecutable"/> is <see langword="false"/>)
    /// executes one row at a time against the same command object. On the first iteration the command
    /// text is rewritten from <c>@name</c> to <c>{name:Type}</c> and stays that way; on every later
    /// iteration RepoDb creates a fresh set of <c>@name</c>-named parameters (with new values) while the
    /// command text is left untouched. Skipping the rename step whenever the text has no <c>@</c> left
    /// would leave those later parameters mismatched against the already-rewritten placeholders,
    /// producing a "substitution is not set" error from the server. Renaming unconditionally keeps
    /// every iteration's parameters aligned with the (stable) rewritten text.
    /// </para>
    /// <para>
    /// The text substitution itself is still skipped when <see cref="DbCommand.CommandText"/> contains
    /// no <c>@</c> character, since there is nothing left to rewrite there — only the parameter-name
    /// stripping needs to happen unconditionally.
    /// </para>
    /// </remarks>
    private void RewriteParameterSyntax()
    {
        if (Parameters.Count == 0)
            return;

        Dictionary<string, string>? replacements = null;

        foreach (ClickHouseDbParameter parameter in Parameters)
        {
            var originalName = parameter.ParameterName;
            if (string.IsNullOrEmpty(originalName) || originalName[0] != '@')
                continue;

            parameter.ParameterName = originalName[1..];
            (replacements ??= new Dictionary<string, string>(StringComparer.Ordinal))[originalName] = parameter.QueryForm;
        }

        if (replacements is null)
            return;

        if (CommandText is { Length: > 0 } sql && sql.Contains('@'))
        {
            CommandText = ParameterTokenPattern().Replace(sql, match =>
                replacements.TryGetValue(match.Value, out var form) ? form : match.Value);
        }
    }

    /// <summary>
    /// Source-generated regex matching an ADO.NET-style parameter token (an <c>@</c> followed by one
    /// or more word characters) anywhere in a SQL string.
    /// </summary>
    /// <returns>The compiled, source-generated <see cref="Regex"/> instance.</returns>
    [GeneratedRegex(@"@\w+", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterTokenPattern();
}
