using System.Reflection;
using Dapper;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class RecruitmentRevisionRegressionTests
{
    [LocalInterviewDatabaseFact]
    public async Task Configured_all_rejected_rollback_is_scoped_idempotent_and_retains_each_visit()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(InterviewObservationSchema);
        await db.ExecuteAsync("ALTER TABLE recruitment_offers ADD COLUMN PipelineStageInstanceId BIGINT NULL");
        await db.ExecuteAsync(@"INSERT INTO recruitment_pipeline_stages(Id,StageCode,StageName,StageType,CardScope,DisplayOrder)
VALUES(9,'REJECTED','Rejected','Rejected','Application',99);
UPDATE recruitment_application_stage_instances SET PipelineStageId=9 WHERE Id=51;
UPDATE recruitment_candidate_applications SET CurrentStage='Rejected' WHERE Id=100;");
        async Task Return() => await (Task<string>)typeof(Payroll.API.Repositories.RecruitmentCaseRepository)
            .GetMethod("ReturnHiringCaseForAllRejectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [db, 10L, 1])!;
        async Task<long> Current() => await db.ExecuteScalarAsync<long>("SELECT CurrentStageInstanceId FROM recruitment_position_pipeline_instances WHERE Id=5");
        async Task Rule(int client, bool enabled) => await db.ExecuteAsync(@"DELETE FROM modulesettings WHERE client_id=@Client;
INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Key,TRUE,@Json)",
            new { Client = client, Key = RecruitmentRollbackRules.Key(1), Json = System.Text.Json.JsonSerializer.Serialize(new { Enabled = enabled, TargetStageCode = "SHARING_PROFILES" }) });
        await Return(); Assert.Equal(61, await Current()); // Legacy/default off.
        await Rule(21, true); await Return(); Assert.Equal(61, await Current()); // Other client cannot enable it.
        await Rule(20, false); await Return(); Assert.Equal(61, await Current());
        await Rule(20, true);
        await db.ExecuteAsync("INSERT INTO recruitment_candidate_applications(Id,PositionId,ClientId,CurrentStage) VALUES(200,10,20,'ATS')");
        await Return(); Assert.Equal(61, await Current()); // One surviving application is enough.
        await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET CurrentStage='Withdrawn' WHERE Id=200");
        await Return(); var firstReturn = await Current(); Assert.NotEqual(61, firstReturn);
        Assert.Equal("ALL_PROFILES_REJECTED", await db.ExecuteScalarAsync<string>("SELECT OutcomeCode FROM recruitment_position_stage_instances WHERE Id=61"));
        Assert.Equal("Superseded", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_hiring_case_advance_requests LIMIT 1"));
        Assert.Equal("Signed", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_process_documents WHERE Id=1"));
        Assert.Equal("Selected", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=1"));
        var anchor = await db.ExecuteScalarAsync<DateTime>("SELECT SlaAnchorAtUtc FROM recruitment_position_pipeline_instances WHERE Id=5");
        await Return(); Assert.Equal(firstReturn, await Current());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_events"));
        await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances SET Status='Completed',CompletedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id;
INSERT INTO recruitment_position_stage_instances(PositionPipelineInstanceId,PipelineStageId,Status,EnteredAtUtc) VALUES(5,2,'Active',UTC_TIMESTAMP());
UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=LAST_INSERT_ID() WHERE Id=5", new { Id = firstReturn });
        await Return(); Assert.NotEqual(firstReturn, await Current());
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_instances WHERE PipelineStageId=1"));
        Assert.Equal(anchor, await db.ExecuteScalarAsync<DateTime>("SELECT SlaAnchorAtUtc FROM recruitment_position_pipeline_instances WHERE Id=5"));
    }
}
