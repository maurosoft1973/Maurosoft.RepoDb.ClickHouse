using FluentAssertions;
using RepoDb.ClickHouse.DbSettings;
using RepoDb.DbSettings;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit.DbSettings;

public class ClickHouseDbSettingTests
{
    private readonly ClickHouseDbSetting _sut = new();

    [Fact] public void OpeningQuote_ShouldBeBacktick() => _sut.OpeningQuote.Should().Be("`");
    [Fact] public void ClosingQuote_ShouldBeBacktick() => _sut.ClosingQuote.Should().Be("`");
    [Fact] public void ParameterPrefix_ShouldBeAtSign() => _sut.ParameterPrefix.Should().Be("@");
    [Fact] public void SchemaSeparator_ShouldBeDot() => _sut.SchemaSeparator.Should().Be(".");
    [Fact] public void DefaultSchema_ShouldBeNull() => _sut.DefaultSchema.Should().BeNull();
    [Fact] public void AreTableHintsSupported_ShouldBeFalse() => _sut.AreTableHintsSupported.Should().BeFalse();
    [Fact] public void IsDirectionSupported_ShouldBeFalse() => _sut.IsDirectionSupported.Should().BeFalse();
    [Fact] public void IsPreparable_ShouldBeFalse() => _sut.IsPreparable.Should().BeFalse();
    [Fact] public void IsUseUpsert_ShouldBeFalse() => _sut.IsUseUpsert.Should().BeFalse();
    [Fact] public void IsExecuteReaderDisposable_ShouldBeTrue() => _sut.IsExecuteReaderDisposable.Should().BeTrue();

    // ─── RepoDB v1.14.0: proprietà RINOMINATA ────────────────────────────────

    /// <summary>
    /// BREAKING v1.14.0: IsMultipleStatementExecutionSupported → IsMultiStatementExecutable.
    /// Il vecchio nome causava un errore di compilazione se si implementava IDbSetting.
    /// Ora usiamo BaseDbSetting che espone il nome corretto.
    /// </summary>
    [Fact]
    public void IsMultiStatementExecutable_ShouldBeFalse()
        => _sut.IsMultiStatementExecutable.Should().BeFalse();

    // ─── RepoDB v1.14.0: proprietà AGGIUNTA ──────────────────────────────────

    /// <summary>
    /// AverageableType era assente nella nostra implementazione v1.0.
    /// ClickHouse restituisce Float64 per AVG → double.
    /// </summary>
    [Fact]
    public void AverageableType_ShouldBeDouble()
        => _sut.AverageableType.Should().Be<double>();

    // ─── RepoDB v1.14.0: BaseDbSetting e GetHashCode thread-safe ─────────────

    /// <summary>
    /// BREAKING v1.14.0: deve ereditare BaseDbSetting (PR #1153).
    /// BaseDbSetting implementa GetHashCode() via HashCode.Combine() evitando
    /// collisioni e race condition nel mapper lookup multi-thread.
    /// Implementare IDbSetting direttamente senza GetHashCode() era un bug latente.
    /// </summary>
    [Fact]
    public void ShouldInheritBaseDbSetting()
        => _sut.Should().BeAssignableTo<BaseDbSetting>();

    [Fact]
    public void GetHashCode_TwoEqualInstances_ShouldProduceSameHash()
    {
        var a = new ClickHouseDbSetting();
        var b = new ClickHouseDbSetting();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_SameInstance_ShouldBeStable()
    {
        var h1 = _sut.GetHashCode();
        var h2 = _sut.GetHashCode();
        h1.Should().Be(h2);
    }

    [Fact]
    public void GetHashCode_ConcurrentAccess_ShouldBeThreadSafe()
    {
        var expected = _sut.GetHashCode();
        var results = new int[200];
        Parallel.For(0, 200, i => results[i] = _sut.GetHashCode());
        results.Should().AllSatisfy(h => h.Should().Be(expected));
    }
}
