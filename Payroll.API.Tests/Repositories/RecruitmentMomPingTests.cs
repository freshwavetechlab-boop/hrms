using Dapper;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class RecruitmentRevisionRegressionTests
{
    [LocalInterviewDatabaseFact]
    public async Task Mom_supporting_files_use_global_attachment_config_and_do_not_count_as_signed_copies()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        // Replace the deliberately minimal generation fixture with the real shared attachment schema.
        await db.ExecuteAsync("DROP TABLE attachment_field_configurations; DROP TABLE attachment_attributes;");
        var attachments = new AttachmentRepository(fixture.Config, null!, null!, null!, null!, null!);
        await attachments.InitializeAsync();
        await attachments.InitializeMomSupportingAttachmentsAsync(); // Configuration-only setup is repeatable.
        await attachments.InitializeMomSupportingAttachmentsAsync();
        var field = Assert.Single(await attachments.GetEffectiveConfigurationsAsync(20, "RECRUITMENT", "PROCESS_SUPPORT"));
        Assert.False(field.IsRequired);
        Assert.True(field.AllowMultiple);
        Assert.Equal(10, field.MaximumFileCount);
        var serverId = await db.ExecuteScalarAsync<long>("SELECT id FROM attachment_storage_servers WHERE server_code='API_LOCAL'");
        await db.ExecuteAsync(@"INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,
entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
VALUES(@PublicId,20,@AttributeId,@FieldId,@ServerId,'RECRUITMENT_PROCESS_SUPPORT',1,'Evidence.pdf','Evidence.pdf','qa/evidence.pdf','pdf','application/pdf',20,@Hash,1)",
            new { PublicId = Guid.NewGuid().ToString(), AttributeId = field.AttachmentAttributeId, FieldId = field.Id, ServerId = serverId, Hash = new string('a',64) });
        var viewer = new AuthUser { Id = 4, ClientId = 20, Permissions = ["recruitment.document.view"] };
        Assert.Single(await attachments.GetAttachmentsAsync("RECRUITMENT_PROCESS_SUPPORT", 1, viewer));
        Assert.Empty(await attachments.GetAttachmentsAsync("RECRUITMENT_PROCESS_DOCUMENT", 1, viewer));
        Assert.Empty(await attachments.GetAttachmentsAsync("RECRUITMENT_PROCESS_SUPPORT", 1, new AuthUser { Id = 4, ClientId = 30, Permissions = viewer.Permissions }));
        Assert.Empty(await attachments.GetAttachmentsAsync("RECRUITMENT_PROCESS_SUPPORT", 1, new AuthUser { Id = 4, ClientId = 20, Permissions = [] }));
        var cases = new RecruitmentCaseRepository(fixture.Config, attachments, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        Assert.False(Assert.Single(await cases.ListProcessDocumentsAsync(viewer, 5, null)).HasFinalSignedAttachment);
        using var bytes = new MemoryStream("%PDF-1.4"u8.ToArray());
        var (uploaded, error) = await attachments.UploadAsync(new() { EntityType = "RECRUITMENT_PROCESS_SUPPORT", EntityId = 1, FieldConfigurationId = field.Id },
            new FormFile(bytes,0,bytes.Length,"file","evidence.pdf"), viewer,"","",default);
        Assert.Null(uploaded);
        Assert.Contains("not allowed", error);
    }

    [LocalInterviewDatabaseFact]
    public async Task Mom_ping_is_assignee_scoped_audited_deduplicated_and_removed_after_completion()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        await db.ExecuteAsync("DELETE FROM recruitment_interview_feedback WHERE PanelUserId=3");
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        var coordinator = new AuthUser { Id = 1, ClientId = 20, Permissions = ["recruitment.document.manage"] };
        var panel = new AuthUser { Id = 3, ClientId = 20, Permissions = ["recruitment.interview.panel"] };
        var request = new RecruitmentMomPingRequest { ApplicationId = 100, UserId = 3, Task = "Feedback" };
        Assert.False((await repository.PingMomWorkAsync(1, request, panel)).Ok);
        Assert.False((await repository.PingMomWorkAsync(1, request, new AuthUser { Id = 1, ClientId = 30, Permissions = coordinator.Permissions })).Ok);
        Assert.False((await repository.PingMomWorkAsync(1, new() { ApplicationId = 101, UserId = 3, Task = "Feedback" }, coordinator)).Ok);
        Assert.False((await repository.PingMomWorkAsync(1, new() { ApplicationId = 100, UserId = 4, Task = "Feedback" }, coordinator)).Ok);
        Assert.False((await repository.PingMomWorkAsync(1, new() { ApplicationId = 100, UserId = 3, Task = "Decision" }, coordinator)).Ok);
        Assert.True((await repository.PingMomWorkAsync(1, request, coordinator)).Ok);
        Assert.True((await repository.PingMomWorkAsync(1, request, coordinator)).Ok);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_audit WHERE Action='MoM work ping'"));
        var ping = Assert.Single(await repository.ListMomPingsAsync(panel));
        Assert.Contains("Selected Candidate", ping.Detail);
        Assert.Contains("coordinator", ping.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/recruitment/interviews?clientId=20&feedbackInterviewId=1", ping.Route);
        Assert.Empty(await repository.ListMomPingsAsync(new AuthUser { Id = 2, ClientId = 20, Permissions = panel.Permissions }));
        Assert.Empty(await repository.ListMomPingsAsync(new AuthUser { Id = 3, ClientId = 30, Permissions = panel.Permissions }));
        Assert.Empty(await repository.ListMomPingsAsync(new AuthUser { Id = 3, ClientId = 20, Permissions = [] }));
        await db.ExecuteAsync("UPDATE authusers SET IsActive=FALSE WHERE Id=3");
        Assert.Empty(await repository.ListMomPingsAsync(panel));
        await db.ExecuteAsync("UPDATE authusers SET IsActive=TRUE WHERE Id=3; INSERT INTO recruitment_interview_feedback VALUES(2,1,3)");
        Assert.Empty(await repository.ListMomPingsAsync(panel));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_audit WHERE Action='MoM work ping'"));
    }

    [LocalInterviewDatabaseFact]
    public async Task Mom_pending_users_follow_interview_round_decision_recruiter_and_score_assignments()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        var admin = new AuthUser { Id = 1, ClientId = 20, Permissions = ["recruitment.document.manage"] };
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET RecruiterUserId=1,TermsConfirmedAtUtc=NULL WHERE Id=100;
UPDATE recruitment_interviews SET DecisionApproverUserId=4 WHERE Id=6;
INSERT INTO recruitment_interview_panel_members VALUES(6,2,'Chair'); INSERT INTO recruitment_interview_feedback VALUES(6,6,2);");
        var readiness = (await repository.GetMomReadinessAsync(1, admin))!;
        Assert.Equal("Terms", Assert.Single(readiness.CandidateChecks.Single(row => row.ApplicationId == 100).PendingWorkOwners).Task);
        var decision = Assert.Single(readiness.CandidateChecks.Single(row => row.ApplicationId == 104).PendingWorkOwners);
        Assert.Equal("Decision", decision.Task);
        Assert.Equal(4, decision.UserId);
        Assert.Contains("decisionInterviewId=6", decision.Route);
        // Reassigning to a different client or inactive user cannot create a notification recipient.
        await db.ExecuteAsync("UPDATE authusers SET ClientId=30 WHERE Id=4");
        Assert.Empty((await repository.GetMomReadinessAsync(1, admin))!.CandidateChecks.Single(row => row.ApplicationId == 104).PendingWorkOwners);
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET TermsConfirmedAtUtc=UTC_TIMESTAMP() WHERE Id=100;
INSERT INTO recruitment_interview_competency_definitions VALUES(1,'Communication');
INSERT INTO recruitment_interview_stage_competencies VALUES(1,1,1,100,1);");
        readiness = (await repository.GetMomReadinessAsync(1, admin))!;
        Assert.True(readiness.CandidateChecks.Single(row => row.ApplicationId == 100).ScoresPending);
        Assert.All(readiness.CandidateChecks.Single(row => row.ApplicationId == 100).PendingWorkOwners, owner => Assert.Equal("Scores", owner.Task));
        Assert.Equal(2, readiness.CandidateChecks.Single(row => row.ApplicationId == 100).PendingWorkOwners.Count);
        // A newer round must retire reminders aimed at the previous round.
        Assert.True((await repository.PingMomWorkAsync(1, new() { ApplicationId = 100, UserId = 3, Task = "Scores" }, admin)).Ok);
        await db.ExecuteAsync("INSERT INTO recruitment_interviews(Id,ApplicationId,Status,Result,RoundConfigurationId) VALUES(7,100,'Scheduled','Pending',1)");
        Assert.Empty(await repository.ListMomPingsAsync(new AuthUser { Id = 3, ClientId = 20, Permissions = ["recruitment.interview.panel"] }));
    }
}
