using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    internal static async Task<long> InsertRosterDescriptionDraftAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, long requisitionId, SimpleJobDescriptionContent content, int actorId)
    {
        var request = new SaveRecruitmentJobDescriptionVersion {
            RequisitionId = requisitionId, Title = content.Title, Summary = content.Summary, RolePurpose = content.RolePurpose,
            Responsibilities = content.Responsibilities.Select((text, i) => new RecruitmentJdResponsibility { ResponsibilityText = text, DisplayOrder = i + 1 }).ToList(),
            Skills = content.Skills.Select((text, i) => new RecruitmentJdSkillRequirement { SkillName = text, IsRequired = true, DisplayOrder = i + 1 }).ToList()
        };
        var error = ValidateJobDescriptionFieldLengths(request);
        if (error.Length > 0) throw new InvalidOperationException(error);
        var id = await db.ExecuteScalarAsync<long>("""
INSERT INTO recruitment_job_description_versions(RequisitionId,ClientId,VersionNumber,Title,Summary,RolePurpose,Status,CreatedByUserId)
VALUES(@RequisitionId,@ClientId,1,@Title,@Summary,@RolePurpose,'Draft',@Actor);SELECT LAST_INSERT_ID();
""", new { RequisitionId = requisitionId, ClientId = clientId, content.Title, content.Summary, content.RolePurpose, Actor = actorId }, tx);
        await InsertJobDescriptionChildrenAsync(db, tx, id, request);
        return id;
    }
}
