using ClickHouse.Driver.ADO;
using FluentAssertions;
using RepoDb.ClickHouse.StatementBuilders;
using RepoDb.DbSettings;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit;

public class ClickHouseBootstrapTests
{
    public ClickHouseBootstrapTests()
        => GlobalConfiguration.Setup().UseClickHouse();

    [Fact]
    public void UseClickHouse_ShouldMarkBootstrapAsInitialized()
        => ClickHouseBootstrap.IsInitialized.Should().BeTrue();

    [Fact]
    public void UseClickHouse_DbSetting_ShouldBeRegisteredAndBeBaseDbSetting()
    {
        var setting = DbSettingMapper.Get<RepoDbClickHouseConnection>();
        setting.Should().NotBeNull();
        // v1.14.0: deve essere BaseDbSetting per GetHashCode() thread-safe
        setting.Should().BeAssignableTo<BaseDbSetting>();
        setting!.OpeningQuote.Should().Be("`");
        setting.ParameterPrefix.Should().Be("@");
        // v1.14.0: proprietà rinominata
        setting.IsMultiStatementExecutable.Should().BeFalse();
        // v1.14.0: proprietà aggiunta
        setting.AverageableType.Should().Be<double>();
    }

    [Fact]
    public void UseClickHouse_DbHelper_ShouldBeRegistered()
        => DbHelperMapper.Get<RepoDbClickHouseConnection>().Should().NotBeNull();

    [Fact]
    public void UseClickHouse_StatementBuilder_ShouldBeClickHouseStatementBuilder()
    {
        var builder = StatementBuilderMapper.Get<RepoDbClickHouseConnection>();
        builder.Should().NotBeNull();
        builder.Should().BeOfType<ClickHouseStatementBuilder>();
    }

    [Fact]
    public void UseClickHouse_CalledMultipleTimes_ShouldBeIdempotent()
    {
        var act = () =>
        {
            GlobalConfiguration.Setup().UseClickHouse();
            GlobalConfiguration.Setup().UseClickHouse();
        };
        act.Should().NotThrow();
    }
}
