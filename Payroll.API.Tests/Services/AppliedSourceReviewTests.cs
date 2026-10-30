using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

// Anonymous in-memory source/configuration evidence only. These tests neither
// query a database nor certify native attendance, tax policy, or saved payroll.
public sealed class AppliedSourceReviewTests
{
    private const string BatchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImportId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RowId = "11111111-1111-4111-8111-111111111111";

    private sealed class Fixture
    {
        public ExcelPayrollSetupRequest Original { get; } = new()
        {
            BatchId = BatchId, TransferReviewVersion = 0, RowIds = [RowId], TemplateName = "Synthetic reviewed salary",
            EffectiveFrom = "2026-09-01", WorkWeek = "Sunday off", PayrollDaysColumnIndex = 0,
            PayableDaysColumnIndex = 1, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
            PayrollReportGenerationDay = 3, ImportAttendance = false,
            EmployeeDecisions = [new() { RowId = RowId, MatchedEmployeeId = 11 }],
            Components =
            [
                new() { Key = "wages", ExistingComponentId = "1", Code = "WAGES", Name = "Wages",
                    Category = "Earning", ComponentRole = "Regular Earning", StatutoryType = "None",
                    CalculationType = "Fixed Amount", Value = "0", SourceColumnIndex = 2, ExpectedColumnIndex = 3,
                    ProRata = true, Taxable = true, Priority = 10, PayType = "Fixed Pay" },
                new() { Key = "travel", ExistingComponentId = "2", Code = "TA_DA", Name = "Travel",
                    Category = "Reimbursement", ComponentRole = "Regular Earning", StatutoryType = "None",
                    CalculationType = "Manual / Variable", Value = "0", SourceColumnIndex = 4, ExpectedColumnIndex = 4,
                    IsPeriodAdjustment = true, ProRata = false, Taxable = false, Priority = 20, PayType = "Variable Pay" }
            ]
        };

        public ExcelPayslipBatch Batch { get; } = new()
        {
            Id = BatchId, ClientId = 7, Month = "2026-09", SheetName = "Salary", HeaderRow = 8,
            Rows = [new()
            {
                Id = RowId, SourceRow = 9, EmployeeCode = "SYN11", EmployeeName = "Synthetic Person",
                Information =
                [
                    new() { Label = "Payroll days", Value = "30" }, new() { Label = "Payable days", Value = "30" },
                    new() { Label = "Monthly wages", Value = "145000" }, new() { Label = "Actual present", Value = "30" }
                ],
                Earnings = [new() { Label = "Wages", Amount = 145000m }, new() { Label = "Travel", Amount = 22680m }],
                DeclaredGross = 167680m, DeclaredDeductions = 0m, NetPay = 167680m
            }]
        };

        public ExcelPayslipCalculationSource Source { get; } = new()
        {
            SheetName = "Salary", HeaderRow = 8, ColumnCount = 9,
            Columns =
            [
                Column(0, "Payroll days", "info"), Column(1, "Payable days", "info"), Column(2, "Monthly wages", "info"),
                Column(3, "Wages", "earning"), Column(4, "Travel", "earning"), Column(5, "Employee name", "employeeName"),
                Column(6, "Employee code", "employeeCode"), Column(7, "Net", "netPay"), Column(8, "Actual present", "info")
            ],
            Rows = [new()
            {
                SourceRow = 9,
                Cells =
                [
                    Cell("30"), Cell("30"), Cell("145000"), Cell("145000", "C9/A9*B9"), Cell("22680"),
                    new() { DataType = "Text", Value = "Synthetic Person" }, new() { DataType = "Text", Value = "SYN11" },
                    Cell("167680", "D9+E9"), Cell("30")
                ]
            }]
        };

        public ExcelPayrollSetupResult Receipt { get; } = new()
        {
            ImportId = ImportId, EmployeeIds = [11], TemplateId = "500", TemplateIds = ["500"], UpdatedCount = 1
        };

        public Employee Current { get; } = new()
        {
            Id = 11, ClientId = 7, EmployeeCode = "SYN11", FirstName = "Synthetic", LastName = "Person",
            DateOfJoining = "2026-01-01", SalaryStructureId = "500", AnnualCtc = 1740000m,
            SalaryJson = "{\"1\":145000,\"2\":0}", SalaryComponents = new() { ["1"] = 145000m, ["2"] = 0m }
        };

