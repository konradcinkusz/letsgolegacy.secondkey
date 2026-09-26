using SecondKey.Replay.Probes;

namespace SecondKey.Replay.Tests;

public class ProbeValueTests
{
    public static TheoryData<object, string> Values => new()
    {
        { DBNull.Value, "null" },
        { "x", "\"x\"" },
        { true, "true" },
        { (byte)1, "1" },
        { (short)2, "2" },
        { 3, "3" },
        { 4L, "4" },
        { 5.25m, "5.25" },
        { 0.5d, "0.5" },
        { 0.25f, "0.25" },
        { new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc), "\"2026-09-26T10:00:00.0000000Z\"" },
        { new Guid("6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40"), "\"6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40\"" },
        { new byte[] { 1, 2, 3 }, "\"AQID\"" },
        { TimeSpan.FromMinutes(90), "\"01:30:00\"" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Database_values_become_json_without_losing_meaning(object value, string json)
    {
        var node = SqlServerTableProbe.ToJson(value);

        Assert.Equal(json, node is null ? "null" : node.ToJsonString());
    }

    [Fact]
    public void An_offset_is_kept_with_the_time()
    {
        var node = SqlServerTableProbe.ToJson(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal("2026-09-26T10:00:00.0000000+02:00", node!.GetValue<string>());
    }

    [Theory]
    [InlineData("Orders", "[Orders]")]
    [InlineData("dbo.Orders", "[dbo].[Orders]")]
    public void Table_names_are_quoted_per_part(string table, string quoted)
    {
        Assert.Equal(quoted, SqlServerTableProbe.QuoteTable(table));
    }

    [Fact]
    public void Diffing_null_snapshots_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => SqlServerTableProbe.Diff(null!, null!));
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerTableProbe("", []));
        Assert.Throws<ArgumentNullException>(() => new SqlServerTableProbe("Server=x", null!));
    }
}
