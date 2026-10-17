using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private static async Task InsertMastersAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan)
    {
        foreach (var c in plan.Components.Where(c => c.IsNew))
        {
            var d = c.Definition;
            await db.ExecuteAsync(@"INSERT INTO salarycomponents
(Id,Code,ComponentType,ComponentRole,StatutoryType,Category,Name,PayType,CalculationType,ValueText,Formula,BaseComponent,Taxable,Ctc,ProRata,Fbp,RestrictFbp,Epf,Esi,Recurring,Scheduled,InvestmentType,CorrectionOf,Active,Priority)
VALUES(@Id,@Code,'Excel reviewed',@ComponentRole,@StatutoryType,@Category,@Name,@PayType,@CalculationType,@Value,@Formula,@BaseComponent,@Taxable,@Ctc,@ProRata,FALSE,FALSE,'Never',FALSE,TRUE,FALSE,'','',TRUE,@Priority)",
                new { c.Id, Code = d.Code.ToUpperInvariant(), d.ComponentRole, d.StatutoryType, d.Category, d.Name,
                    PayType = d.IsPeriodAdjustment ? "Variable Pay" : "Fixed Pay", CalculationType = d.IsPeriodAdjustment ? "Manual / Variable" : d.CalculationType,
                    d.Value, d.Formula, d.BaseComponent, d.Taxable, Ctc = d.Category != "Deduction", ProRata = !d.IsPeriodAdjustment && d.ProRata, d.Priority }, tx);
        }
        var variants = plan.Rows.Where(r => r.TemplateId.Length > 0).GroupBy(r => r.TemplateId).ToList();
        for (var v = 0; v < variants.Count; v++)
        {
            var variant = variants[v];
            var name = request.TemplateName.Trim() + (variants.Count > 1 ? $" / {v + 1}" : "");
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=@ClientId AND LOWER(Name)=LOWER(@Name)", new { ClientId = clientId, Name = name }, tx) != 0)
                throw new InvalidOperationException("A salary template variant name is already in use. Review a new template name.");
            await db.ExecuteAsync(@"INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active)
VALUES(@Id,@ClientId,@ClientRef,@Name,0,TRUE)", new { Id = variant.Key, ClientId = clientId, ClientRef = clientId.ToString(CultureInfo.InvariantCulture), Name = name }, tx);
            var index = 0;
            foreach (var c in variant.First().Components)
            {
                var d = c.Definition;
                await db.ExecuteAsync(@"INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
VALUES(@TemplateId,@ComponentId,@Value,@CalculationType,@Formula,@BaseComponent,@ProRata,'',@Order)",
                    new { TemplateId = variant.Key, ComponentId = c.Id, d.Value, CalculationType = d.IsPeriodAdjustment ? "Manual / Variable" : d.CalculationType,
                        d.Formula, d.BaseComponent, ProRata = (!d.IsPeriodAdjustment && d.ProRata).ToString().ToLowerInvariant(), Order = index++ }, tx);
            }
        }
    }

    private static async Task<int> InsertAttendanceAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan, string importId)
    {
        var ids = plan.Rows.Select(r => r.Employee.Id).ToArray();
        var error = await WeeklyOffAttendanceService.ValidateMonthlyChangesAsync(db, tx, clientId, ids, plan.Batch.Month);
        if (error is not null) throw new InvalidOperationException(error);
        var groupName = request.TemplateName.Trim() + " / " + plan.Batch.Month + " / Excel " + importId[..8];
        if (groupName.Length > 180) groupName = groupName[..180];
        var groupId = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO attendance_groups
(client_id,policy_batch_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,shift_id,attendance_mode,allowed_shift_ids_json)
VALUES(@ClientId,@BatchId,@Name,0,'All','All',@WorkWeek,@StartDay,@EndDay,@ReportDay,TRUE,NULL,'Monthly',JSON_ARRAY()); SELECT LAST_INSERT_ID();",
            new { ClientId = clientId, BatchId = Guid.Parse(importId).ToString(), Name = groupName, WorkWeek = request.WorkWeek.Trim(), StartDay = request.AttendanceCycleStartDay,
                EndDay = request.AttendanceCycleEndDay, ReportDay = request.PayrollReportGenerationDay }, tx);
        foreach (var row in plan.Rows)
        {
            await db.ExecuteAsync("INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(@GroupId,@EmployeeId)", new { GroupId = groupId, EmployeeId = row.Employee.Id }, tx);
            await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES(@ClientId,@EmployeeId,@Month,@Days,@Present,@Payable,@Lop,'Monthly',@Remarks)
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),lop_days=VALUES(lop_days),source_type='Monthly',remarks=VALUES(remarks)",
                new { ClientId = clientId, EmployeeId = row.Employee.Id, Month = plan.Batch.Month, Days = row.Preview.PayrollDays,
                    Present = row.Preview.PresentDays, Payable = row.Preview.PayableDays, Lop = row.Preview.PayrollDays-row.Preview.PayableDays,
                    Remarks = $"Reviewed Excel source {request.BatchId}, row {row.SourceRow}, import {importId}." }, tx);
        }
        return groupId;
    }

    private static async Task InsertAdjustmentsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan, string importId)
    {
        foreach (var row in plan.Rows)
        foreach (var adjustment in row.Adjustments.Where(a => a.Amount != 0))
        {
            var d = adjustment.Component.Definition;
            // ComponentId in the legacy adjustment table is INT; codes carry BIGINT master identity safely.
            await db.ExecuteAsync(@"INSERT INTO payrolladjustments
(ClientId,EmployeeId,EmployeeName,EmployeeCode,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status)
VALUES(@ClientId,@EmployeeId,@EmployeeName,@EmployeeCode,0,@Code,@Name,@Category,@Amount,@Month,'Regular','EXCEL_SETUP',@Notes,@Taxable,'Approved')",
                new { ClientId = clientId, EmployeeId = row.Employee.Id, EmployeeName = (row.Employee.FirstName+" "+row.Employee.LastName).Trim(), row.Employee.EmployeeCode,
                    Code = d.Code.ToUpperInvariant(), d.Name, d.Category, adjustment.Amount, Month = plan.Batch.Month, Notes = $"Reviewed salary Excel {request.BatchId}; import {importId}; source row {row.SourceRow}", d.Taxable }, tx);
        }
    }
}
