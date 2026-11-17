using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class DailyRateMappingLessonTests
{
    [Fact]
    public void AnonymousDailyRateDependenciesRetrieveUnitAndProvenanceBoundariesWithoutChangingSource()
    {
        var headers = new[] { "Daily Rate", "Reference Wage", "Payable Days", "Earned Wage", "ESI", "Employer Admin" };
        var request = new ExcelPayslipMappingRequest { Columns = headers.Select((h, i) => new ExcelPayslipMappingColumn
            { ColumnIndex = i, SourceHeader = h, Kind = i == 3 ? "earning" : i == 4 ? "deduction" : i == 5 ? "employer" : "info",
                HasFormula = i is 1 or 3 or 4 or 5,
                FormulaDependencies = i switch { 1 => [0], 3 => [1, 2], 4 => [0, 3], 5 => [1], _ => [] } }).ToList() };
        var before = JsonSerializer.Serialize(request);
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, File.ReadAllText(path), []);
        var lesson = Assert.Single(references, x => x.Title.StartsWith("Daily Rate Daily Wage Reference Wage"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("only earned wages as earning", lesson.Content);
        Assert.Contains("CODE_MONTHLY/PAYROLL_DAYS", lesson.Content);
        Assert.Contains("not cancelable literals", lesson.Content);
        Assert.Contains("cached result is never a rule", lesson.Content);
        Assert.Contains("Unknown actual presence stays unknown", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v46", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(x => x.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }
}
