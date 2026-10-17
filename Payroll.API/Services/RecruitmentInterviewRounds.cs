using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Round definitions use the existing client-scoped module settings JSON. Each actual
// schedule/feedback keeps its existing interview ID, stage instance and RoundCode.
internal static class RecruitmentInterviewRounds
{
    internal static async Task<IAsyncDisposable> LockAsync(MySqlConnection db, int clientId, long positionId)
    {
        var name = $"recruitment-rounds:{clientId}:{positionId}";
        if (await db.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@Name,10)",new { Name=name }) != 1)
            throw new InvalidOperationException("This job's interview rounds are being updated. Retry after the current save finishes.");
        return new Lease(db,name);
    }
    private sealed class Lease(MySqlConnection db, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { if (db.State == System.Data.ConnectionState.Open) await db.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@Name)",new { Name=name }); }
    }
    internal static string Key(int clientId, long positionId, string stageCode) =>
        $"recruitment_rounds:{clientId}:{positionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stageCode.ToUpperInvariant()))).ToLowerInvariant()[..16]}";

    internal static string SettingsKeySql(string application, string stage) =>
        $"CONCAT('recruitment_rounds:',{application}.ClientId,':',{application}.PositionId,':',LEFT(SHA2(UPPER({stage}.StageCode),256),16))";

    // A previous round or previous visit to this stage cannot satisfy the current visit.
    // No settings row means the existing single-interview behavior remains in force.
    internal static string CompleteFor(string application, string? stageInstance = null) => $@"NOT EXISTS(
SELECT 1 FROM recruitment_application_pipeline_instances roundsFlow
JOIN recruitment_pipeline_stages roundsDefinition ON roundsDefinition.PipelineVersionId=roundsFlow.PipelineVersionId AND roundsDefinition.StageType='Interview' AND roundsDefinition.IsActive=TRUE
JOIN modulesettings roundsSettings ON roundsSettings.client_id={application}.ClientId
 AND roundsSettings.ModuleCode={SettingsKeySql(application, "roundsDefinition")} AND roundsSettings.IsEnabled=TRUE
LEFT JOIN recruitment_application_stage_instances roundsStage ON roundsStage.Id=(SELECT MAX(roundsVisit.Id) FROM recruitment_application_stage_instances roundsVisit
 JOIN recruitment_pipeline_stages roundsVisitDefinition ON roundsVisitDefinition.Id=roundsVisit.PipelineStageId
 WHERE roundsVisit.ApplicationPipelineInstanceId=roundsFlow.Id AND roundsVisitDefinition.StageCode=roundsDefinition.StageCode)
WHERE roundsFlow.ApplicationId={application}.Id
 {(stageInstance is null ? "" : $"AND roundsStage.Id={stageInstance}")}
 AND (COALESCE(JSON_TYPE(JSON_EXTRACT(roundsSettings.SettingsJson,'$.Rounds')),'')<>'ARRAY'
 OR COALESCE(JSON_LENGTH(JSON_EXTRACT(roundsSettings.SettingsJson,'$.Rounds')),0)=0
 OR EXISTS(SELECT 1 FROM JSON_TABLE(roundsSettings.SettingsJson,'$.Rounds[*]' COLUMNS(Name VARCHAR(80) PATH '$.Name')) requiredRound
 WHERE NOT EXISTS(SELECT 1 FROM recruitment_interviews passedRound
 WHERE passedRound.ApplicationId={application}.Id AND passedRound.PipelineStageInstanceId=roundsStage.Id
 AND BINARY passedRound.RoundCode=BINARY requiredRound.Name AND passedRound.Status='Completed' AND passedRound.Result='Selected'
 AND passedRound.Id=(SELECT MAX(lastAttempt.Id) FROM recruitment_interviews lastAttempt
 WHERE lastAttempt.ApplicationId={application}.Id AND lastAttempt.PipelineStageInstanceId=roundsStage.Id AND BINARY lastAttempt.RoundCode=BINARY requiredRound.Name)))))";

    internal static string ValidateDefinitions(IReadOnlyList<RecruitmentInterviewRoundDefinition> rounds,
        IReadOnlyList<RecruitmentInterviewRoundDefinition> previous, ISet<string> usedNames)
    {
        if (rounds.Count is < 1 or > 100) return "Add between 1 and 100 named interview rounds.";
        if (rounds.Any(r => string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 || string.IsNullOrWhiteSpace(r.InterviewType) || r.InterviewType.Length > 80))
            return "Each round needs a name and interview type of at most 80 characters.";
        if (rounds.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rounds.Count)
            return "Round names must be unique within this job's interview stage.";
        for (var i = 0; i < previous.Count; i++)
            if (usedNames.Contains(previous[i].Name) && (i >= rounds.Count || previous[i].Name != rounds[i].Name || previous[i].InterviewType != rounds[i].InterviewType))
                return "A scheduled round's name, type and position are preserved for its feedback/history. Add another round after it.";
        if (usedNames.Except(rounds.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).Any())
            return "Keep every existing scheduled round in this plan so its interview history remains linked.";
        return "";
    }

    internal static List<RecruitmentInterviewRoundState> States(IEnumerable<RecruitmentInterviewRoundDefinition> definitions,
        IEnumerable<RoundAttempt> attempts)
    {
        var latest = attempts.GroupBy(r => r.RoundCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!, StringComparer.OrdinalIgnoreCase);
        var previousPassed = true;
        return definitions.Select(round => {
            latest.TryGetValue(round.Name, out var attempt);
            var passed = attempt is { Status: "Completed", Result: "Selected" };
            var state = new RecruitmentInterviewRoundState { Name=round.Name,InterviewType=round.InterviewType,InterviewId=attempt?.Id,
                Status=attempt?.Status ?? "Pending",Result=attempt?.Result ?? "Pending",Passed=passed,
                CanSchedule=previousPassed && (attempt is null || attempt.Status is "Cancelled" or "No Show" || attempt is { Status:"Completed",Result:"On Hold" }) };
            previousPassed &= passed;
            return state;
        }).ToList();
    }

    internal sealed class RoundAttempt
    {
        public long Id { get; set; }
        public string RoundCode { get; set; } = "";
        public string InterviewType { get; set; } = "";
        public string Status { get; set; } = "";
        public string Result { get; set; } = "";
    }
}
