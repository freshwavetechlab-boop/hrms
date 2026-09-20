using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static class EngineActivityMetadata
{
    public static EngineActivityContext ForRequest(HttpContext context)
    {
        // Route TEMPLATE only. Never actual URLs/query strings/public access tokens.
        var route=(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "Unmatched engine request";
        long? Number(string name)=>long.TryParse(context.Request.RouteValues.GetValueOrDefault(name)?.ToString(),NumberStyles.None,CultureInfo.InvariantCulture,out var id) && id>0 ? id : null;
        var app=Number("applicationId"); var candidate=Number("candidateId"); var position=Number("positionId");
        var id=app ?? candidate ?? position ?? Number("id") ?? Number("runId") ?? Number("batchId");
        var kind=app.HasValue ? "Application" : candidate.HasValue ? "Candidate" : position.HasValue ? "Position"
            : route.StartsWith("/api/pay-runs") ? "Pay run" : "Task";
        return Clean(new EngineActivityContext($"{context.Request.Method} {route}",kind,id?.ToString(CultureInfo.InvariantCulture) ?? "",app,candidate,position));
    }
    public static EngineActivityContext Clean(EngineActivityContext value)=>value with
    {
        Operation=Regex.Replace(value.Operation,@"[\r\n\x00-\x1f]"," ")[..Math.Min(value.Operation.Length,180)],
        ReferenceType=value.ReferenceType is "Application" or "Candidate" or "Position" or "Pay run" or "Task" or "ATS job" or "Dashboard run" or "AI model" ? value.ReferenceType : "Task",
        ReferenceId=Regex.IsMatch(value.ReferenceId,@"^(?:[0-9]{1,19}|[a-fA-F0-9]{32})$") ? value.ReferenceId : "",
        ApplicationId=value.ApplicationId>0 ? value.ApplicationId : null,CandidateId=value.CandidateId>0 ? value.CandidateId : null,
        PositionId=value.PositionId>0 ? value.PositionId : null,ClientId=value.ClientId>0 ? value.ClientId : null,
        QueueWaitMs=value.QueueWaitMs is >=0 && double.IsFinite(value.QueueWaitMs.Value) ? value.QueueWaitMs : null,
        Attempt=value.Attempt>0 ? value.Attempt : null
    };
    public static string SafeAiStatus(string value)=> value is "Completed" or "LowConfidence" or "NotEnabled" or "NoText" or "ProviderError" or "TimedOut" or "ConfigurationError" or "UnsupportedInput" or "UsageLimitReached" or "LocalBusy" or "OutputTruncated" or "InvalidResponse" or "InputTooLarge" or "Failed" ? value : "Unavailable";
}
