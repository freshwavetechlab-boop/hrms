using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentRepository
{
    public async Task<IReadOnlyList<RecruitmentRequesterOption>> GetRequestersAsync(int clientId, AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.CanCreateRequisitions(user)
            || !SelectedClientAccess.Allows(user, clientId)) throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync();
        return await ReadRequesterUsersAsync(db, user, clientId);
    }

    internal static async Task<bool> IsRequesterUserSelectionAllowedAsync(MySqlConnection db, AuthUser user, int clientId, int requesterUserId, int? existingRequesterUserId = null)
    {
        if (existingRequesterUserId is > 0) return requesterUserId == existingRequesterUserId.Value;
        if (!SelectedClientAccess.Allows(user, clientId)) return false;
        return (await ReadRequesterUsersAsync(db, user, clientId)).Any(row => row.UserId == requesterUserId);
    }

    private static async Task<IReadOnlyList<RecruitmentRequesterOption>> ReadRequesterUsersAsync(MySqlConnection db, AuthUser actor, int clientId)
    {
        // Requesters are employer/hiring users. Employee Master records and ESS
        // accounts without recruitment authority are not a fallback.
        var grants = await db.QueryAsync<RequesterUserGrant>(@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,
u.Email,@ClientId ClientId,p.Code PermissionCode
FROM authusers u
JOIN authuserroles ur ON ur.UserId=u.Id
JOIN authroles r ON r.Id=ur.RoleId AND (r.ClientId IS NULL OR r.ClientId IN (0,@ClientId))
JOIN authrolepermissions rp ON rp.RoleId=r.Id
JOIN authpermissions p ON p.Id=rp.PermissionId
WHERE u.IsActive=TRUE
AND ((u.ClientAccessMode='Selected' AND EXISTS (SELECT 1 FROM authuser_clients uc WHERE uc.UserId=u.Id AND uc.ClientId=@ClientId))
 OR ((u.ClientAccessMode IS NULL OR u.ClientAccessMode='Legacy') AND u.ClientId=@ClientId))
AND p.Code IN ('recruitment.rfr.create','recruitment.manage','settings.manage','recruitment.rfr.view','workflow.approve')", new { ClientId = clientId });
        var canManage = actor.Permissions.Contains("recruitment.manage", StringComparer.OrdinalIgnoreCase)
            || actor.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase);
        return grants.GroupBy(row => row.UserId).Where(group =>
        {
            if (!canManage && group.Key != actor.Id) return false;
            var permissions = group.Select(row => row.PermissionCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return permissions.Overlaps(["recruitment.rfr.create", "recruitment.manage", "settings.manage"])
                || permissions.Contains("recruitment.rfr.view") && permissions.Contains("workflow.approve");
        }).Select(group => group.First()).OrderBy(row => row.DisplayName).ThenBy(row => row.Email)
            .Select(row => new RecruitmentRequesterOption { UserId = row.UserId, DisplayName = row.DisplayName, Email = row.Email, ClientId = row.ClientId }).ToList();
    }

    private sealed class RequesterUserGrant
    {
        public int UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public int ClientId { get; set; }
        public string PermissionCode { get; set; } = "";
    }
}
