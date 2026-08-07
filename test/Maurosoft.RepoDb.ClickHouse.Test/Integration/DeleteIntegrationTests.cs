using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

[Collection(ClickHouseTestCollection.Name)]
public class DeleteIntegrationTests(ClickHouseContainerFixture fixture) : IAsyncLifetime
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    public ValueTask InitializeAsync() => new(_fixture.TruncateAllAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ─── Lightweight DELETE ───────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithPredicate_ShouldRemoveMatchingRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profiles = new[]
        {
            TestDataFactory.CreateTrunkProfile(trunkId: "TRK-DEL-01", direction: "inbound"),
            TestDataFactory.CreateTrunkProfile(trunkId: "TRK-DEL-02", direction: "inbound"),
            TestDataFactory.CreateTrunkProfile(trunkId: "TRK-KEEP-01", direction: "outbound"),
        };
        await conn.InsertAllAsync(profiles, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Delete lightweight con WHERE
        await conn.ExecuteNonQueryAsync(
            "DELETE FROM test_trunk_profiles WHERE Direction = 'inbound'", cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        var remaining = await conn.QueryAllAsync<TestTrunkProfile>(cancellationToken: cancellationToken);
        remaining.Should().ContainSingle()
                 .Which.TrunkId.Should().Be("TRK-KEEP-01");
    }

    [Fact]
    public async Task DeleteAsync_WithQueryGroup_ShouldDeleteMatchingRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profile = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-DEL-QG");
        var other = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-KEEP-QG");
        await conn.InsertAllAsync([profile, other], cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var where = new QueryGroup(new QueryField("TrunkId", "TRK-DEL-QG"));
        await conn.DeleteAsync<TestTrunkProfile>(where, cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        var exists = await conn.ExistsAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-DEL-QG", cancellationToken: cancellationToken);

        exists.Should().BeFalse();

        var keptExists = await conn.ExistsAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-KEEP-QG", cancellationToken: cancellationToken);

        keptExists.Should().BeTrue();
    }

    // ─── Truncate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Truncate_ShouldRemoveAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        await conn.InsertAllAsync(TestDataFactory.CreateTrunkProfiles(10).ToList(), cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var countBefore = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        countBefore.Should().Be(10);

        await conn.TruncateAsync<TestTrunkProfile>(cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        var countAfter = await conn.CountAsync<TestTrunkProfile>(where: (object)null!, cancellationToken: cancellationToken);
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task Truncate_OnEmptyTable_ShouldNotThrow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var act = async () => await conn.TruncateAsync<TestTrunkProfile>(cancellationToken: cancellationToken);

        await act.Should().NotThrowAsync();
    }

    // ─── DeleteAll ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAll_ShouldRemoveAllRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        await conn.InsertAllAsync(TestDataFactory.CreateCdrEvents(50).ToList(), cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        await conn.DeleteAllAsync<TestCdrEvent>(cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);

        var count = await conn.CountAsync<TestCdrEvent>(where: (object)null!, cancellationToken: cancellationToken);
        count.Should().Be(0);
    }
}
