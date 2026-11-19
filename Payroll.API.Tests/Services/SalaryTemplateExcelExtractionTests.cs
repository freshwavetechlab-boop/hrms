using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SalaryTemplateExcelExtractionTests
{
    private static SalaryTemplateExcelExtractionRequest Fixture()
    {
        var headers = new[] { "Basic monthly", "Payroll days", "Payable days", "Basic earned", "Employee PF", "Gross salary", "Net pay", "Allowance percentage" };
        ExcelPayslipCalculationCell Cell(string value, string? formula = null) => new() { Value = value, FormulaText = formula, DataType = "Number" };
        return new() {
            ClientId = 42, TemplateName = "Anonymous location salary", Month = "2026-10", SampleRow = 2,
            Source = new() { SheetName = "Salary", HeaderRow = 1, ColumnCount = headers.Length,
                Columns = headers.Select((header, index) => new ExcelPayslipCalculationColumn { ColumnIndex = index, SourceHeader = header, Label = header, Kind = "info" }).ToList(),
                Rows = [new() { SourceRow = 1, Cells = headers.Select(header => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = header, DataType = "Text" }).ToList() },
                    new() { SourceRow = 2, Cells = [Cell("10000"), Cell("30"), Cell("15"), Cell("5000", "A2/B2*C2"), Cell("600", "MIN(D2,15000)*12%"), Cell("5000", "SUM(D2:D2)"), Cell("4400", "D2-E2"), Cell("5")] }] },
            SelectedColumns = [new() { ColumnIndex = 0, Code = "BASIC", Name = "Basic monthly", Kind = "input" },
                new() { ColumnIndex = 1, Kind = "payrollDays" }, new() { ColumnIndex = 2, Kind = "payableDays" },
                new() { ColumnIndex = 3, Code = "BASIC", Name = "Basic", Kind = "earning" },
                new() { ColumnIndex = 4, Code = "PF", Name = "Employee PF", Kind = "deduction" },
                new() { ColumnIndex = 5, Kind = "grossTotal" }, new() { ColumnIndex = 6, Kind = "netPay" }] };
    }

    [Fact]
    public void SameRowSourceUsesSharedTranslatorAndNativeSimulationWithoutSavingSampleBases()
    {
        var request = Fixture(); var before = JsonSerializer.Serialize(request);
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(4400m, result.Simulation!.Net);
        Assert.All(result.Comparisons, comparison => Assert.Equal("Matched", comparison.Status));
        var basic = result.Components.Single(component => component.Code == "BASIC");
        Assert.Equal("", basic.Value); Assert.True(basic.RequiresEmployeeInput); Assert.True(basic.ProRata);
        Assert.Equal(10000m, result.Scenario!.AmountOverrides[basic.Id]);
        Assert.Equal("MIN(BASIC_EARNED,15000)*12%", result.Components.Single(component => component.Code == "PF").Formula);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void AnchoredSampleReferencesRemainCellBindingsRatherThanUnresolvedNames()
    {
        var request = Fixture();
        request.Source.Rows[1].Cells[3]!.FormulaText = "$A2/B$2*$C$2";
        request.Source.Rows[1].Cells[4]!.FormulaText = "MIN($D$2,15000)*12%";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(4400m, result.Simulation!.Net);
        Assert.Equal("", result.Components.Single(component => component.Code == "BASIC").Value);
    }

    [Fact]
    public void SharedHeaderDivisorReusesTheExistingSourceBindingWithoutRequiringTheValueTwice()
    {
        var request = Fixture();
        request.SelectedColumns.RemoveAll(column => column.Kind == "payrollDays");
        request.PayrollDaysCell = "$H$1";
        request.Source.Rows[0].Cells[7] = new() { Value = "30", DataType = "Number" };
        request.Source.Columns[7].SourceHeader = "Shared payroll divisor";
        request.Source.Rows[1].Cells[3]!.FormulaText = "A2/$H$1*C2";
        var before = JsonSerializer.Serialize(request);
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(30, result.Scenario!.PayrollDays);
        Assert.Equal(4400m, result.Simulation!.Net);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("H2", "30", null)]
    [InlineData("H1", "", null)]
    [InlineData("H1", "30", "15+15")]
    [InlineData("H1", "32", null)]
    [InlineData("H1", "26.5", null)]
    public void SharedDivisorRejectsEmployeeRowsBlanksCalculatedCachesAndInvalidDays(string address, string value, string? formula)
    {
        var request = Fixture();
        request.SelectedColumns.RemoveAll(column => column.Kind == "payrollDays");
        request.PayrollDaysCell = address;
        request.Source.Rows[0].Cells[7] = new() { Value = value, FormulaText = formula, DataType = "Number" };
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "PAYROLL_DAYS" && issue.Severity == "Error");
    }

    [Fact]
    public void TheSavedBlankBaseFailsClosedWithoutTheEmployeeOverride()
    {
        var result = SalaryTemplateExcelExtractionService.Extract(Fixture());
        result.Scenario!.AmountOverrides.Clear();
        var native = PayRunRepository.SimulateTemplate(result.Scenario);
        Assert.False(native.Complete);
        Assert.Contains(native.Issues, issue => issue.Component == "BASIC" && issue.Message.Contains("missing"));
        Assert.Null(native.Net);
        // This entry point invokes the same CalculateStructureLines/ReadRequiredAmount
        // used by regular payroll, without the simulation-only InspectTemplate check.
        var error = Assert.Throws<InvalidOperationException>(() => PayRunRepository.PreviewAdvisorTemplate(result.Scenario.Snapshot!, 30, 15));
        Assert.Contains("amount is missing", error.Message);
    }

    [Theory]
    [InlineData("0", "4400")]
    [InlineData("250", "4150")]
    public void PairedDirectFixedDeductionDoesNotReceiveASecondAttendanceProration(string value, string net)
    {
        var request = Fixture();
        request.Source.ColumnCount = 10;
        request.Source.Columns.AddRange([new() { ColumnIndex = 8, SourceHeader = "Monthly PT", Kind = "info" }, new() { ColumnIndex = 9, SourceHeader = "PT", Kind = "deduction" }]);
        request.Source.Rows[0].Cells.AddRange([new() { Value = "Monthly PT", DataType = "Text" }, new() { Value = "PT", DataType = "Text" }]);
        request.Source.Rows[1].Cells.AddRange([new() { Value = value, DataType = "Number" }, new() { Value = value, FormulaText = "I2", DataType = "Number" }]);
        request.Source.Rows[1].Cells[6] = new() { Value = net, FormulaText = "D2-E2-J2", DataType = "Number" };
        request.SelectedColumns.AddRange([new() { ColumnIndex = 8, Code = "PT", Name = "Monthly PT", Kind = "input", ReviewedFixedValue = true }, new() { ColumnIndex = 9, Code = "PT", Name = "PT", Kind = "deduction" }]);
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        var pt = result.Components.Single(component => component.Code == "PT");
        Assert.False(pt.ProRata); Assert.False(pt.RequiresEmployeeInput); Assert.Equal(value, pt.Value);
        Assert.Equal(decimal.Parse(net, System.Globalization.CultureInfo.InvariantCulture), result.Simulation!.Net);
    }

    [Fact]
    public void LiteralOutputRemainsRequiredInputUnlessCommonFixedValueIsExplicitlyReviewed()
    {
        var request = Fixture(); request.Source.Rows[1].Cells[4]!.FormulaText = null;
        var unreviewed = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(unreviewed.CanCreateDraft);
        Assert.Equal("", unreviewed.Components.Single(component => component.Code == "PF").Value);
        Assert.True(unreviewed.Components.Single(component => component.Code == "PF").RequiresEmployeeInput);
        request.SelectedColumns.Single(column => column.Code == "PF").ReviewedFixedValue = true;
        var reviewed = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.Equal("600", reviewed.Components.Single(component => component.Code == "PF").Value);
        Assert.False(reviewed.Components.Single(component => component.Code == "PF").RequiresEmployeeInput);
    }

    [Theory]
    [InlineData("A2/B2*C2+H2", "5005")]
    [InlineData("'Rates'!A2", "5000")]
    [InlineData("LOOKUP_RATE", "5000")]
    [InlineData("A99", "5000")]
    public void MissingOrUnreviewedDependenciesNeverBecomeCachedSalaryConstants(string formula, string cached)
    {
        var request = Fixture(); request.Source.Rows[1].Cells[3]!.FormulaText = formula; request.Source.Rows[1].Cells[3]!.Value = cached;
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Severity == "Error" && issue.Code == "BASIC");
        Assert.Equal("", result.Components.Single(component => component.Code == "BASIC").Value);
    }

    [Fact]
    public void AStaleFormulaCacheIsCaughtByTheExistingExcelCalculator()
    {
        var request = Fixture(); request.Source.Rows[1].Cells[4]!.Value = "999";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "PF" && issue.Message.Contains("shared Excel calculator"));
    }

    [Fact]
    public void ExplicitUnpairedRateCanBeTranslatedButNeverAnUnreviewedOrProtectedIdentifier()
    {
        var request = Fixture();
        request.SelectedColumns.RemoveAll(column => column.Kind is "grossTotal" or "netPay");
        request.Source.Rows[1].Cells[4]!.FormulaText = "D2*H2%"; request.Source.Rows[1].Cells[4]!.Value = "250";
        request.SelectedColumns.Add(new() { ColumnIndex = 7, Code = "RATE", Name = "Allowance percentage", Kind = "input", ReviewedFixedValue = true });
        var reviewed = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(reviewed.CanCreateDraft, string.Join(" | ", reviewed.Issues.Select(issue => issue.Message)));
        Assert.Equal("BASIC_EARNED*5%", reviewed.Components.Single(component => component.Code == "PF").Formula);
        request.Source.Columns[7].SourceHeader = "Mobile number";
        Assert.False(SalaryTemplateExcelExtractionService.Extract(request).CanCreateDraft);
    }

    [Fact]
    public void GrossControlsAreComparedAndAreNotExtraEarnings()
    {
        var request = Fixture(); request.Source.Rows[1].Cells[5]!.Value = "10000";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Equal(2, result.Components.Count);
        Assert.Contains(result.Comparisons, comparison => comparison.Code == "grossTotal" && comparison.Status == "Review");
    }

    [Fact]
    public void NumericContactAllowanceRemainsAnAmountInsteadOfBeingMistakenForAPhone()
    {
        var request = Fixture(); request.Source.Columns[3].SourceHeader = "Telephone allowance";
        Assert.True(SalaryTemplateExcelExtractionService.Extract(request).CanCreateDraft);
    }

    [Fact]
    public void AmbiguousRolesAndDuplicateOutputsAreRejectedBeforeCalculation()
    {
        var request = Fixture(); request.SelectedColumns.Add(new() { ColumnIndex = 7, Kind = "payableDays" });
        Assert.Throws<InvalidOperationException>(() => SalaryTemplateExcelExtractionService.Extract(request));
        request = Fixture(); request.SelectedColumns.Single(column => column.Code == "PF").Code = "BASIC";
        Assert.Throws<InvalidOperationException>(() => SalaryTemplateExcelExtractionService.Extract(request));
    }

    [Fact]
    public void ExistingConditionalAndDecimalRoundingGrammarIsPreserved()
    {
        var request = Fixture(); request.Source.Rows[1].Cells[4]!.FormulaText = "IF(A2>21000,0,ROUND(D2*12%,2))";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal("IF(BASIC_MONTHLY>21000,0,ROUND(BASIC_EARNED*12%,2))", result.Components.Single(component => component.Code == "PF").Formula);
        Assert.Equal(4400m, result.Simulation!.Net);
    }

    [Theory]
    [InlineData("monthlyCtc", "10000", "H2*6%", "MONTHLY_CTC*6%")]
    [InlineData("annualCtc", "120000", "H2/12*6%", "ANNUAL_CTC/12*6%")]
    public void ExplicitCtcInputsRemainNativeTargetsAndNotFixedFormulaCaches(string role, string amount, string formula, string translated)
    {
        var request = Fixture(); request.Source.Rows[1].Cells[7]!.Value = amount;
        request.Source.Columns[7].SourceHeader = role == "monthlyCtc" ? "Monthly CTC" : "Annual CTC";
        request.SelectedColumns.Add(new() { ColumnIndex = 7, Kind = role });
        request.Source.Rows[1].Cells[4]!.FormulaText = formula;
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(translated, result.Components.Single(component => component.Code == "PF").Formula);
        Assert.Equal(120000m, result.Scenario!.AnnualCtc);
        Assert.Equal("", result.Components.Single(component => component.Code == "PF").Value);
    }

    [Fact]
    public void CrossSheetCalculationUsesTheSharedExtractorButUnsupportedNativeRulesRemainBlocked()
    {
        var request = Fixture(); request.Source.Rows[1].Cells[4]!.FormulaText = "'Rates'!A2";
        request.Source.DependencySheets = [new() { WorkbookKey = "current", SheetName = "Rates", SourceHash = new string('a', 64), ColumnCount = 1,
            Rows = [new() { SourceRow = 2, Cells = [new() { Value = "600", DataType = "Number" }] }] }];
        Assert.Equal(600m, ExcelPayslipCalculationService.EvaluateCapturedCell(request.Source, "E2", requireExplicitCells: true).Value);
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "PF" && issue.Message.Contains("cross-sheet"));
        Assert.Equal("", result.Components.Single(component => component.Code == "PF").Value);
    }

    [Fact]
    public void ExtractionRequiresActiveSettingsPermissionAndHonorsSingleClientScope()
    {
        var user = new AuthUser { Id = 7, IsActive = true, ClientId = 42, Permissions = ["client.settings.manage"] };
        Assert.True(SalaryTemplateExcelExtractionService.CanAccess(user, 42));
        Assert.False(SalaryTemplateExcelExtractionService.CanAccess(user, 43));
        user.Permissions = ["payroll.run"]; Assert.False(SalaryTemplateExcelExtractionService.CanAccess(user, 42));
        user.Permissions = ["settings.manage"]; user.IsActive = false; Assert.False(SalaryTemplateExcelExtractionService.CanAccess(user, 42));
        user.IsActive = true; user.MustChangePassword = true; Assert.False(SalaryTemplateExcelExtractionService.CanAccess(user, 42));
    }

    [Fact]
    public void SavedComponentMetadataIsPreservedWithExplicitLineOverrides()
    {
        var request = Fixture(); request.SelectedColumns.Single(column => column.Code == "PF").ExistingComponentId = "77";
        var master = new PayrollReviewComponent("77", "PF", "Deduction", "Statutory Deduction", "EPF", "Fixed Amount", "0", "", "", true, false, true, 91, "Variable Pay");
        var result = SalaryTemplateExcelExtractionService.Extract(request, [master]);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        var candidate = result.Components.Single(component => component.Code == "PF");
        Assert.Equal("Statutory Deduction", candidate.ComponentRole); Assert.Equal("EPF", candidate.StatutoryType);
        Assert.False(candidate.Taxable); Assert.Equal(91, candidate.Priority); Assert.Equal("Variable Pay", candidate.PayType);
        Assert.Equal(master, result.Scenario!.Snapshot!.Components.Single(component => component.Id == "77"));
        Assert.Equal("false", result.Lines.Single(line => line.ComponentId == "77").ProRataOverride);
        Assert.Equal("Formula", result.Lines.Single(line => line.ComponentId == "77").CalculationType);
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("", "CTC*40%")]
    public void RequiredEmployeeInputsCannotInheritNonblankSharedMasterDefaults(string defaultValue, string defaultFormula)
    {
        var request = Fixture(); request.SelectedColumns.Single(column => column.Kind == "earning").ExistingComponentId = "77";
        var master = new PayrollReviewComponent("77", "BASIC", "Earning", "Regular Earning", "None", "Fixed Amount", defaultValue, defaultFormula, "", true, true, true, 1, "Fixed Pay");
        var result = SalaryTemplateExcelExtractionService.Extract(request, [master]);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "BASIC" && issue.Message.Contains("inherit"));
    }

    [Fact]
    public void MissingInactiveWrongCodeOrWrongCategoryMastersCannotBeReused()
    {
        var request = Fixture(); request.SelectedColumns.Single(column => column.Code == "PF").ExistingComponentId = "77";
        Assert.False(SalaryTemplateExcelExtractionService.Extract(request).CanCreateDraft);
        foreach (var master in new[] {
            new PayrollReviewComponent("77", "PF", "Earning", "Regular Earning", "None", "Fixed Amount", "", "", "", false, false, true, 1, "Fixed Pay"),
            new PayrollReviewComponent("77", "OTHER", "Deduction", "Other Deduction", "None", "Fixed Amount", "", "", "", false, false, true, 1, "Fixed Pay"),
            new PayrollReviewComponent("77", "PF", "Deduction", "Other Deduction", "None", "Fixed Amount", "", "", "", false, false, false, 1, "Fixed Pay") })
            Assert.False(SalaryTemplateExcelExtractionService.Extract(request, [master]).CanCreateDraft);
    }

    [Fact]
    public void SettingsProjectionAcceptsNumericIdsAndPreservesActualMasterFlags()
    {
        var masters = SalaryTemplateExcelExtractionService.ReadMasterCatalogue("""{"salaryComponents":[{"id":77,"code":"PF","category":"Deduction","componentRole":"Statutory Deduction","statutoryType":"EPF","calculationType":"Fixed Amount","value":"0","formula":"","baseComponent":"","proRata":true,"taxable":false,"active":true,"priority":"91","payType":"Variable Pay"}]}""");
        var master = Assert.Single(masters); Assert.Equal("77", master.Id); Assert.Equal(91, master.Priority);
        Assert.True(master.ProRata); Assert.False(master.Taxable); Assert.Equal("Variable Pay", master.PayType);
    }

    [Fact]
    public void PresentOnlySourceReferencesNeverBecomePayableReferencesByAliasing()
    {
        var request = Fixture(); request.SelectedColumns.Single(column => column.Kind == "payableDays").Kind = "presentDays";
        request.SelectedColumns.Single(column => column.Kind == "input").Kind = "ignore";
        request.SelectedColumns.Add(new() { ColumnIndex = 7, Code = "RATE", Name = "Monthly rate", Kind = "input", ReviewedFixedValue = true });
        request.Source.Columns[7].SourceHeader = "Monthly rate"; request.Source.Rows[1].Cells[7]!.Value = "10000";
        request.Source.Rows[1].Cells[3]!.FormulaText = "H2/B2*C2";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        var formula = result.Components.Single(component => component.Code == "BASIC").Formula;
        Assert.Contains("PRESENT_DAYS", formula); Assert.DoesNotContain("PAYABLE_DAYS", formula);
        Assert.Equal(4400m, result.Simulation!.Net);
    }

    [Fact]
    public void AMonthlyBaseUsingActualPresenceCannotSilentlyBecomeGenericPayableProration()
    {
        var request = Fixture(); request.SelectedColumns.Single(column => column.Kind == "payableDays").Kind = "presentDays";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "BASIC" && issue.Message.Contains("more than ordinary"));
    }

    [Fact]
    public void NewDraftCannotReuseARegisteredCodeWithoutExplicitCompatibleMasterSelection()
    {
        var request = Fixture();
        var master = new PayrollReviewComponent("77", "BASIC", "Earning", "Regular Earning", "None", "Fixed Amount", "", "", "", true, true, true, 1, "Fixed Pay");
        var result = SalaryTemplateExcelExtractionService.Extract(request, [master]);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "BASIC" && issue.Message.Contains("already uses this code"));
    }

    [Fact]
    public void FullMonthCtcSumUsesExistingSourceBasisProofAndActiveMonthlyDependencies()
    {
        var request = Fixture();
        request.Source.Columns[7].SourceHeader = "Full-month total";
        request.Source.Rows[1].Cells[7]!.FormulaText = "SUM(A2:A2)";
        request.Source.Rows[1].Cells[7]!.Value = "10000";
        request.Source.Rows[1].Cells[4]!.FormulaText = "H2*6%";
        request.SelectedColumns.Add(new() { ColumnIndex = 7, Kind = "monthlyCtc" });
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(120000m, result.Scenario!.AnnualCtc);
        Assert.Contains("BASIC_MONTHLY", result.Components.Single(component => component.Code == "PF").Formula);
        Assert.DoesNotContain("10000", result.Components.Single(component => component.Code == "PF").Formula);
        Assert.Equal(4400m, result.Simulation!.Net);
    }

    private static SalaryTemplateExcelExtractionRequest ComputedMonthlyFixture(string monthlyFormula = "A2*40%", bool lowBase = false)
    {
        var request = Fixture(); request.Source.ColumnCount = 10;
        request.Source.Columns.AddRange([new() { ColumnIndex = 8, SourceHeader = "Monthly housing allowance", Label = "Monthly housing allowance", Kind = "info" },
            new() { ColumnIndex = 9, SourceHeader = "Housing allowance earned", Label = "Housing allowance earned", Kind = "earning" }]);
        request.Source.Rows[0].Cells.AddRange([new() { DataType = "Text", Value = "Monthly housing allowance" }, new() { DataType = "Text", Value = "Housing allowance earned" }]);
        request.Source.Rows[1].Cells.AddRange([new() { DataType = "Number", Value = lowBase ? "0" : "4000", FormulaText = monthlyFormula },
            new() { DataType = "Number", Value = lowBase ? "0" : "2000", FormulaText = "I2/B2*C2" }]);
        request.Source.Rows[1].Cells[5]!.FormulaText = "D2+J2"; request.Source.Rows[1].Cells[5]!.Value = lowBase ? "500" : "7000";
        request.Source.Rows[1].Cells[6]!.FormulaText = "D2+J2-E2"; request.Source.Rows[1].Cells[6]!.Value = lowBase ? "440" : "6400";
        if (lowBase)
        {
            request.Source.Rows[1].Cells[0]!.Value = "1000";
            request.Source.Rows[1].Cells[3]!.Value = "500";
            request.Source.Rows[1].Cells[4]!.Value = "60";
        }
        request.SelectedColumns.AddRange([new() { ColumnIndex = 8, Code = "HOUSING", Name = "Housing monthly", Kind = "input" },
            new() { ColumnIndex = 9, Code = "HOUSING", Name = "Housing allowance", Kind = "earning" }]);
        return request;
    }

    [Fact]
    public void CalculatedMonthlyBaseIsExpandedThroughTheExistingTranslatorAndNeverFrozen()
    {
        var request = ComputedMonthlyFixture(); var before = JsonSerializer.Serialize(request);
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        var housing = result.Components.Single(component => component.Code == "HOUSING");
        Assert.Equal("Formula", housing.CalculationType); Assert.False(housing.ProRata);
        Assert.False(housing.RequiresEmployeeInput); Assert.Equal("", housing.Value);
        Assert.Contains("BASIC_MONTHLY*40%", housing.Formula); Assert.DoesNotContain("HOUSING_MONTHLY", housing.Formula);
        Assert.DoesNotContain("4000", housing.Formula); Assert.DoesNotContain(housing.Id, result.Scenario!.AmountOverrides.Keys);
        Assert.Equal(2000m, result.Simulation!.Lines.Single(line => line.Code == "HOUSING").Earned);
        Assert.Equal(6400m, result.Simulation.Net); Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false, 6400)]
    [InlineData(true, 440)]
    public void ComputedMonthlyConditionalKeepsItsLiveBranchesAndDecimalRounding(bool lowBase, int expectedNet)
    {
        var result = SalaryTemplateExcelExtractionService.Extract(ComputedMonthlyFixture("IF(A2>5000,ROUND(A2*40%,2),0)", lowBase));
        Assert.True(result.CanCreateDraft, string.Join(" | ", result.Issues.Select(issue => issue.Message)));
        var housing = result.Components.Single(component => component.Code == "HOUSING");
        Assert.Contains("IF(BASIC_MONTHLY>5000,ROUND(BASIC_MONTHLY*40%,2),0)", housing.Formula);
        Assert.Equal((decimal)expectedNet, result.Simulation!.Net);
        Assert.All(result.Comparisons, comparison => Assert.Equal("Matched", comparison.Status));
    }

    [Theory]
    [InlineData("I2*40%")]
    [InlineData("H2*40%")]
    public void ComputedMonthlyCyclesAndUnreviewedLiteralDependenciesRemainBlocked(string monthlyFormula)
    {
        var request = ComputedMonthlyFixture(monthlyFormula);
        request.Source.Rows[1].Cells[7]!.Value = "10000";
        var result = SalaryTemplateExcelExtractionService.Extract(request);
        Assert.False(result.CanCreateDraft);
        Assert.Contains(result.Issues, issue => issue.Code == "HOUSING" && issue.Severity == "Error");
        Assert.Equal("", result.Components.Single(component => component.Code == "HOUSING").Value);
    }
}
