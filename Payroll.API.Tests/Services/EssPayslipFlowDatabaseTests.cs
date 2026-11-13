using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[CollectionDefinition("EssPayslipFlow",DisableParallelization=true)]
public sealed class EssPayslipFlowCollection { }

[Collection("EssPayslipFlow")]
public sealed class EssPayslipFlowDatabaseTests
{
    private sealed class IsolatedFactAttribute:FactAttribute
    {
        public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_ESS_PAYSLIP_E2E_TEST_DB") is null)Skip="Explicit isolated ESS to payslip database required.";}
    }
    [IsolatedFact]
    public async Task One_ESS_save_updates_the_same_employee_payslip_and_locks_without_rewriting_salary_or_source()
    {
        var f=new EssPayslipFlowFixture(Environment.GetEnvironmentVariable("FREVO_ESS_PAYSLIP_E2E_TEST_DB")!);
        await f.ResetAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","EssProfile"));
        var before=(await f.Slips.GetAsync(907,EssPayslipFlowFixture.BatchId))!;
        Assert.Empty(ExcelPayslipPresentation.Current(before.Rows.Single()).Information.Single(i=>i.Label=="UAN").Value);
        var saved=await f.Ess.SaveProfileAsync(107,907,new SaveEssProfileRequest{FirstName="Verified",LastName="Employee",UanNumber="001234567890",EsicNumber="0012345678",Gender="Female",WorkEmail="verified@example.test",Mobile="9000000999",PanNumber="ABCDE1234F",BankName="Synthetic Bank",BankAccountNo="0012345678901",IfscCode="ABCD0000123",PaymentMode="Bank Transfer"},"synthetic-profile@example.test");
        Assert.Null(saved.Error);Assert.False(saved.Profile!.CanEdit);
        var after=(await f.Slips.GetAsync(907,EssPayslipFlowFixture.BatchId))!;
        var current=ExcelPayslipPresentation.Current(after.Rows.Single());
        Assert.Equal("001234567890",current.Information.Single(i=>i.Label=="UAN").Value);Assert.Equal("Verified Employee",current.EmployeeName);Assert.Equal("verified@example.test",current.Email);
        Assert.Equal("9000000999",current.Information.Single(i=>i.Label=="Mobile No").Value);
        Assert.Equal(JsonSerializer.Serialize(before.Rows.Single().Earnings),JsonSerializer.Serialize(current.Earnings));
        Assert.Equal(JsonSerializer.Serialize(before.Rows.Single().Deductions),JsonSerializer.Serialize(current.Deductions));
        Assert.Equal(JsonSerializer.Serialize(before.Rows.Single().EmployerContributions),JsonSerializer.Serialize(current.EmployerContributions));Assert.Equal(before.Rows.Single().NetPay,current.NetPay);
        var state=JsonSerializer.SerializeToElement(await f.StateAsync());Assert.True(state.GetProperty("salaryUnchanged").GetBoolean());Assert.True(state.GetProperty("batchJsonUnchanged").GetBoolean());Assert.True(state.GetProperty("sourceJsonUnchanged").GetBoolean());Assert.Equal(1,state.GetProperty("saves").GetInt32());
    }
}
