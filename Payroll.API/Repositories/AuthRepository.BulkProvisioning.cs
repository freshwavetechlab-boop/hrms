using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    internal static string NormalizeProvisionPasswordMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "clientpolicy" => "ClientPolicy",
        "shared" => "Shared",
        "employeecode" => "EmployeeCode",
        "email" => "Email",
        "mobile" => "Mobile",
        _ => throw new InvalidOperationException("Choose a supported temporary password pattern.")
    };

    internal static string EmployeeProvisionPassword(string mode, string? employeeCode, string? workEmail, string? mobile)
    {
        var value = mode switch
        {
            "EmployeeCode" => employeeCode?.Trim() ?? "",
            "Email" => workEmail?.Trim() ?? "",
            "Mobile" => mobile?.Trim() ?? "",
            _ => throw new InvalidOperationException("Choose a supported employee temporary password pattern.")
        };
        return value;
    }

    private async Task<InitialEmployeePassword> ResolveProvisionPasswordAsync(MySqlConnection connection,
        EmployeeLoginProvisionPreview employee, string mode, string sharedPassword, string fallbackPassword, bool explicitMode,
        MySqlTransaction transaction)
    {
        if (mode == "ClientPolicy")
            return await ResolveInitialPasswordAsync(connection, employee, explicitMode ? "" : sharedPassword, transaction);
        if (mode == "Shared") return new InitialEmployeePassword(sharedPassword, RequireChange: true);

        string? mobile = null;
        if (mode == "Mobile")
            mobile = await connection.ExecuteScalarAsync<string?>(@"SELECT
CASE WHEN p.EmployeeId IS NOT NULL THEN COALESCE(p.Mobile,'')
ELSE COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson,'$.mobile')),JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson,'$.Mobile')),'') END
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
WHERE e.Id=@EmployeeId AND e.ClientId=@ClientId", new { employee.EmployeeId, employee.ClientId }, transaction);
        var password = EmployeeProvisionPassword(mode, employee.EmployeeCode, employee.WorkEmail, mobile);
        return password.Length > 0
            ? new InitialEmployeePassword(password, RequireChange: true)
            : new InitialEmployeePassword(fallbackPassword, RequireChange: true, Notice: $"{(mode == "Email" ? "Work email" : mode == "Mobile" ? "Mobile number" : "Employee code")} is missing; batch temporary password used.");
    }
}
