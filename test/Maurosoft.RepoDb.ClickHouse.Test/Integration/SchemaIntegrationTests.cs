using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb.ClickHouse.DbHelpers;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

[Collection(ClickHouseTestCollection.Name)]
public class SchemaIntegrationTests(ClickHouseContainerFixture fixture)
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    // ─── GetFields via system.columns ────────────────────────────────────────

    [Fact]
    public async Task GetFields_ForTestTrunkProfiles_ShouldReturnAllColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        var fields = (await helper.GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken)).ToList();

        fields.Should().NotBeEmpty();
        fields.Select(f => f.Name).Should().Contain(
        [
            "TrunkId", "Direction", "AvgCallDuration", "CallsPerHour", "LastUpdated"
        ]);
    }

    [Fact]
    public async Task GetFields_TrunkId_ShouldBeMarkedAsPrimaryKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        var fields = (await helper.GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken)).ToList();
        var trunkId = fields.Single(f => f.Name == "TrunkId");

        trunkId.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task GetFields_NumericColumn_ShouldMapToCorrectDotNetType()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        var fields = (await helper.GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken)).ToList();

        fields.Single(f => f.Name == "AvgCallDuration").Type.Should().Be<double>();
        fields.Single(f => f.Name == "CallsPerHour").Type.Should().Be<long>();
    }

    [Fact]
    public async Task GetFields_StringColumn_ShouldMapToString()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        var fields = (await helper.GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken)).ToList();

        fields.Single(f => f.Name == "Direction").Type.Should().Be<string>();
    }

    [Fact]
    public async Task GetFields_DateTimeColumn_ShouldMapToDateTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        var fields = (await helper.GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken)).ToList();

        fields.Single(f => f.Name == "LastUpdated").Type.Should().Be<DateTime>();
    }

    [Fact]
    public async Task GetFields_LowCardinalityColumn_ShouldUnwrapToString()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        // Country è definita come LowCardinality(String)
        var fields = (await helper.GetFieldsAsync(conn, "test_cdr_events", cancellationToken: cancellationToken)).ToList();

        fields.Single(f => f.Name == "Country").Type.Should().Be<string>();
    }

    [Fact]
    public async Task GetFields_QualifiedTableName_ShouldWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        var helper = new ClickHouseDbHelper();

        // Nome qualificato: database.tablename
        var fields = (await helper.GetFieldsAsync(conn, "default.test_trunk_profiles", cancellationToken: cancellationToken)).ToList();

        fields.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetFields_ViaRepoDb_ShouldUseRegisteredHelper()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);
        conn.Open();

        // Usa il GetDbHelper() extension di RepoDb che internamente usa il mapper registrato
        var fields = await conn.GetDbHelper()
                               .GetFieldsAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken);

        fields.Should().NotBeEmpty();
        fields.Select(f => f.Name).Should().Contain("TrunkId");
    }
}
