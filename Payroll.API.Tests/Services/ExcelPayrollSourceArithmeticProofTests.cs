using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollSourceArithmeticProofTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRequest Request) Fixture(double monthly=10000d,int units=17)
    {
        string N(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
        var basic=monthly/26*units;var bonus=833.5d/26*units;var net=(monthly+833.5d)/26*units-bonus;
        string[] values=["26",units.ToString(CultureInfo.InvariantCulture),N(monthly),"833.5",N(basic),N(bonus),N(net),N(monthly+833.5d),N(monthly)];
        string?[] expressions=[null,null,null,null,"C3/A3*B3","D3/A3*B3","H3/A3*B3-F3","SUM(C3:D3)",null];
        var q=new ExcelPayrollSetupRequest {PayrollDaysColumnIndex=0,PayableDaysColumnIndex=1,ReviewedFixedInputs=true,ImportAttendance=false,
            Components=[new(){Key="basic",Code="BASIC",SourceColumnIndex=2,ExpectedColumnIndex=4,ProRata=true}]};
        var source=new ExcelPayslipCalculationSource {ColumnCount=9,HeaderRow=2,
            Columns=Enumerable.Range(0,9).Select(i=>new ExcelPayslipCalculationColumn{ColumnIndex=i,Kind=i==4?"earning":i==6?"netPay":"info"}).ToList(),
            Rows=[new(){SourceRow=3,Cells=values.Select((value,i)=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value=value,FormulaText=expressions[i]}).ToList()}]};
        var saved=new ExcelPayslipRow {Id="synthetic",SourceRow=3,NetPay=decimal.Parse(N(net),CultureInfo.InvariantCulture),Earnings=[new(){Label="Basic",Amount=decimal.Parse(N(basic),CultureInfo.InvariantCulture)}]};
        var p=new ExcelPayrollSetupRepository.Plan {Batch=new(){ClientId=42,Rows=[saved]},Source=source};
        p.Components.Add(new(q.Components[0],"1",true));
        p.Rows.Add(new(){RowId=saved.Id,SourceRow=3,Components=p.Components,
            Preview=ExcelPayrollSetupRepository.CalculateRow(42,q,p.Components,source.Rows[0],saved,calculationSource:source).Preview});
        return(p,q);
    }
    [Fact] public void OffsetBonusAndDistributedProrationHaveAnExactSourceNetIdentity()
    {
        var(p,q)=Fixture();var before=JsonSerializer.Serialize(new{p.Source,p.Batch,p.Rows[0].Preview});
        Assert.True(ExcelPayrollSetupRepository.HasSourceArithmeticIdentity(p,p.Rows[0],q));
        var proofs=q.Components.Select(c=>ExcelPayrollSetupRepository.InspectTransferFormula(q,p.Source,p.Batch.Rows[0],c)).ToList();
        var review=ExcelPayrollSetupRepository.ClassifyTransferAmounts(p,p.Rows[0],q,new string('a',64),proofs);
        Assert.Equal("Explained rounding",review.Status);
        Assert.Equal(before,JsonSerializer.Serialize(new{p.Source,p.Batch,p.Rows[0].Preview}));
    }
    [Fact] public void OriginalCacheAndBrowserJsonMayNameTheSameIeeeValueAcrossADecimalDisplayMidpoint()
    {
        var(p,q)=Fixture(1000,16);var net=p.Batch.Rows[0].NetPay;
        var value=double.Parse(net.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);
        p.Source.Rows[0].Cells[6]!.Value=value.ToString("G17",CultureInfo.InvariantCulture);
        var cache=decimal.Parse(p.Source.Rows[0].Cells[6]!.Value,CultureInfo.InvariantCulture);
        Assert.NotEqual(ExcelPayrollSetupRepository.SourceDisplayPrecision(net),ExcelPayrollSetupRepository.SourceDisplayPrecision(cache));
        Assert.Equal(value,double.Parse(cache.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture));
        Assert.True(ExcelPayrollSetupRepository.HasSourceArithmeticIdentity(p,p.Rows[0],q));
    }
    [Theory] [InlineData("net cache")][InlineData("component cache")][InlineData("fee")][InlineData("tiny fee")][InlineData("equal unrelated base")][InlineData("other row")]
    public void CacheTamperingFeesAndCoincidentallyEqualOtherInputsAreNotArithmeticProof(string change)
    {
        var(p,q)=Fixture();var cells=p.Source.Rows[0].Cells;
        if(change=="net cache") {cells[6]!.Value=(p.Batch.Rows[0].NetPay+0.001m).ToString(CultureInfo.InvariantCulture);p.Batch.Rows[0].NetPay+=0.001m;}
        if(change=="component cache") {cells[4]!.Value=(p.Rows[0].Preview.Lines[0].SourceAmount+0.001m)!.Value.ToString(CultureInfo.InvariantCulture);p.Rows[0].Preview.Lines[0]=p.Rows[0].Preview.Lines[0] with {SourceAmount=p.Rows[0].Preview.Lines[0].SourceAmount+0.001m};}
        if(change is "fee" or "tiny fee") {var delta=change=="fee"?"0.001":"0.000000000001";cells[6]!.FormulaText+="+"+delta;var net=double.Parse(cells[6]!.Value,CultureInfo.InvariantCulture)+double.Parse(delta,CultureInfo.InvariantCulture);cells[6]!.Value=net.ToString("R",CultureInfo.InvariantCulture);p.Batch.Rows[0].NetPay=decimal.Parse(cells[6]!.Value,CultureInfo.InvariantCulture);}
        if(change=="equal unrelated base")cells[6]!.FormulaText="I3/A3*B3";
        if(change=="other row")cells[6]!.FormulaText="C4/A3*B3";
        Assert.False(ExcelPayrollSetupRepository.HasSourceArithmeticIdentity(p,p.Rows[0],q));
    }
}
