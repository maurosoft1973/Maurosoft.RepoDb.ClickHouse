using FluentAssertions;
using RepoDb.ClickHouse.DbSettings;
using RepoDb.ClickHouse.StatementBuilders;
using RepoDb.Enumerations;
using RepoDb.Exceptions;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit.StatementBuilders;

/// <summary>
/// Verifica che lo StatementBuilder generi SQL corretto per il dialetto ClickHouse.
/// I test usano NormalizeSQL() per ignorare differenze di spaziatura/case nei whitespace.
///
/// RepoDB v1.15.1: IStatementBuilder non prende più un QueryBuilder come parametro:
/// ogni metodo costruisce il proprio SQL internamente a partire da tableName/fields/where/ecc.
/// </summary>
public partial class ClickHouseStatementBuilderTests
{
    private readonly ClickHouseStatementBuilder _sut;
    private readonly ClickHouseDbSetting _dbSetting;

    public ClickHouseStatementBuilderTests()
    {
        _dbSetting = new ClickHouseDbSetting();
        _sut = new ClickHouseStatementBuilder(_dbSetting);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string N(string sql)
        => WhitespaceRegex().Replace(sql.Trim(), " ");

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    private static IEnumerable<Field> Fields(params string[] names)
        => names.Select(n => new Field(n));

    // ─── CreateQuery ────────────────────────────────────────────────────────

    [Fact]
    public void CreateQuery_SimpleSelect_ShouldGenerateCorrectSQL()
    {
        var sql = _sut.CreateQuery(
            "trunk_profiles",
            Fields("TrunkId", "Direction", "CallsPerHour"),
            where: null, orderBy: null, top: null);

        N(sql).Should().Contain("SELECT")
              .And.Contain("`TrunkId`")
              .And.Contain("`Direction`")
              .And.Contain("`CallsPerHour`")
              .And.Contain("FROM `trunk_profiles`");
    }

    [Fact]
    public void CreateQuery_WithTop_ShouldAddLimit()
    {
        var sql = _sut.CreateQuery(
            "cdr_events",
            Fields("EventId", "Duration"),
            where: null, orderBy: null, top: 100);

        N(sql).Should().Contain("LIMIT 100");
    }

    [Fact]
    public void CreateQuery_WithOrderBy_ShouldAddOrderByClause()
    {
        var orderBy = new[] { new OrderField("StartTime", Order.Ascending) };
        var sql = _sut.CreateQuery(
            "cdr_events",
            Fields("EventId", "StartTime"),
            where: null, orderBy: orderBy, top: null);

        N(sql).Should().Contain("ORDER BY");
        N(sql).Should().Contain("`StartTime`");
    }

    [Fact]
    public void CreateQuery_WithWhere_ShouldAddWhereClause()
    {
        var where = new QueryGroup(new QueryField("TrunkId", "TRK-001"));
        var sql = _sut.CreateQuery(
            "trunk_profiles",
            Fields("TrunkId", "Direction"),
            where: where, orderBy: null, top: null);

        N(sql).Should().Contain("WHERE");
        N(sql).Should().Contain("`TrunkId`");
    }

    // ─── CreateCount ────────────────────────────────────────────────────────

    [Fact]
    public void CreateCount_ShouldUseCOUNTStar()
    {
        var sql = _sut.CreateCount("cdr_events");

        N(sql).Should().Contain("SELECT COUNT(*)");
        N(sql).Should().Contain("FROM `cdr_events`");
    }

    [Fact]
    public void CreateCount_WithWhere_ShouldAddWhereClause()
    {
        var where = new QueryGroup(new QueryField("Country", "IT"));
        var sql = _sut.CreateCount("cdr_events", where: where);

        N(sql).Should().Contain("COUNT(*)");
        N(sql).Should().Contain("WHERE");
        N(sql).Should().Contain("`Country`");
    }

    // ─── CreateInsert ────────────────────────────────────────────────────────

    [Fact]
    public void CreateInsert_ShouldGenerateInsertIntoWithValues()
    {
        var sql = _sut.CreateInsert(
            "trunk_profiles",
            Fields("TrunkId", "Direction", "CallsPerHour"));

        N(sql).Should().Contain("INSERT INTO `trunk_profiles`");
        N(sql).Should().Contain("`TrunkId`").And.Contain("`Direction`").And.Contain("`CallsPerHour`");
        N(sql).Should().Contain("VALUES");
        N(sql).Should().Contain("@TrunkId");
        N(sql).Should().Contain("@Direction");
        N(sql).Should().Contain("@CallsPerHour");
    }

    [Fact]
    public void CreateInsert_ShouldNotContainOUTPUT_NoIdentityInClickHouse()
    {
        var sql = _sut.CreateInsert(
            "my_table",
            Fields("Id", "Name"),
            primaryField: new DbField("Id", true, false, false, typeof(ulong), null, null, null, null!, false, "Id"));

        // ClickHouse non supporta OUTPUT INSERTED come SQL Server
        N(sql).Should().NotContain("OUTPUT");
        N(sql).Should().NotContain("RETURNING");
    }

    // ─── CreateInsertAll ─────────────────────────────────────────────────────

    [Fact]
    public void CreateInsertAll_WithBatchSize3_ShouldGenerate3ValueRows()
    {
        var sql = _sut.CreateInsertAll(
            "cdr_events",
            Fields("EventId", "TrunkId", "Duration"),
            batchSize: 3);

        // Deve contenere 3 set di parametri con indici _0, _1, _2
        sql.Should().Contain("@EventId_0");
        sql.Should().Contain("@EventId_1");
        sql.Should().Contain("@EventId_2");
        sql.Should().NotContain("@EventId_3");
    }

    // ─── CreateUpdate (Mutation) ──────────────────────────────────────────────

    [Fact]
    public void CreateUpdate_ShouldGenerateAlterTableUpdateMutation()
    {
        var pk = new DbField("TrunkId", true, false, false, typeof(string), null, null, null, null!, false, "TrunkId");
        var sql = _sut.CreateUpdate(
            "trunk_profiles",
            Fields("TrunkId", "Direction", "CallsPerHour"),
            where: null,
            primaryField: pk);

        N(sql).Should().Contain("ALTER TABLE");
        N(sql).Should().Contain("`trunk_profiles`");
        N(sql).Should().Contain("UPDATE");
        // La PK non deve essere nell'UPDATE SET
        N(sql).Should().NotContain("`TrunkId` = @TrunkId");
        N(sql).Should().Contain("`Direction`");
        N(sql).Should().Contain("`CallsPerHour`");
    }

    [Fact]
    public void CreateUpdate_OnlyPKFields_ShouldThrowEmptyException()
    {
        var pk = new DbField("Id", true, false, false, typeof(long), null, null, null, null!, false, "Id");

        var act = () => _sut.CreateUpdate(
            "my_table",
            Fields("Id"),   // solo la PK, nessun campo da aggiornare
            primaryField: pk);

        act.Should().Throw<EmptyException>();
    }

    // ─── CreateDelete ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateDelete_ShouldGenerateLightweightDelete()
    {
        var sql = _sut.CreateDelete("cdr_events");

        N(sql).Should().Contain("DELETE FROM `cdr_events`");
        // Non deve usare la sintassi mutation ALTER TABLE
        N(sql).Should().NotContain("ALTER TABLE");
    }

    [Fact]
    public void CreateDelete_WithWhere_ShouldAddWhereClause()
    {
        var where = new QueryGroup(new QueryField("TrunkId", "TRK-001"));
        var sql = _sut.CreateDelete("cdr_events", where: where);

        N(sql).Should().Contain("DELETE FROM `cdr_events`");
        N(sql).Should().Contain("WHERE");
        N(sql).Should().Contain("`TrunkId`");
    }

    // ─── CreateMerge ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateMerge_ShouldFallbackToInsert()
    {
        var sql = _sut.CreateMerge(
            "trunk_profiles",
            Fields("TrunkId", "Direction", "CallsPerHour"));

        // Merge su ClickHouse è un INSERT (funziona con ReplacingMergeTree)
        N(sql).Should().Contain("INSERT INTO `trunk_profiles`");
        N(sql).Should().NotContain("MERGE");
    }

    // ─── CreateTruncate ───────────────────────────────────────────────────────

    [Fact]
    public void CreateTruncate_ShouldGenerateTruncateTable()
    {
        var sql = _sut.CreateTruncate("cdr_events");

        N(sql).Should().Contain("TRUNCATE");
        N(sql).Should().Contain("`cdr_events`");
    }

    // ─── CreateExists ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateExists_ShouldSelectOneWithLimitOne()
    {
        var sql = _sut.CreateExists("trunk_profiles");

        N(sql).Should().Contain("SELECT 1");
        N(sql).Should().Contain("FROM `trunk_profiles`");
        N(sql).Should().Contain("LIMIT 1");
    }

    // ─── CreateBatchQuery ─────────────────────────────────────────────────────

    [Fact]
    public void CreateBatchQuery_ShouldUseLimitOffset()
    {
        var sql = _sut.CreateBatchQuery(
            "cdr_events",
            Fields("EventId", "StartTime"),
            page: 2,
            rowsPerBatch: 50);

        N(sql).Should().Contain("LIMIT 50 OFFSET 100");
    }

    [Fact]
    public void CreateBatchQuery_FirstPage_ShouldHaveZeroOffset()
    {
        var sql = _sut.CreateBatchQuery(
            "cdr_events",
            Fields("EventId"),
            page: 0,
            rowsPerBatch: 100);

        N(sql).Should().Contain("LIMIT 100 OFFSET 0");
    }

    // ─── Aggregate functions ──────────────────────────────────────────────────

    [Fact]
    public void CreateSum_ShouldGenerateSumFunction()
    {
        var sql = _sut.CreateSum("cdr_events", new Field("Duration"));
        N(sql).Should().Contain("SUM(`Duration`)").And.Contain("AS SumValue");
    }

    [Fact]
    public void CreateAverage_ShouldGenerateAvgFunction()
    {
        var sql = _sut.CreateAverage("cdr_events", new Field("Duration"));
        N(sql).Should().Contain("AVG(`Duration`)").And.Contain("AS AverageValue");
    }

    [Fact]
    public void CreateMin_ShouldGenerateMinFunction()
    {
        var sql = _sut.CreateMin("cdr_events", new Field("Duration"));
        N(sql).Should().Contain("MIN(`Duration`)").And.Contain("AS MinValue");
    }

    [Fact]
    public void CreateMax_ShouldGenerateMaxFunction()
    {
        var sql = _sut.CreateMax("cdr_events", new Field("Duration"));
        N(sql).Should().Contain("MAX(`Duration`)").And.Contain("AS MaxValue");
    }

    // ─── Quoting ──────────────────────────────────────────────────────────────

    [Fact]
    public void CreateQuery_TableName_ShouldBeBacktickQuoted()
    {
        var sql = _sut.CreateQuery("my_table", Fields("Id"));

        N(sql).Should().Contain("`my_table`");
    }

    [Fact]
    public void CreateQuery_QualifiedTableName_ShouldPreserveDatabase()
    {
        var sql = _sut.CreateQuery("default.cdr_events", Fields("Id"));

        // Il nome qualificato deve essere preservato
        N(sql).Should().Contain("default");
        N(sql).Should().Contain("cdr_events");
    }
}
