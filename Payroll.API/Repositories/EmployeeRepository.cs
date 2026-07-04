using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class EmployeeRepository(IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<Guid, EmployeeImportJobStatus> ImportJobs = new();
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task InitializeAsync() { await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db); }
    public async Task<IEnumerable<Employee>> GetAsync() { await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db); var rows = (await db.QueryAsync<Employee>("SELECT * FROM employees ORDER BY FirstName, LastName")).ToList(); await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows); return rows; }
    public async Task<int> SaveAsync(Employee employee, string changedBy = "System", string? infotypeCode = null, string? changeReason = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        var wasNew = employee.Id == 0;
        var before = employee.Id > 0 ? await LoadEmployeeAsync(db, employee.Id) : null;
        var actionType = wasNew ? "Hire" : "Master Update";
        if (employee.Id == 0) employee.Id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,Grade,WorkLocationId,ReportingManagerId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@Grade,@WorkLocationId,@ReportingManagerId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", employee);
        else await db.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId,EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,IsActive=@IsActive WHERE Id=@Id", employee);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(db, employee);
        await db.ExecuteAsync("UPDATE employees SET SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson WHERE Id=@Id", employee);
        var after = await LoadEmployeeAsync(db, employee.Id) ?? employee;
        var reason = string.IsNullOrWhiteSpace(changeReason) ? wasNew ? "Employee hired" : "Infotype updated" : changeReason.Trim();
        await WriteCurrentInfotypesAsync(db, after, actionType, EffectiveDate(after), reason, changedBy, before, wasNew ? null : NormalizeInfotypeCodes(infotypeCode));
        return employee.Id;
    }
    public async Task<EmployeeDeletePreview?> GetDeletePreviewAsync(int id)
    {
        await using var db = Connection(); await db.OpenAsync();
        var employee = await db.QueryFirstOrDefaultAsync<(int Id, string EmployeeCode, string FirstName, string LastName)>("SELECT Id,EmployeeCode,FirstName,LastName FROM employees WHERE Id=@id", new { id });
        if (employee.Id == 0) return null;
        var links = new List<string>();
        async Task Add(string label, string table, string column, string filter = "") { var count = await CountSafeAsync(db, table, column, id, filter); if (count > 0) links.Add($"{count} {label}{(count == 1 ? "" : "s")}"); }
        await Add("reporting employee", "employees", "ReportingManagerId", "AND IsActive=TRUE");
        await Add("login user", "authusers", "EmployeeId", "AND IsActive=TRUE");
        await Add("pay run row", "payrunemployees", "EmployeeId");
        await Add("payroll adjustment", "payrolladjustments", "EmployeeId");
        await Add("attendance group mapping", "attendance_group_employees", "employee_id");
        await Add("geo fence mapping", "attendance_geo_fence_rule_employees", "employee_id");
        await Add("monthly attendance row", "employee_monthly_attendance", "employee_id");
        await Add("daily attendance row", "employee_daily_attendance", "employee_id");
        await Add("leave balance", "employee_leave_balances", "employee_id");
        await Add("ESS leave request", "essleaverequests", "EmployeeId");
        await Add("tax declaration", "employee_tax_declaration_headers", "employee_id");
        await Add("tax computation", "tax_computation_snapshots", "employee_id");
        var name = $"{employee.FirstName} {employee.LastName}".Trim();
        return new EmployeeDeletePreview(employee.Id, employee.EmployeeCode, string.IsNullOrWhiteSpace(name) ? employee.EmployeeCode : name, links, links.Count == 0);
    }
    public async Task<(bool Ok, string Error)> DeleteAsync(int id)
    {
        var preview = await GetDeletePreviewAsync(id);
        if (preview is null) return (false, "Employee not found.");
        if (preview.Links.Count > 0) return (false, $"Cannot delete {preview.EmployeeName}. Linked records: {string.Join(" | ", preview.Links)}");
        await using var db = Connection(); await db.OpenAsync();
        await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=@id", new { id });
        return (true, "");
    }

    public async Task<IEnumerable<EmployeeInfotypeRecord>> GetInfotypesAsync(int employeeId, bool activeOnly = false)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        var filter = activeOnly ? "AND t.Status='Active'" : "";
        return await db.QueryAsync<EmployeeInfotypeRecord>($@"{InfotypeUnionSql($"t.EmployeeId=@employeeId {filter}")}
ORDER BY EffectiveFrom DESC, Id DESC", new { employeeId });
    }

    public async Task<IEnumerable<EmployeeInfotypeRecord>> GetActiveInfotypesAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        return await db.QueryAsync<EmployeeInfotypeRecord>($@"{InfotypeUnionSql("t.ClientId=@clientId AND t.Status='Active' AND e.IsActive=TRUE")}
ORDER BY EmployeeCode, InfotypeCode", new { clientId });
    }

    public async Task<IEnumerable<EmployeeAuditTrail>> GetAuditTrailAsync(int employeeId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        return await db.QueryAsync<EmployeeAuditTrail>("SELECT * FROM employee_audit_trail WHERE EmployeeId=@employeeId ORDER BY ChangedAt DESC, Id DESC", new { employeeId });
    }

    public async Task<(Employee? Employee, string? Error)> ProcessActionAsync(EmployeeActionRequest request, string changedBy)
    {
        if (request.EmployeeId <= 0) return (null, "Select an employee.");
        if (!EmployeeActions.Contains(request.ActionType)) return (null, "Select a valid employee action.");
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        var before = await LoadEmployeeAsync(db, request.EmployeeId);
        if (before is null) return (null, "Employee not found.");
        var next = CloneEmployee(before);
        if (request.ActionType is "Promotion" or "Demotion" or "Transfer")
        {
            if (!string.IsNullOrWhiteSpace(request.Department)) next.Department = request.Department.Trim();
            if (!string.IsNullOrWhiteSpace(request.Designation)) next.Designation = request.Designation.Trim();
            if (!string.IsNullOrWhiteSpace(request.Grade)) next.Grade = request.Grade.Trim();
            if (request.WorkLocationId > 0) next.WorkLocationId = request.WorkLocationId;
        }
        if (request.ActionType is "Salary Change")
        {
            if (request.AnnualCtc > 0) next.AnnualCtc = request.AnnualCtc;
            if (!string.IsNullOrWhiteSpace(request.SalaryStructureId)) next.SalaryStructureId = request.SalaryStructureId;
            if (!string.IsNullOrWhiteSpace(request.SalaryJson) && request.SalaryJson != "{}") next.SalaryJson = request.SalaryJson;
        }
        if (request.ActionType is "Retire" or "Terminate" or "Resign") next.IsActive = false;
        if (request.ActionType == "Rehire") next.IsActive = true;

        await db.ExecuteAsync(@"UPDATE employees SET Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,SalaryJson=@SalaryJson,IsActive=@IsActive WHERE Id=@Id", next);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(db, next);
        var after = await LoadEmployeeAsync(db, request.EmployeeId) ?? next;
        await WriteCurrentInfotypesAsync(db, after, request.ActionType, request.EffectiveDate, request.Reason, changedBy, before, ActionInfotypeCodes(request.ActionType));
        return (after, null);
    }

    public async Task<EmployeeImportResult> ImportCsvAsync(int clientId, IFormFile file)
    {
        return await ImportRowsAsync(clientId, await ParseImportFileAsync(file));
    }

    public async Task<EmployeeImportJobStatus> StartImportCsvJobAsync(int clientId, IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new EmployeeImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        ImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetJob(job.JobId, current => current with { State = "Processing" });
            var result = await ImportRowsAsync(clientId, rows, (completed, inserted, updated) => SetJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
            SetJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
        });
        return job;
    }

    public EmployeeImportJobStatus? GetImportJob(Guid jobId) => ImportJobs.TryGetValue(jobId, out var job) ? job : null;

    public async Task<byte[]> BuildImportTemplateAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        var client = await db.QueryFirstOrDefaultAsync<(int Id, string Name)>("SELECT Id, Name FROM clients WHERE Id=@clientId", new { clientId });
        var drops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade') ORDER BY Type, Value", new { clientId })).ToList();
        var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE ORDER BY Name", new { clientId })).ToList();
        string First(string type, string fallback) => drops.FirstOrDefault(item => item.Type == type).Value ?? fallback;
        var headers = new[] { "Employee Code", "First Name", "Last Name", "Gender", "Date Of Joining", "Work Email", "Department", "Designation", "Grade", "Work Location", "Annual CTC", "Date Of Birth", "Mobile", "PAN", "Aadhaar", "UAN Number", "Address", "Correspondence Address", "Permanent Address" };
        var example = new[] { "EMP001", "Rahul", "Sharma", "Male", "2026-04-01", "rahul@example.com", First("Department", ""), First("Designation", ""), First("Employee Grade", ""), locations.FirstOrDefault().Name ?? "", "600000", "1995-01-15", "9876543210", "ABCDE1234F", "123412341234", "100200300400", "Local address", "Correspondence address", "Permanent address" };
        var masters = new List<string[]> { new[] { "Master Type", "Value", "Id" } };
        if (client.Id > 0) masters.Add(new[] { "Client", client.Name, client.Id.ToString() });
        masters.AddRange(drops.Select(item => new[] { item.Type, item.Value, "" }));
        masters.AddRange(locations.Select(item => new[] { "Work Location", item.Name, item.Id.ToString() }));
        return BuildXlsx(("Employees", new[] { headers, example }), ("Masters", masters));
    }

    async Task<EmployeeImportResult> ImportRowsAsync(int clientId, List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        try
        {
            if (rows.Count < 2) return new EmployeeImportResult(0, 0, 0, ["Import file has no data rows."]);
            var header = rows[0].Select(Norm).ToList();
            var validDrops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade')", new { clientId }, tx)).GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.Select(v => v.Value).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE", new { clientId }, tx)).ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
            var inserted = 0; var updated = 0; var completed = 0; var errors = new List<string>();
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
                string V(string name) => header.IndexOf(Norm(name)) is var ix && ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                var rowErrors = new List<string>();
                var code = V("Employee Code"); var first = V("First Name"); var email = V("Work Email");
                if (string.IsNullOrWhiteSpace(code)) rowErrors.Add($"Row {i + 1}: Employee Code is required.");
                if (string.IsNullOrWhiteSpace(first)) rowErrors.Add($"Row {i + 1}: First Name is required.");
                if (!DateOk(V("Date Of Joining"))) rowErrors.Add($"Row {i + 1}: Date Of Joining must be yyyy-MM-dd.");
                if (!DateOk(V("Date Of Birth"))) rowErrors.Add($"Row {i + 1}: Date Of Birth must be yyyy-MM-dd.");
                ValidateMaster("Department", V("Department"), validDrops, rowErrors, i + 1);
                ValidateMaster("Designation", V("Designation"), validDrops, rowErrors, i + 1);
                ValidateMaster("Employee Grade", V("Grade"), validDrops, rowErrors, i + 1, "Grade");
                var workLocation = V("Work Location");
                if (!string.IsNullOrWhiteSpace(workLocation) && !locations.ContainsKey(workLocation)) rowErrors.Add($"Row {i + 1}: Work Location \"{workLocation}\" is not in Work Locations.");
                if (rowErrors.Count > 0) { errors.AddRange(rowErrors); completed++; progress?.Invoke(completed, inserted, updated); continue; }
                var existingId = await db.ExecuteScalarAsync<int?>(@"SELECT Id FROM employees WHERE ClientId=@clientId AND (EmployeeCode=@code OR (WorkEmail<>'' AND WorkEmail=@email)) ORDER BY EmployeeCode=@code DESC LIMIT 1", new { clientId, code, email }, tx);
                var personal = JsonSerializer.Serialize(new { dateOfBirth = V("Date Of Birth"), mobile = V("Mobile"), panNumber = V("PAN"), aadhaarNumber = V("Aadhaar"), uanNumber = V("UAN Number"), address = V("Address"), correspondenceAddress = V("Correspondence Address"), permanentAddress = V("Permanent Address") });
                var args = new { Id = existingId ?? 0, ClientId = clientId, EmployeeCode = code, FirstName = first, LastName = V("Last Name"), Gender = V("Gender"), DateOfJoining = DbDate(V("Date Of Joining")), WorkEmail = email, Department = V("Department"), Designation = V("Designation"), Grade = V("Grade"), WorkLocationId = string.IsNullOrWhiteSpace(workLocation) ? 0 : locations[workLocation], AnnualCtc = decimal.TryParse(V("Annual CTC"), out var ctc) ? ctc : 0, PersonalJson = personal, SalaryJson = "{}", PaymentJson = "{}", IsActive = true };
                var id = existingId ?? (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,Grade,WorkLocationId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@Grade,@WorkLocationId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", args, tx);
                if (existingId is null) inserted++; else { updated++; await db.ExecuteAsync(@"UPDATE employees SET EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,AnnualCtc=@AnnualCtc,PersonalJson=@PersonalJson,IsActive=TRUE WHERE Id=@Id", args, tx); }
                await db.ExecuteAsync(@"INSERT INTO employeepersonaldetails (EmployeeId,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,Address,CorrespondenceAddress,PermanentAddress)
VALUES (@Id,@DateOfBirth,@Mobile,@Pan,@Aadhaar,@Uan,@Address,@Correspondence,@Permanent)
ON DUPLICATE KEY UPDATE DateOfBirth=@DateOfBirth,Mobile=@Mobile,PanNumber=@Pan,AadhaarNumber=@Aadhaar,UanNumber=@Uan,Address=@Address,CorrespondenceAddress=@Correspondence,PermanentAddress=@Permanent", new { Id = id, DateOfBirth = DbDate(V("Date Of Birth")), Mobile = V("Mobile"), Pan = V("PAN"), Aadhaar = V("Aadhaar"), Uan = V("UAN Number"), Address = V("Address"), Correspondence = V("Correspondence Address"), Permanent = V("Permanent Address") }, tx);
                completed++; progress?.Invoke(completed, inserted, updated);
            }
            if (errors.Count > 0) { await tx.RollbackAsync(); return new EmployeeImportResult(totalRows, 0, 0, errors); }
            await tx.CommitAsync(); return new EmployeeImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex) { await tx.RollbackAsync(); return new EmployeeImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]); }
    }

    static void SetJob(Guid jobId, Func<EmployeeImportJobStatus, EmployeeImportJobStatus> update) => ImportJobs.AddOrUpdate(jobId, _ => update(new EmployeeImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));
    static readonly HashSet<string> EmployeeActions = new(StringComparer.OrdinalIgnoreCase) { "Hire", "Promotion", "Salary Change", "Demotion", "Transfer", "Retire", "Terminate", "Resign", "Rehire", "Master Update" };
    static readonly HashSet<string> EmployeeInfotypeCodes = new(StringComparer.OrdinalIgnoreCase) { "0000", "0001", "0002", "0006", "0008", "0009" };

    static HashSet<string>? NormalizeInfotypeCodes(string? infotypeCode)
    {
        if (string.IsNullOrWhiteSpace(infotypeCode)) return new HashSet<string>(EmployeeInfotypeCodes, StringComparer.OrdinalIgnoreCase);
        var codes = infotypeCode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(EmployeeInfotypeCodes.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return codes.Count == 0 ? new HashSet<string>(EmployeeInfotypeCodes, StringComparer.OrdinalIgnoreCase) : codes;
    }

    static HashSet<string> ActionInfotypeCodes(string actionType) => actionType switch
    {
        "Salary Change" => new HashSet<string>(["0000", "0008"], StringComparer.OrdinalIgnoreCase),
        "Promotion" or "Demotion" => new HashSet<string>(["0000", "0001"], StringComparer.OrdinalIgnoreCase),
        "Transfer" => new HashSet<string>(["0000", "0001"], StringComparer.OrdinalIgnoreCase),
        "Retire" or "Terminate" or "Resign" or "Rehire" => new HashSet<string>(["0000"], StringComparer.OrdinalIgnoreCase),
        _ => new HashSet<string>(["0000"], StringComparer.OrdinalIgnoreCase)
    };

    static string InfotypeUnionSql(string where) => $@"
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0000' InfotypeCode,'Actions' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('IsActive',t.IsActive,'DateOfJoining',t.DateOfJoining) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0000_actions t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}
UNION ALL
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0001' InfotypeCode,'Organizational Assignment' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('ClientId',t.ClientId,'Department',t.Department,'Designation',t.Designation,'Grade',t.Grade,'WorkLocationId',t.WorkLocationId,'ReportingManagerId',t.ReportingManagerId,'WorkEmail',t.WorkEmail,'PortalAccess',t.PortalAccess) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0001_org_assignment t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}
UNION ALL
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0002' InfotypeCode,'Personal Data' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('FirstName',t.FirstName,'LastName',t.LastName,'Gender',t.Gender,'PersonalDetails',JSON_OBJECT('DateOfBirth',t.DateOfBirth,'Mobile',t.Mobile,'PanNumber',t.PanNumber,'AadhaarNumber',t.AadhaarNumber,'UanNumber',t.UanNumber,'EsicNumber',t.EsicNumber)) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0002_personal_data t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}
UNION ALL
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0006' InfotypeCode,'Addresses' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('Address',t.Address,'CorrespondenceAddress',t.CorrespondenceAddress,'PermanentAddress',t.PermanentAddress) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0006_addresses t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}
UNION ALL
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0008' InfotypeCode,'Basic Pay' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('SalaryStructureId',t.SalaryStructureId,'AnnualCtc',t.AnnualCtc,'SalaryComponents',JSON_EXTRACT(t.SalaryJson,'$')) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0008_basic_pay t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}
UNION ALL
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0009' InfotypeCode,'Bank Details' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('BankName',t.BankName,'BankAccountNo',t.BankAccountNo,'IfscCode',t.IfscCode,'PaymentMode',t.PaymentMode) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0009_bank_details t JOIN employees e ON e.Id=t.EmployeeId WHERE {where}";

    static async Task EnsureEmployeeInfotypeTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employee_infotype_records (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    InfotypeCode VARCHAR(20) NOT NULL,
    InfotypeName VARCHAR(120) NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    DataJson JSON NOT NULL,
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_EmployeeInfotype_Employee (EmployeeId, InfotypeCode, Status, EffectiveFrom),
    INDEX IX_EmployeeInfotype_Client_Active (ClientId, Status, InfotypeCode)
);
CREATE TABLE IF NOT EXISTS employee_it0000_actions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    DateOfJoining VARCHAR(20) NOT NULL DEFAULT '',
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0000_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0000_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_it0001_org_assignment (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    Department VARCHAR(160) NOT NULL DEFAULT '',
    Designation VARCHAR(160) NOT NULL DEFAULT '',
    Grade VARCHAR(80) NOT NULL DEFAULT '',
    WorkLocationId INT NOT NULL DEFAULT 0,
    ReportingManagerId INT NOT NULL DEFAULT 0,
    WorkEmail VARCHAR(190) NOT NULL DEFAULT '',
    PortalAccess BOOLEAN NOT NULL DEFAULT FALSE,
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0001_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0001_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_it0002_personal_data (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    FirstName VARCHAR(120) NOT NULL DEFAULT '',
    LastName VARCHAR(120) NOT NULL DEFAULT '',
    Gender VARCHAR(40) NOT NULL DEFAULT '',
    DateOfBirth VARCHAR(20) NOT NULL DEFAULT '',
    Mobile VARCHAR(40) NOT NULL DEFAULT '',
    PanNumber VARCHAR(40) NOT NULL DEFAULT '',
    AadhaarNumber VARCHAR(40) NOT NULL DEFAULT '',
    UanNumber VARCHAR(40) NOT NULL DEFAULT '',
    EsicNumber VARCHAR(40) NOT NULL DEFAULT '',
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0002_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0002_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_it0006_addresses (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    Address TEXT NULL,
    CorrespondenceAddress TEXT NULL,
    PermanentAddress TEXT NULL,
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0006_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0006_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_it0008_basic_pay (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    SalaryStructureId VARCHAR(80) NOT NULL DEFAULT '',
    AnnualCtc DECIMAL(18,2) NOT NULL DEFAULT 0,
    SalaryJson JSON NOT NULL,
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0008_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0008_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_it0009_bank_details (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ActionType VARCHAR(60) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    BankName VARCHAR(190) NOT NULL DEFAULT '',
    BankAccountNo VARCHAR(80) NOT NULL DEFAULT '',
    IfscCode VARCHAR(40) NOT NULL DEFAULT '',
    PaymentMode VARCHAR(60) NOT NULL DEFAULT '',
    ChangeReason VARCHAR(500) NOT NULL DEFAULT '',
    CreatedBy VARCHAR(190) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_IT0009_Employee (EmployeeId, Status, EffectiveFrom),
    INDEX IX_IT0009_Client (ClientId, Status)
);
CREATE TABLE IF NOT EXISTS employee_audit_trail (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    EmployeeCode VARCHAR(50) NOT NULL DEFAULT '',
    ActionType VARCHAR(60) NOT NULL,
    InfotypeCode VARCHAR(20) NOT NULL,
    FieldName VARCHAR(120) NOT NULL,
    OldValue TEXT NULL,
    NewValue TEXT NULL,
    EffectiveFrom DATE NOT NULL,
    ChangedBy VARCHAR(190) NOT NULL DEFAULT '',
    ChangedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_EmployeeAudit_Employee (EmployeeId, ChangedAt),
    INDEX IX_EmployeeAudit_Action (ActionType, InfotypeCode)
);");
    }

    static async Task<Employee?> LoadEmployeeAsync(MySqlConnection db, int id)
    {
        var employee = await db.QueryFirstOrDefaultAsync<Employee>("SELECT * FROM employees WHERE Id=@id", new { id });
        if (employee is null) return null;
        var rows = new List<Employee> { employee };
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows);
        return rows[0];
    }

    static Employee CloneEmployee(Employee row) => new()
    {
        Id = row.Id, ClientId = row.ClientId, EmployeeCode = row.EmployeeCode, FirstName = row.FirstName, LastName = row.LastName, Gender = row.Gender,
        DateOfJoining = row.DateOfJoining, WorkEmail = row.WorkEmail, Department = row.Department, Designation = row.Designation, Grade = row.Grade,
        WorkLocationId = row.WorkLocationId, ReportingManagerId = row.ReportingManagerId, PortalAccess = row.PortalAccess, SalaryStructureId = row.SalaryStructureId,
        AnnualCtc = row.AnnualCtc, SalaryJson = row.SalaryJson, PersonalJson = row.PersonalJson, PaymentJson = row.PaymentJson, IsActive = row.IsActive,
        SalaryComponents = new Dictionary<string, decimal>(row.SalaryComponents), PersonalDetails = row.PersonalDetails, PaymentDetails = row.PaymentDetails
    };

    static DateTime EffectiveDate(Employee employee) =>
        DateTime.TryParse(employee.DateOfJoining, out var date) ? date.Date : DateTime.Today;

    static async Task WriteCurrentInfotypesAsync(MySqlConnection db, Employee employee, string actionType, DateTime effectiveDate, string reason, string changedBy, Employee? before, HashSet<string>? onlyInfotypeCodes = null)
    {
        effectiveDate = effectiveDate.Date;
        var beforeSnapshots = before is null ? new Dictionary<string, string>() : InfotypeSnapshots(before).ToDictionary(item => item.InfotypeCode, item => item.DataJson);
        var infotypes = InfotypeSnapshots(employee).Where(item => onlyInfotypeCodes is null || onlyInfotypeCodes.Contains(item.InfotypeCode));
        foreach (var item in infotypes)
        {
            if (beforeSnapshots.TryGetValue(item.InfotypeCode, out var oldJson) && oldJson == item.DataJson) continue;
            await WritePhysicalInfotypeAsync(db, employee, item.InfotypeCode, actionType, effectiveDate, reason, changedBy);
        }
        await WriteAuditRowsAsync(db, employee, before, actionType, effectiveDate, changedBy ?? "", onlyInfotypeCodes);
    }

    static async Task WritePhysicalInfotypeAsync(MySqlConnection db, Employee employee, string infotypeCode, string actionType, DateTime effectiveDate, string reason, string changedBy)
    {
        var meta = new { EmployeeId = employee.Id, employee.ClientId, ActionType = actionType, EffectiveFrom = effectiveDate, ChangeReason = reason ?? "", CreatedBy = changedBy ?? "" };
        switch (infotypeCode)
        {
            case "0000":
                await CloseActiveInfotypeAsync(db, "employee_it0000_actions", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0000_actions (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,IsActive,DateOfJoining,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@IsActive,@DateOfJoining,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.IsActive, employee.DateOfJoining, meta.ChangeReason, meta.CreatedBy });
                break;
            case "0001":
                await CloseActiveInfotypeAsync(db, "employee_it0001_org_assignment", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0001_org_assignment (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,Department,Designation,Grade,WorkLocationId,ReportingManagerId,WorkEmail,PortalAccess,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@Department,@Designation,@Grade,@WorkLocationId,@ReportingManagerId,@WorkEmail,@PortalAccess,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.Department, employee.Designation, employee.Grade, employee.WorkLocationId, employee.ReportingManagerId, employee.WorkEmail, employee.PortalAccess, meta.ChangeReason, meta.CreatedBy });
                break;
            case "0002":
                await CloseActiveInfotypeAsync(db, "employee_it0002_personal_data", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0002_personal_data (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,FirstName,LastName,Gender,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,EsicNumber,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@FirstName,@LastName,@Gender,@DateOfBirth,@Mobile,@PanNumber,@AadhaarNumber,@UanNumber,@EsicNumber,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.FirstName, employee.LastName, employee.Gender, employee.PersonalDetails.DateOfBirth, employee.PersonalDetails.Mobile, employee.PersonalDetails.PanNumber, employee.PersonalDetails.AadhaarNumber, employee.PersonalDetails.UanNumber, employee.PersonalDetails.EsicNumber, meta.ChangeReason, meta.CreatedBy });
                break;
            case "0006":
                await CloseActiveInfotypeAsync(db, "employee_it0006_addresses", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0006_addresses (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,Address,CorrespondenceAddress,PermanentAddress,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@Address,@CorrespondenceAddress,@PermanentAddress,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.PersonalDetails.Address, employee.PersonalDetails.CorrespondenceAddress, employee.PersonalDetails.PermanentAddress, meta.ChangeReason, meta.CreatedBy });
                break;
            case "0008":
                await CloseActiveInfotypeAsync(db, "employee_it0008_basic_pay", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0008_basic_pay (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@SalaryStructureId,@AnnualCtc,@SalaryJson,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.SalaryStructureId, employee.AnnualCtc, SalaryJson = string.IsNullOrWhiteSpace(employee.SalaryJson) ? "{}" : employee.SalaryJson, meta.ChangeReason, meta.CreatedBy });
                break;
            case "0009":
                await CloseActiveInfotypeAsync(db, "employee_it0009_bank_details", employee.Id, effectiveDate);
                await db.ExecuteAsync(@"INSERT INTO employee_it0009_bank_details (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,BankName,BankAccountNo,IfscCode,PaymentMode,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@BankName,@BankAccountNo,@IfscCode,@PaymentMode,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.PaymentDetails.BankName, employee.PaymentDetails.BankAccountNo, employee.PaymentDetails.IfscCode, employee.PaymentDetails.PaymentMode, meta.ChangeReason, meta.CreatedBy });
                break;
        }
    }

    static Task CloseActiveInfotypeAsync(MySqlConnection db, string tableName, int employeeId, DateTime effectiveDate) =>
        db.ExecuteAsync($@"UPDATE {tableName}
SET Status='Historical', EffectiveTo=DATE_SUB(@EffectiveFrom, INTERVAL 1 DAY)
WHERE EmployeeId=@EmployeeId AND Status='Active' AND EffectiveFrom<=@EffectiveFrom", new { EmployeeId = employeeId, EffectiveFrom = effectiveDate });

    static IEnumerable<(string InfotypeCode, string InfotypeName, string DataJson)> InfotypeSnapshots(Employee employee)
    {
        yield return ("0000", "Actions", JsonSerializer.Serialize(new { employee.IsActive, employee.DateOfJoining }));
        yield return ("0001", "Organizational Assignment", JsonSerializer.Serialize(new { employee.ClientId, employee.Department, employee.Designation, employee.Grade, employee.WorkLocationId, employee.ReportingManagerId, employee.WorkEmail }));
        yield return ("0002", "Personal Data", JsonSerializer.Serialize(new { employee.FirstName, employee.LastName, employee.Gender, employee.PersonalDetails }));
        yield return ("0006", "Addresses", JsonSerializer.Serialize(new { employee.PersonalDetails.Address, employee.PersonalDetails.CorrespondenceAddress, employee.PersonalDetails.PermanentAddress }));
        yield return ("0008", "Basic Pay", JsonSerializer.Serialize(new { employee.SalaryStructureId, employee.AnnualCtc, employee.SalaryComponents }));
        yield return ("0009", "Bank Details", JsonSerializer.Serialize(employee.PaymentDetails));
    }

    static async Task WriteAuditRowsAsync(MySqlConnection db, Employee after, Employee? before, string actionType, DateTime effectiveDate, string changedBy, HashSet<string>? onlyInfotypeCodes = null)
    {
        effectiveDate = effectiveDate.Date;
        var oldValues = before is null ? new Dictionary<string, string>() : AuditValues(before);
        var newValues = AuditValues(after);
        var rows = newValues.Keys.Union(oldValues.Keys).Select(key => new { Key = key, Old = oldValues.GetValueOrDefault(key, ""), New = newValues.GetValueOrDefault(key, "") }).Where(row => row.Old != row.New && (onlyInfotypeCodes is null || onlyInfotypeCodes.Contains(row.Key.Split(':')[0])))
            .Select(row => new { after.Id, after.EmployeeCode, ActionType = actionType, InfotypeCode = row.Key.Split(':')[0], FieldName = row.Key.Split(':')[1], OldValue = row.Old, NewValue = row.New, EffectiveFrom = effectiveDate, ChangedBy = changedBy ?? "" })
            .ToList();
        if (rows.Count == 0) return;
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail (EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES (@Id,@EmployeeCode,@ActionType,@InfotypeCode,@FieldName,@OldValue,@NewValue,@EffectiveFrom,@ChangedBy)", rows);
    }

    static Dictionary<string, string> AuditValues(Employee employee) => new()
    {
        ["0000:IsActive"] = employee.IsActive.ToString(),
        ["0001:Department"] = employee.Department ?? "",
        ["0001:Designation"] = employee.Designation ?? "",
        ["0001:Grade"] = employee.Grade ?? "",
        ["0001:WorkLocationId"] = employee.WorkLocationId.ToString(),
        ["0001:ReportingManagerId"] = employee.ReportingManagerId.ToString(),
        ["0002:FirstName"] = employee.FirstName ?? "",
        ["0002:LastName"] = employee.LastName ?? "",
        ["0002:Gender"] = employee.Gender ?? "",
        ["0008:SalaryStructureId"] = employee.SalaryStructureId ?? "",
        ["0008:AnnualCtc"] = employee.AnnualCtc.ToString("0.##"),
        ["0008:SalaryJson"] = employee.SalaryJson ?? "{}",
        ["0009:BankName"] = employee.PaymentDetails.BankName ?? "",
        ["0009:BankAccountNo"] = employee.PaymentDetails.BankAccountNo ?? "",
        ["0009:IfscCode"] = employee.PaymentDetails.IfscCode ?? "",
        ["0009:PaymentMode"] = employee.PaymentDetails.PaymentMode ?? ""
    };

    static async Task<int> CountSafeAsync(MySqlConnection db, string table, string column, int id, string filter) { try { return await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {column}=@id {filter}", new { id }); } catch { return 0; } }
    static void ValidateMaster(string type, string value, Dictionary<string, HashSet<string>> masters, List<string> errors, int row, string? label = null) { if (!string.IsNullOrWhiteSpace(value) && (!masters.TryGetValue(type, out var values) || !values.Contains(value))) errors.Add($"Row {row}: {label ?? type} \"{value}\" is not in Dropdown Masters."); }
    static bool DateOk(string value) => string.IsNullOrWhiteSpace(value) || DateTime.TryParseExact(value, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _);
    static string? DbDate(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    static string Norm(string value) => value.Replace(" ", "").Replace("_", "").ToLowerInvariant();
    static async Task<List<List<string>>> ParseImportFileAsync(IFormFile file) { using var ms = new MemoryStream(); await file.CopyToAsync(ms); var bytes = ms.ToArray(); return file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ParseXlsx(bytes) : ParseCsv(Encoding.UTF8.GetString(bytes)); }
    static List<List<string>> ParseCsv(string text) { var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false; for (var i = 0; i < text.Length; i++) { var c = text[i]; if (q && c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else if (c == '"') q = !q; else if (!q && c == ',') { row.Add(cell.ToString()); cell.Clear(); } else if (!q && (c == '\n' || c == '\r')) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; } else cell.Append(c); } row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row); return rows; }
    static List<List<string>> ParseXlsx(byte[] bytes) { using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); var shared = ReadSharedStrings(zip); var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("Employees sheet not found."); using var stream = sheet.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var rows = new List<List<string>>(); foreach (var row in doc.Descendants(ns + "row")) { var values = new List<string>(); foreach (var cell in row.Elements(ns + "c")) { var index = CellIndex((string?)cell.Attribute("r") ?? "A1"); while (values.Count < index) values.Add(""); var type = (string?)cell.Attribute("t") ?? ""; var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? ""; values.Add(type == "s" && int.TryParse(raw, out var si) && si >= 0 && si < shared.Count ? shared[si] : raw); } rows.Add(values); } return rows; }
    static List<string> ReadSharedStrings(ZipArchive zip) { var entry = zip.GetEntry("xl/sharedStrings.xml"); if (entry is null) return []; using var stream = entry.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; return doc.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList(); }
    static int CellIndex(string reference) { var n = 0; foreach (var c in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(c) - 'A' + 1; return Math.Max(0, n - 1); }
    static byte[] BuildXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets) { using var ms = new MemoryStream(); using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>"""); Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""); Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"""); Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>"""); Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((s, i) => (s.Name, i + 1)))); foreach (var (sheet, ix) in sheets.Select((s, i) => (s, i + 1))) Add(zip, $"xl/worksheets/sheet{ix}.xml", SheetXml(sheet.Rows)); } return ms.ToArray(); }
    static void Add(ZipArchive zip, string path, string text) { var entry = zip.CreateEntry(path); using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(text); }
    static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) => new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), sheets.Select(s => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("name", s.Name), new XAttribute("sheetId", s.Index), new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{s.Index}")))))).ToString(SaveOptions.DisableFormatting);
    static string SheetXml(IEnumerable<string[]> rows) => new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), rows.Select((row, r) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", r + 1), row.Select((cell, c) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", $"{Col(c + 1)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);
    static string Col(int n) { var s = ""; while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; } return s; }
}

public record EmployeeImportResult(int TotalRows, int Inserted, int Updated, List<string> Errors);
public record EmployeeImportJobStatus(Guid JobId, string State, int TotalRows, int CompletedRows, int Inserted, int Updated, List<string> Errors);
public record EmployeeDeletePreview(int EmployeeId, string EmployeeCode, string EmployeeName, List<string> Links, bool CanDelete);
