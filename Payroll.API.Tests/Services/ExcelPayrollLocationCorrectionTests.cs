using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Assignment = Payroll.API.Repositories.ExcelPayrollSetupRepository.LocationAssignmentStorageRow;
using Allocation = Payroll.API.Repositories.ExcelPayrollSetupRepository.LocationAllocationStorageRow;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollLocationCorrectionTests
{
    [Fact]
    public void Reviewed_period_storage_status_fits_the_existing_assignment_column()
    {
        Assert.InRange(ReviewedLocationCorrectionOrigins.Status.Length,1,20);
        Assert.NotEqual("Active",ReviewedLocationCorrectionOrigins.Status);
        Assert.NotEqual("ReviewedPeriodCorrection",ReviewedLocationCorrectionOrigins.Status);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string BatchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", AssignmentId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ImportId = "cccccccccccccccccccccccccccccccc";
    internal sealed record Fixture(ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source, ExcelPayrollSetupRequest Request,
        ExcelPayrollSetupResult Receipt, string Setup, Employee Employee, List<ExcelPayrollSetupRepository.Identity> Identities,
        List<Assignment> Assignments, List<Allocation> Allocations);
    internal static Fixture Create(decimal percent = 20, bool addExactTemplate = false)
    {
        var earning = 54000 * percent / 100; var net = earning - 100;
        var source = new ExcelPayslipCalculationSource { SheetName = "Synthetic salary", HeaderRow = 2, ColumnCount = 12,
            Columns = Enumerable.Range(0,12).Select(i => new ExcelPayslipCalculationColumn { ColumnIndex = i, Label = "Synthetic " + i,
                SourceHeader = "Synthetic " + i, Kind = i == 0 ? "employeeCode" : i == 1 ? "employeeName" : i == 4 ? "ignore" : i == 8 ? "earning" : i == 9 ? "deduction" : i == 10 ? "employer" : i == 11 ? "netPay" : "info" }).ToList(),
            Rows = [new() { SourceRow = 3, Cells = [new() { Value = "SYN-LOC-1" },new() { Value = "Synthetic Person" },new() { Value = "ABCDE1234F" },
                new() { Value = "31" },new() { Value = "31" },new() { Value = "31" },new() { Value = "54000" },new() { Value = percent.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Value = earning.ToString(System.Globalization.CultureInfo.InvariantCulture), FormulaText = "=ROUND(G3*H3%/D3*F3,0)" },
                new() { Value = "100" },new() { Value = "50" },new() { Value = net.ToString(System.Globalization.CultureInfo.InvariantCulture), FormulaText = "=I3-J3" }] }] };
        source.Columns[2].Label="PAN";source.Columns[2].SourceHeader="PAN";
        source.Columns[8].Label="Synthetic allowance";source.Columns[9].Label="Synthetic deduction";source.Columns[10].Label="Synthetic employer";
        var row = new ExcelPayslipRow { Id = "source-one", SourceRow = 3, EmployeeCode = "SYN-LOC-1", EmployeeName = "Synthetic Person", NetPay = net,
            DeclaredGross = earning, DeclaredDeductions = 100, Information = [new() { Label = "PAN", Value = "ABCDE1234F" }],
            Earnings = [new() { Label = "Synthetic allowance", Amount = earning }], Deductions = [new() { Label = "Synthetic deduction", Amount = 100 }], EmployerContributions = [new() { Label = "Synthetic employer", Amount = 50 }] };
        row.Information=source.Columns.Where(column=>column.Kind=="info").Select(column=>new ExcelPayslipText{Label=column.Label,Value=source.Rows[0].Cells[column.ColumnIndex]!.Value}).ToList();
        var batch = new ExcelPayslipBatch { Id = BatchId, ClientId = 42, Month = "2026-10", SheetName = source.SheetName, HeaderRow = 2, Rows = [row] };
        var request = new ExcelPayrollSetupRequest { BatchId = BatchId, RowIds = [row.Id], TemplateName = "Synthetic location", EffectiveFrom = "2026-11-14", WorkWeek = "Sunday off",
            PayrollDaysColumnIndex = 3, PresentDaysColumnIndex = null, PayableDaysColumnIndex = 5, AttendanceCycleEndDay=31, ReviewedFixedInputs = true, ReviewedPolicyScope = true, TransferReviewVersion = 1,
            SourceColumnRoles = Enumerable.Range(0,12).ToDictionary(i => i, i => source.Columns[i].Kind),
            Components = [new() { Key = "earning", ExistingComponentId = "1", Code = "SYN_ALLOWANCE", Name = "Synthetic allowance", Category = "Earning", CalculationType = "Formula", Formula = "ROUND(((54000)*(20)%)/(PAYROLL_DAYS)*(PAYABLE_DAYS),0)", ExpectedColumnIndex = 8, ProRata = false, Priority = 100, PayType = "Fixed Pay" },
                new() { Key = "deduction", ExistingComponentId = "2", Code = "SYN_DEDUCTION", Name = "Synthetic deduction", Category = "Deduction", ComponentRole = "Regular Deduction", Value = "100", ExpectedColumnIndex = 9, ProRata = false, Priority = 200, PayType = "Fixed Pay" },
                new() { Key = "employer", ExistingComponentId = "3", Code = "SYN_EMPLOYER", Name = "Synthetic employer", Category = "Benefit", ComponentRole = "Employer Benefit", Value = "50", ExpectedColumnIndex = 10, ProRata = false, Priority = 300, PayType = "Fixed Pay" }],
            LocationAllocations = new() { PayrollPeriod = batch.Month, ConfigurationEffectiveFrom = "2026-11-14", Reviewed = true, ReviewedSettingsApplicability = true,
                Groups = [new() { EmployeeId = 7, ReferenceAssignmentId = AssignmentId, Parts = [new() { RowId = row.Id, WorkLocationId = 3, TemplateId = "500", ComponentScopes = new() { ["1"] = "SourceFormulaPerPart", ["2"] = "SourceFormulaPerPart", ["3"] = "SourceFormulaPerPart" } }] }] },
            AttendanceReview = new() { Reviewed = true, PresentMeaning = "NotMapped", Mode = "SourceSeed", SourceMonth = batch.Month, PayableMeaning = "AlreadyPayable", CycleStart = "2026-10-01", CycleEnd = "2026-10-31" } };
        request.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request);
        var setup = new JsonObject { ["salaryComponents"] = new JsonArray(request.Components.Select((component,index) => (JsonNode)new JsonObject {
            ["id"] = (index+1).ToString(),["code"] = component.Code,["name"] = component.Name,["category"] = component.Category,["componentRole"] = component.ComponentRole,
            ["statutoryType"] = "None",["calculationType"] = component.CalculationType,["value"] = component.Value,["formula"] = "",["baseComponent"] = "",["proRata"] = false,
            ["taxable"] = true,["active"] = true,["priority"] = component.Priority.ToString(),["payType"] = "Fixed Pay" }).ToArray()),
            ["salaryStructures"] = new JsonArray(new JsonObject { ["id"] = "500",["clientId"] = "42",["name"] = "Existing immutable rules",["active"] = true,["annualCtc"] = "129600",
                ["lines"] = new JsonArray(request.Components.Select((component,index) => (JsonNode)new JsonObject { ["componentId"] = (index+1).ToString(),["calculationType"] = component.CalculationType,
                    ["value"] = component.Value,["formula"] = component.Formula,["baseComponent"] = "",["proRataOverride"] = "false",["roundingMode"] = "" }).ToArray()) }) };
        // Use the actual source translator, not a hand-written equivalent rewrite.
        var originalFormula = ExcelPayrollSetupRepository.TranslateTransferFormula("=ROUND(G3*H3%/D3*F3,0)", request, source, row);
        if (percent == 20) { request.Components[0].Formula = originalFormula; setup["salaryStructures"]![0]!["lines"]![0]!["formula"] = originalFormula; }
        if (addExactTemplate)
        {
            var copy = setup["salaryStructures"]![0]!.DeepClone(); copy["id"] = "501"; copy["lines"]![0]!["formula"] = originalFormula;
            setup["salaryStructures"]!.AsArray().Add(copy);
            request.TemplateScopeReviewVersion=1;request.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[3],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
            request.TemplateDecisions=[new(){VariantKey="501",ExistingTemplateId="501",ScopeAction="AttestLegacyScope",ScopeReason="Explicit synthetic period-only existing template review"}];
        }
        var rulesHash = ExcelPayrollSetupRepository.LocationSettingsRulesHash(setup["salaryStructures"]![0]!.AsObject(), setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray());
        var sourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        request.AttendanceReview.SourceHash=sourceHash;
        var assignment = new Assignment(AssignmentId,42,7,3,"Synthetic state","500",129600,"{}","{\"1\":\"SourceFormulaPerPart\",\"2\":\"SourceFormulaPerPart\",\"3\":\"SourceFormulaPerPart\"}",
            rulesHash,sourceHash,new(2026,11,14),ImportId,BatchId,row.Id,"Synthetic reviewer");
        var allocation = new Allocation(42,7,batch.Month,"synthetic-part",AssignmentId,31,30,30,AssignmentId,ImportId,BatchId,row.Id,sourceHash);
        var receipt = new ExcelPayrollSetupResult { ImportId = ImportId, EmployeeIds = [7], LocationAllocations = new(),
            LocationAssignmentReceipts = [new(row.Id,7,allocation.SourcePartKey,AssignmentId,3,"500",batch.Month,"2026-11-14",rulesHash,sourceHash)] };
        return new(batch,source,request,receipt,setup.ToJsonString(),new() { Id=7,ClientId=42,EmployeeCode="SYN-LOC-1",FirstName="Synthetic",LastName="Person" },
            [new() { Id=7,ClientId=42,EmployeeCode="SYN-LOC-1",Name="Synthetic Person",Pan="ABCDE1234F",IsActive=true,EligibleForMonth=true }],[assignment],[allocation]);
    }
    internal static ExcelPayrollSetupRepository.CorrectionParts Build(Fixture fixture) => ExcelPayrollSetupRepository.BuildLocationCorrectionParts(42,
        fixture.Batch,fixture.Source,fixture.Request,fixture.Receipt,fixture.Setup,fixture.Employee,fixture.Identities,fixture.Allocations,fixture.Assignments,"[]",nativeInput:Native());
    private static NativePayrollAttendanceInput Native()=>new(7,42,"2026-10",31,30,30,"2026-10-01","2026-10-31",new string('f',64),"");

    [Fact]
    public void Known_source_days_correct_only_complete_period_inputs_without_mutating_source_or_assignment()
    {
        var fixture=Create();var before=JsonSerializer.Serialize(fixture);
        var plan=Build(fixture);var part=Assert.Single(plan.Period.Parts);
        Assert.Equal(30,part.PresentDays);Assert.Equal(31,part.PayableDays);Assert.Equal(AssignmentId,part.AssignmentId);Assert.Empty(plan.Assignments);
        var actual=PayRunRepository.SimulateLocationParts(plan.Period,fixture.Setup,31,30,31);
        Assert.True(actual.Complete,string.Join(";",actual.Issues.Select(issue=>issue.Message)));Assert.Equal(10700,actual.Net);Assert.Equal(50,actual.Benefits);
        Assert.Equal(before,JsonSerializer.Serialize(fixture));
    }
    [Fact]
    public void Changed_source_rate_is_not_frozen_or_silently_copied_to_a_new_global_template()
    {
        var fixture=Create(25);var before=fixture.Setup;
        var error=Assert.Throws<InvalidOperationException>(()=>Build(fixture));
        Assert.Contains("No existing Settings template",error.Message);Assert.Equal(before,fixture.Setup);
    }
    [Fact]
    public void Existing_location_template_accepts_only_grouping_and_default_round_syntax_without_rewriting_evidence()
    {
        var fixture=Create();var setup=JsonNode.Parse(fixture.Setup)!;
        const string configured="ROUND(((54000)*(20)%)/(PAYROLL_DAYS)*(PAYABLE_DAYS))";
        setup["salaryStructures"]![0]!["lines"]![0]!["formula"]=configured;
        fixture=fixture with{Setup=setup.ToJsonString()};var before=JsonSerializer.Serialize(fixture);
        var plan=Build(fixture);Assert.Equal(10700,Assert.Single(plan.Rows).Net);Assert.Equal(30,plan.Period.Parts[0].PresentDays);Assert.Equal(31,plan.Period.Parts[0].PayableDays);
        Assert.Equal(before,JsonSerializer.Serialize(fixture));
        Assert.NotEqual(fixture.Assignments[0].RulesHash,Assert.Single(plan.Assignments).RulesHash);
        // Raw syntax remains a distinct audit fingerprint; only template matching
        // receives structural equivalence. The old immutable assignment survives.
        Assert.Equal(configured,JsonNode.Parse(fixture.Setup)!["salaryStructures"]![0]!["lines"]![0]!["formula"]!.ToString());
    }
    [Fact]
    public void Exact_existing_rules_produce_a_period_only_new_assignment_and_preserve_old_version()
    {
        var fixture=Create(25,true);var before=JsonSerializer.Serialize(fixture.Assignments);
        var plan=Build(fixture);var assignment=Assert.Single(plan.Assignments);
        Assert.Equal("501",assignment.SalaryStructureId);Assert.Equal(ReviewedLocationCorrectionOrigins.Status,assignment.Status);
        Assert.Equal("{}",assignment.SalaryInputsJson);Assert.NotEqual(AssignmentId,assignment.Id);Assert.Equal(before,JsonSerializer.Serialize(fixture.Assignments));
        var result=PayRunRepository.SimulateLocationParts(plan.Period,fixture.Setup,31,30,31);Assert.True(result.Complete);Assert.Equal(13400,result.Net);
    }
    [Theory]
    [InlineData("identity")][InlineData("receipt")][InlineData("days")][InlineData("sourceOutput")][InlineData("unknownFinancial")]
    public void Missing_or_conflicting_source_proof_blocks_every_change(string mode)
    {
        var fixture=Create();
        switch(mode)
        {
            case "identity":fixture.Identities[0].Pan="ZZZZZ1234Z";break;
            case "receipt":fixture.Receipt.LocationAssignmentReceipts.Clear();break;
            case "days":fixture.Request.AttendanceReview!.PresentMeaning="NotSupplied";break;
            case "sourceOutput":fixture.Source.Rows[0].Cells[8]!.Value="999";break;
            case "unknownFinancial":fixture.Source.Columns[6].Kind="earning";fixture.Request.SourceColumnRoles[6]="earning";break;
        }
        Assert.ThrowsAny<InvalidOperationException>(()=>Build(fixture));
    }
    [Theory]
    [InlineData("earning")][InlineData("deduction")][InlineData("employer")][InlineData("net")][InlineData("attendance")][InlineData("extra")]
    public void Full_native_proof_rejects_changed_line_total_days_or_extra_components(string mode)
    {
        var fixture=Create();var plan=Build(fixture);var source=Assert.Single(plan.Rows);
        var lines=new JsonArray(source.Lines.Select(line=>(JsonNode)new JsonObject { ["Id"]=line.Code,["Category"]=line.Category,["amount"]=line.CalculatedAmount }).ToArray());
        var native=new PayRunEmployee{GrossPay=source.Gross!.Value,StatutoryDeductions=source.Deductions!.Value,NetPay=source.Net!.Value,
            PresentDays=source.PresentDays,PayableDays=source.PayableDays,DetailsJson=lines.ToJsonString()};
        ExcelPayrollSetupRepository.RequireLocationCorrectionParity(native,plan.Rows);
        switch(mode)
        {
            case "earning":lines[0]!["amount"]=1;break;
            case "deduction":lines[1]!["amount"]=0;break;
            case "employer":lines[2]!["amount"]=0;break;
            case "net":native.NetPay=1;break;
            case "attendance":native.PresentDays=31;break;
            case "extra":lines.Add(new JsonObject{["Id"]="EXTRA",["Category"]="Deduction",["amount"]=0});break;
        }
        native.DetailsJson=lines.ToJsonString();Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.RequireLocationCorrectionParity(native,plan.Rows));
    }
    [Theory]
    [InlineData("month")][InlineData("row")][InlineData("source")][InlineData("inactive")][InlineData("approval")]
    public void Period_only_assignment_cannot_escape_its_explicit_period_source_reference(string invalid)
    {
        var assignment=new PayRunRepository.LocationPartAssignmentStoreRow{AssignmentId=AssignmentId,ClientId=42,EmployeeId=7,WorkLocationId=3,WorkState="Synthetic state",
            SalaryStructureId="500",AnnualCtc=129600,SalaryInputsJson="{}",ComponentScopesJson="{\"1\":\"SourceFormulaPerPart\"}",RulesHash=new('d',64),SourceHash=new('e',64),
            ImportId=ImportId,BatchId=BatchId,RowId="source-one",Status=ReviewedLocationCorrectionOrigins.Status,CorrectionPayPeriod="2026-10",CorrectionEvidenceVerified=true,ConfigurationEffectiveFrom="2026-11-14"};
        var allocation=new PayRunRepository.LocationPartStoreRow{ClientId=42,EmployeeId=7,PayPeriod="2026-10",PartKey="synthetic-part",AssignmentId=AssignmentId,
            PayrollDays=31,PresentDays=31,PayableDays=31,ReferenceAssignmentId=AssignmentId,AllocationImportId=ImportId,AllocationBatchId=BatchId,AllocationRowId="source-one",AllocationSourceHash=new('e',64)};
        Assert.Single(PayRunRepository.ReadLocationPartPeriods([allocation],[assignment],new Dictionary<int,int>{{7,42}},"2026-10"));
        switch(invalid){case "month":assignment.CorrectionPayPeriod="2026-11";break;case "row":assignment.RowId="other";break;case "source":assignment.SourceHash=new('f',64);break;case "inactive":assignment.Status="Inactive";break;case "approval":assignment.CorrectionEvidenceVerified=false;break;}
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ReadLocationPartPeriods([allocation],[assignment],new Dictionary<int,int>{{7,42}},"2026-10"));
    }
    [Fact]
    public void Ordinary_active_discovery_and_period_correction_no_default_behavior_are_retained()
    {
        var corrected=new PayRunRepository.LocationPartAssignmentStoreRow { AssignmentId=AssignmentId,ClientId=42,EmployeeId=7,Status=ReviewedLocationCorrectionOrigins.Status };
        Assert.Empty(PayRunRepository.ReadLocationPartPeriods([],[corrected],new Dictionary<int,int>{{7,42}},"2026-11"));
        corrected.Status="Active";corrected.ConfigurationEffectiveFrom="2026-10-01";
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ReadLocationPartPeriods([],[corrected],new Dictionary<int,int>{{7,42}},"2026-11"));
    }
    [Theory]
    [InlineData("scope")][InlineData("password")][InlineData("role")][InlineData("permission")]
    public void Only_global_active_full_payroll_administrator_can_request_correction(string missing)
    {
        var actor=new AuthUser{Id=1,IsActive=true,Roles=["super_admin"],Permissions=["settings.manage","employees.manage","payroll.run"]};
        Assert.True(ExcelPayrollSetupRepository.CanManageLocationCorrection(actor,42));
        switch(missing){case "scope":actor.ClientId=42;break;case "password":actor.MustChangePassword=true;break;case "role":actor.Roles.Clear();break;case "permission":actor.Permissions.Remove("payroll.run");break;}
        Assert.False(ExcelPayrollSetupRepository.CanManageLocationCorrection(actor,42));
    }
    private static void PrepareRecovery(Fixture fixture)
    {
        var row=fixture.Batch.Rows[0];
        fixture.Batch.ProfileLinks=[new(row.Id,row.SourceRow,fixture.Employee.Id,ExcelPayslipMasterSynchronization.ProfileIdentityHash(fixture.Batch,row,fixture.Source))];
        fixture.Request.EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=fixture.Employee.Id,AllowCreate=false}];
        fixture.Request.TemplateScopeReviewVersion=1;fixture.Request.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[3],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
    }
    [Fact]
    public void Explicit_orphan_recovery_uses_fresh_mapping_and_durable_link_without_fabricated_parent()
    {
        var fixture=Create();PrepareRecovery(fixture);
        var plan=ExcelPayrollSetupRepository.BuildLocationCorrectionParts(42,fixture.Batch,fixture.Source,fixture.Request,null,fixture.Setup,
            fixture.Employee,fixture.Identities,fixture.Allocations,fixture.Assignments,"[]",orphanRecovery:true,nativeInput:Native());
        Assert.Equal(30,Assert.Single(plan.Period.Parts).PresentDays);Assert.Empty(plan.Assignments);
    }
    [Theory]
    [InlineData("missing")][InlineData("employee")][InlineData("witness")][InlineData("decision")]
    public void Orphan_recovery_never_promotes_code_name_or_stale_profile_link_to_identity(string invalid)
    {
        var fixture=Create();PrepareRecovery(fixture);
        switch(invalid)
        {
            case "missing":fixture.Batch.ProfileLinks=null;break;
            case "employee":fixture.Batch.ProfileLinks![0]=fixture.Batch.ProfileLinks[0] with{EmployeeId=8};break;
            case "witness":fixture.Batch.ProfileLinks![0]=fixture.Batch.ProfileLinks[0] with{SourceIdentityHash=new string('f',64)};break;
            case "decision":fixture.Request.EmployeeDecisions[0].MatchedEmployeeId=8;break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.BuildLocationCorrectionParts(42,fixture.Batch,fixture.Source,fixture.Request,null,fixture.Setup,
            fixture.Employee,fixture.Identities,fixture.Allocations,fixture.Assignments,"[]",orphanRecovery:true,nativeInput:Native()));
    }
    [Theory]
    [InlineData("cache")][InlineData("month")][InlineData("cycle")]
    public void Stale_or_mismatched_source_day_boundary_cannot_certify_a_correction(string invalid)
    {
        var fixture=Create();
        switch(invalid)
        {
            case "cache":fixture.Source.Rows[0].Cells[5]!.FormulaText="=30";fixture.Request.AttendanceReview!.SourceHash=ExcelPayslipRepository.EmploymentSourceHash(fixture.Batch,fixture.Source);break;
            case "month":fixture.Request.AttendanceReview!.SourceMonth="2026-09";break;
            case "cycle":fixture.Request.AttendanceReview!.CycleStart="2026-09-30";break;
        }
        Assert.Throws<InvalidOperationException>(()=>Build(fixture));
    }
    [Fact]
    public void Persisted_decimal_scales_do_not_create_a_false_period_mismatch()
    {
        var period=Build(Create()).Period;
        var copy=JsonSerializer.Deserialize<PayrollLocationPartPeriod>(JsonSerializer.Serialize(period))!;
        copy.Parts[0].PresentDays=30.0000m;copy.Parts[0].PayableDays=31.0000m;copy.Parts[0].AnnualCtc=129600.0000m;
        Assert.True(PayRunRepository.SameLocationCorrectionPeriod(period,copy));copy.Parts[0].PayableDays=30.99m;
        Assert.False(PayRunRepository.SameLocationCorrectionPeriod(period,copy));
    }
    [Theory]
    [InlineData("valid")][InlineData("equalCacheWrongDependency")][InlineData("duplicate")][InlineData("crossRow")][InlineData("category")]
    public void Aggregate_is_ignored_only_when_exact_distinct_original_component_dependencies_are_proved(string mode)
    {
        var source=new ExcelPayslipCalculationSource{SheetName="Synthetic aggregate",HeaderRow=2,ColumnCount=5,
            Columns=Enumerable.Range(0,5).Select(index=>new ExcelPayslipCalculationColumn{ColumnIndex=index,Label="Original earning "+index,SourceHeader="Original earning "+index,Kind=index==4?"earning":"ignore"}).ToList(),
            Rows=[new(){SourceRow=3,Cells=[new(){Value="1"},new(){Value="2"},new(){Value="3"},new(){Value="4"},new(){Value="10",FormulaText="=(A3)+(B3)+(C3)+(D3)"}]}]};
        var request=new ExcelPayrollSetupRequest{SourceColumnRoles=new(){[0]="earning",[1]="earning",[2]="earning",[3]="earning",[4]="ignore"}};
        var components=Enumerable.Range(0,4).Select(index=>new ExcelPayrollSetupRepository.ResolvedComponent(new(){Code="E"+index,ExpectedColumnIndex=index,Category="Earning"},index.ToString(),false)).ToArray();
        switch(mode)
        {
            case "equalCacheWrongDependency":source.Rows[0].Cells[4]!.FormulaText="=A3*10";break;
            case "duplicate":source.Rows[0].Cells[4]!.FormulaText="=A3+A3+C3+D3";break;
            case "crossRow":source.Rows[0].Cells[4]!.FormulaText="=A4+B3+C3+D3";break;
            case "category":components[0].Definition.Category="Benefit";break;
        }
        if(mode=="valid")
        {
            var proof=Assert.Single(ExcelPayrollSetupRepository.ReviewCorrectionAggregates(source,new(){Id="row",SourceRow=3},request,components));
            Assert.Equal(new[]{0,1,2,3},proof.OriginalOutputColumns);Assert.Equal(10,proof.Amount);Assert.Equal(64,proof.ProofHash.Length);
        }
        else Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReviewCorrectionAggregates(source,new(){Id="row",SourceRow=3},request,components));
    }
}
