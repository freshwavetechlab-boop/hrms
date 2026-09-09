using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class RecruitmentRequestDocumentParsingService(ResumeParsingService documentTextParser)
{
    private static readonly string[] Headings =
    [
        "job summary", "role summary", "role overview", "role purpose", "particulars", "position", "position name", "designation",
        "number of positions", "no of positions", "openings", "vacancies", "band", "band/salary band", "salary band", "ctc", "location",
        "educational qualification", "educational qualifications", "qualification", "qualifications", "education", "experience",
        "experience, educational qualification", "educational qualification, experience", "requirements", "technical skills",
        "technical & analytics skills", "key skills", "key skills & technical expertise", "job profile", "job profile/duties",
        "key responsibilities", "roles and responsibilities", "responsibilities", "preferred certifications", "certifications",
        "languages", "language", "benefits", "perks", "desirable skills", "preferred skills", "good to have"
    ];

    public async Task<RecruitmentRequestDocumentParseResult> ParseAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var parsed = await documentTextParser.ParseAsync(file, cancellationToken);
        var text = Normalize(parsed.Text);
        var draft = new SaveRecruitmentRequisition
        {
            Id = 0,
            SourceType = "Job Description",
            SourceDocumentName = Path.GetFileName(file.FileName),
            ExternalApprovalStatus = "JD Received",
            SourceNotes = $"Parser-assisted prefill from {Path.GetFileName(file.FileName)}. Verify extracted values before saving.",
            NumberOfOpenings = 1,
            Currency = "INR"
        };
        var detected = new List<string> { "sourceType", "sourceDocumentName", "externalApprovalStatus", "sourceNotes" };
        var warnings = new List<string>();

        Set("positionTitle", PositionTitle(text, file.FileName), value => draft.PositionTitle = value);
        Set("department", Labeled(text, "department", "division", "business unit"), value => draft.Department = value);
        Set("jobLocation", Labeled(text, "location", "place of posting", "work location"), value => draft.JobLocation = value);
        Set("externalPositionCode", Labeled(text, "position code", "post code", "reference code", "job code"), value => draft.ExternalPositionCode = value);
        Set("sourceReference", Labeled(text, "file no", "file number", "reference no", "reference number", "computer no"), value => draft.SourceReference = value);
        Set("sourceAuthority", Authority(text), value => draft.SourceAuthority = value);
        Set("experienceRange", Experience(text), value => draft.ExperienceRange = value);
        Set("qualification", Qualifications(text), value => draft.Qualification = value);
        Set("requiredSkills", RequiredSkills(text), value => draft.RequiredSkills = value);
        Set("preferredSkills", Block(text, "preferred skills", "desirable skills", "good to have"), value => draft.PreferredSkills = CompactList(value, 1800));
        Set("certifications", Block(text, "preferred certifications", "certifications"), value => draft.Certifications = CompactList(value, 480));
        Set("languages", Languages(text), value => draft.Languages = value);
        Set("benefits", Block(text, "benefits", "perks"), value => draft.Benefits = CompactList(value, 1800));

        var summary = Block(text, "job summary", "role summary", "role overview", "role purpose");
        if (string.IsNullOrWhiteSpace(summary)) summary = FirstUsefulParagraph(text);
        Set("businessJustification", Limit(summary, 3800), value => draft.BusinessJustification = value);

        var responsibilityBlock = Block(text, "key responsibilities", "roles and responsibilities", "responsibilities", "job profile/duties", "job profile");
        var responsibilities = Items(responsibilityBlock).Take(20).ToList();
        var rolePurpose = responsibilities.Count > 0 ? string.Join("\n", responsibilities.Take(10)) : summary;
        Set("reasonForHiring", Limit(rolePurpose, 480), value => draft.ReasonForHiring = value);

        var openings = NumberOfOpenings(text);
        if (openings.HasValue)
        {
            draft.NumberOfOpenings = openings.Value;
            detected.Add("numberOfOpenings");
        }
        var salary = Salary(text);
        if (salary.Maximum > 0)
        {
            draft.SalaryMin = salary.Minimum;
            draft.SalaryMax = salary.Maximum;
            draft.BudgetAvailable = true;
            draft.BudgetAmount = salary.Maximum;
            detected.AddRange(["salaryMin", "salaryMax", "budgetAvailable", "budgetAmount"]);
        }
        Set("employmentType", EmploymentType(text), value => draft.EmploymentType = value);
        draft.WorkMode = Regex.IsMatch(text, @"\bremote\b", RegexOptions.IgnoreCase) ? "Remote"
            : Regex.IsMatch(text, @"\bhybrid\b", RegexOptions.IgnoreCase) ? "Hybrid" : "Office";
        detected.Add("workMode");
        if (LooksTechnical(text))
        {
            draft.PositionCategory = "Technical";
            detected.Add("positionCategory");
        }

        var structured = new
        {
            schema = "recruitment-request-source/v1",
            parsedAtUtc = DateTime.UtcNow,
            originalFileName = draft.SourceDocumentName,
            parserName = parsed.ParserName,
            parserVersion = parsed.ParserVersion,
            roleSummary = summary,
            rolePurpose,
            responsibilities,
            requiredSkills = Items(draft.RequiredSkills),
            preferredSkills = Items(draft.PreferredSkills),
            qualifications = Items(draft.Qualification),
            certifications = Items(draft.Certifications),
            languages = Items(draft.Languages),
            benefits = Items(draft.Benefits)
        };
        draft.SourceParsedJson = JsonSerializer.Serialize(structured, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        detected.Add("sourceParsedJson");

        var required = new Dictionary<string, string>
        {
            ["positionTitle"] = "Role / position", ["department"] = "Department", ["numberOfOpenings"] = "Openings",
            ["requestedByEmployeeId"] = "Requested by", ["clientId"] = "Client"
        };
        var review = required.Where(row => !detected.Contains(row.Key, StringComparer.OrdinalIgnoreCase)).Select(row => row.Value).ToList();
        if (string.IsNullOrWhiteSpace(text)) warnings.Add("Readable text was not found. The original file can still be saved and reviewed manually.");
        if (!string.IsNullOrWhiteSpace(parsed.Error)) warnings.Add(parsed.Error);
        if (review.Count > 0) warnings.Add("Complete the remaining business fields before saving; parser suggestions never auto-submit the request.");

        return new RecruitmentRequestDocumentParseResult
        {
            Status = string.IsNullOrWhiteSpace(text) ? "NeedsReview" : parsed.Status,
            ParserName = parsed.ParserName,
            ParserVersion = parsed.ParserVersion,
            OriginalFileName = draft.SourceDocumentName,
            Draft = draft,
            DetectedFields = detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ReviewFields = review,
            Warnings = warnings.Distinct().ToList()
        };

        void Set(string field, string value, Action<string> setter)
        {
            value = CleanValue(value);
            if (string.IsNullOrWhiteSpace(value)) return;
            setter(value);
            detected.Add(field);
        }
    }

    private static string PositionTitle(string text, string fileName)
    {
        var title = Labeled(text, "job description", "position name", "position", "designation", "job title", "role title");
        if (!string.IsNullOrWhiteSpace(title) && title.Length <= 180) return title;
        var fallback = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"(?i)^JD[_\-\s]*|[_]+", " ");
        return Regex.Replace(fallback, @"\s+", " ").Trim();
    }

    private static string Authority(string text)
    {
        var labelled = Labeled(text, "requesting authority", "source authority", "organisation", "organization", "client");
        if (!string.IsNullOrWhiteSpace(labelled)) return labelled;
        return Regex.IsMatch(text, @"\bUIDAI\b|Unique Identification Authority of India", RegexOptions.IgnoreCase) ? "UIDAI" : "";
    }

    private static int? NumberOfOpenings(string text)
    {
        foreach (var pattern in new[]
        {
            @"(?im)(?:number|no\.?|total)\s+of\s+(?:positions?|vacancies|openings?)\s*[:\-]?\s*(?<n>\d{1,3})",
            @"(?im)\b(?<n>\d{1,3})\s*(?:positions?|vacancies|openings?)\b"
        })
        {
            var match = Regex.Match(text, pattern);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out var number) && number is > 0 and <= 999) return number;
        }
        return null;
    }

    private static string Experience(string text)
    {
        var range = Regex.Match(text, @"(?i)(?<min>\d{1,2})\s*(?:-|–|to)\s*(?<max>\d{1,2})\s*\+?\s*(?:years?|yrs?)");
        if (range.Success) return $"{range.Groups["min"].Value}-{range.Groups["max"].Value} years";
        var values = Regex.Matches(text, @"(?i)(?:minimum\s+(?:of\s+)?)?(?<years>\d{1,2})\s*\+?\s*(?:years?|yrs?)(?:\s+of)?\s+(?:relevant\s+|professional\s+|hands-on\s+|total\s+)?experience")
            .Select(match => int.TryParse(match.Groups["years"].Value, out var years) ? years : 0).Where(years => years is > 0 and <= 60).ToList();
        return values.Count > 0 ? $"{values.Min()}+ years" : "";
    }

    private static string Qualifications(string text)
    {
        var block = Block(text, "educational qualification", "educational qualifications", "qualification", "qualifications", "education");
        var degree = @"(?i)\b(B\.?\s*E\.?|B\.?\s*Tech|M\.?\s*Tech|MCA|MSc|Bachelor|Master|degree|post.?graduat|equivalent)\b";
        var candidates = Items(block).Where(line => Regex.IsMatch(line, degree)).Take(8).ToList();
        if (candidates.Count == 0)
            candidates = text.Split('\n').Select(CleanValue).Where(line => line.Length <= 300 && Regex.IsMatch(line, degree)).Take(8).ToList();
        return string.Join("; ", candidates.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string RequiredSkills(string text)
    {
        var block = Block(text, "technical & analytics skills", "technical skills", "key skills & technical expertise", "key skills", "skills", "requirements");
        var lines = Items(block).Where(line => line.Length is >= 2 and <= 300).Take(20).ToList();
        if (lines.Count == 0)
            lines = text.Split('\n').Select(CleanValue).Where(line => line.Length <= 220 && Regex.IsMatch(line, @"(?i)\b(SQL|Python|Java|\.NET|React|Angular|AWS|Azure|cloud|database|analytics|machine learning|DevOps|Docker|Kubernetes|Power BI|Tableau)\b")).Take(16).ToList();
        return string.Join("; ", lines.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Languages(string text)
    {
        var block = Block(text, "languages", "language");
        if (!string.IsNullOrWhiteSpace(block)) return CompactList(block, 230);
        return string.Join(", ", new[] { "English", "Hindi" }.Where(language => Regex.IsMatch(text, $@"\b{language}\b", RegexOptions.IgnoreCase)));
    }

    private static (decimal Minimum, decimal Maximum) Salary(string text)
    {
        var range = Regex.Match(text, @"(?i)(?<min>\d+(?:\.\d+)?)\s*(?:-|–|to)\s*(?<max>\d+(?:\.\d+)?)\s*(?:LPA|lakhs?|lacs?)");
        if (range.Success && decimal.TryParse(range.Groups["min"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var min) && decimal.TryParse(range.Groups["max"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var max)) return (min * 100000m, max * 100000m);
        var upto = Regex.Match(text, @"(?i)(?:up\s*to|upto|ctc\s*[:\-]?)\s*(?<max>\d+(?:\.\d+)?)\s*(?:LPA|lakhs?|lacs?)");
        if (upto.Success && decimal.TryParse(upto.Groups["max"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out max)) return (0, max * 100000m);
        return (0, 0);
    }

    private static string EmploymentType(string text)
    {
        if (Regex.IsMatch(text, @"\bcontract(?:ual)?\b|fixed[ -]term", RegexOptions.IgnoreCase)) return "Contract";
        if (Regex.IsMatch(text, @"\bpermanent\b|regular employment", RegexOptions.IgnoreCase)) return "Permanent";
        if (Regex.IsMatch(text, @"\bintern(?:ship)?\b", RegexOptions.IgnoreCase)) return "Intern";
        return "";
    }

    private static bool LooksTechnical(string text) => Regex.IsMatch(text, @"(?i)\b(software|database|developer|architect|engineer|data scientist|analytics|machine learning|cloud|DevOps|cyber|IT infrastructure|biometric)\b");

    private static string Labeled(string text, params string[] labels)
    {
        foreach (var label in labels.OrderByDescending(value => value.Length))
        {
            var escaped = Regex.Escape(label).Replace("\\ ", @"\s+");
            var match = Regex.Match(text, $@"(?im)^\s*{escaped}\s*(?:[:\-–]\s*|\s+)(?<value>[^\r\n]{{1,300}})$");
            if (!match.Success) continue;
            var value = CleanValue(match.Groups["value"].Value);
            if (!Headings.Contains(value, StringComparer.OrdinalIgnoreCase)) return value;
        }
        return "";
    }

    private static string Block(string text, params string[] targets)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = CleanValue(lines[index]);
            var target = targets.FirstOrDefault(value => StartsWithHeading(line, value));
            if (target is null) continue;
            var values = new List<string>();
            var remainder = Regex.Replace(line, $@"(?i)^\s*{Regex.Escape(target)}\s*[:\-–]?\s*", "").Trim();
            if (!string.IsNullOrWhiteSpace(remainder)) values.Add(remainder);
            for (var next = index + 1; next < lines.Length && values.Sum(value => value.Length) < 5000; next++)
            {
                var candidate = CleanValue(lines[next]);
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (IsHeading(candidate)) break;
                if (Regex.IsMatch(candidate, @"^\d+\s*\|\s*P\s*a\s*g\s*e$", RegexOptions.IgnoreCase)) continue;
                values.Add(candidate);
            }
            var result = string.Join("\n", values).Trim();
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        return "";
    }

    private static bool StartsWithHeading(string line, string heading) => Regex.IsMatch(line, $@"(?i)^\s*{Regex.Escape(heading)}(?:\s*[:\-–]|\s*$|\s+)");
    private static bool IsHeading(string line) => Headings.Any(heading => StartsWithHeading(line, heading) && line.Length <= heading.Length + 80);

    private static string FirstUsefulParagraph(string text)
    {
        var lines = text.Split('\n').Select(CleanValue).Where(line => line.Length >= 35 && !IsHeading(line)).Take(5);
        return Limit(string.Join(" ", lines), 1000);
    }

    private static List<string> Items(string value) => (value ?? "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(CleanValue).Where(item => item.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string CompactList(string value, int maximum) => Limit(string.Join("; ", Items(value)), maximum);
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(1, maximum - 1)].TrimEnd() + "…";
    private static string CleanValue(string value)
    {
        value = Regex.Replace(value ?? "", @"^\s*(?:[•·*?\-]+|o\s+)", "", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"\s+", " ").Trim(' ', ':', '-', '–', '|');
    }

    private static string Normalize(string value)
    {
        value = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        value = Regex.Replace(value, @"[ \t]+", " ");
        return Regex.Replace(value, @"\n{3,}", "\n\n").Trim();
    }
}
