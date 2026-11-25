using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    internal static async Task AssertEmployeeUserDeactivationSafeAsync(MySqlConnection db, MySqlTransaction tx, IReadOnlyCollection<int> employeeIds)
    {
        if (employeeIds.Count == 0) return;
        // Resignation must not offer a way around the same last-administrator
        // protection used by user deletion and account deactivation.
        var affectedAdmin = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(
SELECT 1 FROM authusers u JOIN authuserroles ur ON ur.UserId=u.Id
JOIN authrolepermissions rp ON rp.RoleId=ur.RoleId JOIN authpermissions p ON p.Id=rp.PermissionId
WHERE u.EmployeeId IN @EmployeeIds AND u.IsActive=TRUE AND p.Code='security.manage')", new { EmployeeIds = employeeIds }, tx);
        if (!affectedAdmin) return;
        var remainingAdmin = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(
SELECT 1 FROM authusers u JOIN authuserroles ur ON ur.UserId=u.Id
JOIN authrolepermissions rp ON rp.RoleId=ur.RoleId JOIN authpermissions p ON p.Id=rp.PermissionId
WHERE u.IsActive=TRUE AND u.ClientId IS NULL AND u.ClientAccessMode='Legacy'
AND (u.EmployeeId IS NULL OR u.EmployeeId NOT IN @EmployeeIds) AND p.Code='security.manage')", new { EmployeeIds = employeeIds }, tx);
        if (!remainingAdmin) throw new InvalidOperationException("Keep another active unrestricted security administrator before resigning this employee.");
    }

    internal static async Task DeactivateEmployeeUsersAsync(MySqlConnection db, MySqlTransaction tx, IReadOnlyCollection<int> employeeIds)
    {
        if (employeeIds.Count == 0) return;
        await db.QueryAsync<int>("SELECT Id FROM authusers WHERE EmployeeId IN @EmployeeIds ORDER BY Id FOR UPDATE", new { EmployeeIds = employeeIds }, tx);
        await AssertEmployeeUserDeactivationSafeAsync(db, tx, employeeIds);
        await db.ExecuteAsync(@"UPDATE authsessions s JOIN authusers u ON u.Id=s.UserId
SET s.RevokedAt=UTC_TIMESTAMP() WHERE u.EmployeeId IN @EmployeeIds AND s.RevokedAt IS NULL",
            new { EmployeeIds = employeeIds }, tx);
        await db.ExecuteAsync("UPDATE authusers SET IsActive=FALSE WHERE EmployeeId IN @EmployeeIds AND IsActive=TRUE", new { EmployeeIds = employeeIds }, tx);
        // Retain identities, permissions, attendance mappings and all payroll /
        // leave history. Rehire requires an explicit login reactivation by HR.
    }

}
