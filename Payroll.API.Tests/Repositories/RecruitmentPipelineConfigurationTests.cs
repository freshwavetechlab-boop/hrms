using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentPipelineConfigurationTests
{
    private static AuthUser Admin => new() { Id = 1, IsActive = true, Permissions = ["settings.manage"] };

    [LocalInterviewDatabaseFact]
    public async Task OrdinarySaveUpdatesSameConfigurationAndPreservesAllMappedIdentities()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        await f.Db.ExecuteAsync("INSERT INTO recruitment_application_pipeline_instances VALUES(70,1); INSERT INTO recruitment_application_stage_instances VALUES(71,11); INSERT INTO recruitment_pipeline_transition_requests VALUES(80,21,'Applied'); INSERT INTO recruitment_stage_action_executions VALUES(90,41)");
        var original = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var request = Request(original); request.Definition.PipelineName = "Updated ordinary pipeline";
        request.Configuration.Stages[0].StageName = "Reviewed intake"; request.Configuration.Stages[0].SlaDurationMinutes = 1800;
        request.Configuration.Transitions[0].ActionLabel = "Proceed";
        var saved = await repo.SavePipelineConfigurationAsync(request, Admin);
        Assert.Empty(saved.Error); Assert.NotNull(saved.Row);
        Assert.Equal(1, saved.Row.Version.Id); Assert.Equal("Published", saved.Row.Version.Status);
        Assert.Equal(new long[] { 11, 12 }, saved.Row.Version.Stages.Select(s => s.Id));
        Assert.Equal(41, Assert.Single(saved.Row.Version.Stages[0].Actions).Id);
        Assert.Equal(51, Assert.Single(saved.Row.Version.Stages[0].Actions[0].Recipients).Id);
        Assert.Equal(21, Assert.Single(saved.Row.Version.Transitions).Id); Assert.Equal(31, Assert.Single(saved.Row.Version.Transitions[0].Rules).Id);
        Assert.Equal("Reviewed intake", saved.Row.Version.Stages[0].StageName); Assert.Equal(1800, saved.Row.Version.Stages[0].SlaDurationMinutes);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_versions"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT PipelineVersionId FROM recruitment_position_pipeline_assignments WHERE Id=99"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT PipelineVersionId FROM recruitment_application_pipeline_instances WHERE Id=70"));
        Assert.Equal(11, await f.Db.ExecuteScalarAsync<int>("SELECT PipelineStageId FROM recruitment_application_stage_instances WHERE Id=71"));
        Assert.Equal(41, await f.Db.ExecuteScalarAsync<int>("SELECT StageActionId FROM recruitment_stage_action_executions WHERE Id=90"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_audit WHERE Action='Update pipeline configuration' AND OldValueJson IS NOT NULL"));
        Assert.NotEqual(original.ConfigurationHash, saved.Row.Version.ConfigurationHash);
    }

    [LocalInterviewDatabaseFact]
    public async Task CreateIsImmediatelyUsableAndLateInvalidChildRollsBackTheEntireAggregate()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var source = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var copy = RecruitmentPipelineRepository.BuildSimilarPipelineDraft(source);
        var created = await repo.SavePipelineConfigurationAsync(new() { Definition = new() { ClientId = 20, PipelineCode = "ORDINARY_NEW", PipelineName = "New ordinary pipeline" }, Configuration = copy }, Admin);
        Assert.Empty(created.Error); Assert.NotNull(created.Row);
        Assert.Equal("Published", created.Row.Version.Status); Assert.Equal(created.Row.Version.Id, created.Row.Definition.CurrentPublishedVersionId);
        Assert.Equal(1, created.Row.Version.VersionNumber);
        var failed = RecruitmentPipelineRepository.BuildSimilarPipelineDraft(source); failed.Stages[0].Actions[0].Id = 41;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SavePipelineConfigurationAsync(new() { Definition = new() { ClientId = 20, PipelineCode = "MUST_ROLLBACK", PipelineName = "Must rollback" }, Configuration = failed }, Admin));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_definitions WHERE PipelineCode='MUST_ROLLBACK'"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_versions"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_definitions"));
        Assert.Equal(11, await f.Db.ExecuteScalarAsync<int>("SELECT PipelineStageId FROM recruitment_pipeline_stage_actions WHERE Id=41"));
        Assert.Equal(source.ConfigurationHash, (await repo.GetPipelineVersionAsync(1, Admin))!.ConfigurationHash);
    }

    [LocalInterviewDatabaseFact]
    public async Task MappedStepRemovalOrSemanticChangeIsRejectedWithoutTouchingTheConfiguration()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        await f.Db.ExecuteAsync("INSERT INTO recruitment_position_stage_instances VALUES(200,11)");
        var source = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var changed = Request(source); changed.Configuration.Stages[0].StageType = "HR";
        var rejected = await repo.SavePipelineConfigurationAsync(changed, Admin);
        Assert.Null(rejected.Row); Assert.Contains("identity", rejected.Error);
        var removed = Request(source); removed.Configuration.Stages[0].Id = -9; removed.Configuration.Stages[0].StageCode = "REPLACEMENT";
        removed.Configuration.Transitions[0].Id = 0; removed.Configuration.Transitions[0].FromStageId = -9; removed.Configuration.Transitions[0].FromStageCode = "REPLACEMENT";
        rejected = await repo.SavePipelineConfigurationAsync(removed, Admin);
        Assert.Null(rejected.Row); Assert.Contains("cannot be deleted", rejected.Error);
        Assert.Equal(source.ConfigurationHash, (await repo.GetPipelineVersionAsync(1, Admin))!.ConfigurationHash);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_audit"));
    }

    [LocalInterviewDatabaseFact]
    public async Task ReplacingAnUnusedStepCanRetargetAnExistingEdgeWithoutLosingItsIdentity()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var source = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var request = Request(source); var stage = request.Configuration.Stages[0];
        stage.Id = -7; stage.StageCode = "REPLACED"; stage.StageName = "Replacement intake";
        stage.Actions[0].Id = 0; stage.Actions[0].Recipients[0].Id = 0;
        request.Configuration.Transitions[0].FromStageId = -7; request.Configuration.Transitions[0].FromStageCode = "REPLACED";
        var saved = await repo.SavePipelineConfigurationAsync(request, Admin);
        Assert.Empty(saved.Error); Assert.NotNull(saved.Row);
        Assert.NotEqual(11, saved.Row.Version.Stages[0].Id);
        Assert.Equal(21, saved.Row.Version.Transitions[0].Id); Assert.Equal(31, saved.Row.Version.Transitions[0].Rules[0].Id);
        Assert.Equal(saved.Row.Version.Stages[0].Id, saved.Row.Version.Transitions[0].FromStageId);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_stages WHERE Id=11"));
    }

    [LocalInterviewDatabaseFact]
    public async Task StaleConcurrentSaveAndForeignClientAreRejectedBeforeAnyPartialUpdate()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var source = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var first = Request(source); first.Definition.PipelineName = "First save wins";
        Assert.NotNull((await repo.SavePipelineConfigurationAsync(first, Admin)).Row);
        var stale = Request(source); stale.Definition.PipelineName = "Stale overwrite";
        await Assert.ThrowsAsync<RecruitmentPipelineConfigurationConflict>(() => repo.SavePipelineConfigurationAsync(stale, Admin));
        Assert.Equal("First save wins", await f.Db.ExecuteScalarAsync<string>("SELECT PipelineName FROM recruitment_pipeline_definitions WHERE Id=1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repo.SavePipelineConfigurationAsync(first, new() { Id = 9, ClientId = 30, IsActive = true, Permissions = ["settings.manage"] }));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_audit"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_versions"));
    }

    [LocalInterviewDatabaseFact]
    public async Task NewPipelineCannotStealExistingStageIdsAndPendingRulesStayBoundToTheirApproval()
    {
        await using var f = await Fixture(); var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var source = (await repo.GetPipelineVersionAsync(1, Admin))!;
        var stolen = Request(source); stolen.Definition.Id = 0; stolen.Definition.PipelineCode = "STOLEN"; stolen.Configuration.Id = 0;
        Assert.Null((await repo.SavePipelineConfigurationAsync(stolen, Admin)).Row);
        await f.Db.ExecuteAsync("INSERT INTO recruitment_hiring_case_advance_requests VALUES(300,11,'ADVANCE','Pending Approval')");
        var pending = Request(source); pending.Configuration.Transitions[0].Rules[0].DecimalValue = 99;
        var rejected = await repo.SavePipelineConfigurationAsync(pending, Admin);
        Assert.Null(rejected.Row); Assert.Contains("pending approval", rejected.Error);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_definitions"));
        Assert.Equal(77m, await f.Db.ExecuteScalarAsync<decimal>("SELECT DecimalValue FROM recruitment_pipeline_transition_rules WHERE Id=31"));
    }

    private static SaveRecruitmentPipelineConfiguration Request(RecruitmentPipelineVersion version) => new()
    {
        Definition = new() { Id = 1, ClientId = 20, PipelineCode = "SOURCE", PipelineName = "Fixture published journey", Description = "Fixture" },
        Configuration = JsonSerializer.Deserialize<SaveRecruitmentPipelineVersion>(JsonSerializer.Serialize(version))!, ExpectedConfigurationHash = version.ConfigurationHash
    };

    private static async Task<RecruitmentPipelineCloneTests.Database> Fixture()
    {
        var f = await RecruitmentPipelineCloneTests.Database.CreateAsync();
        await f.Db.ExecuteAsync("""
ALTER TABLE recruitment_audit ADD COLUMN OldValueJson JSON NULL;
ALTER TABLE recruitment_pipeline_stages ADD UNIQUE KEY UX_stage_code(PipelineVersionId,StageCode),ADD UNIQUE KEY UX_stage_order(PipelineVersionId,DisplayOrder);
ALTER TABLE recruitment_pipeline_transitions ADD CONSTRAINT FK_config_transition_from FOREIGN KEY (FromStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE, ADD CONSTRAINT FK_config_transition_to FOREIGN KEY (ToStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE;
ALTER TABLE recruitment_pipeline_transition_rules ADD CONSTRAINT FK_config_transition_rule FOREIGN KEY (TransitionId) REFERENCES recruitment_pipeline_transitions(Id) ON DELETE CASCADE;
INSERT INTO recruitment_templates VALUES(15,20,TRUE); UPDATE recruitment_pipeline_stage_actions SET TemplateId=15 WHERE Id=41;
CREATE TABLE recruitment_application_pipeline_instances(Id BIGINT,PipelineVersionId BIGINT);
CREATE TABLE recruitment_application_stage_instances(Id BIGINT,PipelineStageId BIGINT);
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT,PipelineVersionId BIGINT);
CREATE TABLE recruitment_position_stage_instances(Id BIGINT,PipelineStageId BIGINT);
CREATE TABLE recruitment_process_documents(Id BIGINT,PipelineStageId BIGINT);
CREATE TABLE recruitment_hiring_case_advance_requests(Id BIGINT,PipelineStageId BIGINT,OutcomeCode VARCHAR(80),Status VARCHAR(40));
CREATE TABLE recruitment_pipeline_transition_requests(Id BIGINT,TransitionId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_stage_action_executions(Id BIGINT,StageActionId BIGINT);
CREATE TABLE recruitment_profile_batch_notification_deliveries(Id BIGINT,StageActionId BIGINT);
CREATE TABLE recruitment_interviews(Id BIGINT,RoundConfigurationId BIGINT);
CREATE TABLE recruitment_interview_feedback_competency_scores(Id BIGINT,InterviewStageCompetencyId BIGINT);
CREATE TABLE recruitment_sla_adjustments(Id BIGINT,StageOfferConfigurationId BIGINT);
""");
        return f;
    }
}
