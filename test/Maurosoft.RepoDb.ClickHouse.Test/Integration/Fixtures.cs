using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using RepoDb;
using RepoDb.Attributes;
using RepoDb.ClickHouse;
using Testcontainers.ClickHouse;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

// ─── Entità di test ──────────────────────────────────────────────────────────

/// <summary>
/// Trunk con ReplacingMergeTree — supporta upsert via INSERT.
/// </summary>
[Map("test_trunk_profiles")]
public class TestTrunkProfile
{
    [Primary]
    public string TrunkId { get; set; } = "";
    public string Direction { get; set; } = "";
    public double AvgCallDuration { get; set; }
    public long CallsPerHour { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// CDR event con MergeTree standard.
/// </summary>
[Map("test_cdr_events")]
public class TestCdrEvent
{
    [Primary]
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string TrunkId { get; set; } = "";
    public string CallingNumber { get; set; } = "";
    public string CalledNumber { get; set; } = "";
    public DateTime StartTime { get; set; }
    public int Duration { get; set; }
    public string Country { get; set; } = "";
}

// ─── Container Fixture (shared across collection) ─────────────────────────────

/// <summary>
/// Fixture che avvia UN singolo container ClickHouse per tutti i test di integrazione.
/// Usa xUnit IAsyncLifetime per startup/teardown asincrono.
/// </summary>
public sealed class ClickHouseContainerFixture : IAsyncLifetime
{
    private readonly ClickHouseContainer _container;

    public string ConnectionString { get; private set; } = "";

    public ClickHouseContainerFixture()
    {
        _container = new ClickHouseBuilder()
            .WithImage("clickhouse/clickhouse-server:24.10-alpine")
            .WithPortBinding(8123, assignRandomHostPort: true)
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await _container.StartAsync(cancellationToken);

        ConnectionString = _container.GetConnectionString();

        // Bootstrap RepoDb una sola volta
        GlobalConfiguration.Setup().UseClickHouse();

        // Crea le tabelle di test
        await using var conn = new ClickHouseConnection(ConnectionString);
        conn.Open();
        await CreateTablesAsync(conn, cancellationToken);
    }

    public async ValueTask DisposeAsync()
        => await _container.DisposeAsync();

    // ─── DDL per le tabelle di test ──────────────────────────────────────────

    private static async Task CreateTablesAsync(ClickHouseConnection conn, CancellationToken cancellationToken)
    {
        // Trunk profiles: ReplacingMergeTree per supportare upsert
        await ExecuteDdlAsync(conn, """
            CREATE TABLE IF NOT EXISTS test_trunk_profiles
            (
                TrunkId          String,
                Direction        String,
                AvgCallDuration  Float64,
                CallsPerHour     Int64,
                LastUpdated      DateTime
            )
            ENGINE = ReplacingMergeTree()
            ORDER BY TrunkId
            """, cancellationToken);

        // CDR events: MergeTree standard
        await ExecuteDdlAsync(conn, """
            CREATE TABLE IF NOT EXISTS test_cdr_events
            (
                EventId       String,
                TrunkId       String,
                CallingNumber String,
                CalledNumber  String,
                StartTime     DateTime,
                Duration      Int32,
                Country       LowCardinality(String)
            )
            ENGINE = MergeTree()
            ORDER BY (TrunkId, StartTime)
            """, cancellationToken);
    }

    private static async Task ExecuteDdlAsync(ClickHouseConnection conn, string ddl, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ─── Helper per ottenere una connessione aperta ───────────────────────────

    public ClickHouseConnection OpenConnection()
    {
        var conn = new ClickHouseConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Trunca le tabelle di test tra i test per garantire isolamento.
    /// </summary>
    public async Task TruncateAllAsync(CancellationToken cancellationToken)
    {
        await using var conn = new ClickHouseConnection(ConnectionString);
        conn.Open();
        await ExecuteDdlAsync(conn, "TRUNCATE TABLE test_trunk_profiles", cancellationToken);
        await ExecuteDdlAsync(conn, "TRUNCATE TABLE test_cdr_events", cancellationToken);
    }
}

// ─── xUnit Collection per condividere il container ───────────────────────────

[CollectionDefinition(ClickHouseTestCollection.Name)]
public sealed class ClickHouseTestCollection : ICollectionFixture<ClickHouseContainerFixture>
{
    public const string Name = "ClickHouse Integration Tests";
}

// ─── Factory di dati di test ─────────────────────────────────────────────────

public static class TestDataFactory
{
    public static TestTrunkProfile CreateTrunkProfile(
        string? trunkId = null,
        string direction = "outbound",
        double avgDuration = 45.0,
        long callsPerHour = 100)
        => new()
        {
            TrunkId = trunkId ?? $"TRK-{Guid.NewGuid():N}"[..12],
            Direction = direction,
            AvgCallDuration = avgDuration,
            CallsPerHour = callsPerHour,
            LastUpdated = DateTime.UtcNow
        };

    public static IEnumerable<TestTrunkProfile> CreateTrunkProfiles(int count)
        => Enumerable.Range(1, count).Select(i => CreateTrunkProfile(
            trunkId: $"TRK-{i:D4}",
            direction: i % 2 == 0 ? "inbound" : "outbound",
            avgDuration: 10.0 + i * 2.5,
            callsPerHour: 50L + i * 10));

    public static TestCdrEvent CreateCdrEvent(
        string? trunkId = null,
        string country = "IT",
        int duration = 60)
        => new()
        {
            EventId = Guid.NewGuid().ToString(),
            TrunkId = trunkId ?? "TRK-0001",
            CallingNumber = "+39011123456",
            CalledNumber = "+442071234567",
            StartTime = DateTime.UtcNow,
            Duration = duration,
            Country = country
        };

    public static IEnumerable<TestCdrEvent> CreateCdrEvents(int count, string trunkId = "TRK-0001")
        => Enumerable.Range(1, count).Select(i => new TestCdrEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TrunkId = trunkId,
            CallingNumber = $"+3901112345{i % 100:D2}",
            CalledNumber = $"+44207123{i % 10000:D4}",
            StartTime = DateTime.UtcNow.AddSeconds(-i * 10),
            Duration = 10 + (i % 300),
            Country = i % 3 == 0 ? "GB" : "IT"
        });
}
