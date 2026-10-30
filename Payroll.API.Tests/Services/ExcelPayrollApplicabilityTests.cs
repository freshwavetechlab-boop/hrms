using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
namespace Payroll.API.Tests.Services;
public sealed partial class ExcelPayrollTransferTests
{
    private static (ExcelPayrollSetupRepository.Plan,ExcelPayrollSetupRequest) ScopeFixture() {
        var(p,q)=Fixture();q.TemplateScopeReviewVersion=1;q.TemplateScope=new(){Reviewed=true,EffectiveTo="2026-09-30",LocationMode="AllClientLocations",CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
        q.TemplateDecisions=[new(){VariantKey="1000",ExistingTemplateId="500"}];return(p,q);
    }
    private static ExcelPayrollRecordedTemplateScope RecordedScope() {
        var(p,q)=ScopeFixture();q.TemplateDecisions[0].ScopeAction="AttestLegacyScope";ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.True(p.Preview.CanApply);return p.Preview.Transfer!.Templates.Single().Applicability!;
    }
    [Fact] public void UnknownLegacyApplicabilityNeedsExplicitAttestationAndPersistsOnlyCohortEvidence() {
        var(p,q)=ScopeFixture();var template=Template();var original=template.ToJsonString();ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[template]);
        var candidate=p.Preview.Transfer!.Templates.Single().Candidates.Single();Assert.True(candidate.ConfigurationExact);Assert.False(candidate.Exact);Assert.Equal("Unknown legacy scope",candidate.ScopeStatus);Assert.False(p.Preview.CanApply);
        (p,q)=ScopeFixture();q.TemplateDecisions[0].ScopeAction="AttestLegacyScope";ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[template]);
        Assert.True(p.Preview.CanApply);Assert.Equal(original,template.ToJsonString());Assert.Equal(new[]{"500"},p.Preview.TemplateIds);
        var saved=JsonSerializer.Deserialize<ExcelPayrollTransferReview>(JsonSerializer.Serialize(p.Preview.Transfer))!;var scope=saved.Templates.Single().Applicability!;Assert.Equal(42,scope.ClientId);Assert.Equal(p.Batch.Id,scope.SourceBatchId);Assert.StartsWith("SOURCE_RULES:",scope.PolicyKey);
    }
    [Fact] public void KnownCompatibleScopeReusesWithoutAnotherLegacyAttestation() {
        var(p,q)=ScopeFixture();p.TemplateScopes=[RecordedScope()];ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.True(p.Preview.CanApply);Assert.Equal("Compatible",p.Preview.Transfer!.Templates.Single().Candidates.Single().ScopeStatus);Assert.Single(p.ReusedTemplateIds);
    }
    [Theory][InlineData("Divisor")][InlineData("PolicyVersion")]
    public void SameArithmeticCannotForceReuseAcrossDifferentDayMeaningOrPolicyVersion(string field) {
        var(p,q)=ScopeFixture();p.TemplateScopes=[RecordedScope()];q.TemplateDecisions[0].ScopeAction="ExtendReviewedScope";
        if(field=="Divisor")q.TemplateScope!.DivisorMeaning="ContractFixedDays";else{q.TemplateScope!.PolicyBasis="DatedPolicy";q.TemplateScope.PolicyVersion="approved-v2";}
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.False(p.Preview.CanApply);Assert.Equal("Different policy/basis",p.Preview.Transfer!.Templates.Single().Candidates.Single().ScopeStatus);
    }
    [Fact] public void LaterPeriodRequiresReviewedExtensionAndPreservesPriorReceipt() {
        var old=RecordedScope();var before=JsonSerializer.Serialize(old);var(p,q)=ScopeFixture();p.Batch.Month="2026-10";q.EffectiveFrom="2026-10-01";q.TemplateScope!.EffectiveTo="2026-10-31";p.TemplateScopes=[old];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.False(p.Preview.CanApply);Assert.Equal("Extension review required",p.Preview.Transfer!.Templates.Single().Candidates.Single().ScopeStatus);
        (p,q)=ScopeFixture();p.Batch.Month="2026-10";q.EffectiveFrom="2026-10-01";q.TemplateScope!.EffectiveTo="2026-10-31";q.TemplateDecisions[0].ScopeAction="ExtendReviewedScope";p.TemplateScopes=[old];ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);
        Assert.True(p.Preview.CanApply);Assert.Equal("2026-10-01",p.Preview.Transfer!.Templates.Single().Applicability!.EffectiveFrom);Assert.Equal(before,JsonSerializer.Serialize(old));
    }
    [Fact] public void SameRulesAtAnotherLocationNeedExplicitScopeExtensionNotAnotherEmployeeRateTemplate() {
        var old=RecordedScope();old.Scope.LocationMode="Listed";old.Scope.WorkLocationIds=[1];var(p,q)=ScopeFixture();q.TemplateScope!.LocationMode="Listed";q.TemplateScope.WorkLocationIds=[2];p.Rows[0].Employee.WorkLocationId=2;p.TemplateScopes=[old];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.False(p.Preview.CanApply);
        (p,q)=ScopeFixture();q.TemplateScope!.LocationMode="Listed";q.TemplateScope.WorkLocationIds=[2];p.Rows[0].Employee.WorkLocationId=2;p.TemplateScopes=[old];q.TemplateDecisions[0].ScopeAction="ExtendReviewedScope";ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);
        Assert.True(p.Preview.CanApply);Assert.Equal("500",p.Rows[0].TemplateId);Assert.Equal(new[]{1},old.Scope.WorkLocationIds);
    }
    [Fact] public void MissingOrWrongEmployeeCategoryBlocksRatherThanInferringItFromPay() {
        var(p,q)=ScopeFixture();q.TemplateScope!.CategoryKind="SkillCategory";q.TemplateScope.Categories=["Skilled"];q.TemplateDecisions[0].ScopeAction="AttestLegacyScope";
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.False(p.Preview.CanApply);Assert.Contains(p.Preview.Issues,i=>i.Message.Contains("category is missing"));
    }
    [Fact] public void IdenticalRulesNeedReasonBeforeSeparateUnknownApplicabilityVariant() {
        var(p,q)=ScopeFixture();q.TemplateDecisions=[new(){VariantKey="1000",CreateVariant=true}];ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.False(p.Preview.CanApply);
        (p,q)=ScopeFixture();q.TemplateDecisions=[new(){VariantKey="1000",CreateVariant=true,ScopeAction="DifferentScope",ScopeReason="Reviewed separate contract and location applicability."}];ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.True(p.Preview.CanApply);
    }
    [Fact] public void EffectiveDatedSourceRateChangeCreatesNewVariantWithoutRewritingOldTemplate() {
        var old=RecordedScope();var oldJson=JsonSerializer.Serialize(old);var template=Template();var original=template.ToJsonString();var(p,q)=ScopeFixture();p.TemplateScopes=[old];p.Batch.Month="2026-10";q.EffectiveFrom="2026-10-01";q.TemplateScope!.EffectiveTo="2026-10-31";q.Components[1].Formula="BASIC_EARNED*13%";p.Source.Rows[0].Cells[4]!.FormulaText="D3*13%";p.Source.Rows[0].Cells[4]!.Value="13";p.Batch.Rows[0].Deductions[0].Amount=13;p.Batch.Rows[0].NetPay=87;
        var calculated=ExcelPayrollSetupRepository.CalculateRow(42,q,p.Components,p.Source.Rows[0],p.Batch.Rows[0]);p.Rows[0].Preview=calculated.Preview;p.Preview.Rows.Clear();p.Preview.Rows.Add(calculated.Preview);q.TemplateDecisions=[new(){VariantKey="1000",CreateVariant=true}];
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[template]);Assert.True(p.Preview.CanApply);Assert.Equal(87,p.Rows[0].Preview.Net);Assert.Equal("2026-10-01",p.Preview.Transfer!.Templates.Single().Applicability!.EffectiveFrom);Assert.Equal(original,template.ToJsonString());Assert.Equal(oldJson,JsonSerializer.Serialize(old));
    }
    [Fact] public void ScopeSignatureSeparatesEffectivePeriodsButIgnoresLocationListOrder() {
        var(p,q)=ScopeFixture();q.TemplateScope!.LocationMode="Listed";q.TemplateScope.WorkLocationIds=[1,2];var first=ExcelPayrollSetupRepository.ScopedTemplateSignature(p.Components,q);q.TemplateScope.WorkLocationIds=[2,1];Assert.Equal(first,ExcelPayrollSetupRepository.ScopedTemplateSignature(p.Components,q));q.EffectiveFrom="2026-10-01";Assert.NotEqual(first,ExcelPayrollSetupRepository.ScopedTemplateSignature(p.Components,q));
    }
    [Fact] public void KeepCurrentCanRemainUnchangedWithoutInventingApplicability() {
        var(p,q)=ScopeFixture();p.Rows[0].ExistingTemplateId="500";q.TemplateScope=null;q.TemplateDecisions=[new(){VariantKey="1000",KeepExisting=true}];ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[Template()]);Assert.True(p.Preview.CanApply);Assert.Null(p.Preview.Transfer!.Templates.Single().Applicability);Assert.Equal(new[]{"500"},p.Preview.TemplateIds);
    }
    [Fact] public void ScopeConfirmationIsRequiredAndInvalidEndDateCannotApply() {
        var(p,q)=ScopeFixture();q.TemplateScope!.Reviewed=false;Assert.NotEmpty(ExcelPayrollSetupRepository.TemplateScopeIssues(q,p.Rows));q.TemplateScope.Reviewed=true;q.TemplateScope.EffectiveTo="2026-08-31";Assert.Contains(ExcelPayrollSetupRepository.TemplateScopeIssues(q,p.Rows),m=>m.Contains("end date"));
    }

    [Fact] public void RulesSignatureIncludesClassificationAndNotOnlyTheSameComponentIdAndFormula() {
        var(p,q)=ScopeFixture();var first=ExcelPayrollSetupRepository.TransferRulesSignature(p.Components);q.Components[1].StatutoryType="PF Employee";Assert.NotEqual(first,ExcelPayrollSetupRepository.TransferRulesSignature(p.Components));
    }
}
