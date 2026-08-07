using FluentAssertions;
using RepoDb.ClickHouse.DbHelpers;
using Xunit;

namespace RepoDb.ClickHouse.Tests.Unit.DbHelpers;

public class ClickHouseTypeResolverTests
{
    private readonly ClickHouseDbTypeNameToClientTypeResolver _sut = new();

    // ─── Tipi interi ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UInt8",  typeof(byte))]
    [InlineData("UInt16", typeof(ushort))]
    [InlineData("UInt32", typeof(uint))]
    [InlineData("UInt64", typeof(ulong))]
    [InlineData("Int8",   typeof(sbyte))]
    [InlineData("Int16",  typeof(short))]
    [InlineData("Int32",  typeof(int))]
    [InlineData("Int64",  typeof(long))]
    public void Resolve_IntegerTypes_ShouldReturnCorrectDotNetType(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── Tipi floating point ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Float32", typeof(float))]
    [InlineData("Float64", typeof(double))]
    public void Resolve_FloatTypes_ShouldReturnCorrectDotNetType(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── Decimal ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Decimal")]
    [InlineData("Decimal32")]
    [InlineData("Decimal64")]
    [InlineData("Decimal128")]
    [InlineData("Decimal256")]
    [InlineData("UInt128")]
    [InlineData("UInt256")]
    [InlineData("Int128")]
    [InlineData("Int256")]
    public void Resolve_DecimalAndBigIntTypes_ShouldReturnDecimal(string clickHouseType)
        => _sut.Resolve(clickHouseType).Should().Be<decimal>();

    // ─── String ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("String",      typeof(string))]
    [InlineData("FixedString", typeof(string))]
    [InlineData("IPv4",        typeof(string))]
    [InlineData("IPv6",        typeof(string))]
    public void Resolve_StringTypes_ShouldReturnString(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── Date / Time ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Date",       typeof(DateOnly))]
    [InlineData("Date32",     typeof(DateOnly))]
    [InlineData("DateTime",   typeof(DateTime))]
    [InlineData("DateTime64", typeof(DateTime))]
    public void Resolve_DateTimeTypes_ShouldReturnCorrectDotNetType(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── UUID ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UUID_ShouldReturnGuid()
        => _sut.Resolve("UUID").Should().Be<Guid>();

    // ─── Bool ────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Bool_ShouldReturnBool()
        => _sut.Resolve("Bool").Should().Be<bool>();

    // ─── Nullable(T) unwrap ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Nullable(Int32)",    typeof(int))]
    [InlineData("Nullable(UInt64)",   typeof(ulong))]
    [InlineData("Nullable(DateTime)", typeof(DateTime))]
    [InlineData("Nullable(Float64)",  typeof(double))]
    [InlineData("Nullable(String)",   typeof(string))]
    public void Resolve_NullableWrapper_ShouldUnwrapAndResolveInnerType(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── LowCardinality(T) unwrap ─────────────────────────────────────────────

    [Theory]
    [InlineData("LowCardinality(String)", typeof(string))]
    [InlineData("LowCardinality(Int32)",  typeof(int))]
    public void Resolve_LowCardinalityWrapper_ShouldUnwrapAndResolveInnerType(string clickHouseType, Type expected)
        => _sut.Resolve(clickHouseType).Should().Be(expected);

    // ─── Array(T) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Array(String)")]
    [InlineData("Array(Int64)")]
    [InlineData("Array(UInt32)")]
    public void Resolve_ArrayType_ShouldReturnArray(string clickHouseType)
        => _sut.Resolve(clickHouseType).Should().Be<Array>();

    // ─── Tipi sconosciuti ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("SomeUnknownType")]
    [InlineData("Map(String, Int32)")]
    [InlineData("Tuple(Int32, String)")]
    public void Resolve_UnknownType_ShouldReturnObject(string clickHouseType)
        => _sut.Resolve(clickHouseType).Should().Be<object>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrEmpty_ShouldReturnObject(string? clickHouseType)
        => _sut.Resolve(clickHouseType!).Should().Be<object>();
}