        public JsonObject Settings { get; } = JsonNode.Parse("""
            {"salaryComponents":[
              {"id":"1","code":"WAGES","name":"Wages","category":"Earning","componentRole":"Regular Earning","statutoryType":"None",
               "calculationType":"Fixed Amount","value":"0","formula":"","baseComponent":"","proRata":true,"taxable":true,"active":true,"priority":"10","payType":"Fixed Pay"},
              {"id":"2","code":"TA_DA","name":"Travel","category":"Reimbursement","componentRole":"Regular Earning","statutoryType":"None",
               "calculationType":"Manual / Variable","value":"0","formula":"","baseComponent":"","proRata":false,"taxable":false,"active":true,"priority":"20","payType":"Variable Pay"}],
             "salaryStructures":[{"id":"500","clientId":"7","name":"Synthetic reviewed salary","active":true,"annualCtc":"1740000","lines":[
              {"componentId":"1","value":"0","calculationType":"Fixed Amount","formula":"","baseComponent":"","proRataOverride":"true","roundingMode":""},
              {"componentId":"2","value":"0","calculationType":"Manual / Variable","formula":"","baseComponent":"","proRataOverride":"false","roundingMode":""}]}]}
            """)!.AsObject();

        public List<Employee> Identities { get; }
        public Fixture() => Identities = [Current];
        public ExcelPayrollSetupRepository.AppliedSourceRowReview Review(string? nativeDetailsJson = null, NativePayrollAttendanceInput? native = null) =>
            ExcelPayrollSetupRepository.ReviewAppliedSourceRow(7, 11, Batch, Source, Original, Receipt, Settings.ToJsonString(), Current, Identities, nativeDetailsJson, native);
        public string Snapshot() => JsonSerializer.Serialize(new { Batch, Source, Original, Receipt, Current, Identities, Settings });

        private static ExcelPayslipCalculationColumn Column(int index, string label, string kind) =>
            new() { ColumnIndex = index, SourceHeader = label, Label = label, Kind = kind };
        private static ExcelPayslipCalculationCell Cell(string value, string? formula = null) =>
            new() { DataType = "Number", Value = value, FormulaText = formula };
    }

    private static NativePayrollAttendanceInput NativeDays(string month = "2026-09") =>
        new(11,7,month,30,27,30,month+"-01",month+"-30",new string('c',64),"");

    private static Fixture ConditionalFixture()
    {
        var f=new Fixture();
        f.Original.Components.Add(new(){Key="bonus",ExistingComponentId="3",Code="BONUS",Name="Bonus",Category="Earning",
            ComponentRole="Regular Earning",StatutoryType="None",CalculationType="Formula",Value="0",Formula="0",
            ExpectedColumnIndex=9,Taxable=true,Priority=30,PayType="Fixed Pay"});
        f.Source.ColumnCount=10;f.Source.Columns.Add(new(){ColumnIndex=9,SourceHeader="Bonus",Label="Bonus",Kind="earning"});
        f.Source.Rows[0].Cells.Add(new(){DataType="Number",Value="0",FormulaText="IF(C9<=21000,D9*10%,0)"});
        f.Batch.Rows[0].Earnings.Add(new(){Label="Bonus",Amount=0});
        ((JsonArray)f.Settings["salaryComponents"]!).Add(JsonNode.Parse("""{"id":"3","code":"BONUS","name":"Bonus","category":"Earning","componentRole":"Regular Earning","statutoryType":"None","calculationType":"Formula","value":"0","formula":"0","baseComponent":"","proRata":false,"taxable":true,"active":true,"priority":"30","payType":"Fixed Pay"}"""));
        ((JsonArray)f.Settings["salaryStructures"]![0]!["lines"]!).Add(JsonNode.Parse("""{"componentId":"3","value":"0","calculationType":"Formula","formula":"0","baseComponent":"","proRataOverride":"false","roundingMode":""}"""));
        return f;
    }

