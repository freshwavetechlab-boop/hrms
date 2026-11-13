using System.Text;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    public static bool IsEmployeePhoneLoginPortal(string? portal) =>
        string.Equals(portal, "ESS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(portal, "Employee", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes an Indian mobile number without accepting arbitrary last-ten-digit
    /// suffixes, extensions, masked values, Unicode digits, or other country codes.
    /// </summary>
    public static string NormalizeEmployeeLoginPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return string.Empty;
        var input = value.Trim();
        var hasPlus = input[0] == '+';
        var digits = new StringBuilder(14);
        for (var index = 0; index < input.Length; index++)
        {
            var ch = input[index];
            if (ch is >= '0' and <= '9') digits.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or '(' or ')') continue;
            else if (ch == '+' && index == 0) continue;
            else return string.Empty;
        }

        var normalized = digits.ToString();
        if (hasPlus)
        {
            if (normalized.Length != 12 || !normalized.StartsWith("91", StringComparison.Ordinal))
                return string.Empty;
            normalized = normalized[2..];
        }
        else if (normalized.Length == 14 && normalized.StartsWith("0091", StringComparison.Ordinal))
            normalized = normalized[4..];
        else if (normalized.Length == 12 && normalized.StartsWith("91", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized.Length == 10 && normalized[0] is >= '6' and <= '9'
            ? normalized : string.Empty;
    }

    private static async Task<AuthUserRecord?> FindEmployeePhoneLoginAsync(MySqlConnection connection, string? loginId)
    {
        var phone = NormalizeEmployeeLoginPhone(loginId);
        if (phone.Length == 0) return null;

        // This opt-in is independent of the existing code/email login. No schema or
        // policy is created by authentication, and clients without the flag stay off.
        if (!await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS(SELECT 1 FROM ResourceStates r
JOIN ess_client_settings s ON r.ResourceId=CAST(s.ClientId AS CHAR) AND s.IsActive=TRUE
WHERE r.ResourceType='EmployeePhoneLogin' AND r.CurrentState='Enabled')"))
            return null;

        var owners = (await connection.QueryAsync<EmployeePhoneLoginOwner>(@"
SELECT e.Id EmployeeId,e.ClientId,e.PortalAccess,
       CASE WHEN p.EmployeeId IS NOT NULL THEN COALESCE(p.Mobile,'')
            ELSE COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson,'$.mobile')),
                          JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson,'$.Mobile')),'') END Mobile,
       (COALESCE(phonePolicy.CurrentState,'Disabled')='Enabled' AND COALESCE(clientSetting.IsActive,FALSE)) PhoneLoginEnabled
FROM employees e
JOIN clients c ON c.Id=e.ClientId AND c.IsActive=TRUE
LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
LEFT JOIN ess_client_settings clientSetting ON clientSetting.ClientId=e.ClientId
LEFT JOIN ResourceStates phonePolicy ON phonePolicy.ResourceType='EmployeePhoneLogin'
     AND phonePolicy.ResourceId=CAST(e.ClientId AS CHAR)
WHERE e.IsActive=TRUE"))
            .Where(owner => NormalizeEmployeeLoginPhone(owner.Mobile) == phone)
            .ToList();

        // A shared number is never resolved by trying passwords, by choosing the first
        // row, or by ignoring owners in another client whose phone login is disabled.
        if (owners.Count != 1) return null;
        var owner = owners[0];
        if (!owner.PortalAccess || !owner.PhoneLoginEnabled) return null;

        var accountIds = (await connection.QueryAsync<int>(@"
SELECT Id FROM authusers WHERE EmployeeId=@EmployeeId AND ClientId=@ClientId AND IsActive=TRUE",
            new { owner.EmployeeId, owner.ClientId })).ToList();
        if (accountIds.Count != 1) return null;
        return await connection.QueryFirstOrDefaultAsync<AuthUserRecord>(
            "SELECT * FROM authusers WHERE Id=@Id AND IsActive=TRUE", new { Id = accountIds[0] });
    }

    private sealed class EmployeePhoneLoginOwner
    {
        public int EmployeeId { get; set; }
        public int ClientId { get; set; }
        public bool PortalAccess { get; set; }
        public string Mobile { get; set; } = string.Empty;
        public bool PhoneLoginEnabled { get; set; }
    }
}
