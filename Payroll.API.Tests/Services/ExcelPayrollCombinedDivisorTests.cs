using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollCombinedDivisorTests
{
    private static (ExcelPayrollSetupRequest Request,ExcelPayslipCalculationSource Source,List<ExcelPayslipRow> Rows) Fixture()
    {
        var rows=new List<ExcelPayslipRow> { new(){Id="one",SourceRow=8},new(){Id="two",SourceRow=69} };
        var header=Enumerable.Range(0,12).Select(_=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell()).ToList();
        header[10]!.Value="31";
        var data=Enumerable.Range(0,12).Select(_=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell()).ToList();
        data[4]!.Value="12424";data[10]!.Value="19";data[11]!.FormulaText="E8/$K$6*K8";
        var source=new ExcelPayslipCalculationSource { HeaderRow=1,ColumnCount=12,
            Columns=Enumerable.Range(0,12).Select(index=>new ExcelPayslipCalculationColumn {ColumnIndex=index,Kind=index==11?"earning":"ignore"}).ToList(),
            Rows=[new(){SourceRow=6,Cells=header},new(){SourceRow=8,Cells=data}] };
        var request=new ExcelPayrollSetupRequest { FixedPayrollDays=31,PayrollDaysColumnIndex=-1,PayrollDaysCell="$K$6",PayableDaysColumnIndex=10,
            Components=[new(){Code="BASIC",Category="Earning",SourceColumnIndex=4,ExpectedColumnIndex=11,ProRata=true}] };
        return(request,source,rows);
    }

    [Fact]
    public void RetainedAbsoluteConstantAfterNormalizedHeaderIsSupportedWithoutChangingSource()
    {
        var(request,source,rows)=Fixture();var before=JsonSerializer.Serialize(source);
        ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows);
        Assert.Equal(before,JsonSerializer.Serialize(source));Assert.Null(source.MonthDaysCell);
        Assert.Equal(31,request.FixedPayrollDays); // A reviewed salary divisor, not an inferred calendar-month length.
    }

    [Fact]
    public void AnUnselectedSavedEmployeeRowCannotBeReusedAsAConstant()
    {
        var(request,source,rows)=Fixture();rows.Add(new(){Id="unselected",SourceRow=6});
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
    }

    [Fact]
    public void BodyConstantRequiresFullSavedBatchEvidence()
    {
        var(request,source,rows)=Fixture();
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]]));
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],[]));
    }

    [Theory]
    [InlineData("employeeName")][InlineData("employeeCode")][InlineData("email")]
    [InlineData("earning")][InlineData("deduction")][InlineData("employer")]
    [InlineData("netPay")][InlineData("grossTotal")][InlineData("deductionTotal")]
    public void ProtectedColumnRolesCannotBeBypassedByAReviewOverride(string role)
    {
        var(request,source,rows)=Fixture();source.Columns[10].Kind=role;request.SourceColumnRoles[10]="ignore";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
        source.Columns[10].Kind="ignore";request.SourceColumnRoles[10]=role;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
    }

    [Theory]
    [InlineData("missing")][InlineData("formula")][InlineData("error")][InlineData("calculationError")]
    [InlineData("missingCache")][InlineData("different")][InlineData("fractional")]
    public void MissingCalculatedOrErroneousConstantNeverBecomesRecurringInput(string invalid)
    {
        var(request,source,rows)=Fixture();var cell=source.Rows[0].Cells[10]!;
        switch(invalid)
        {
            case "missing":source.Rows[0].Cells[10]=null;break;
            case "formula":cell.FormulaText="30+1";break;
            case "error":cell.Error="#VALUE!";break;
            case "calculationError":cell.CalculationError="Invalid";break;
            case "missingCache":cell.MissingCachedValue=true;break;
            case "different":cell.Value="30";break;
            case "fractional":cell.Value="30.5";break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
    }

    [Theory]
    [InlineData("E8/K6*K8")][InlineData("E8/$K6*K8")][InlineData("E8/K$6*K8")]
    [InlineData("E8/$K$6*L8")][InlineData("E8/$K$43*K8")]
    public void BodyConstantRequiresExactAbsoluteReferenceAndReviewedPayableCell(string formula)
    {
        var(request,source,rows)=Fixture();source.Rows[1].Cells[11]!.FormulaText=formula;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
    }
}
