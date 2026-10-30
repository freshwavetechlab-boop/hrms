using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private static Task<bool> HasNullablePresenceSchemaAsync(MySqlConnection db, MySqlTransaction? tx) =>
        SourcePayableAttendance.SchemaReadyAsync(db, tx);

    internal static string? PayableOnlyWriteStateError(bool schemaReady, bool hasMonthlySnapshot, bool hasDailyAttendance, bool hasDailyPolicy, bool hasSplitPf)
    {
        if (!schemaReady) return "Payable-only attendance requires the reviewed nullable-presence schema for monthly attendance and payroll employees. Complete that additive migration before importing; no schema is changed by preview or Apply.";
        if (hasMonthlySnapshot) return "An existing native monthly attendance snapshot is retained. Payable-only import cannot overwrite known or previously reviewed attendance; retain native attendance and review the source difference separately.";
        if (hasDailyAttendance) return "Existing dated attendance is retained. Payable-only source totals cannot replace its presence evidence; retain and review the native attendance workflow.";
        try { SourcePayableAttendance.EnsureIndependent([], hasDailyPolicy, hasSplitPf); }
        catch (InvalidOperationException error) { return error.Message; }
        return null;
    }

    private async Task ValidatePayableOnlyAttendanceStateAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan)
    {
        if (!request.ImportPayableOnlyAttendance) return;
        void Error(string message, string? rowId = null)
        {
            if (!plan.Preview.Issues.Any(issue => issue.Severity == "Error" && issue.RowId == rowId && issue.Message == message))
                plan.Preview.Issues.Add(new("Error", message, rowId, "Payable-only attendance"));
        }
        if (PayableOnlyRequestError(request) is string requestError) { Error(requestError); return; }
        var ready = PayableOnlyRolloutEnabled(configuration) && await HasNullablePresenceSchemaAsync(db, tx);
        if (PayableOnlyRolloutError(configuration, ready) is string rolloutError) { Error(rolloutError); return; }
        var cycle = PayRunRepository.NativeAttendanceCycle(plan.Batch.Month, request.AttendanceCycleStartDay, request.AttendanceCycleEndDay);
        var start = DateTime.ParseExact(cycle.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(cycle.End, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ids = plan.Rows.Where(row => !row.KeepExisting && row.Employee.Id > 0).Select(row => row.Employee.Id).Distinct().ToArray();
        var monthly = ids.Length == 0 ? new HashSet<int>() : (await db.QueryAsync<int>("SELECT employee_id FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids", new { ClientId=clientId, Month=plan.Batch.Month, Ids=ids }, tx)).ToHashSet();
        var daily = ids.Length == 0 ? new HashSet<int>() : (await db.QueryAsync<int>("SELECT DISTINCT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End", new { ClientId=clientId, Ids=ids, Start=start, End=end }, tx)).ToHashSet();
        var attendance = await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId);
        var hasDailyPolicy = attendance.WeeklyOffRuleVersions.Any(version => version.EffectiveFrom.Date <= end.Date);
        var pfVersions = await PfPolicyRepository.ReadVersionsAsync(db, tx, clientId);
        foreach (var row in plan.Rows.Where(row => !row.KeepExisting))
        {
            bool split;
            try { split = PfPolicyCalculator.ApplicableVersions(pfVersions, row.TemplateId, DateOnly.FromDateTime(start), DateOnly.FromDateTime(end)).Count > 1; }
            catch (InvalidOperationException error) { Error(error.Message, row.RowId); continue; }
            if (PayableOnlyWriteStateError(true, monthly.Contains(row.Employee.Id), daily.Contains(row.Employee.Id), hasDailyPolicy, split) is string stateError)
                Error(stateError, row.RowId);
        }
    }

    internal sealed record ReviewedAttendanceWrite(decimal? PresentDays, string SourceType, string Remarks);
    internal static ReviewedAttendanceWrite AttendanceWriteForRow(Plan plan, PlannedRow row, ExcelPayrollSetupRequest request, int clientId, string importId)
    {
        if (!request.ImportPayableOnlyAttendance)
            return new(row.Preview.PresentDays, "Monthly", $"Reviewed Excel source {request.BatchId}, row {row.SourceRow}, import {importId}.");
        if (PayableOnlyRequestError(request) is string requestError) throw new InvalidOperationException(requestError);
        var sourceHash = Hash(JsonSerializer.Serialize(new { plan.Batch, plan.Source }, Json));
        var proofs = plan.Preview.Transfer?.AttendanceProofs.Where(proof => proof.RowId == row.RowId).ToArray() ?? [];
        var currentProof = ProveAttendanceInput(plan, row, request, clientId, sourceHash);
        if (proofs.Length != 1 || proofs[0].Status != PayableOnlyProofStatus || proofs[0].PresentDays is not null
            || plan.Batch.ClientId != clientId || row.Employee.ClientId != clientId || request.BatchId != plan.Batch.Id
            || currentProof.Status != PayableOnlyProofStatus || proofs[0].InputHash != currentProof.InputHash
            || proofs[0].SourceHash != sourceHash || proofs[0].SourceRow != row.SourceRow
            || proofs[0].PayrollDays != row.Preview.PayrollDays || proofs[0].PayableDays != row.Preview.PayableDays)
            throw new InvalidOperationException("Payable-only attendance needs the exact reviewed saved-source proof for this row, client, month and day inputs. Refresh preview; no present value is inferred.");
        return new(null, SourcePayableAttendance.SourceType, SourcePayableAttendance.Encode(new(1, clientId, row.Employee.Id,
            plan.Batch.Month, request.BatchId, row.RowId, importId, sourceHash, row.Preview.PayrollDays, row.Preview.PayableDays)));
    }

    private static async Task InsertMastersAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan)
    {
        var used = plan.Rows.SelectMany(r=>r.Components).Select(c=>c.Id).ToHashSet();
        foreach (var c in plan.Components.Where(c => c.IsNew && used.Contains(c.Id)))
        {
            var d = c.Definition;
            await db.ExecuteAsync(@"INSERT INTO salarycomponents
(Id,Code,ComponentType,ComponentRole,StatutoryType,Category,Name,PayType,CalculationType,ValueText,Formula,BaseComponent,Taxable,Ctc,ProRata,Fbp,RestrictFbp,Epf,Esi,Recurring,Scheduled,InvestmentType,CorrectionOf,Active,Priority)
VALUES(@Id,@Code,'Excel reviewed',@ComponentRole,@StatutoryType,@Category,@Name,@PayType,@CalculationType,@Value,@Formula,@BaseComponent,@Taxable,@Ctc,@ProRata,FALSE,FALSE,'Never',FALSE,TRUE,FALSE,'','',TRUE,@Priority)",
                new { c.Id, Code = d.Code.ToUpperInvariant(), d.ComponentRole, d.StatutoryType, d.Category, d.Name,
                    PayType = ResolvedPayType(d), CalculationType = d.IsPeriodAdjustment ? "Manual / Variable" : d.CalculationType,
                    d.Value, d.Formula, d.BaseComponent, d.Taxable, Ctc = d.Category != "Deduction", ProRata = !d.IsPeriodAdjustment && d.ProRata, d.Priority }, tx);
        }
        var allVariants = plan.Rows.Where(r => r.TemplateId.Length > 0).GroupBy(r => r.TemplateId).ToList();
        var variants = allVariants.Where(g => !plan.ReusedTemplateIds.Contains(g.Key)).ToList();
        for (var v = 0; v < variants.Count; v++)
        {
            var variant = variants[v];
            var name = plan.Preview.Transfer?.Templates.SingleOrDefault(t => t.TemplateId == variant.Key && t.Action == "Create reviewed variant")?.Name
                ?? request.TemplateName.Trim() + (allVariants.Count > 1 ? $" / {allVariants.FindIndex(g => g.Key == variant.Key) + 1}" : "");
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

    private async Task<int> InsertAttendanceAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan, string importId, ExcelPayrollSetupResult result)
    {
        await ValidatePayableOnlyAttendanceStateAsync(db, tx, clientId, request, plan);
        if (!plan.Preview.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Preview.Issues.Where(issue=>issue.Severity=="Error").Select(issue=>issue.Message)));
        var ids = plan.Rows.Select(r => r.Employee.Id).ToArray();
        var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, clientId, plan.Batch.Month,
            plan.Rows.Select(row => (row.Employee.Id, request.ImportPayableOnlyAttendance ? (decimal?)null : row.Preview.PresentDays, row.Preview.PayableDays)));
        if (employmentError is not null) throw new InvalidOperationException(employmentError);
        var error = await WeeklyOffAttendanceService.ValidateMonthlyChangesAsync(db, tx, clientId, ids, plan.Batch.Month);
        if (error is not null) throw new InvalidOperationException(error);
        var groupName = request.TemplateName.Trim() + " / " + plan.Batch.Month + " / Excel " + importId[..8];
        if (groupName.Length > 180) groupName = groupName[..180];
        var memberships = (await db.QueryAsync<(int EmployeeId, int GroupId, bool IsSourceCohort)>(@"SELECT m.employee_id EmployeeId,g.id GroupId,
EXISTS(SELECT 1 FROM excel_payroll_setup_imports receipt WHERE receipt.client_id=g.client_id AND receipt.id=REPLACE(g.policy_batch_id,'-','') AND receipt.status='Applied') IsSourceCohort
FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND m.employee_id IN @Ids", new { ClientId = clientId, Ids = ids }, tx)).ToLookup(row => row.EmployeeId);
        if (ids.Any(id => memberships[id].Count() > 1) || !request.PreserveExistingAttendancePolicies && memberships.Any())
            throw new InvalidOperationException("Review existing attendance memberships before applying this setup.");
        var groupId = 0;
        if (ids.Any(id => !memberships[id].Any())) groupId = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO attendance_groups
(client_id,policy_batch_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,shift_id,attendance_mode,allowed_shift_ids_json)
VALUES(@ClientId,@BatchId,@Name,0,'All','All',@WorkWeek,@StartDay,@EndDay,@ReportDay,TRUE,NULL,'Monthly',JSON_ARRAY()); SELECT LAST_INSERT_ID();",
            new { ClientId = clientId, BatchId = Guid.Parse(importId).ToString(), Name = groupName, WorkWeek = request.WorkWeek.Trim(), StartDay = request.AttendanceCycleStartDay,
                EndDay = request.AttendanceCycleEndDay, ReportDay = request.PayrollReportGenerationDay }, tx);
        foreach (var row in plan.Rows)
        {
            var assigned = memberships[row.Employee.Id].FirstOrDefault().GroupId;
            if (assigned == 0)
            {
                assigned = groupId;
                await db.ExecuteAsync("INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(@GroupId,@EmployeeId)", new { GroupId = assigned, EmployeeId = row.Employee.Id }, tx);
            }
            result.AttendanceAssignments.Add(new(row.Employee.Id, assigned, assigned == groupId || memberships[row.Employee.Id].FirstOrDefault().IsSourceCohort));
            var reviewedAttendance = AttendanceWriteForRow(plan, row, request, clientId, importId);
            var insert = @"INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES(@ClientId,@EmployeeId,@Month,@Days,@Present,@Payable,@Lop,@SourceType,@Remarks)";
            // An unknown-presence seed is insert-only; even a concurrent existing attendance row is never replaced.
            if (!request.ImportPayableOnlyAttendance) insert += @"
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),lop_days=VALUES(lop_days),source_type=VALUES(source_type),remarks=VALUES(remarks)";
            await db.ExecuteAsync(insert,
                new { ClientId = clientId, EmployeeId = row.Employee.Id, Month = plan.Batch.Month, Days = row.Preview.PayrollDays,
                    Present = reviewedAttendance.PresentDays, Payable = row.Preview.PayableDays, Lop = row.Preview.PayrollDays-row.Preview.PayableDays,
                    reviewedAttendance.SourceType, reviewedAttendance.Remarks }, tx);
        }
        return result.AttendanceAssignments.First().GroupId;
    }

    private static async Task InsertAdjustmentsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan, string importId, ExcelPayrollSetupResult result)
    {
        foreach (var row in plan.Rows)
        foreach (var adjustment in row.Adjustments.Where(a => a.Amount != 0))
        {
            var d = adjustment.Component.Definition;
            var reused=await ReadSourceAdjustmentReuseAsync(db,tx,clientId,plan,row,adjustment.Component,adjustment.Amount,true);
            var reviewed=plan.AdjustmentReuses.SingleOrDefault(a=>a.RowId==row.RowId&&a.ComponentCode.Equals(d.Code,StringComparison.OrdinalIgnoreCase));
            if(reused!=reviewed)throw new InvalidOperationException("Existing source adjustment changed after preview; refresh before applying configuration.");
            if(reused is not null){result.ReusedAdjustments.Add(reused);continue;}
            // ComponentId in the legacy adjustment table is INT; codes carry BIGINT master identity safely.
            await db.ExecuteAsync(@"INSERT INTO payrolladjustments
(ClientId,EmployeeId,EmployeeName,EmployeeCode,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status)
VALUES(@ClientId,@EmployeeId,@EmployeeName,@EmployeeCode,0,@Code,@Name,@Category,@Amount,@Month,'Regular','EXCEL_SETUP',@Notes,@Taxable,'Approved')",
                new { ClientId = clientId, EmployeeId = row.Employee.Id, EmployeeName = (row.Employee.FirstName+" "+row.Employee.LastName).Trim(), row.Employee.EmployeeCode,
                    Code = d.Code.ToUpperInvariant(), d.Name, d.Category, adjustment.Amount, Month = plan.Batch.Month, Notes = $"Reviewed salary Excel {request.BatchId}; import {importId}; source row {row.SourceRow}", d.Taxable }, tx);
        }
    }
}
