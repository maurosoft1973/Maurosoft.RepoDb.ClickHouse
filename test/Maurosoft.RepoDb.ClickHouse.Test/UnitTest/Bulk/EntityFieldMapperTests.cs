using FluentAssertions;
using RepoDb.Attributes;
using RepoDb.ClickHouse.Bulk;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit.Bulk;

// ─── Entità di test ───────────────────────────────────────────────────────────

[Map("mapped_table")]
public class EntityWithMap
{
    [Primary]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class EntityWithWriteOnly
{
    public string Id { get; set; } = "";
    public string Keep { get; set; } = "";

    // Proprietà write-only: deve essere ignorata
    public static string WriteOnly { set => _ = value; }
}

public class PlainEntity
{
    public long TrunkId { get; set; }
    public double CallsPerHour { get; set; }
    public string Direction { get; set; } = "";
}

public class NullableEntity
{
    public int? OptionalInt { get; set; }
    public DateTime? OptionalDate { get; set; }
    public string? OptionalString { get; set; }
}

// ─── Test ─────────────────────────────────────────────────────────────────────

public class EntityFieldMapperTests
{
    // ─── Mapping base ─────────────────────────────────────────────────────────

    [Fact]
    public void Bindings_PlainEntity_ShouldMapAllReadableProperties()
    {
        var mapper = new EntityFieldMapper<PlainEntity>();

        mapper.Bindings.Select(b => b.ColumnName)
              .Should().BeEquivalentTo("TrunkId", "CallsPerHour", "Direction");
    }

    [Fact]
    public void Bindings_EntityWithWriteOnly_ShouldExcludeWriteOnly()
    {
        var mapper = new EntityFieldMapper<EntityWithWriteOnly>();

        var names = mapper.Bindings.Select(b => b.ColumnName).ToList();
        names.Should().Contain("Id").And.Contain("Keep");
        names.Should().NotContain("WriteOnly");
    }

    // ─── Getter compilati ─────────────────────────────────────────────────────

    [Fact]
    public void ValueGetter_ShouldReturnCorrectValue()
    {
        var mapper = new EntityFieldMapper<PlainEntity>();
        var entity = new PlainEntity
        {
            TrunkId = 42L,
            CallsPerHour = 123.45,
            Direction = "outbound"
        };

        var trunkBinding = mapper.Bindings.Single(b => b.ColumnName == "TrunkId");
        var callsBinding = mapper.Bindings.Single(b => b.ColumnName == "CallsPerHour");
        var dirBinding = mapper.Bindings.Single(b => b.ColumnName == "Direction");

        trunkBinding.ValueGetter(entity).Should().Be(42L);
        callsBinding.ValueGetter(entity).Should().Be(123.45);
        dirBinding.ValueGetter(entity).Should().Be("outbound");
    }

    [Fact]
    public void ValueGetter_NullableProperty_ShouldReturnNullWhenNotSet()
    {
        var mapper = new EntityFieldMapper<NullableEntity>();
        var entity = new NullableEntity(); // tutti null

        var intBinding = mapper.Bindings.Single(b => b.ColumnName == "OptionalInt");
        intBinding.ValueGetter(entity).Should().BeNull();
    }

    [Fact]
    public void ValueGetter_NullableProperty_ShouldReturnValueWhenSet()
    {
        var mapper = new EntityFieldMapper<NullableEntity>();
        var entity = new NullableEntity { OptionalInt = 99, OptionalString = "hello" };

        var intBinding = mapper.Bindings.Single(b => b.ColumnName == "OptionalInt");
        var stringBinding = mapper.Bindings.Single(b => b.ColumnName == "OptionalString");

        intBinding.ValueGetter(entity).Should().Be(99);
        stringBinding.ValueGetter(entity).Should().Be("hello");
    }

    // ─── ColumnType resolution ────────────────────────────────────────────────

    [Fact]
    public void ColumnType_WithDbFields_ShouldUseClickHouseType()
    {
        // Simula i DbField restituiti da system.columns
        DbField[] dbFields =
        [
            new("TrunkId",      false, false, false, typeof(ulong),  null, null, null, null!, false, "TrunkId"),
            new("CallsPerHour", false, false, false, typeof(double), null, null, null, null!, false, "CallsPerHour"),
            new("Direction",    false, false, false, typeof(string), null, null, null, null!, false, "Direction"),
        ];

        var mapper = new EntityFieldMapper<PlainEntity>(dbFields);

        mapper.Bindings.Single(b => b.ColumnName == "TrunkId").ColumnType
              .Should().Be<ulong>(); // CH usa UInt64, non Int64
    }

    [Fact]
    public void ColumnType_WithoutDbFields_ShouldFallbackToPropertyType()
    {
        var mapper = new EntityFieldMapper<PlainEntity>();

        mapper.Bindings.Single(b => b.ColumnName == "TrunkId").ColumnType
              .Should().Be<long>(); // tipo .NET diretto
    }

    [Fact]
    public void ColumnType_NullableProperty_ShouldUnwrapToUnderlyingType()
    {
        var mapper = new EntityFieldMapper<NullableEntity>();

        // int? → int
        mapper.Bindings.Single(b => b.ColumnName == "OptionalInt").ColumnType
              .Should().Be<int>();

        // DateTime? → DateTime
        mapper.Bindings.Single(b => b.ColumnName == "OptionalDate").ColumnType
              .Should().Be<DateTime>();
    }

    // ─── Ignored columns override ─────────────────────────────────────────────

    [Fact]
    public void Constructor_WithIgnoredColumns_ShouldExcludeSpecifiedColumns()
    {
        var mapper = new EntityFieldMapper<PlainEntity>(
            dbFields: null,
            ignoredColumns: ["Direction"]);

        mapper.Bindings.Select(b => b.ColumnName)
              .Should().NotContain("Direction");
        mapper.Bindings.Select(b => b.ColumnName)
              .Should().Contain("TrunkId").And.Contain("CallsPerHour");
    }

    [Fact]
    public void Constructor_IgnoredColumnsCaseInsensitive_ShouldWork()
    {
        var mapper = new EntityFieldMapper<PlainEntity>(
            dbFields: null,
            ignoredColumns: ["direction"]); // lowercase

        mapper.Bindings.Select(b => b.ColumnName)
              .Should().NotContain("Direction");
    }
}
