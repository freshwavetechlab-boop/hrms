using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollHelperProrationProofTests
{
    [Theory]
    [InlineData("=M7*L7",true)]
    [InlineData("=(I7/J7)*L7",true)]
    [InlineData("=I7/(J7*L7)",false)]
    [InlineData("=I7*L7/J7",false)]
    [InlineData("=ROUND(I7/J7,0)*L7",false)]
    public void Helper_grouping_uses_the_exact_engine_AST_without_reassociation_or_cache_equality(string formula,bool exact)
    {
        var component=new ExcelPayrollSetupComponent {Key="basic",Code="BASIC",Name="Basic",SourceColumnIndex=8,
            ExpectedColumnIndex=13,CalculationType="Fixed Amount",Value="0",ProRata=true};
        var request=new ExcelPayrollSetupRequest {PayrollDaysColumnIndex=9,PayableDaysColumnIndex=11,ReviewedFixedInputs=true,Components=[component]};
        var source=new ExcelPayslipCalculationSource {ColumnCount=14,Columns=Enumerable.Range(0,14).Select(i=>new ExcelPayslipCalculationColumn {
            ColumnIndex=i,Kind=i==13?"earning":"info",SourceHeader="Synthetic input "+i}).ToList(),
            Rows=[new(){SourceRow=7,Cells=Enumerable.Range(0,14).Select(_=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value="0"}).ToList()}]};
        var cells=source.Rows[0].Cells;cells[8]!.Value="13000";cells[9]!.Value="26";cells[11]!.Value="26";
        cells[12]=new(){Value="500",FormulaText="=I7/J7"};cells[13]=new(){Value="13000",FormulaText=formula};
        var result=ExcelPayrollSetupRepository.InspectTransferFormula(request,source,new(){Id="source-7",SourceRow=7},component);
        Assert.Equal(exact?"Exact supported translation":"Blocked",result.Status);
    }
}
