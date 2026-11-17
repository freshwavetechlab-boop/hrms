using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Tests.Repositories;

public sealed class EssAttendanceSummaryReaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("12.5")]
    public void Database_presence_retains_unknown_zero_and_known_days_in_json(string? presentText)
    {
        decimal? expected = presentText is null ? null : decimal.Parse(presentText, CultureInfo.InvariantCulture);
        using var table = new DataTable();
        table.Columns.Add("Month", typeof(string));
        table.Columns.Add("PresentDays", typeof(decimal)).AllowDBNull = true;
        table.Columns.Add("PayableDays", typeof(decimal));
        table.Columns.Add("TotalWorkingDays", typeof(int));
        table.Rows.Add("2026-10", expected.HasValue ? (object)expected.Value : DBNull.Value, 21.5m, 31);

        using var reader = table.CreateDataReader();
        var parse = reader.GetRowParser<EssAttendanceSummary>();
        Assert.True(reader.Read());
        var summary = parse(reader);

        Assert.Equal<decimal?>(expected, summary.PresentDays);
        Assert.Equal("2026-10", summary.Month);
        Assert.Equal(21.5m, summary.PayableDays);
        Assert.Equal(31, summary.TotalWorkingDays);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var transport = JsonSerializer.SerializeToElement(summary, options);
        var actualPresence = transport.GetProperty("presentDays");
        if (expected.HasValue)
            Assert.Equal(expected.Value, actualPresence.GetDecimal());
        else
            Assert.Equal(JsonValueKind.Null, actualPresence.ValueKind);

        var roundTrip = transport.Deserialize<EssAttendanceSummary>(options)!;
        Assert.Equal(expected, roundTrip.PresentDays);
        Assert.Equal(summary.PayableDays, roundTrip.PayableDays);
        Assert.Equal(summary.TotalWorkingDays, roundTrip.TotalWorkingDays);
    }
}
