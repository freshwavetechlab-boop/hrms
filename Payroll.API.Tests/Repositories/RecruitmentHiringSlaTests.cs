using Dapper;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class RecruitmentRevisionRegressionTests
{
    [LocalInterviewDatabaseFact]
    public async Task Job_sla_uses_overall_deadline_with_completion_and_configured_pause_behavior()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync("""
CREATE TABLE hiring(Id BIGINT,OverallDueAtUtc DATETIME(6),CompletedAtUtc DATETIME(6));
CREATE TABLE instance(Id BIGINT,EnteredAtUtc DATETIME(6),DueAtUtc DATETIME(6),CompletedAtUtc DATETIME(6));
CREATE TABLE stage(IsInitial BOOLEAN,TargetOffsetMinutes INT,PauseBehavior VARCHAR(40));
CREATE TABLE workOrder(DueAtUtc DATETIME(6));
CREATE TABLE recruitment_position_stage_pause_periods(PositionStageInstanceId BIGINT,PausedAtUtc DATETIME(6),ResumedAtUtc DATETIME(6));
INSERT INTO hiring VALUES(1,TIMESTAMPADD(DAY,27,UTC_TIMESTAMP(6)),NULL);
INSERT INTO instance VALUES(1,TIMESTAMPADD(DAY,-2,UTC_TIMESTAMP(6)),TIMESTAMPADD(DAY,-1,UTC_TIMESTAMP(6)),NULL);
INSERT INTO stage VALUES(TRUE,0,'ShiftStageAndOverall');
INSERT INTO workOrder VALUES(TIMESTAMPADD(DAY,20,UTC_TIMESTAMP(6)));
""");
        var due = RecruitmentHiringSla.DueFor("hiring", "instance", "stage", "workOrder.DueAtUtc");
        var query = "SELECT " + due + " Due," + RecruitmentHiringSla.BreachedFor("hiring", due) + " Breached,"
            + RecruitmentHiringSla.PausedFor("instance", "stage") + " Paused,"
            + RecruitmentHiringSla.StageBreachedFor("instance", "stage") + " StageBreached FROM hiring CROSS JOIN instance CROSS JOIN stage CROSS JOIN workOrder";
        Assert.False((await db.QuerySingleAsync<SlaCheck>(query)).StageBreached); // Initial day-zero anchor.
        await db.ExecuteAsync("UPDATE stage SET TargetOffsetMinutes=NULL");
        Assert.True((await db.QuerySingleAsync<SlaCheck>(query)).StageBreached); // A timed initial stage is not an anchor.
        await db.ExecuteAsync("UPDATE stage SET IsInitial=FALSE,TargetOffsetMinutes=1440");
        var result = await db.QuerySingleAsync<SlaCheck>(query);
        Assert.True(result.StageBreached);
        Assert.False(result.Breached); // A missed stage target does not breach the overall job.
        await db.ExecuteAsync("UPDATE hiring SET OverallDueAtUtc=TIMESTAMPADD(DAY,-2,UTC_TIMESTAMP(6))");
        Assert.True((await db.QuerySingleAsync<SlaCheck>(query)).Breached);
        await db.ExecuteAsync("INSERT INTO recruitment_position_stage_pause_periods VALUES(1,TIMESTAMPADD(DAY,-5,UTC_TIMESTAMP(6)),NULL)");
        result = await db.QuerySingleAsync<SlaCheck>(query);
        Assert.True(result.Paused);
        Assert.False(result.Breached);
        foreach (var behavior in new[] { "ShiftStageOnly", "NoShift" })
        {
            await db.ExecuteAsync("UPDATE stage SET PauseBehavior=@behavior", new { behavior });
            result = await db.QuerySingleAsync<SlaCheck>(query);
            Assert.False(result.Paused);
            Assert.True(result.Breached);
        }
        await db.ExecuteAsync("UPDATE hiring SET CompletedAtUtc=TIMESTAMPADD(DAY,-3,UTC_TIMESTAMP(6))");
        Assert.False((await db.QuerySingleAsync<SlaCheck>(query)).Breached); // Completed before the deadline.
        await db.ExecuteAsync("UPDATE hiring SET CompletedAtUtc=TIMESTAMPADD(DAY,-1,UTC_TIMESTAMP(6))");
        Assert.True((await db.QuerySingleAsync<SlaCheck>(query)).Breached);
        await db.ExecuteAsync("UPDATE hiring SET CompletedAtUtc=NULL,OverallDueAtUtc=NULL");
        Assert.False((await db.QuerySingleAsync<SlaCheck>(query)).Breached); // Work-order fallback.
        await db.ExecuteAsync("UPDATE workOrder SET DueAtUtc=NULL");
        Assert.Null((await db.QuerySingleAsync<SlaCheck>(query)).Due);
        Assert.False((await db.QuerySingleAsync<SlaCheck>(query)).Breached);
    }

    private sealed class SlaCheck
    {
        public DateTime? Due { get; set; }
        public bool Breached { get; set; }
        public bool Paused { get; set; }
        public bool StageBreached { get; set; }
    }
}
