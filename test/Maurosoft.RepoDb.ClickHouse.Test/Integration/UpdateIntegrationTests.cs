using System;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using FluentAssertions;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Integration;

/// <summary>
/// Test per le mutation ALTER TABLE UPDATE di ClickHouse.
/// Le mutations sono asincrone nel motore: i test usano OPTIMIZE TABLE FINAL
/// e polling per garantire che il risultato sia visibile.
/// </summary>
[Collection(ClickHouseTestCollection.Name)]
public class UpdateIntegrationTests(ClickHouseContainerFixture fixture) : IAsyncLifetime
{
    private readonly ClickHouseContainerFixture _fixture = fixture;

    public ValueTask InitializeAsync() => new(_fixture.TruncateAllAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ─── UPDATE tramite mutation ──────────────────────────────────────────────

    [Fact]
    public async Task Update_ViaMutation_ShouldUpdateValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profile = TestDataFactory.CreateTrunkProfile(
            trunkId: "TRK-UPD-01",
            callsPerHour: 100);
        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Mutation ALTER TABLE UPDATE (asincrona nel motore)
        await conn.ExecuteNonQueryAsync(
            "ALTER TABLE test_trunk_profiles UPDATE CallsPerHour = @newVal WHERE TrunkId = @id",
            new { newVal = 999L, id = "TRK-UPD-01" },
            cancellationToken: cancellationToken);

        // Attende che la mutation sia completata
        await WaitForMutationAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken);

        var result = (await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-UPD-01", cancellationToken: cancellationToken)).Single();

        result.CallsPerHour.Should().Be(999);
    }

    [Fact]
    public async Task Update_MultipleColumns_ShouldUpdateAllSpecifiedColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profile = TestDataFactory.CreateTrunkProfile(
            trunkId: "TRK-UPD-MULTI",
            direction: "outbound",
            avgDuration: 10.0,
            callsPerHour: 50);
        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        await conn.ExecuteNonQueryAsync(
            """
            ALTER TABLE test_trunk_profiles
            UPDATE Direction = @dir, CallsPerHour = @calls
            WHERE TrunkId = @id
            """,
            new { dir = "inbound", calls = 300L, id = "TRK-UPD-MULTI" },
            cancellationToken: cancellationToken);

        await WaitForMutationAsync(conn, "test_trunk_profiles", cancellationToken: cancellationToken);

        var result = (await conn.QueryAsync<TestTrunkProfile>(
            p => p.TrunkId == "TRK-UPD-MULTI", cancellationToken: cancellationToken)).Single();

        result.Direction.Should().Be("inbound");
        result.CallsPerHour.Should().Be(300);
        result.AvgCallDuration.Should().BeApproximately(10.0, 0.001); // invariato
    }

    [Fact]
    public async Task Update_NonExistentRow_ShouldNotThrow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var act = async () => await conn.ExecuteNonQueryAsync(
            "ALTER TABLE test_trunk_profiles UPDATE CallsPerHour = 1 WHERE TrunkId = 'NON-EXISTENT'",
            cancellationToken: cancellationToken);

        // ClickHouse non lancia eccezione per UPDATE di zero righe
        await act.Should().NotThrowAsync();
    }

    // ─── Check mutation pending ───────────────────────────────────────────────

    [Fact]
    public async Task SystemMutations_AfterUpdate_ShouldBeQueryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new RepoDbClickHouseConnection(_fixture.ConnectionString);

        var profile = TestDataFactory.CreateTrunkProfile(trunkId: "TRK-MUT-CHECK");
        await conn.InsertAsync(profile, cancellationToken: cancellationToken);
        await Task.Delay(200, cancellationToken);

        await conn.ExecuteNonQueryAsync(
            "ALTER TABLE test_trunk_profiles UPDATE Direction = 'test' WHERE TrunkId = @id",
            new { id = "TRK-MUT-CHECK" },
            cancellationToken: cancellationToken);

        // system.mutations deve essere interrogabile
        var act = async () => await conn.ExecuteQueryAsync<dynamic>(
            "SELECT mutation_id, command, is_done FROM system.mutations WHERE table = 'test_trunk_profiles'",
            cancellationToken: cancellationToken);

        await act.Should().NotThrowAsync();
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Attende che tutte le mutations pendenti su una tabella siano completate.
    /// Usa polling con OPTIMIZE FINAL come fallback.
    /// </summary>
    private static async Task WaitForMutationAsync(
        ClickHouseConnection conn,
        string tableName,
        int maxWaitMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        // Forza il completamento della mutation
        await conn.ExecuteNonQueryAsync(
            $"OPTIMIZE TABLE {tableName} FINAL", cancellationToken: cancellationToken);

        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            var pending = await conn.ExecuteScalarAsync<ulong>(
                $"SELECT COUNT() FROM system.mutations WHERE table = '{tableName}' AND is_done = 0",
                cancellationToken: cancellationToken);

            if (pending == 0)
                return;

            await Task.Delay(200, cancellationToken);
        }
    }
}
