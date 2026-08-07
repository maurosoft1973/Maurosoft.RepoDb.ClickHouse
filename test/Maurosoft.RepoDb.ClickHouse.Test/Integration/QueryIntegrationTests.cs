using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb;
using RepoDb.Enumerations;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

[Collection(ClickHouseTestCollection.Name)]
public class QueryIntegrationTests(ClickHouseContainerFixture fixture) : IAsyncLifetime
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    public ValueTask InitializeAsync() => new(_fixture.TruncateAllAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ─── QueryAll ────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAll_EmptyTable_ShouldReturnEmptyCollection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var result = await conn.QueryAllAsync<TestTrunkProfile>(cancellationToken: cancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAll_AfterInsert_ShouldReturnAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(5).ToList();
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);

        // ClickHouse necessita di un breve delay per rendere visibili i dati
        await Task.Delay(200, cancellationToken);

        var result = await conn.QueryAllAsync<TestTrunkProfile>(cancellationToken: cancellationToken);

        result.Should().HaveCount(5);
    }

    // ─── Query con filtro ─────────────────────────────────────────────────────

    [Fact]
    public async Task Query_WithWhereExpression_ShouldFilterCorrectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var inbound = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-IN-01", direction: "inbound");
        var outbound = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-OUT-01", direction: "outbound");
        await conn.InsertAllAsync([inbound, outbound], cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var result = await conn.QueryAsync<TestTrunkProfile>(
            p => p.Direction == "inbound", cancellationToken: cancellationToken);

        result.Should().ContainSingle()
              .Which.TrunkId.Should().Be("TRK-IN-01");
    }

    [Fact]
    public async Task Query_WithNumericFilter_ShouldFilterCorrectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profiles = new[]
        {
            TestDataFactory.CreateTrunkProfile(trunkId: "TRK-LOW",  callsPerHour: 10),
            TestDataFactory.CreateTrunkProfile(trunkId: "TRK-HIGH", callsPerHour: 500),
        };
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var result = await conn.QueryAsync<TestTrunkProfile>(
            p => p.CallsPerHour > 100, cancellationToken: cancellationToken);

        result.Should().ContainSingle()
              .Which.TrunkId.Should().Be("TRK-HIGH");
    }

    // ─── Count ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Count_ShouldReturnCorrectCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(7).ToList();
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var count = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);

        count.Should().Be(7);
    }

    [Fact]
    public async Task Count_WithWhere_ShouldCountFilteredRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(10).ToList();
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        // I profili con indice pari hanno direction="inbound" (vedere factory)
        var count = await conn.CountAsync<TestTrunkProfile>(
            p => p.Direction == "inbound", cancellationToken: cancellationToken);

        count.Should().Be(5);
    }

    // ─── Exists ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exists_WhenRecordExists_ShouldReturnTrue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profile = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-EXISTS");
        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var exists = await conn.ExistsAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-EXISTS", cancellationToken: cancellationToken);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_WhenRecordDoesNotExist_ShouldReturnFalse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var exists = await conn.ExistsAsync<TestTrunkProfile>(
            p => p.TrunkId == "NON-EXISTENT-TRK", cancellationToken: cancellationToken);

        exists.Should().BeFalse();
    }

    // ─── BatchQuery (paginazione) ─────────────────────────────────────────────

    [Fact]
    public async Task BatchQuery_ShouldPaginateCorrectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(20).ToList();
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var page1 = (await conn.BatchQueryAsync<TestTrunkProfile>(
            page: 0, rowsPerBatch: 5,
            orderBy: [new OrderField("TrunkId", Order.Ascending)], where: (object)null!,
            cancellationToken: cancellationToken)).ToList();

        var page2 = (await conn.BatchQueryAsync<TestTrunkProfile>(
            page: 1, rowsPerBatch: 5,
            orderBy: [new OrderField("TrunkId", Order.Ascending)], where: (object)null!,
            cancellationToken: cancellationToken)).ToList();

        page1.Should().HaveCount(5);
        page2.Should().HaveCount(5);
        page1.Select(p => p.TrunkId).Should().NotIntersectWith(page2.Select(p => p.TrunkId));
    }

    // ─── ExecuteQuery (SQL raw) ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteQuery_WithClickHouseSpecificSQL_ShouldWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var profiles = TestDataFactory.CreateTrunkProfiles(3).ToList();
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Usa funzioni ClickHouse-specific
        var result = await conn.ExecuteQueryAsync<TestTrunkProfile>(
            "SELECT * FROM test_trunk_profiles ORDER BY TrunkId LIMIT 2", cancellationToken: cancellationToken);

        result.Should().HaveCount(2);
    }

    // ─── Aggregate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteScalar_SUM_ShouldReturnCorrectValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var events = new[]
        {
            TestDataFactory.CreateCdrEvent(duration: 100),
            TestDataFactory.CreateCdrEvent(duration: 200),
            TestDataFactory.CreateCdrEvent(duration: 300),
        };
        await conn.InsertAllAsync(events, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var total = await conn.ExecuteScalarAsync<long>(
            "SELECT SUM(Duration) FROM test_cdr_events", cancellationToken: cancellationToken);

        total.Should().Be(600);
    }
}
