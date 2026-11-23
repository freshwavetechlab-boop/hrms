using System.Text;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    internal static string ValidateJobDescriptionFieldLengths(SaveRecruitmentJobDescriptionVersion request)
    {
        static string Characters(string? value, int maximum, string label) => (value ?? "").Trim().EnumerateRunes().Count() > maximum
            ? $"{label} must be {maximum} characters or fewer. Review this field before saving; the submitted text was not shortened." : "";
        static string Text(string? value, string label) => Encoding.UTF8.GetByteCount((value ?? "").Trim()) > 65535
            ? $"{label} exceeds 65535 UTF-8 bytes. Split the full wording across appropriate rows before saving; the submitted text was not shortened." : "";

        var errors = new List<string>
        {
            Characters(request.Title, 180, "Public role title"),
            Text(request.Summary, "Role summary"), Text(request.RolePurpose, "Role purpose")
        };
        if (request.Responsibilities is null || request.Skills is null || request.Certifications is null || request.Languages is null || request.Benefits is null)
            return "JD detail rows are invalid. Reload the JD and try again.";
        for (var i = 0; i < request.Responsibilities.Count; i++)
        {
            var row = request.Responsibilities[i];
            if (row is null) return $"Responsibility row {i + 1} is invalid.";
            errors.Add(Text(row.ResponsibilityText, $"Responsibility row {i + 1}"));
        }
        for (var i = 0; i < request.Skills.Count; i++)
        {
            var row = request.Skills[i];
            if (row is null) return $"Skill row {i + 1} is invalid.";
            errors.Add(Characters(row.SkillName, 180, $"Skill row {i + 1}: skill name"));
            errors.Add(Characters(row.MinimumProficiency, 80, $"Skill row {i + 1}: reviewer level"));
        }
        for (var i = 0; i < request.Certifications.Count; i++)
        {
            var row = request.Certifications[i];
            if (row is null) return $"Certification row {i + 1} is invalid.";
            errors.Add(Text(row.CertificationName, $"Certification row {i + 1}"));
        }
        for (var i = 0; i < request.Languages.Count; i++)
        {
            var row = request.Languages[i];
            if (row is null) return $"Language row {i + 1} is invalid.";
            errors.Add(Characters(row.LanguageName, 120, $"Language row {i + 1}: language name"));
            errors.Add(Characters(row.Proficiency, 80, $"Language row {i + 1}: proficiency"));
        }
        for (var i = 0; i < request.Benefits.Count; i++)
        {
            var row = request.Benefits[i];
            if (row is null) return $"Benefit row {i + 1} is invalid.";
            errors.Add(Characters(row.BenefitName, 180, $"Benefit row {i + 1}: benefit name"));
            errors.Add(Characters(row.Description, 1000, $"Benefit row {i + 1}: description"));
        }
        return errors.FirstOrDefault(error => error.Length > 0) ?? "";
    }
}
