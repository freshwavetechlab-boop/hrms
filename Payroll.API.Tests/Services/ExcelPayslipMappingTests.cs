using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMappingTests
{
    private static ExcelPayslipMappingRequest Request() => new() { Columns = [
        new() { ColumnIndex = 0, SourceHeader = "Employee name", Label = "Employee name", Kind = "employeeName" },
        new() { ColumnIndex = 1, SourceHeader = "Performance reward", Label = "Performance reward" },
        new() { ColumnIndex = 2, SourceHeader = "Monthly BASIC", Label = "Monthly BASIC", Kind = "info" },
        new() { ColumnIndex = 3, SourceHeader = "Take home", Label = "Take home", Kind = "netPay" },
        new() { ColumnIndex = 4, SourceHeader = "PF employer", Label = "PF employer", Kind = "employer" },
        new() { ColumnIndex = 5, SourceHeader = "UAN", Label = "UAN" },
        new() { ColumnIndex = 6, SourceHeader = "Increment CTC", Label = "Increment CTC" },
        new() { ColumnIndex = 7, SourceHeader = "Deduction subtotal", Label = "Deduction subtotal", Kind = "deductionTotal" }
    ] };
    private static ExcelPayslipMappingResult Apply(string json, ExcelPayslipMappingRequest? request = null, ExcelPayslipTemplate? template = null) =>
        ExcelPayslipMappingService.ApplyResponse(request ?? Request(), template, [], JsonDocument.Parse(json).RootElement, "test-model");

    [Fact]
    public void UnknownEarningCanBeSuggestedAndPartialSubtotalDemotedWithoutChangingSource()
    {
        var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = Apply("""{"columns":[{"columnIndex":1,"kind":"earning","confidence":0.96},{"columnIndex":7,"kind":"info","confidence":0.99}]}""", request);
        Assert.Equal("earning", result.Columns[1].Kind); Assert.Equal("info", result.Columns[7].Kind);
        Assert.Equal("netPay", result.Columns[3].Kind); Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("ai", result.Method); Assert.Contains(1, result.ReviewColumnIndexes); Assert.Contains(7, result.ReviewColumnIndexes);
        Assert.DoesNotContain("blankAsZero", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(0, "deduction")]
    [InlineData(2, "earning")]
    [InlineData(3, "ignore")]
    [InlineData(4, "deduction")]
    [InlineData(5, "earning")]
    [InlineData(6, "earning")]
    public void AiCannotPromoteKnownIdentifiersInputsBillingOrReclassifyEmployerCosts(int index, string kind)
    {
        var result = Apply($$"""{"columns":[{"columnIndex":{{index}},"kind":"{{kind}}","confidence":1}]}""");
        Assert.Equal(Request().Columns[index].Kind, result.Columns[index].Kind);
        Assert.Contains(index, result.ReviewColumnIndexes);
    }

    [Theory]
    [InlineData("{\"columns\":{}}")]
    [InlineData("{\"columns\":[{\"columnIndex\":99,\"kind\":\"earning\",\"confidence\":1}]}")]
    [InlineData("{\"columns\":[{\"columnIndex\":1,\"kind\":\"sql\",\"confidence\":1}]}")]
    [InlineData("{\"columns\":[{\"columnIndex\":1,\"kind\":\"earning\",\"confidence\":1},{\"columnIndex\":1,\"kind\":\"earning\",\"confidence\":1}]}")]
    [InlineData("{\"columns\":[{\"columnIndex\":1,\"kind\":\"earning\",\"confidence\":2}]}")]
    public void InvalidModelOutputCannotBeApplied(string json) => Assert.Throws<InvalidOperationException>(() => Apply(json));

    [Fact]
    public void LowConfidenceAndDuplicateNetRemainForReview()
    {
        var result = Apply("""{"columns":[{"columnIndex":1,"kind":"earning","confidence":0.3},{"columnIndex":6,"kind":"netPay","confidence":1}]}""");
        Assert.Equal("ignore", result.Columns[1].Kind); Assert.Equal("ignore", result.Columns[6].Kind);
        var request = Request(); request.Columns[6].SourceHeader = "Final payable";
        result = Apply("""{"columns":[{"columnIndex":6,"kind":"netPay","confidence":1}]}""", request);
        Assert.Single(result.Columns, c => c.Kind == "netPay");
        Assert.Contains(6, result.ReviewColumnIndexes);
    }

    [Fact]
    public void TemplateComponentMustBeFromTheSelectedTemplateAndHaveMatchingCategory()
    {
        var template = new ExcelPayslipTemplate { Id = "t", Components = [new() { Id = "reward", Name = "Performance pay", Category = "Earning" }, new() { Id = "tax", Category = "Deduction" }] };
        var result = Apply("""{"columns":[{"columnIndex":1,"kind":"earning","confidence":1,"componentId":"reward"}]}""", template: template);
        Assert.Equal("reward", result.Columns[1].ComponentId); Assert.Equal("Performance pay", result.Columns[1].Label);
        foreach (var id in new[] { "foreign-client-component", "tax" })
        {
            result = Apply($$"""{"columns":[{"columnIndex":1,"kind":"earning","confidence":1,"componentId":"{{id}}"}]}""", template: template);
            Assert.Null(result.Columns[1].ComponentId); Assert.Contains(1, result.ReviewColumnIndexes);
        }
    }

    [Fact]
    public void ProviderPromptRedactsIdentifiersAndOmitsSourceAmountsFormulasAndPresetLabels()
    {
        var request = Request(); request.Columns[1].SourceHeader = "Reward test@example.test 001234567890 ABCDE1234F";
        request.Columns[1].Label = "private person"; request.Columns[1].HasFormula = true; request.Columns[1].FormulaDependencies = [2, 3];
        var examples = new[] { new ExcelPayslipMappingExample("x", [new() { SourceHeader = "test@example.test 001234567890 Reward", Kind = "earning", Label = "private employee" }]) };
        var refs = ExcelPayslipMappingService.Retrieve(request.Columns, "## Classification contract\nDo not change data.\n## Reward\nReward is earning.", examples);
        var prompt = ExcelPayslipMappingService.BuildPrompt(request, null, refs);
        Assert.Contains("dependencies", prompt); Assert.DoesNotContain("test@example.test", prompt); Assert.DoesNotContain("001234567890", prompt);
        Assert.DoesNotContain("ABCDE1234F", prompt); Assert.DoesNotContain("private person", prompt); Assert.DoesNotContain("private employee", prompt);
        Assert.DoesNotContain("formulaText", prompt); Assert.DoesNotContain("blankAsZero", prompt); Assert.DoesNotContain("salaryTemplateId", prompt);
    }

    [Fact]
    public void ValidationRejectsLargeInputsUntrustedFunctionsBadIndexesAndDependencies()
    {
        var request = Request(); ExcelPayslipMappingService.ValidateRequest(request);
        request.Columns[1].FormulaFunctions = ["HYPERLINK"]; Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
        request = Request(); request.Columns[1].FormulaDependencies = [100]; Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
        request = Request(); request.Columns[1].ColumnIndex = 0; Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
        request = Request(); request.Columns[1].SourceHeader = new('x', 513); Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
    }

    [Fact]
    public void RetrievalIncludesRelevantVerifiedCasesSafetyContractAndOnlySuppliedClientPresets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        Assert.True(File.Exists(path), "Knowledge must ship with published API artifacts.");
        var request = Request(); request.Columns[1].SourceHeader = "NHIDCL hardship allowance Income Tax TDS deduction subtotal";
        var refs = ExcelPayslipMappingService.Retrieve(request.Columns, File.ReadAllText(path), []);
        Assert.Contains(refs, r => r.Title.Contains("TDS")); Assert.Contains(refs, r => r.Title.Contains("Classification contract"));
        Assert.DoesNotContain(refs, r => r.Source == "client-saved-mapping");
    }

    [Fact]
    public void MappingMemoryUsesValidatedPresetMetadataAndPrefersScopedRecordOverLegacy()
    {
        var signature = new string('a', 64);
        string Preset(string label) => JsonSerializer.Serialize(new Dictionary<string, object> {
            [signature] = new { headerSignature = signature, columns = new[] { new { columnIndex = 0, sourceHeader = "Employee name", label, kind = "employeeName" } } }
        });
        var examples = ExcelPayslipRepository.ReadMappingExamples(["not json", Preset("Scoped"), Preset("Legacy")]);
        Assert.Equal("Scoped", Assert.Single(examples).Columns[0].Label);
        Assert.Empty(ExcelPayslipRepository.ReadMappingExamples(["{\"not-a-signature\":{\"employeeName\":\"Do not retrieve salary rows\"}}"]));
    }

    [Fact]
    public void AttendanceLookupEvidenceIsRetainedForReviewWithoutPromotingDaysToPay()
    {
        var request = Request();
        request.Columns[2].SourceHeader = "Payable days";
        request.Columns[2].HasFormula = true;
        request.Columns[2].FormulaFunctions = ["IFERROR", "VLOOKUP", "INDEX", "MATCH"];
        request.Columns[2].CrossSheetDependencies = [new() { SheetIndex = 1, SourceType = "attendance", Status = "supplied", ColumnIndexes = [0, 1, 2, 3] }];
        ExcelPayslipMappingService.ValidateRequest(request);
        var before = JsonSerializer.Serialize(request);
        var result = Apply("""{"columns":[{"columnIndex":2,"kind":"earning","confidence":1}]}""", request);
        Assert.Equal("info", result.Columns[2].Kind);
        Assert.Contains(2, result.ReviewColumnIndexes);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        // Even an unchanged suggestion requires review of the linked attendance input.
        Assert.Contains(2, Apply("""{"columns":[]} """, request).ReviewColumnIndexes);
        var prompt = ExcelPayslipMappingService.BuildPrompt(request, null, []);
        Assert.Contains("crossSheetDependencies", prompt);
        Assert.Contains("attendance", prompt); Assert.Contains("VLOOKUP", prompt);
        Assert.DoesNotContain("sourceRow", prompt); Assert.DoesNotContain("formulaText", prompt);
    }

    [Theory]
    [InlineData("supplied", -1, "attendance", 3)]
    [InlineData("missing", 1, "attendance", 3)]
    [InlineData("resolved-by-cache", -1, "attendance", 3)]
    [InlineData("missing", -1, "private@example.test", 3)]
    [InlineData("missing", -1, "unknown", 512)]
    public void DependencyMetadataCannotCarryUnboundedOrInventedResolution(string status, int sheet, string type, int column)
    {
        var request = Request();
        request.Columns[1].CrossSheetDependencies = [new() { SheetIndex = sheet, Status = status, SourceType = type, ColumnIndexes = [column] }];
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
    }

    [Theory]
    [InlineData("grossTotal", "earning")]
    [InlineData("deductionTotal", "deduction")]
    public void FormulaTotalsCannotBeAddedAgainAsPay(string baseline, string proposed)
    {
        var request = Request(); request.Columns[7].Kind = baseline; request.Columns[7].HasFormula = true;
        var result = Apply($$"""{"columns":[{"columnIndex":7,"kind":"{{proposed}}","confidence":1}]}""", request);
        Assert.Equal(baseline, result.Columns[7].Kind); Assert.Contains(7, result.ReviewColumnIndexes);
    }

    [Fact]
    public void CrossSheetKnowledgeIsAlwaysRetrievedEvenWhenHeaderVocabularyRanksOtherPatternsHigher()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var request = Request();
        request.Columns[1].CrossSheetDependencies = [new() { SheetIndex = -1, SourceType = "unknown", Status = "external-review", ColumnIndexes = [0] }];
        ExcelPayslipMappingService.ValidateRequest(request);
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, File.ReadAllText(path), []);
        Assert.Contains(references, r => r.Title.StartsWith("Cross-sheet formula inputs"));
        Assert.Contains(1, Apply("""{"columns":[]} """, request).ReviewColumnIndexes);
    }
}
