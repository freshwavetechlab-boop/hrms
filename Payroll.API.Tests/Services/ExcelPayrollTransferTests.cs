using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class ExcelPayrollTransferTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRequest Request) Fixture()
    {
        var request = new ExcelPayrollSetupRequest { TransferReviewVersion=1, ReviewedPolicyScope=true, ReviewedFixedInputs=true, BatchId=new string('a',32), RowIds=["one"], TemplateName="Synthetic variant", EffectiveFrom="2026-09-01", PayrollDaysColumnIndex=0, PayableDaysColumnIndex=1,
            TemplateDecisions=[new() { VariantKey="1000",CreateVariant=true }], Components=[
                new() { Key="basic", Code="BASIC",SourceColumnIndex=2,ExpectedColumnIndex=3,ProRata=true },
                new() { Key="pf",Code="PF",Category="Deduction",CalculationType="Formula",Formula="BASIC_EARNED*12%",ExpectedColumnIndex=4,ProRata=false },
                new() { Key="er",Code="PF_ER",Category="Benefit",CalculationType="Formula",Formula="BASIC_EARNED*12%",ExpectedColumnIndex=5,ProRata=false }
            ] };
        var row = new ExcelPayslipRow { Id="one",SourceRow=3,EmployeeCode="SYN-1",EmployeeName="Synthetic Person",NetPay=88,
            Earnings=[new() { Label="Basic",Amount=100 }],Deductions=[new() { Label="PF",Amount=12 }],EmployerContributions=[new() { Label="Employer PF",Amount=12 }] };
        var source = new ExcelPayslipCalculationSource { SheetName="Salary",ColumnCount=6,Columns=Enumerable.Range(0,6).Select(i=>new ExcelPayslipCalculationColumn { ColumnIndex=i,Kind=i==3?"earning":i==4?"deduction":i==5?"employer":"info" }).ToList(),Rows=[new() { SourceRow=3,Cells=[
            new() { Value="30" },new() { Value="30" },new() { Value="100" },new() { Value="100",FormulaText="C3/A3*B3" },new() { Value="12",FormulaText="D3*12%" },new() { Value="12",FormulaText="D3*12%" }
        ] }] };
        var components=request.Components.Select((d,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(d,(i+1).ToString(),false)).ToList();
        var plan = new ExcelPayrollSetupRepository.Plan { Batch=new() { Id=request.BatchId,ClientId=42,Month="2026-09",Rows=[row] },Source=source };
        plan.Components.AddRange(components);
        var preview = new ExcelPayrollSetupPreviewRow { RowId="one",EmployeeId=10,PayrollDays=30,PresentDays=30,PayableDays=30,Gross=100,Deductions=12,Benefits=12,Net=88,
            Lines=[new("BASIC",100,100,0),new("PF",12,12,0),new("PF_ER",12,12,0)] };
        plan.Rows.Add(new() { RowId="one",SourceRow=3,Employee=new() { Id=10 },TemplateId="1000",Components=components,Preview=preview }); plan.Preview.Rows.Add(preview);
        return (plan,request);
    }
    private static JsonObject Template(string id="500",int client=42) => JsonNode.Parse($$"""
        {"id":"{{id}}","clientId":"{{client}}","name":"Existing synthetic","active":true,"lines":[
        {"componentId":"1","value":"0","calculationType":"Fixed Amount","formula":"","baseComponent":"","proRataOverride":"true","roundingMode":""},
        {"componentId":"2","value":"0","calculationType":"Formula","formula":"BASIC_EARNED*12%","baseComponent":"","proRataOverride":"false","roundingMode":""},
        {"componentId":"3","value":"0","calculationType":"Formula","formula":"BASIC_EARNED*12%","baseComponent":"","proRataOverride":"false","roundingMode":""}]}
        """)!.AsObject();

    [Fact] public void SupportedSourceProofPreparesConfigurationButNeverClaimsEmployeeParity()
    {
        var (p,r)=Fixture(); ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]);
        Assert.True(p.Preview.CanApply); Assert.All(p.Preview.Transfer!.Formulas,f=>Assert.Equal("Exact supported translation",f.Status));
        Assert.Contains("not verified",p.Preview.Transfer.PreparationStatus); Assert.StartsWith("Not verified",p.Preview.Transfer.ParityStatus);
        Assert.All(p.Preview.Transfer.Components,c=>Assert.Equal("Reuse unchanged",c.Action));
    }
    [Theory] [InlineData("VLOOKUP(C3,Prior!A1:B4,2,0)")] [InlineData("IF(A3>0,D3*12%,0)")] [InlineData("ROUNDUP(-D3,0)")] [InlineData("D999*12%")]
    public void UnsupportedOrUnboundFormulaCannotBeFrozenDespiteCachedAmountMatch(string formula)
    {
        var (p,r)=Fixture(); p.Source.Rows[0].Cells[4]!.FormulaText=formula;
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); Assert.False(p.Preview.CanApply); Assert.Contains(p.Preview.Transfer!.Formulas,f=>f.Code=="PF" && f.Status=="Blocked");
    }
    [Fact] public void FormulaMismatchEmployerMismatchAndUnmappedOutputEachBlockApply()
    {
        var (p,r)=Fixture(); r.Components[1].Formula="BASIC_EARNED*13%";
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); Assert.False(p.Preview.CanApply);
        (p,r)=Fixture(); p.Rows[0].Preview.Benefits=13; ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); Assert.False(p.Preview.CanApply);
        (p,r)=Fixture(); r.Components[2].ExpectedColumnIndex=null; ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); Assert.False(p.Preview.CanApply);
    }
    [Fact] public void ExistingExactTemplateRequiresExplicitReuseAndIsNotInsertedAgain()
    {
        var (p,r)=Fixture(); ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]); Assert.False(p.Preview.CanApply);
        (p,r)=Fixture(); r.TemplateDecisions=[new() { VariantKey="1000",ExistingTemplateId="500" }];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template(),Template("501")]);
        Assert.True(p.Preview.CanApply); Assert.Equal("500",p.Rows[0].Employee.SalaryStructureId); Assert.Contains("500",p.ReusedTemplateIds);
        Assert.Equal(2,p.Preview.Transfer!.Templates[0].Candidates.Count);
    }
    [Fact] public void CrossClientChangedOrInactiveTemplatesCannotBeReused()
    {
        foreach(var kind in new[] { "foreign","changed","inactive" }) {
            var (p,r)=Fixture(); r.TemplateDecisions=[new() { VariantKey="1000",ExistingTemplateId="500" }];
            var candidate=Template(client:kind=="foreign"?43:42);
            if(kind=="changed") candidate["lines"]![1]!["formula"]="BASIC_EARNED*13%";
            if(kind=="inactive") candidate["active"]=false;
            ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[candidate]); Assert.False(p.Preview.CanApply); Assert.Empty(p.ReusedTemplateIds);
        }
    }
    [Fact] public void FixedOutputAndPolicyAssumptionsRequireSeparateReview()
    {
        var (p,r)=Fixture(); p.Source.Rows[0].Cells[4]!.FormulaText=null; r.ReviewedFixedInputs=false; r.ReviewedPolicyScope=false;
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); Assert.False(p.Preview.CanApply);
        Assert.Contains(p.Preview.Issues,i=>i.Message.Contains("policy assumptions"));
    }
    [Fact] public void ChangedSourceSnapshotAndChangedPlanCannotReuseAStaleReview()
    {
        var (p,r)=Fixture(); ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); var first=p.Preview.Transfer!.SourceHash;
        var (changed,request)=Fixture(); changed.Source.Rows[0].Cells[2]!.Value="101";
        ExcelPayrollSetupRepository.FinalizeTransferReview(changed,request,42,[]); Assert.NotEqual(first,changed.Preview.Transfer!.SourceHash);
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.RequireMatchingSetupPlanHash(first,changed.Preview.Transfer.SourceHash));
        ExcelPayrollSetupRepository.RequireMatchingSetupPlanHash(first,first.ToUpperInvariant());
    }
    [Fact] public void DurableReceiptRoundtripRetainsProvenanceAndReplayRequestIdentity()
    {
        var (p,r)=Fixture(); ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]);
        var result=new ExcelPayrollSetupResult { ImportId="receipt",Transfer=p.Preview.Transfer,TransferRows=[new("one",10,30,30,30,88,p.Rows[0].Preview.Lines)] };
        var saved=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal("one",saved.TransferRows[0].RowId); Assert.Equal(10,saved.TransferRows[0].EmployeeId); Assert.Equal(3,saved.TransferRows[0].Lines.Count);
        Assert.StartsWith("Not verified",saved.Transfer!.ParityStatus); Assert.True(ExcelPayrollSetupRepository.SameReviewedRequest(JsonSerializer.Serialize(r,new JsonSerializerOptions(JsonSerializerDefaults.Web)),r));
        r.TemplateDecisions[0].CreateVariant=false; Assert.False(ExcelPayrollSetupRepository.SameReviewedRequest(JsonSerializer.Serialize(Fixture().Request,new JsonSerializerOptions(JsonSerializerDefaults.Web)),r));
    }
    [Fact] public void TransferRequiresExistingSetupPermissionsAndClientScope()
    {
        var user=new AuthUser { Id=1,IsActive=true,ClientId=42,Permissions=["settings.manage","employees.manage","payroll.run"] };
        Assert.True(ExcelPayrollSetupEndpoints.CanAccess(user,42)); Assert.False(ExcelPayrollSetupEndpoints.CanAccess(user,43));
        user.Permissions=["employees.manage","payroll.run"]; Assert.False(ExcelPayrollSetupEndpoints.CanAccess(user,42));
    }
    [Fact] public void SavedDraftRetainsProvenanceWithoutLosingInactiveDefaultsOrAuthorizingApply()
    {
        var (p,r)=Fixture(); ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[]); p.Preview.PlanHash=new string('b',64);
        var metadata=JsonNode.Parse(ExcelPayrollSetupRepository.TransferDraftMetadata("{\"editableDefaultsRevision\":3}",p.Preview))!;
        Assert.Equal(3,metadata["editableDefaultsRevision"]!.GetValue<int>());
        Assert.Equal(p.Preview.Transfer!.SourceHash,metadata["transfer"]!["sourceHash"]!.GetValue<string>());
        Assert.Equal(p.Preview.PlanHash,metadata["previewPlanHash"]!.GetValue<string>());
        Assert.StartsWith("Not verified",metadata["transfer"]!["parityStatus"]!.GetValue<string>());
    }
    [Fact] public void KeepExistingRecordsMismatchWithoutChangingSharedTemplateOrApplyingSourceRows()
    {
        var (p,r)=Fixture(); p.Rows[0].ExistingTemplateId="500"; r.TemplateDecisions=[new() { VariantKey="1000",KeepExisting=true }];r.ReviewedPolicyScope=false;
        var template=Template(); template["lines"]![1]!["formula"]="BASIC_EARNED*13%";var before=template.ToJsonString();
        p.Rows[0].Preview.Lines[0]=new("BASIC",100,101,1);p.Rows[0].Preview.Gross=101;
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[template]);
        Assert.True(p.Preview.CanApply); Assert.True(p.Rows[0].KeepExisting); Assert.Contains("unresolved",p.Preview.Transfer!.Templates[0].Action);
        Assert.False(p.Preview.Transfer.Templates[0].Candidates[0].Exact);Assert.Equal(before,template.ToJsonString());
        Assert.Single(p.Preview.Transfer.Templates[0].KeptAssignments);Assert.Contains(p.Preview.Issues,i=>i.Severity=="Review" && i.Message.Contains("comparison is unresolved"));
        var result=new ExcelPayrollSetupResult();ExcelPayrollSetupRepository.SeparateKeptTransferRows(p,result);
        Assert.Empty(p.Rows);Assert.Empty(result.EmployeeIds);Assert.Single(result.KeptRows);Assert.Equal("500",result.KeptRows[0].TemplateId);
    }
    [Fact] public void KeepRequiresVerifiedCurrentAssignmentsAndNeverOverridesUnresolvedIdentity()
    {
        var (p,r)=Fixture();r.TemplateDecisions=[new() {VariantKey="1000",KeepExisting=true}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);Assert.False(p.Preview.CanApply);
        (p,r)=Fixture();p.Rows[0].ExistingTemplateId="500";r.TemplateDecisions=[new() {VariantKey="1000",KeepExisting=true}];
        p.Preview.Issues.Add(new("Error","Identity is ambiguous","one"));
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);Assert.False(p.Preview.CanApply);
    }
    [Fact] public void MixedReceiptDoesNotMarkKeptRowsAsTransferredAndLegacyReceiptsStillWork()
    {
        var result=new ExcelPayrollSetupResult { KeptRows=[new("kept",10,"500","Keep decision")] };
        var rows=ExcelPayrollSetupRepository.AppliedReceiptRows("[\"applied\",\"kept\"]",JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] {"applied"},rows);Assert.Equal(2,ExcelPayrollSetupRepository.AppliedReceiptRows("[\"one\",\"two\"]","{}").Count);
    }
    [Fact] public void ChoiceValidationAndSideBySideDifferencesAreExplicit()
    {
        var (p,r)=Fixture();r.TemplateDecisions=[new() { VariantKey="1000",KeepExisting=true,CreateVariant=true }];
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateTransferRequest(r));
        var template=Template();template["lines"]![1]!["formula"]="BASIC_EARNED*13%";
        var details=ExcelPayrollSetupRepository.TransferTemplateDetails(template,p.Components);
        Assert.Contains(details,d=>d.Component=="PF" && d.Field=="formula" && d.Existing=="BASIC_EARNED*13%" && d.ExcelDerived=="BASIC_EARNED*12%");
    }
    [Fact] public void InitialPreviewOffersCurrentComparisonBeforeAnyDecisionAndOldDecisionCanBeRevised()
    {
        var (p,r)=Fixture();p.Rows[0].ExistingTemplateId="500";r.TemplateDecisions=[];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);Assert.Single(p.Preview.Transfer!.Templates[0].ExistingResults);Assert.False(p.Preview.CanApply);
        var saved=JsonSerializer.Serialize(r,new JsonSerializerOptions(JsonSerializerDefaults.Web));r.TemplateDecisions=[new() { VariantKey="1000",KeepExisting=true }];
        Assert.False(ExcelPayrollSetupRepository.SameReviewedRequest(saved,r));
    }
    [Fact] public void ReferenceOnlyCanRecordVerifiedKeepWithoutClaimingSourceTransfer() {
        var(p,r)=Fixture();p.Source.CaptureVersion=1;p.Source.DependencyReviewStatus="ReferenceOnly";p.Rows[0].ExistingTemplateId="500";
        r.TemplateDecisions=[new(){VariantKey="1000",KeepExisting=true}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);
        Assert.True(p.Preview.CanApply);Assert.True(p.Rows[0].KeepExisting);Assert.Empty(p.Preview.Transfer!.AttendanceProofs);
        Assert.Contains("no source transfer",p.Preview.Transfer.PreparationStatus);
    }    [Fact] public void FormulaBackedMonthlyInputCannotBeFrozenEvenWhenOutputAmountsMatch() {
        var(p,r)=Fixture();p.Source.Rows[0].Cells[2]!.FormulaText="VLOOKUP(A3,'[1]SalaryGrid'!A1:D9,4,0)";
        var proof=ExcelPayrollSetupRepository.InspectTransferFormula(r,p.Source,p.Batch.Rows[0],r.Components[0]);
        Assert.Equal("Blocked",proof.Status);Assert.Contains("recurring constant",proof.Reason);
    }
    private static void AddSyntheticRateRow(ExcelPayrollSetupRepository.Plan p, ExcelPayrollSetupRequest r, bool capped = false)
    {
        var row=JsonSerializer.Deserialize<ExcelPayslipRow>(JsonSerializer.Serialize(p.Batch.Rows[0]))!;
        row.Id="two";row.SourceRow=4;row.EmployeeCode="SYN-2";row.EmployeeName="Other Synthetic Person";
        row.Earnings[0].Amount=200;row.Deductions[0].Amount=capped?18:24;row.EmployerContributions[0].Amount=24;row.NetPay=capped?182:176;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSourceRow>(JsonSerializer.Serialize(p.Source.Rows[0]))!;
        source.SourceRow=4;source.Cells[2]!.Value="200";source.Cells[3]!.Value="200";source.Cells[3]!.FormulaText="C4/A4*B4";
        source.Cells[4]!.Value=capped?"18":"24";source.Cells[4]!.FormulaText=capped?"MIN(D4,150)*12%":"D4*12%";
        source.Cells[5]!.Value="24";source.Cells[5]!.FormulaText="D4*12%";
        if(capped) r.Components[1].RowFormulas["two"]="MIN(BASIC_EARNED,150)*12%";
        r.RowIds.Add(row.Id);p.Batch.Rows.Add(row);p.Source.Rows.Add(source);
        var arithmetic=ExcelPayrollSetupRepository.CalculateRow(42,r,p.Components,source,row);
        p.Rows.Add(new() {RowId=row.Id,SourceRow=4,Employee=new() {Id=11},TemplateId=capped?"1001":"1000",
            Components=ExcelPayrollSetupRepository.ComponentsForRow(p.Components,row.Id),Preview=arithmetic.Preview,SalaryInputs=arithmetic.Inputs});
        p.Preview.Rows.Add(arithmetic.Preview);
    }
    [Fact] public void MultiTemplateSameRulesPreserveDifferentEmployeeInputsAndReuseOneReviewedCandidate()
    {
        var(p,r)=Fixture();AddSyntheticRateRow(p,r);
        var first=ExcelPayrollSetupRepository.CalculateRow(42,r,p.Components,p.Source.Rows[0],p.Batch.Rows[0]);
        Assert.Equal(100,first.Inputs["1"]);Assert.Equal(200,p.Rows[1].SalaryInputs["1"]);
        Assert.Equal(88,first.Preview.Net);Assert.Equal(176,p.Rows[1].Preview.Net);
        Assert.Equal(p.Components.Select(c=>c.Definition.Formula),p.Rows[1].Components.Select(c=>c.Definition.Formula));
        r.TemplateDecisions=[new(){VariantKey="1000",ExistingTemplateId="500"}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);
        Assert.True(p.Preview.CanApply);Assert.Single(p.Preview.Transfer!.Templates);Assert.Single(p.ReusedTemplateIds);
        Assert.All(p.Rows,row=>Assert.Equal("500",row.TemplateId));Assert.Equal(200,p.Rows[1].SalaryInputs["1"]);
    }
    [Fact] public void MultiTemplateDistinctCapturedCapRequiresSeparateReviewedRuleVariant()
    {
        var(p,r)=Fixture();AddSyntheticRateRow(p,r,true);
        r.TemplateDecisions=[new(){VariantKey="1000",ExistingTemplateId="500"},new(){VariantKey="1001",CreateVariant=true}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template()]);
        Assert.True(p.Preview.CanApply);Assert.Equal(2,p.Preview.Transfer!.Templates.Count);
        Assert.Equal("Reuse unchanged",p.Preview.Transfer.Templates[0].Action);
        Assert.Equal("Create reviewed variant",p.Preview.Transfer.Templates[1].Action);
        Assert.False(p.Preview.Transfer.Templates[1].Candidates.Single().Exact);
        Assert.Equal(18,p.Rows[1].Preview.Lines.Single(l=>l.Code=="PF").CalculatedAmount);
    }
    [Fact] public void MultiTemplateRepeatedAndRevisedBatchReviewReusesExistingTemplateWithoutMutatingCatalogue()
    {
        var catalogue=Template();var original=catalogue.ToJsonString();
        foreach(var batchId in new[]{new string('a',32),new string('b',32)}) {
            var(p,r)=Fixture();r.BatchId=batchId;p.Batch.Id=batchId;r.TemplateDecisions=[new(){VariantKey="1000",ExistingTemplateId="500"}];
            ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[catalogue]);
            Assert.True(p.Preview.CanApply);Assert.Equal("500",p.Rows.Single().TemplateId);Assert.Single(p.ReusedTemplateIds);
        }
        Assert.Equal(original,catalogue.ToJsonString());
        // DB receipt replay/assignment uniqueness is not exercised by this pure review test.
    }
    [Fact] public void MultiTemplateForeignCatalogueCannotBecomeReuseCandidateAndRemainsUnmodified()
    {
        var(p,r)=Fixture();var foreign=Template("700",43);var untouched=foreign.ToJsonString();
        r.TemplateDecisions=[new(){VariantKey="1000",ExistingTemplateId="700"}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[Template(),foreign]);
        Assert.False(p.Preview.CanApply);Assert.DoesNotContain(p.Preview.Transfer!.Templates.Single().Candidates,c=>c.Id=="700");
        Assert.Equal(untouched,foreign.ToJsonString());
    }
    [Theory][InlineData("workLocationId","2")][InlineData("policyVersion","future-policy")][InlineData("effectiveFrom","2030-01-01")][InlineData("payrollDivisor","26")]
    public void ComponentOnlyComparisonDoesNotPretendToCertifyApplicability(string key,string value)
    {
        var(p,r)=Fixture();var candidate=Template();candidate[key]=value;
        Assert.Empty(ExcelPayrollSetupRepository.TransferTemplateDifferences(candidate,p.Components));
        // Current comparison is line-level only; this test records a gap, not safe applicability certification.
    }
    [Fact] public void MultiTemplateEquivalentWhitespaceReusesWhileLineOrderRemainsConservative()
    {
        var(p,r)=Fixture();var spaced=Template();spaced["lines"]![1]!["formula"]=" BASIC_EARNED * 12% ";
        Assert.Empty(ExcelPayrollSetupRepository.TransferTemplateDifferences(spaced,p.Components));
        var reordered=Template();var lines=reordered["lines"]!.AsArray();var first=lines[0]!.DeepClone();lines[0]=lines[1]!.DeepClone();lines[1]=first;
        Assert.NotEmpty(ExcelPayrollSetupRepository.TransferTemplateDifferences(reordered,p.Components));
    }
    [Fact] public void MultiTemplateChangedComponentIdentityAndRoundingCannotReuseAsExact()
    {
        var(p,r)=Fixture();var candidate=Template();candidate["lines"]![2]!["componentId"]="different-employer-component";
        Assert.NotEmpty(ExcelPayrollSetupRepository.TransferTemplateDifferences(candidate,p.Components));
        candidate=Template();candidate["lines"]![1]!["roundingMode"]="Nearest rupee";
        Assert.Contains(ExcelPayrollSetupRepository.TransferTemplateDifferences(candidate,p.Components),d=>d.Contains("roundingMode"));
    }


    private static (ExcelPayrollSetupRepository.Plan,ExcelPayrollSetupRequest) SharedHeaderFixture() {
        var(p,r)=Fixture();p.Source.HeaderRow=2;r.PayrollDaysColumnIndex=-1;r.FixedPayrollDays=30;r.PayrollDaysCell="$B$2";
        p.Source.Rows.Insert(0,new(){SourceRow=2,Cells=[new(){Value=""},new(){Value="30"}]});p.Source.Rows[1].Cells[3]!.FormulaText="C3/$B$2*B3";return(p,r);
    }
    [Fact] public void SharedHeaderDivisorBindsSourceInputWithoutEditingLegacySourceMetadata() {
        var(p,r)=SharedHeaderFixture();var before=JsonSerializer.Serialize(p.Source);ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(r,p.Source,p.Batch.Rows);
        Assert.Null(p.Source.MonthDaysCell);Assert.Equal(before,JsonSerializer.Serialize(p.Source));
        Assert.Equal("BASIC_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS",ExcelPayrollSetupRepository.TranslateTransferFormula("C3/$B$2*B3",r,p.Source,p.Batch.Rows[0]));
    }
    [Theory][InlineData("29",null)][InlineData("30","15*2")][InlineData("",null)]
    public void SharedHeaderDivisorRefusesChangedMissingOrCalculatedCache(string value,string? formula) {
        var(p,r)=SharedHeaderFixture();p.Source.Rows[0].Cells[1]!.Value=value;p.Source.Rows[0].Cells[1]!.FormulaText=formula;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(r,p.Source,p.Batch.Rows));
    }
    [Fact] public void SharedHeaderDivisorMustBeHeaderLiteralAndReferencedBySelectedEarning() {
        var(p,r)=SharedHeaderFixture();p.Source.Rows[1].Cells[3]!.FormulaText="C3/30*B3";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(r,p.Source,p.Batch.Rows));
        (p,r)=SharedHeaderFixture();r.PayrollDaysCell="B3";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(r,p.Source,p.Batch.Rows));
    }
    [Fact] public void SharedHeaderDivisorChangesAttendanceAcknowledgementBinding() {
        var(p,r)=SharedHeaderFixture();var binding=ExcelPayrollSetupRepository.AttendanceBinding(r);r.PayrollDaysCell="C2";
        Assert.NotEqual(binding,ExcelPayrollSetupRepository.AttendanceBinding(r));r.PayrollDaysCell="B2";Assert.Equal(binding,ExcelPayrollSetupRepository.AttendanceBinding(r));
    }

    [Theory][InlineData(" = basic_earned * 12.0% ","BASIC_EARNED*12%")][InlineData("IF ( A >= 1e1, ROUND(B, 0.00), 0)","IF(A>=10,ROUND(B,0),0)")]
    public void CanonicalNativeFormulaPreservesCalculatedResultsAndRuleSignature(string first,string second) {
        Assert.Equal(PayrollFormulaEvaluator.Canonicalize(first),PayrollFormulaEvaluator.Canonicalize(second));
        Assert.Equal(PayrollFormulaEvaluator.Evaluate(first,_=>100),PayrollFormulaEvaluator.Evaluate(second,_=>100));
        var(p,r)=Fixture();var original=ExcelPayrollSetupRepository.TransferRulesSignature(p.Components);r.Components[1].Formula=" = basic_earned * 12.00% ";
        Assert.Equal(original,ExcelPayrollSetupRepository.TransferRulesSignature(p.Components));
    }
    [Theory][InlineData("A/(B*C)","A/B*C")][InlineData("IF(A,B,C)","IF(A,C,B)")][InlineData("ROUND(A,0)","ROUNDDOWN(A,0)")]
    public void CanonicalNativeFormulaNeverChangesOperatorBranchOrRoundingOrder(string first,string second) => Assert.NotEqual(PayrollFormulaEvaluator.Canonicalize(first),PayrollFormulaEvaluator.Canonicalize(second));
    [Theory][InlineData("BASIC _EARNED*12%")][InlineData("IF(A,1,System.IO.File.ReadAllText(1))")]
    public void CanonicalNativeFormulaRejectsInvalidOriginalBeforeRemovingWhitespace(string formula) => Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.Canonicalize(formula));
}

