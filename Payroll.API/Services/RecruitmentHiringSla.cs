namespace Payroll.API.Services;

// Job status and countdown share one deadline. Stage breaches stay in stage history.
internal static class RecruitmentHiringSla
{
    internal static string PausedFor(string stage, string definition) => $"({definition}.PauseBehavior='ShiftStageAndOverall' AND EXISTS(SELECT 1 FROM recruitment_position_stage_pause_periods p WHERE p.PositionStageInstanceId={stage}.Id AND p.ResumedAtUtc IS NULL))";
    internal static string DueFor(string hiring, string stage, string definition, string? fallback = null)
    {
        var due = fallback is null ? $"{hiring}.OverallDueAtUtc" : $"COALESCE({hiring}.OverallDueAtUtc,{fallback})";
        return $"TIMESTAMPADD(SECOND,CASE WHEN {definition}.PauseBehavior='ShiftStageAndOverall' THEN COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,p.PausedAtUtc,UTC_TIMESTAMP(6))) FROM recruitment_position_stage_pause_periods p WHERE p.PositionStageInstanceId={stage}.Id AND p.ResumedAtUtc IS NULL),0) ELSE 0 END,{due})";
    }
    internal static string BreachedFor(string hiring, string due) => $"({due} IS NOT NULL AND COALESCE({hiring}.CompletedAtUtc,UTC_TIMESTAMP(6))>{due})";
    internal static string StageBreachedFor(string instance, string stage) => $"(NOT ({stage}.IsInitial=TRUE AND {stage}.TargetOffsetMinutes <=> 0) AND {instance}.EnteredAtUtc IS NOT NULL AND {instance}.DueAtUtc IS NOT NULL AND COALESCE({instance}.CompletedAtUtc,UTC_TIMESTAMP(6))>{instance}.DueAtUtc)";
}