    [Fact]
    public void Current_source_review_accepts_corrected_effective_conditional_rule_without_rewriting_legacy_receipt()
    {
        var f=ConditionalFixture();
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review("[]",NativeDays())); // Equal zero is not rule parity.
        f.Settings["salaryStructures"]![0]!["lines"]![2]!["formula"]="IF(WAGES_MONTHLY<=21000,WAGES_EARNED*10%,0)";
        var before=f.Snapshot();var review=f.Review("[]",NativeDays());
        Assert.Equal("IF(WAGES_MONTHLY<=21000,WAGES_EARNED*10%,0)",review.Lines.Single(l=>l.Code=="BONUS").NativeFormula);
        Assert.Equal(before,f.Snapshot());Assert.Equal("0",f.Original.Components[2].Formula);
    }

    [Fact]
    public void Current_source_review_blocks_frozen_employee_zero_even_when_the_new_conditional_amount_matches()
    {
        var f=ConditionalFixture();f.Settings["salaryStructures"]![0]!["lines"]![2]!["formula"]="IF(WAGES_MONTHLY<=21000,WAGES_EARNED*10%,0)";
        f.Current.SalaryComponents["3"]=0;
        Assert.Contains("old stored amount",Assert.Throws<SelectedPayRunConflictException>(()=>f.Review("[]",NativeDays())).Message);
    }

    [Fact]
    public void Current_source_fixed_input_review_is_new_native_bound_evidence_not_a_historical_acknowledgment()
    {
        var f=new Fixture();f.Original.ReviewedFixedInputs=false;
        f.Original.Components[0].ProRata=false;f.Settings["salaryStructures"]![0]!["lines"]![0]!["proRataOverride"]="false";
        f.Source.Rows[0].Cells[3]!.FormulaText=null;
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
        var before=f.Snapshot();var result=f.Review("[]",NativeDays());
        Assert.Contains("current fixed-input semantics",result.Lines.Single(l=>l.Code=="WAGES").FormulaStatus);
        Assert.Equal(before,f.Snapshot());Assert.False(f.Original.ReviewedFixedInputs);
    }

    [Fact]
    public void Native_day_boundary_reuses_payable_mapping_without_recreating_lookup_or_freezing_salary()
    {
        var f = new Fixture();
        f.Source.Rows[0].Cells[1]!.FormulaText = "IFERROR(VLOOKUP(G9,Attendance!A1:D50,4,FALSE),0)";
        var before = f.Snapshot();
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review());
        var result = f.Review(native: NativeDays());
        Assert.Equal(167680m,result.Arithmetic.Net);
        Assert.Contains("original lookup closure not certified",result.Lines.Single(l=>l.Code=="WAGES").FormulaStatus);
        Assert.Null(result.SourcePresentDays); // Native payable never claims source presence.
        Assert.Equal(before,f.Snapshot());
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.EvaluateCapturedCell(f.Source,"D9",true));
    }

    [Theory]
    [InlineData("employee")] [InlineData("client")] [InlineData("month")] [InlineData("cycle")]
    [InlineData("payable")] [InlineData("divisor")] [InlineData("hash")] [InlineData("policy-error")]
    [InlineData("missing-cache")] [InlineData("error")] [InlineData("cache")] [InlineData("financial-role")]
    public void Native_day_boundary_blocks_stale_wrong_scope_or_invalid_inputs(string mutation)
    {
        var f = new Fixture(); var native = NativeDays();
        f.Source.Rows[0].Cells[1]!.FormulaText = "VLOOKUP(G9,Attendance!A1:D50,4,FALSE)";
        switch(mutation)
        {
            case "employee": native=native with{EmployeeId=12}; break;
            case "client": native=native with{ClientId=8}; break;
            case "month": native=native with{Month="2026-10"}; break;
            case "cycle": native=native with{CycleStart="2026-08-31"}; break;
            case "payable": native=native with{PayableDays=29}; break;
            case "divisor": native=native with{PayrollDays=31}; break;
            case "hash": native=native with{SnapshotHash=""}; break;
            case "policy-error": native=native with{Error="Unreviewed policy"}; break;
            case "missing-cache": f.Source.Rows[0].Cells[1]!.MissingCachedValue=true; break;
            case "error": f.Source.Rows[0].Cells[1]!.Error="#N/A"; break;
            case "cache": f.Source.Rows[0].Cells[1]!.Value="29"; break;
            case "financial-role": f.Original.SourceColumnRoles[1]="earning"; break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(native:native));
    }

    [Fact]
    public void Native_day_boundary_does_not_replace_missing_financial_lookup_or_source_formula()
    {
        var f = new Fixture();
        f.Source.Rows[0].Cells[1]!.FormulaText="VLOOKUP(G9,Attendance!A1:D50,4,FALSE)";
        f.Source.Rows[0].Cells[3]!.FormulaText="VLOOKUP(G9,UnreviewedSalary!A1:D50,4,FALSE)";
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(native:NativeDays()));
    }

    [Theory]
    [InlineData("30+1")]
    [InlineData("1/0")]
    [InlineData("VLOOKUP(G9,Attendance!A1:D50,4,TRUE)")]
    [InlineData("IFERROR(VLOOKUP(G9,Attendance!A1:D50,4,FALSE),30)")]
    public void Native_day_boundary_never_hides_stale_arithmetic_approximate_lookup_or_plausible_fallback(string formula)
    {
        var f=new Fixture();f.Source.Rows[0].Cells[1]!.FormulaText=formula;
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(native:NativeDays()));
    }

    [Fact]
    public void Native_day_boundary_keeps_zero_fallback_distinct_from_proven_zero_and_lwp_basis_explicit()
    {
        var f = new Fixture(); var arithmetic=f.Review().Arithmetic;
        arithmetic.PayableDays=0;
        f.Source.Rows[0].Cells[1]!.Value="0";
        f.Source.Rows[0].Cells[1]!.FormulaText="IFERROR(VLOOKUP(G9,Attendance!A1:D50,4,FALSE),0)";
        Assert.Throws<SelectedPayRunConflictException>(()=>ExcelPayrollSetupRepository.ReviewAppliedNativeDayInputs(
            7,11,f.Batch,f.Source,f.Batch.Rows[0],f.Original,arithmetic,NativeDays() with{PayableDays=0}));
        f.Source.Rows[0].Cells[1]!.FormulaText=null;
        var proven=ExcelPayrollSetupRepository.ReviewAppliedNativeDayInputs(7,11,f.Batch,f.Source,f.Batch.Rows[0],f.Original,arithmetic,NativeDays() with{PayableDays=0});
        Assert.Equal(0m,proven["B9"]);
        f.Original.AttendanceReview=new(){Reviewed=true,LwpColumnIndex=8,LwpBasis=""};
        Assert.Throws<SelectedPayRunConflictException>(()=>ExcelPayrollSetupRepository.ReviewAppliedNativeDayInputs(
            7,11,f.Batch,f.Source,f.Batch.Rows[0],f.Original,arithmetic,NativeDays() with{PayableDays=0}));
    }

    [Fact]
    public void Native_day_boundary_is_period_scoped_and_works_for_a_31_day_month()
    {
        var f = new Fixture(); f.Batch.Month="2026-10"; f.Original.AttendanceCycleEndDay=31;
        f.Source.Rows[0].Cells[0]!.Value="31"; f.Source.Rows[0].Cells[1]!.Value="31";
        f.Batch.Rows[0].Information[0].Value="31"; f.Batch.Rows[0].Information[1].Value="31";
        f.Source.Rows[0].Cells[1]!.FormulaText="VLOOKUP(G9,Attendance!A1:D50,4,FALSE)";
        var native=NativeDays("2026-10") with{PayrollDays=31,PayableDays=31,CycleEnd="2026-10-31"};
        Assert.Equal(167680m,f.Review(native:native).Arithmetic.Net);
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(native:native with{PayableDays=30}));
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(native:NativeDays()));
    }

    [Fact]
    public void Native_day_source_review_uses_existing_representation_precision_without_ignoring_small_business_differences()
    {
        var f=new Fixture();f.Source.Rows[0].Cells[2]!.Value="72500";f.Source.Rows[0].Cells[3]!.Value="72500";
        f.Source.Rows[0].Cells[7]!.Value="95180";f.Batch.Rows[0].Information[2].Value="72500";
        f.Batch.Rows[0].Earnings[0].Amount=72500;f.Batch.Rows[0].DeclaredGross=95180;f.Batch.Rows[0].NetPay=95180;
        f.Current.AnnualCtc=870000;f.Current.SalaryComponents["1"]=72500;
        f.Settings["salaryStructures"]![0]!["annualCtc"]="870000";
        Assert.Equal(95180m,f.Review().Arithmetic.Net);
        f.Source.Rows[0].Cells[3]!.FormulaText="C9/A9*B9+0.00001";
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
    }

    [Fact]
    public void Native_day_source_review_honors_only_explicit_reviewed_blank_columns()
    {
        var f=new Fixture();f.Source.Rows[0].Cells[4]!.Value="";
        f.Batch.Rows[0].Earnings[1].Amount=0;f.Batch.Rows[0].DeclaredGross=145000;f.Batch.Rows[0].NetPay=145000;
        f.Source.Rows[0].Cells[7]!.Value="145000";
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
        f.Source.Columns[4].BlankAsZero=true;
        Assert.Equal(145000m,f.Review().Arithmetic.Net);
        f.Source.Rows[0].Cells[4]!.MissingCachedValue=true;
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
    }

    [Fact]
    public void Native_day_boundary_maps_reviewed_lwp_to_divisor_minus_payable_and_preserves_source()
    {
        var f=new Fixture(); f.Source.Rows[0].Cells[8]!.Value="0";f.Batch.Rows[0].Information[3].Value="0";
        var arithmetic=f.Review().Arithmetic;
        f.Original.AttendanceReview=new(){Mode="VerifyExistingNative",Reviewed=true,LwpColumnIndex=8,LwpBasis="PayrollDivisorMinusPayable",
            SourceMonth=f.Batch.Month,SourceHash=ExcelPayslipRepository.EmploymentSourceHash(f.Batch,f.Source),
            CycleStart="2026-09-01",CycleEnd="2026-09-30",PayableMeaning="AlreadyPayable",PresentMeaning="NotMapped"};
        f.Original.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(f.Original);
        var before=f.Snapshot();var boundary=ExcelPayrollSetupRepository.ReviewAppliedNativeDayInputs(7,11,f.Batch,f.Source,
            f.Batch.Rows[0],f.Original,arithmetic,NativeDays());
        Assert.Equal(0m,boundary["I9"]);Assert.Equal(before,f.Snapshot());
    }

    [Fact]
    public void Legacy_applied_review_derives_complete_source_proof_without_reapplying_or_rewriting_history()
    {
        var f = new Fixture(); var before = f.Snapshot(); var result = f.Review();
        Assert.Equal(30, result.PayrollDays); Assert.Equal(30m, result.PayableDays);
        Assert.Null(result.SourcePresentDays); Assert.Equal("NotMapped", result.SourceActualWorkedMeaning);
        Assert.Equal(167680m, result.Arithmetic.Gross); Assert.Equal(0m, result.Arithmetic.Deductions);
        Assert.Equal(167680m, result.Arithmetic.Net); Assert.Equal(2, result.Lines.Count); Assert.Equal(2, result.Components.Count);
        var travel = Assert.Single(result.Lines,line => line.Code == "TA_DA");
        Assert.Equal("Reimbursement", travel.Category); Assert.Equal(22680m, travel.SourceRaw); Assert.Equal(22680m, travel.CoreAmount);
        Assert.Equal("Reviewed existing period literal", travel.FormulaStatus);
        Assert.Equal("Exact supported translation", Assert.Single(result.Lines,line => line.Code == "WAGES").FormulaStatus);
        Assert.All(new[] { result.SourceHash, result.MappingFingerprint, result.IdentityFingerprint, result.FormulaFingerprint },
            fingerprint => Assert.Matches("^[a-fA-F0-9]{64}$", fingerprint));
        Assert.Null(f.Receipt.Transfer); Assert.Empty(f.Receipt.TransferRows); Assert.Equal(0, f.Original.TransferReviewVersion);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Effective_template_overrides_remain_authoritative_when_unused_master_defaults_and_priority_differ()
    {
        var f = new Fixture();
        f.Settings["salaryComponents"]![0]!["priority"] = "90";
        f.Settings["salaryComponents"]![0]!["calculationType"] = "Formula";
        f.Settings["salaryComponents"]![0]!["formula"] = "999";
        f.Settings["salaryComponents"]![0]!["proRata"] = false;
        var before = f.Snapshot(); var result = f.Review("[]");
        Assert.Equal(167680m, result.Arithmetic.Net);
        Assert.Contains("Effective template rules verified", result.Lines.Single(l => l.Code == "WAGES").FormulaStatus);
        Assert.Equal(before, f.Snapshot());
        f.Settings["salaryStructures"]![0]!["lines"]![0]!["proRataOverride"] = "false";
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review("[]"));
    }

    [Fact]
    public void Source_present_column_remains_unmapped_instead_of_certifying_calculator_fallback_as_actual_worked()
    {
        var f = new Fixture(); f.Source.Rows[0].Cells[8]!.Value = "27"; f.Batch.Rows[0].Information[3].Value = "27";
        var result = f.Review();
        Assert.Equal(30m, result.Arithmetic.PresentDays); Assert.Null(result.SourcePresentDays);
        Assert.Equal("NotMapped", result.SourceActualWorkedMeaning);
    }

    [Fact]
    public void Current_saved_source_hash_is_rederived_and_not_copied_from_a_legacy_receipt()
    {
        var f = new Fixture(); var first = f.Review(); var repeated = f.Review();
        Assert.Equal(first.SourceHash, repeated.SourceHash); Assert.Equal(first.MappingFingerprint, repeated.MappingFingerprint);
        Assert.Equal(first.IdentityFingerprint, repeated.IdentityFingerprint); Assert.Equal(first.FormulaFingerprint, repeated.FormulaFingerprint);
        f.Source.Rows[0].Cells[8]!.Value = "27"; f.Batch.Rows[0].Information[3].Value = "27";
        Assert.NotEqual(first.SourceHash, f.Review().SourceHash);
    }

    [Fact]
    public void Explicit_existing_one_period_literal_does_not_require_rewriting_a_legacy_fixed_input_acknowledgment()
    {
        var f = new Fixture(); f.Original.ReviewedFixedInputs = false;
        var result = f.Review();
        Assert.Equal(22680m, Assert.Single(result.Lines,line => line.Code == "TA_DA").CoreAmount);
        Assert.False(f.Original.ReviewedFixedInputs); Assert.Null(f.Receipt.Transfer);
    }

    [Theory]
    [InlineData("client")] [InlineData("batch")] [InlineData("missing-decision")] [InlineData("wrong-decision")]
    [InlineData("duplicate-decision")] [InlineData("receipt-membership")] [InlineData("duplicate-membership")]
    [InlineData("kept-source")] [InlineData("missing-source-row")] [InlineData("duplicate-source-row")]
    public void Exact_explicit_source_employee_and_applied_receipt_binding_is_required(string mutation)
    {
        var f = new Fixture();
        switch (mutation)
        {
            case "client": f.Batch.ClientId = 8; break;
            case "batch": f.Original.BatchId = new string('c', 32); break;
            case "missing-decision": f.Original.EmployeeDecisions.Clear(); break;
            case "wrong-decision": f.Original.EmployeeDecisions[0].MatchedEmployeeId = 12; break;
            case "duplicate-decision": f.Original.EmployeeDecisions.Add(new() { RowId = RowId, MatchedEmployeeId = 11 }); break;
            case "receipt-membership": f.Receipt.EmployeeIds.Clear(); break;
            case "duplicate-membership": f.Receipt.EmployeeIds.Add(11); break;
            case "kept-source": f.Receipt.KeptRows.Add(new(RowId, 11, "500", "Synthetic kept assignment")); break;
            case "missing-source-row": f.Source.Rows.Clear(); break;
            case "duplicate-source-row": f.Source.Rows.Add(f.Source.Rows[0]); break;
        }
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review());
    }

    [Theory]
    [InlineData("cache")] [InlineData("missing-cache")] [InlineData("error")] [InlineData("calculation-error")]
    [InlineData("unsupported-formula")] [InlineData("different-formula")] [InlineData("period-formula")]
    [InlineData("financial-amount")] [InlineData("net-total")] [InlineData("output-role")]
    [InlineData("missing-output")]
    public void Source_closure_values_output_roles_and_literal_period_semantics_are_not_assumed(string mutation)
    {
        var f = new Fixture();
        switch (mutation)
        {
            case "cache": f.Source.Rows[0].Cells[3]!.Value = "144999"; break;
            case "missing-cache": f.Source.Rows[0].Cells[3]!.MissingCachedValue = true; break;
            case "error": f.Source.Rows[0].Cells[3]!.Error = "#REF!"; break;
            case "calculation-error": f.Source.Rows[0].Cells[3]!.CalculationError = "Unresolved dependency"; break;
            case "unsupported-formula": f.Source.Rows[0].Cells[3]!.FormulaText = "VLOOKUP(C9,Missing!A1:B2,2,0)"; break;
            case "different-formula": f.Source.Rows[0].Cells[3]!.FormulaText = "C9/A9*B9+1"; break;
            case "period-formula": f.Source.Rows[0].Cells[4]!.FormulaText = "22680+0"; break;
            case "financial-amount": f.Batch.Rows[0].Earnings[1].Amount = 22681m; break;
            case "net-total": f.Batch.Rows[0].NetPay = 167681m; break;
            case "output-role": f.Original.SourceColumnRoles[4] = "employer"; break;
            case "missing-output": f.Original.Components[1].ExpectedColumnIndex = null; break;
        }
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review());
    }

    [Theory]
    [InlineData("master-category")] [InlineData("master-tax")] [InlineData("master-paytype")]
    [InlineData("template-proration")] [InlineData("template-formula")] [InlineData("template-inactive")]
    [InlineData("template-client")] [InlineData("missing-template")] [InlineData("identity-conflict")]
    [InlineData("identity-missing")] [InlineData("duplicate-master-code")]
    public void Current_master_template_and_strict_identity_are_independently_verified(string mutation)
    {
        var f = new Fixture();
        switch (mutation)
        {
            case "master-category": f.Settings["salaryComponents"]![1]!["category"] = "Earning"; break;
            case "master-tax": f.Settings["salaryComponents"]![1]!["taxable"] = true; break;
            case "master-paytype": f.Settings["salaryComponents"]![1]!["payType"] = "Fixed Pay"; break;
            case "template-proration": f.Settings["salaryStructures"]![0]!["lines"]![1]!["proRataOverride"] = "true"; break;
            case "template-formula": f.Settings["salaryStructures"]![0]!["lines"]![0]!["formula"] = "PAYABLE_DAYS"; break;
            case "template-inactive": f.Settings["salaryStructures"]![0]!["active"] = false; break;
            case "template-client": f.Settings["salaryStructures"]![0]!["clientId"] = "8"; break;
            case "missing-template": f.Current.SalaryStructureId = "501"; break;
            case "identity-conflict": f.Current.EmployeeCode = "SYN12"; break;
            case "identity-missing": f.Identities.Clear(); break;
            case "duplicate-master-code": f.Identities.Add(new() { Id = 12, ClientId = 7, EmployeeCode = "SYN11", FirstName = "Other", LastName = "Person" }); break;
        }
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review());
    }

    [Theory]
    [InlineData("roles")] [InlineData("cell-value")] [InlineData("components")] [InlineData("receipt-membership")]
    public void Malformed_metadata_is_an_actionable_conflict(string mutation)
    {
        var f = new Fixture();
        switch (mutation)
        {
            case "roles": f.Original.SourceColumnRoles = null!; break;
            case "cell-value": f.Source.Rows[0].Cells[4]!.Value = null!; break;
            case "components": f.Original.Components = null!; break;
            case "receipt-membership": f.Receipt.EmployeeIds = null!; break;
        }
        Assert.Throws<SelectedPayRunConflictException>(() => f.Review());
    }

    [Fact]
    public void Existing_transfer_v1_adjustment_lane_still_rejects_legacy_receipt_without_new_provenance_review()
    {
        var f = new Fixture();
        var adjustment = new PayrollAdjustment
        {
            Id = 101, ClientId = 7, EmployeeId = 11, ComponentCode = "TA_DA", ComponentName = "Travel",
            AdjustmentType = "Reimbursement", Amount = 22680m, PayPeriod = "2026-09", PayRunType = "Regular",
            ReasonCode = "EXCEL_SETUP", Status = "Approved", Taxable = false,
            Notes = $"Reviewed salary Excel {BatchId}; import {ImportId}; source row 9"
        };
        var request = new CreatePayRunRequest { ClientId = 7, PayPeriod = "2026-09", RunType = "Regular", IncludedEmployeeIds = [11], AdjustmentIds = [101] };
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunAdjustments.Review(request, adjustment,
            SelectedPayRunAdjustments.Reference(adjustment), f.Batch, f.Source, f.Original, f.Receipt,
            new string('c', 64), new string('d', 64)));
        Assert.Equal("Approved", adjustment.Status); Assert.Null(adjustment.PayRunId); Assert.Null(f.Receipt.Transfer);
    }
}
