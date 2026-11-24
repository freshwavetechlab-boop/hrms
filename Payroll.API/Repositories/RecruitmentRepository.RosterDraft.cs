using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentRepository
{
    internal static async Task<long> InsertRosterHiringDraftAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int? locationId, string location, string reference, string number, SimpleJobDescriptionContent content, int actorId)
    {
        // An existing-manpower role is preparation, not an approval, vacancy or
        // published job. Unknown budget/qualification/experience stay unset.
        var request = new SaveRecruitmentRequisition {
            ClientId = clientId, BranchId = locationId ?? 0, RequesterUserId = actorId > 0 ? actorId : null,
            PositionTitle = content.Title, JobLocation = location, RequiredSkills = string.Join(", ", content.Skills),
            BusinessJustification = content.Summary, ReasonForHiring = "Role prepared from an existing employee list.",
            SourceType = "Employee List JD", SourceReference = reference, SourceNotes = "Draft role only; hiring approval and publishing remain separate.",
            Languages = "", Benefits = "", HiringPriority = "Normal"
        };
        var errors = RecruitmentRequisitionTextLimits.Validate(request);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors.Select(error => error.Message)));
        await db.ExecuteAsync(InsertSql, Payload(request, new AuthUser { Id = actorId },
            new RequesterRow { Id = 0, ClientId = clientId }, clientId, number, 0, DateTime.UtcNow.Date), tx);
        return await db.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()", transaction: tx);
    }
}
