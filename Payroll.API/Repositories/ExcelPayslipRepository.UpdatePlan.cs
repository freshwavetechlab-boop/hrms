using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private sealed record UpdateFieldBinding(ExcelPayslipUpdateField Field, string Kind, int Position, string? MasterKey);
    private sealed class UpdateContext
    {
        public string BatchJson = "", SourceJson = "", SendJson = "";
        public ExcelPayslipBatch Raw = new(), Projection = new();
        public ExcelPayslipCalculationSource? Source;
        public Dictionary<string, UpdateFieldBinding> Fields = [];
        public Dictionary<int, Dictionary<string, string>> Masters = [];
        public Dictionary<int, string> MasterBasis = [];
        public Dictionary<string, int> MasterByRow = [];
        public bool AppliedSalaryLinks, ProcessedPayroll, ImmutableSource;
        public List<string> Blockers = [];
    }
    private sealed class UpdateInput
    {
        public string RowId { get; set; } = "";
        public string FieldId { get; set; } = "";
        public string Value { get; set; } = "";
        public int EmployeeId { get; set; }
        public string? MasterKey { get; set; }
        public bool WriteSource { get; set; }
    }
    private sealed class UpdatePlan
    {
        public List<ExcelPayslipUpdateChange> Changes = [];
        public List<UpdateInput> Inputs = [];
        public List<string> Blockers = [];
        public string BatchJson = "", SourceJson = "";
        public Dictionary<int, Dictionary<string, string>> MasterChanges = [];
    }

    private async Task<UpdateContext> LoadUpdateContextAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id)
    {
        var stored = await db.QuerySingleOrDefaultAsync<(string BatchJson, string? SourceJson, string SendJson)>(@"
SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson,CAST(b.send_state_json AS CHAR) SendJson
FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@Id", new { ClientId = clientId, Id = id }, tx);
        if (string.IsNullOrWhiteSpace(stored.BatchJson)) throw new InvalidOperationException("The saved payslip batch was not found for this client.");
        var context = new UpdateContext { BatchJson = stored.BatchJson, SourceJson = stored.SourceJson ?? "", SendJson = stored.SendJson };
        context.Raw = JsonSerializer.Deserialize<ExcelPayslipBatch>(stored.BatchJson, JsonOptions)!;
        if (context.Raw.ClientId != clientId || context.Raw.Id != id) throw new InvalidOperationException("The saved batch scope is invalid.");
        context.Source = stored.SourceJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(stored.SourceJson, JsonOptions);
        context.Projection = await ReadCorrectionBatchAsync(db, clientId, id, tx) ?? throw new InvalidOperationException("The saved payslip batch is unavailable.");
        context.MasterByRow = context.Projection.Rows.Where(ExcelPayslipPresentation.Linked)
            .ToDictionary(row => row.Id, row => row.MasterDetails!.EmployeeId, StringComparer.Ordinal);
        var ids = context.MasterByRow.Values.Distinct().ToArray();
        context.Masters = await EmployeeRepository.ReadReviewedExcelMasterFieldsBatchAsync(db, tx, ids, clientId);
        if (ids.Length > 0)
            context.MasterBasis = (await db.QueryAsync<(int Id, string Basis)>(@"SELECT Id,SHA2(JSON_OBJECT('code',EmployeeCode,'client',ClientId,
'template',SalaryStructureId,'active',IsActive,'portal',PortalAccess),256) Basis FROM employees WHERE ClientId=@ClientId AND Id IN @Ids",
                new { ClientId = clientId, Ids = ids }, tx)).ToDictionary(row => row.Id, row => row.Basis);
        if (await UpdateTableExistsAsync(db, tx, "excel_payroll_setup_imports"))
            context.AppliedSalaryLinks = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM excel_payroll_setup_imports i
JOIN excel_payslip_batches b ON b.client_id=i.client_id AND b.id=i.batch_id WHERE i.client_id=@ClientId
AND i.status='Applied' AND JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month'))=@Month)", new { ClientId = clientId, context.Raw.Month }, tx);
        if (await UpdateTableExistsAsync(db, tx, "payruns"))
            context.ProcessedPayroll = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Month
