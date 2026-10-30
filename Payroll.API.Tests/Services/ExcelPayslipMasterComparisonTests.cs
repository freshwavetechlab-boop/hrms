using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMasterComparisonTests
{
    [Fact]
    public void UnrequestedPrivateFieldsAreNotReturnedAndLegacyNullFieldsRemainMissing()
    {
        var employee=new Employee {ClientId=33,EmployeeCode="SYN001",WorkEmail=null!,PersonalDetails=null!,PaymentDetails=null!};
        var comparison=ExcelPayslipRepository.MasterComparison(33,"SYN001",employee,"");
        Assert.All(comparison.Fields.Values,value=>Assert.NotNull(value));Assert.Null(employee.PersonalDetails);
        var selected=ExcelPayslipRepository.SelectMasterComparisonFields(comparison,"Designation,SalaryJson");
        Assert.Equal(new[]{"EmployeeCode","EmployeeName","Email","Designation"},selected.Fields.Keys);
        Assert.DoesNotContain("BankAccountNo",selected.Fields.Keys);Assert.DoesNotContain("UAN",selected.Fields.Keys);
    }
    [Fact]
    public void FieldProjectionPreservesLiteralValuesWithoutReturningRawJsonOrAccountAccess()
    {
        var employee=new Employee {Id=7,ClientId=33,EmployeeCode="NHDC00001",FirstName="Synthetic",LastName="Worker",WorkEmail="old@example.invalid",AnnualCtc=360000,
            PersonalJson="{\"privateExtra\":\"not returned\"}",PaymentJson="{\"privateExtra\":\"not returned\"}",PortalAccess=true,
            PersonalDetails=new() {UanNumber="001234567890",Mobile="000123",State="State"},PaymentDetails=new() {BankAccountNo="0000123456789012",IfscCode="TEST0000001"}};
        var result=ExcelPayslipRepository.MasterComparison(33,"nhdc00001",employee,"Synthetic location");
        Assert.Equal("Matched",result.Status);Assert.Equal("0000123456789012",result.Fields["BankAccountNo"]);
        Assert.Equal("001234567890",result.Fields["UAN"]);Assert.Equal("360000",result.Fields["AnnualCTC"]);
        Assert.Equal("old@example.invalid",result.Fields["Email"]);Assert.Equal("Synthetic Worker",result.Fields["EmployeeName"]);
        Assert.DoesNotContain("PersonalJson",result.Fields.Keys);Assert.DoesNotContain("PaymentJson",result.Fields.Keys);Assert.DoesNotContain("PortalAccess",result.Fields.Keys);
        Assert.DoesNotContain("Id",result.Fields.Keys);Assert.DoesNotContain("SalaryJson",result.Fields.Keys);
        Assert.Equal("0000123456789012",employee.PaymentDetails.BankAccountNo);
    }

    [Theory]
    [InlineData(11,"NHDC00001")]
    [InlineData(33,"DIFFERENT")]
    public void ChangedCodeOrClientCannotReadAnUnrelatedMaster(int clientId,string code) =>
        Assert.Throws<InvalidOperationException>(()=>ExcelPayslipRepository.MasterComparison(clientId,code,new Employee {ClientId=33,EmployeeCode="NHDC00001"},""));

    [Theory]
    [InlineData("employees.view",true)]
    [InlineData("employees.manage",true)]
    [InlineData("security.manage",true)]
    [InlineData("payroll.run",false)]
    [InlineData("settings.manage",false)]
    public void PrivateComparisonRequiresEmployeeReadPermission(string permission,bool expected)
    {
        var user=new AuthUser {Id=1,IsActive=true,Permissions=[permission]};
        Assert.Equal(expected,ExcelPayslipEndpoints.CanReadMasterComparison(user));
        user.IsActive=false;Assert.False(ExcelPayslipEndpoints.CanReadMasterComparison(user));user.IsActive=true;user.MustChangePassword=true;Assert.False(ExcelPayslipEndpoints.CanReadMasterComparison(user));
    }
}
