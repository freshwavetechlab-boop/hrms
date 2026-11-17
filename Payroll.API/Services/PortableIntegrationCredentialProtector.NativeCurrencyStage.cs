using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectNativeCurrencyStage(string value) => Protect(value, "native-currency-stage:v1:",
        Encoding.UTF8.GetBytes("Payroll.API.NativePayslipCurrencyStage.v1"));

    internal string? UnprotectNativeCurrencyStage(string value) =>
        IsPortable(value, "native-currency-stage:v1:") && TryUnprotect(value, "native-currency-stage:v1:",
            Encoding.UTF8.GetBytes("Payroll.API.NativePayslipCurrencyStage.v1"), legacyAiProtector, out var plaintext)
            ? plaintext : null;
}
