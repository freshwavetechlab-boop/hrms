using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PayrollLocationPayslipProjectionTests
{
    private static PayrollLocationComponentPart Part(string key,decimal earned,string scope="SourceFormulaPerPart")=>new(key,key,key=="A"?10:20,"T",30,key=="A"?17:13,key=="A"?17:13,scope,"",30000,earned);
    private static object Line(string code,string category,decimal amount,params PayrollLocationComponentPart[] parts)=>new{Id=code,Name=code,Category=category,monthlyAmount=30000,amount,locationParts=parts};
    private static PayRunEmployee Employee(params object[] lines)=>new(){EmployeeId=1,GrossPay=30000,StatutoryDeductions=3600,NetPay=26400,DetailsJson=JsonSerializer.Serialize(lines)};
    [Fact] public void Separate_slips_keep_actual_part_earnings_deductions_and_days_and_reconcile_once()
    {
        var employee=Employee(Line("BASIC","Earning",30000,Part("A",17000),Part("B",13000)),Line("PF","Deduction",3600,Part("A",2040),Part("B",1560)),
            Line("ER","Benefit",3900,Part("A",2210),Part("B",1690)));
        var before=JsonSerializer.Serialize(employee);var result=PayrollLocationPayslipProjection.Build(employee,"B");
        Assert.Equal(2,result.Count);Assert.Equal(17,result[0].PresentDays);Assert.Equal(14960,result[0].Net);Assert.Equal(11440,result[1].Net);
        Assert.Equal(employee.NetPay,result.Sum(r=>r.Net));Assert.Equal(before,JsonSerializer.Serialize(employee));
    }
    [Fact] public void Monthly_deduction_is_placed_once_on_the_explicit_reference_location()
    {
        var employee=Employee(Line("BASIC","Earning",30000,Part("A",17000),Part("B",13000)),Line("PF","Deduction",3600,Part("A",0,"Monthly"),Part("B",0,"Monthly")));
        var result=PayrollLocationPayslipProjection.Build(employee,"B");Assert.Equal(0,result[0].Deductions);Assert.Equal(3600,result[1].Deductions);
        Assert.Throws<InvalidOperationException>(()=>PayrollLocationPayslipProjection.Build(employee,null));
    }
    [Fact] public void Personal_monthly_amount_is_not_repeated_for_both_locations()
    {
        var employee=Employee(Line("BASIC","Earning",30000,Part("A",17000),Part("B",13000)),new{Id="PF",Name="PF",Category="Deduction",monthlyAmount=3600,amount=3600});
        Assert.Equal(3600,PayrollLocationPayslipProjection.Build(employee,"A").Sum(r=>r.Deductions));
    }
    [Fact] public void Invalid_saved_breakup_is_not_balanced_to_the_employee_net()
    {
        var employee=Employee(Line("BASIC","Earning",30000,Part("A",17001),Part("B",13000)));
        Assert.Throws<InvalidOperationException>(()=>PayrollLocationPayslipProjection.Build(employee,"A"));
    }
    [Fact] public void Ordinary_saved_result_keeps_the_existing_consolidated_document_path()
    {
        var employee=Employee(new{Id="BASIC",Name="BASIC",Category="Earning",amount=30000});
        Assert.Empty(PayrollLocationPayslipProjection.Build(employee,null));
    }
}
