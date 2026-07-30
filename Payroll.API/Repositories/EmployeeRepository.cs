using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class EmployeeRepository(IConfiguration configuration, AuthRepository authRepository, NotificationRepository notificationRepository)
{
    private const string InsertImportMode = "insert";
    private const string UpdateImportMode = "update";
    private const string UpsertImportMode = "upsert";
    private static readonly ConcurrentDictionary<Guid, EmployeeImportJobStatus> ImportJobs = new();
    private static readonly ConcurrentDictionary<Guid, EmployeeImportReviewState> ImportReviews = new();
    private static readonly TimeSpan ImportReviewLifetime = TimeSpan.FromMinutes(30);
    private static readonly string[] It0000ImportHeaders = ["Date Of Joining", "Active"];
    private static readonly string[] It0001ImportHeaders = ["Work Email", "Department", "Designation", "Grade", "Work Location Id", "Work Location", "Reporting Manager User Id", "Reporting Manager Email", "Portal Access"];
    private static readonly string[] It0002ImportHeaders = ["First Name", "Last Name", "Gender", "Date Of Birth", "Mobile", "PAN", "Aadhaar", "UAN Number", "ESIC Number"];
    private static readonly string[] It0006ImportHeaders = ["Address", "Correspondence Address", "Permanent Address"];
    private static readonly string[] It0008ImportHeaders = ["Salary Template Id", "Salary Template", "Annual CTC", "Salary Json"];
    private static readonly string[] It0009ImportHeaders = ["Bank Name", "Bank Account No", "IFSC", "Payment Mode"];
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task InitializeAsync() { await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db); }
    public async Task<IEnumerable<Employee>> GetAsync() { await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db); var rows = (await db.QueryAsync<Employee>("SELECT * FROM employees ORDER BY FirstName, LastName")).ToList(); await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows); return rows; }
    public async Task<IEnumerable<WorkflowApprover>> GetManagerUsersAsync()
    {
        await using var db = Connection(); await db.OpenAsync();
        return await db.QueryAsync<WorkflowApprover>(@"SELECT u.Id,u.DisplayName,u.Email,u.ClientId,COALESCE(c.Name,'All clients') ClientName
FROM authusers u
LEFT JOIN clients c ON c.Id=u.ClientId
WHERE u.IsActive=TRUE
ORDER BY u.DisplayName,u.Email");
    }
    public async Task<int> SaveAsync(Employee employee, string changedBy = "System", string? infotypeCode = null, string? changeReason = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db);
        return await SaveWithOpenConnectionAsync(db, employee, changedBy, infotypeCode, changeReason);
    }

    private static async Task<int> SaveWithOpenConnectionAsync(MySqlConnection db, Employee employee, string changedBy = "System", string? infotypeCode = null, string? changeReason = null)
    {
        var wasNew = employee.Id == 0;
        var before = employee.Id > 0 ? await LoadEmployeeAsync(db, employee.Id) : null;
        var actionType = wasNew ? "Hire" : "Master Update";
        if (employee.Id == 0) employee.Id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,Grade,WorkLocationId,ReportingManagerId,ReportingManagerUserId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@Grade,@WorkLocationId,@ReportingManagerId,@ReportingManagerUserId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", employee);
        else await db.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId,EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,ReportingManagerUserId=@ReportingManagerUserId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,IsActive=@IsActive WHERE Id=@Id", employee);
        if (wasNew) await EnsureDefaultTaxProfileAsync(db, employee.Id, employee.ClientId);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(db, employee);
        await db.ExecuteAsync("UPDATE employees SET SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson WHERE Id=@Id", employee);
        var after = await LoadEmployeeAsync(db, employee.Id) ?? employee;
        var reason = string.IsNullOrWhiteSpace(changeReason) ? wasNew ? "Employee hired" : "Infotype updated" : changeReason.Trim();
        await WriteCurrentInfotypesAsync(db, after, actionType, EffectiveDate(after), reason, changedBy, before, wasNew ? null : NormalizeInfotypeCodes(infotypeCode));
        await SyncAttendancePolicyMappingsAsync(db, after, before);
        return employee.Id;
    }

    private static async Task EnsureDefaultTaxProfileAsync(MySqlConnection db, int employeeId, int clientId)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS employee_tax_regime_selections (
id INT PRIMARY KEY AUTO_INCREMENT,
employee_id INT NOT NULL, client_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, regime VARCHAR(20) NOT NULL DEFAULT 'New',
status VARCHAR(30) NOT NULL DEFAULT 'Draft', submitted_at DATETIME NULL, approved_at DATETIME NULL, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_employee_tax_regime (employee_id, financial_year));");
        await db.ExecuteAsync(@"INSERT INTO employee_tax_regime_selections (employee_id,client_id,financial_year,regime,status)
VALUES (@EmployeeId,@ClientId,@FinancialYear,'New','Draft')
ON DUPLICATE KEY UPDATE client_id=@ClientId",
            new { EmployeeId = employeeId, ClientId = clientId, FinancialYear = CurrentFinancialYear() });
    }

    private static string CurrentFinancialYear()
    {
        var today = DateTime.Today;
        var startYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{startYear}-{(startYear + 1) % 100:00}";
    }
    public async Task<EmployeeDeletePreview?> GetDeletePreviewAsync(int id)
    {
        await using var db = Connection(); await db.OpenAsync();
        var employee = await db.QueryFirstOrDefaultAsync<(int Id, string EmployeeCode, string FirstName, string LastName)>("SELECT Id,EmployeeCode,FirstName,LastName FROM employees WHERE Id=@id", new { id });
        if (employee.Id == 0) return null;
        var links = new List<string>();
        async Task Add(string label, string table, string column, string filter = "") { var count = await CountSafeAsync(db, table, column, id, filter); if (count > 0) links.Add($"{count} {label}{(count == 1 ? "" : "s")}"); }
        await Add("reporting employee", "employees", "ReportingManagerId", "AND IsActive=TRUE");
        await Add("reporting manager user", "employees", "ReportingManagerUserId", "AND IsActive=TRUE");
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
        await SyncAttendancePolicyMappingsAsync(db, after, before);
        return (after, null);
    }

    public async Task<EmployeeImportResult> ImportCsvAsync(int clientId, IFormFile file)
    {
        return await ImportCsvAsync(clientId, file, UpsertImportMode);
    }

    public async Task<EmployeeImportResult> ImportCsvAsync(int clientId, IFormFile file, string? mode)
    {
        return await ImportCsvAsync(clientId, file, mode, null, null);
    }

    public async Task<EmployeeImportResult> ImportCsvAsync(int clientId, IFormFile? file, string? mode, Guid? reviewToken, string? decisionsJson)
    {
        if (!TryNormalizeImportMode(mode, out var normalizedMode, out var error))
            return new EmployeeImportResult(0, 0, 0, [error]);
        var prepared = await PrepareReviewedImportAsync(clientId, file, normalizedMode, reviewToken, decisionsJson);
        if (prepared.Error is not null) return prepared.Error;
        return await ImportWorkbookAsync(clientId, prepared.Workbook!, normalizedMode);
    }

    public async Task<EmployeeImportJobStatus> StartImportCsvJobAsync(int clientId, IFormFile file)
    {
        return await StartImportCsvJobAsync(clientId, file, UpsertImportMode);
    }

    public async Task<EmployeeImportJobStatus> StartImportCsvJobAsync(int clientId, IFormFile file, string? mode)
    {
        return await StartImportCsvJobAsync(clientId, file, mode, null, null);
    }

    public async Task<EmployeeImportJobStatus> StartImportCsvJobAsync(int clientId, IFormFile? file, string? mode, Guid? reviewToken, string? decisionsJson)
    {
        if (!TryNormalizeImportMode(mode, out var normalizedMode, out var modeError))
        {
            var failed = new EmployeeImportJobStatus(Guid.NewGuid(), "Failed", 0, 0, 0, 0, [modeError]);
            ImportJobs[failed.JobId] = failed;
            return failed;
        }
        var prepared = await PrepareReviewedImportAsync(clientId, file, normalizedMode, reviewToken, decisionsJson);
        if (prepared.Error is not null)
        {
            var result = prepared.Error;
            var state = result.RequiresConfirmation ? "NeedsConfirmation" : "Failed";
            var failed = new EmployeeImportJobStatus(Guid.NewGuid(), state, result.TotalRows, 0, 0, 0, result.Errors, result.ReviewToken, result.RequiresConfirmation, result.ReviewRows);
            ImportJobs[failed.JobId] = failed;
            return failed;
        }
        var workbook = prepared.Workbook!;
        var totalRows = CountImportRows(workbook);
        var job = new EmployeeImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        ImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetJob(job.JobId, current => current with { State = "Processing" });
            var result = await ImportWorkbookAsync(clientId, workbook, normalizedMode, (completed, inserted, updated) => SetJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
            SetJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
        });
        return job;
    }

    public EmployeeImportJobStatus? GetImportJob(Guid jobId) => ImportJobs.TryGetValue(jobId, out var job) ? job : null;

    public async Task<EmployeeImportPreflightResult> PreflightImportCsvAsync(int clientId, IFormFile file, string? mode)
    {
        PruneImportReviews();
        if (!TryNormalizeImportMode(mode, out var normalizedMode, out var modeError))
            return new EmployeeImportPreflightResult(Guid.Empty, 0, false, false,
                [new EmployeeImportPreflightRow(1, "Employees", "", "Blocked", null, null, null, [], [modeError], [])],
                DateTime.UtcNow);
        var workbook = await ParseImportWorkbookAsync(file);
        var result = await BuildImportPreflightAsync(clientId, workbook, normalizedMode);
        if (result.ReviewToken != Guid.Empty)
            ImportReviews[result.ReviewToken] = new EmployeeImportReviewState(clientId, normalizedMode, workbook, result, result.ExpiresAtUtc, await HashFileAsync(file));
        return result;
    }

    private async Task<(EmployeeImportWorkbook? Workbook, EmployeeImportResult? Error)> PrepareReviewedImportAsync(
        int clientId, IFormFile? file, string normalizedMode, Guid? reviewToken, string? decisionsJson)
    {
        PruneImportReviews();
        if (reviewToken.HasValue)
        {
            // Claim the token atomically so two confirmation requests cannot
            // queue the same reviewed workbook at the same time.
            if (!ImportReviews.TryRemove(reviewToken.Value, out var review))
                return (null, new EmployeeImportResult(0, 0, 0, ["Employee import review has expired or was already used. Run preflight again."]));
            if (review.ClientId != clientId || !string.Equals(review.Mode, normalizedMode, StringComparison.OrdinalIgnoreCase))
            {
                RestoreImportReview(reviewToken.Value, review);
                return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, ["Review token does not belong to this client/import mode."]));
            }
            if (file is null || file.Length == 0)
            {
                RestoreImportReview(reviewToken.Value, review);
                return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, ["Re-upload the reviewed employee file when confirming the import."], review.Result.ReviewToken, true, review.Result.Rows));
            }
            if (!string.Equals(await HashFileAsync(file), review.FileHash, StringComparison.Ordinal))
            {
                RestoreImportReview(reviewToken.Value, review);
                return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, ["The confirmation file is different from the reviewed file. Run preflight again."], review.Result.ReviewToken, true, review.Result.Rows));
            }
            var confirmed = ApplyImportReviewDecisions(review, decisionsJson);
            if (confirmed.Error is not null)
            {
                RestoreImportReview(reviewToken.Value, review);
                return (null, confirmed.Error);
            }
            return (confirmed.Workbook, null);
        }

        if (file is null || file.Length == 0)
            return (null, new EmployeeImportResult(0, 0, 0, ["Select an employee CSV or Excel file."]));
        var workbook = await ParseImportWorkbookAsync(file);
        var preflight = await BuildImportPreflightAsync(clientId, workbook, normalizedMode);
        if (!preflight.CanImport || preflight.RequiresConfirmation)
        {
            ImportReviews[preflight.ReviewToken] = new EmployeeImportReviewState(clientId, normalizedMode, workbook, preflight, preflight.ExpiresAtUtc, await HashFileAsync(file));
            var errors = !preflight.CanImport
                ? preflight.Rows.SelectMany(row => row.BlockingReasons.Select(reason => $"{row.Sheet} row {row.RowNumber}: {reason}")).Distinct().ToList()
                : ["Employee import needs explicit review before any rows are saved."];
            return (null, new EmployeeImportResult(preflight.TotalRows, 0, 0, errors, preflight.ReviewToken, preflight.RequiresConfirmation, preflight.Rows));
        }
        return (workbook, null);
    }

    private async Task<EmployeeImportPreflightResult> BuildImportPreflightAsync(int clientId, EmployeeImportWorkbook workbook, string importMode)
    {
        var token = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.Add(ImportReviewLifetime);
        var totalRows = CountImportRows(workbook);
        if (totalRows == 0)
            return new EmployeeImportPreflightResult(token, 0, false, false,
                [new EmployeeImportPreflightRow(1, "Employees", "", "Blocked", null, null, null, [], ["Import file has no data rows."], [])],
                expiresAt);

        // The identity preflight applies to the flat Employees worksheet. Existing
        // infotype workbooks retain their established Employee Code contract.
        if (HasDataSheet(workbook, "0001 Org Assignment", "0002 Personal Data", "0006 Addresses", "0008 Basic Pay", "0009 Bank Details"))
            return new EmployeeImportPreflightResult(token, totalRows, true, false, [], expiresAt);

        await using var db = Connection();
        await db.OpenAsync();
        var existing = (await db.QueryAsync<Employee>("SELECT * FROM employees WHERE ClientId=@clientId", new { clientId })).ToList();
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, existing);
        var byId = existing.ToDictionary(employee => employee.Id);
        var byCode = BuildIdentityMap(existing, employee => NormalizeCode(employee.EmployeeCode));
        var byMobile = BuildIdentityMap(existing, employee => NormalizeMobile(employee.PersonalDetails?.Mobile));
        var byAadhaar = BuildIdentityMap(existing, employee => NormalizeAadhaar(employee.PersonalDetails?.AadhaarNumber));
        var byPan = BuildIdentityMap(existing, employee => NormalizePan(employee.PersonalDetails?.PanNumber));
        var byBank = BuildIdentityMap(existing, employee => NormalizeBankAccount(employee.PaymentDetails?.BankAccountNo));

        var rows = GetEmployeeDataSheet(workbook);
        var sheetName = GetEmployeeDataSheetName(workbook);
        var map = rows.Count > 0 ? HeaderMap(rows[0]) : new Dictionary<string, int>();
        var resultRows = new List<EmployeeImportPreflightRow>();
        var workbookIdentifiers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < rows.Count; index++)
        {
            var source = rows[index];
            if (Blank(source)) continue;
            var rowNumber = index + 1;
            var blocking = new List<string>();
            var reasons = new List<string>();
            var reviewCandidates = new Dictionary<int, (Employee Employee, List<string> Reasons)>();
            var hardConflict = false;
            var alternateIdentifierConflict = false;
            Employee? stableMatch = null;

            var idText = Cell(source, map, "Employee ID");
            if (!string.IsNullOrWhiteSpace(idText))
            {
                if (!int.TryParse(idText, out var employeeId) || employeeId <= 0)
                {
                    blocking.Add("Employee ID must be a positive whole number.");
                    hardConflict = true;
                }
                else if (!byId.TryGetValue(employeeId, out stableMatch))
                {
                    blocking.Add($"Employee ID {employeeId} does not belong to this client.");
                    hardConflict = true;
                }
                else
                {
                    reasons.Add("Exact Employee ID");
                    AddReviewCandidate(reviewCandidates, stableMatch, "Exact Employee ID");
                }
            }

            var code = Cell(source, map, "Employee Code");
            var codeMatches = LookupIdentity(byCode, NormalizeCode(code));
            if (codeMatches.Count > 1)
            {
                blocking.Add($"Employee Code \"{code}\" is assigned to multiple employees.");
                hardConflict = true;
            }
            foreach (var employee in codeMatches) AddReviewCandidate(reviewCandidates, employee, "Exact Employee Code");
            var codeMatch = codeMatches.Count == 1 ? codeMatches[0] : null;
            if (stableMatch is not null && codeMatch is not null && stableMatch.Id != codeMatch.Id)
            {
                blocking.Add($"Employee ID and Employee Code \"{code}\" identify different employees.");
                hardConflict = true;
            }
            else if (stableMatch is not null && !string.IsNullOrWhiteSpace(code) && !string.Equals(stableMatch.EmployeeCode, code, StringComparison.OrdinalIgnoreCase))
            {
                blocking.Add($"Employee ID {stableMatch.Id} belongs to employee code \"{stableMatch.EmployeeCode}\", not \"{code}\".");
                hardConflict = true;
            }
            else if (stableMatch is null && codeMatch is not null)
            {
                stableMatch = codeMatch;
                reasons.Add("Exact Employee Code");
            }

            var identifierCandidates = new Dictionary<int, (Employee Employee, List<string> Reasons)>();
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byMobile, NormalizeMobile(Cell(source, map, "Mobile"))), "Mobile");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byAadhaar, NormalizeAadhaar(Cell(source, map, "Aadhaar"))), "Aadhaar");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byPan, NormalizePan(Cell(source, map, "PAN"))), "PAN");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byBank, NormalizeBankAccount(Cell(source, map, "Bank Account No"))), "Bank Account");
            foreach (var candidate in identifierCandidates.Values)
                foreach (var reason in candidate.Reasons)
                    AddReviewCandidate(reviewCandidates, candidate.Employee, $"Exact {reason}");

            if (identifierCandidates.Count > 1)
            {
                var detail = string.Join("; ", identifierCandidates.Values.Select(candidate =>
                    $"{candidate.Employee.EmployeeCode}: {string.Join(", ", candidate.Reasons)}"));
                blocking.Add($"Identifiers point to different employees ({detail}).");
                alternateIdentifierConflict = true;
            }
            else if (identifierCandidates.Count == 1)
            {
                var identifierMatch = identifierCandidates.Values.Single();
                if (stableMatch is not null && stableMatch.Id != identifierMatch.Employee.Id)
                {
                    blocking.Add($"{string.Join(", ", identifierMatch.Reasons)} belongs to employee {identifierMatch.Employee.EmployeeCode}, not {stableMatch.EmployeeCode}.");
                    alternateIdentifierConflict = true;
                }
                else
                {
                    stableMatch ??= identifierMatch.Employee;
                    reasons.AddRange(identifierMatch.Reasons.Select(reason => $"Exact {reason}"));
                }
            }

            var matchStatus = stableMatch is null ? "New" : reasons.Any(reason => reason is "Exact Employee ID" or "Exact Employee Code")
                ? reasons.Contains("Exact Employee ID") ? "MatchedByEmployeeId" : "MatchedByEmployeeCode"
                : "MatchedByIdentifiers";

            if (blocking.Count == 0 && stableMatch is null)
            {
                var probable = FindNameAddressMatches(existing, source, map);
                if (probable.Count > 1)
                {
                    blocking.Add($"Name and address match multiple employees: {string.Join(", ", probable.Select(employee => employee.EmployeeCode))}.");
                    hardConflict = true;
                    foreach (var employee in probable) AddReviewCandidate(reviewCandidates, employee, "Same normalized name and address");
                }
                else if (probable.Count == 1)
                {
                    stableMatch = probable[0];
                    matchStatus = "ProbableDuplicate";
                    reasons.Add("Same normalized name and address; manual decision required");
                    AddReviewCandidate(reviewCandidates, stableMatch, "Same normalized name and address");
                }
            }

            if (importMode == InsertImportMode && stableMatch is not null && matchStatus != "ProbableDuplicate")
            {
                blocking.Add($"Employee {stableMatch.EmployeeCode} already exists. Insert mode accepts new employees only.");
                hardConflict = true;
            }
            if (importMode == UpdateImportMode && stableMatch is null)
            {
                blocking.Add("No existing employee was identified. Update mode accepts existing employees only.");
                if (!alternateIdentifierConflict) hardConflict = true;
            }
            if (stableMatch is null && string.IsNullOrWhiteSpace(code))
            {
                blocking.Add("Employee Code is required for a new employee. Generate or map a code before confirmation.");
                if (!alternateIdentifierConflict) hardConflict = true;
            }

            var canResolveConflict = alternateIdentifierConflict && !hardConflict && importMode != InsertImportMode && reviewCandidates.Count > 0;
            if (canResolveConflict) matchStatus = "IdentityConflict";
            else if (blocking.Count > 0) matchStatus = "Blocked";
            var displayedMatch = canResolveConflict ? null : stableMatch;
            var changes = displayedMatch is null ? [] : BuildImportChanges(source, map, displayedMatch);
            var candidateEmployees = reviewCandidates.Values.Select(candidate => new EmployeeImportCandidate(
                candidate.Employee.Id,
                candidate.Employee.EmployeeCode,
                $"{candidate.Employee.FirstName} {candidate.Employee.LastName}".Trim(),
                candidate.Reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BuildImportChanges(source, map, candidate.Employee))).ToList();
            var identityEvidence = BuildIdentityEvidence(source, map, byId, byCode, byMobile, byAadhaar, byPan, byBank);
            resultRows.Add(new EmployeeImportPreflightRow(
                rowNumber,
                sheetName,
                displayedMatch?.EmployeeCode ?? code,
                matchStatus,
                displayedMatch?.Id,
                displayedMatch?.EmployeeCode,
                displayedMatch is null ? null : $"{displayedMatch.FirstName} {displayedMatch.LastName}".Trim(),
                reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                blocking.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                changes,
                candidateEmployees,
                identityEvidence,
                canResolveConflict));
            TrackWorkbookIdentifier(workbookIdentifiers, "Mobile", NormalizeMobile(Cell(source, map, "Mobile")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "Aadhaar", NormalizeAadhaar(Cell(source, map, "Aadhaar")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "PAN", NormalizePan(Cell(source, map, "PAN")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "Bank Account", NormalizeBankAccount(Cell(source, map, "Bank Account No")), resultRows.Count - 1);
        }

        foreach (var duplicate in workbookIdentifiers.Where(item => item.Value.Distinct().Count() > 1))
        {
            var separator = duplicate.Key.IndexOf(':');
            var label = separator > 0 ? duplicate.Key[..separator] : "Identifier";
            foreach (var rowIndex in duplicate.Value.Distinct())
            {
                var row = resultRows[rowIndex];
                var otherRows = duplicate.Value.Distinct().Where(index => index != rowIndex).Select(index => resultRows[index].RowNumber);
                var blockingReasons = row.BlockingReasons
                    .Append($"{label} is also supplied on workbook row(s) {string.Join(", ", otherRows)}. One person must not be inserted or updated twice.")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                resultRows[rowIndex] = row with { MatchStatus = "Blocked", BlockingReasons = blockingReasons, CanResolveConflict = false };
            }
        }

        // A mixed workbook remains actionable: blocked rows can be explicitly
        // skipped while clean rows proceed. All-blocked/fatal files cannot import.
        var canImport = resultRows.Any(row => row.BlockingReasons.Count == 0 || row.CanResolveConflict);
        var requiresConfirmation = resultRows.Any(RowRequiresConfirmation);
        return new EmployeeImportPreflightResult(token, totalRows, canImport, requiresConfirmation, resultRows, expiresAt);
    }

    private static (EmployeeImportWorkbook? Workbook, EmployeeImportResult? Error) ApplyImportReviewDecisions(EmployeeImportReviewState review, string? decisionsJson)
    {
        List<EmployeeImportDecision> decisions;
        try
        {
            decisions = string.IsNullOrWhiteSpace(decisionsJson)
                ? []
                : JsonSerializer.Deserialize<List<EmployeeImportDecision>>(decisionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex)
        {
            return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, [$"Decisions JSON is invalid: {ex.Message}"], review.Result.ReviewToken, true, review.Result.Rows));
        }

        var decisionMap = decisions
            .GroupBy(decision => $"{Norm(decision.Sheet)}:{decision.RowNumber}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var workbook = CloneWorkbook(review.Workbook);
        var sheetName = GetEmployeeDataSheetName(workbook);
        if (!workbook.Sheets.TryGetValue(sheetName, out var rows) || rows.Count == 0)
            return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, ["Employee worksheet is no longer available."], review.Result.ReviewToken, true, review.Result.Rows));
        var map = HeaderMap(rows[0]);
        var skipRows = new HashSet<int>();

        foreach (var reviewRow in review.Result.Rows)
        {
            var prefix = $"{reviewRow.Sheet} row {reviewRow.RowNumber}";
            decisionMap.TryGetValue($"{Norm(reviewRow.Sheet)}:{reviewRow.RowNumber}", out var decision);
            if (reviewRow.BlockingReasons.Count > 0 && !reviewRow.CanResolveConflict)
            {
                if (string.Equals(decision?.Action?.Trim(), "skip", StringComparison.OrdinalIgnoreCase))
                {
                    skipRows.Add(reviewRow.RowNumber);
                    continue;
                }
                errors.Add($"{prefix}: this row is blocked and must be explicitly skipped. {string.Join(" | ", reviewRow.BlockingReasons)}");
                continue;
            }

            if (RowRequiresConfirmation(reviewRow) && decision is null)
            {
                errors.Add($"{prefix}: explicit update/insert/skip confirmation is required.");
                continue;
            }

            var defaultAction = reviewRow.MatchedEmployeeId.HasValue ? "update" : "insert";
            var action = (decision?.Action ?? defaultAction).Trim().ToLowerInvariant();
            if (action == "skip")
            {
                skipRows.Add(reviewRow.RowNumber);
                continue;
            }
            if (action is not ("update" or "insert"))
            {
                errors.Add($"{prefix}: action must be update, insert, or skip.");
                continue;
            }

            if (action == "update")
            {
                var employeeId = decision?.EmployeeId ?? reviewRow.MatchedEmployeeId;
                if (!employeeId.HasValue || employeeId.Value <= 0)
                {
                    errors.Add($"{prefix}: select the existing employee to update.");
                    continue;
                }
                var reviewedCandidates = reviewRow.CandidateEmployees ?? [];
                if (reviewRow.CanResolveConflict && reviewedCandidates.All(candidate => candidate.EmployeeId != employeeId.Value))
                {
                    errors.Add($"{prefix}: selected employee is not one of the reviewed identity candidates.");
                    continue;
                }
                if (!reviewRow.CanResolveConflict && reviewRow.MatchedEmployeeId.HasValue && employeeId.Value != reviewRow.MatchedEmployeeId.Value)
                {
                    errors.Add($"{prefix}: confirmed employee does not match the reviewed employee.");
                    continue;
                }
                var selectedCandidate = reviewedCandidates.FirstOrDefault(candidate => candidate.EmployeeId == employeeId.Value);
                var selectedChanges = selectedCandidate?.Changes ?? reviewRow.Changes;
                var fieldChoices = decision?.FieldChoices is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(decision.FieldChoices, StringComparer.OrdinalIgnoreCase);
                foreach (var change in selectedChanges.Where(change => change.Sensitive || change.PayrollImpact))
                {
                    if (!fieldChoices.TryGetValue(change.Field, out var fieldChoice)
                        || fieldChoice is null
                        || (fieldChoice = fieldChoice.Trim()) is not ("keepExisting" or "useImported"))
                    {
                        errors.Add($"{prefix}: choose keepExisting or useImported for {change.Label}.");
                        continue;
                    }
                    if (fieldChoice == "keepExisting")
                    {
                        BlankImportField(rows[reviewRow.RowNumber - 1], map, change.Field);
                        continue;
                    }
                    if (IsIdentityField(change.Field))
                    {
                        var evidence = (reviewRow.IdentityEvidence ?? []).FirstOrDefault(item => item.Field.Equals(change.Field, StringComparison.OrdinalIgnoreCase));
                        var otherOwners = evidence?.Candidates.Where(candidate => candidate.EmployeeId != employeeId.Value).ToList() ?? [];
                        if (otherOwners.Count > 0)
                            errors.Add($"{prefix}: imported {change.Label} already belongs to {string.Join(", ", otherOwners.Select(candidate => candidate.EmployeeCode))}; choose keepExisting.");
                    }
                }
                EnsureImportHeader(rows, map, "Employee ID");
                EnsureImportHeader(rows, map, "Employee Code");
                SetImportCell(rows[reviewRow.RowNumber - 1], map, "Employee ID", employeeId.Value.ToString());
                SetImportCell(rows[reviewRow.RowNumber - 1], map, "Employee Code", selectedCandidate?.EmployeeCode ?? reviewRow.MatchedEmployeeCode ?? reviewRow.ProposedEmployeeCode);
            }
            else
            {
                if (reviewRow.MatchStatus is "MatchedByEmployeeId" or "MatchedByEmployeeCode" or "MatchedByIdentifiers" or "IdentityConflict")
                {
                    errors.Add($"{prefix}: an exact identifier already belongs to {reviewRow.MatchedEmployeeCode}; it cannot be inserted as another employee.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(reviewRow.ProposedEmployeeCode))
                {
                    errors.Add($"{prefix}: Employee Code is required before inserting.");
                    continue;
                }
            }
        }

        if (errors.Count > 0)
            return (null, new EmployeeImportResult(review.Result.TotalRows, 0, 0, errors, review.Result.ReviewToken, true, review.Result.Rows));
        foreach (var rowNumber in skipRows.OrderByDescending(value => value))
            if (rowNumber > 1 && rowNumber <= rows.Count) rows.RemoveAt(rowNumber - 1);
        return (workbook, null);
    }

    private static Dictionary<string, List<Employee>> BuildIdentityMap(IEnumerable<Employee> employees, Func<Employee, string> selector)
    {
        var result = new Dictionary<string, List<Employee>>(StringComparer.OrdinalIgnoreCase);
        foreach (var employee in employees)
        {
            var key = selector(employee);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!result.TryGetValue(key, out var matches)) result[key] = matches = [];
            matches.Add(employee);
        }
        return result;
    }

    private static List<Employee> LookupIdentity(Dictionary<string, List<Employee>> map, string key) =>
        string.IsNullOrWhiteSpace(key) || !map.TryGetValue(key, out var matches) ? [] : matches;

    private static void AddIdentifierMatches(
        Dictionary<int, (Employee Employee, List<string> Reasons)> candidates,
        IEnumerable<Employee> matches,
        string reason)
    {
        foreach (var employee in matches)
        {
            if (!candidates.TryGetValue(employee.Id, out var candidate))
                candidates[employee.Id] = (employee, [reason]);
            else if (!candidate.Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
                candidate.Reasons.Add(reason);
        }
    }

    private static void AddReviewCandidate(
        Dictionary<int, (Employee Employee, List<string> Reasons)> candidates,
        Employee employee,
        string reason)
    {
        if (!candidates.TryGetValue(employee.Id, out var candidate))
            candidates[employee.Id] = (employee, [reason]);
        else if (!candidate.Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
            candidate.Reasons.Add(reason);
    }

    private static List<EmployeeImportIdentityEvidence> BuildIdentityEvidence(
        List<string> row,
        Dictionary<string, int> headers,
        Dictionary<int, Employee> byId,
        Dictionary<string, List<Employee>> byCode,
        Dictionary<string, List<Employee>> byMobile,
        Dictionary<string, List<Employee>> byAadhaar,
        Dictionary<string, List<Employee>> byPan,
        Dictionary<string, List<Employee>> byBank)
    {
        var evidence = new List<EmployeeImportIdentityEvidence>();

        void Add(string field, string label, string header, bool sensitive, string normalized, IEnumerable<Employee> candidates, Func<Employee, string> existingValue)
        {
            if (!HasHeader(headers, header)) return;
            var uploaded = Cell(row, headers, header);
            if (string.IsNullOrWhiteSpace(uploaded)) return;
            evidence.Add(new EmployeeImportIdentityEvidence(
                field,
                label,
                sensitive ? MaskIdentityValue(field, uploaded) : uploaded.Trim(),
                sensitive,
                candidates.Select(employee => new EmployeeImportEvidenceCandidate(
                    employee.Id,
                    employee.EmployeeCode,
                    $"{employee.FirstName} {employee.LastName}".Trim(),
                    sensitive ? MaskIdentityValue(field, existingValue(employee)) : existingValue(employee))).ToList()));
        }

        var idText = Cell(row, headers, "Employee ID");
        var idCandidates = int.TryParse(idText, out var employeeId) && byId.TryGetValue(employeeId, out var byIdEmployee) ? new[] { byIdEmployee } : [];
        Add("employeeId", "Employee ID", "Employee ID", false, idText, idCandidates, employee => employee.Id.ToString());
        var code = Cell(row, headers, "Employee Code");
        Add("employeeCode", "Employee Code", "Employee Code", false, NormalizeCode(code), LookupIdentity(byCode, NormalizeCode(code)), employee => employee.EmployeeCode);
        var mobile = Cell(row, headers, "Mobile");
        Add("mobile", "Mobile", "Mobile", true, NormalizeMobile(mobile), LookupIdentity(byMobile, NormalizeMobile(mobile)), employee => employee.PersonalDetails?.Mobile ?? "");
        var aadhaar = Cell(row, headers, "Aadhaar");
        Add("aadhaar", "Aadhaar", "Aadhaar", true, NormalizeAadhaar(aadhaar), LookupIdentity(byAadhaar, NormalizeAadhaar(aadhaar)), employee => employee.PersonalDetails?.AadhaarNumber ?? "");
        var pan = Cell(row, headers, "PAN");
        Add("pan", "PAN", "PAN", true, NormalizePan(pan), LookupIdentity(byPan, NormalizePan(pan)), employee => employee.PersonalDetails?.PanNumber ?? "");
        var bank = Cell(row, headers, "Bank Account No");
        Add("bankAccountNo", "Bank Account", "Bank Account No", true, NormalizeBankAccount(bank), LookupIdentity(byBank, NormalizeBankAccount(bank)), employee => employee.PaymentDetails?.BankAccountNo ?? "");
        return evidence;
    }

    private static void TrackWorkbookIdentifier(Dictionary<string, List<int>> values, string label, string normalized, int rowIndex)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return;
        var key = $"{label}:{normalized}";
        if (!values.TryGetValue(key, out var rows)) values[key] = rows = [];
        rows.Add(rowIndex);
    }

    private static List<Employee> FindNameAddressMatches(IEnumerable<Employee> existing, List<string> row, Dictionary<string, int> map)
    {
        var name = NormalizeWords($"{Cell(row, map, "First Name")} {Cell(row, map, "Last Name")}");
        var addresses = new[]
        {
            Cell(row, map, "Address"),
            Cell(row, map, "Correspondence Address"),
            Cell(row, map, "Permanent Address")
        }.Select(NormalizeWords).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(name) || addresses.Count == 0) return [];
        return existing.Where(employee =>
        {
            if (!string.Equals(NormalizeWords($"{employee.FirstName} {employee.LastName}"), name, StringComparison.OrdinalIgnoreCase)) return false;
            var personal = employee.PersonalDetails ?? new EmployeePersonalDetails();
            var employeeAddresses = new[] { personal.Address, personal.CorrespondenceAddress, personal.PermanentAddress }
                .Select(NormalizeWords)
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return employeeAddresses.Any(addresses.Contains);
        }).ToList();
    }

    private static List<EmployeeImportFieldChange> BuildImportChanges(List<string> row, Dictionary<string, int> map, Employee? existing)
    {
        // New rows are initial data capture, not changes requiring per-row approval.
        if (existing is not { } current) return [];
        var personal = current.PersonalDetails ?? new EmployeePersonalDetails();
        var payment = current.PaymentDetails ?? new EmployeePaymentDetails();
        var changes = new List<EmployeeImportFieldChange>();

        void Add(string header, string field, string oldValue, bool sensitive = false, bool payrollImpact = false, Func<string, string>? compare = null)
        {
            if (!HasHeader(map, header)) return;
            var next = Cell(row, map, header);
            if (string.IsNullOrWhiteSpace(next)) return;
            compare ??= value => value.Trim();
            if (string.Equals(compare(oldValue ?? ""), compare(next), StringComparison.OrdinalIgnoreCase)) return;
            changes.Add(new EmployeeImportFieldChange(
                field,
                header,
                sensitive ? MaskIdentityValue(field, oldValue) : oldValue ?? "",
                sensitive ? MaskIdentityValue(field, next) : next,
                sensitive,
                payrollImpact));
        }

        Add("First Name", "firstName", current.FirstName ?? "");
        Add("Last Name", "lastName", current.LastName ?? "");
        Add("Gender", "gender", current.Gender ?? "");
        Add("Date Of Joining", "dateOfJoining", current.DateOfJoining ?? "", payrollImpact: true, compare: NormalizeDate);
        Add("Date Of Birth", "dateOfBirth", personal.DateOfBirth, compare: NormalizeDate);
        Add("Work Email", "workEmail", current.WorkEmail ?? "", sensitive: true);
        Add("Mobile", "mobile", personal.Mobile, sensitive: true, compare: NormalizeMobileForComparison);
        Add("Department", "department", current.Department ?? "");
        Add("Designation", "designation", current.Designation ?? "");
        Add("Grade", "grade", current.Grade ?? "");
        Add("Work Location Id", "workLocationId", current.WorkLocationId <= 0 ? "" : current.WorkLocationId.ToString());
        Add("Reporting Manager User Id", "reportingManagerUserId", current.ReportingManagerUserId?.ToString() ?? "");
        Add("Portal Access", "portalAccess", current.PortalAccess ? "TRUE" : "FALSE", sensitive: true, compare: NormalizeBoolean);
        Add("Active", "active", current.IsActive ? "TRUE" : "FALSE", payrollImpact: true, compare: NormalizeBoolean);
        Add("Salary Template Id", "salaryTemplateId", current.SalaryStructureId ?? "", payrollImpact: true);
        Add("Annual CTC", "annualCtc", current.AnnualCtc.ToString(System.Globalization.CultureInfo.InvariantCulture), payrollImpact: true, compare: NormalizeNumber);
        Add("Salary Json", "salaryJson", current.SalaryJson ?? "{}", payrollImpact: true);
        Add("PAN", "pan", personal.PanNumber, sensitive: true, compare: NormalizePanForComparison);
        Add("Aadhaar", "aadhaar", personal.AadhaarNumber, sensitive: true, compare: NormalizeAadhaarForComparison);
        Add("UAN Number", "uanNumber", personal.UanNumber, sensitive: true, compare: NormalizeAlphaNumeric);
        Add("ESIC Number", "esicNumber", personal.EsicNumber, sensitive: true, compare: NormalizeAlphaNumeric);
        Add("Address", "address", personal.Address);
        Add("Correspondence Address", "correspondenceAddress", personal.CorrespondenceAddress);
        Add("Permanent Address", "permanentAddress", personal.PermanentAddress);
        Add("Bank Name", "bankName", payment.BankName, payrollImpact: true);
        Add("Bank Account No", "bankAccountNo", payment.BankAccountNo, sensitive: true, payrollImpact: true, compare: NormalizeBankForComparison);
        Add("IFSC", "ifsc", payment.IfscCode, sensitive: true, payrollImpact: true, compare: NormalizeAlphaNumeric);
        Add("Payment Mode", "paymentMode", payment.PaymentMode, payrollImpact: true);
        return changes;
    }

    private static bool RowRequiresConfirmation(EmployeeImportPreflightRow row) =>
        row.BlockingReasons.Count > 0
        || row.MatchStatus is "MatchedByIdentifiers" or "ProbableDuplicate"
        || row.Changes.Any(change => change.Sensitive || change.PayrollImpact);

    private static string NormalizeCode(string? value) => (value ?? "").Trim().ToUpperInvariant();
    private static string NormalizeDigits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());
    private static string NormalizeAlphaNumeric(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalizeMobile(string? value)
    {
        var digits = NormalizeDigits(value);
        if (digits.Length < 10 || IsPlaceholder(digits)) return "";
        return digits.Length > 10 ? digits[^10..] : digits;
    }
    private static string NormalizeMobileForComparison(string? value)
    {
        var digits = NormalizeDigits(value);
        return digits.Length > 10 ? digits[^10..] : digits;
    }
    private static string NormalizeAadhaar(string? value)
    {
        var digits = NormalizeDigits(value);
        return digits.Length == 12 && !IsPlaceholder(digits) ? digits : "";
    }
    private static string NormalizePan(string? value)
    {
        var normalized = NormalizeAlphaNumeric(value);
        return Regex.IsMatch(normalized, @"^[A-Z]{5}[0-9]{4}[A-Z]$") ? normalized : "";
    }
    private static string NormalizeBankAccount(string? value)
    {
        var normalized = NormalizeAlphaNumeric(value);
        return normalized.Length >= 6 && !IsPlaceholder(normalized) ? normalized : "";
    }
    private static string NormalizeAadhaarForComparison(string? value) => NormalizeDigits(value);
    private static string NormalizePanForComparison(string? value) => NormalizeAlphaNumeric(value);
    private static string NormalizeBankForComparison(string? value) => NormalizeAlphaNumeric(value);
    private static bool IsPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value is "NA" or "NIL" or "NONE" or "NOTAVAILABLE"
        || value.All(character => character == '0');
    private static string NormalizeWords(string? value) => string.Join(' ', Regex.Matches((value ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value));
    private static string NormalizeBoolean(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "active" or "1" => "true",
            "false" or "no" or "inactive" or "0" => "false",
            _ => (value ?? "").Trim().ToLowerInvariant()
        };
    private static string NormalizeDate(string? value) => TryDate(value ?? "", out var date) ? date : (value ?? "").Trim();
    private static string NormalizeNumber(string? value) =>
        decimal.TryParse((value ?? "").Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : (value ?? "").Trim();
    private static string MaskIdentityValue(string field, string? value)
    {
        var normalized = NormalizeAlphaNumeric(value);
        if (string.IsNullOrWhiteSpace(normalized)) return "";
        if (field.Equals("mobile", StringComparison.OrdinalIgnoreCase))
        {
            var digits = NormalizeMobileForComparison(value);
            if (digits.Length <= 6) return $"••{digits}";
            return $"{digits[..2]}••••{digits[^4..]}";
        }
        if (field.Equals("pan", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Length <= 5) return $"••{normalized}";
            return $"{normalized[..3]}•••••{normalized[^2..]}";
        }
        var visible = normalized.Length <= 4 ? normalized : normalized[^4..];
        return $"••••{visible}";
    }

    private static EmployeeImportWorkbook CloneWorkbook(EmployeeImportWorkbook workbook) =>
        new(workbook.Sheets.ToDictionary(
            sheet => sheet.Key,
            sheet => sheet.Value.Select(row => row.ToList()).ToList(),
            StringComparer.OrdinalIgnoreCase));

    private static string GetEmployeeDataSheetName(EmployeeImportWorkbook workbook)
    {
        foreach (var name in new[] { "Employees", "Employee", "CSV" })
        {
            var exact = workbook.Sheets.Keys.FirstOrDefault(key => string.Equals(Norm(key), Norm(name), StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return workbook.Sheets
            .Where(sheet => !new[] { "references", "reference", "masters", "instructions" }.Contains(Norm(sheet.Key), StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(sheet => sheet.Value.Skip(1).Any(row => !Blank(row))).Key ?? "Employees";
    }

    private static void EnsureImportHeader(List<List<string>> rows, Dictionary<string, int> map, string header)
    {
        if (HasHeader(map, header)) return;
        var index = rows[0].Count;
        rows[0].Add(header);
        map[Norm(header)] = index;
        for (var row = 1; row < rows.Count; row++)
            while (rows[row].Count <= index) rows[row].Add("");
    }

    private static void SetImportCell(List<string> row, Dictionary<string, int> map, string header, string value)
    {
        var index = map[Norm(header)];
        while (row.Count <= index) row.Add("");
        row[index] = value;
    }

    private static void BlankImportField(List<string> row, Dictionary<string, int> map, string field)
    {
        var headers = field switch
        {
            "dateOfJoining" => new[] { "Date Of Joining" },
            "workEmail" => ["Work Email"],
            "portalAccess" => ["Portal Access"],
            "active" => ["Active"],
            "salaryTemplateId" => ["Salary Template Id"],
            "annualCtc" => ["Annual CTC"],
            "salaryJson" => ["Salary Json"],
            "mobile" => ["Mobile"],
            "pan" => ["PAN"],
            "aadhaar" => ["Aadhaar"],
            "uanNumber" => ["UAN Number"],
            "esicNumber" => ["ESIC Number"],
            "bankAccountNo" => ["Bank Account No"],
            "bankName" => ["Bank Name"],
            "ifsc" => ["IFSC"],
            "paymentMode" => ["Payment Mode"],
            _ => Array.Empty<string>()
        };
        foreach (var header in headers)
        {
            if (!map.TryGetValue(Norm(header), out var index)) continue;
            while (row.Count <= index) row.Add("");
            row[index] = "";
        }
    }

    private static bool IsIdentityField(string field) =>
        field is "mobile" or "aadhaar" or "pan" or "bankAccountNo";

    private static void PruneImportReviews()
    {
        var now = DateTime.UtcNow;
        foreach (var review in ImportReviews.Where(item => item.Value.ExpiresAtUtc <= now))
            ImportReviews.TryRemove(review.Key, out _);
    }

    private static void RestoreImportReview(Guid token, EmployeeImportReviewState review)
    {
        if (review.ExpiresAtUtc > DateTime.UtcNow)
            ImportReviews.TryAdd(token, review);
    }

    private static async Task<string> HashFileAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    public async Task<byte[]> BuildImportTemplateAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        await PayrollDataTableStore.EnsureAsync(db);
        var client = await db.QueryFirstOrDefaultAsync<(int Id, string Name)>("SELECT Id, Name FROM clients WHERE Id=@clientId", new { clientId });
        var drops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade') ORDER BY Type, Value", new { clientId })).ToList();
        var locations = (await db.QueryAsync<LocationRef>("SELECT Id, Name, City, State FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE ORDER BY Name", new { clientId })).ToList();
        var templates = ReadSalaryTemplates(await PayrollDataTableStore.GetSetupJsonAsync(db)).Where(template => TemplateForClient(template, clientId)).ToList();
        string First(string type, string fallback) => drops.FirstOrDefault(item => item.Type == type).Value ?? fallback;
        var location = locations.FirstOrDefault();
        var template = templates.FirstOrDefault();
        var managerUsers = (await db.QueryAsync<UserRef>("SELECT Id,DisplayName,Email FROM authusers WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId=@clientId) ORDER BY DisplayName,Email", new { clientId })).ToList();
        var manager = managerUsers.FirstOrDefault();
        var employeeHeaders = new[]
        {
            "Employee Code", "First Name", "Last Name", "Gender", "Date Of Joining", "Date Of Birth", "Work Email", "Mobile",
            "Department", "Designation", "Grade", "Work Location", "Reporting Manager Email", "Portal Access", "Active",
            "Salary Template", "Annual CTC", "PAN", "Aadhaar", "UAN Number", "ESIC Number", "Address", "Correspondence Address",
            "Permanent Address", "Bank Name", "Bank Account No", "IFSC", "Payment Mode", "Change Reason"
        };
        var employeeExample = new[]
        {
            "EMP001", "Rahul", "Sharma", "Male", "2026-04-01", "1995-01-15", "rahul@example.com", "9876543210",
            First("Department", ""), First("Designation", ""), First("Employee Grade", ""), location?.Name ?? "", manager?.Email ?? "",
            "TRUE", "TRUE", template?.Name ?? "", template?.AnnualCtc ?? "600000", "ABCDE1234F", "123412341234", "100200300400", "",
            "Local address", "Correspondence address", "Permanent address", "HDFC Bank", "50100123456789", "HDFC0001234", "Bank Transfer", "Initial upload"
        };
        var instructions = new List<string[]>
        {
            new[] { "Column", "Required", "How to fill", "Validation" },
            new[] { "Employee Code", "Yes", "Unique employee code. Existing code updates that employee.", "No duplicate code in the file." },
            new[] { "First Name", "No", "Employee first name.", "Optional for bulk upload." },
            new[] { "Last Name", "No", "Employee last name.", "" },
            new[] { "Gender", "No", "Male, Female, or Other.", "Must match allowed values." },
            new[] { "Date Of Joining", "No", "Use yyyy-MM-dd, e.g. 2026-04-01.", "Optional. If filled, it must be a valid date." },
            new[] { "Work Email", "No", "Employee office email.", "Cannot already belong to another employee." },
            new[] { "Department / Designation / Grade", "No", "Use text from Dropdown Masters.", "Must match active master data for the client." },
            new[] { "Work Location", "No", "Use the work location name, not ID.", "Must match one active work location for the client." },
            new[] { "Reporting Manager Email", "No", "Use manager login email from Users.", "Must match an active user for the client or global user." },
            new[] { "Salary Template", "No", "Use salary template name, not ID.", "Must match one active salary template for the client." },
            new[] { "Annual CTC", "No", "Numeric amount only.", "If blank, template CTC is used where available." },
            new[] { "Portal Access / Active", "No", "TRUE/FALSE, YES/NO, 1/0, Active/Inactive.", "Defaults are preserved for existing employees." },
            new[] { "Bank / Address / Statutory IDs", "No", "Fill text values directly.", "Stored as provided. Format validation is currently relaxed." }
        };
        var references = new List<string[]> { new[] { "Reference Type", "Id", "Value", "Extra", "Notes" } };
        if (client.Id > 0) references.Add(new[] { "Client", client.Id.ToString(), client.Name, "", "Selected client" });
        references.AddRange(drops.Select(item => new[] { item.Type, "", item.Value, "", "" }));
        references.AddRange(locations.Select(item => new[] { "Work Location", item.Id.ToString(), item.Name, item.City, item.State }));
        references.AddRange(managerUsers.Select(item => new[] { "Manager User", item.Id.ToString(), item.DisplayName, item.Email, "" }));
        references.AddRange(templates.Select(item => new[] { "Salary Template", item.Id, item.Name, item.AnnualCtc, $"ClientId={RefId(item.ClientId)}" }));
        references.AddRange(new[] { new[] { "Gender", "", "Male", "", "" }, new[] { "Gender", "", "Female", "", "" }, new[] { "Gender", "", "Other", "", "" } });
        references.AddRange(new[] { new[] { "Payment Mode", "", "Bank Transfer", "", "" }, new[] { "Payment Mode", "", "Cheque", "", "" }, new[] { "Payment Mode", "", "Cash", "", "" } });
        references.AddRange(new[] { new[] { "Boolean", "", "TRUE", "", "Allowed values: TRUE/FALSE, YES/NO, 1/0, Active/Inactive" }, new[] { "Date Format", "", "yyyy-MM-dd", "", "Example: 2026-04-01" } });
        return BuildXlsx(("Employees", new[] { employeeHeaders, employeeExample }), ("Instructions", instructions), ("References", references));
    }

    async Task<EmployeeImportResult> ImportWorkbookAsync(int clientId, EmployeeImportWorkbook workbook, string importMode, Action<int, int, int>? progress = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureEmployeeInfotypeTablesAsync(db); await PayrollDataTableStore.EnsureAsync(db);
        try
        {
            var totalRows = CountImportRows(workbook);
            if (totalRows == 0) return new EmployeeImportResult(0, 0, 0, ["Import file has no data rows."]);
            var validDrops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade')", new { clientId })).GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.Select(v => v.Value).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var locations = (await db.QueryAsync<LocationRef>("SELECT Id, Name, City, State FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE", new { clientId })).ToList();
            var locationsById = locations.ToDictionary(x => x.Id);
            var locationsByName = locations.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
            var managerUsers = (await db.QueryAsync<UserRef>("SELECT Id,DisplayName,Email FROM authusers WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId=@clientId)", new { clientId })).ToList();
            var managerUsersById = managerUsers.ToDictionary(x => x.Id);
            var managerUsersByEmail = managerUsers.Where(x => !string.IsNullOrWhiteSpace(x.Email)).GroupBy(x => x.Email.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var salaryTemplates = ReadSalaryTemplates(await PayrollDataTableStore.GetSetupJsonAsync(db)).Where(template => TemplateForClient(template, clientId)).ToList();
            var salaryTemplateById = salaryTemplates.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var salaryTemplateByName = salaryTemplates.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
            var existing = (await db.QueryAsync<Employee>("SELECT * FROM employees WHERE ClientId=@clientId", new { clientId })).ToList();
            await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, existing);
            var existingById = existing.ToDictionary(x => x.Id);
            var existingByCode = existing.Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode)).GroupBy(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var existingByEmail = existing.Where(x => !string.IsNullOrWhiteSpace(x.WorkEmail)).GroupBy(x => x.WorkEmail, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var drafts = new Dictionary<string, EmployeeImportDraft>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();

            EmployeeImportDraft? DraftFor(string code, int rowNumber, string sheet, int? employeeId = null)
            {
                code = code.Trim();
                if (importMode == InsertImportMode && employeeId.HasValue)
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee ID cannot be supplied in insert mode; IDs are generated by HRMS.");
                    return null;
                }
                var foundById = employeeId.HasValue ? existingById.GetValueOrDefault(employeeId.Value) : null;
                var foundByCode = !string.IsNullOrWhiteSpace(code) ? existingByCode.GetValueOrDefault(code) : null;

                if (employeeId.HasValue && foundById is null)
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee ID {employeeId.Value} does not belong to this client.");
                    return null;
                }
                if (foundById is not null && !string.IsNullOrWhiteSpace(code) && !string.Equals(foundById.EmployeeCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee ID {employeeId} belongs to employee code \"{foundById.EmployeeCode}\", not \"{code}\".");
                    return null;
                }
                if (foundById is not null && foundByCode is not null && foundById.Id != foundByCode.Id)
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee ID {employeeId} and Employee Code \"{code}\" identify different employees.");
                    return null;
                }

                var matched = foundById ?? foundByCode;
                if (importMode == InsertImportMode && matched is not null)
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee Code \"{matched.EmployeeCode}\" already exists. Insert mode accepts new employees only.");
                    return null;
                }
                if (importMode == UpdateImportMode && matched is null)
                {
                    errors.Add($"{sheet} row {rowNumber}: Employee \"{code}\" was not found. Update mode accepts existing employees only.");
                    return null;
                }

                var draftKey = matched is not null ? $"id:{matched.Id}" : $"code:{code}";
                if (drafts.TryGetValue(draftKey, out var existingDraft)) return existingDraft;
                var employee = matched is not null ? CloneEmployee(matched) : new Employee { ClientId = clientId, EmployeeCode = code, IsActive = true, SalaryJson = "{}", PersonalJson = "{}", PaymentJson = "{}", PersonalDetails = new EmployeePersonalDetails(), PaymentDetails = new EmployeePaymentDetails() };
                var draft = new EmployeeImportDraft(employee, rowNumber);
                drafts[draftKey] = draft;
                return draft;
            }

            void Mark(EmployeeImportDraft draft, string infotypeCode, string reason)
            {
                draft.Infotypes.Add(infotypeCode);
                if (!string.IsNullOrWhiteSpace(reason)) draft.ChangeReason = reason.Trim();
            }

            bool AddCodeError(string sheet, int rowNumber, string code, HashSet<string> seen)
            {
                if (string.IsNullOrWhiteSpace(code)) { errors.Add($"{sheet} row {rowNumber}: Employee Code is required."); return true; }
                if (!seen.Add(code)) { errors.Add($"{sheet} row {rowNumber}: Employee Code \"{code}\" is duplicated in this sheet."); return true; }
                return false;
            }

            void ProcessOrgRows(List<List<string>> rows, string sheet)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                var writesActions = HasAnyHeader(map, It0000ImportHeaders);
                var writesOrg = HasAnyHeader(map, It0001ImportHeaders);
                if (!writesActions && !writesOrg) { errors.Add($"{sheet}: no supported organizational or action headers were supplied."); return; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    if (AddCodeError(sheet, rowNumber, code, seen)) continue;
                    var draft = DraftFor(code, rowNumber, sheet); if (draft is null) continue;
                    var employee = draft.Employee;
                    var reason = Cell(row, map, "Change Reason");
                    if (writesActions)
                    {
                        if (HasHeader(map, "Date Of Joining"))
                        {
                            if (TryDate(Cell(row, map, "Date Of Joining"), out var doj)) { if (!string.IsNullOrWhiteSpace(doj)) employee.DateOfJoining = doj; }
                            else errors.Add($"{sheet} row {rowNumber}: Date Of Joining must be yyyy-MM-dd.");
                        }
                        if (HasHeader(map, "Active"))
                        {
                            if (TryBool(Cell(row, map, "Active"), employee.IsActive, out var active)) employee.IsActive = active;
                            else errors.Add($"{sheet} row {rowNumber}: Active must be TRUE/FALSE.");
                        }
                        Mark(draft, "0000", reason);
                    }
                    if (writesOrg)
                    {
                        if (HasHeader(map, "Work Email"))
                        {
                            var email = Cell(row, map, "Work Email");
                            if (!string.IsNullOrWhiteSpace(email))
                            {
                                if (!EmailOk(email)) errors.Add($"{sheet} row {rowNumber}: Work Email is not a valid email address.");
                                else
                                {
                                    employee.WorkEmail = email;
                                    if (existingByEmail.TryGetValue(email, out var emailOwner) && emailOwner.Id != employee.Id) errors.Add($"{sheet} row {rowNumber}: Work Email already belongs to employee {emailOwner.EmployeeCode}.");
                                }
                            }
                        }
                        if (HasHeader(map, "Department")) { var value = Cell(row, map, "Department"); ValidateMaster("Department", value, validDrops, errors, rowNumber, "Department", sheet); SetIfAny(next => employee.Department = next, value); }
                        if (HasHeader(map, "Designation")) { var value = Cell(row, map, "Designation"); ValidateMaster("Designation", value, validDrops, errors, rowNumber, "Designation", sheet); SetIfAny(next => employee.Designation = next, value); }
                        if (HasHeader(map, "Grade")) { var value = Cell(row, map, "Grade"); ValidateMaster("Employee Grade", value, validDrops, errors, rowNumber, "Grade", sheet); SetIfAny(next => employee.Grade = next, value); }
                        if (HasAnyHeader(map, "Work Location Id", "Work Location"))
                        {
                            var locationId = ResolveWorkLocationId(Cell(row, map, "Work Location Id"), Cell(row, map, "Work Location"), locationsById, locationsByName, errors, sheet, rowNumber);
                            if (locationId.HasValue) employee.WorkLocationId = locationId.Value;
                        }
                        if (HasAnyHeader(map, "Reporting Manager User Id", "Reporting Manager Email"))
                        {
                            var managerUserId = ResolveManagerUserId(Cell(row, map, "Reporting Manager User Id"), Cell(row, map, "Reporting Manager Email"), managerUsersById, managerUsersByEmail, errors, sheet, rowNumber);
                            if (managerUserId.HasValue) employee.ReportingManagerUserId = managerUserId.Value;
                        }
                        if (HasHeader(map, "Portal Access"))
                        {
                            if (TryBool(Cell(row, map, "Portal Access"), employee.PortalAccess, out var portal)) employee.PortalAccess = portal;
                            else errors.Add($"{sheet} row {rowNumber}: Portal Access must be TRUE/FALSE.");
                        }
                        Mark(draft, "0001", reason);
                    }
                }
            }

            void ProcessPersonalRows(List<List<string>> rows, string sheet)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                if (!HasAnyHeader(map, It0002ImportHeaders)) { errors.Add($"{sheet}: no supported personal-data headers were supplied."); return; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    if (AddCodeError(sheet, rowNumber, code, seen)) continue;
                    var draft = DraftFor(code, rowNumber, sheet); if (draft is null) continue;
                    var employee = draft.Employee; var personal = employee.PersonalDetails ?? new EmployeePersonalDetails(); employee.PersonalDetails = personal;
                    if (HasHeader(map, "First Name")) SetIfAny(value => employee.FirstName = value, Cell(row, map, "First Name"));
                    if (HasHeader(map, "Last Name")) SetIfAny(value => employee.LastName = value, Cell(row, map, "Last Name"));
                    if (HasHeader(map, "Gender")) { var gender = Cell(row, map, "Gender"); if (!string.IsNullOrWhiteSpace(gender)) { if (!new[] { "Male", "Female", "Other" }.Contains(gender, StringComparer.OrdinalIgnoreCase)) errors.Add($"{sheet} row {rowNumber}: Gender must be Male, Female, or Other."); else employee.Gender = gender; } }
                    if (HasHeader(map, "Date Of Birth")) { if (TryDate(Cell(row, map, "Date Of Birth"), out var dob)) { if (!string.IsNullOrWhiteSpace(dob)) personal.DateOfBirth = dob; } else errors.Add($"{sheet} row {rowNumber}: Date Of Birth must be yyyy-MM-dd."); }
                    SetIfAny(value => personal.Mobile = value, Cell(row, map, "Mobile"));
                    SetIfAny(value => personal.PanNumber = value, Cell(row, map, "PAN"));
                    SetIfAny(value => personal.AadhaarNumber = value, Cell(row, map, "Aadhaar"));
                    SetIfAny(value => personal.UanNumber = value, Cell(row, map, "UAN Number"));
                    SetIfAny(value => personal.EsicNumber = value, Cell(row, map, "ESIC Number"));
                    Mark(draft, "0002", Cell(row, map, "Change Reason"));
                }
            }

            void ProcessAddressRows(List<List<string>> rows, string sheet)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                if (!HasAnyHeader(map, It0006ImportHeaders)) { errors.Add($"{sheet}: no supported address headers were supplied."); return; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    if (AddCodeError(sheet, rowNumber, code, seen)) continue;
                    var draft = DraftFor(code, rowNumber, sheet); if (draft is null) continue;
                    var personal = draft.Employee.PersonalDetails ?? new EmployeePersonalDetails(); draft.Employee.PersonalDetails = personal;
                    SetIfAny(value => personal.Address = value, Cell(row, map, "Address"));
                    SetIfAny(value => personal.CorrespondenceAddress = value, Cell(row, map, "Correspondence Address"));
                    SetIfAny(value => personal.PermanentAddress = value, Cell(row, map, "Permanent Address"));
                    Mark(draft, "0006", Cell(row, map, "Change Reason"));
                }
            }

            void ProcessPayRows(List<List<string>> rows, string sheet)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                if (!HasAnyHeader(map, It0008ImportHeaders)) { errors.Add($"{sheet}: no supported basic-pay headers were supplied."); return; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    if (AddCodeError(sheet, rowNumber, code, seen)) continue;
                    var draft = DraftFor(code, rowNumber, sheet); if (draft is null) continue;
                    var employee = draft.Employee;
                    var template = HasAnyHeader(map, "Salary Template Id", "Salary Template")
                        ? ResolveSalaryTemplate(Cell(row, map, "Salary Template Id"), Cell(row, map, "Salary Template"), salaryTemplateById, salaryTemplateByName, errors, sheet, rowNumber)
                        : null;
                    if (template is not null) employee.SalaryStructureId = template.Id;
                    if (HasHeader(map, "Annual CTC"))
                    {
                        var ctcText = Cell(row, map, "Annual CTC");
                        if (!string.IsNullOrWhiteSpace(ctcText))
                        {
                            if (decimal.TryParse(ctcText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ctc)) employee.AnnualCtc = ctc;
                            else errors.Add($"{sheet} row {rowNumber}: Annual CTC must be numeric.");
                        }
                        else if (template is not null && decimal.TryParse(template.AnnualCtc, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var templateCtc) && employee.AnnualCtc <= 0) employee.AnnualCtc = templateCtc;
                    }
                    if (HasHeader(map, "Salary Json"))
                    {
                        var salaryJson = Cell(row, map, "Salary Json");
                        if (!string.IsNullOrWhiteSpace(salaryJson))
                        {
                            if (JsonObjectOk(salaryJson)) employee.SalaryJson = salaryJson;
                            else errors.Add($"{sheet} row {rowNumber}: Salary Json must be a valid JSON object.");
                        }
                    }
                    Mark(draft, "0008", Cell(row, map, "Change Reason"));
                }
            }

            void ProcessBankRows(List<List<string>> rows, string sheet)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                if (!HasAnyHeader(map, It0009ImportHeaders)) { errors.Add($"{sheet}: no supported bank-detail headers were supplied."); return; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    if (AddCodeError(sheet, rowNumber, code, seen)) continue;
                    var draft = DraftFor(code, rowNumber, sheet); if (draft is null) continue;
                    var payment = draft.Employee.PaymentDetails ?? new EmployeePaymentDetails(); draft.Employee.PaymentDetails = payment;
                    SetIfAny(value => payment.BankName = value, Cell(row, map, "Bank Name"));
                    SetIfAny(value => payment.BankAccountNo = value, Cell(row, map, "Bank Account No"));
                    SetIfAny(value => payment.IfscCode = value, Cell(row, map, "IFSC"));
                    var mode = Cell(row, map, "Payment Mode"); if (!string.IsNullOrWhiteSpace(mode)) { if (!new[] { "Bank Transfer", "Cheque", "Cash" }.Contains(mode, StringComparer.OrdinalIgnoreCase)) errors.Add($"{sheet} row {rowNumber}: Payment Mode must be Bank Transfer, Cheque, or Cash."); else payment.PaymentMode = mode; }
                    Mark(draft, "0009", Cell(row, map, "Change Reason"));
                }
            }

            void ProcessFlatRows(List<List<string>> rows)
            {
                if (rows.Count < 2) return;
                var map = HeaderMap(rows[0]);
                var writesActions = HasAnyHeader(map, It0000ImportHeaders);
                var writesOrg = HasAnyHeader(map, It0001ImportHeaders);
                var writesPersonal = HasAnyHeader(map, It0002ImportHeaders);
                var writesAddress = HasAnyHeader(map, It0006ImportHeaders);
                var writesPay = HasAnyHeader(map, It0008ImportHeaders);
                var writesBank = HasAnyHeader(map, It0009ImportHeaders);
                if (!writesActions && !writesOrg && !writesPersonal && !writesAddress && !writesPay && !writesBank) { errors.Add("Employees: no supported employee field headers were supplied."); return; }
                var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenIds = new HashSet<int>();
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i]; if (Blank(row)) continue;
                    var rowNumber = i + 1; var code = Cell(row, map, "Employee Code");
                    var employeeIdText = Cell(row, map, "Employee ID");
                    int? employeeId = null;
                    if (!string.IsNullOrWhiteSpace(employeeIdText))
                    {
                        if (!int.TryParse(employeeIdText, out var parsedEmployeeId) || parsedEmployeeId <= 0) { errors.Add($"Employees row {rowNumber}: Employee ID must be a positive whole number."); continue; }
                        employeeId = parsedEmployeeId;
                        if (!seenIds.Add(parsedEmployeeId)) { errors.Add($"Employees row {rowNumber}: Employee ID {parsedEmployeeId} is duplicated in this sheet."); continue; }
                    }
                    if (string.IsNullOrWhiteSpace(code) && !employeeId.HasValue) { errors.Add($"Employees row {rowNumber}: Employee Code or Employee ID is required."); continue; }
                    if (!string.IsNullOrWhiteSpace(code) && !seenCodes.Add(code)) { errors.Add($"Employees row {rowNumber}: Employee Code \"{code}\" is duplicated in this sheet."); continue; }
                    var draft = DraftFor(code, rowNumber, "Employees", employeeId); if (draft is null) continue;
                    var employee = draft.Employee;
                    var personal = employee.PersonalDetails ?? new EmployeePersonalDetails(); employee.PersonalDetails = personal;
                    var reason = Cell(row, map, "Change Reason");

                    if (writesActions)
                    {
                        if (HasHeader(map, "Date Of Joining")) { if (TryDate(Cell(row, map, "Date Of Joining"), out var doj)) { if (!string.IsNullOrWhiteSpace(doj)) employee.DateOfJoining = doj; } else errors.Add($"Employees row {rowNumber}: Date Of Joining must be yyyy-MM-dd."); }
                        if (HasHeader(map, "Active")) { if (TryBool(Cell(row, map, "Active"), employee.IsActive, out var active)) employee.IsActive = active; else errors.Add($"Employees row {rowNumber}: Active must be TRUE/FALSE."); }
                        Mark(draft, "0000", reason);
                    }
                    if (writesOrg)
                    {
                        if (HasHeader(map, "Work Email")) { var email = Cell(row, map, "Work Email"); if (!string.IsNullOrWhiteSpace(email)) { if (!EmailOk(email)) errors.Add($"Employees row {rowNumber}: Work Email is not a valid email address."); else { employee.WorkEmail = email; if (existingByEmail.TryGetValue(email, out var emailOwner) && emailOwner.Id != employee.Id) errors.Add($"Employees row {rowNumber}: Work Email already belongs to employee {emailOwner.EmployeeCode}."); } } }
                        if (HasHeader(map, "Department")) { var value = Cell(row, map, "Department"); ValidateMaster("Department", value, validDrops, errors, rowNumber, "Department", "Employees"); SetIfAny(next => employee.Department = next, value); }
                        if (HasHeader(map, "Designation")) { var value = Cell(row, map, "Designation"); ValidateMaster("Designation", value, validDrops, errors, rowNumber, "Designation", "Employees"); SetIfAny(next => employee.Designation = next, value); }
                        if (HasHeader(map, "Grade")) { var value = Cell(row, map, "Grade"); ValidateMaster("Employee Grade", value, validDrops, errors, rowNumber, "Grade", "Employees"); SetIfAny(next => employee.Grade = next, value); }
                        if (HasAnyHeader(map, "Work Location Id", "Work Location")) { var locationId = ResolveWorkLocationId(Cell(row, map, "Work Location Id"), Cell(row, map, "Work Location"), locationsById, locationsByName, errors, "Employees", rowNumber); if (locationId.HasValue) employee.WorkLocationId = locationId.Value; }
                        if (HasAnyHeader(map, "Reporting Manager User Id", "Reporting Manager Email")) { var managerUserId = ResolveManagerUserId(Cell(row, map, "Reporting Manager User Id"), Cell(row, map, "Reporting Manager Email"), managerUsersById, managerUsersByEmail, errors, "Employees", rowNumber); if (managerUserId.HasValue) employee.ReportingManagerUserId = managerUserId.Value; }
                        if (HasHeader(map, "Portal Access")) { if (TryBool(Cell(row, map, "Portal Access"), employee.PortalAccess, out var portal)) employee.PortalAccess = portal; else errors.Add($"Employees row {rowNumber}: Portal Access must be TRUE/FALSE."); }
                        Mark(draft, "0001", reason);
                    }
                    if (writesPersonal)
                    {
                        if (HasHeader(map, "First Name")) SetIfAny(value => employee.FirstName = value, Cell(row, map, "First Name"));
                        if (HasHeader(map, "Last Name")) SetIfAny(value => employee.LastName = value, Cell(row, map, "Last Name"));
                        if (HasHeader(map, "Gender")) { var gender = Cell(row, map, "Gender"); if (!string.IsNullOrWhiteSpace(gender)) { if (!new[] { "Male", "Female", "Other" }.Contains(gender, StringComparer.OrdinalIgnoreCase)) errors.Add($"Employees row {rowNumber}: Gender must be Male, Female, or Other."); else employee.Gender = gender; } }
                        if (HasHeader(map, "Date Of Birth")) { if (TryDate(Cell(row, map, "Date Of Birth"), out var dob)) { if (!string.IsNullOrWhiteSpace(dob)) personal.DateOfBirth = dob; } else errors.Add($"Employees row {rowNumber}: Date Of Birth must be yyyy-MM-dd."); }
                        SetIfAny(value => personal.Mobile = value, Cell(row, map, "Mobile"));
                        SetIfAny(value => personal.PanNumber = value, Cell(row, map, "PAN"));
                        SetIfAny(value => personal.AadhaarNumber = value, Cell(row, map, "Aadhaar"));
                        SetIfAny(value => personal.UanNumber = value, Cell(row, map, "UAN Number"));
                        SetIfAny(value => personal.EsicNumber = value, Cell(row, map, "ESIC Number"));
                        Mark(draft, "0002", reason);
                    }
                    if (writesAddress)
                    {
                        SetIfAny(value => personal.Address = value, Cell(row, map, "Address"));
                        SetIfAny(value => personal.CorrespondenceAddress = value, Cell(row, map, "Correspondence Address"));
                        SetIfAny(value => personal.PermanentAddress = value, Cell(row, map, "Permanent Address"));
                        Mark(draft, "0006", reason);
                    }
                    if (writesPay)
                    {
                        var template = HasAnyHeader(map, "Salary Template Id", "Salary Template") ? ResolveSalaryTemplate(Cell(row, map, "Salary Template Id"), Cell(row, map, "Salary Template"), salaryTemplateById, salaryTemplateByName, errors, "Employees", rowNumber) : null;
                        if (template is not null) employee.SalaryStructureId = template.Id;
                        if (HasHeader(map, "Annual CTC")) { var ctcText = Cell(row, map, "Annual CTC"); if (!string.IsNullOrWhiteSpace(ctcText)) { if (decimal.TryParse(ctcText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ctc)) employee.AnnualCtc = ctc; else errors.Add($"Employees row {rowNumber}: Annual CTC must be numeric."); } else if (template is not null && decimal.TryParse(template.AnnualCtc, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var templateCtc) && employee.AnnualCtc <= 0) employee.AnnualCtc = templateCtc; }
                        if (HasHeader(map, "Salary Json")) { var salaryJson = Cell(row, map, "Salary Json"); if (!string.IsNullOrWhiteSpace(salaryJson)) { if (JsonObjectOk(salaryJson)) employee.SalaryJson = salaryJson; else errors.Add($"Employees row {rowNumber}: Salary Json must be a valid JSON object."); } }
                        Mark(draft, "0008", reason);
                    }
                    if (writesBank)
                    {
                        var payment = employee.PaymentDetails ?? new EmployeePaymentDetails(); employee.PaymentDetails = payment;
                        SetIfAny(value => payment.BankName = value, Cell(row, map, "Bank Name"));
                        SetIfAny(value => payment.BankAccountNo = value, Cell(row, map, "Bank Account No"));
                        SetIfAny(value => payment.IfscCode = value, Cell(row, map, "IFSC"));
                        if (HasHeader(map, "Payment Mode")) { var paymentMode = Cell(row, map, "Payment Mode"); if (!string.IsNullOrWhiteSpace(paymentMode)) { if (!new[] { "Bank Transfer", "Cheque", "Cash" }.Contains(paymentMode, StringComparer.OrdinalIgnoreCase)) errors.Add($"Employees row {rowNumber}: Payment Mode must be Bank Transfer, Cheque, or Cash."); else payment.PaymentMode = paymentMode; } }
                        Mark(draft, "0009", reason);
                    }
                }
            }

            var hasInfotypeSheets = HasDataSheet(workbook, "0001 Org Assignment", "0002 Personal Data", "0006 Addresses", "0008 Basic Pay", "0009 Bank Details");
            if (hasInfotypeSheets)
            {
                ProcessOrgRows(GetSheet(workbook, "0001 Org Assignment"), "0001 Org Assignment");
                ProcessPersonalRows(GetSheet(workbook, "0002 Personal Data"), "0002 Personal Data");
                ProcessAddressRows(GetSheet(workbook, "0006 Addresses"), "0006 Addresses");
                ProcessPayRows(GetSheet(workbook, "0008 Basic Pay"), "0008 Basic Pay");
                ProcessBankRows(GetSheet(workbook, "0009 Bank Details"), "0009 Bank Details");
            }
            else ProcessFlatRows(GetEmployeeDataSheet(workbook));

            foreach (var draft in drafts.Values)
            {
                if (string.IsNullOrWhiteSpace(draft.Employee.FirstName)) draft.Employee.FirstName = draft.Employee.EmployeeCode;
            }
            if (errors.Count > 0) return new EmployeeImportResult(totalRows, 0, 0, errors);

            var inserted = 0; var updated = 0; var completed = 0;
            var savedEmployees = new List<Employee>();
            foreach (var draft in drafts.Values.OrderBy(x => x.FirstRow))
            {
                var isNew = draft.Employee.Id == 0;
                if (isNew && draft.Employee.IsActive && !string.IsNullOrWhiteSpace(draft.Employee.EmployeeCode))
                    draft.Employee.PortalAccess = true;
                await SaveWithOpenConnectionAsync(db, draft.Employee, "Bulk Upload", isNew ? null : string.Join(',', draft.Infotypes.OrderBy(x => x)), draft.ChangeReason);
                if (draft.Employee.IsActive && draft.Employee.PortalAccess) savedEmployees.Add(draft.Employee);
                if (isNew) inserted++; else updated++;
                completed++;
                if (completed == drafts.Count || completed % 25 == 0) progress?.Invoke(Math.Min(totalRows, completed), inserted, updated);
            }
            if (savedEmployees.Count > 0) _ = Task.Run(() => ProvisionAndQueueWelcomeBatchAsync(savedEmployees));
            return new EmployeeImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex) { return new EmployeeImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]); }
    }

    static void SetJob(Guid jobId, Func<EmployeeImportJobStatus, EmployeeImportJobStatus> update) => ImportJobs.AddOrUpdate(jobId, _ => update(new EmployeeImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));

    async Task ProvisionAndQueueWelcomeAsync(Employee employee)
    {
        var provision = await authRepository.EnsureEmployeeLoginAsync(employee.Id);
        if (string.IsNullOrWhiteSpace(provision.TemporaryPassword) || provision.UserId is null || string.IsNullOrWhiteSpace(provision.NotificationEmail) || !EmailOk(provision.NotificationEmail)) return;
        var portalUrl = configuration["EssPortal:Url"] ?? configuration["AppUrls:EssPortal"] ?? "http://localhost:5174";
        await notificationRepository.PublishEventAsync(new NotificationEvent
        {
            EventCode = "ESS.WELCOME",
            ResourceType = "Employee",
            ResourceId = employee.Id.ToString(),
            ClientId = employee.ClientId,
            ActorName = "Bulk Upload",
            ActorEmail = "bulk-upload@system.local",
            PayloadJson = JsonSerializer.Serialize(new
            {
                employeeId = employee.Id,
                employeeCode = employee.EmployeeCode,
                employeeName = string.IsNullOrWhiteSpace(provision.EmployeeName) ? $"{employee.FirstName} {employee.LastName}".Trim() : provision.EmployeeName,
                employeeEmail = provision.NotificationEmail,
                loginId = provision.Email,
                temporaryPassword = provision.TemporaryPassword,
                essPortalUrl = portalUrl,
                loginUrl = portalUrl,
                mustChangePassword = true
            })
        });
    }

    async Task ProvisionAndQueueWelcomeBatchAsync(IEnumerable<Employee> employees)
    {
        foreach (var employee in employees)
        {
            try { await ProvisionAndQueueWelcomeAsync(employee); }
            catch { }
        }
    }
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
SELECT t.Id,t.EmployeeId,t.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,'0001' InfotypeCode,'Organizational Assignment' InfotypeName,t.ActionType,t.EffectiveFrom,t.EffectiveTo,t.Status,JSON_OBJECT('ClientId',t.ClientId,'Department',t.Department,'Designation',t.Designation,'Grade',t.Grade,'WorkLocationId',t.WorkLocationId,'ReportingManagerId',t.ReportingManagerId,'ReportingManagerUserId',t.ReportingManagerUserId,'ReportingManagerUser',COALESCE(u.DisplayName,''),'ReportingManagerEmail',COALESCE(u.Email,''),'WorkEmail',t.WorkEmail,'PortalAccess',t.PortalAccess) DataJson,t.ChangeReason,t.CreatedBy,t.CreatedAt
FROM employee_it0001_org_assignment t JOIN employees e ON e.Id=t.EmployeeId LEFT JOIN authusers u ON u.Id=t.ReportingManagerUserId WHERE {where}
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
    ReportingManagerUserId INT NULL,
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
        await EnsureTableColumnAsync(db, "employees", "ReportingManagerUserId", "INT NULL AFTER ReportingManagerId");
        await EnsureTableColumnAsync(db, "employee_it0001_org_assignment", "ReportingManagerUserId", "INT NULL AFTER ReportingManagerId");
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
        WorkLocationId = row.WorkLocationId, ReportingManagerId = row.ReportingManagerId, ReportingManagerUserId = row.ReportingManagerUserId, PortalAccess = row.PortalAccess, SalaryStructureId = row.SalaryStructureId,
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

    static async Task SyncAttendancePolicyMappingsAsync(MySqlConnection db, Employee employee, Employee? before)
    {
        var clientIds = new[] { employee.ClientId, before?.ClientId ?? employee.ClientId }.Where(id => id > 0).Distinct().ToArray();
        foreach (var clientId in clientIds)
        {
            await db.ExecuteAsync(@"DELETE age FROM attendance_group_employees age
JOIN attendance_groups g ON g.id=age.attendance_group_id
WHERE age.employee_id=@EmployeeId AND g.client_id=@ClientId", new { EmployeeId = employee.Id, ClientId = clientId });
        }

        if (!employee.IsActive || employee.Id <= 0 || employee.ClientId <= 0 || employee.WorkLocationId <= 0 || string.IsNullOrWhiteSpace(employee.Department) || string.IsNullOrWhiteSpace(employee.Designation))
            return;

        await db.ExecuteAsync(@"INSERT IGNORE INTO attendance_group_employees (attendance_group_id, employee_id)
SELECT g.id, @EmployeeId
FROM attendance_groups g
WHERE g.client_id=@ClientId
  AND (g.work_location_id=0 OR g.work_location_id=@WorkLocationId)
  AND (g.department='' OR g.department='All' OR g.department=@Department)
  AND (g.designation='' OR g.designation='All' OR g.designation=@Designation)
  AND g.is_active=TRUE", new
        {
            EmployeeId = employee.Id,
            employee.ClientId,
            employee.WorkLocationId,
            Department = employee.Department.Trim(),
            Designation = employee.Designation.Trim()
        });
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
                await db.ExecuteAsync(@"INSERT INTO employee_it0001_org_assignment (EmployeeId,ClientId,ActionType,EffectiveFrom,Status,Department,Designation,Grade,WorkLocationId,ReportingManagerId,ReportingManagerUserId,WorkEmail,PortalAccess,ChangeReason,CreatedBy)
VALUES (@EmployeeId,@ClientId,@ActionType,@EffectiveFrom,'Active',@Department,@Designation,@Grade,@WorkLocationId,@ReportingManagerId,@ReportingManagerUserId,@WorkEmail,@PortalAccess,@ChangeReason,@CreatedBy)", new { meta.EmployeeId, meta.ClientId, meta.ActionType, meta.EffectiveFrom, employee.Department, employee.Designation, employee.Grade, employee.WorkLocationId, employee.ReportingManagerId, employee.ReportingManagerUserId, employee.WorkEmail, employee.PortalAccess, meta.ChangeReason, meta.CreatedBy });
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
        yield return ("0001", "Organizational Assignment", JsonSerializer.Serialize(new { employee.ClientId, employee.Department, employee.Designation, employee.Grade, employee.WorkLocationId, employee.ReportingManagerId, employee.ReportingManagerUserId, employee.WorkEmail }));
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
        ["0001:ReportingManagerUserId"] = (employee.ReportingManagerUserId ?? 0).ToString(),
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
    static async Task EnsureTableColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column", new { table, column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }
    static void ValidateMaster(string type, string value, Dictionary<string, HashSet<string>> masters, List<string> errors, int row, string label, string sheet) { if (!string.IsNullOrWhiteSpace(value) && (!masters.TryGetValue(type, out var values) || !values.Contains(value))) errors.Add($"{sheet} row {row}: {label} \"{value}\" is not in Dropdown Masters."); }
    static bool DateOk(string value) => TryDate(value, out _);
    static string? DbDate(string value) => TryDate(value, out var date) && !string.IsNullOrWhiteSpace(date) ? date : null;
    static string Norm(string? value) => (value ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
    static bool HasHeader(Dictionary<string, int> header, string name) => header.ContainsKey(Norm(name));
    static bool HasAnyHeader(Dictionary<string, int> header, params string[] names) => names.Any(name => HasHeader(header, name));
    static bool TryNormalizeImportMode(string? mode, out string normalized, out string error)
    {
        var key = Norm(string.IsNullOrWhiteSpace(mode) ? UpsertImportMode : mode.Trim());
        normalized = key switch
        {
            "insert" or "insertnew" or "insertonly" or "add" or "addnew" or "create" => InsertImportMode,
            "update" or "updateexisting" or "updateonly" => UpdateImportMode,
            "upsert" or "addorupdate" or "auto" or "automatic" => UpsertImportMode,
            _ => ""
        };
        error = string.IsNullOrWhiteSpace(normalized)
            ? $"Import mode \"{mode}\" is invalid. Use insert, update, or upsert."
            : "";
        return !string.IsNullOrWhiteSpace(normalized);
    }
    static async Task<EmployeeImportWorkbook> ParseImportWorkbookAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var isXlsx = file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || (bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04);
        return isXlsx ? ParseXlsxWorkbook(bytes) : new EmployeeImportWorkbook(new Dictionary<string, List<List<string>>>(StringComparer.OrdinalIgnoreCase) { ["Employees"] = ParseCsv(Encoding.UTF8.GetString(bytes)) });
    }
    static List<List<string>> ParseCsv(string text) { var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false; for (var i = 0; i < text.Length; i++) { var c = text[i]; if (q && c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else if (c == '"') q = !q; else if (!q && c == ',') { row.Add(cell.ToString()); cell.Clear(); } else if (!q && (c == '\n' || c == '\r')) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; } else cell.Append(c); } row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row); return rows; }
    static EmployeeImportWorkbook ParseXlsxWorkbook(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var sheetRefs = WorkbookSheets(zip).ToList();
        if (sheetRefs.Count == 0) sheetRefs = zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName).Select((entry, index) => new SheetRef($"Sheet {index + 1}", entry.FullName)).ToList();
        var sheets = new Dictionary<string, List<List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetRef in sheetRefs)
        {
            var entry = zip.GetEntry(sheetRef.Path);
            if (entry is not null) sheets[sheetRef.Name] = ParseXlsxSheet(entry, shared);
        }
        return new EmployeeImportWorkbook(sheets);
    }

    static IEnumerable<SheetRef> WorkbookSheets(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry is null) yield break;
        var rels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relEntry is not null)
        {
            using var relStream = relEntry.Open();
            var relDoc = XDocument.Load(relStream);
            foreach (var rel in relDoc.Descendants().Where(x => x.Name.LocalName == "Relationship"))
            {
                var id = (string?)rel.Attribute("Id") ?? "";
                var target = (string?)rel.Attribute("Target") ?? "";
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target)) rels[id] = target;
            }
        }
        using var stream = workbookEntry.Open();
        var doc = XDocument.Load(stream);
        var index = 0;
        foreach (var sheet in doc.Descendants().Where(x => x.Name.LocalName == "sheet"))
        {
            index++;
            var name = (string?)sheet.Attribute("name") ?? $"Sheet {index}";
            var relId = (string?)sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id") ?? $"rId{index}";
            var target = rels.GetValueOrDefault(relId, $"worksheets/sheet{index}.xml");
            var path = target.StartsWith('/') ? target.TrimStart('/') : target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : $"xl/{target}";
            yield return new SheetRef(name, path);
        }
    }

    static List<List<string>> ParseXlsxSheet(ZipArchiveEntry sheet, List<string> shared)
    {
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var index = CellIndex((string?)cell.Attribute("r") ?? "A1");
                while (values.Count < index) values.Add("");
                var type = (string?)cell.Attribute("t") ?? "";
                var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? "";
                values.Add(type == "s" && int.TryParse(raw, out var si) && si >= 0 && si < shared.Count ? shared[si] : raw);
            }
            rows.Add(values);
        }
        return rows;
    }
    static List<string> ReadSharedStrings(ZipArchive zip) { var entry = zip.GetEntry("xl/sharedStrings.xml"); if (entry is null) return []; using var stream = entry.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; return doc.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList(); }
    static int CellIndex(string reference) { var n = 0; foreach (var c in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(c) - 'A' + 1; return Math.Max(0, n - 1); }
    static byte[] BuildXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", ContentTypesXml(sheets.Length));
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml(sheets.Length));
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>""");
            Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((s, i) => (SafeSheetName(s.Name), i + 1))));
            foreach (var (sheet, ix) in sheets.Select((s, i) => (s, i + 1))) Add(zip, $"xl/worksheets/sheet{ix}.xml", SheetXml(sheet.Rows));
        }
        return ms.ToArray();
    }
    static void Add(ZipArchive zip, string path, string text) { var entry = zip.CreateEntry(path); using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(text); }
    static string ContentTypesXml(int sheetCount) => $"""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{string.Concat(Enumerable.Range(1, sheetCount).Select(i => $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""))}</Types>""";
    static string WorkbookRelsXml(int sheetCount) => $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Concat(Enumerable.Range(1, sheetCount).Select(i => $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>"""))}<Relationship Id="rId{sheetCount + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""";
    static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) => new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), sheets.Select(s => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("name", s.Name), new XAttribute("sheetId", s.Index), new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{s.Index}")))))).ToString(SaveOptions.DisableFormatting);
    static string SheetXml(IEnumerable<string[]> rows) => new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), rows.Select((row, r) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", r + 1), row.Select((cell, c) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", $"{Col(c + 1)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);
    static string Col(int n) { var s = ""; while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; } return s; }
    static string SafeSheetName(string name) => string.Join("", name.Where(ch => !"[]:*?/\\ ".Contains(ch) || ch == ' ')).Trim() is var clean && clean.Length > 31 ? clean[..31] : string.IsNullOrWhiteSpace(clean) ? "Sheet" : clean;
    static Dictionary<string, int> HeaderMap(List<string> header) => header.Select((value, index) => (Key: Norm(value), Index: index)).Where(item => !string.IsNullOrWhiteSpace(item.Key)).GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
    static string Cell(List<string> row, Dictionary<string, int> header, string name) => header.TryGetValue(Norm(name), out var index) && index >= 0 && index < row.Count ? row[index].Trim() : "";
    static bool Blank(List<string> row) => row.All(string.IsNullOrWhiteSpace);
    static void SetIfAny(Action<string> set, string value) { if (!string.IsNullOrWhiteSpace(value)) set(value.Trim()); }
    static List<List<string>> GetSheet(EmployeeImportWorkbook workbook, params string[] names)
    {
        foreach (var name in names) if (workbook.Sheets.TryGetValue(name, out var rows)) return rows;
        var wanted = names.Select(Norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return workbook.Sheets.FirstOrDefault(sheet => wanted.Contains(Norm(sheet.Key))).Value ?? [];
    }
    static List<List<string>> GetEmployeeDataSheet(EmployeeImportWorkbook workbook)
    {
        var rows = GetSheet(workbook, "Employees", "Employee", "CSV");
        if (rows.Count > 0) return rows;
        return workbook.Sheets
            .Where(sheet => !new[] { "references", "reference", "masters", "instructions" }.Contains(Norm(sheet.Key), StringComparer.OrdinalIgnoreCase))
            .Select(sheet => sheet.Value)
            .FirstOrDefault(sheetRows => sheetRows.Skip(1).Any(row => !Blank(row))) ?? [];
    }
    static bool HasDataSheet(EmployeeImportWorkbook workbook, params string[] names) => names.Any(name => GetSheet(workbook, name).Skip(1).Any(row => !Blank(row)));
    static int CountImportRows(EmployeeImportWorkbook workbook)
    {
        var known = new[] { "0001 Org Assignment", "0002 Personal Data", "0006 Addresses", "0008 Basic Pay", "0009 Bank Details" };
        var total = known.Sum(name => GetSheet(workbook, name).Skip(1).Count(row => !Blank(row)));
        return total > 0 ? total : GetEmployeeDataSheet(workbook).Skip(1).Count(row => !Blank(row));
    }
    static bool TryDate(string value, out string date)
    {
        date = "";
        if (string.IsNullOrWhiteSpace(value)) return true;
        value = value.Trim();
        if (double.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var serial) && serial >= 20000 && serial <= 80000)
        {
            date = DateTime.FromOADate(serial).ToString("yyyy-MM-dd");
            return true;
        }
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var exact) || DateTime.TryParse(value, out exact))
        {
            date = exact.ToString("yyyy-MM-dd");
            return true;
        }
        return false;
    }
    static bool TryBool(string value, bool fallback, out bool result)
    {
        result = fallback;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "active": case "1": result = true; return true;
            case "false": case "no": case "inactive": case "0": result = false; return true;
            default: return false;
        }
    }
    static bool JsonObjectOk(string value)
    {
        try { using var doc = JsonDocument.Parse(value); return doc.RootElement.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }
    static bool EmailOk(string value) => Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    static int? ResolveWorkLocationId(string idText, string name, Dictionary<int, LocationRef> byId, Dictionary<string, List<LocationRef>> byName, List<string> errors, string sheet, int row)
    {
        if (!string.IsNullOrWhiteSpace(idText))
        {
            if (int.TryParse(idText, out var id) && byId.ContainsKey(id)) return id;
            errors.Add($"{sheet} row {row}: Work Location Id \"{idText}\" is not in Work Locations for this client.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!byName.TryGetValue(name, out var matches)) { errors.Add($"{sheet} row {row}: Work Location \"{name}\" is not an active Work Location for this client."); return null; }
        if (matches.Count > 1) { errors.Add($"{sheet} row {row}: Work Location \"{name}\" is found more than once. Rename one location or use a unique location name."); return null; }
        return matches[0].Id;
    }
    static int? ResolveManagerUserId(string idText, string email, Dictionary<int, UserRef> byId, Dictionary<string, UserRef> byEmail, List<string> errors, string sheet, int row)
    {
        if (!string.IsNullOrWhiteSpace(idText))
        {
            if (int.TryParse(idText, out var id) && byId.ContainsKey(id)) return id;
            errors.Add($"{sheet} row {row}: Reporting Manager User Id \"{idText}\" is not an active user for this client.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(email)) return null;
        if (byEmail.TryGetValue(email.Trim(), out var user)) return user.Id;
        errors.Add($"{sheet} row {row}: Reporting Manager Email \"{email}\" is not an active user for this client.");
        return null;
    }
    static SalaryTemplateRef? ResolveSalaryTemplate(string id, string name, Dictionary<string, SalaryTemplateRef> byId, Dictionary<string, List<SalaryTemplateRef>> byName, List<string> errors, string sheet, int row)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (byId.TryGetValue(id, out var template)) return template;
            errors.Add($"{sheet} row {row}: Salary Template Id \"{id}\" is not in Salary Templates for this client.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!byName.TryGetValue(name, out var matches)) { errors.Add($"{sheet} row {row}: Salary Template \"{name}\" is not active for this client."); return null; }
        if (matches.Count > 1) { errors.Add($"{sheet} row {row}: Salary Template \"{name}\" is found more than once. Rename one salary template or use a unique template name."); return null; }
        return matches[0];
    }
    static List<SalaryTemplateRef> ReadSalaryTemplates(string setupJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(setupJson) ? "{}" : setupJson);
            if (!doc.RootElement.TryGetProperty("salaryStructures", out var structures) || structures.ValueKind != JsonValueKind.Array) return [];
            return structures.EnumerateArray()
                .Where(item => !item.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False)
                .Select(item => new SalaryTemplateRef(JsonText(item, "id"), JsonText(item, "name"), JsonText(item, "clientId"), JsonText(item, "annualCtc")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToList();
        }
        catch { return []; }
    }
    static string JsonText(JsonElement item, string property) => item.TryGetProperty(property, out var value) ? value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.ToString(), JsonValueKind.True => "TRUE", JsonValueKind.False => "FALSE", _ => value.ToString() } : "";
    static string RefId(string value) => (value ?? "").Split(':')[0].Trim();
    static bool TemplateForClient(SalaryTemplateRef template, int clientId) { var refId = RefId(template.ClientId); return string.IsNullOrWhiteSpace(refId) || refId == "0" || refId == clientId.ToString(); }
    private sealed class EmployeeImportDraft(Employee employee, int firstRow)
    {
        public Employee Employee { get; } = employee;
        public int FirstRow { get; } = firstRow;
        public HashSet<string> Infotypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ChangeReason { get; set; } = "Bulk upload";
    }
    private sealed class LocationRef
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
    }
    private sealed class UserRef
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
    }
    private sealed record EmployeeImportReviewState(
        int ClientId,
        string Mode,
        EmployeeImportWorkbook Workbook,
        EmployeeImportPreflightResult Result,
        DateTime ExpiresAtUtc,
        string FileHash);
    private sealed record SalaryTemplateRef(string Id, string Name, string ClientId, string AnnualCtc);
    private sealed record SheetRef(string Name, string Path);
}

