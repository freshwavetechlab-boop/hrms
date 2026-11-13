using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipProfilePresentationTests
{
    [Fact]
    public void Legacy_profile_link_keeps_source_email_identifiers_and_period_location_when_master_is_blank_or_moved()
    {
        var row=new ExcelPayslipRow { Id="source",SourceRow=3,EmployeeCode="TEST-1",EmployeeName="Source name",Email="source@example.test",NetPay=1234,
            Information=[new(){Label="UAN",Value="001234567890"},new(){Label="ESIC",Value="0012345678"},new(){Label="Work Location",Value="Salary-period site"}],
            Employment=new(7,"TEST-1","Active",null,false,false),
            MasterDetails=new(7,"Analyst"){Linked=true,ProfileOnly=true,Fields=new(){["uan"]="",["esic"]="",["worklocation"]="Current Master site"}} };
        var current=ExcelPayslipPresentation.Current(row);
        Assert.Equal(row.EmployeeCode,current.EmployeeCode);Assert.Equal(row.EmployeeName,current.EmployeeName);Assert.Equal(row.Email,current.Email);
        Assert.Equal("001234567890",current.Information.Single(f=>f.Label=="UAN").Value);
        Assert.Equal("0012345678",current.Information.Single(f=>f.Label=="ESIC").Value);
        Assert.Equal("Salary-period site",current.Information.Single(f=>f.Label=="Work Location").Value);
    }

    [Fact]
    public void Profile_only_updates_identity_fields_but_never_money_even_if_salary_projection_is_present()
    {
        var row = new ExcelPayslipRow
        {
            Id = "source-1", SourceRow = 7, EmployeeCode = "TEST-1", EmployeeName = "Old name", Email = "old@example.test",
            NetPay = 1234.5678m, DeclaredGross = 1500.1234m, DeclaredDeductions = 265.5556m,
            Earnings = [new() { Label = "Basic", Amount = 1500.1234m }], Deductions = [new() { Label = "PF", Amount = 265.5556m }],
            EmployerContributions = [new() { Label = "Employer PF", Amount = 200.0123m }],
            Information = [new() { Label = "UAN", Value = "" },new() { Label = "Contact Number", Value = "old" },new() { Label = "CTC", Value = "original monthly base" },new() { Label = "ESI Number", Value = "" },new() { Label = "Basic (E)", Value = "original base" }],
            Employment = new(7,"TEST-1","Active",null,false,false),
            MasterDetails = new(7,"Analyst") { Linked = true, ProfileOnly = true, EmployeeCode = "TEST-1", EmployeeName = "Current name", Email = "current@example.test",
                Fields = new() { ["uan"] = "001234567890",["phone"] = "9000000001",["esic"] = "0012345678",["annualctc"] = "900000",["monthlyctc"] = "75000",["source:basice"] = "99999" },
                Salary = new("Updated","",[new() { Label = "Other",Amount = 99999 }],[],[],99999,0,99999) }
        };
        var original = JsonSerializer.Serialize(row);
        var current = ExcelPayslipPresentation.Current(row);
        Assert.Equal("Current name",current.EmployeeName); Assert.Equal("current@example.test",current.Email);
        Assert.Equal("001234567890",current.Information.Single(f=>f.Label=="UAN").Value);
        Assert.Equal("9000000001",current.Information.Single(f=>f.Label=="Contact Number").Value);
        Assert.Equal("0012345678",current.Information.Single(f=>f.Label=="ESI Number").Value);
        Assert.Equal("original monthly base",current.Information.Single(f=>f.Label=="CTC").Value);
        Assert.Equal("original base",current.Information.Single(f=>f.Label=="Basic (E)").Value);
        Assert.Equal(row.NetPay,current.NetPay); Assert.Equal(row.DeclaredGross,current.DeclaredGross); Assert.Equal(row.DeclaredDeductions,current.DeclaredDeductions);
        Assert.Same(row.Earnings,current.Earnings); Assert.Same(row.Deductions,current.Deductions); Assert.Same(row.EmployerContributions,current.EmployerContributions);
        Assert.Equal(original,JsonSerializer.Serialize(row));
    }
}
