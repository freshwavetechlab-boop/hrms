using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectLocationCorrection(string value) => Protect(value, "location-correction:v1:", Encoding.UTF8.GetBytes("Payroll.API.LocationCorrection.v1"));
    internal string? UnprotectLocationCorrection(string value) => IsPortable(value, "location-correction:v1:")
        && TryUnprotect(value, "location-correction:v1:", Encoding.UTF8.GetBytes("Payroll.API.LocationCorrection.v1"), legacyStorageProtector, out var plaintext) ? plaintext : null;
}
