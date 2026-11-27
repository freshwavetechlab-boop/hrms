using System.Globalization;
using System.Text.Json;
using Payroll.API.Repositories;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class EmployeeImportDateTests
{
    [Fact]
    public void Date_format_lesson_is_retrieved_from_headers_without_employee_or_client_data()
    {
        var columns = new[] { "Employee Code", "Date Of Joining", "Date Of Birth" }.Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "info", HasNumbers = true }).ToArray();
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, knowledge, []), row => row.Title.StartsWith("Date Of Joining Date Of Birth:"));
        Assert.True(lesson.Content.Length < 2600); Assert.Contains("not a salary classification", lesson.Content);
        Assert.Contains("Preserve every employee code", lesson.Content); Assert.Contains("1930–2029", lesson.Content);
        Assert.Equal("excel-payroll-mapping-v84", ExcelPayslipMappingService.KnowledgeVersion);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-IN")]
    [InlineData("hi-IN")]
    public void Actual_employee_save_parser_matches_preview_independently_of_server_locale(string culture)
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var matrix = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "employee-import-dates.json")));
            foreach (var item in matrix.RootElement.EnumerateArray())
            {
                var expected = item.GetProperty("expected");
                var valid = EmployeeRepository.TryDate(item.GetProperty("input").GetString()!, out var parsed);
                Assert.Equal(expected.ValueKind != JsonValueKind.Null, valid);
                if (valid) Assert.Equal(expected.GetString(), parsed);
            }
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
}
