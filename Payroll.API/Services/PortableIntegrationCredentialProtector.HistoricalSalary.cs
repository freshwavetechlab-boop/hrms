using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectHistoricalSalary(string value) => Protect(value, "historical-salary:v1:", Encoding.UTF8.GetBytes("Payroll.API.HistoricalPeriodSalary.v1"));
    internal string? UnprotectHistoricalSalary(string value) =>
        IsPortable(value, "historical-salary:v1:") && TryUnprotect(value, "historical-salary:v1:", Encoding.UTF8.GetBytes("Payroll.API.HistoricalPeriodSalary.v1"), legacyAiProtector, out var plaintext)
            ? plaintext : null;
}
