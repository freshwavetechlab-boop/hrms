using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class RecruitmentRequestDocumentParsingService(
    ResumeParsingService documentTextParser,
    RecruitmentAiScoringService aiScoring,
    RecruitmentDocumentRagService documentRag)
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

    public async Task<RecruitmentRequestDocumentParseResult> ParseAsync(IFormFile file, int clientId, CancellationToken cancellationToken)
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
            HiringType = "Contractual",
            EmploymentType = "Contractual",
            HiringPriority = "High",
            IsReplacement = false,
            BudgetAvailable = false,
            Languages = "English",
            Benefits = "As per company norms",
            Currency = "INR",
            WorkMode = "Office"
        };
        var detected = new List<string>
        {
            "sourceType", "sourceDocumentName", "externalApprovalStatus", "sourceNotes",
            "numberOfOpenings", "hiringType", "employmentType", "hiringPriority",
            "isReplacement", "budgetAvailable", "languages", "benefits", "currency", "workMode"
        };
        var warnings = new List<string>();
        var fieldMetadata = new Dictionary<string, RecruitmentAiHiringFieldTrace>(StringComparer.OrdinalIgnoreCase)
        {
            ["numberOfOpenings"] = DefaultTrace(), ["hiringType"] = DefaultTrace(), ["employmentType"] = DefaultTrace(),
            ["hiringPriority"] = DefaultTrace(), ["isReplacement"] = DefaultTrace(), ["budgetAvailable"] = DefaultTrace(),
            ["languages"] = DefaultTrace(), ["benefits"] = DefaultTrace(), ["currency"] = DefaultTrace(),
            ["sourceType"] = DefaultTrace(), ["sourceDocumentName"] = ExactTrace(), ["externalApprovalStatus"] = DefaultTrace(), ["sourceNotes"] = DefaultTrace()
        };

        Set("positionTitle", PositionTitle(text, file.FileName), value => draft.PositionTitle = value);
        Set("department", Labeled(text, "department", "division", "business unit"), value => draft.Department = value);
        Set("jobLocation", JobLocation(text), value => draft.JobLocation = value);
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
            fieldMetadata["numberOfOpenings"] = ExactTrace();
        }
        var salary = Salary(text);
        if (salary.Maximum > 0)
        {
            draft.SalaryMin = salary.Minimum;
            draft.SalaryMax = salary.Maximum;
            detected.AddRange(["salaryMin", "salaryMax"]);
            fieldMetadata["salaryMin"] = ExactTrace();
            fieldMetadata["salaryMax"] = ExactTrace();
        }
        Set("employmentType", EmploymentType(text), value => draft.EmploymentType = value);
        var extractedWorkMode = WorkMode(text);
        if (!string.IsNullOrWhiteSpace(extractedWorkMode))
            Set("workMode", extractedWorkMode, value => draft.WorkMode = value);
        else fieldMetadata["workMode"] = DefaultTrace();
        if (LooksTechnical($"{draft.PositionTitle}\n{text}"))
        {
            draft.PositionCategory = "Technical";
            detected.Add("positionCategory");
            fieldMetadata["positionCategory"] = InferredTrace();
        }

        byte[]? sourceDocument = null;
        var sourceContentType = "";
        var isPdf = Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var needsDocumentVision = isPdf && (string.IsNullOrWhiteSpace(text)
            || parsed.ParserName.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase)
            || !LooksLikeRecruitmentText(text));
        if (needsDocumentVision && file.Length <= 10 * 1024 * 1024)
        {
            await using var source = file.OpenReadStream();
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
            sourceDocument = memory.ToArray();
            sourceContentType = "application/pdf";
        }
        var retrieval = await documentRag.RetrieveAsync(clientId, RecruitmentDocumentRagService.HiringDocumentKind,
            [draft.PositionTitle, draft.Department, draft.RequiredSkills, draft.Qualification, text], cancellationToken);
        var vocabularyDecisions = new List<RecruitmentRagVocabularyMatch>();
        var ai = await aiScoring.SuggestHiringDocumentAsync(clientId, text, sourceDocument, sourceContentType, retrieval, cancellationToken);
        if (ai.Status is "Completed" or "LowConfidence")
        {
            SetAi("positionTitle", Limit(ai.PositionTitle, 190), value => draft.PositionTitle = value);
            SetAi("department", Limit(ai.Department, 180), value => draft.Department = value);
            SetAi("businessUnit", Limit(ai.BusinessUnit, 180), value => draft.BusinessUnit = value, requireExact: true);
            SetAi("costCenter", Limit(ai.CostCenter, 120), value => draft.CostCenter = value, requireExact: true);
            SetAi("jobLocation", Limit(ai.JobLocation, 240), value => draft.JobLocation = value, requireExact: true);
            SetAi("workMode", Limit(ai.WorkMode, 120), value => draft.WorkMode = value, requireExact: true);
            SetAi("project", Limit(ai.Project, 180), value => draft.Project = value, requireExact: true);
            SetAi("externalPositionCode", Limit(ai.ExternalPositionCode, 120), value => draft.ExternalPositionCode = value, requireExact: true);
            SetAi("sourceReference", Limit(ai.SourceReference, 240), value => draft.SourceReference = value, requireExact: true);
            SetAi("sourceAuthority", Limit(ai.SourceAuthority, 240), value => draft.SourceAuthority = value, requireExact: true);
            SetAi("experienceRange", Limit(ai.ExperienceRange, 120), value => draft.ExperienceRange = value, requireExact: true);
            SetAi("qualification", CompactList(string.Join("; ", ai.Qualifications.Count > 0 ? ai.Qualifications : [ai.Qualification]), 1800), value => draft.Qualification = value, requireExact: true);
            SetAi("hiringType", Limit(ai.HiringType, 120), value => draft.HiringType = value);
            SetAi("employmentType", Limit(ai.EmploymentType, 120), value => draft.EmploymentType = value);
            SetAi("positionCategory", Limit(ai.PositionCategory, 120), value => draft.PositionCategory = value);
            SetAi("hiringPriority", Limit(ai.HiringPriority, 40), value => draft.HiringPriority = value, requireExact: true);
            SetAi("requiredSkills", CompactList(string.Join("; ", ai.RequiredSkills), 1800), value => draft.RequiredSkills = value, preserveExact: false);
            SetAi("preferredSkills", CompactList(string.Join("; ", ai.PreferredSkills), 1800), value => draft.PreferredSkills = value, preserveExact: false);
            SetAi("certifications", CompactList(string.Join("; ", ai.Certifications), 480), value => draft.Certifications = value);
            SetAi("languages", CompactList(string.Join("; ", ai.Languages), 480), value => draft.Languages = value);
            SetAi("benefits", CompactList(string.Join("; ", ai.Benefits), 1800), value => draft.Benefits = value, requireExact: true);
            SetAi("businessJustification", Limit(string.IsNullOrWhiteSpace(ai.BusinessJustification) ? ai.RoleSummary : ai.BusinessJustification, 3800), value => draft.BusinessJustification = value, preserveExact: false);
            SetAi("reasonForHiring", Limit(string.IsNullOrWhiteSpace(ai.HiringNotes) ? ai.RolePurpose : ai.HiringNotes, 480), value => draft.ReasonForHiring = value, preserveExact: false);
            if (ai.NumberOfOpenings is > 0 and <= 999)
            {
                SetAiValue("numberOfOpenings", () => draft.NumberOfOpenings = ai.NumberOfOpenings.Value, requireExact: true);
            }
            if (ai.IsReplacement == true)
                SetAiValue("isReplacement", () => draft.IsReplacement = true, requireExact: true);
            if (ai.BudgetAvailable == true)
                SetAiValue("budgetAvailable", () => draft.BudgetAvailable = true, requireExact: true);
            if (ai.BudgetAmount is > 0)
                SetAiValue("budgetAmount", () => draft.BudgetAmount = ai.BudgetAmount.Value, requireExact: true);
            if (ai.SalaryMin is > 0)
                SetAiValue("salaryMin", () => draft.SalaryMin = ai.SalaryMin.Value, requireExact: true);
            if (ai.SalaryMax is > 0)
                SetAiValue("salaryMax", () => draft.SalaryMax = ai.SalaryMax.Value, requireExact: true);
            if (ai.SalaryMin is > 0 || ai.SalaryMax is > 0)
                SetAi("currency", Limit(ai.Currency, 12), value => draft.Currency = value, requireExact: true);
            if (DateTime.TryParse(ai.TargetJoiningDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var targetJoiningDate))
            {
                SetAiValue("targetJoiningDate", () => draft.TargetJoiningDate = targetJoiningDate.Date, requireExact: true);
            }
            if (ai.Status == "Completed" && ai.Responsibilities.Count > 0) responsibilities = CleanItems(ai.Responsibilities, 20, 1000);
            if (ai.Status == "Completed" && !string.IsNullOrWhiteSpace(ai.RoleSummary)) summary = Limit(ai.RoleSummary, 3800);
            if (ai.Status == "Completed" && !string.IsNullOrWhiteSpace(ai.RolePurpose)) rolePurpose = Limit(ai.RolePurpose, 3800);
            if (string.IsNullOrWhiteSpace(draft.Department))
                Set("department", InferDepartment(draft.PositionTitle), value => draft.Department = value, "inferred", .65m);
            warnings.Add(ai.Status == "Completed"
                ? $"AI-enhanced suggestions from {ai.Provider} {ai.Model} ({ai.Confidence:P0} confidence). Review before saving."
                : $"Only high-confidence exact fields from {ai.Provider} {ai.Model} were retained; the overall AI result was {ai.Confidence:P0} confidence.");
            if (retrieval.References.Count > 0 && ai.Status == "Completed")
                warnings.Add($"{retrieval.References.Count} similar approved client reference(s) helped normalize the suggestions; source-document facts remained authoritative.");
        }
        else if (ai.Status is not ("NotEnabled" or "NoText"))
        {
            var detail = string.IsNullOrWhiteSpace(ai.Error) ? ai.Status : $"{ai.Status}: {ai.Error}";
            warnings.Add($"AI enhancement was unavailable ({detail}); reliable local parser suggestions were retained.");
        }

        // Image-only client documents can still prepare a usable, editable JD when
        // the active provider has no PDF vision support. These are explicitly
        // generic defaults derived only from the role title, never invented source facts.
        var defaultsApplied = new List<string>();
        var defaultSkills = DefaultSkills(draft.PositionTitle);
        if (string.IsNullOrWhiteSpace(draft.ExperienceRange))
        {
            Set("experienceRange", InferExperienceRange(draft.PositionTitle), value => draft.ExperienceRange = value, "inferred", .58m);
            defaultsApplied.Add("experience range");
        }
        if (string.IsNullOrWhiteSpace(draft.JobLocation)
            && retrieval.Defaults.TryGetValue("JobLocation", out var learnedLocation)
            && IsPlausibleLocation(learnedLocation))
        {
            Set("jobLocation", learnedLocation, value => draft.JobLocation = value, "inferred", .55m);
            defaultsApplied.Add("work location");
        }
        var usableRequiredSkills = Items(draft.RequiredSkills).Where(IsUsableSkillName).Take(20).ToList();
        if (usableRequiredSkills.Count < 3)
        {
            usableRequiredSkills.AddRange(defaultSkills.Required.Where(value => !usableRequiredSkills.Contains(value, StringComparer.OrdinalIgnoreCase)));
            Set("requiredSkills", string.Join("; ", usableRequiredSkills.Take(20)), value => draft.RequiredSkills = value,
                usableRequiredSkills.Count == 0 ? "default" : "inferred", .6m);
            defaultsApplied.Add("required skills");
        }
        var usablePreferredSkills = Items(draft.PreferredSkills).Where(IsUsableSkillName).Take(20).ToList();
        if (usablePreferredSkills.Count < 3)
        {
            usablePreferredSkills.AddRange(defaultSkills.Preferred.Where(value => !usablePreferredSkills.Contains(value, StringComparer.OrdinalIgnoreCase)));
            Set("preferredSkills", string.Join("; ", usablePreferredSkills.Take(20)), value => draft.PreferredSkills = value,
                usablePreferredSkills.Count == 0 ? "default" : "inferred", .6m);
            defaultsApplied.Add("preferred skills");
        }
        if (string.IsNullOrWhiteSpace(draft.Qualification))
        {
            Set("qualification", "Bachelor's degree or equivalent in a relevant discipline", value => draft.Qualification = value, "default", .6m);
            defaultsApplied.Add("qualification");
        }
        if (string.IsNullOrWhiteSpace(draft.Certifications))
        {
            Set("certifications", DefaultCertification(draft.PositionTitle), value => draft.Certifications = value, "default", .55m);
            defaultsApplied.Add("certification");
        }
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = $"Support the organisation as {RolePhrase(draft.PositionTitle)} through reliable delivery, collaboration and continuous improvement.";
            Set("businessJustification", summary, value => draft.BusinessJustification = value, "default", .55m);
            defaultsApplied.Add("role summary");
        }
        if (responsibilities.Count == 0)
        {
            responsibilities = DefaultResponsibilities(draft.PositionTitle);
            rolePurpose = string.Join("\n", responsibilities);
            Set("reasonForHiring", Limit(rolePurpose, 480), value => draft.ReasonForHiring = value, "default", .55m);
            defaultsApplied.Add("responsibilities");
        }
        if (defaultsApplied.Count > 0)
            warnings.Add($"Editable general defaults were added for {string.Join(", ", defaultsApplied)} because those facts were not readable from the source. Replace them when client-confirmed details are available.");

        if (string.IsNullOrWhiteSpace(draft.Department) && !string.IsNullOrWhiteSpace(draft.PositionTitle))
            Set("department", InferDepartment(draft.PositionTitle), value => draft.Department = value, "inferred", .65m);
        NormalizeVocabulary("positionCategory", "Position Category", draft.PositionCategory, value => draft.PositionCategory = value,
            [draft.PositionTitle, draft.PositionCategory, draft.RequiredSkills]);
        NormalizeVocabulary("department", "Department", draft.Department, value => draft.Department = value,
            [draft.PositionTitle, draft.Department, draft.PositionCategory, draft.RequiredSkills]);
        NormalizeVocabulary("experienceRange", "Experience Range", draft.ExperienceRange, value => draft.ExperienceRange = value,
            [draft.ExperienceRange, text]);
        NormalizeVocabulary("hiringType", "Hiring Type", draft.HiringType, value => draft.HiringType = value,
            [draft.HiringType]);
        NormalizeVocabulary("employmentType", "Employment Type", draft.EmploymentType, value => draft.EmploymentType = value,
            [draft.EmploymentType]);
        NormalizeVocabulary("workMode", "Work Mode", draft.WorkMode, value => draft.WorkMode = value,
            [draft.WorkMode, draft.JobLocation]);
        NormalizeVocabulary("jobLocation", "Work Location", draft.JobLocation, value => draft.JobLocation = value,
            [draft.JobLocation, LocationEvidence(text)], allowSemantic: false);
        draft.JobLocation = NormalizeLocation(draft.JobLocation);
        if (retrieval.References.Count > 0 && ai.Status != "Completed")
            warnings.Add($"{retrieval.References.Count} similar approved client reference(s) were retrieved for review; no source-document facts were replaced.");

        var localSkillRequirements = LocalSkillRequirements(draft.RequiredSkills, draft.PreferredSkills);
        var skillRequirements = MergeAndNormalizeSkillRequirements(
            ai.Status == "Completed" ? ai.SkillRequirements : [],
            localSkillRequirements);
        foreach (var requirement in skillRequirements.Where(row => string.IsNullOrWhiteSpace(row.Proficiency)))
        {
            requirement.Proficiency = InferReviewerLevel(draft.PositionTitle);
            if (!requirement.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase))
            {
                requirement.SourceType = "inferred";
                requirement.Confidence = Math.Max(requirement.Confidence, .62m);
            }
        }
        var qualificationRequirements = ai.Status == "Completed" && ai.QualificationRequirements.Count > 0
            ? ai.QualificationRequirements
            : LocalQualificationRequirements(draft.Qualification, draft.PositionTitle);
        foreach (var requirement in qualificationRequirements.Where(row => string.IsNullOrWhiteSpace(row.Specialization)))
        {
            requirement.Specialization = InferSpecialization(draft.PositionTitle);
            if (!requirement.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase))
            {
                requirement.SourceType = "inferred";
                requirement.Confidence = Math.Max(requirement.Confidence, .62m);
            }
        }
        var certificationRequirements = ai.Status == "Completed" && ai.CertificationRequirements.Count > 0
            ? ai.CertificationRequirements
            : LocalCertificationRequirements(draft.Certifications);
        var languageRequirements = ai.Status == "Completed" && ai.LanguageRequirements.Count > 0
            ? ai.LanguageRequirements
            : LocalLanguageRequirements(draft.Languages);
        foreach (var requirement in languageRequirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Proficiency)) requirement.Proficiency = "Professional";
            if (requirement.LanguageName.Equals("English", StringComparison.OrdinalIgnoreCase)) requirement.IsMandatory = true;
        }

        var structured = new
        {
            schema = "recruitment-request-source/v2",
            parsedAtUtc = DateTime.UtcNow,
            originalFileName = draft.SourceDocumentName,
            parserName = parsed.ParserName,
            parserVersion = parsed.ParserVersion,
            ai = new { applied = ai.Status == "Completed", ai.Provider, ai.Model, ai.Confidence, ai.Status, ai.Error },
            retrieval = new
            {
                engine = retrieval.Engine,
                scopeClientId = retrieval.ScopeClientId,
                documentKind = retrieval.DocumentKind,
                referenceCount = retrieval.References.Count,
                vocabularyCount = retrieval.Vocabulary.Sum(row => row.Value.Count),
                references = retrieval.References.Select(reference => new
                {
                    reference.ReferenceId,
                    reference.SourceType,
                    reference.Title,
                    reference.Department,
                    reference.Status,
                    reference.Similarity,
                    reference.MatchedEvidence,
                    reference.UpdatedAtUtc
                }),
                vocabularyDecisions
            },
            clientSuggestion = ai.Status == "Completed" ? ai.ClientName : "",
            fieldMetadata,
            roleSummary = summary,
            rolePurpose,
            responsibilities,
            requiredSkills = Items(draft.RequiredSkills),
            preferredSkills = Items(draft.PreferredSkills),
            qualifications = Items(draft.Qualification),
            certifications = Items(draft.Certifications),
            languages = Items(draft.Languages),
            benefits = Items(draft.Benefits),
            skillRequirements,
            qualificationRequirements,
            certificationRequirements,
            languageRequirements
        };
        draft.SourceParsedJson = JsonSerializer.Serialize(structured, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        detected.Add("sourceParsedJson");

        var required = new Dictionary<string, string>
        {
            ["positionTitle"] = "Role / position", ["department"] = "Department", ["numberOfOpenings"] = "Openings",
            ["requestedByEmployeeId"] = "Requested by", ["clientId"] = "Client"
        };
        var review = required.Where(row => !detected.Contains(row.Key, StringComparer.OrdinalIgnoreCase)).Select(row => row.Value).ToList();
        if (string.IsNullOrWhiteSpace(text))
            warnings.Add(ai.Status == "Completed"
                ? "The scanned/image PDF was read by the configured AI. Verify its suggestions before saving."
                : "Readable text was not found. The original file can still be saved and reviewed manually.");
        if (!string.IsNullOrWhiteSpace(parsed.Error) && ai.Status != "Completed") warnings.Add(parsed.Error);
        if (review.Count > 0) warnings.Add("Complete the remaining business fields before saving; parser suggestions never auto-submit the request.");

        return new RecruitmentRequestDocumentParseResult
        {
            Status = ai.Status == "Completed" ? "Parsed" : string.IsNullOrWhiteSpace(text) ? "NeedsReview" : parsed.Status,
            ParserName = $"{parsed.ParserName}{(retrieval.HasContext ? " + LocalRAG" : "")}{(ai.Status == "Completed" ? $" + {ai.Provider}" : "")}",
            ParserVersion = parsed.ParserVersion,
            OriginalFileName = draft.SourceDocumentName,
            Draft = draft,
            DetectedFields = detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ReviewFields = review,
            Warnings = warnings.Distinct().ToList()
        };

        void Set(string field, string value, Action<string> setter, string sourceType = "exact", decimal confidence = .9m)
        {
            value = CleanValue(value);
            if (string.IsNullOrWhiteSpace(value)) return;
            setter(value);
            detected.Add(field);
            fieldMetadata[field] = new RecruitmentAiHiringFieldTrace { SourceType = sourceType, Confidence = confidence };
        }

        void SetAi(string field, string value, Action<string> setter, bool requireExact = false, bool preserveExact = true)
        {
            var trace = AiTrace(field);
            if (trace.SourceType == "manual") return;
            if (ai.Status == "LowConfidence" && (trace.SourceType != "exact" || trace.Confidence < .75m)) return;
            if (requireExact && trace.SourceType != "exact") return;
            if (preserveExact && detected.Contains(field, StringComparer.OrdinalIgnoreCase) && trace.SourceType != "exact") return;
            Set(field, value, setter, trace.SourceType, trace.Confidence);
        }

        void SetAiValue(string field, Action setter, bool requireExact = false)
        {
            var trace = AiTrace(field);
            if (trace.SourceType == "manual") return;
            if (ai.Status == "LowConfidence" && (trace.SourceType != "exact" || trace.Confidence < .75m)) return;
            if (requireExact && trace.SourceType != "exact") return;
            if (detected.Contains(field, StringComparer.OrdinalIgnoreCase) && trace.SourceType != "exact") return;
            setter();
            detected.Add(field);
            fieldMetadata[field] = trace;
        }

        RecruitmentAiHiringFieldTrace AiTrace(string field)
        {
            if (ai.FieldMetadata.TryGetValue(field, out var trace))
                return new RecruitmentAiHiringFieldTrace { SourceType = TraceSource(trace.SourceType), Confidence = Math.Clamp(trace.Confidence, 0m, 1m) };
            return InferredTrace(ai.Confidence);
        }

        void NormalizeVocabulary(string field, string vocabularyType, string currentValue, Action<string> setter,
            IEnumerable<string> evidence, bool allowSemantic = true)
        {
            var decision = documentRag.MatchVocabulary(retrieval, vocabularyType, currentValue, evidence);
            if (decision is null || decision.SelectedValue.Equals(currentValue, StringComparison.OrdinalIgnoreCase)) return;
            if (!allowSemantic && decision.MatchMode.Equals("Semantic", StringComparison.OrdinalIgnoreCase)) return;
            setter(decision.SelectedValue);
            vocabularyDecisions.Add(decision);
            detected.Add(field);
            fieldMetadata[field] = new RecruitmentAiHiringFieldTrace
            {
                SourceType = decision.MatchMode == "ExactOrAlias" ? "exact" : "inferred",
                Confidence = decision.Confidence
            };
        }
    }

    private static RecruitmentAiHiringFieldTrace DefaultTrace() => new() { SourceType = "default", Confidence = 1m };
    private static RecruitmentAiHiringFieldTrace ExactTrace(decimal confidence = .9m) => new() { SourceType = "exact", Confidence = confidence };
    private static RecruitmentAiHiringFieldTrace InferredTrace(decimal confidence = .7m) => new() { SourceType = "inferred", Confidence = confidence };
    private static string TraceSource(string value) => value.Equals("exact", StringComparison.OrdinalIgnoreCase) ? "exact"
        : value.Equals("inferred", StringComparison.OrdinalIgnoreCase) ? "inferred" : "manual";

    private static List<string> CleanItems(IEnumerable<string> values, int maximumItems, int maximumLength) =>
        values.Select(CleanValue).Where(value => value.Length > 0).Select(value => Limit(value, maximumLength))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximumItems).ToList();

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
        var normalized = Regex.Replace(text, @"(?i)\be\s*x\s*p\s*e\s*r\s*i\s*e\s*n\s*c\s*e\b", "experience");
        normalized = Regex.Replace(normalized, @"(?i)\b(?:cxperiencc|experiencc|experienee|experlence)\b", "experience");
        var range = Regex.Match(normalized, @"(?i)(?<min>\d{1,2})\s*(?:-|–|to)\s*(?<max>\d{1,2})\s*[*+]?\s*(?:years?|yrs?)");
        if (range.Success) return $"{range.Groups["min"].Value}-{range.Groups["max"].Value} years";
        var values = Regex.Matches(normalized, @"(?is)(?:minimum\s+(?:of\s+)?|at\s+least\s+)?(?<years>\d{1,2}(?:\.\d+)?)\s*[*+]?\s*(?:years?|yrs?)\b(?=[\s\S]{0,140}\bexperience\b)")
            .Cast<Match>()
            .Concat(Regex.Matches(normalized, @"(?is)\bexperience\b[\s\S]{0,140}?(?:minimum\s+(?:of\s+)?|at\s+least\s+)?(?<years>\d{1,2}(?:\.\d+)?)\s*[*+]?\s*(?:years?|yrs?)\b").Cast<Match>())
            .Select(match => decimal.TryParse(match.Groups["years"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var years) ? years : 0m)
            .Where(years => years is > 0 and <= 60).ToList();
        return values.Count > 0 ? $"{values.Max():0.#}+ years" : "";
    }

    private static string InferExperienceRange(string title)
    {
        if (Regex.IsMatch(title, @"(?i)chief|director|head|principal")) return "8+ years";
        if (Regex.IsMatch(title, @"(?i)architect|manager|supervisor|lead")) return "5+ years";
        if (Regex.IsMatch(title, @"(?i)senior|sr\.?")) return "4+ years";
        if (Regex.IsMatch(title, @"(?i)junior|trainee|intern|fresher")) return "0-2 years";
        return "3-5 years";
    }

    private static string JobLocation(string text)
    {
        var inline = Labeled(text, "place of posting", "work location", "job location", "location");
        if (IsPlausibleLocation(inline)) return NormalizeLocation(inline);

        var evidence = LocationEvidence(text);
        foreach (var pattern in new[]
        {
            @"(?i)\b(?<value>(?:UIDAI[ \t]+)?(?:TC|Tech(?:nology)?[ \t]*Cent(?:re|er))[ \t]*[-–.,:/]*[ \t]*[A-Z][A-Za-z]+(?:[ \t]+[A-Z][A-Za-z]+){0,3})",
            @"(?i)\b(?<value>(?:Head|Regional|Branch)[ \t]+Office[ \t]*[-–.,:/]*[ \t]*[A-Z][A-Za-z]+(?:[ \t]+[A-Z][A-Za-z]+){0,3})",
            @"(?i)\b(?<value>ARO[ \t]*[-–.,:/]*[ \t]*[A-Z][A-Za-z]+(?:[ \t]+[A-Z][A-Za-z]+){0,3})"
        })
        {
            var match = Regex.Match(evidence, pattern);
            if (match.Success && IsPlausibleLocation(match.Groups["value"].Value))
                return NormalizeLocation(match.Groups["value"].Value);
        }
        return "";
    }

    private static string LocationEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var heading = Regex.Match(text, @"(?im)^[ \t]*(?:place[ \t]+of[ \t]+posting|work[ \t]+location|job[ \t]+location|location)\b[^\r\n]*");
        if (!heading.Success) return text.Length <= 1_200 ? text : text[..1_200];
        var length = Math.Min(1_200, text.Length - heading.Index);
        return text.Substring(heading.Index, length);
    }

    private static bool IsPlausibleLocation(string value)
    {
        var clean = CleanValue(value);
        if (clean.Length is < 2 or > 140) return false;
        if (Regex.IsMatch(clean, @"(?i)qualification|education|experience|requirement|salary|\bctc\b|lakhs?|lpa|responsib|skills?|position|stakeholder|manage|coordinate|implement")) return false;
        if (clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 10) return false;
        var letters = clean.Count(char.IsLetter);
        return letters >= 2 && letters >= clean.Length * .62;
    }

    private static string NormalizeLocation(string value) => Regex.Replace(CleanValue(value), @"(?i)\b(?:Bangalore|Bengalore|Bangaluru|Bengalum|Bangalor)\b", "Bengaluru");

    private static string WorkMode(string text)
    {
        var labelled = Labeled(text, "work mode", "working mode", "mode of work", "work arrangement");
        if (Regex.IsMatch(labelled, @"(?i)\bremote\b")) return "Remote";
        if (Regex.IsMatch(labelled, @"(?i)\bhybrid\b")) return "Hybrid";
        if (Regex.IsMatch(labelled, @"(?i)\b(?:on[- ]?site|office)\b")) return "Office";
        if (Regex.IsMatch(text, @"(?i)\bremote\s+(?:role|position|work(?:ing)?|arrangement)\b|\bwork(?:ing)?\s+remotely\b")) return "Remote";
        if (Regex.IsMatch(text, @"(?i)\bhybrid\s+(?:work(?:ing)?|arrangement|workplace|role|position)\b")) return "Hybrid";
        if (Regex.IsMatch(text, @"(?i)\bon[- ]?site\s+(?:work(?:ing)?|role|position)\b")) return "Office";
        return "";
    }

    private static bool LooksLikeRecruitmentText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 160) return false;
        var signals = Regex.Matches(text, @"(?i)\b(position|role|experience|qualification|skills?|responsibilit(?:y|ies)|location|salary|ctc|job profile)\b").Count;
        return signals >= 2;
    }

    private static string Qualifications(string text)
    {
        var block = Block(text, "educational qualification", "educational qualifications", "qualification", "qualifications", "education");
        var candidates = Items(block).Where(LooksLikeQualification).Select(NormalizeQualification).Take(8).ToList();
        if (candidates.Count == 0)
            candidates = text.Split('\n').Select(CleanValue).Where(line => line.Length <= 300 && LooksLikeQualification(line))
                .Select(NormalizeQualification).Take(8).ToList();
        return string.Join("; ", candidates.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool LooksLikeQualification(string value) =>
        Regex.IsMatch(value, @"(?<![A-Za-z0-9])(?:BE|B\.\s*E\.?|BTech|B\.\s*Tech|MTech|M\.\s*Tech|MCA|MSc|M\.\s*Sc\.)(?![A-Za-z0-9])")
        || Regex.IsMatch(value, @"(?i)\b(?:bachelor(?:'s)?|master(?:'s)?|post[ -]?graduat(?:e|ion)|doctoral|doctorate|diploma|degree)\b");

    private static string NormalizeQualification(string value)
    {
        var clean = CleanValue(value);
        clean = Regex.Replace(clean, @"(?i)^(?:experience|educational[ \t]+qualifications?|qualifications?)[ \t,;:|–-]*", "");
        clean = CleanValue(clean);
        clean = Regex.Replace(clean, @"(?i)^(?:[ivx]+|[a-z])[.)][ \t]*", "");
        clean = Regex.Replace(clean, @"(?i)^\[?,\.\s*Tech\b", "B.Tech");
        return clean;
    }

    private static string RequiredSkills(string text)
    {
        var block = Block(text, "technical & analytics skills", "technical skills", "key skills & technical expertise", "key skills", "skills", "requirements");
        var evidence = string.IsNullOrWhiteSpace(block) ? text : block;
        var recognized = RecognizedSkills(evidence);
        if (recognized.Count >= 3) return string.Join("; ", recognized);
        var lines = Items(block).Where(IsUsableSkillName).Take(12);
        return string.Join("; ", recognized.Concat(lines).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
    }

    private static List<string> RecognizedSkills(string text)
    {
        var patterns = new (string Name, string Pattern)[]
        {
            ("PostgreSQL", @"\bPostgre(?:SQL|s SQL|s)\b"), ("MS SQL Server", @"\b(?:MS[- ]?SQL|SQL Server)\b"),
            ("MySQL", @"\bMySQL\b"), ("MongoDB", @"\bMongoDB\b"), ("Cassandra", @"\bCas+andra\b"),
            ("HBase", @"\bHBase\b"), ("Snowflake", @"\bSnowflake\b"), ("DynamoDB", @"\bDynamoDB\b"),
            ("DocumentDB", @"\bDocumentDB\b"), ("Vertica", @"\bVertica\b"), ("SQL", @"\bSQL\b"),
            ("Python", @"\bPython\b"), ("R", @"\bR\b(?=\s*(?:,|/|or\b))"), ("Java / J2EE", @"\b(?:Java|J2EE)\b"),
            (".NET", @"(?<!\w)\.NET\b"), ("Spring Boot", @"\bSpring\s*Boot\b"), ("Spring", @"\bSpring\b"),
            ("Hibernate", @"\bHibernate\b"), ("React", @"\bReact(?:\.js|JS)?\b"), ("Angular", @"\bAngular\b"),
            ("Node.js", @"\bNode(?:\.js|JS)\b"), ("REST APIs", @"\bREST(?:ful)?\s+APIs?\b|\bAPI integration\b"),
            ("AWS", @"\bAWS\b"), ("Azure", @"\bAzure\b"), ("Google Cloud", @"\b(?:GCP|Google Cloud)\b"),
            ("Docker", @"\bDocker\b"), ("Kubernetes", @"\bKubernetes\b|\bK8s\b"), ("CI/CD", @"\bCI\s*/\s*CD\b"),
            ("DevOps", @"\bDevOps\b"), ("Linux", @"\bLinux\b"), ("Cloud architecture", @"\bcloud\b.{0,35}\barchitect"),
            ("Machine learning", @"\bmachine\s+learning\b"), ("Deep learning", @"\bdeep\s+learning\b"),
            ("Computer vision", @"\bcomputer\s+vision\b"), ("Data analysis", @"\bdata\s+anal(?:ysis|ytics)\b"),
            ("Spark", @"\bSpark\b"), ("Hadoop", @"\bHadoop\b"), ("Dask", @"\bDask\b"),
            ("Power BI", @"\bPower\s*BI\b"), ("Tableau", @"\bTableau\b"), ("Looker", @"\bLooker\b"),
            ("Biometric systems", @"\bbiometric"), ("Fraud analytics", @"\bfraud\b.{0,30}\banalytics?\b"),
            ("SLA management", @"\bSLAs?\b"), ("Stakeholder management", @"\bstakeholders?\b"),
            ("System architecture", @"\bsystem\s+architect"), ("Performance tuning", @"\bperformance\s+tuning\b"),
            ("Database security", @"\bdatabase\b.{0,30}\bsecurity\b"), ("Backup and recovery", @"\bbackup\b|\brecovery\b"),
            ("Technical documentation", @"\btechnical\s+documentation\b|\bSOPs?\b")
        };
        return patterns.Where(row => Regex.IsMatch(text, row.Pattern, RegexOptions.IgnoreCase))
            .Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
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

    private static (string[] Required, string[] Preferred) DefaultSkills(string title)
    {
        if (Regex.IsMatch(title, @"(?i)bio[- ]?challenge|biometric"))
            return (["Biometric application development", "Secure API integration", "Application testing and debugging"], ["Biometric SDK integration", "Database fundamentals", "Stakeholder communication"]);
        if (Regex.IsMatch(title, @"(?i)data scientist|data analyst|fraud"))
            return (["Data analysis", "SQL", "Machine learning"], ["Python", "Data visualisation", "Stakeholder communication"]);
        if (Regex.IsMatch(title, @"(?i)database"))
            return (["Database design", "SQL", "Performance and reliability"], ["Data security", "Automation", "Technical documentation"]);
        if (Regex.IsMatch(title, @"(?i)platform|cloud|devops"))
            return (["Platform engineering", "Automation and scripting", "System reliability"], ["Cloud operations", "Security practices", "Technical documentation"]);
        if (Regex.IsMatch(title, @"(?i)IVIDD|delivery manager|service manager"))
            return (["Service delivery management", "SLA management", "Stakeholder management"], ["Biometric systems", "Process improvement", "Technical documentation"]);
        if (Regex.IsMatch(title, @"(?i)software|application|developer|architect"))
            return (["Application development", "Software design", "Testing and debugging"], ["API integration", "Database fundamentals", "Stakeholder communication"]);
        return (["Role-domain expertise", "Problem solving", "Quality delivery"], ["Technical documentation", "Stakeholder communication", "Continuous improvement"]);
    }

    private static List<string> DefaultResponsibilities(string title) =>
    [
        $"Deliver assigned {RolePhrase(title)} outcomes in line with approved requirements and timelines.",
        "Collaborate with business, technical and stakeholder teams to resolve issues and communicate progress.",
        "Maintain quality documentation, controls and continuous improvement for the assigned work."
    ];

    private static string RolePhrase(string title) => string.IsNullOrWhiteSpace(title) ? "the assigned role" : $"the {title.Trim()} role";

    private static string DefaultCertification(string title)
    {
        if (Regex.IsMatch(title, @"(?i)database")) return "Relevant database technology certification (preferred)";
        if (Regex.IsMatch(title, @"(?i)data|analytics|fraud")) return "Relevant data analytics or fraud-risk certification (preferred)";
        if (Regex.IsMatch(title, @"(?i)cloud|devops|platform")) return "Relevant cloud or DevOps certification (preferred)";
        if (Regex.IsMatch(title, @"(?i)software|application|developer|architect")) return "Relevant software development certification (preferred)";
        return "Relevant professional certification (preferred)";
    }

    private static string InferReviewerLevel(string title)
    {
        if (Regex.IsMatch(title, @"(?i)lead|manager|supervisor|senior|architect|head")) return "Advanced";
        if (Regex.IsMatch(title, @"(?i)junior|trainee|intern|associate")) return "Beginner";
        return "Intermediate";
    }

    private static string InferSpecialization(string title)
    {
        if (Regex.IsMatch(title, @"(?i)database|software|application|developer|architect|cloud|devops|platform")) return "Computer Science, Information Technology or a related discipline";
        if (Regex.IsMatch(title, @"(?i)data|analytics|fraud")) return "Data Science, Statistics, Computer Science or a related discipline";
        return "Relevant discipline for the role";
    }

    private static string InferDepartment(string positionTitle)
    {
        if (Regex.IsMatch(positionTitle, @"(?i)fraud|risk")) return "Identity Fraud & Risk Analytics";
        if (Regex.IsMatch(positionTitle, @"(?i)data scientist|analytics")) return "Data Science & Analytics";
        if (Regex.IsMatch(positionTitle, @"(?i)database")) return "Database Engineering";
        if (Regex.IsMatch(positionTitle, @"(?i)platform|cloud|devops")) return "Platform Engineering";
        if (Regex.IsMatch(positionTitle, @"(?i)software|application|developer|architect|engineer")) return "Software Engineering";
        return "Technology";
    }

    private static List<RecruitmentAiHiringSkillSuggestion> LocalSkillRequirements(string requiredSkills, string preferredSkills)
    {
        var rows = Items(requiredSkills).Select(value => (Value: value, Required: true))
            .Concat(Items(preferredSkills).Select(value => (Value: value, Required: false)))
            .Where(row => row.Value.Length <= 300)
            .GroupBy(row => row.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.Required).First())
            .Take(40)
            .ToList();
        if (rows.Count == 0) return [];

        var units = rows.Sum(row => row.Required ? 2m : 1m);
        var result = rows.Select(row => new RecruitmentAiHiringSkillSuggestion
        {
            SkillName = row.Value,
            IsRequired = row.Required,
            MinimumYears = ExplicitYears(row.Value),
            Proficiency = ExplicitProficiency(row.Value),
            WeightPercent = Math.Round((row.Required ? 2m : 1m) * 100m / units, 2),
            SourceType = "exact",
            Confidence = .82m
        }).ToList();
        result[^1].WeightPercent += 100m - result.Sum(row => row.WeightPercent);
        return result;
    }

    private static List<RecruitmentAiHiringSkillSuggestion> MergeAndNormalizeSkillRequirements(
        IEnumerable<RecruitmentAiHiringSkillSuggestion> primary,
        IEnumerable<RecruitmentAiHiringSkillSuggestion> fallback)
    {
        var fallbackRows = fallback.Where(row => IsUsableSkillName(row.SkillName)).ToList();
        var rows = primary.Where(row => IsUsableSkillName(row.SkillName))
            .GroupBy(row => CleanValue(row.SkillName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.IsRequired).First())
            .Take(40)
            .ToList();

        foreach (var required in new[] { true, false })
        {
            var minimum = 3;
            foreach (var supplement in fallbackRows.Where(row => row.IsRequired == required))
            {
                if (rows.Count(row => row.IsRequired == required) >= minimum) break;
                if (rows.Any(row => CleanValue(row.SkillName).Equals(CleanValue(supplement.SkillName), StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(supplement);
                if (rows.Count >= 40) break;
            }
        }

        AddGenericCoverage(true, ["Role-domain expertise", "Analytical problem solving", "Quality-focused delivery"]);
        AddGenericCoverage(false, ["Technical documentation", "Cross-functional collaboration", "Continuous improvement", "Stakeholder communication"]);

        if (rows.Count == 0) return [];
        var rawWeights = rows.Select(row => row.WeightPercent > 0 ? row.WeightPercent : row.IsRequired ? 2m : 1m).ToList();
        var total = rawWeights.Sum();
        for (var index = 0; index < rows.Count; index++)
            rows[index].WeightPercent = Math.Round(rawWeights[index] * 100m / total, 2, MidpointRounding.AwayFromZero);
        rows[^1].WeightPercent += 100m - rows.Sum(row => row.WeightPercent);
        return rows;

        void AddGenericCoverage(bool required, IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (rows.Count(row => row.IsRequired == required) >= 3 || rows.Count >= 40) return;
                if (rows.Any(row => CleanValue(row.SkillName).Equals(candidate, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new RecruitmentAiHiringSkillSuggestion
                {
                    SkillName = candidate,
                    IsRequired = required,
                    Proficiency = "",
                    WeightPercent = required ? 2m : 1m,
                    SourceType = "inferred",
                    Confidence = .55m
                });
            }
        }
    }

    private static bool IsUsableSkillName(string value)
    {
        var clean = CleanValue(value);
        if (clean.Length is < 2 or > 160) return false;
        if (clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 18) return false;
        if (Regex.IsMatch(clean, @"(?i)^(?:and|those|position|experience|educational|willingness|ability|research|skills?|requirements?|qualifications?|responsibilities|job profile)\b")) return false;
        if (Regex.IsMatch(clean, @"(?i)\b(?:IITs?|IISc|IIITs?|BITS|NITs?|institutions?|bachelor(?:'s)?|master(?:'s)?|B\.?[ \t]*Tech|M\.?[ \t]*Tech|MCA|MSc)\b")) return false;
        return clean.Count(char.IsLetterOrDigit) >= clean.Length * .55;
    }

    private static List<RecruitmentAiHiringQualificationSuggestion> LocalQualificationRequirements(string value, string positionTitle) =>
        Items(value).Where(item => item.Length <= 500).Take(12).Select(item => new RecruitmentAiHiringQualificationSuggestion
        {
            QualificationName = item,
            Specialization = InferSpecialization(positionTitle),
            IsMandatory = !Regex.IsMatch(item, @"(?i)preferred|desirable|advantage|good to have"),
            SourceType = "inferred",
            Confidence = .72m
        }).ToList();

    private static List<RecruitmentAiHiringCertificationSuggestion> LocalCertificationRequirements(string value) =>
        Items(value).Where(item => item.Length <= 300).Take(12).Select(item => new RecruitmentAiHiringCertificationSuggestion
        {
            CertificationName = item,
            IsMandatory = Regex.IsMatch(item, @"(?i)mandatory|required|must have"),
            ProofRequired = false,
            SourceType = "exact",
            Confidence = .82m
        }).ToList();

    private static List<RecruitmentAiHiringLanguageSuggestion> LocalLanguageRequirements(string value) =>
        Items(value.Replace(',', ';')).Where(item => item.Length <= 120).Take(12).Select(item => new RecruitmentAiHiringLanguageSuggestion
        {
            LanguageName = item,
            Proficiency = "Professional",
            IsMandatory = item.Equals("English", StringComparison.OrdinalIgnoreCase),
            SourceType = "inferred",
            Confidence = .65m
        }).ToList();

    private static decimal ExplicitYears(string value)
    {
        var match = Regex.Match(value, @"(?i)(?<years>\d{1,2}(?:\.\d+)?)\s*\+?\s*(?:years?|yrs?)");
        return match.Success && decimal.TryParse(match.Groups["years"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var years)
            && years is > 0 and <= 60 ? years : 0m;
    }

    private static string ExplicitProficiency(string value)
    {
        var match = Regex.Match(value, @"(?i)\b(expert|advanced|proficient|intermediate|familiar|basic|beginner)\b");
        return match.Success ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Value.ToLowerInvariant()) : "";
    }

    private static string Labeled(string text, params string[] labels)
    {
        foreach (var label in labels.OrderByDescending(value => value.Length))
        {
            var escaped = Regex.Escape(label).Replace("\\ ", @"[ \t]+");
            var match = Regex.Match(text, $@"(?im)^[ \t]*{escaped}[ \t]*(?:[:\-–][ \t]*|[ \t]+)(?<value>[^\r\n]{{1,300}})$");
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
