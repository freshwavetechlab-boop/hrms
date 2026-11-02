namespace Payroll.API.Services;

/// <summary>One switch policy for every resume entry point. AI remains an opt-in
/// fallback, and cannot run while resume parsing is disabled.</summary>
internal sealed record ResumeParsingJobPolicy(bool ParsingEnabled, bool AiEnabled)
{
    internal static ResumeParsingJobPolicy Resolve(bool clientParsingEnabled,
        bool? postingParsingEnabled, bool? postingAiEnabled,
        bool? parsingOverride = null, bool? aiOverride = null)
    {
        var parsing = parsingOverride ?? postingParsingEnabled ?? clientParsingEnabled;
        return new(parsing, parsing && (aiOverride ?? postingAiEnabled ?? false));
    }
}