public record EmployeeImportWorkbook(Dictionary<string, List<List<string>>> Sheets);
public record EmployeeImportDecision(
    int RowNumber,
    string Sheet,
    string Action,
    int? EmployeeId = null,
    Dictionary<string, string>? FieldChoices = null);
public record EmployeeImportFieldChange(string Field, string Label, string OldValue, string NewValue, bool Sensitive, bool PayrollImpact);
public record EmployeeImportCandidate(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    List<string> MatchReasons,
    List<EmployeeImportFieldChange> Changes);
public record EmployeeImportEvidenceCandidate(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string ExistingValue);
public record EmployeeImportIdentityEvidence(
    string Field,
    string Label,
    string UploadedValue,
    bool Sensitive,
    List<EmployeeImportEvidenceCandidate> Candidates);
public record EmployeeImportPreflightRow(
    int RowNumber,
    string Sheet,
    string ProposedEmployeeCode,
    string MatchStatus,
    int? MatchedEmployeeId,
    string? MatchedEmployeeCode,
    string? MatchedEmployeeName,
    List<string> MatchReasons,
    List<string> BlockingReasons,
    List<EmployeeImportFieldChange> Changes,
    List<EmployeeImportCandidate>? CandidateEmployees = null,
    List<EmployeeImportIdentityEvidence>? IdentityEvidence = null,
    bool CanResolveConflict = false);
public record EmployeeImportPreflightResult(
    Guid ReviewToken,
    int TotalRows,
    bool CanImport,
    bool RequiresConfirmation,
    List<EmployeeImportPreflightRow> Rows,
    DateTime ExpiresAtUtc);
public record EmployeeImportResult(
    int TotalRows,
    int Inserted,
    int Updated,
    List<string> Errors,
    Guid? ReviewToken = null,
    bool RequiresConfirmation = false,
    List<EmployeeImportPreflightRow>? ReviewRows = null);
public record EmployeeImportJobStatus(
    Guid JobId,
    string State,
    int TotalRows,
    int CompletedRows,
    int Inserted,
    int Updated,
    List<string> Errors,
    Guid? ReviewToken = null,
    bool RequiresConfirmation = false,
    List<EmployeeImportPreflightRow>? ReviewRows = null);
public record EmployeeDeletePreview(int EmployeeId, string EmployeeCode, string EmployeeName, List<string> Links, bool CanDelete);
