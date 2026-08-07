using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb.ClickHouse.Bulk;
using RepoDb.ClickHouse.Extensions;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

[Collection(ClickHouseTestCollection.Name)]
public class BulkInsertIntegrationTests(ClickHouseContainerFixture fixture) : IAsyncLifetime
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    public ValueTask InitializeAsync() => new(_fixture.TruncateAllAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ─── BulkInsert base ─────────────────────────────────────────────────────

    [Fact]
    public async Task BulkInsert_SmallBatch_ShouldInsertAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(100).ToList();

        var result = await conn.BulkInsertAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        result.RowsWritten.Should().Be(100);
        result.BatchCount.Should().Be(1);
        result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
        result.TableName.Should().Be("test_trunk_profiles");

        var count = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(100);
    }

    [Fact]
    public async Task BulkInsert_LargeDataset_ShouldInsertAllRowsInMultipleBatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var events = TestDataFactory.CreateCdrEvents(50_000).ToList();

        var result = await conn.BulkInsertAsync(events,
            new ClickHouseBulkInsertOptions { BatchSize = 10_000 },
            cancellationToken: cancellationToken);
        await Task.Delay(500, cancellationToken);

        result.RowsWritten.Should().Be(50_000);
        result.BatchCount.Should().Be(5);
        result.RowsPerSecond.Should().BeGreaterThan(0);

        var count = await conn.CountAsync<TestCdrEvent>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(50_000);
    }

    [Fact]
    public async Task BulkInsert_Result_ShouldReportThroughput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var events = TestDataFactory.CreateCdrEvents(10_000).ToList();

        var result = await conn.BulkInsertAsync(events, cancellationToken: cancellationToken);

        result.RowsPerSecond.Should().BeGreaterThan(100);
        result.ToString().Should().Contain("test_cdr_events");
    }

    // ─── Streaming IAsyncEnumerable ───────────────────────────────────────────

    [Fact]
    public async Task BulkInsert_AsyncEnumerable_ShouldStreamWithoutMaterializingAll()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        // Genera 20_000 eventi come stream asincrono (non materializzati in RAM)
        var stream = GenerateEventsAsync(20_000, cancellationToken);

        var result = await conn.BulkInsertAsync(stream,
            new ClickHouseBulkInsertOptions { BatchSize = 5_000 },
            cancellationToken: cancellationToken);
        await Task.Delay(400, cancellationToken);

        result.RowsWritten.Should().Be(20_000);
        result.BatchCount.Should().Be(4);

        var count = await conn.CountAsync<TestCdrEvent>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(20_000);
    }

    [Fact]
    public async Task BulkInsert_AsyncEnumerable_CancellationShouldStop()
    {
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(millisecondsDelay: 50);

        var stream = GenerateEventsAsync(1_000_000, cts.Token); // enorme, ma verrà cancellato
        var act = async () => await conn.BulkInsertAsync(stream,
            new ClickHouseBulkInsertOptions { BatchSize = 100_000 },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── TableName override ───────────────────────────────────────────────────

    [Fact]
    public async Task BulkInsert_WithTableNameOverride_ShouldUseSpecifiedTable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(10).ToList();

        // Forza il nome tabella esplicitamente
        var result = await conn.BulkInsertAsync(
            profiles,
            tableName: "test_trunk_profiles",
            cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        result.TableName.Should().Be("test_trunk_profiles");
        var count = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(10);
    }

    // ─── Options preset ───────────────────────────────────────────────────────

    [Fact]
    public async Task BulkInsert_WithHighThroughputPreset_ShouldComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var events = TestDataFactory.CreateCdrEvents(5_000).ToList();

        var result = await conn.BulkInsertAsync(
            events,
            ClickHouseBulkInsertOptions.HighThroughput with { BatchSize = 2_500 },
            cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        result.RowsWritten.Should().Be(5_000);
    }

    [Fact]
    public async Task BulkInsert_WithDiagnosticPreset_ShouldComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(50).ToList();

        // Diagnostic ha BatchSize=10_000 ma noi abbiamo solo 50 righe → 1 batch
        var result = await conn.BulkInsertAsync(
            profiles,
            ClickHouseBulkInsertOptions.Diagnostic,
            cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        result.RowsWritten.Should().Be(50);
        result.BatchCount.Should().Be(1);
    }

    // ─── Data integrity ───────────────────────────────────────────────────────

    [Fact]
    public async Task BulkInsert_ShouldPreserveAllFieldValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profile = new TestTrunkProfile
        {
            TrunkId = "TRK-BULK-INTEGRITY",
            Direction = "inbound",
            AvgCallDuration = 77.77,
            CallsPerHour = 42_000L,
            LastUpdated = new DateTime(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        await conn.BulkInsertAsync([profile], cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var saved = (await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-BULK-INTEGRITY", cancellationToken: cancellationToken)).Single();

        saved.Direction.Should().Be("inbound");
        saved.AvgCallDuration.Should().BeApproximately(77.77, 0.001);
        saved.CallsPerHour.Should().Be(42_000L);
        saved.LastUpdated.Should().BeCloseTo(profile.LastUpdated, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BulkInsert_EmptyCollection_ShouldReturnZeroRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var result = await conn.BulkInsertAsync(
            Enumerable.Empty<TestTrunkProfile>(),
            cancellationToken: cancellationToken);

        result.RowsWritten.Should().Be(0);
        result.BatchCount.Should().Be(0);
    }

    // ─── Confronto con InsertAll ADO.NET ──────────────────────────────────────

    [Fact]
    public async Task BulkInsert_vs_InsertAll_BothShouldProduceIdenticalCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn1 = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        await using var conn2 = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var adonetEvents = TestDataFactory.CreateCdrEvents(1_000, "TRK-ADO").ToList();
        var bulkEvents = TestDataFactory.CreateCdrEvents(1_000, "TRK-BULK").ToList();

        // ADO.NET batch standard
        await conn1.InsertAllAsync(adonetEvents, batchSize: 200, cancellationToken: cancellationToken);
        // Bulk nativo
        await conn2.BulkInsertAsync(bulkEvents,
            new ClickHouseBulkInsertOptions { BatchSize = 500 },
            cancellationToken: cancellationToken);

        await Task.Delay(400, cancellationToken);

        var adonetCount = await conn1.CountAsync<TestCdrEvent>(
            e => e.TrunkId == "TRK-ADO", cancellationToken: cancellationToken);
        var bulkCount = await conn2.CountAsync<TestCdrEvent>(
            e => e.TrunkId == "TRK-BULK", cancellationToken: cancellationToken);

        adonetCount.Should().Be(1_000);
        bulkCount.Should().Be(1_000);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<TestCdrEvent> GenerateEventsAsync(
        int count,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return TestDataFactory.CreateCdrEvent(
                trunkId: $"TRK-{i % 10:D4}",
                country: i % 2 == 0 ? "IT" : "GB",
                duration: 10 + (i % 300));

            // Simula latenza di produzione ogni 1000 eventi
            if (i % 1000 == 0)
                await Task.Yield();
        }
    }
}
