using RepoDb.ClickHouse.DbHelpers;
using RepoDb.ClickHouse.DbSettings;
using RepoDb.ClickHouse.StatementBuilders;

namespace RepoDb.ClickHouse;

/// <summary>
/// One-time registration point that wires RepoDb's <see cref="DbSettingMapper"/>,
/// <see cref="DbHelperMapper"/>, and <see cref="StatementBuilderMapper"/> for
/// <see cref="RepoDbClickHouseConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// RepoDb resolves the correct <c>IDbSetting</c>/<c>IDbHelper</c>/<c>IStatementBuilder</c> for a call
/// based on the <em>compile-time</em> type of the connection variable used at the call site — not its
/// runtime type. Registration must therefore target <see cref="RepoDbClickHouseConnection"/>
/// specifically (not the underlying driver's <c>ClickHouseConnection</c>): calling RepoDb extension
/// methods on a variable declared as the driver's own connection type would fail to find a matching
/// registration.
/// </para>
/// <para>Typical usage — once, at application startup (e.g. in <c>Program.cs</c>):</para>
/// <code>
/// GlobalConfiguration.Setup().UseClickHouse();
/// </code>
/// </remarks>
public static class ClickHouseBootstrap
{
    private static readonly Lock _lock = new();  // .NET 9+ System.Threading.Lock

    /// <summary>
    /// <see langword="true"/> once <see cref="Initialize"/> has completed at least one successful
    /// registration pass; <see langword="false"/> beforehand.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Registers ClickHouse's <c>IDbSetting</c>, <c>IDbHelper</c>, and <c>IStatementBuilder</c> with
    /// RepoDb's global mappers, if not already done.
    /// </summary>
    /// <remarks>
    /// Idempotent and thread-safe: a fast-path unsynchronized check avoids taking the lock on the
    /// common case where initialization already happened, and a second check inside the lock avoids a
    /// duplicate (harmless, but wasteful) registration under a race between concurrent first callers.
    /// Safe to call from multiple constructors/entry points (see
    /// <see cref="ClickHouseRepository{TEntity}"/> and <see cref="GlobalConfigurationExtension.UseClickHouse"/>).
    /// </remarks>
    internal static void Initialize()
    {
        if (IsInitialized)
            return;

        lock (_lock)
        {
            if (IsInitialized)
                return;

            var dbSetting = new ClickHouseDbSetting();

            DbSettingMapper.Add<RepoDbClickHouseConnection>(dbSetting, true);
            DbHelperMapper.Add<RepoDbClickHouseConnection>(new ClickHouseDbHelper(), true);
            StatementBuilderMapper.Add<RepoDbClickHouseConnection>(
                new ClickHouseStatementBuilder(dbSetting), true);

            IsInitialized = true;
        }
    }
}

/// <summary>
/// Extension over <see cref="GlobalConfiguration"/> that registers ClickHouse support using RepoDb's
/// fluent setup syntax.
/// </summary>
public static class GlobalConfigurationExtension
{
    /// <summary>
    /// Registers ClickHouse's <c>IDbSetting</c>, <c>IDbHelper</c>, and <c>IStatementBuilder</c> with
    /// RepoDb. Equivalent to calling <see cref="ClickHouseBootstrap.Initialize"/> directly; provided
    /// as a fluent extension so it reads naturally alongside RepoDb's own <c>GlobalConfiguration.Setup()</c> API.
    /// </summary>
    /// <param name="configuration">The RepoDb global configuration builder.</param>
    /// <returns><paramref name="configuration"/>, to allow further fluent chaining.</returns>
    /// <example>
    /// <code>
    /// GlobalConfiguration.Setup().UseClickHouse();
    /// </code>
    /// </example>
    public static GlobalConfiguration UseClickHouse(this GlobalConfiguration configuration)
    {
        ClickHouseBootstrap.Initialize();
        return configuration;
    }
}