AND Status NOT IN ('Failed','Cancelled','Rejected'))", new { ClientId = clientId, context.Raw.Month }, tx);
        try { await RequireMutableConsolidationSnapshotAsync(db, tx, clientId, id); }
        catch (InvalidOperationException) { context.ImmutableSource = true; }
        var state = JsonSerializer.Deserialize<ExcelPayslipSendState>(context.SendJson, JsonOptions) ?? new();
        if (await UpdateTableExistsAsync(db, tx, "notification_queue"))
        {
            var statuses = BuildDeliveryStatuses(context.Raw, state, await ReadDeliveryQueueAsync(db, clientId, state, tx));
            if (statuses.Items.Any(row => row.RecipientDeliveries.Any(delivery => delivery.Status is "Queued" or "Unknown" or "Deferred")))
                context.Blockers.Add("Payslip delivery is active or its state cannot be verified. Finish or review it before changing this saved batch.");
            if (await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM notification_queue WHERE ClientId=@ClientId
AND ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest') AND (ResourceId=@Id OR ResourceId LIKE @Prefix)
AND Status IN ('Pending','Queued','Retry','Processing','Sending','ExcelPending','ExcelRetry','ExcelCorrectedPending','ExcelCorrectedRetry'))",
                new { ClientId = clientId, Id = id, Prefix = id + ":%" }, tx))
                context.Blockers.Add("Payslip emails are queued or processing. Let that job finish before applying an Excel update.");
        }
        else if (state.Deliveries.Count > 0)
            context.Blockers.Add("Historical delivery status cannot be verified. No update can be applied until it is reviewed.");
        context.Fields = BuildUpdateFields(context);
        return context;
    }

    private static Task<bool> UpdateTableExistsAsync(MySqlConnection db, MySqlTransaction tx, string table) => db.ExecuteScalarAsync<bool>(
        "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Table)", new { Table = table }, tx);

    private static readonly Dictionary<string, string> UpdateMasterLabels = new()
    {
        ["employeeName"]="Employee name",["email"]="Email",["mobile"]="Mobile",["uan"]="UAN",["esic"]="ESIC",["pan"]="PAN",["aadhaar"]="Aadhaar",
        ["bankAccount"]="Bank account",["ifsc"]="IFSC",["bankName"]="Bank name",["dateOfBirth"]="Date of birth",["dateOfJoining"]="Date of joining",
        ["gender"]="Gender",["designation"]="Designation",["department"]="Department",["grade"]="Grade",["workLocation"]="Work location",
        ["annualCtc"]="Annual CTC",["address"]="Address",["correspondenceAddress"]="Correspondence address",["permanentAddress"]="Permanent address",
        ["city"]="City",["district"]="District",["state"]="State"
    };

    internal static string? ReviewedUpdateMasterKey(string kind, string label)
    {
        if (kind == "employeeName") return "employeeName";
        if (kind == "email") return "email";
        if (kind != "info") return null;
        return ExcelPayslipPresentation.Key(label) switch
        {
            "email" or "emailid" or "workemail" or "emailaddress" => "email",
            "uan" or "uanno" or "uannumber" or "universalaccountnumber" or "universalaccountnumberuan" => "uan",
            "esic" or "esicno" or "esino" or "esicnumber" or "esinumber" => "esic",
            "pan" or "panno" or "pannumber" => "pan",
            "aadhaar" or "aadhar" or "aadhaarno" or "aadharno" or "aadhaarnumber" or "aadharnumber" => "aadhaar",
            "mobile" or "mobileno" or "mobilenumber" or "phone" or "phoneno" or "phonenumber" or "contactno" or "contactnumber" => "mobile",
            "accountno" or "accountnumber" or "bankaccountno" or "bankaccountnumber" or "acno" => "bankAccount",
            "ifsc" or "ifsccode" => "ifsc", "bank" or "bankname" => "bankName",
            "dateofbirth" or "birthdate" or "dob" => "dateOfBirth", "dateofjoining" or "joiningdate" or "doj" => "dateOfJoining",
            "gender" => "gender", "designation" or "designationmanpowercategory" or "manpowercategory" => "designation",
            "department" => "department", "grade" => "grade", "location" or "worklocation" or "placeofposting" => "workLocation",
            "annualctc" or "annualcosttocompany" => "annualCtc",
            "address" => "address", "correspondenceaddress" => "correspondenceAddress", "permanentaddress" => "permanentAddress",
            "city" => "city", "district" => "district", "state" => "state", _ => null
        };
    }

    private static Dictionary<string, UpdateFieldBinding> BuildUpdateFields(UpdateContext context)
    {
        var fields = new Dictionary<string, UpdateFieldBinding>(StringComparer.Ordinal);
        void Add(string id, string label, string kind, int position, int? sourceColumn)
        {
            var master = ReviewedUpdateMasterKey(kind, label);
            var numeric = master == "annualCtc" || kind is "earning" or "deduction" or "employer" or "netPay" or "grossTotal" or "deductionTotal"
                || kind == "info" && master is null && (Regex.IsMatch(label, @"\b(days?|hours?|rate|wages?|salary|amount|bonus|ctc)\b", RegexOptions.IgnoreCase)
                    || new[] { "payabledays", "paybledays", "presentdays", "workingdays", "monthdays", "totaldays", "payableunits", "extradutydays", "hourlyrate", "dailyrate" }.Contains(ExcelPayslipPresentation.Key(label), StringComparer.Ordinal));
            var formulaOutput = numeric && sourceColumn.HasValue && context.Raw.Rows.All(row =>
                !string.IsNullOrWhiteSpace(context.Source?.Rows.SingleOrDefault(r => r.SourceRow == row.SourceRow)?.Cells.ElementAtOrDefault(sourceColumn.Value)?.FormulaText));
            var masterImpactBlocked = context.AppliedSalaryLinks && master is not null && master is not ("employeeName" or "email" or "mobile" or "bankAccount" or "bankName" or "ifsc" or "address" or "correspondenceAddress" or "permanentAddress");
            var financialBlocked = masterImpactBlocked || numeric && (context.AppliedSalaryLinks || context.ProcessedPayroll);
            var readOnly = kind == "employeeCode" || formulaOutput || financialBlocked || context.ImmutableSource && master is null;
            var reason = kind == "employeeCode" ? "Employee code identifies an existing employee and cannot be changed by this update."
                : formulaOutput ? "Saved formula output: update its plain inputs; this formula will not be overwritten."
                : financialBlocked ? "This month has payroll or reviewed salary links. Use Configure payroll for financial changes."
                : context.ImmutableSource && master is null ? "Retained consolidated source data is immutable. Master fields can be updated separately."
                : master is not null && (context.AppliedSalaryLinks || context.ImmutableSource) ? "Updates Master only; the reviewed original salary source is retained."
                : master is null && kind == "info" ? "Saved source information only. Attendance totals do not create daily attendance or a Master salary basis." : "";
            var type = master is "dateOfBirth" or "dateOfJoining" ? "date" : master == "workLocation" ? "reference" : numeric || master == "annualCtc" ? "number" : "text";
            fields[id] = new(new() { Id = id, Label = label, Type = type, Destination = master is null ? "Batch only"
                : context.AppliedSalaryLinks || context.ImmutableSource ? "Master only" : "Batch + Master", Editable = !readOnly,
                Reason = reason, SourceColumnIndex = sourceColumn, MasterField = master }, kind, position, master);
        }
        if (context.Source is { } source)
        {
            var positions = new Dictionary<string, int>();
            foreach (var column in source.Columns.Where(c => c.Kind != "ignore"))
            {
                var position = positions.GetValueOrDefault(column.Kind); positions[column.Kind] = position + 1;
                Add("source:" + column.ColumnIndex, column.Label, column.Kind, position, column.ColumnIndex);
            }
        }
        else
        {
            Add("batch:employeeCode", "Employee code", "employeeCode", 0, null);
            Add("batch:employeeName", "Employee name", "employeeName", 0, null);
            Add("batch:email", "Email", "email", 0, null);
            var row = context.Raw.Rows.FirstOrDefault();
            if (row is not null)
            {
                foreach (var (kind, items) in new[] { ("info", row.Information.Select(v => v.Label)), ("earning", row.Earnings.Select(v => v.Label)),
                    ("deduction", row.Deductions.Select(v => v.Label)), ("employer", row.EmployerContributions.Select(v => v.Label)) })
                    foreach (var (label, index) in items.Select((label, index) => (label, index))) Add($"batch:{kind}:{index}", label, kind, index, null);
                Add("batch:netPay", "Net pay", "netPay", 0, null);
                if (row.DeclaredGross.HasValue) Add("batch:grossTotal", "Gross total", "grossTotal", 0, null);
                if (row.DeclaredDeductions.HasValue) Add("batch:deductionTotal", "Deduction total", "deductionTotal", 0, null);
            }
        }
        foreach (var key in context.Masters.Values.SelectMany(fields => fields.Keys).Where(key => UpdateMasterLabels.ContainsKey(key) || key.StartsWith("salaryInput:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            if (fields.Values.Any(field => field.MasterKey == key && field.Field.Editable)) continue;
            var numeric = key == "annualCtc" || key.StartsWith("salaryInput:", StringComparison.Ordinal);
            var impacted = context.AppliedSalaryLinks && key is not ("employeeName" or "email" or "mobile" or "bankAccount" or "bankName" or "ifsc" or "address" or "correspondenceAddress" or "permanentAddress");
            var editable = !impacted && (!numeric || !context.AppliedSalaryLinks && !context.ProcessedPayroll);
            var id = "master:" + key;
            var label = UpdateMasterLabels.TryGetValue(key, out var knownLabel) ? knownLabel : "configured input " + key[12..];
            fields[id] = new(new() { Id = id, Label = "Master " + label,
                Type = numeric ? "number" : key is "dateOfBirth" or "dateOfJoining" ? "date" : key == "workLocation" ? "reference" : "text",
                Destination = "Master only", Editable = editable, MasterField = key,
                Reason = !editable ? "This field can affect the existing reviewed payroll calculation. Use Configure payroll for its financial impact review."
                    : numeric ? "Explicit existing Master basis only. Monthly earned pay is not converted into annual CTC or base wages."
                    : "Existing matched Master only; no employee will be created." }, "master", 0, key);
        }
        return fields;
    }

    private static string UpdateFieldValue(UpdateContext context, ExcelPayslipRow row, UpdateFieldBinding binding)
    {
        if (binding.MasterKey is { } key && context.MasterByRow.TryGetValue(row.Id, out var employeeId)
            && context.Masters.TryGetValue(employeeId, out var master) && master.TryGetValue(key, out var value)) return value;
        return binding.Kind switch
        {
            "employeeCode" => row.EmployeeCode, "employeeName" => row.EmployeeName, "email" => row.Email,
            "info" => row.Information.ElementAtOrDefault(binding.Position)?.Value ?? "",
            "earning" => row.Earnings.ElementAtOrDefault(binding.Position)?.Amount.ToString(CultureInfo.InvariantCulture) ?? "",
            "deduction" => row.Deductions.ElementAtOrDefault(binding.Position)?.Amount.ToString(CultureInfo.InvariantCulture) ?? "",
            "employer" => row.EmployerContributions.ElementAtOrDefault(binding.Position)?.Amount.ToString(CultureInfo.InvariantCulture) ?? "",
            "netPay" => row.NetPay.ToString(CultureInfo.InvariantCulture), "grossTotal" => row.DeclaredGross?.ToString(CultureInfo.InvariantCulture) ?? "",
            "deductionTotal" => row.DeclaredDeductions?.ToString(CultureInfo.InvariantCulture) ?? "", _ => ""
        };
    }

    private static string UpdateCode(UpdateContext context, ExcelPayslipRow row) => context.Projection.Rows.Single(r => r.Id == row.Id).MasterDetails is { Linked: true } details
        && context.MasterByRow.ContainsKey(row.Id) && !string.IsNullOrWhiteSpace(details.EmployeeCode) ? details.EmployeeCode : row.EmployeeCode;
    private static string UpdateName(UpdateContext context, ExcelPayslipRow row) => context.Projection.Rows.Single(r => r.Id == row.Id).MasterDetails is { Linked: true } details
        && context.MasterByRow.ContainsKey(row.Id) && !string.IsNullOrWhiteSpace(details.EmployeeName) ? details.EmployeeName : row.EmployeeName;
}
