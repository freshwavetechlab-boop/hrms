using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class ExcelPayrollSetupTests
{
    [Fact] public void SavedProposalsRemainInactiveWithoutChangingTheFinancialRequest()
    {
        var input=new JsonObject { ["leave"]=new JsonObject { ["status"]="Approved",["active"]=true,["quotaApproved"]=true,["proposedAnnualPaidLeave"]=10 },["holiday"]=new JsonObject { ["notes"]="Awaiting calendar" } };
        var saved=ExcelPayrollSetupRepository.InactiveDefaultsDraft(input);
        Assert.False(saved["leave"]!["active"]!.GetValue<bool>());
        Assert.False(saved["leave"]!["quotaApproved"]!.GetValue<bool>());
        Assert.Equal(10,saved["leave"]!["proposedAnnualPaidLeave"]!.GetValue<int>());
        Assert.Equal("Draft",saved["shift"]!["status"]!.GetValue<string>());
        Assert.True(input["leave"]!["active"]!.GetValue<bool>());
    }

    [Fact] public void BackdatedInitialSalaryRetainsDistinctEmptySetupDatesInOrder()
    {
        var from = new DateTime(2026,9,1);
        var history = new[] {
            new EmployeeRepository.ExcelPayrollSalaryHistory { Id=1,EffectiveFrom=new(2026,9,20),Status="Active" },
            new EmployeeRepository.ExcelPayrollSalaryHistory { Id=2,EffectiveFrom=new(2026,9,10),Status="Historical" },
            new EmployeeRepository.ExcelPayrollSalaryHistory { Id=3,EffectiveFrom=new(2026,9,20),Status="Historical" },
            new EmployeeRepository.ExcelPayrollSalaryHistory { Id=4,EffectiveFrom=from,AnnualCtc=120000,SalaryStructureId="existing" },
        };
        Assert.Equal(new[] { new DateTime(2026,9,10),new DateTime(2026,9,20) },EmployeeRepository.ExcelSalaryHistoryMarkers(history,from));
        Assert.Equal("Active",history[0].Status); // planning is read-only
    }

    [Theory]
    [InlineData("template",0,"{}")]
    [InlineData("",120000,"{}")]
    [InlineData("",0,"{\"101\":0}")]
    [InlineData("",0,"[]")]
    [InlineData("",0,"malformed")]
    public void BackdatedInitialSetupNeverReplacesAConfiguredFutureSalary(string template,decimal annual,string salary)
    {
        var history = new[] { new EmployeeRepository.ExcelPayrollSalaryHistory { EffectiveFrom=new(2026,9,20),SalaryStructureId=template,AnnualCtc=annual,SalaryJson=salary } };
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers(history,new(2026,9,1)));
    }

    private static ExcelPayrollSetupRequest Request() => new()
    {
        BatchId = new string('a',32), RowIds = ["one","two"], TemplateName = "Reviewed salary import", EffectiveFrom = "2026-09-01",
        PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, WorkWeek = "Sunday off",
        Components = [
            new() { Key = "base", Code = "IMPORT_BASIC", Name = "Basic", SourceColumnIndex = 2, ExpectedColumnIndex = 3, ProRata = true, Priority = 10 },
            new() { Key = "pf", Code = "IMPORT_PF", Name = "Employee PF", Category = "Deduction", ComponentRole = "Statutory Deduction", StatutoryType = "PF Employee", CalculationType = "Formula", Formula = "IMPORT_BASIC_EARNED*12%", Value = "", ExpectedColumnIndex = 4, ProRata = false, Taxable = false, Priority = 20 },
            new() { Key = "employer", Code = "IMPORT_ER", Name = "Employer PF", Category = "Benefit", ComponentRole = "Employer Contribution", StatutoryType = "PF Employer", CalculationType = "Formula", Formula = "IMPORT_BASIC_EARNED*12%", Value = "", ExpectedColumnIndex = 5, ProRata = false, Taxable = false, Priority = 30 }
        ]
    };
    private static List<ExcelPayrollSetupRepository.ResolvedComponent> Components(ExcelPayrollSetupRequest r) => r.Components.Select((c,i) => new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),true)).ToList();
    private static ExcelPayslipCalculationSourceRow Source(params string[] values) => new() { SourceRow = 3, Cells = values.Select(v => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = v }).ToList() };
    private static ExcelPayslipRow Row(string id = "one", decimal net = 8800) => new() { Id = id, EmployeeCode = "SOURCE001", EmployeeName = "Example Employee", NetPay = net };

    [Fact] public void MixedEmployeeDenominatorsUseTheSameNativeArithmetic()
    {
        var r = Request();
        var full = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("12","12","28450","28450","3414","3414"),Row(net:25036));
        var partial = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","12","28450","11380","1365.60","1365.60"),Row(net:10014.40m));
        Assert.Equal(28450m,full.Preview.Gross); Assert.Equal(11380m,partial.Preview.Gross);
        Assert.Equal(25036m,full.Preview.Net); Assert.Equal(10014.40m,partial.Preview.Net);
        Assert.Equal(341400m,full.Preview.AnnualCtc); Assert.Equal(full.Preview.AnnualCtc,partial.Preview.AnnualCtc);
        Assert.DoesNotContain(partial.Issues,i => i.Severity == "Error");
    }

    [Fact] public void ZeroAttendanceDoesNotBecomePermanentZeroSalaryOrPfExemption()
    {
        var r = Request(); r.Components[1].ExemptWhenSourceZero = true;
        var result = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","0","26210","0","0","0"),Row(net:0));
        Assert.Equal(0,result.Preview.Net); Assert.Equal(26210,result.Inputs["1"]);
        Assert.DoesNotContain("2",result.Inputs.Keys); Assert.DoesNotContain("3",result.Inputs.Keys);
        Assert.Equal(314520,result.Preview.AnnualCtc);
    }

    [Fact] public void CalculatedSourceDeductionsNeverBecomeStoredSalaryAmounts()
    {
        var r = Request();
        var result = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","15","20000","10000","1200","1200"),Row());
        Assert.Single(result.Inputs); Assert.Equal(20000,result.Inputs["1"]);
        Assert.Equal(8800,result.Preview.Net); Assert.Equal(1200,result.Preview.Benefits);
    }

    [Fact] public void AnExplicitReviewedPfExemptionRetainsZeroForPayableEmployee()
    {
        var r = Request(); r.Components[1].ExemptWhenSourceZero = true;
        var result = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","30","20000","20000","0","2400"),Row(net:20000));
        Assert.Equal(0,result.Inputs["2"]); Assert.Equal(20000,result.Preview.Net);
        Assert.Contains(result.Issues,i => i.Code == "IMPORT_PF" && i.Message.Contains("exemption"));
    }

    [Fact] public void PeriodRecoveryIsNotAPermanentSalaryOverrideAndEmployerCostNeverReducesNet()
    {
        var r = Request(); r.Components.Add(new() { Key="recovery",Code="IMPORT_RECOVERY",Name="Recovery",Category="Deduction",ComponentRole="Recovery",SourceColumnIndex=6,ExpectedColumnIndex=6,IsPeriodAdjustment=true,ProRata=false,Taxable=false });
        var result = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","15","20000","10000","1200","1200","500"),Row(net:8300));
        Assert.Equal(8300,result.Preview.Net); Assert.Equal(1200,result.Preview.Benefits);
        Assert.Single(result.Adjustments); Assert.Single(result.Inputs);
    }

    [Fact] public void RowFormulaVariantsAreExplicitAndDoNotModifySharedDefinitions()
    {
        var r = Request(); r.Components[1].RowFormulas["two"] = "0";
        var one = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","30","10000","10000","1200","1200"),Row());
        var two = ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),Source("30","30","10000","10000","0","1200"),Row("two",10000));
        Assert.Equal(8800,one.Preview.Net); Assert.Equal(10000,two.Preview.Net);
        Assert.Equal("IMPORT_BASIC_EARNED*12%",r.Components[1].Formula);
    }

    [Fact] public void BlankAndMissingCachedInputsAreRejectedUnlessMonetaryBlankWasReviewed()
    {
        var source=Source("", "0");
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ReadNumber(source,0));
        Assert.Equal(0,ExcelPayrollSetupRepository.ReadNumber(source,0,true));
        source.Cells[0]!.FormulaText="A1*2";
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ReadNumber(source,0,true));
        source.Cells[0]!.FormulaText=null; source.Cells[0]!.MissingCachedValue=true;
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ReadNumber(source,0,true));
    }

    [Fact] public void InformationMonthlyBlankNeedsItsOwnExplicitChoiceAndCannotMaskMissingDays()
    {
        var r=Request(); r.Components[0].BlankInputAsZero=true;
        r.Components.Add(new() { Key="other",Code="IMPORT_OTHER",Name="Other base",SourceColumnIndex=6,ExpectedColumnIndex=7 });
        var values=Source("30","30","","0","0","0","1000","1000");
        var result=ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),values,Row(net:1000));
        Assert.Equal(0,result.Inputs["1"]); Assert.Equal(1000,result.Inputs["4"]);
        values.Cells[0]!.Value="";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.CalculateRow(33,r,Components(r),values,Row()));
    }

    [Fact] public void NewEmployeeUsesKnownSourceFieldsBeforeEditableFallbacksWithoutEnablingPortal()
    {
        var r=Request(); r.DefaultDepartment="Fallback"; r.DefaultDesignation="Fallback role";
        var employee=ExcelPayrollSetupRepository.NewEmployee(33,Row(),r,new()
        {
            ["DESIGNATION"]="Driver II",["DEPARTMENT"]="Operations",["GRADE"]="G2",["BANKACCOUNTNUMBER"]="1234567890",
            ["IFSCCODE"]="EXAM0000001",["DOJ"]="01/09/2026",["UANNO"]="123456789012"
        });
        Assert.Equal("Driver II",employee.Designation); Assert.Equal("Operations",employee.Department); Assert.Equal("G2",employee.Grade);
        Assert.Equal("2026-09-01",employee.DateOfJoining); Assert.Equal("1234567890",employee.PaymentDetails.BankAccountNo);
        Assert.Equal("EXAM0000001",employee.PaymentDetails.IfscCode); Assert.False(employee.PortalAccess);
        Assert.Equal("123456789012",employee.PersonalDetails.UanNumber);
    }

    [Fact] public void SourceNameAndReviewedMasterIdCanReuseDifferentEmployeeCodesSafely()
    {
        var existing = new ExcelPayrollSetupRepository.Identity { Id=2700,ClientId=33,EmployeeCode="NHDC00001",Name="Example Employee",IsActive=true,WorkLocation="Noida" };
        var issues = new List<ExcelPayrollSetupIssue>();
        var match = ExcelPayrollSetupRepository.MatchIdentity(Row(),new(),[existing],new() { RowId="one",MatchedEmployeeId=2700 },issues);
        Assert.Equal(2700,match!.Id); Assert.Empty(issues);
        existing.Name="Different Person";
        ExcelPayrollSetupRepository.MatchIdentity(Row(),new(),[existing],new() { RowId="one",MatchedEmployeeId=2700 },issues);
        Assert.Contains(issues,i=>i.Severity=="Error");
    }

    [Fact] public void StrongIdentifierConflictsCannotBeOverriddenByManualEmployeeChoice()
    {
        var selected=new ExcelPayrollSetupRepository.Identity { Id=1,Name="Example Employee",EmployeeCode="MASTER1",Pan="ABCDE1234F" };
        var other=new ExcelPayrollSetupRepository.Identity { Id=2,Name="Other",EmployeeCode="MASTER2",Pan="BCDEF1234G" };
        var issues=new List<ExcelPayrollSetupIssue>();
        ExcelPayrollSetupRepository.MatchIdentity(Row(),new() { ["PAN"]="BCDEF1234G" },[selected,other],new() { MatchedEmployeeId=1 },issues);
        Assert.Contains(issues,i=>i.Severity=="Error" && i.Message.Contains("conflict"));
    }

    [Fact] public void DraftDefaultsRoundTripWithoutCreatingActiveLeaveOrInventedDates()
    {
        var r=Request(); r.EditableDefaults=new() { ["leave"] = new JsonObject { ["status"]="NeedsReview",["entitlement"]=0,["active"]=false },["holidays"] = new JsonObject { ["year"]=2026,["dates"]=new JsonArray() } };
        ExcelPayrollSetupRepository.ValidateRequest(r);
        var saved=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(r))!;
        Assert.Equal("NeedsReview",saved.EditableDefaults["leave"]!["status"]!.ToString());
        Assert.Equal("false",saved.EditableDefaults["leave"]!["active"]!.ToJsonString());
    }

    [Fact] public void ImportPermissionsDoNotFollowPayslipSendPermission()
    {
        var user=new AuthUser { Id=1,IsActive=true,ClientId=33,Permissions=["payslips.send","payroll.run"] };
        Assert.False(ExcelPayrollSetupEndpoints.CanAccess(user,33));
        user.Permissions=["settings.manage","employees.manage","payroll.run"];
        Assert.True(ExcelPayrollSetupEndpoints.CanAccess(user,33)); Assert.False(ExcelPayrollSetupEndpoints.CanAccess(user,34));
        user.MustChangePassword=true; Assert.False(ExcelPayrollSetupEndpoints.CanAccess(user,33));
    }

    [Fact] public void SelectionReceiptKeyIsOrderIndependentButSubsetSpecific()
    {
        Assert.Equal(ExcelPayrollSetupRepository.SelectionHash(["a","b"]),ExcelPayrollSetupRepository.SelectionHash(["b","a"]));
        Assert.NotEqual(ExcelPayrollSetupRepository.SelectionHash(["a","b"]),ExcelPayrollSetupRepository.SelectionHash(["a"]));
    }

    [Fact] public void ExplicitImportCohortDoesNotExpandWhenAnotherEmployeeIsEdited()
    {
        var policies=new[]
        {
            new EmployeeRepository.ExcelPayrollPolicyCohort { ClientId=33,GroupId=77,EmployeeIdsJson="[2700,2715,2723,2746,2759]" },
            new EmployeeRepository.ExcelPayrollPolicyCohort { ClientId=34,GroupId=88,EmployeeIdsJson="[2700]" }
        };
        var selected=new Employee { Id=2700,ClientId=33,IsActive=true };
        Assert.Equal([77],EmployeeRepository.SelectedExcelPayrollPolicyGroups(selected,policies));
        Assert.Empty(EmployeeRepository.SelectedExcelPayrollPolicyGroups(new() { Id=2800,ClientId=33,IsActive=true },policies));
        selected.IsActive=false; Assert.Empty(EmployeeRepository.SelectedExcelPayrollPolicyGroups(selected,policies));
        selected.IsActive=true; selected.ClientId=35;
        Assert.Empty(EmployeeRepository.SelectedExcelPayrollPolicyGroups(selected,policies));
    }

    [Fact] public void UnclassifiedEmployerFeesUseEditableBenefitRoleWithoutFalsePfReportingTag()
    {
        var r=Request(); r.Components.Add(new() { Key="admin",Code="IMPORT_ADMIN",Name="PF admin cost",Category="Benefit",ComponentRole="Employer Benefit",StatutoryType="None",CalculationType="Formula",Value="",Formula="IMPORT_BASIC_EARNED*.5%",ProRata=false,Taxable=false });
        ExcelPayrollSetupRepository.ValidateRequest(r);
        r.Components[^1].ComponentRole="Employer Contribution";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateRequest(r));
        r.Components[^1].StatutoryType="PF Employer";
        ExcelPayrollSetupRepository.ValidateRequest(r);
    }
}
