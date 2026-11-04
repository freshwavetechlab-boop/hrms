using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    public async Task<IReadOnlyList<PayrollLocationPayslipDocument>> GetLocationPayslipDocumentsAsync(int clientId, int payRunId, int workLocationId = 0)
    {
        if (clientId <= 0 || payRunId <= 0 || workLocationId < 0) throw new InvalidOperationException("Select a saved payroll and location.");
        await using var db = Connection(); await db.OpenAsync();
        var rows = (await db.QueryAsync<EssPayslipRow>(@"SELECT p.Id PayRunEmployeeId,p.PayRunId,p.EmployeeId,p.ClientId,p.EmployeeCode,p.EmployeeName,p.Department,
p.PresentDays,p.PayableDays,p.GrossPay,p.StatutoryDeductions,p.OneTimeEarnings,p.OneTimeDeductions,p.NetPay,p.PaymentStatus,p.PaymentDate,p.DetailsJson,
r.PayPeriod,r.PayDate,r.Status RunStatus,r.RunType,r.TotalWorkingDays,COALESCE(c.Name,'') ClientName,
e.WorkEmail,e.Designation,e.DateOfJoining,COALESCE(w.Name,'') WorkLocation,COALESCE(pd.Address,'') Address,
COALESCE(pd.PanNumber,'') PanNumber,COALESCE(pd.UanNumber,'') UanNumber,COALESCE(pay.BankName,'') BankName,
COALESCE(pay.BankAccountNo,'') BankAccountNo,COALESCE(pay.IfscCode,'') IfscCode
FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId LEFT JOIN clients c ON c.Id=p.ClientId
LEFT JOIN employees e ON e.Id=p.EmployeeId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId=p.EmployeeId
WHERE p.ClientId=@ClientId AND r.ClientId=@ClientId AND p.PayRunId=@PayRunId AND p.IsSkipped=FALSE
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid') ORDER BY p.EmployeeCode,p.EmployeeId",
            new { ClientId = clientId, PayRunId = payRunId })).ToList();
        var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1") ?? new Organization();
        var template = await db.QueryFirstOrDefaultAsync<EssPayslipTemplate>(@"SELECT Id,ClientId,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active
FROM paysliptemplates WHERE Active=TRUE AND (ClientId=@ClientId OR ClientId=0)
ORDER BY CASE WHEN ClientId=@ClientId THEN 0 ELSE 1 END,Id DESC LIMIT 1", new { ClientId = clientId }) ?? new EssPayslipTemplate();
        // A split slip is period/location-specific; employee-wide YTD remains in
        // the existing consolidated document. No template is saved or modified.
        template.ShowYtd = false;
        var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id,Name FROM worklocations WHERE ClientId=@ClientId", new { ClientId = clientId })).ToDictionary(l => l.Id, l => l.Name);
        var employeeLocations = (await db.QueryAsync<(int EmployeeId, int LocationId)>("SELECT Id EmployeeId,WorkLocationId LocationId FROM employees WHERE ClientId=@ClientId", new { ClientId = clientId })).ToDictionary(e => e.EmployeeId, e => e.LocationId);
        var nativeTables = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='employee_payroll_location_allocations'") > 0;
        var references = !nativeTables ? [] : (await db.QueryAsync<LocationSlipReference>(@"SELECT a.employee_id EmployeeId,a.reference_assignment_id AssignmentId
FROM employee_payroll_location_allocations a JOIN payruns r ON r.ClientId=a.client_id AND r.PayPeriod=a.pay_period
WHERE r.Id=@PayRunId AND a.client_id=@ClientId", new { PayRunId = payRunId, ClientId = clientId })).ToList();
        var documents = new List<PayrollLocationPayslipDocument>();
        foreach (var row in rows)
        {
            var choices = references.Where(r => r.EmployeeId == row.EmployeeId).Select(r => r.AssignmentId).Distinct().ToList();
            if (choices.Count > 1) throw new InvalidOperationException("Saved monthly reference assignments disagree.");
            var native = PayrollLocationPayslipProjection.Build(new PayRunEmployee { EmployeeId = row.EmployeeId, DetailsJson = row.DetailsJson,
                GrossPay = row.GrossPay, StatutoryDeductions = row.StatutoryDeductions, OneTimeEarnings = row.OneTimeEarnings,
                OneTimeDeductions = row.OneTimeDeductions, NetPay = row.NetPay }, choices.FirstOrDefault());
            if (native.Count == 0)
            {
                var locationId = employeeLocations.GetValueOrDefault(row.EmployeeId);
                if (workLocationId > 0 && locationId != workLocationId) continue;
                Add(row, "employee:" + row.EmployeeId, locationId, row.WorkLocation, false);
            }
            else foreach (var part in native.Where(p => workLocationId == 0 || p.WorkLocationId == workLocationId))
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<EssPayslipRow>(System.Text.Json.JsonSerializer.Serialize(row))!;
                copy.WorkLocation = locations.GetValueOrDefault(part.WorkLocationId) ?? throw new InvalidOperationException("Saved payroll location is unavailable in this client.");
                copy.PresentDays = part.PresentDays; copy.PayableDays = part.PayableDays; copy.TotalWorkingDays = part.PayrollDays;
                copy.GrossPay = part.Gross; copy.StatutoryDeductions = part.Deductions; copy.NetPay = part.Net;
                copy.OneTimeEarnings = copy.OneTimeDeductions = 0; copy.DetailsJson = part.DetailsJson;
                Add(copy, part.Key, part.WorkLocationId, copy.WorkLocation, true);
            }
            void Add(EssPayslipRow saved, string key, int locationId, string locationName, bool nativePart)
            {
                var html = BuildPayslipHtml(organization, template, saved, new EssPayslipYtd());
                if (nativePart) html = html.Replace(Detail("LOP", Amount(Math.Max(0, saved.TotalWorkingDays - saved.PayableDays))),
                    Detail("Attendance scope", "This location part"), StringComparison.Ordinal)
                    .Replace(Detail("Working Days", saved.TotalWorkingDays), Detail("Payroll divisor", saved.TotalWorkingDays), StringComparison.Ordinal);
                if (saved.RunStatus is "Draft" or "Pending Approval") html = html.Replace("<main class=", "<div style=\"text-align:center;font:bold 14px sans-serif;padding:8px\">DRAFT — PAYROLL APPROVAL PENDING</div><main class=", StringComparison.Ordinal);
                var lines = ParsePayslipLines(saved.DetailsJson);
                documents.Add(new(row.EmployeeId, row.EmployeeCode, row.EmployeeName, key, locationId, locationName,
                    saved.GrossPay + saved.OneTimeEarnings, saved.StatutoryDeductions + saved.OneTimeDeductions, saved.NetPay, saved.PresentDays, saved.PayableDays,
                    $"payslip-{SafeFile(row.EmployeeCode)}-{SafeFile(row.PayPeriod)}-{locationId}-{SafeFile(key)}.html", html)
                {
                    // Print-only adapter to the existing paginated PDF renderer.
                    // It is never saved as an Excel batch or calculation source.
                    PrintSnapshot = new ExcelPayslipRow { Id = key, SourceRow = documents.Count + 1, EmployeeCode = row.EmployeeCode, EmployeeName = row.EmployeeName,
                        Earnings = lines.Where(l => l.Amount != 0 && l.Category is "Earning" or "Reimbursement").Select(l => new ExcelPayslipAmount { Label = l.Name, Amount = l.Amount }).ToList(),
                        Deductions = lines.Where(l => l.Amount != 0 && l.Category.Contains("Deduction", StringComparison.OrdinalIgnoreCase)).Select(l => new ExcelPayslipAmount { Label = l.Name, Amount = l.Amount }).ToList(),
                        EmployerContributions = lines.Where(l => l.Amount != 0 && l.Category == "Benefit").Select(l => new ExcelPayslipAmount { Label = l.Name, Amount = l.Amount }).ToList(),
                        NetPay = saved.NetPay, DeclaredGross = saved.GrossPay + saved.OneTimeEarnings, DeclaredDeductions = saved.StatutoryDeductions + saved.OneTimeDeductions,
                        Information = [new() { Label = "Location", Value = locationName }, new() { Label = "Designation", Value = saved.Designation },
                            new() { Label = "Present days", Value = saved.PresentDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not supplied" },
                            new() { Label = "Payable days", Value = saved.PayableDays.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            new() { Label = "Payroll divisor", Value = saved.TotalWorkingDays.ToString() },
                            new() { Label = "Account No", Value = PayslipPresentation.AccountNumber(saved.BankAccountNo) }, new() { Label = "IFSC", Value = saved.IfscCode }] }
                });
            }
        }
        return documents;
    }
    private sealed record LocationSlipReference(int EmployeeId, string? AssignmentId);
}
