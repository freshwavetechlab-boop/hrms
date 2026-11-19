using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecurringControlMappingLessonsTests
{
    [Fact]
    public void GrossDependencyLessonRetrievesFromOrdinaryAnonymousFinancialHeaders()
    {
        var request = Request(["Basic", "Total (Gross)", "Workmen Comp/LWF/PT"], ["earning", "grossTotal", "deduction"]);
        request.Columns[2].FormulaDependencies = [1];
        var lesson = Retrieve(request, "Gross Total SUM Workmen Comp LWF PT:");
        Assert.Contains("neither another earning nor an INFO salary input", lesson.Content);
        Assert.Contains("Never substitute the cached gross", lesson.Content);
        Assert.Contains("Literal-only totals", lesson.Content);
        Assert.Contains("Structure proof alone does not certify", lesson.Content);
        AssertPromptPreserves(request, lesson);
    }

    [Fact]
    public void MonetaryContactAllowanceLessonKeepsMonthlyAndEarnedRolesAndIdentifierNearMisses()
    {
        var request = Request(["Telephone Allowance", "Payroll Days", "Payable Days", "Telephone Allowance"], ["info", "info", "info", "earning"]);
        request.Columns[3].FormulaDependencies = [0, 1, 2];
        var lesson = Retrieve(request, "Telephone Allowance Mobile Reimbursement Monthly Earned:");
        Assert.Contains("monthly base as Information", lesson.Content);
        Assert.Contains("only the earned cash output as earning", lesson.Content);
        Assert.Contains("Bare Phone, Telephone Number, Mobile No", lesson.Content);
        Assert.Contains("mixed contact/allowance labels", lesson.Content);
        AssertPromptPreserves(request, lesson);
    }

    private static ExcelPayslipMappingRequest Request(string[] headers, string[] kinds) => new() {
        Columns = headers.Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header,
            Kind = kinds[index], HasFormula = index == headers.Length - 1, FormulaDependencies = index == headers.Length - 1 ? [0] : [] }).ToList() };

    private static ExcelPayslipMappingService.Reference Retrieve(ExcelPayslipMappingRequest request, string prefix)
    {
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []), reference => reference.Title.StartsWith(prefix));
        Assert.True(lesson.Content.Length < 2600);
        return lesson;
    }

    private static void AssertPromptPreserves(ExcelPayslipMappingRequest request, ExcelPayslipMappingService.Reference lesson)
    {
        var before = JsonSerializer.Serialize(request);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, [lesson]));
        Assert.Equal("excel-payroll-mapping-v74", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references")[0].GetProperty("content").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }
}
