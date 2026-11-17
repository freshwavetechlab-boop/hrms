using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed partial class ReportedPeriodInputV2Tests
{
    [Fact]
    public void ReportedSnapshotWithRetainedProfileMetadataClosesCashAndRejectsStaleMetadata()
    {
        var f=Fixture.Create();
        f.Batch.ProfileLinks=[];
        PopulateRow(f); f.RefreshHashes();
        var snapshot=ReportedPeriodInputProof.SourceHash(f.Batch,f.Source);
        Assert.NotEqual(snapshot,ExcelPayslipRepository.EmploymentSourceHash(f.Batch,f.Source));
        var bound=f.Bind(); var closure=f.Close(bound); var packet=TemplatePacket(f,closure);
        var result=ExcelPayrollSetupRepository.PrepareReportedTemplateArithmetic(packet.Request,f.Batch,f.Source,f.Row,packet.Template,packet.Masters,f.Profile,bound,600000);
        Assert.Equal(12,result.Arithmetic.Preview.Lines.Count);
        Assert.Equal(snapshot,ReportedPeriodInputProof.SourceHash(f.Batch,f.Source));
        f.Batch.ProfileLinks=null;
        Assert.Throws<InvalidOperationException>(()=>f.Bind());
    }

    [Theory]
    [InlineData(0,0)] [InlineData(5,25)] [InlineData(4.5,24)] [InlineData(1,23)]
    public void NativeCalculatorUsesExactIndependentValuesAndBothReportedDayInputs(decimal oldDays,decimal newDays)
    {
        var f=Fixture.Create(oldDays,newDays); var b=f.Bind(); var closure=f.Close(b);
        var context=ReportedPeriodCalculationContext.FromBound(f.Profile,b);
        var request=NativeRequest(f,closure,context);
        var actual=PayRunRepository.SimulateTemplate(request);
        Assert.True(actual.Complete,string.Join("; ",actual.Issues.Select(i=>i.Message)));
        Assert.Equal(12,actual.Lines.Count);
        foreach(var cell in closure.Cells.Where(c=>c.Kind is "earning" or "deduction" or "employer"))
            Assert.Equal(decimal.Round(cell.Calculated,2,MidpointRounding.ToEven),actual.Lines.Single(l=>l.ComponentId=="synthetic-"+cell.ColumnIndex).Earned);
        Assert.Equal(oldDays+newDays,request.PayableDays);
        Assert.Equal(10000.1234567890123m,actual.Lines.First(l=>l.Code=="C17").NamedOperands!.Values["BASE_OLD_ALLOWANCE"]);
        Assert.Equal("ReferenceOnly",f.Source.DependencyReviewStatus);
        Assert.False(b.ActualPresenceKnown); Assert.False(b.ExternalLookupsVerified);
    }

    [Fact]
    public void V2NativeCalculationDoesNotMergeDifferentRateSplitsWithTheSameTotalDays()
    {
        var a=Fixture.Create(0,24); var b=Fixture.Create(1,23);
        var ar=PayRunRepository.SimulateTemplate(NativeRequest(a,a.Close(a.Bind()),ReportedPeriodCalculationContext.FromBound(a.Profile,a.Bind())));
        var br=PayRunRepository.SimulateTemplate(NativeRequest(b,b.Close(b.Bind()),ReportedPeriodCalculationContext.FromBound(b.Profile,b.Bind())));
        Assert.True(ar.Complete); Assert.True(br.Complete);
        Assert.NotEqual(ar.Gross,br.Gross);
    }

    [Fact]
    public void UploadingSerializedV2ValuesCannotManufactureTheTrustedCalculationContext()
    {
        var f=Fixture.Create();var b=f.Bind();var context=ReportedPeriodCalculationContext.FromBound(f.Profile,b);
        var json=JsonSerializer.Serialize(context,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("reportedPeriodProof",json,StringComparison.OrdinalIgnoreCase);
        var uploaded=JsonSerializer.Deserialize<NamedFormulaInputContext>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(uploaded.ReportedPeriodProof);
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.SimulateTemplate(NativeRequest(f,f.Close(b),uploaded)));
    }

    [Theory]
    [InlineData("value")] [InlineData("payable")] [InlineData("divisor")] [InlineData("definition")]
    public void NativeV2RejectsTamperedContextOrDayOverrides(string mutation)
    {
        var f=Fixture.Create();var b=f.Bind();var context=ReportedPeriodCalculationContext.FromBound(f.Profile,b);
        var request=NativeRequest(f,f.Close(b),context);
        switch(mutation)
        {
            case "value":context.Values["DAYS_OLD_RATE"]=4;break;
            case "definition":context.Definitions[0]=context.Definitions[0] with { Precision=4 };break;
            case "payable":request.PayableDays=28;break;
            case "divisor":request.PayrollDays=31;break;
        }
        if(mutation is "value" or "definition") Assert.Throws<InvalidOperationException>(()=>PayRunRepository.SimulateTemplate(request));
        else Assert.False(PayRunRepository.SimulateTemplate(request).Complete);
    }

    [Fact]
    public void TrustedContextCloningPreservesProofButNeverSharesMutableValues()
    {
        var f=Fixture.Create();var original=ReportedPeriodCalculationContext.FromBound(f.Profile,f.Bind());
        var clone=ReportedPeriodCalculationContext.Clone(original);
        ReportedPeriodCalculationContext.Validate(clone,[],30,28.5m);
        clone.Values["DAYS_NEW_RATE"]=23;
        Assert.Equal(24,original.Values["DAYS_NEW_RATE"]);
        Assert.Throws<InvalidOperationException>(()=>ReportedPeriodCalculationContext.Validate(clone,[],30,28.5m));
    }

    [Fact]
    public void DedicatedTemplateAdapterUsesTheSameNativeCalculatorAndPreservesTheRetainedTarget()
    {
        var f=Fixture.Create(); PopulateRow(f); var bound=f.Bind();var closure=f.Close(bound);
        var packet=TemplatePacket(f,closure);
        var before=ReportedPeriodInputProof.SourceHash(f.Batch,f.Source);
        var proof=ExcelPayrollSetupRepository.PrepareReportedTemplateArithmetic(packet.Request,f.Batch,f.Source,f.Row,packet.Template,packet.Masters,f.Profile,bound,600000);
        Assert.Equal(600000,proof.Arithmetic.Preview.AnnualCtc);
        Assert.Equal(28.5m,proof.Arithmetic.Preview.PayableDays);
        Assert.Empty(proof.Arithmetic.Inputs); Assert.Empty(proof.Arithmetic.Adjustments);
        Assert.Equal(12,proof.Arithmetic.Preview.Lines.Count);
        Assert.NotNull(proof.Amounts);Assert.Equal(4,proof.Amounts!.Totals.Count);
        Assert.Equal(before,ReportedPeriodInputProof.SourceHash(f.Batch,f.Source));
        Assert.All(packet.Template["lines"]!.AsArray().OfType<JsonObject>().Where(l=>l["calculationType"]!.ToString()=="Formula"),l=>Assert.DoesNotContain("VLOOKUP",l["formula"]!.ToString()));
    }

    [Theory]
    [InlineData(543,0,10000)] [InlineData(0,1200,0)]
    public void TemplateAdapterKeepsNonzeroPeriodCashAndSignedNegativeOutputsAsSeparateGates(decimal arrears,decimal offset,decimal allowance)
    {
        var f=Fixture.Create(5,25,arrears,allowance,offset);PopulateRow(f);var bound=f.Bind();var packet=TemplatePacket(f,f.Close(bound));
        var error=Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.PrepareReportedTemplateArithmetic(packet.Request,f.Batch,f.Source,f.Row,packet.Template,packet.Masters,f.Profile,bound,600000));
        Assert.Contains(arrears!=0?"approved transaction":"Signed negative",error.Message);
    }

    [Theory]
    [InlineData("divisor")] [InlineData("financial-role")] [InlineData("day-role")] [InlineData("master-paytype")] [InlineData("master-priority")]
    public void TemplateAdapterCannotCertifyARequestOrNativeMasterDifferentFromItsBoundProof(string mutation)
    {
        var f=Fixture.Create();PopulateRow(f);var bound=f.Bind();var packet=TemplatePacket(f,f.Close(bound));
        switch(mutation)
        {
            case "divisor":packet.Request.SourceReview.FixedPayrollDays=31;break;
            case "financial-role":packet.Request.SourceReview.SourceColumnRoles[16]="ignore";break;
            case "day-role":packet.Request.SourceReview.SourceColumnRoles[15]="earning";break;
            case "master-paytype":packet.Masters[0]["payType"]="Variable Pay";break;
            case "master-priority":packet.Masters[0]["priority"]=999;break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.PrepareReportedTemplateArithmetic(packet.Request,f.Batch,f.Source,f.Row,packet.Template,packet.Masters,f.Profile,bound,600000));
    }

    private static PayrollSimulationRequest NativeRequest(Fixture f,ReportedPeriodCashClosureV2 closure,NamedFormulaInputContext context)
    {
        var cash=closure.Cells.Where(c=>c.Kind is "earning" or "deduction" or "employer").ToArray();
        var components=cash.Select(c=>new PayrollReviewComponent("synthetic-"+c.ColumnIndex,"C"+c.ColumnIndex,c.Kind switch { "earning"=>"Earning","deduction"=>"Deduction",_=>"Benefit" },c.Kind=="employer"?"Employer Benefit":"Regular Earning","None",string.IsNullOrWhiteSpace(c.OriginalFormula)?"Manual / Variable":"Formula","0",string.IsNullOrWhiteSpace(c.OriginalFormula)?"":c.NativeExpression,"",false,true,true,c.ColumnIndex,"Fixed Pay")).ToList();
        var lines=components.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Formula,c.CalculationType,"","false")).ToList();
        var bound=f.Bind();
        return new() { ClientId=7,TemplateId="12",Mode="Template",PayrollDays=bound.PayrollDivisor,PresentDays=Math.Min(bound.PayableDays,bound.PayrollDivisor),PayableDays=bound.PayableDays,AnnualCtc=600000,NamedInputs=context,Snapshot=new("Template",7,"",600000,components,lines) };
    }

    private static void PopulateRow(Fixture f)
    {
        var cells=f.Source.Rows.Single(r=>r.SourceRow==f.Row.SourceRow).Cells;
        decimal Amount(int column)=>decimal.Parse(cells[column]!.Value,CultureInfo.InvariantCulture);
        f.Row.Earnings=f.Source.Columns.Where(c=>c.Kind=="earning").Select(c=>new ExcelPayslipAmount { Label=c.Label,Amount=Amount(c.ColumnIndex) }).ToList();
        f.Row.Deductions=f.Source.Columns.Where(c=>c.Kind=="deduction").Select(c=>new ExcelPayslipAmount { Label=c.Label,Amount=Amount(c.ColumnIndex) }).ToList();
        f.Row.EmployerContributions=f.Source.Columns.Where(c=>c.Kind=="employer").Select(c=>new ExcelPayslipAmount { Label=c.Label,Amount=Amount(c.ColumnIndex) }).ToList();
        f.Row.DeclaredGross=Amount(32);f.Row.NetPay=Amount(37);f.RefreshHashes();
    }

    private static (NamedFormulaTemplateRequest Request,JsonObject Template,List<JsonObject> Masters) TemplatePacket(Fixture f,ReportedPeriodCashClosureV2 closure)
    {
        var simulation=NativeRequest(f,closure,ReportedPeriodCalculationContext.FromBound(f.Profile,f.Bind()));
        var q=new ExcelPayrollSetupRequest { BatchId=f.Batch.Id,RowIds=[f.Row.Id],TemplateName="Synthetic split wages",EffectiveFrom="2026-10-01",FixedPayrollDays=30,PayableDaysColumnIndex=27,ImportAttendance=false,ReportedPeriodInputs=f.Review,EmployeeDecisions=[new(){RowId=f.Row.Id,MatchedEmployeeId=101}],Components=simulation.Snapshot!.Components.Select(c=>new ExcelPayrollSetupComponent { Key=c.Id,ExistingComponentId=c.Id,Code=c.Code,Name=c.Code,Category=c.Category,ComponentRole=c.ComponentRole,StatutoryType=c.StatutoryType,CalculationType=c.CalculationType,Value=c.Value,Formula=c.Formula,ProRata=false,Taxable=c.Taxable,Priority=c.Priority,PayType=c.PayType,ExpectedColumnIndex=int.Parse(c.Code[1..],CultureInfo.InvariantCulture) }).ToList() };
        var masters=simulation.Snapshot.Components.Select(c=>new JsonObject { ["id"]=c.Id,["code"]=c.Code,["category"]=c.Category,["componentRole"]=c.ComponentRole,["statutoryType"]=c.StatutoryType,["calculationType"]=c.CalculationType,["taxable"]=c.Taxable,["priority"]=c.Priority,["payType"]=c.PayType,["active"]=true }).ToList();
        var template=new JsonObject { ["id"]=12,["clientId"]="7",["active"]=true,["lines"]=new JsonArray(simulation.Snapshot.Lines.Select(l=>(JsonNode)new JsonObject { ["componentId"]=l.ComponentId,["value"]=l.Value,["calculationType"]=l.CalculationType,["formula"]=l.Formula,["baseComponent"]=l.BaseComponent,["proRataOverride"]=l.ProRataOverride,["roundingMode"]="" }).ToArray()) };
        var request=new NamedFormulaTemplateRequest { ClientId=7,Name="Synthetic reported wages",ExistingTemplateId="12",SourceRowId=f.Row.Id,SourceHash=f.Review.SourceHash,SourceReview=q,Definitions=f.Profile.Definitions,AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=f.Review.SourceHash,MaxAbsoluteDifference=1,Reason="Native currency rounding is explicitly reviewed."} };
        return (request,template,masters);
    }
}
