using System;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

[Collection(ClickHouseTestCollection.Name)]
public class InsertIntegrationTests(ClickHouseContainerFixture fixture) : IAsyncLifetime
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    public ValueTask InitializeAsync() => new(_fixture.TruncateAllAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ─── Insert singolo ───────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_SingleEntity_ShouldBeQueryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profile = TestDataFactory.CreateTrunkProfile(
            trunkId: "TRK-INSERT-01",
            direction: "outbound",
            avgDuration: 42.5,
            callsPerHour: 150);

        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var result = (await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-INSERT-01", cancellationToken: cancellationToken)).ToList();

        result.Should().ContainSingle();
        var saved = result[0];
        saved.Direction.Should().Be("outbound");
        saved.AvgCallDuration.Should().BeApproximately(42.5, 0.001);
        saved.CallsPerHour.Should().Be(150);
    }

    [Fact]
    public async Task Insert_EntityWithDateTime_ShouldPreserveValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var now = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var profile = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-DT-01");
        profile.LastUpdated = now;

        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var result = (await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-DT-01", cancellationToken: cancellationToken)).Single();

        // ClickHouse tronca a secondi per DateTime
        result.LastUpdated.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }

    // ─── InsertAll ────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAll_BatchOf100_ShouldInsertAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(100).ToList();

        var affectedRows = await conn.InsertAllAsync(profiles, batchSize: 50, cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        var count = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(100);
    }

    [Fact]
    public async Task InsertAll_SmallBatch_ShouldHandleCorrectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(3).ToList();

        await conn.InsertAllAsync(profiles, batchSize: 10, cancellationToken: cancellationToken); // batchSize > actual count
        await Task.Delay(200, cancellationToken);

        var count = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(3);
    }

    [Fact]
    public async Task InsertAll_LargeBatch_ShouldInsertAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var events = TestDataFactory.CreateCdrEvents(5000).ToList();

        await conn.InsertAllAsync(events, batchSize: 1000, cancellationToken: cancellationToken);
        await Task.Delay(500, cancellationToken);

        var count = await conn.CountAsync<TestCdrEvent>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(5000);
    }

    // ─── Upsert con ReplacingMergeTree ────────────────────────────────────────

    [Fact]
    public async Task MergeAsync_WithReplacingMergeTree_ShouldUpsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        // Primo insert
        var profile = TestDataFactory.CreateTrunkProfile(
            trunkId: "TRK-UPSERT-01",
            callsPerHour: 100);
        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Upsert con valore aggiornato
        profile.CallsPerHour = 999;
        await conn.MergeAsync(profile, cancellationToken: cancellationToken);

        // Forza la deduplication di ReplacingMergeTree
        await conn.ExecuteNonQueryAsync(
            "OPTIMIZE TABLE test_trunk_profiles FINAL", cancellationToken: cancellationToken);
        await Task.Delay(500, cancellationToken);

        var result = await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-UPSERT-01", cancellationToken: cancellationToken);

        result.Should().ContainSingle()
              .Which.CallsPerHour.Should().Be(999);
    }

    // ─── Dati con caratteri speciali ──────────────────────────────────────────

    [Fact]
    public async Task Insert_StringWithSpecialChars_ShouldBeStoredCorrectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var ev = TestDataFactory.CreateCdrEvent(trunkId: "TRK-SPEC");
        ev.CallingNumber = "+39 011/123.456";
        ev.Country = "IT-special'test";

        await conn.InsertAsync(ev, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var result = (await conn.QueryAsync<TestCdrEvent>(
            e => e.EventId == ev.EventId, cancellationToken: cancellationToken)).Single();

        result.CallingNumber.Should().Be("+39 011/123.456");
        result.Country.Should().Be("IT-special'test");
    }
}
