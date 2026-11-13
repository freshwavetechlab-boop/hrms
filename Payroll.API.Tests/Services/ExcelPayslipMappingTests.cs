using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMappingTests
{
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
        Assert.Equal("excel-payroll-mapping-v25", ExcelPayslipMappingService.KnowledgeVersion);
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
        Assert.Equal("excel-payroll-mapping-v25",ExcelPayslipMappingService.KnowledgeVersion);
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
        Assert.Equal("excel-payroll-mapping-v25",ExcelPayslipMappingService.KnowledgeVersion);
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
        Assert.Equal("excel-payroll-mapping-v25", prompt.RootElement.GetProperty("knowledgeVersion").GetString());
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
