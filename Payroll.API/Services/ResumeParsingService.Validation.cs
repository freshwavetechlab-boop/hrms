using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed partial class ResumeParsingService
{
    // Literal professional evidence only. This is a vocabulary for finding text,
    // not a list inferred from a designation, target JD, photograph or neighbour CV.
    private static readonly string[] ExplicitTechnologyTerms =
    [
        "AWS", "Azure", "GCP", "OpenShift", "OpenStack", "Kubernetes", "Docker", "Terraform", "Ansible",
        "Python", "Java", "JavaScript", "TypeScript", "C++", "C#", ".NET", "SQL", "PostgreSQL", "MySQL",
        "Oracle Database", "MongoDB", "Redis", "Kafka", "RabbitMQ", "Spring Boot", "React", "Angular",
        "Node.js", "Jenkins", "GitLab", "GitHub", "CI/CD", "DevOps", "DevSecOps", "MLOps", "SRE",
        "AI", "ML", "AI/ML", "GenAI", "Generative AI", "Artificial Intelligence", "Machine Learning",
        "Deep Learning", "NLP", "LLM", "LLMs", "RAG", "TensorFlow", "PyTorch", "LangChain", "Elasticsearch",
        "Cybersecurity", "Cloud Security", "Cloud Architecture", "Enterprise Architecture", "Microservices",
        "Data Engineering", "Data Science", "Data Analytics", "Zero Trust", "Blockchain", "IoT", "Linux",
        "REST", "GraphQL", "API Management", "API Security", "Power BI", "Tableau", "Snowflake", "Databricks"
    ];
    private static readonly Regex ExplicitTechnologyPattern = new(@"(?<![\p{L}\p{N}+#])(?:"
        + string.Join('|', ExplicitTechnologyTerms.OrderByDescending(term => term.Length).Select(Regex.Escape))
        + @")(?![\p{L}\p{N}+#])", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static IEnumerable<string> ExplicitProfessionalTechnologies(IReadOnlyList<ResumeParsedSection> sections)
    {
        foreach (var section in sections.Where(section => section.SectionCode is "SKILLS" or "SUMMARY" or "EXPERIENCE"))
        {
            foreach (var line in section.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsUnverifiedSkillClaim(line) || Regex.IsMatch(line, @"\b(?:required skills|requirements|seeking candidates|references for)\b", RegexOptions.IgnoreCase)) continue;
                foreach (Match match in ExplicitTechnologyPattern.Matches(line)) yield return match.Value;
            }
        }
    }
}

public sealed record ResumeFieldReviewIssue(string Field, string Code, string Message);

public static class ResumeParsingValidation
{
    public static IReadOnlyList<ResumeFieldReviewIssue> Review(ResumeParseResult parse)
    {
        if (parse.Status == "Disabled") return [];
        var issues = new List<ResumeFieldReviewIssue>(parse.AiReviewIssues);
        if (!string.IsNullOrWhiteSpace(parse.AiReviewStatus) && parse.AiReviewStatus != "Completed")
            issues.Add(new("AI review", "AiReviewUnavailable", "AI review did not complete (" + parse.AiReviewStatus + "); local parser details remain available for review."));
        if (parse.Status != "Parsed") return issues.Append(new ResumeFieldReviewIssue("Source", "UnreadableSource", "Resume text needs review; do not learn from an unreadable source.")).ToList();
        void Missing(string field, string value) { if (string.IsNullOrWhiteSpace(value)) issues.Add(new(field, "MissingEvidence", field + " was not established from the resume; confirm it against the original.")); }
        Missing("Name", parse.Facts.FullName);
        Missing("Current company", parse.Facts.CurrentCompany);
        Missing("Current designation", parse.Facts.CurrentTitle);
        Missing("Qualification", parse.Facts.HighestQualification);
        if (parse.Facts.Skills.Count == 0) issues.Add(new("Skills", "MissingEvidence", "No explicit skills were established; review the original resume."));
        if (parse.Facts.Skills.Count >= 100) issues.Add(new("Skills", "ExtractionLimit", "The skill list reached the parser limit; review the section boundaries and selected skills."));
        var datedRoles = parse.Sections.Where(section => section.SectionCode == "EXPERIENCE")
            .SelectMany(section => section.Content.Split('\n'))
            .Where(line => Regex.IsMatch(line, @"\b(?:chief|head|director|manager|president|cto|cio|cpto|vp)\b.*\b\d{1,2}[/.](?:19|20)\d{2}\b", RegexOptions.IgnoreCase));
        if (datedRoles.Any(line => !parse.Facts.Experience.Any(row => Normalize(row.Evidence).Contains(Normalize(line), StringComparison.Ordinal))))
            issues.Add(new("Employment dates", "DateReview", "An employment row has incomplete or ambiguous dates. Confirm the latest role and employer against the original resume."));
        return issues;
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();

    public static string ReviewSummary(ResumeParseResult parse) => string.Join(" ", parse.ReviewIssues
        .Where(issue => issue.Code is "DateReview" or "ExtractionLimit" or "AiReviewUnavailable" or "AiFieldConflict" or "AiFieldUnverified").Select(issue => issue.Message));
}
