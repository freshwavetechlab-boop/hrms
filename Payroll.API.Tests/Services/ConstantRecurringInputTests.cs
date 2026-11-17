using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ConstantRecurringInputTests
{
    private static (ExcelPayrollSetupRequest Q, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row) Fixture(string expression = "700+300-25", decimal amount = 975m)
    {
        var q = new ExcelPayrollSetupRequest { BatchId = new string('b',32), RowIds = ["synthetic-7"], TemplateName = "Synthetic monthly inputs",
            EffectiveFrom = "2026-10-01", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            ReviewedFixedInputs = true, ReviewedPolicyScope = true, ImportAttendance = false };
        q.Components = [new() { Key="basic",Code="BASIC",Name="Basic",Category="Earning",ComponentRole="Regular Earning",CalculationType="Fixed Amount",PayType="Fixed Pay",Value="0",SourceColumnIndex=2,ExpectedColumnIndex=4,ProRata=true,Priority=10 },
            new() { Key="other",Code="OTHER_ALLOW",Name="Other allowance",Category="Earning",ComponentRole="Regular Earning",CalculationType="Fixed Amount",PayType="Fixed Pay",Value="0",SourceColumnIndex=3,ExpectedColumnIndex=5,ProRata=true,Priority=20 }];
        q.SourceColumnRoles[3]="info";
        var source=new ExcelPayslipCalculationSource { ColumnCount=6,HeaderRow=1,
            Columns=Enumerable.Range(0,6).Select(i=>new ExcelPayslipCalculationColumn { ColumnIndex=i,Kind=i<4?"info":"earning",SourceHeader=i==3?"Other allowance":"Input "+i,Label=i==3?"Other allowance":i==4?"Basic":i==5?"Other":"Input "+i }).ToList(),
            Rows=[new(){SourceRow=7,Cells=Enumerable.Range(0,6).Select(_=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { DataType="Number",Value="0" }).ToList()}] };
        var cells=source.Rows[0].Cells;cells[0]!.Value="30";cells[1]!.Value="15";cells[2]!.Value="12000";cells[3]!.Value=amount.ToString(CultureInfo.InvariantCulture);cells[3]!.FormulaText=expression;
        cells[4]!.Value="6000";cells[4]!.FormulaText="C7/A7*B7";cells[5]!.Value=(amount/2).ToString(CultureInfo.InvariantCulture);cells[5]!.FormulaText="D7/A7*B7";
        var row=new ExcelPayslipRow { Id="synthetic-7",SourceRow=7,EmployeeCode="SYN001",EmployeeName="Synthetic Person",DeclaredGross=6000+amount/2,NetPay=6000+amount/2,
            Earnings=[new(){Label="Basic",Amount=6000},new(){Label="Other",Amount=amount/2}] };
        return(q,source,row);
    }

    [Theory]
    [InlineData("700+300-25",975)] [InlineData("=(2400-400)/2",1000)] [InlineData("1000*5%",50)]
    [InlineData("2.5E2+5",255)] [InlineData("+975",975)] [InlineData("-25+1000",975)] [InlineData("0",0)] [InlineData(" 975 ",975)]
    public void ActualTransferAndCalculatorUseEvaluatedMonthlyEntryAndKeepReusableFormula(string expression,int amount)
    {
        var(q,source,row)=Fixture(expression,amount);var before=JsonSerializer.Serialize(new{q,source,row});var other=q.Components[1];
        var proof=ExcelPayrollSetupRepository.InspectConstantRecurringInput(q,source,row,other);Assert.Equal((decimal)amount,proof.Value);Assert.Equal(expression,proof.Formula);
        var translation=ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence("D7/A7*B7",q,source,row);
        Assert.Equal("OTHER_ALLOW_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS",translation.Formula);
        var dependency=Assert.Single(translation.Dependencies);Assert.Equal("reviewed-constant-arithmetic",dependency.Kind);Assert.Equal(expression,dependency.Formula);
        Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,other).Status);
        var result=ExcelPayrollSetupRepository.CalculateRow(11,q,q.Components.Select((c,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+101).ToString(),false)).ToArray(),source.Rows[0],row,calculationSource:source);
        Assert.Equal((decimal)amount,result.Inputs["102"]);Assert.Equal(6000m+amount/2m,result.Preview.Gross);Assert.Equal(row.NetPay,result.Preview.Net);
        Assert.Equal(before,JsonSerializer.Serialize(new{q,source,row}));
    }

    [Theory]
    [InlineData("cell-reference")] [InlineData("e-column-reference")] [InlineData("external")] [InlineData("lazy-function")]
    [InlineData("missing-cache")] [InlineData("error")] [InlineData("calculation-error")] [InlineData("cache-mismatch")]
    [InlineData("text-cell")] [InlineData("protected-header")] [InlineData("financial-role")] [InlineData("unreviewed-role")]
    [InlineData("not-reviewed")] [InlineData("not-policy-reviewed")] [InlineData("period-adjustment")]
    [InlineData("output-alias")] [InlineData("duplicate-input")] [InlineData("duplicate-row")] [InlineData("duplicate-column")]
    [InlineData("missing-row")] [InlineData("negative")] [InlineData("precision")] [InlineData("overflow")]
    [InlineData("divide-zero")] [InlineData("overlength")] [InlineData("overdepth")]
    public void EntryArithmeticCannotFreezeDependenciesErrorsOrUnreviewedInputs(string problem)
    {
        var(q,source,row)=Fixture();var c=q.Components[1];var cell=source.Rows[0].Cells[3]!;
        switch(problem){
            case "cell-reference":cell.FormulaText="C7+1";break;
            case "e-column-reference":cell.FormulaText="E7+1";break;
            case "external":cell.FormulaText="[other.xlsx]Sheet1!A7";break;
            case "lazy-function":cell.FormulaText="IF(1,975,C7)";break;
            case "missing-cache":cell.MissingCachedValue=true;break;
            case "error":cell.Error="#VALUE!";break;
            case "calculation-error":cell.CalculationError="#REF!";break;
            case "cache-mismatch":cell.Value="976";break;
            case "text-cell":cell.DataType="Text";break;
            case "protected-header":source.Columns[3].SourceHeader="Account number";break;
            case "financial-role":source.Columns[3].Kind="earning";break;
            case "unreviewed-role":q.SourceColumnRoles[3]="ignore";break;
            case "not-reviewed":q.ReviewedFixedInputs=false;break;
            case "not-policy-reviewed":q.ReviewedPolicyScope=false;break;
            case "period-adjustment":c.IsPeriodAdjustment=true;break;
            case "output-alias":c.ExpectedColumnIndex=3;break;
            case "duplicate-input":q.Components[0].SourceColumnIndex=3;break;
            case "duplicate-row":source.Rows.Add(new(){SourceRow=7,Cells=source.Rows[0].Cells});break;
            case "duplicate-column":source.Columns.Add(new(){ColumnIndex=3,Kind="info",SourceHeader="Other allowance"});break;
            case "missing-row":source.Rows.Clear();break;
            case "negative":cell.FormulaText="-975";cell.Value="-975";break;
            case "precision":cell.FormulaText="1/3";cell.Value="0.3333333333333333333333333333";break;
            case "overflow":cell.FormulaText="1E40";break;
            case "divide-zero":cell.FormulaText="975/0";break;
            case "overlength":cell.FormulaText=new string('0',1001);break;
            case "overdepth":cell.FormulaText=new string('(',70)+"975"+new string(')',70);break;
        }
        var before=JsonSerializer.Serialize(new{q,source,row});Assert.ThrowsAny<InvalidOperationException>(()=>ExcelPayrollSetupRepository.InspectConstantRecurringInput(q,source,row,c));Assert.Equal(before,JsonSerializer.Serialize(new{q,source,row}));
    }

    [Fact]
    public void DailyRateEntryRetainsTheExistingRateAndPayableDaySemantics()
    {
        var(q,source,row)=Fixture();var c=q.Components[1];c.CalculationType="Daily Rate";c.ProRata=false;
        source.Rows[0].Cells[5]!.FormulaText="D7*B7";source.Rows[0].Cells[5]!.Value="14625";
        row.Earnings[1].Amount=14625;row.DeclaredGross=row.NetPay=20625;
        Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,c).Status);
        var result=ExcelPayrollSetupRepository.CalculateRow(11,q,q.Components.Select((v,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(v,(i+101).ToString(),false)).ToArray(),source.Rows[0],row,calculationSource:source);
        Assert.Equal(975m,result.Inputs["102"]);Assert.Equal(20625m,result.Preview.Net);
    }

    [Fact]
    public void EqualEntryValuesStillRetainDistinctCapturedFormulaEvidence()
    {
        var(q,source,row)=Fixture();var first=ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,q.Components[1]);
        source.Rows[0].Cells[3]!.FormulaText="1000-25";
        var second=ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,q.Components[1]);
        Assert.Equal("Exact supported translation",first.Status);Assert.Equal(first.Status,second.Status);Assert.NotEqual(first.SourceHash,second.SourceHash);
    }

    [Fact]
    public void ActualCalculatorReevaluatesAndRejectsChangedEntryCache()
    {
        var(q,source,row)=Fixture();source.Rows[0].Cells[3]!.Value="976";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.CalculateRow(11,q,q.Components.Select((c,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+101).ToString(),false)).ToArray(),source.Rows[0],row,calculationSource:source));
    }
}
