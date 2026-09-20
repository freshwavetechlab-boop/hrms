using System.Text;
using System.Text.Json;

namespace Payroll.API.Models;

public sealed record RecruitmentTextLimit(string Field, string Label, int Maximum, string Unit = "characters");
public sealed record RecruitmentTextError(string Field, string Message);

/// <summary>One contract for parser review, browser validation and every requisition save path.</summary>
public static class RecruitmentRequisitionTextLimits
{
    // Match recruitment_requisitions and its open-position projection. TEXT limits are bytes,
    // VARCHAR limits are Unicode code points, not .NET/JavaScript UTF-16 string lengths.
    public static IReadOnlyList<RecruitmentTextLimit> Fields { get; } = Array.AsReadOnly(new RecruitmentTextLimit[]
    {
        new("businessUnit", "Business unit", 120),
        new("department", "Department", 120),
        new("costCenter", "Cost center", 120),
        new("positionTitle", "Role / position", 180),
        new("positionCategory", "Position category", 120),
        new("employmentType", "Employment type", 80),
        new("hiringType", "Hiring type", 80),
        new("jobLocation", "Work location", 160),
        new("workMode", "Work mode", 40),
        new("project", "Project", 160),
        new("hiringPriority", "Priority", 40),
        new("businessJustification", "Business justification", 65535, "UTF-8 bytes"),
        new("reasonForHiring", "Hiring notes", 500),
        new("experienceRange", "Experience", 80),
        new("qualification", "Qualification", 250),
        new("requiredSkills", "Required skills", 65535, "UTF-8 bytes"),
        new("preferredSkills", "Preferred skills", 65535, "UTF-8 bytes"),
        new("certifications", "Certifications", 500),
        new("languages", "Languages", 250),
        new("currency", "Currency", 10),
        new("benefits", "Benefits", 65535, "UTF-8 bytes"),
        new("externalPositionCode", "Client position code", 80),
        new("sourceType", "Source type", 80),
        new("sourceReference", "Source reference", 240),
        new("sourceDocumentName", "Source document", 500),
        new("sourceAuthority", "Source authority", 240),
        new("externalApprovalStatus", "Client approval state", 80),
        new("sourceNotes", "Source notes", 65535, "UTF-8 bytes"),
    });

    private static readonly IReadOnlyDictionary<string, System.Reflection.PropertyInfo> Properties =
        typeof(SaveRecruitmentRequisition).GetProperties().Where(p => p.PropertyType == typeof(string))
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

    public static List<RecruitmentTextError> Validate(SaveRecruitmentRequisition request)
    {
        var errors = new List<RecruitmentTextError>();
        foreach (var rule in Fields)
        {
            var value = Properties[rule.Field].GetValue(request) as string ?? "";
            var length = rule.Unit == "characters" ? value.EnumerateRunes().Count() : Encoding.UTF8.GetByteCount(value);
            if (length > rule.Maximum)
                errors.Add(new(rule.Field, $"{rule.Label}: maximum {rule.Maximum} {rule.Unit}; received {length}. Shorten this field before saving; keep the full detail in the source document or source notes."));
        }
        return errors;
    }

    public static RecruitmentRequestDocumentParseResult Review(RecruitmentRequestDocumentParseResult result)
    {
        var errors = Validate(result.Draft);
        if (errors.Count == 0) return result;
        result.Status = "NeedsReview";
        foreach (var error in errors)
        {
            var label = Fields.First(rule => rule.Field == error.Field).Label;
            if (!result.ReviewFields.Contains(label)) result.ReviewFields.Add(label);
            if (!result.Warnings.Contains(error.Message)) result.Warnings.Add(error.Message);
        }
        // Keep the original extracted draft and structured evidence intact for manual review.
        return result;
    }
}
