using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ConfigurationAmountDifferenceTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Case(ExcelPayrollSetupRequest Request,ExcelPayrollSetupRepository.Plan Plan,
        ExcelPayrollSetupRepository.PlannedRow Row,List<ExcelPayrollTransferFormula> Formulas);
    private static string SourceHash(Case c)=>ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new{c.Plan.Batch,c.Plan.Source},Json));
    private static Case Fixture()
    {
        var cells=new ExcelPayslipCalculationSourceRow{SourceRow=8,Cells=[
            new(){DataType="Number",Value="10.005"},new(){DataType="Number",Value="20.005"},
            new(){DataType="Number",Value="30"},new(){DataType="Number",Value="30"},
            new(){DataType="Number",Value="10.005",FormulaText="A8/C8*D8"},
            new(){DataType="Number",Value="20.005",FormulaText="B8/C8*D8"},
            new(){DataType="Number",Value="30.01",FormulaText="SUM(E8:F8)"}]};
        var source=new ExcelPayslipCalculationSource{ColumnCount=7,Rows=[cells],Columns=Enumerable.Range(0,7).Select(i=>new ExcelPayslipCalculationColumn{
            ColumnIndex=i,SourceHeader=i<2?"Monthly allowance":i==2?"Payroll days":i==3?"Payable days":i==6?"Net pay":"Allowance "+i,
            Label=i==4?"Allowance A":i==5?"Allowance B":"Field "+i,Kind=i is 4 or 5?"earning":i==6?"netPay":"info"}).ToList()};
        var saved=new ExcelPayslipRow{Id="synthetic-config-review",SourceRow=8,EmployeeCode="SYN01",EmployeeName="Synthetic Example",NetPay=30.01m,
            Earnings=[new(){Label="Allowance A",Amount=10.005m},new(){Label="Allowance B",Amount=20.005m}]};
        var batch=new ExcelPayslipBatch{Id=new string('a',32),ClientId=7,Month="2026-10",Rows=[saved]};
        var q=new ExcelPayrollSetupRequest{BatchId=batch.Id,RowIds=[saved.Id],TransferReviewVersion=1,ReviewedFixedInputs=true,ReviewedPolicyScope=true,
            PayrollDaysColumnIndex=2,FixedPayrollDays=30,PayableDaysColumnIndex=3,PresentDaysColumnIndex=null,ImportAttendance=false,
            SourceColumnRoles=source.Columns.ToDictionary(c=>c.ColumnIndex,c=>c.Kind),EmployeeDecisions=[new(){RowId=saved.Id,MatchedEmployeeId=77,AllowCreate=false}],
            Components=Enumerable.Range(0,2).Select(i=>new ExcelPayrollSetupComponent{Key="allow-"+i,ExistingComponentId=(9001+i).ToString(),Code=i==0?"ALLOW_A":"ALLOW_B",
                Name=i==0?"Allowance A":"Allowance B",Category="Earning",CalculationType="Fixed Amount",SourceColumnIndex=i,ExpectedColumnIndex=i+4,Value="0",ProRata=true,PayType="Fixed Pay"}).ToList()};
        var components=q.Components.Select(c=>new ExcelPayrollSetupRepository.ResolvedComponent(c,c.ExistingComponentId!,false)).ToArray();
        var planned=new ExcelPayrollSetupRepository.PlannedRow{RowId=saved.Id,SourceRow=8,Employee=new(){Id=77,ClientId=7},TemplateId="900",Components=components};
        var plan=new ExcelPayrollSetupRepository.Plan{Batch=batch,Source=source};plan.Rows.Add(planned);plan.Components.AddRange(components);
        var formulas=q.Components.Select(c=>ExcelPayrollSetupRepository.InspectTransferFormula(q,source,saved,c)).ToList();
        Assert.All(formulas,f=>Assert.Equal("Exact supported translation",f.Status));
        var result=new Case(q,plan,planned,formulas);Refresh(result);
        q.ConfigurationDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=SourceHash(result),MaxAbsoluteDifference=1,Reason="Explicit source-bound currency review; retain every original and native amount."};
        return result;
    }
    private static void Refresh(Case c)
    {
        var calculated=ExcelPayrollSetupRepository.CalculateRow(7,c.Request,c.Row.Components,c.Plan.Source.Rows[0],c.Plan.Batch.Rows[0],
            blankAsZeroColumns:c.Plan.Source.Columns.Where(column=>column.BlankAsZero).Select(column=>column.ColumnIndex).ToHashSet(),calculationSource:c.Plan.Source);
        c.Row.Preview=calculated.Preview;
        if(c.Request.ConfigurationDifferenceReview is null)Assert.True(c.Row.Preview.WhatIfSimulation is not null,string.Join("; ",calculated.Issues.Select(i=>i.Message)));
        c.Row.Preview.EmployeeId=77;c.Row.Preview.TemplateId=c.Row.TemplateId;
        if(c.Request.ConfigurationDifferenceReview is{} declaration)declaration.SourceHash=SourceHash(c);
    }
    private static ExcelPayrollTransferAmountReview Review(Case c)=>ExcelPayrollSetupRepository.ReviewConfigurationAmountDifferences(c.Plan,c.Row,c.Request,SourceHash(c),c.Formulas,
        new(){Status="Mismatch",RowId=c.Row.RowId,SourceRow=8,Reason="Original independent classification remains visible."});

    [Fact]public void BoundedConfigurationReviewPreservesAmountsAndCannotClaimEmployeeParityOrPresence()
    {
        var c=Fixture();var before=JsonSerializer.Serialize(new{c.Request,c.Plan.Batch,c.Plan.Source,c.Row.Preview},Json);var result=Review(c);
        Assert.True(result.Status==ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,result.Reason);Assert.Equal("setup-bounded-monetary-difference-v1",result.Version);
        Assert.False(result.Acknowledged);Assert.Null(result.BoundedDifference);Assert.NotNull(result.ConfigurationDifference);
        Assert.False(result.ConfigurationDifference.EmployeePayrollParityVerified);Assert.False(result.ConfigurationDifference.ActualPresenceCertified);
        Assert.Equal(4,result.ConfigurationDifference.Totals.Count);Assert.Equal(3,result.ConfigurationDifference.SourceAggregates.Count);Assert.Equal(2,result.Lines.Count);
        Assert.Equal(64,result.ProofHash.Length);Assert.Equal(64,result.ConfigurationDifference.TemplateTraceHash.Length);
        Assert.Equal(before,JsonSerializer.Serialize(new{c.Request,c.Plan.Batch,c.Plan.Source,c.Row.Preview},Json));
    }
    private static Case BinarySourceBoundary()
    {
        var c=Fixture();
        c.Request.Components[0].CalculationType="Formula";
        c.Request.Components[0].Formula="3456.7890123456749";
        c.Request.Components[0].SourceColumnIndex=null;c.Request.Components[0].ProRata=false;
        c.Plan.Source.Rows[0].Cells[4]!.Value="3456.7890123456749";
        c.Plan.Source.Rows[0].Cells[4]!.FormulaText="3456.7890123456749";
        c.Plan.Batch.Rows[0].Earnings[0].Amount=3456.789012345675m;
        c.Plan.Batch.Rows[0].NetPay=c.Plan.Batch.Rows[0].Earnings.Sum(a=>a.Amount);
        c.Plan.Source.Rows[0].Cells[6]!.Value=c.Plan.Batch.Rows[0].NetPay.ToString("G29",CultureInfo.InvariantCulture);
        c.Formulas.Clear();c.Formulas.AddRange(c.Request.Components.Select(d=>ExcelPayrollSetupRepository.InspectTransferFormula(c.Request,c.Plan.Source,c.Plan.Batch.Rows[0],d)));
        Assert.All(c.Formulas,f=>Assert.Equal("Exact supported translation",f.Status));
        Refresh(c);return c;
    }
    [Fact]public void ExistingImporterBinaryIdentityAlsoAppliesToBoundedConfigurationSourceBinding()
    {
        var c=BinarySourceBoundary();var stored=c.Plan.Batch.Rows[0].Earnings[0].Amount;
        var captured=c.Row.Preview.Lines[0].SourceAmount!.Value;
        Assert.NotEqual(ExcelPayrollSetupRepository.SourceDisplayPrecision(stored),ExcelPayrollSetupRepository.SourceDisplayPrecision(captured));
        Assert.True(Payroll.API.Services.ExcelPayslipCalculationService.SameAdjacentCachedAmount(stored,captured));
        var before=JsonSerializer.Serialize(new{c.Request,c.Plan.Batch,c.Plan.Source,c.Row.Preview},Json);
        var result=Review(c);
        Assert.True(result.Status==ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,result.Reason);
        Assert.Equal(captured,result.Lines.Single(l=>l.Code=="ALLOW_A").SourceRaw);
        Assert.False(result.ConfigurationDifference!.EmployeePayrollParityVerified);
        Assert.False(result.ConfigurationDifference.ActualPresenceCertified);
        Assert.Equal(before,JsonSerializer.Serialize(new{c.Request,c.Plan.Batch,c.Plan.Source,c.Row.Preview},Json));
    }
    [Fact]public void BinarySourceBindingDoesNotAcceptAnAlteredSavedAmountEvenInsideTheMoneyLimit()
    {
        var c=BinarySourceBoundary();c.Plan.Batch.Rows[0].Earnings[0].Amount+=0.000001m;Refresh(c);
        var result=Review(c);
        Assert.NotEqual(ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,result.Status);
        Assert.Contains("unchanged source output",result.Reason);
    }
    [Fact]public void DefaultWireAndDefaultWorkflowDoNotAcquireAnyMonetaryPermission()
    {
        var q=new ExcelPayrollSetupRequest();Assert.DoesNotContain("configurationDifference",JsonSerializer.Serialize(q,Json),StringComparison.OrdinalIgnoreCase);
        var review=new ExcelPayrollTransferAmountReview();Assert.DoesNotContain("configurationDifference",JsonSerializer.Serialize(review,Json),StringComparison.OrdinalIgnoreCase);
        ExcelPayrollSetupRepository.RequireConfigurationDifferenceReviewEnabled(new ConfigurationBuilder().Build(),q);
        var c=Fixture();c.Request.ConfigurationDifferenceReview=null;var old=new ExcelPayrollTransferAmountReview{Status="Mismatch"};
        Assert.Same(old,ExcelPayrollSetupRepository.ReviewConfigurationAmountDifferences(c.Plan,c.Row,c.Request,SourceHash(c),c.Formulas,old));
    }
    [Fact]public void ExplicitWriterIsOffUnlessConfiguredAndDoesNotWaiveTheDeclaration()
    {
        var c=Fixture();Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.RequireConfigurationDifferenceReviewEnabled(new ConfigurationBuilder().Build(),c.Request));
        var enabled=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Payroll:ConfigurationAmountDifferenceReviewEnabled","true"}}).Build();
        ExcelPayrollSetupRepository.RequireConfigurationDifferenceReviewEnabled(enabled,c.Request);
        c.Request.ConfigurationDifferenceReview!.Reviewed=false;Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(1.01)]
    public void LimitMustRemainPositiveAndAtMostOne(decimal limit){var c=Fixture();c.Request.ConfigurationDifferenceReview!.MaxAbsoluteDifference=limit;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Theory][InlineData("version")][InlineData("reviewed")][InlineData("hash")][InlineData("reason")][InlineData("policy")][InlineData("fixed")][InlineData("transfer")][InlineData("reported")][InlineData("named")][InlineData("allocation")]
    public void MalformedOrOtherWorkflowDeclarationsCannotBeUsed(string change)
    {
        var c=Fixture();switch(change){case"version":c.Request.ConfigurationDifferenceReview!.Version=2;break;case"reviewed":c.Request.ConfigurationDifferenceReview!.Reviewed=false;break;case"hash":c.Request.ConfigurationDifferenceReview!.SourceHash="old";break;case"reason":c.Request.ConfigurationDifferenceReview!.Reason="short";break;case"policy":c.Request.ReviewedPolicyScope=false;break;case"fixed":c.Request.ReviewedFixedInputs=false;break;case"transfer":c.Request.TransferReviewVersion=0;break;case"reported":c.Request.ReportedPeriodInputs=new();break;case"named":c.Request.NamedInputReview=new();break;case"allocation":c.Request.LocationAllocations=new();break;}
        Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Theory][InlineData("employee")][InlineData("foreign")][InlineData("row")][InlineData("owner")][InlineData("create")][InlineData("newMaster")][InlineData("template")][InlineData("keep")][InlineData("formula")][InlineData("formulaHash")][InlineData("formulaColumn")][InlineData("missingFormula")][InlineData("duplicateFormula")][InlineData("category")][InlineData("sourceRole")][InlineData("total")][InlineData("trace")]
    public void MonetaryReviewCannotWaiveOwnershipCoverageRolesFormulasOrFreshNativeArithmetic(string change)
    {
        var c=Fixture();switch(change){case"employee":c.Row.Employee.Id=0;break;case"foreign":c.Row.Employee.ClientId=8;break;case"row":c.Row.SourceRow=9;break;case"owner":c.Request.EmployeeDecisions[0].MatchedEmployeeId=78;break;case"create":c.Request.EmployeeDecisions[0].AllowCreate=true;break;case"newMaster":c.Row.Components=c.Row.Components.Select((x,i)=>i==0?x with{IsNew=true}:x).ToArray();break;case"template":c.Row.TemplateId="";break;case"keep":c.Row.KeepExisting=true;break;case"formula":c.Formulas[0]=c.Formulas[0] with{Status="Blocked"};break;case"formulaHash":c.Formulas[0]=c.Formulas[0] with{SourceHash="invalid"};break;case"formulaColumn":c.Formulas[0]=c.Formulas[0] with{SourceColumn=0};break;case"missingFormula":c.Formulas.RemoveAt(0);break;case"duplicateFormula":c.Formulas[1]=c.Formulas[0];break;case"category":c.Row.Preview.Lines[0]=c.Row.Preview.Lines[0] with{Category="Deduction"};break;case"sourceRole":c.Request.SourceColumnRoles[4]="info";break;case"total":c.Row.Preview.Net++;break;case"trace":c.Row.Preview.Lines[0]=c.Row.Preview.Lines[0] with{UnroundedEarned=123};break;}
        Assert.NotEqual(ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,Review(c).Status);
    }
    [Fact]public void OppositeComponentDifferencesCannotHideBeyondLimitEvenWhenNetIsEqual()
    {
        var c=Fixture();c.Plan.Source.Rows[0].Cells[4]!.Value="11.015";c.Plan.Source.Rows[0].Cells[5]!.Value="18.995";
        c.Plan.Batch.Rows[0].Earnings[0].Amount=11.015m;c.Plan.Batch.Rows[0].Earnings[1].Amount=18.995m;Refresh(c);
        var result=Review(c);Assert.NotEqual(ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,result.Status);Assert.Contains("per-line monetary limit",result.Reason);
    }
    [Fact]public void WrongSourceOrStaleAcknowledgementDoesNotBecomeAReview()
    {
        var c=Fixture();var old=Review(c).ProofHash;c.Request.RoundingReviews[c.Row.RowId]=old;Assert.Equal(old,Review(c).ProofHash);
        c.Request.ConfigurationDifferenceReview!.Reason+=" Reviewed revised comparison.";Assert.NotEqual(old,Review(c).ProofHash);
        c.Request.ConfigurationDifferenceReview.SourceHash=new string('e',64);Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Fact]public void ConfigurationEvidenceCannotSatisfyHistoricalEmployeeReview()
    {
        var c=Fixture();var result=Review(c);var historical=new HistoricalPeriodSalaryRequest{Setup=c.Request,AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=SourceHash(c),MaxAbsoluteDifference=1,Reason="Separate historical review"}};
        c.Request.RoundingReviews[c.Row.RowId]=result.ProofHash;Assert.False(ExcelPayrollSetupRepository.IsAcknowledgedHistoricalAmountDifference(historical,result));
    }
    private static ExcelPayrollSetupComponent AddPeriod(Case c,bool blank)
    {
        var component=new ExcelPayrollSetupComponent{Key="period",ExistingComponentId="9003",Code="PERIOD_REC",Name="Period recovery",Category="Deduction",
            ComponentRole="Regular Deduction",CalculationType="Manual / Variable",PayType="Variable Pay",Value="0",SourceColumnIndex=7,ExpectedColumnIndex=7,IsPeriodAdjustment=true,ProRata=false,BlankInputAsZero=true};
        c.Request.Components.Add(component);c.Request.SourceColumnRoles[7]="deduction";
        c.Plan.Source.Columns.Add(new(){ColumnIndex=7,SourceHeader="Period recovery",Label="Period recovery",Kind="deduction",BlankAsZero=true});c.Plan.Source.ColumnCount=8;
        c.Plan.Source.Rows[0].Cells.Add(new(){DataType=blank?"Blank":"Number",Value=blank?"":"0.005"});
        var amount=blank?0:.005m;c.Plan.Batch.Rows[0].Deductions.Add(new(){Label="Period recovery",Amount=amount});c.Plan.Batch.Rows[0].NetPay-=amount;
        c.Plan.Source.Rows[0].Cells[6]!.Value=c.Plan.Batch.Rows[0].NetPay.ToString("G29",CultureInfo.InvariantCulture);c.Plan.Source.Rows[0].Cells[6]!.FormulaText="SUM(E8:F8)-H8";
        c.Plan.Components.Add(new(component,"9003",false));c.Row.Components=c.Plan.Components.ToArray();c.Formulas.Clear();c.Formulas.AddRange(c.Request.Components.Select(d=>ExcelPayrollSetupRepository.InspectTransferFormula(c.Request,c.Plan.Source,c.Plan.Batch.Rows[0],d)));
        Assert.All(c.Formulas,f=>Assert.Contains(f.Status,new[]{"Exact supported translation","Reviewed fixed source"}));Refresh(c);return component;
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void ExplicitPeriodLiteralsAndDoublyReviewedOptionalZeroKeepTheirOrdinaryCurrencyStage(bool blank)
    {
        var c=Fixture();AddPeriod(c,blank);var before=JsonSerializer.Serialize(new{c.Request,c.Plan.Source,c.Plan.Batch,c.Row.Preview},Json);var result=Review(c);
        Assert.True(result.Status==ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,result.Reason);
        Assert.Equal(0,result.Lines.Single(l=>l.Code=="PERIOD_REC").Calculated);Assert.Equal(before,JsonSerializer.Serialize(new{c.Request,c.Plan.Source,c.Plan.Batch,c.Row.Preview},Json));
    }
    [Theory][InlineData("formula")][InlineData("error")][InlineData("missing")][InlineData("text")][InlineData("unreviewed")][InlineData("foreignInput")]
    public void PeriodReviewCannotFreezeFormulaCachesOrGuessMissingFinancialInputs(string mutation)
    {
        var c=Fixture();var definition=AddPeriod(c,true);var cell=c.Plan.Source.Rows[0].Cells[7]!;
        switch(mutation){case"formula":cell.FormulaText="1-1";break;case"error":cell.Error="#VALUE!";break;case"missing":cell.MissingCachedValue=true;break;case"text":cell.DataType="Text";cell.Value="0";break;case"unreviewed":definition.BlankInputAsZero=false;break;case"foreignInput":definition.SourceColumnIndex=0;break;}
        c.Request.ConfigurationDifferenceReview!.SourceHash=SourceHash(c);
        Assert.NotEqual(ExcelPayrollSetupRepository.BoundedConfigurationDifferenceStatus,Review(c).Status);
    }
    [Fact]public void HistoricalAndReportedAdaptersExplicitlyRejectConfigurationOnlyMetadata()
    {
        var c=Fixture();var request=new HistoricalPeriodSalaryRequest{Setup=c.Request};
        Assert.Contains("Configuration monetary review",Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateHistoricalSalaryRequest(request)).Message);
        Assert.Contains("Configuration monetary review",Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateReportedHistoricalSalaryRequest(request)).Message);
    }
}
