using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // A read-only current-Master projection. It uses the regular payroll body,
    // current assignment/inputs and the reviewed source days, without changing
    // attendance, effective-dated payroll history, payruns or tax snapshots.
    internal async Task RefreshExcelPayslipSalaryAsync(MySqlConnection db,MySqlTransaction tx,ExcelPayslipBatch batch,string setupJson,
        IReadOnlyList<(ExcelPayslipRow Row,ExcelPayslipMasterLink Link,Employee Employee)> items)
    {
        var adjustments=await GetApplicableAdjustmentsAsync(db,tx,batch.ClientId,batch.Month,"Regular",[]);
        foreach(var (row,link,master) in items)
        {
            try
            {
                var employee=CurrentMasterSalaryEmployee(master);
                var personal=master.PersonalDetails;
                employee.WorkState=await db.QuerySingleOrDefaultAsync<string>("SELECT State FROM worklocations WHERE ClientId=@ClientId AND Id=@WorkLocationId",new{master.ClientId,master.WorkLocationId},tx)??personal.State;
                row.MasterDetails!.Salary=await CalculateCurrentExcelPayslipSalaryAsync(db,tx,batch,setupJson,employee,link,adjustments);
            }
            catch(Exception error)when(error is InvalidOperationException or FormatException or OverflowException)
            {
                row.MasterDetails!.Salary=ExcelPayslipRepository.SyncReview(error.Message);
            }
        }
    }

    private static PayRunSourceEmployee CurrentMasterSalaryEmployee(Employee master)
    {
        var employee=JsonSerializer.Deserialize<PayRunSourceEmployee>(JsonSerializer.Serialize(master))!;var p=master.PersonalDetails;
        employee.HasPersonalDetails=true;employee.PersonalState=p.State;employee.PanNumber=p.PanNumber;employee.UanNumber=p.UanNumber;
        employee.EsicNumber=p.EsicNumber;employee.EsicEmployee=p.EsicEmployee;employee.PtLwfWorkmenComp=p.PtLwfWorkmenComp;
        employee.Tds=p.Tds;employee.Recovery=p.Recovery;employee.BankAccountNo=master.PaymentDetails.BankAccountNo;employee.IfscCode=master.PaymentDetails.IfscCode;
        return employee;
    }

    internal static PayRunEmployee CalculateMasterSalaryArithmetic(Employee master,string setupJson,ExcelPayslipBatch batch,ExcelPayslipMasterLink link)
        =>BuildCurrentMasterSalary(CurrentMasterSalaryEmployee(master),setupJson,batch,link,[]);

    private static PayRunEmployee BuildCurrentMasterSalary(PayRunSourceEmployee employee,string setupJson,ExcelPayslipBatch batch,ExcelPayslipMasterLink link,
        List<PayrollAdjustment> adjustments,decimal tds=-1,string tdsCode="TDS")
    {
        if(link.PresentDays is null)
            employee.PayableOnlyAttendance=new(link.ReviewedExtraDutyUnits?2:1,batch.ClientId,employee.Id,batch.Month,link.SourceBatchId,link.RowId,
                link.ImportId,link.SourceHash,link.PayrollDays,link.PayableDays,ReviewedExtraDutyUnits:link.ReviewedExtraDutyUnits);
        return BuildEmployee(0,employee,setupJson,batch.Month,link.PayrollDays,link.PresentDays,link.PayableDays,adjustments,0,0,tds,false,tdsCode);
    }

    private async Task<ExcelPayslipCurrentSalary> CalculateCurrentExcelPayslipSalaryAsync(MySqlConnection db,MySqlTransaction tx,
        ExcelPayslipBatch batch,string setupJson,PayRunSourceEmployee employee,ExcelPayslipMasterLink link,List<PayrollAdjustment> adjustments)
    {
        if(link.PayrollDays is <1 or >31||link.PayableDays<0||link.PayableDays>(link.ReviewedExtraDutyUnits?999.99m:link.PayrollDays)
            ||link.PresentDays is <0||link.PresentDays>link.PayrollDays)
            throw new InvalidOperationException("Review the linked source salary divisor and payable units before recalculating this salary.");
        var setup=ReadPayrollSetup(setupJson);
        var structure=setup.Structures.SingleOrDefault(s=>s.Active&&s.Id==employee.SalaryStructureId
            &&s.ClientId.Split([';',',','|']).Any(c=>c.Split(':')[0].Trim()==batch.ClientId.ToString(CultureInfo.InvariantCulture)));
        if(structure is null)throw new InvalidOperationException("Assign an active salary template for this client in Employee Master.");
        var issues=InspectTemplate(setup.Components,structure,employee.SalaryComponents).Where(i=>i.Severity=="Error").ToArray();
        if(issues.Length>0)throw new InvalidOperationException(string.Join(" ",issues.Select(i=>i.Message)));
        if(link.PresentDays is null)
            employee.PayableOnlyAttendance=new(link.ReviewedExtraDutyUnits?2:1,batch.ClientId,employee.Id,batch.Month,link.SourceBatchId,link.RowId,
                link.ImportId,link.SourceHash,link.PayrollDays,link.PayableDays,ReviewedExtraDutyUnits:link.ReviewedExtraDutyUnits);
        await LoadLocationPartPeriodsAsync(db,tx,[employee],batch.Month);
        if(employee.LocationParts is not null)throw new InvalidOperationException("This employee has reviewed location allocations. Review their location salary inputs before generating a current Master payslip.");
        await LoadPfPolicyPeriodsAsync(db,tx,[employee],batch.Month,setupJson);
        var selected=adjustments.Where(a=>a.EmployeeId==employee.Id).ToList();
        var result=BuildCurrentMasterSalary(employee,setupJson,batch,link,selected);
        var tax=await CalculateMonthlyTdsAsync(db,tx,employee,result,batch.Month,persistSnapshot:false);
        if(tax.Calculated)result=BuildCurrentMasterSalary(employee,setupJson,batch,link,selected,tax.Amount,tax.ComponentCode);
        using var details=JsonDocument.Parse(result.DetailsJson);
        List<ExcelPayslipAmount> Lines(Func<string,bool> category)=>details.RootElement.EnumerateArray().Where(l=>category(Text(l,"Category")))
            .Select(l=>new ExcelPayslipAmount{Label=Text(l,"Name"),Amount=NumberFrom(Text(l,"amount"))}).ToList();
        return new("Updated","Calculated from current Employee Master and the reviewed source salary days; original Excel amounts are retained separately.",
            Lines(IsPayableEarningCategory),Lines(IsDeductionCategory),Lines(c=>c=="Benefit"),result.GrossPay+result.OneTimeEarnings,
            result.StatutoryDeductions+result.OneTimeDeductions,result.NetPay);
    }
}
