using FluentAssertions;
using RepoDb.ClickHouse.Bulk;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit.Bulk;

public class ClickHouseBulkInsertOptionsTests
{
    // ─── Default ─────────────────────────────────────────────────────────────

    [Fact]
    public void Default_ShouldHaveSensibleValues()
    {
        var opt = ClickHouseBulkInsertOptions.Default;

        opt.BatchSize.Should().Be(100_000);
        opt.MaxDegreeOfParallelism.Should().Be(1);
        opt.UseRepoDbFieldMapping.Should().BeTrue();
        opt.CommandTimeoutSeconds.Should().Be(300);
        opt.EnableDiagnostics.Should().BeFalse();
        opt.TableName.Should().BeNull();
    }

    // ─── HighThroughput ───────────────────────────────────────────────────────

    [Fact]
    public void HighThroughput_ShouldHaveLargeBatchAndParallelism()
    {
        var opt = ClickHouseBulkInsertOptions.HighThroughput;

        opt.BatchSize.Should().BeGreaterThanOrEqualTo(100_000);
        opt.MaxDegreeOfParallelism.Should().BeGreaterThanOrEqualTo(2);
        opt.CommandTimeoutSeconds.Should().BeGreaterThan(300);
    }

    // ─── Diagnostic ───────────────────────────────────────────────────────────

    [Fact]
    public void Diagnostic_ShouldHaveSmallBatchAndLoggingEnabled()
    {
        var opt = ClickHouseBulkInsertOptions.Diagnostic;

        opt.BatchSize.Should().BeLessThan(100_000);
        opt.EnableDiagnostics.Should().BeTrue();
        opt.MaxDegreeOfParallelism.Should().Be(1);
    }

    // ─── Record with expression ───────────────────────────────────────────────

    [Fact]
    public void With_TableName_ShouldOverrideOnlyTableName()
    {
        var base_  = ClickHouseBulkInsertOptions.HighThroughput;
        var custom = base_ with { TableName = "my_table" };

        custom.TableName.Should().Be("my_table");
        custom.BatchSize.Should().Be(base_.BatchSize);
        custom.MaxDegreeOfParallelism.Should().Be(base_.MaxDegreeOfParallelism);
    }

    // ─── RowsPerSecond ────────────────────────────────────────────────────────

    [Fact]
    public void BulkInsertResult_RowsPerSecond_ShouldCalculateCorrectly()
    {
        var result = new ClickHouseBulkInsertResult
        {
            RowsWritten = 1_000_000,
            BatchCount  = 10,
            Elapsed     = TimeSpan.FromSeconds(10),
            TableName   = "test_table"
        };

        result.RowsPerSecond.Should().BeApproximately(100_000, 1);
    }

    [Fact]
    public void BulkInsertResult_RowsPerSecond_WhenZeroElapsed_ShouldReturnZero()
    {
        var result = new ClickHouseBulkInsertResult
        {
            RowsWritten = 100,
            BatchCount  = 1,
            Elapsed     = TimeSpan.Zero,
            TableName   = "test"
        };

        result.RowsPerSecond.Should().Be(0);
    }

    [Fact]
    public void BulkInsertResult_ToString_ShouldContainKeyInfo()
    {
        var result = new ClickHouseBulkInsertResult
        {
            RowsWritten = 500_000,
            BatchCount  = 5,
            Elapsed     = TimeSpan.FromSeconds(5),
            TableName   = "cdr_events"
        };

        var str = result.ToString();
        str.Should().Contain("cdr_events");
        str.Should().Contain("500");   // righe (formattate con N0)
        str.Should().Contain("5");     // batch
    }
}
