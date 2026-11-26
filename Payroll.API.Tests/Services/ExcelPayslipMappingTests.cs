using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMappingTests
{
    private static ExcelPayslipMappingRequest RevisedBasicFixture(bool distinctHeadings = false) => new() { Columns = [
        new() { ColumnIndex = 0, SourceHeader = distinctHeadings ? "Original Basic" : "Basic", Kind = "info", HasNumbers = true },
        new() { ColumnIndex = 1, SourceHeader = "Increment", Kind = "info", HasNumbers = true, HasBlanks = true, HasFormula = true, FormulaDependencies = [0] },
        new() { ColumnIndex = 2, SourceHeader = distinctHeadings ? "Revised Basic" : "Basic", Kind = "info", HasNumbers = true, HasFormula = true, FormulaDependencies = [0, 1] },
        new() { ColumnIndex = 3, SourceHeader = "Total Days", Kind = "info", HasNumbers = true },
        new() { ColumnIndex = 4, SourceHeader = "Payable Days", Kind = "info", HasNumbers = true },
        new() { ColumnIndex = 5, SourceHeader = distinctHeadings ? "Earned Basic" : "Basic", Kind = "earning", HasNumbers = true, HasFormula = true, FormulaDependencies = [2, 3, 4] },
        new() { ColumnIndex = 6, SourceHeader = "Employee PF", Kind = "deduction", HasNumbers = true, HasFormula = true, FormulaDependencies = [5], FormulaFunctions = ["IF"] },
        new() { ColumnIndex = 7, SourceHeader = "Employee ESIC", Kind = "deduction", HasNumbers = true, HasFormula = true, FormulaDependencies = [2, 5], FormulaFunctions = ["IF"] },
        new() { ColumnIndex = 8, SourceHeader = "Employer PF", Kind = "employer", HasNumbers = true, HasFormula = true, FormulaDependencies = [2], FormulaFunctions = ["IF"] },
        new() { ColumnIndex = 9, SourceHeader = "Employer ESIC", Kind = "employer", HasNumbers = true, HasFormula = true, FormulaDependencies = [2, 5], FormulaFunctions = ["IF"] },
        new() { ColumnIndex = 10, SourceHeader = "Total Deductions", Kind = "deductionTotal", HasNumbers = true, HasFormula = true, FormulaDependencies = [6, 7] },
        new() { ColumnIndex = 11, SourceHeader = "Net Pay", Kind = "netPay", HasNumbers = true, HasFormula = true, FormulaDependencies = [5, 10] },
        new() { ColumnIndex = 12, SourceHeader = "ESIC", Kind = "info", HasBlanks = true },
    ] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisedBasicContributionLessonUsesDependenciesWithoutClientNamesOrSalaryData(bool distinctHeadings)
    {
        var request = RevisedBasicFixture(distinctHeadings);
        var before = JsonSerializer.Serialize(request);
        ExcelPayslipMappingService.ValidateRequest(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, reference => reference.Title.StartsWith("Original Basic Increment Revised Basic Earned Basic PF ESIC:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("two Information columns share a Basic heading", lesson.Content);
        Assert.Contains("retain its supported native IF", lesson.Content);
        Assert.Contains("Never freeze its current branch", lesson.Content);
        Assert.Contains("alias a Basic operand to MONTHLY_CTC", lesson.Content);
        Assert.Contains("blank/zero actual ESIC output used by those controls remains the deduction", lesson.Content);
        Assert.Contains("Mapping suggestions cannot approve", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray().Single(reference => reference.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        Assert.False(prompt.RootElement.TryGetProperty("rows", out _));
        using var response = JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1},{"columnIndex":2,"kind":"earning","confidence":1},{"columnIndex":12,"kind":"deduction","confidence":1}],"recurringBlankInputReview":{"version":2,"reviewed":true}}""");
        var result = ExcelPayslipMappingService.ApplyResponse(request, null, references, response.RootElement, "synthetic");
        Assert.Equal(request.Columns.Select(column => column.Kind), result.Columns.Select(column => column.Kind));
        Assert.Contains(0, result.ReviewColumnIndexes); Assert.Contains(2, result.ReviewColumnIndexes); Assert.Contains(12, result.ReviewColumnIndexes);
        Assert.Equal(new[] { 6, 7 }, request.Columns[10].FormulaDependencies);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("unrelated-basis")]
    [InlineData("wrong-earned-dependency")]
    [InlineData("no-monthly-conditional")]
    [InlineData("incomplete")]
    [InlineData("external-increment")]
    public void RevisedBasicLessonDoesNotActivateFromSimilarHeadersOrUnresolvedGraphs(string kind)
    {
        var request = RevisedBasicFixture();
        switch (kind)
        {
            case "unrelated-basis": request.Columns[2].FormulaDependencies = [1]; break;
            case "wrong-earned-dependency": request.Columns[5].FormulaDependencies = [0, 3, 4]; break;
            case "no-monthly-conditional": foreach (var column in request.Columns.Where(column => column.Kind is "deduction" or "employer")) column.FormulaDependencies = [5]; break;
            case "incomplete": request.Columns[2].DependencyEvidenceIncomplete = true; break;
            case "external-increment": request.Columns[1].CrossSheetDependencies = [new() { SheetIndex = -1, SourceType = "other", Status = "missing", ColumnIndexes = [0] }]; break;
        }
        var before = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        Assert.DoesNotContain(references, reference => reference.Title.StartsWith("Original Basic Increment Revised Basic Earned Basic PF ESIC:"));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void MonthlyDeductionEmployerHelpersRetrieveTheirFormulaBoundaryWithoutEmployeeData()
    {
        var headers = new[] { "Monthly PF", "PF", "Monthly PT", "PT", "Monthly Employer PF", "Employer PF", "Payroll days", "Payable days", "Net pay" };
        var columns = headers.Select((header, i) => new ExcelPayslipMappingColumn { ColumnIndex = i, SourceHeader = header, HasNumbers = true,
            Kind = i is 1 or 3 ? "deduction" : i == 5 ? "employer" : i == 8 ? "netPay" : "info", HasFormula = i is 0 or 1 or 3 or 4 or 5 or 8,
            FormulaDependencies = i is 1 or 3 or 5 ? [i - 1] : [] }).ToList();
        var before = JsonSerializer.Serialize(columns);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, knowledge, []), reference => reference.Title.StartsWith("Monthly PF Monthly PT Employer PF:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("preserve employee versus employer identity", lesson.Content);
        Assert.Contains("no second attendance proration", lesson.Content);
        Assert.Contains("Anchored A1 references remain captured cells", lesson.Content);
        Assert.Contains("employee-specific sample bases stay blank", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(new() { Columns = columns }, null, [lesson]));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(columns));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("ignore")]
    public void MonthlyBasicBasicPayInputAliasBoundaryIsRetrievedFromHeadersAndDependencies(string inputKind)
    {
        var request = new ExcelPayslipMappingRequest { Columns = [
            new() { ColumnIndex = 0, SourceHeader = "Monthly Basic", Kind = inputKind, HasNumbers = true },
            new() { ColumnIndex = 1, SourceHeader = "Total Days", Kind = "info", HasNumbers = true },
            new() { ColumnIndex = 2, SourceHeader = "Payable Days", Kind = "info", HasNumbers = true },
            new() { ColumnIndex = 3, SourceHeader = "Basic Pay", Kind = "earning", HasNumbers = true, HasFormula = true, FormulaDependencies = [0, 1, 2], FormulaFunctions = ["ROUND"] },
            new() { ColumnIndex = 4, SourceHeader = "Other cost", Kind = "ignore", HasNumbers = true },
            new() { ColumnIndex = 5, SourceHeader = "Net Pay", Kind = "netPay", HasNumbers = true, HasFormula = true, FormulaDependencies = [3] }
        ] };
        var before = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, reference => reference.Title.StartsWith("Monthly Basic Basic Pay: dependency-bound blank template inputs"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("Both native bindings use the same component code", lesson.Content);
        Assert.Contains("A shared zero default is still nonblank", lesson.Content);
        Assert.Contains("unrelated cost without a direct formula dependency", lesson.Content);
        Assert.Contains("source employee's amount is a temporary simulation input", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray().Single(reference => reference.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
        if (inputKind == "info")
        {
            using var proposed = JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1}]}""");
            var applied = ExcelPayslipMappingService.ApplyResponse(request, null, references, proposed.RootElement, "synthetic");
            Assert.Equal("info", applied.Columns[0].Kind);
            Assert.Equal("earning", applied.Columns[3].Kind);
            Assert.Equal("ignore", applied.Columns[4].Kind);
            Assert.Contains(0, applied.ReviewColumnIndexes);
        }
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void MonthlyInputAliasRetrievalHookDoesNotTreatAnUnrelatedCostAsASalaryDependency()
    {
        const string knowledge = "## Classification contract\nKeep source information separate.\n\n## Monthly Basic Basic Pay: dependency-bound blank template inputs\nMonthly alias guidance.";
        var columns = new List<ExcelPayslipMappingColumn> {
            new() { ColumnIndex = 0, SourceHeader = "Monthly Basic", Kind = "info", HasNumbers = true },
            new() { ColumnIndex = 1, SourceHeader = "Total Days", Kind = "info", HasNumbers = true },
            new() { ColumnIndex = 2, SourceHeader = "Payable Days", Kind = "info", HasNumbers = true },
            new() { ColumnIndex = 3, SourceHeader = "Basic Pay", Kind = "earning", HasFormula = true, FormulaDependencies = [4] },
            new() { ColumnIndex = 4, SourceHeader = "Other cost", Kind = "info", HasNumbers = true }
        };
        var before = JsonSerializer.Serialize(columns);
        var references = ExcelPayslipMappingService.Retrieve(columns, knowledge, []);
        Assert.DoesNotContain(references, reference => reference.Source == "excel-payroll-mapping.md#monthly-input-aliases");
        Assert.Equal(before, JsonSerializer.Serialize(columns));
    }

    [Fact]
    public void ConditionalTemplatePreviewBoundaryIsRetrievedWithoutClientNamesOrEmployeeValues()
    {
        var columns = new[] { "Basic", "HRA", "ESIC", "PT", "Source subtotal", "Payable Days", "Formula" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = index is 2 or 3 ? "deduction" : "info", HasNumbers = true, HasFormula = index is 2 or 3 }).ToList();
        var before = JsonSerializer.Serialize(columns);
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []), reference => reference.Title.StartsWith("Basic HRA ESIC PT Subtotal:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("frontend arithmetic-only evaluator", lesson.Content);
        Assert.Contains("function names are not missing salary components", lesson.Content);
        Assert.Contains("must not turn missing inputs", lesson.Content);
        Assert.Contains("not a new mapping role", lesson.Content);
        Assert.Equal(before, JsonSerializer.Serialize(columns));
    }

    [Fact]
    public void ProfessionalTaxLessonKeepsSourceDeductionSeparateFromAnUnmappedAutomaticCharge()
    {
        var request=new ExcelPayslipMappingRequest{Columns=new[]{"Professional Tax","P.Tax","Period deduction","Payable fee"}.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind="deduction",HasNumbers=true,HasFormula=true}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Professional Tax P.Tax deduction:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("statutory type Professional Tax",lesson.Content);Assert.Contains("statutory type None remains separate",lesson.Content);Assert.Contains("Never force a PT-zero control",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void ReportedSourceSnapshotLessonIsRetrievedWithoutChangingFinancialRoles()
    {
        var columns=new[]{"Old Basic","New Basic","Net Working Days","Wages","Special Allowance"}
            .Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind="info",HasNumbers=true}).ToList();
        var before=JsonSerializer.Serialize(columns);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var refs=ExcelPayslipMappingService.Retrieve(columns,knowledge,[]);
        var lesson=Assert.Single(refs,r=>r.Title.StartsWith("Old Basic New Basic Net Working Days: reported source snapshot"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("same complete typed source snapshot",lesson.Content);
        Assert.Contains("intentionally omits profile links",lesson.Content);
        Assert.Contains("not new payroll arithmetic",lesson.Content);
        Assert.Contains("still needs a fresh exact review",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(new(){Columns=columns},null,refs));
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(columns));
    }

    [Fact]
    public void ClosedPriorSalaryLessonKeepsCurrentBudgetIndependentAndRequiresSourceBoundReview()
    {
        var headers = new[] { "Current CTC", "Last Month Salary", "Payroll days", "Payable days", "Bonus", "Basic" };
        var request = new ExcelPayslipMappingRequest { Columns = headers.Select((header, index) => new ExcelPayslipMappingColumn
        {
            ColumnIndex = index, SourceHeader = header, Kind = index >= 4 ? "earning" : "info", HasNumbers = true,
            HasFormula = index == 5, FormulaDependencies = index == 5 ? [1, 2, 3, 4] : []
        }).ToList() };
        var original = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, item => item.Title.StartsWith("Current CTC Last Month Salary Basic Payable Days Bonus:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("separate Information dependencies", lesson.Content);
        Assert.Contains("closed month's own source literal", lesson.Content);
        Assert.Contains("current and future Master/history", lesson.Content);
        Assert.Contains("A changed prior literal invalidates the rule proof", lesson.Content);
        Assert.Contains("creates no named-input profile", lesson.Content);
        Assert.Contains("acknowledge the exact complete component/total ledger", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void WorkLocationStatePtLessonRequiresActualJurisdictionAndPreservesSourceAndStatusReview()
    {
        var columns=new[]{"Work Location","State","PT","Professional Tax","Net Pay"}.Select((header,index)=>
            new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind=index<2?"info":index==4?"netPay":"deduction"}).ToList();
        var request=new ExcelPayslipMappingRequest{Columns=columns};
        var original=JsonSerializer.Serialize(request);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Work Location State PT Professional Tax:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("State field is blank",lesson.Content);
        Assert.Contains("including personal-state inputs",lesson.Content);
        Assert.Contains("preserve its ID, name and other fields",lesson.Content);
        Assert.Contains("missing actual attendance must never become zero",lesson.Content);
        Assert.Contains("reactivate a cancelled record automatically",lesson.Content);
        Assert.Contains("unknown state is not a tax exemption",lesson.Content);
        Assert.Contains("jurisdiction or gender",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal(original,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void SourceWholePtAndApprovedPeriodExtraLessonRequiresNativeProofAndPreservesManualCharges()
    {
        var headers=new[]{"PT","Professional Tax","Approved period adjustment","Source net","Gross"};
        var columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind=index<3?"deduction":index==3?"netPay":"grossTotal"}).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("PT Professional Tax Approved period adjustment:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("Template-only arithmetic does not include the existing native charge",lesson.Content);
        Assert.Contains("including tax, with only that exact extra excluded",lesson.Content);
        Assert.Contains("preserves the original row, amount and history",lesson.Content);
        Assert.Contains("cancel a manual/different charge or an applied transaction",lesson.Content);
        Assert.Contains("missing actual attendance cannot become zero",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(new(){Columns=columns},null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
    }
    [Fact]
    public void SourceWithholdingDiagnosticIsRetrievedWithoutTurningBlankTdsIntoAnExemption()
    {
        var headers = new[] { "TDS", "Income Tax", "Withholding", "Fixed Amount", "Payable Days" };
        var request = new ExcelPayslipMappingRequest { Columns = headers.Select((h, i) =>
            new ExcelPayslipMappingColumn { ColumnIndex = i, SourceHeader = h, Kind = "info", HasNumbers = true }).ToList() };
        var before = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, reference => reference.Title.StartsWith("TDS Income Tax Withholding:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("closed to one payroll month", lesson.Content);
        Assert.Contains("Annual tax liability", lesson.Content);
        Assert.Contains("unreviewed blank", lesson.Content);
        Assert.Contains("must not be frozen", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void NumericMonthlyEntryExpressionsAreRetrievedWithoutFreezingReferencesOrChangingAmounts()
    {
        var headers = new[] { "Other allowance", "Monthly input", "Rate", "Numeric arithmetic", "Payable Days" };
        var request = new ExcelPayslipMappingRequest { Columns = headers.Select((h, i) =>
            new ExcelPayslipMappingColumn { ColumnIndex = i, SourceHeader = h, Kind = "info", HasNumbers = true }).ToList() };
        var before = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, r => r.Title.StartsWith("Other allowance Monthly input Rate Numeric arithmetic:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("independently evaluated", lesson.Content);
        Assert.Contains("exact cache equality", lesson.Content);
        Assert.Contains("employee-specific templates", lesson.Content);
        Assert.Contains("=A7+B7", lesson.Content);
        Assert.Contains("four-decimal", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void NullableSingleNameMasterDiagnosticIsRetrievedWithoutApprovingIdentityChanges()
    {
        var headings = new[] { "Employee code", "Employee name", "First name", "Last name", "Surname", "BASIC" };
        var request = new ExcelPayslipMappingRequest { Columns = headings.Select((h, i) =>
            new ExcelPayslipMappingColumn { ColumnIndex = i, SourceHeader = h, Kind = "info" }).ToList() };
        var before = JsonSerializer.Serialize(request);
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, r => r.Title.StartsWith("Employee code Employee name First name Last name Surname:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("CONCAT_WS", lesson.Content);
        Assert.Contains("do not invent a surname", lesson.Content);
        Assert.Contains("genuinely different source name", lesson.Content);
        Assert.Contains("do not approve a named source row", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void RepeatedGrossExpansionKeepsActualSourceAstAndRequiresWideStorage()
    {
        var headers=new[]{"Gross Total","SUM","Workmen Comp","LWF","PT","Basic","HRA","Allowance"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind="info",HasNumbers=true}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Gross Total SUM Workmen Comp LWF PT:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("4096",lesson.Content);Assert.Contains("512-node",lesson.Content);
        Assert.Contains("Preview/Apply do not run DDL or truncate",lesson.Content);Assert.Contains("dropping range gaps",lesson.Content);
        Assert.Contains("not certify actual saved payroll parity",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void DifferentSourceRulesDoNotDuplicateMatchingTemplatesOrChangeSharedAssignments()
    {
        var headers=new[]{"CTC","BASIC","HRA","PF Admin","EDLI","Workmen Comp/LWF/PT"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind="info",HasNumbers=true}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var refs=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(refs,r=>r.Title.StartsWith("CTC BASIC HRA PF Admin EDLI Workmen Comp/LWF/PT Shared Template:"));
        Assert.Contains("DifferentScope",lesson.Content);Assert.Contains("must reuse",lesson.Content);Assert.Contains("not proof of attendance",lesson.Content);
        Assert.Contains("legacy personal combined-deduction seed",lesson.Content);Assert.Contains("template-only default zero",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,refs));
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void SplitRateCorrectionRetainsOneSettlementMonthAndRejectsStaleSourceAuthority()
    {
        var headers=new[]{"Old VDA","New VDA","Old Basic","New Basic","Wages","Special Allowance","Total Earning payment","Net Working Days","Arrears"};var request=new ExcelPayslipMappingRequest{Columns=headers.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind="info",HasNumbers=true}).ToList()};var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));var refs=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);var lesson=Assert.Single(refs,r=>r.Title.StartsWith("Old VDA Old Basic New VDA New Basic"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("never justifies dropping the old-rate earnings",lesson.Content);Assert.Contains("not full declared gross",lesson.Content);Assert.Contains("stale hash rejects the entire Save",lesson.Content);Assert.Contains("does not create a batch/version",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,refs));Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void NormalCtcOperandLessonDoesNotAliasAnIndependentEntrySalaryBase()
    {
        var headers=new[]{"Revised CTC","Increment","Hardship Allowance","Monthly Target","Old CTC"};var request=new ExcelPayslipMappingRequest{Columns=headers.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind="info",HasNumbers=true,HasFormula=i is 0 or 2}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));var refs=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);var lesson=Assert.Single(refs,r=>r.Title.StartsWith("Revised CTC Increment Hardship Allowance Monthly Target:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("aliasing disabled",lesson.Content);Assert.Contains("distinct entry/prior salary base stays separate",lesson.Content);Assert.Contains("existing native MONTHLY_CTC",lesson.Content);Assert.Contains("Historical and reported payroll reject",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,refs));Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void ReviewedEmptyArrearsLessonPreservesSubtotalAndCannotApproveMissingData()
    {
        var headers=new[]{"Arrears","Blank Subtotal","Professional Tax","PT Conditional","Birthday Allowance"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((h,i)=>new ExcelPayslipMappingColumn{ColumnIndex=i,SourceHeader=h,Kind=i==2?"deduction":"info",HasBlanks=i==0,HasFormula=i is 1 or 3}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var refs=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);var lesson=Assert.Single(refs,r=>r.Title.StartsWith("Arrears Blank Subtotal Professional Tax:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("current full batch/source hash",lesson.Content);Assert.Contains("typed Blank",lesson.Content);
        Assert.Contains("final gross is a different quantity",lesson.Content);Assert.Contains("Historical and reported-input adapters reject",lesson.Content);Assert.Contains("Suggestions and RAG cannot grant",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,refs));Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        using var response=JsonDocument.Parse("""{"columns":[],"recurringBlankInputReview":{"reviewed":true}}""");var result=ExcelPayslipMappingService.ApplyResponse(request,null,refs,response.RootElement,"synthetic-model");
        Assert.Equal(request.Columns.Select(c=>c.Kind),result.Columns.Select(c=>c.Kind));Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void ReviewedExistingTemplateIdentityLessonRetainsSourceRulesAndCannotApproveNewPay()
    {
        var headers=new[]{"ROUND Formula","Payroll Days","Payable Days","HRA","BASIC_MONTHLY","Template Reuse"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind=index==3?"earning":"info",HasNumbers=true}).ToList()};
        var original=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var refs=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);var lesson=Assert.Single(refs,r=>r.Title.StartsWith("ROUND Formula Payroll Days Payable Days:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("identity unary plus",lesson.Content);Assert.Contains("actual existing template formula",lesson.Content);
        Assert.Contains("Keep the ordinary exact structure/signature API",lesson.Content);Assert.Contains("Numeric sampling",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,refs));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());Assert.Equal(original,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void ConfigurationCurrencyReviewLessonCannotBecomePayrollOrMappingApproval()
    {
        var headers=new[]{"Monthly Currency","Configuration Rounding Review","Allowance","Gross","Deductions","Net Pay"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Label=header,Kind=index==2?"earning":index==5?"netPay":"info",HasNumbers=true}).ToList()};
        var before=JsonSerializer.Serialize(request);var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);var rule=Assert.Single(references,r=>r.Title.StartsWith("Monthly Currency Configuration Rounding Review:"));
        Assert.True(rule.Content.Length<2600);
        Assert.Contains("existing importer cache-identity contract",rule.Content);
        Assert.Contains("not native arithmetic equivalence or a monetary tolerance",rule.Content);
        Assert.Contains("differing currency rounding, stale hashes and unsupported formulas remain blocked",rule.Content);
        Assert.True(rule.Content.Length<2600);Assert.Contains("opposite differences cannot cancel",rule.Content);
        Assert.Contains("never certifies saved Employee payroll",rule.Content);Assert.Contains("Missing identity",rule.Content);Assert.Contains("Suggestions and RAG cannot grant",rule.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(rule.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==rule.Title).GetProperty("content").GetString());
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        using var response=JsonDocument.Parse("""{"columns":[],"approveConfiguration":true}""");
        var result=ExcelPayslipMappingService.ApplyResponse(request,null,references,response.RootElement,"synthetic-model");
        Assert.Equal(request.Columns.Select(c=>c.Kind),result.Columns.Select(c=>c.Kind));Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void RecurringLiteralStorageLessonIsRetrievedWithoutChangingMonetaryOrIdentifierRoles()
    {
        var headers=new[]{"Basic","HRA","Other Allowance","Monthly Salary","Daily Wage","UAN Number","Payable Days","Net Pay"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Label=header,Kind=index==7?"netPay":"info",HasNumbers=true}).ToList()};
        var before=JsonSerializer.Serialize(request);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.EndsWith(": recurring input storage precision"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("identical finite IEEE-754 bits",lesson.Content);
        Assert.Contains("Real fifth-decimal precision",lesson.Content);Assert.Contains("mapped financial outputs remain blocked",lesson.Content);
        Assert.Contains("does not certify payroll parity",lesson.Content);Assert.Contains("all source formulas",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        using var response=JsonDocument.Parse("""{"columns":[],"approveAllDifferences":true}""");
        var mapped=ExcelPayslipMappingService.ApplyResponse(request,null,references,response.RootElement,"synthetic-model");
        Assert.Equal(request.Columns.Select(c=>c.Kind),mapped.Columns.Select(c=>c.Kind));Assert.Equal(before,JsonSerializer.Serialize(request));
    }
    [Fact]
    public void IsolatedAbsoluteDivisorLessonIsRetrievedFromStructuralHeadersWithoutChangingRoles()
    {
        var headers=new[]{"Rate Per Month","Basic","HRA","Telephone Allowance","Medical Allowance","Other Allowances","Total Days","Total Working Days","Earned Basic","","Earned HRA","Net Pay"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Label=index==9?"Column (J)":header,
            Kind=index is 8 or 10?"earning":index==9?"ignore":index==11?"netPay":"info",
            HasFormula=index is 8 or 10,HasNumbers=true,HasBlanks=index==9,
            FormulaDependencies=index==8?[1,7,9]:index==10?[2,7,9]:[]}).ToList()};
        var before=JsonSerializer.Serialize(request);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.TrimEnd('\r').EndsWith(": isolated absolute divisor"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("physical row may also contain an employee",lesson.Content);
        Assert.Contains("whole-column isolation",lesson.Content);
        Assert.Contains("relative reference",lesson.Content);
        Assert.Contains("not actual presence",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));
        Assert.Equal("ignore",request.Columns[9].Kind);
        Assert.All(request.Columns.Take(8),column=>Assert.Equal("info",column.Kind));
        Assert.All(request.Columns.Where(column=>column.HasFormula),column=>Assert.Equal("earning",column.Kind));
        request.Columns[9].HasFormula=true;
        Assert.DoesNotContain(ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]),
            reference=>reference.Source.EndsWith("#isolated-divisor",StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitMonetaryReviewLessonNeverApprovesMissingDataOrChangesSourceRoles()
    {
        var headers=new[]{"Gross","Net","PF","ESIC","Rounding Review"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Kind=index==1?"netPay":index>=2&&index<=3?"deduction":"info"}).ToList()};
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Gross Net PF ESIC Rounding Review:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("Default historical review remains exact",lesson.Content);
        Assert.Contains("freshly acknowledged proof hash",lesson.Content);
        Assert.Contains("never relabel it Exact or Explained rounding",lesson.Content);
        Assert.Contains("distinct salary-input dependencies remain blockers",lesson.Content);
        Assert.Contains("not an automatic AI approval",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        var before=JsonSerializer.Serialize(request);
        using var provider=JsonDocument.Parse("""{"columns":[],"approveAllDifferences":true}""");
        var result=ExcelPayslipMappingService.ApplyResponse(request,null,references,provider.RootElement,"synthetic-model");
        Assert.Equal(request.Columns.Select(c=>c.Kind),result.Columns.Select(c=>c.Kind));Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void NumericSourceRepresentationLessonPreservesExactCellAndSalaryBoundaries()
    {
        var request = new ExcelPayslipMappingRequest { Columns = new[] { "Basic", "PF", "ESIC", "Gross", "Net Pay", "Cached Number" }
            .Select((header,index)=>new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind=index==0?"earning":index==4?"netPay":"info" }).ToList() };
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,item=>item.Title.StartsWith("Basic PF ESIC Gross Net Pay Cached Number:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("existing G15 source-display contract",lesson.Content);
        Assert.Contains("at most one IEEE step",lesson.Content);
        Assert.Contains("same-sign nonzero amounts within 0.00000001",lesson.Content);
        Assert.Contains("midpoint-to-even and midpoint-away",lesson.Content);
        Assert.Contains("Never use this for identity/text",lesson.Content);
        Assert.Contains("exact same-cell cache verification",lesson.Content);
        Assert.Contains("independent formula/native proof",lesson.Content);
        Assert.Contains("Do not round stored salary",lesson.Content);
        Assert.Contains("double-rounding presentation difference",lesson.Content);
        Assert.Contains("authenticated receipt captured by actual native processing",lesson.Content);
        Assert.Contains("A fresh simulation or source cache is not frozen processing evidence",lesson.Content);
        Assert.Contains("all declared native totals, including employer cost",lesson.Content);
        Assert.Contains("block instead of silently falling back",lesson.Content);
        Assert.Contains("original captured source and native processing epoch",lesson.Content);
        Assert.Contains("numeric lexemes",lesson.Content);
        Assert.Contains("top-level profileLinks",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(item=>item.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
    }

    [Fact]
    public void MonthlyBudgetInformationLessonRequiresOriginalClosedEvidenceAndKeepsFinancialRoles()
    {
        var headers=new[]{"CTC","Monthly Gross","Salary Budget","Reference","Basic","Net Pay"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Kind=index==4?"earning":index==5?"netPay":"info"}).ToList()};
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("CTC Monthly Gross Salary Budget Reference:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("independently authenticated closed-period annual salary target",lesson.Content);
        Assert.Contains("never reverse-prorate earned pay",lesson.Content);
        Assert.Contains("Other fields with the same legacy token",lesson.Content);
        Assert.Contains("replay never restores an old pointer",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        var before=JsonSerializer.Serialize(request);
        using var provider=JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1}]}""");
        var result=ExcelPayslipMappingService.ApplyResponse(request,null,references,provider.RootElement,"synthetic-model");
        Assert.Equal("info",result.Columns[0].Kind);Assert.Equal("earning",result.Columns[4].Kind);
        Assert.Equal("netPay",result.Columns[5].Kind);Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void RecordedAbsenceLessonPreservesRawDatedWitnessAndRejectsInventedAttendance()
    {
        var columns=new[]{"Attendance Status","Recorded Daily","Absent"}.Select((header,index)=>new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind="info" }).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,knowledge,[]),item=>item.Title.StartsWith("Attendance Status Recorded Daily Absent:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("A/Absent status with exactly zero units",lesson.Content);
        Assert.Contains("original raw status in its immutable witness hash",lesson.Content);
        Assert.Contains("Positive or negative absence units",lesson.Content);
        Assert.Contains("Monthly payable totals still do not establish daily presence",lesson.Content);
    }

    [Fact]
    public void ReviewedUanAliasLessonPropagatesOnlyExistingImmutableBinding()
    {
        var columns=new[]{"UAN","UAN NO","Employee Code","PAN"}.Select((header,index)=>new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind="info" }).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,knowledge,[]),item=>item.Title.StartsWith("UAN UAN NO Employee Code PAN:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("carry that exact reviewed column binding",lesson.Content);
        Assert.Contains("source/hash/header/identity validators",lesson.Content);
        Assert.Contains("Never auto-pick another alias",lesson.Content);
        Assert.Contains("normalize a NEW marker",lesson.Content);
        Assert.Contains("Missing, stale, conflicting or unreviewed bindings remain blocked",lesson.Content);
    }

    [Fact]
    public void TypedOperandLessonIsRetrievedFromAnonymousBasePercentAndDependencyHeadings()
    {
        var headers=new[]{"Entry CTC","Hardship Category Percent","Independent Base","Payable Days","Hardship Allowance"};
        var request=new ExcelPayslipMappingRequest { Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn {
            ColumnIndex=index,SourceHeader=header,Kind=index==4?"earning":"info",HasFormula=index==4,FormulaDependencies=index==4?[0,1,3]:[] }).ToList() };
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,item=>item.Title.StartsWith("Entry CTC Hardship Category Percent Independent Base:"));
        Assert.True(lesson.Content.Length<2600); Assert.Contains("FormulaOperandOnly",lesson.Content);
        Assert.Contains("rather than employee Master CTC or earned Basic",lesson.Content);
        Assert.Contains("create a template for every employee's literal value",lesson.Content);
        Assert.Contains("normal saved Employee replay",lesson.Content); Assert.Contains("real editable numeric input cells",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(item=>item.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        var before=JsonSerializer.Serialize(request);
        using var provider=JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1}]}""");
        var result=ExcelPayslipMappingService.ApplyResponse(request,null,references,provider.RootElement,"synthetic-model");
        Assert.Equal("info",result.Columns[0].Kind); Assert.Equal("earning",result.Columns[4].Kind); Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void NumericPolicyReaderLessonDoesNotTurnMaterializationIntoAnAttendanceRule()
    {
        var columns=new[]{"Work Location ID","Work Week","Attendance Cycle","Cycle Start Day","Cycle End Day"}
            .Select((header,index)=>new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind="info" }).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,knowledge,[]),item=>item.Title.StartsWith("Work Location ID Work Week Attendance Cycle:"));
        Assert.True(lesson.Content.Length<2600); Assert.Contains("checked numeric materialization",lesson.Content);
        Assert.Contains("overflow must still fail",lesson.Content); Assert.Contains("not a reason to alter an Excel mapping or attendance entitlement",lesson.Content);
    }

    [Fact]
    public void ClosedBankOwnershipLessonRequiresExplicitSourceReviewAndKeepsIdentifierRoles()
    {
        var request = new ExcelPayslipMappingRequest { Columns = new[] { "Account No", "Bank Account", "UAN", "Employee Name", "Source identity" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = index == 3 ? "employeeName" : "info" }).ToList() };
        var knowledge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md"));
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, knowledge, []);
        var lesson = Assert.Single(references, item => item.Title.StartsWith("Account No Bank Account Missing UAN Employee Name:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("employees outside the payroll selection", lesson.Content);
        Assert.Contains("unique matching literal government identifier", lesson.Content);
        Assert.Contains("Number cells over Excel's 15-digit boundary", lesson.Content);
        Assert.Contains("Default identity gates", lesson.Content);
        Assert.Contains("not a general matching shortcut", lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal(lesson.Content, prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == lesson.Title).GetProperty("content").GetString());
        var before = JsonSerializer.Serialize(request);
        using var provider = JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1},{"columnIndex":2,"kind":"deduction","confidence":1}]}""");
        var result = ExcelPayslipMappingService.ApplyResponse(request, null, references, provider.RootElement, "synthetic-model");
        Assert.Equal("info", result.Columns[0].Kind); Assert.Equal("info", result.Columns[2].Kind);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void InitialMonthlyCtcLessonUsesAnonymousHeadersAndRetainsTheExactSelectedCellBoundary()
    {
        var headers=new[]{"Current CTC","Monthly CTC","Initial salary history","Last Month Salary","Payroll Days","Payable Days","Basic","Net Pay"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Kind=index==6?"earning":index==7?"netPay":"info",
            HasFormula=index==6,FormulaDependencies=index==6?[0,4,5]:[]}).ToList()};
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Current CTC Monthly CTC Initial salary history:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("exactly equal current Master annual target without rounding",lesson.Content);
        Assert.Contains("exact selected same-row CTC reference",lesson.Content);
        Assert.Contains("another equal-valued cell is a distinct dependency",lesson.Content);
        Assert.Contains("never fills this gap automatically",lesson.Content);
        Assert.Contains("source Rate, earned Basic, net pay, blanks or formula caches",lesson.Content);
        Assert.Contains("not proof of actual joining",lesson.Content);
        Assert.Contains("unsupported zero-basis native proof remains blocked",lesson.Content);
        Assert.Contains("Keep that column's financial role and count it once",lesson.Content);
        Assert.Contains("do not copy current Master values",lesson.Content);
        Assert.Contains("Ordinary setup and default historical coverage remain unchanged",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        var supplied=prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString();
        Assert.Equal(lesson.Content,supplied);Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        var before=JsonSerializer.Serialize(request);
        using var response=JsonDocument.Parse("""{"columns":[{"columnIndex":0,"kind":"earning","confidence":1}]}""");
        var result=ExcelPayslipMappingService.ApplyResponse(request,null,references,response.RootElement,"synthetic-model");
        Assert.Equal("info",result.Columns[0].Kind);Assert.Contains(0,result.ReviewColumnIndexes);
        Assert.Equal("earning",result.Columns[6].Kind);Assert.Equal("netPay",result.Columns[7].Kind);Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void MissingParentRebindingLessonRequiresBothOldFinancialReplayAndFreshSourceProof()
    {
        var columns=new[]{"Source row","Assignment","Parent receipt","Location part","Employee code","Net Pay"}
            .Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind="info"}).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Source row Assignment Parent receipt Location part:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("every selected source part",lesson.Content);
        Assert.Contains("independently owned canonical strong identifier",lesson.Content);
        Assert.Contains("existing parent is not a missing parent",lesson.Content);
        Assert.Contains("full-precision financial and summary line",lesson.Content);
        Assert.Contains("actual persisted currency precision",lesson.Content);
        Assert.Contains("fresh source proof cannot replace failed old financial replay",lesson.Content);
        Assert.Contains("old IDs/missing-parent evidence in the archive",lesson.Content);
        Assert.Contains("not a global mapping rule",lesson.Content);
        Assert.Contains("preview does not prove persistence compatibility",lesson.Content);
        Assert.Contains("inspect its exact receipt and authoritative before/after state before retrying",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(new(){Columns=columns},null,references));
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
    }

    [Fact]
    public void Legacy_profile_sync_lesson_is_retrieved_without_turning_identity_metadata_into_pay()
    {
        var request = new ExcelPayslipMappingRequest { Columns = [
            new() { ColumnIndex=0, SourceHeader="Employee Code", Kind="employeeCode" },
            new() { ColumnIndex=1, SourceHeader="PAN", Kind="info" },
            new() { ColumnIndex=2, SourceHeader="UAN", Kind="ignore" },
            new() { ColumnIndex=3, SourceHeader="ESIC", Kind="info" },
            new() { ColumnIndex=4, SourceHeader="Email", Kind="email" },
            new() { ColumnIndex=5, SourceHeader="Phone", Kind="info" },
            new() { ColumnIndex=6, SourceHeader="Bank", Kind="info" },
            new() { ColumnIndex=7, SourceHeader="CTC", Kind="info" },
            new() { ColumnIndex=8, SourceHeader="Basic", Kind="earning", HasNumbers=true },
            new() { ColumnIndex=9, SourceHeader="Net Pay", Kind="netPay", HasFormula=true, FormulaDependencies=[8] }
        ] };
        var path = Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var references = ExcelPayslipMappingService.Retrieve(request.Columns,File.ReadAllText(path),[]);
        var lesson = Assert.Single(references,item=>item.Title.StartsWith("Email Phone CTC Bank:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("separate profile-only link",lesson.Content);
        Assert.Contains("source identity witness",lesson.Content);
        Assert.Contains("partial UAN, conflicting identifiers or ambiguous candidates",lesson.Content);
        Assert.Contains("Mapping suggestions classify headers; they cannot approve identity links",lesson.Content);
        Assert.Contains("recalculate earnings/deductions/net",lesson.Content);
        Assert.Contains("blank Master email/information value retains a populated source value",lesson.Content);
        Assert.Contains("populated salary-period source location",lesson.Content);
        Assert.Contains("Employment and Master details agree on a positive Employee ID",lesson.Content);
        Assert.Contains("Keep Source employee code and Original employee code as captured",lesson.Content);
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        var supplied = prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(item=>item.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString();
        Assert.Equal(lesson.Content,supplied);

        var before = JsonSerializer.Serialize(request);
        using var monetary = JsonDocument.Parse("""{"columns":[{"columnIndex":2,"kind":"earning","confidence":1},{"columnIndex":4,"kind":"deduction","confidence":1},{"columnIndex":7,"kind":"earning","confidence":1}]}""");
        var result = ExcelPayslipMappingService.ApplyResponse(request,null,references,monetary.RootElement,"synthetic-model");
        Assert.Equal("ignore",result.Columns[2].Kind);
        Assert.Equal("email",result.Columns[4].Kind);
        Assert.Equal("info",result.Columns[7].Kind);
        Assert.Equal("earning",result.Columns[8].Kind);
        Assert.Equal("netPay",result.Columns[9].Kind);
        Assert.Equal(before,JsonSerializer.Serialize(request));
        Assert.Contains(2,result.ReviewColumnIndexes);
        Assert.Contains(4,result.ReviewColumnIndexes);
        Assert.Contains(7,result.ReviewColumnIndexes);
    }

    [Fact]
    public void Repeated_identifier_alias_review_is_retrieved_from_headers_and_does_not_reclassify_pay()
    {
        var request = new ExcelPayslipMappingRequest { Columns = [
            new() { ColumnIndex=0, SourceHeader="Employee Code", Kind="employeeCode" },
            new() { ColumnIndex=1, SourceHeader="Employee Name", Kind="employeeName" },
            new() { ColumnIndex=2, SourceHeader="UAN", Kind="info" },
            new() { ColumnIndex=3, SourceHeader="UAN NO.", Kind="ignore", HasNumbers=true },
            new() { ColumnIndex=4, SourceHeader="PAN", Kind="info" },
            new() { ColumnIndex=5, SourceHeader="Basic", Kind="earning", HasNumbers=true },
            new() { ColumnIndex=6, SourceHeader="Net paid", Kind="netPay", HasFormula=true, FormulaDependencies=[5] }
        ] };
        var path = Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var references = ExcelPayslipMappingService.Retrieve(request.Columns,File.ReadAllText(path),[]);
        var lesson = Assert.Single(references, item=>item.Title.StartsWith("UAN UAN NO Employee Code PAN:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("including ignored columns",lesson.Content);
        Assert.Contains("conflicting populated aliases",lesson.Content);
        Assert.Contains("matching suffix or numeric length cannot reconstruct",lesson.Content);
        Assert.Contains("not an identity-link approval",lesson.Content);
        Assert.Contains("monetary values/formulas",lesson.Content);

        var before = JsonSerializer.Serialize(request);
        var response = JsonDocument.Parse("""{"columns":[{"columnIndex":3,"kind":"info","confidence":0.99}]}""").RootElement;
        var result = ExcelPayslipMappingService.ApplyResponse(request,null,references,response,"synthetic-model");
        Assert.Equal("info",result.Columns[2].Kind);
        Assert.Equal("info",result.Columns[3].Kind);
        Assert.Contains(3,result.ReviewColumnIndexes);
        Assert.Equal("earning",result.Columns[5].Kind);
        Assert.Equal("netPay",result.Columns[6].Kind);
        Assert.Equal(before,JsonSerializer.Serialize(request));
        var invalidMoney = JsonDocument.Parse("""{"columns":[{"columnIndex":3,"kind":"earning","confidence":1}]}""").RootElement;
        var protectedResult = ExcelPayslipMappingService.ApplyResponse(request,null,references,invalidMoney,"synthetic-model");
        Assert.Equal("ignore",protectedResult.Columns[3].Kind);
        Assert.Contains(3,protectedResult.ReviewColumnIndexes);
    }

    [Fact]
    public void AttendantNetPaymentAndRecoveryRetrieveWorksheetSelectionWithoutClientData()
    {
        var columns = new[] { "Name of Attendant", "Name of the Officers", "Net Payment", "Basic", "Working Days", "UAN", "Recovery worksheet" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "ignore" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("Name of Attendant Name of the Officers Net Payment:"));
        Assert.Contains("Recovery-only salary files remain importable", lesson.Content);
        Assert.Contains("must not become a second employee-name column", lesson.Content);
        Assert.Contains("Retain every populated source column, original value and formula", lesson.Content);
        Assert.True(lesson.Content.Length < 2600);
    }

    [Fact]
    public void RevertedDisplayChoicesRetrieveTheSaveBoundaryWithoutInferringSalaryRules()
    {
        var columns = new[] { "CTC", "Branch", "In Hand Salary", "Payslip Label" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "ignore" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("CTC Branch In Hand Salary Payslip Label:"));
        Assert.Contains("preset alone cannot update", lesson.Content);
        Assert.Contains("not a salary classification rule", lesson.Content);
        Assert.Contains("original formulas and validation", lesson.Content);
        Assert.True(lesson.Content.Length < 1800);
    }

    [Fact]
    public void Branding_graphics_retrieve_a_presentation_boundary_without_changing_salary_roles()
    {
        var columns = new[] { "Company Seal", "Employer Signature", "Logo Image", "Employee Name", "Basic", "Net Pay" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "ignore" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("Company Seal Employer Signature Logo Image:"));
        Assert.Contains("legacy request flags", lesson.Content);
        Assert.Contains("Preserve imported figures, identifiers and formulas", lesson.Content);
        Assert.Contains("not a new salary role", lesson.Content);
        Assert.True(lesson.Content.Length < 2600);
    }

    [Fact]
    public void Same_month_location_batch_boundary_is_retrieved_without_client_names_or_employee_data()
    {
        var columns = new[] { "Employee Code", "Work Location", "Payroll Unit", "Attendance", "Payable Days", "Net Pay" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "info" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("Work Location Payroll Unit Attendance Payable Days Net Pay:"));
        Assert.Contains("own explicit batch ID", lesson.Content); Assert.Contains("not a new salary role", lesson.Content);
        Assert.Contains("identity review", lesson.Content); Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("explicitly fill literal blanks", lesson.Content);
        Assert.Contains("Mixed-location files require row-specific evidence", lesson.Content);
        Assert.Equal("excel-payroll-mapping-v80", ExcelPayslipMappingService.KnowledgeVersion);
    }
    [Fact]
    public void SplitWagePeriodLessonIsRetrievedFromAnonymousStructuralHeadings()
    {
        var columns = new[] { "Old VDA", "Old Basic", "New VDA", "New Basic", "Net Working Days", "Wages", "Special Allowance", "Total Earning payment", "LWP", "Arrears" }
            .Select((header,index)=>new ExcelPayslipMappingColumn {ColumnIndex=index,SourceHeader=header,Kind="ignore"}).ToList();
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),r=>r.Title.StartsWith("Old VDA Old Basic New VDA New Basic"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("combined subtotal is the sole gross control",lesson.Content);
        Assert.Contains("must not be deducted twice",lesson.Content);
        Assert.Contains("Keep both period day values visible",lesson.Content);
        Assert.Contains("do not create Master records on payslip-only Save",lesson.Content);
        Assert.Contains("not automatic exclusions",lesson.Content);
    }
    [Fact]
    public void LegacyBlankCalculationBoundaryIsRetrievedWithoutAnyClientOrEmployeeData()
    {
        var columns = new[] { "Original Refund", "Original PF Deduction", "Attendance", "In Hand Salary", "Source workbook", "Source worksheet" }
            .Select((header,index) => new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind="info" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),
            item => item.Title.StartsWith("Create another month Attendance In Hand Salary"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("explicitly captured, empty, untyped, formula-free",lesson.Content);
        Assert.Contains("Typed empty TEXT",lesson.Content);
        Assert.Contains("does not approve blank mapped monetary columns",lesson.Content);
        Assert.Contains("not a new earning/deduction mapping",lesson.Content);
    }

    [Fact]
    public void CombinedPreparationBoundaryIsRetrievedFromAttendanceAndTemplateHeaders()
    {
        var columns=new[] {"Present Days","Payable Days","Work Location","Salary Template","Effective Date","Policy"}
            .Select((header,index)=>new ExcelPayslipMappingColumn {ColumnIndex=index,SourceHeader=header,Kind="info"}).ToList();
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),r=>r.Title.StartsWith("Reviewed native payroll setup boundaries"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("Reuse only exact existing templates",lesson.Content);
        Assert.Contains("verified source-to-Master links",lesson.Content);
        Assert.Contains("Monthly totals do not create daily marks",lesson.Content);Assert.Contains("do not resubmit completed cohorts",lesson.Content);
    }
    [Fact]
    public void GrossSubtotalCashAdditionAndWarningReviewBoundaryIsRetrievedFromStructuralHeaders()
    {
        var headers = new[] { "Gross Salary (A)", "Total Deduction (B)", "Additional Allowance (C)", "Net Take Home (A-B+C)" };
        var columns = headers.Select((header,index) => new ExcelPayslipMappingColumn { ColumnIndex=index,SourceHeader=header,Kind="info" }).ToList();
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),r=>r.Title.StartsWith("Gross Salary (A) Total Deduction (B)"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("Keep C as an earning",lesson.Content);
        Assert.Contains("already included in A must not be added twice",lesson.Content);
        Assert.Contains("does not repair formulas, change salary",lesson.Content);
        Assert.Contains("not promote an individual approval into a global auto-ignore",lesson.Content);
    }

    [Fact]
    public void PartialGrossControlRemapRetrievesOwnershipAndApprovalBoundariesWithoutMutatingMapping()
    {
        var headers=new[]{"Gross Salary (A)","Total Deduction (B)","Additional Allowance (C)","Net Take Home (A-B+C)"};
        var request=new ExcelPayslipMappingRequest{Columns=headers.Select((header,index)=>new ExcelPayslipMappingColumn{
            ColumnIndex=index,SourceHeader=header,Label=header,Kind=index==0?"grossTotal":index==1?"deductionTotal":index==2?"earning":"netPay",
            HasNumbers=true,HasFormula=index==3,FormulaDependencies=index==3?[0,1,2]:[]}).ToList()};
        var before=JsonSerializer.Serialize(request);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,r=>r.Title.StartsWith("Gross Salary (A) Total Deduction (B)"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("keep A as labelled Information",lesson.Content);
        Assert.Contains("retain saved row IDs and mail/job/Master associations",lesson.Content);
        Assert.Contains("Changed declared controls invalidate warning reviews and PDF hashes",lesson.Content);
        Assert.Contains("actual cash, label/category/order or identity changes",lesson.Content);
        Assert.Contains("unprinted partial subtotal as ignored",lesson.Content);Assert.Contains("preflight every row before Save",lesson.Content);
        Assert.Contains("Never ignore a true full-gross control",lesson.Content);
        Assert.Contains("Retain its exact original cell, value and formula",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal("excel-payroll-mapping-v80",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(lesson.Content,prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==lesson.Title).GetProperty("content").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));Assert.Equal("grossTotal",request.Columns[0].Kind);
    }
    [Fact]
    public void ConsolidatedPayslipActionBoundaryIsRetrievedFromSourceTraceHeaders()
    {
        var headers = new[] { "Source workbook", "Source worksheet", "Source Excel row", "Payroll unit", "Employee code", "Attendance" };
        var columns = headers.Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex=index, SourceHeader=header, Kind="info" }).ToList();
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]), r=>r.Title.StartsWith("Source workbook Source worksheet Payroll unit"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("normal payslip list",lesson.Content);
        Assert.Contains("Legacy view preferences",lesson.Content);
        Assert.Contains("not a new salary mapping rule",lesson.Content);
        Assert.Contains("does not authorize merging two slips",lesson.Content);
        Assert.Contains("saved-batch List/Dashboard can time out",lesson.Content);
        Assert.Contains("distinct client/batch pairs in bounded queries",lesson.Content);
        Assert.Contains("existing exact snapshot hashes in one read-only repeatable snapshot",lesson.Content);
        Assert.Contains("financial refresh to selected RowIds only",lesson.Content);
        Assert.Contains("selecting one duplicate must not hide the other row",lesson.Content);
        Assert.Contains("No cross-request salary cache is introduced",lesson.Content);
        Assert.Contains("changed or missing bindings stay stale",lesson.Content);
        Assert.Contains("Never cache hashes across requests, alter proofs, re-import duplicates",lesson.Content);
    }
    [Fact]
    public void NumericIdentityDisplayBoundaryIsRetrievedFromAnonymousHeaders()
    {
        var columns = new List<ExcelPayslipMappingColumn> {
            new() { ColumnIndex=0, SourceHeader="AADHAR NUMBER", Kind="ignore", HasNumbers=true },
            new() { ColumnIndex=1, SourceHeader="CONTACT NUMBER", Kind="info", HasNumbers=true },
            new() { ColumnIndex=2, SourceHeader="UAN NUMBER", Kind="info", HasNumbers=true },
            new() { ColumnIndex=3, SourceHeader="ESI NUMBER", Kind="info", HasNumbers=true },
            new() { ColumnIndex=4, SourceHeader="ACCOUNT NUMBER", Kind="info", HasNumbers=true } };
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]), r=>r.Title.StartsWith("Aadhaar Aadhar"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("display formatting only",lesson.Content);
        Assert.Contains("Literal scientific text",lesson.Content);
        Assert.Contains("identity-review evidence",lesson.Content);
        Assert.Contains("Aadhaar Number and Aadhar Number as the same canonical Master field",lesson.Content);
        Assert.Contains("requires an existing verified same-client Master link",lesson.Content);
        Assert.Equal("excel-payroll-mapping-v80",ExcelPayslipMappingService.KnowledgeVersion);
    }
    [Fact]
    public void NativePayslipDesignLessonIncludesEffectiveMasterLabelsWithoutSalaryAuthority()
    {
        var columns = new List<ExcelPayslipMappingColumn> {
            new() { ColumnIndex=0, SourceHeader="Payslip Register Excel Payslip", Kind="ignore" },
            new() { ColumnIndex=1, SourceHeader="Designation Department Grade Work Location Email", Kind="info" } };
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]), item=>item.Title.StartsWith("Payslip Register Excel Payslip"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("physical worksheet columns alone",lesson.Content);
        Assert.Contains("approved saved core payroll",lesson.Content);
        Assert.Contains("reject unmapped nonzero financial components",lesson.Content);
        Assert.Contains("protect their mapping from stale whole-setup saves",lesson.Content);
        Assert.Contains("grants no payroll approval",lesson.Content);
        Assert.Contains("protecting its original Classic fallback",lesson.Content);
        Assert.Contains("all affected approved financial components",lesson.Content);
        Assert.Contains("any live API, ESS or worker reader ignores month scope",lesson.Content);
    }
    [Theory]
    [InlineData("Policy Batch Attendance Group Guid", "Policy Batch Attendance Group Guid:", "Do not change a shared connection's GUID mode")]
    [InlineData("ROUND Formula Payroll Days Payable Days", "ROUND Formula Payroll Days Payable Days:", "Keep raw formulas and source/rules fingerprints unchanged")]
    [InlineData("Legacy Date-wise Present Days Working Days", "Legacy Date-wise Present Days Working Days:", "not a new policy entitlement or salary rule")]
    public void SourceSetupCompatibilityLessonsRetainTheirProofBoundaries(string header,string title,string boundary)
    {
        var columns=new List<ExcelPayslipMappingColumn> { new() { ColumnIndex=0,SourceHeader=header,Kind="info" } };
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]), item=>item.Title.StartsWith(title));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains(boundary,lesson.Content);
    }
    [Fact]
    public void EmployeeInputsAndEmployerCostLessonIsRetrievedFromAnonymousSalaryDayHeaders()
    {
        var columns = new List<ExcelPayslipMappingColumn> {
            new() { ColumnIndex=0, SourceHeader="Basic", Kind="info", HasNumbers=true },
            new() { ColumnIndex=1, SourceHeader="DA", Kind="info", HasNumbers=true },
            new() { ColumnIndex=2, SourceHeader="Payable Days", Kind="info", HasNumbers=true },
            new() { ColumnIndex=3, SourceHeader="Earned Basic", Kind="earning", HasFormula=true, FormulaDependencies=[0,2] } };
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]), item=>item.Title.StartsWith("Basic DA Payable Days:"));
        Assert.True(lesson.Content.Length<2600); Assert.Contains("explicit employee zero is still valid",lesson.Content);
        Assert.Contains("outside cash earnings",lesson.Content); Assert.Contains("no universal fixed divisor",lesson.Content);
        Assert.Contains("already be itemized", lesson.Content);
        Assert.Contains("bounded by the saved period aggregate", lesson.Content);
        Assert.Contains("Register gross includes regular plus period earnings", lesson.Content);
    }
    [Fact]
    public void FormulaCellsAndAttendancePolicyLessonIsRetrievedFromPfEsicDayHeaders()
    {
        var columns=new List<ExcelPayslipMappingColumn>{new(){ColumnIndex=0,SourceHeader="PF",Kind="deduction",HasFormula=true,FormulaDependencies=[2]},new(){ColumnIndex=1,SourceHeader="ESIC",Kind="deduction",HasFormula=true,FormulaDependencies=[2]},new(){ColumnIndex=2,SourceHeader="Payable Days",Kind="info",HasNumbers=true}};
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),r=>r.Title.StartsWith("PF ESIC Payable Days:"));
        Assert.Contains("real Excel formulas",lesson.Content);Assert.Contains("not a fallback",lesson.Content);
        Assert.Contains("Employer PF/ESIC contributions stay outside",lesson.Content);Assert.True(lesson.Content.Length<2600);
        Assert.Contains("global PT switch alone does not prove",lesson.Content);
    }
    [Fact]
    public void CurrentMasterContactAndCtcLessonIsRetrievedWithoutEmployeeValues()
    {
        var columns=new List<ExcelPayslipMappingColumn>{new(){ColumnIndex=0,SourceHeader="Email",Kind="email"},new(){ColumnIndex=1,SourceHeader="Phone",Kind="info"},new(){ColumnIndex=2,SourceHeader="CTC",Kind="info"},new(){ColumnIndex=3,SourceHeader="Bank",Kind="info"}};
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),r=>r.Title.StartsWith("Email Phone CTC Bank:"));
        Assert.Contains("immutable Employee ID",lesson.Content);Assert.Contains("unknown actual presence unknown",lesson.Content);
        Assert.Contains("regular payroll calculator",lesson.Content);Assert.True(lesson.Content.Length<2600);
    }
    [Fact]
    public void DesignationMasterSyncBoundaryIsRetrievedFromStructuralHeaders()
    {
        var columns=new List<ExcelPayslipMappingColumn>{new(){ColumnIndex=0,SourceHeader="Employee code",Kind="employeeCode"},
            new(){ColumnIndex=1,SourceHeader="Designation_Manpower Category",Kind="info"},new(){ColumnIndex=2,SourceHeader="Basic",Kind="earning"}};
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),reference=>reference.Title.StartsWith("Designation Manpower Category:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("preserving the captured source field",lesson.Content);
        Assert.Contains("Regenerate current-row literals and formulas",lesson.Content);
    }
    [Fact]
    public void LiteralDeductionConfigurationLessonIsRetrievedWithoutClientOrFileNames()
    {
        var columns=new List<ExcelPayslipMappingColumn>{
            new(){ColumnIndex=0,SourceHeader="Employee name",Kind="employeeName"},
            new(){ColumnIndex=1,SourceHeader="PT",Kind="deduction",HasNumbers=true},
            new(){ColumnIndex=2,SourceHeader="MLWF",Kind="deduction",HasNumbers=true},
            new(){ColumnIndex=3,SourceHeader="Penalty",Kind="deduction",HasNumbers=true},
            new(){ColumnIndex=4,SourceHeader="Net paid",Kind="netPay",HasFormula=true,FormulaDependencies=[1,2,3]}};
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,File.ReadAllText(path),[]),reference=>reference.Title.StartsWith("PT MLWF Penalty:"));
        Assert.True(lesson.Content.Length<2600);
        Assert.Contains("differing literal alone does not establish a new salary rule",lesson.Content);
        Assert.Contains("never freeze formula caches",lesson.Content);
        Assert.Contains("absent Professional Tax column is not a zero instruction", lesson.Content);
        Assert.Contains("employee/client/month", lesson.Content);
        Assert.Equal("excel-payroll-mapping-v80",ExcelPayslipMappingService.KnowledgeVersion);
    }
    [Fact]
    public void HistoricalSetupBoundaryIsRetrievedFromAnonymousPeriodRateHeadings()
    {
        var columns = new[] { "Salary history", "Effective month", "Rate", "Employee code", "Net pay" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "info" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("Salary history Effective month Rate Employee code:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("every retained/future history row", lesson.Content);
        Assert.Contains("complete closed month", lesson.Content);
        Assert.Contains("not automatically a declared CTC", lesson.Content);
        Assert.Contains("not a new salary role", lesson.Content);
        Assert.Contains("proposed historical salary basis",lesson.Content);
        Assert.Contains("one transaction",lesson.Content);
        Assert.Contains("Missing PT alone never implies zero",lesson.Content);
        Assert.Contains("independent source calculation",lesson.Content);
        Assert.Contains("SQL NULL, differing budgets and configured future salary remain blocked",lesson.Content);
        Assert.Contains("Mapping suggestions cannot grant this internal authority",lesson.Content);
    }
    [Fact]
    public void NativeDayAndCapBoundaryIsRetrievedFromAnonymousDayHeadings()
    {
        var columns = new[] { "Paid Days", "Total working Days", "Attedance", "Present Days", "Basic", "PF", "ESIC" }
            .Select((header, index) => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = header, Kind = "info" }).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var lesson = Assert.Single(ExcelPayslipMappingService.Retrieve(columns, File.ReadAllText(path), []),
            reference => reference.Title.StartsWith("Paid Days Total working Days Attedance:"));
        Assert.True(lesson.Content.Length < 2600);
        Assert.Contains("actual-presence review", lesson.Content);
        Assert.Contains("nonnullable summary DTO", lesson.Content);
        Assert.Contains("default 0", lesson.Content);
        Assert.Contains("before resolving source cells to literals", lesson.Content);
        Assert.Contains("distinct rate cells", lesson.Content);
        Assert.Contains("not a new earning, deduction or statutory policy", lesson.Content);
    }
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
    [InlineData("missing", -1, "unknown", 1500)]
    public void DependencyMetadataCannotCarryUnboundedOrInventedResolution(string status, int sheet, string type, int column)
    {
        var request = Request();
        request.Columns[1].CrossSheetDependencies = [new() { SheetIndex = sheet, Status = status, SourceType = type, ColumnIndexes = [column] }];
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipMappingService.ValidateRequest(request));
    }

    [Fact]
    public void MappingAccepts1500ColumnsAndFarDependenciesButRejects1501()
    {
        var request = Request();
        for (var i = request.Columns.Count; i < 1500; i++)
            request.Columns.Add(new() { ColumnIndex = i, SourceHeader = "Extra", Label = "Extra", Kind = "ignore" });
        request.Columns[1].FormulaDependencies = [1499];
        request.Columns[1].CrossSheetDependencies = [new() { SheetIndex = 0, Status = "supplied", SourceType = "attendance", ColumnIndexes = [1499] }];
        ExcelPayslipMappingService.ValidateRequest(request);
        request.Columns.Add(new() { ColumnIndex = 1500, SourceHeader = "Extra", Label = "Extra", Kind = "ignore" });
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
        var dependencyLesson = Assert.Single(references, r => r.Title.StartsWith("Cross-sheet formula inputs"));
        Assert.True(dependencyLesson.Content.Length <= 2600, "Dependency guidance must fit in its prompt reference.");
        Assert.Contains("external-link relationships", dependencyLesson.Content);
        Assert.Contains("once per workbook alias and target sheet", dependencyLesson.Content);
        Assert.Contains("ReferenceOnly", dependencyLesson.Content);
        Assert.Contains("without upgrading it to Reviewed", dependencyLesson.Content);
        using var dependencyPrompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Contains("external-link relationships", dependencyPrompt.RootElement.GetRawText());
        Assert.Contains(1, Apply("""{"columns":[]} """, request).ReviewColumnIndexes);
    }

    private static ExcelPayslipMappingRequest TransportBonusRequest()
    {
        var request = new ExcelPayslipMappingRequest { Columns = Enumerable.Range(0, 54)
            .Select(index => new ExcelPayslipMappingColumn { ColumnIndex = index, SourceHeader = "", Label = "", Kind = "ignore" }).ToList() };
        void Column(int index, string header, string kind, params int[] dependencies)
        {
            request.Columns[index] = new() { ColumnIndex = index, SourceHeader = header, Label = header, Kind = kind,
                HasNumbers = kind != "employeeName", HasText = kind == "employeeName", HasFormula = dependencies.Length > 0,
                FormulaDependencies = dependencies };
        }
        Column(0, "SR NO", "ignore"); Column(1, "ID NO", "ignore"); Column(2, "CONDUCTOR NAME", "employeeName");
        Column(3, "uan", "info"); Column(4, "Basic", "info"); Column(5, "DA", "info");
        Column(6, "HRA @ 5%", "info", 4, 5); Column(7, "Bonus @ 8.33%", "info", 4, 5);
        Column(8, "Leave", "info"); Column(9, "Uniform Allowance", "info"); Column(10, "Total", "ignore", 4, 5, 6, 7, 8, 9);
        Column(11, "Month days", "info"); Column(12, "Payble Days", "info");
        Column(14, "Basic", "earning", 4, 11, 12); Column(15, "DA", "earning", 5, 11, 12);
        Column(16, "HRA @ 5%", "earning", 6, 11, 12); Column(17, "Bonus @ 8.33%", "earning", 7, 11, 12);
        Column(18, "Leave", "earning", 8, 11, 12); Column(19, "Uniform Allowance", "earning", 9, 11, 12);
        Column(20, "Total", "ignore", 10, 11, 12);
        Column(21, "EPF 13%", "employer"); Column(22, "ESIC @ 3.25%", "employer");
        Column(23, "Total", "ignore", 20, 21, 22); Column(24, "Service Charge", "ignore");
        Column(25, "Total", "ignore", 23, 24); Column(26, "IGST", "ignore"); Column(27, "Invoice", "ignore", 25, 26);
        Column(29, "EPF 12%", "deduction"); Column(30, "ESIC @ .75%", "deduction");
        Column(31, "Pt", "deduction"); Column(32, "MLWF", "deduction");
        Column(33, "After statutory deductions", "ignore", 20, 29, 30, 31, 17, 32);
        Column(34, "recovery", "deduction"); Column(35, "Penalty", "deduction"); Column(36, "net paid", "netPay", 33, 34, 35);
        Column(52, "Recovery", "deduction"); Column(53, "Invoice total", "ignore");
        return request;
    }

    [Fact]
    public void TransportBonusCaseIsRetrievedFromHeadersAndDependenciesWithoutClientOrWorkbookNames()
    {
        var request = TransportBonusRequest(); ExcelPayslipMappingService.ValidateRequest(request);
        var path = Path.Combine(AppContext.BaseDirectory, "FrevoPilot", "docs", "excel-payroll-mapping.md");
        var references = ExcelPayslipMappingService.Retrieve(request.Columns, File.ReadAllText(path), []);
        var pattern = Assert.Single(references, item => item.Title.StartsWith("TMT pattern:"));
        using var prompt = JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request, null, references));
        Assert.Equal("excel-payroll-mapping-v80", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        var supplied = prompt.RootElement.GetProperty("references").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == pattern.Title).GetProperty("content").GetString()!;
        Assert.Equal(pattern.Content, supplied); // The critical case must fit without prompt truncation.
        Assert.Contains("BA Recovery", supplied);
        Assert.Contains("V EPF 13% and W ESIC 3.25%", supplied);
        Assert.Contains("AB is Invoice and AC is blank", supplied);
        Assert.DoesNotContain("formulaText", prompt.RootElement.GetRawText());
        Assert.DoesNotContain("sourceFileName", prompt.RootElement.GetRawText());
    }

    [Fact]
    public void TransportBonusSuggestionsCanCorrectOnlyTheOffsetBonusAndSideRecoveryWithoutChangingSource()
    {
        var request = TransportBonusRequest(); var before = JsonSerializer.Serialize(request);
        var result = Apply("""{"columns":[{"columnIndex":17,"kind":"info","confidence":0.99},{"columnIndex":52,"kind":"ignore","confidence":0.99},{"columnIndex":7,"kind":"earning","confidence":1}]}""", request);
        Assert.Equal("info", result.Columns[17].Kind); Assert.Equal("ignore", result.Columns[52].Kind);
        Assert.Equal("info", result.Columns[7].Kind); Assert.Equal("deduction", result.Columns[34].Kind);
        Assert.Equal("netPay", result.Columns[36].Kind); Assert.Equal("employer", result.Columns[21].Kind);
        Assert.Equal("employer", result.Columns[22].Kind); Assert.Equal("ignore", result.Columns[27].Kind);
        Assert.Equal(new[] { 17, 52 }, result.Columns.Where(column => column.Kind != request.Columns[column.ColumnIndex].Kind).Select(column => column.ColumnIndex));
        Assert.Contains(17, result.ReviewColumnIndexes); Assert.Contains(52, result.ReviewColumnIndexes);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void MonthlyTotalDutyUnitAndLiteralDeductionBoundaryIsRetrievedFromStructuralHeadings()
    {
        var request=TransportBonusRequest();request.Columns[11].SourceHeader="Total Days";
        var path=Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md");
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,File.ReadAllText(path),[]);
        var rule=Assert.Single(references,r=>r.Title.StartsWith("Native setup: monthly Total"));
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        var supplied=prompt.RootElement.GetProperty("references").EnumerateArray().Single(r=>r.GetProperty("title").GetString()==rule.Title).GetProperty("content").GetString();
        Assert.Equal(rule.Content,supplied);Assert.Contains("never annualize attendance-prorated Basic",supplied);
        Assert.Contains("Unknown actual presence stays unknown",supplied);Assert.Contains("small unexplained mismatch remains blocked",supplied);
    }

    [Fact]
    public void OrdinaryCashBonusAndEmployeeRecoveryRemainValidRolesForOtherLayouts()
    {
        var request = new ExcelPayslipMappingRequest { Columns = [
            new() { ColumnIndex = 0, SourceHeader = "Employee Name", Label = "Employee Name", Kind = "employeeName" },
            new() { ColumnIndex = 1, SourceHeader = "Basic", Label = "Basic", Kind = "earning" },
            new() { ColumnIndex = 2, SourceHeader = "Cash Bonus", Label = "Cash Bonus", Kind = "ignore" },
            new() { ColumnIndex = 3, SourceHeader = "Employee Recovery", Label = "Employee Recovery", Kind = "ignore" },
            new() { ColumnIndex = 4, SourceHeader = "Net Pay", Label = "Net Pay", Kind = "netPay", HasFormula = true, FormulaDependencies = [1, 2, 3] }
        ] };
        var result = Apply("""{"columns":[{"columnIndex":2,"kind":"earning","confidence":0.99},{"columnIndex":3,"kind":"deduction","confidence":0.99}]}""", request);
        Assert.Equal("earning", result.Columns[2].Kind); Assert.Equal("deduction", result.Columns[3].Kind);
        Assert.Equal("netPay", result.Columns[4].Kind);
    }
}

