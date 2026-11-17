using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectNativeCurrencyRecapture(string value) => Protect(value, "native-currency-recapture:v1:",
        Encoding.UTF8.GetBytes("Payroll.API.NativePayslipCurrencyRecapture.v1"));

    internal string? UnprotectNativeCurrencyRecapture(string value) =>
        IsPortable(value, "native-currency-recapture:v1:") && TryUnprotect(value, "native-currency-recapture:v1:",
            Encoding.UTF8.GetBytes("Payroll.API.NativePayslipCurrencyRecapture.v1"), legacyAiProtector, out var plain)
            ? plain : null;
}
