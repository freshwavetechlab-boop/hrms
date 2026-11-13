namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    public static bool IsAadhaarFirstEightPasswordMode(string? mode) =>
        string.Equals(mode?.Trim(), "AadhaarFirst8", StringComparison.OrdinalIgnoreCase);

    public static string AadhaarFirstEightTemporaryPassword(string? aadhaar)
    {
        // This is deliberately a separate opt-in mode. Existing Aadhaar/full,
        // random, fixed and employee-code policies keep their original meaning.
        var compact = new string((aadhaar ?? "").Where(c => c != ' ' && c != '-').ToArray());
        if (compact.Length != 12 || compact.Any(c => c is < '0' or > '9'))
            throw new InvalidOperationException("A complete 12-digit Aadhaar is required for this client's initial password policy. Update Employee Master before creating the login.");
        return compact[..8];
    }

    private sealed record InitialEmployeePassword(string Value, bool RequireChange = false);
}
