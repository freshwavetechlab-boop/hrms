using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollSumMappingTests
{
    [Fact]
    public void OvertimeHoursLessonKeepsIndependentQuantityAndMonthlyInputsOutOfEarnings()
    {
        var headers = new[] { "Fixed Pay", "HRA", "OT Hours", "Overtime" };
        var request = new ExcelPayslipMappingRequest { Columns = headers.Select((header, index) => new ExcelPayslipMappingColumn {
            ColumnIndex = index, SourceHeader = header, Kind = index == 3 ? "earning" : "info",
            HasFormula = index == 3, FormulaDependencies = index == 3 ? [2] : [] }).ToList() };
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []),
            reference => reference.Title.StartsWith("Fixed Pay HRA OT Hours Overtime:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("Monthly inputs and hours stay Information", lesson.Content);
        Assert.Contains("truthful Hours units", lesson.Content);
        Assert.Contains("Zero hours is a supplied quantity", lesson.Content);
        Assert.Contains("Do not make a template for each hours quantity", lesson.Content);
        Assert.Contains("extra-duty days with different rate/policy meaning", lesson.Content);
        var before = JsonSerializer.Serialize(request);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, [lesson]));
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references")[0].GetProperty("content").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void SumLessonRetrievesFromStructuralHeadingsWithoutClientOrFilenameAndKeepsProofBoundaries()
    {
        var request = new ExcelPayslipMappingRequest { Columns = new[] { "Basic", "Gross Total", "Employee PF cap", "Employer PF cap" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header,
                Kind = index == 0 ? "earning" : index == 2 ? "deduction" : index == 3 ? "employer" : "info", HasFormula = index >= 2,
                FormulaDependencies = index >= 2 ? [0] : [] }).ToList() };
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, reference => reference.Title.StartsWith("PF Employer Gross Cap SUM:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("Retain the function and its arguments", lesson.Content);
        Assert.Contains("not a mapping role or salary component", lesson.Content);
        Assert.Contains("unsupported nested functions", lesson.Content);
        Assert.Contains("does not certify source-cache arithmetic", lesson.Content);
        var before = JsonSerializer.Serialize(request);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v74", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(reference => reference.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }
}
