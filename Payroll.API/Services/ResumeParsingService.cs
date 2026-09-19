using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class ResumeParsingService(
    RecruitmentAiScoringService aiScoring,
    RecruitmentDocumentRagService documentRag,
    ILogger<ResumeParsingService> logger)
{
    private const int MaxInputBytes = 10 * 1024 * 1024;
    private const int MaxExtractedBytes = 20 * 1024 * 1024;
    private const int MaxExtractedCharacters = 2_000_000;
    private const int MaxBuiltInPdfBytes = 2 * 1024 * 1024;
    private const string ParserVersion = "2.5";
    private static readonly SemaphoreSlim OcrGate = new(2, 2);
    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Horizontal separators only: dates/IDs on the following line are not part of a phone.
    private static readonly Regex PhonePattern = new(@"(?<!\d)(?:\+?91[ \t().-]*)?[6-9](?:[ \t().-]*\d){9}(?![ \t().-]*\d)", RegexOptions.Compiled);
    private static readonly Regex InternationalPhonePattern = new(@"(?<![+\d])\+?\d(?:[ \t().-]*\d){9,14}(?![ \t().-]*\d)", RegexOptions.Compiled);
    private static readonly Regex PhoneLabelPattern = new(@"(?im)^[ \t]*(?:mobile|phone|telephone|tel|cell|contact)(?:[ \t]+(?:no\.?|number))?[ \t]*[:\-]?[ \t]*(?<value>[+()\d][+()\d \t.\-]{7,35})", RegexOptions.Compiled);
    private static readonly Regex NameLabelPattern = new(@"(?im)^\s*(?:candidate\s+)?(?:full\s+)?name\s*[:\-]\s*(?<value>\p{L}[\p{L}\p{M}.'-]+(?:\s+\p{L}[\p{L}\p{M}.'-]+){1,4})\s*$", RegexOptions.Compiled);
    private static readonly Regex AddressLabelPattern = new(@"(?im)^\s*(?:(?:current|permanent|residential|postal|mailing)\s+)?address\s*[:\-]\s*(?<value>[^\r\n]{8,300})(?:\r?\n(?<next>[^\r\n]{8,180}))?", RegexOptions.Compiled);
    private static readonly Regex LocationLabelPattern = new(@"(?im)^\s*(?:current\s+)?location\s*[:\-]\s*(?<value>[^\r\n]{2,120})", RegexOptions.Compiled);
    private static readonly Regex PersonNamePattern = new(@"^\p{L}[\p{L}\p{M}.'-]+(?:\s+\p{L}[\p{L}\p{M}.'-]+){1,4}$", RegexOptions.Compiled);
    private static readonly Regex RoleTitlePattern = new(@"\b(?:administrator|analyst|architect|consultant|coordinator|designer|developer|devops|director|engineer|executive|intern|lead|manager|officer|recruiter|specialist|supervisor|team)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmploymentDateRangePattern = new(@"(?ix)
        (?<startMonth>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|0?[1-9]|1[0-2])?
        [\s./-]*(?<startYear>(?:19|20)\d{2})\s*(?:-|–|—|to|till|through)\s*
        (?:(?<present>present|current|now|till\s+date)|(?<endMonth>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|0?[1-9]|1[0-2])?[\s./-]*(?<endYear>(?:19|20)\d{2}))", RegexOptions.Compiled);
    private static readonly Regex QualificationPattern = new(@"(?ix)\b(?:ph\.?\s*d|doctorate|m\.?\s*tech|m\.?\s*e\.?|m\.?\s*s\.?|m\.?\s*sc|mba|mca|m\.?\s*com|b\.?\s*tech|b\.?\s*e\.?|b\.?\s*sc|bca|b\.?\s*com|bba|bachelor(?:'s)?|master(?:'s)?|diploma|iti|12th|10th)\b", RegexOptions.Compiled);
    private static readonly Regex FlexibleEmploymentDateRangePattern = new(@"(?ix)
        (?<startMonth>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|0?[1-9]|1[0-2])?
        [\s./-]*['\u2019]?(?<startYear>(?:(?:19|20)?\d{2}))\s*(?:-|\u2013|\u2014|to|till|through)\s*
        (?:(?<present>present|current|now|till\s+date)|(?<endMonth>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|0?[1-9]|1[0-2])?[\s./-]*['\u2019]?(?<endYear>(?:(?:19|20)?\d{2})))", RegexOptions.Compiled);

    public async Task<ResumeParseResult> ParseAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();
        return await ParseAsync(source, file.FileName, file.Length, cancellationToken);
    }

    public async Task<ResumeParseResult> ParseAsync(
        IFormFile file,
        int clientId,
        IEnumerable<string>? jobContext,
        CancellationToken cancellationToken)
    {
        var local = await ParseAsync(file, cancellationToken);
        return await EnhanceAsync(file, local, clientId, jobContext, cancellationToken);
    }

    public async Task<ResumeParseResult> EnhanceAsync(
        IFormFile file,
        ResumeParseResult local,
        int clientId,
        IEnumerable<string>? jobContext,
        CancellationToken cancellationToken)
    {
        // AI is a fallback for incomplete extraction, not a second parser pass for
        // already reliable text resumes.
        if (HasCompleteLocalIdentity(local)) return local;

        byte[]? sourceDocument = null;
        if (file.Length <= MaxInputBytes
            && Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            && !local.Status.Equals("Parsed", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await using var source = file.OpenReadStream();
                using var memory = new MemoryStream();
                await CopyToLimitedAsync(source, memory, MaxInputBytes, cancellationToken);
                var bytes = memory.ToArray();
                if (HasPdfSignature(bytes)) sourceDocument = bytes;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Resume PDF could not be prepared for optional AI extraction ({FileName}).", Path.GetFileName(file.FileName));
            }
        }
        return await EnhanceWithAiAsync(local, file.FileName, clientId, jobContext, sourceDocument, cancellationToken);
    }

    private static bool HasCompleteLocalIdentity(ResumeParseResult parse) =>
        parse.Status.Equals("Parsed", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(parse.Facts.FullName)
        && (!string.IsNullOrWhiteSpace(parse.Facts.Email) || !string.IsNullOrWhiteSpace(parse.Facts.Phone))
        && !string.IsNullOrWhiteSpace(parse.Facts.CurrentTitle)
        && parse.Facts.Skills.Count > 0
        && parse.Facts.CharacterCount >= 200;

    public async Task<ResumeParseResult> ParseAsync(
        Stream source,
        string fileName,
        long fileLength,
        int clientId,
        IEnumerable<string>? jobContext,
        CancellationToken cancellationToken)
    {
        if (fileLength > MaxInputBytes)
            return await ParseAsync(source, fileName, fileLength, cancellationToken);
        try
        {
            using var memory = new MemoryStream();
            await CopyToLimitedAsync(source, memory, MaxInputBytes, cancellationToken);
            var bytes = memory.ToArray();
            await using var localSource = new MemoryStream(bytes, writable: false);
            var local = await ParseAsync(localSource, fileName, fileLength, cancellationToken);
            var sourceDocument = Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                && !local.Status.Equals("Parsed", StringComparison.OrdinalIgnoreCase)
                && HasPdfSignature(bytes)
                    ? bytes
                    : null;
            return await EnhanceWithAiAsync(local, fileName, clientId, jobContext, sourceDocument, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Resume could not be buffered for AI-assisted parsing ({FileName}).", Path.GetFileName(fileName));
            return ResumeParseResult.WithoutContent("Failed", "BuiltIn", ParserVersion, "Resume text could not be extracted safely. The original document remains available for authorized manual review.");
        }
    }

    public async Task<ResumeParseResult> ParseAsync(Stream source, string fileName, long fileLength, CancellationToken cancellationToken)
    {
        try
        {
            if (fileLength > MaxInputBytes)
                return ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", ParserVersion, "Resume parsing was skipped because the file exceeds the 10 MB parser limit.");
            using var memory = new MemoryStream();
            await CopyToLimitedAsync(source, memory, MaxInputBytes, cancellationToken);
            var bytes = memory.ToArray();
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var parserName = "BuiltIn";
            var parserVersion = ParserVersion;
            string text;
            if (extension == ".pdf")
            {
                var pdf = await ReadPdfAsync(bytes, cancellationToken);
                text = pdf.Text;
                parserName = pdf.ParserName;
            }
            else if (extension == ".doc")
            {
                var legacy = await TryReadLegacyDocAsync(bytes, cancellationToken);
                text = legacy.Text;
                parserName = legacy.Succeeded ? "Antiword" : "BuiltIn";
            }
            else
            {
                text = extension switch
                {
                    ".txt" or ".csv" => DecodeText(bytes),
                    ".rtf" => StripRtf(DecodeText(bytes)),
                    ".docx" => ReadDocx(bytes),
                    ".odt" => ReadOdt(bytes),
                    _ => ""
                };
            }
            text = NormalizeText(text);
            var status = LooksLikeUsefulResumeText(text) ? "Parsed" : "NeedsReview";
            // Contact identity is intentionally more tolerant than the full parser. Some
            // text PDFs expose the email/phone in the raw PDF payload even when their font
            // encoding prevents reliable page-text extraction. Global Talent Pool intake
            // can still store and preview those resumes for a recruiter.
            var email = EmailPattern.Match(text).Value;
            var phone = ExtractPhone(text);
            if (extension == ".pdf" && (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(phone)))
            {
                var binaryText = Encoding.Latin1.GetString(bytes);
                if (string.IsNullOrWhiteSpace(email)) email = EmailPattern.Match(binaryText).Value;
                if (string.IsNullOrWhiteSpace(phone)) phone = ExtractPhone(binaryText);
            }
            var fullName = status == "Parsed" ? ExtractFullName(text, fileName) : "";
            var residentialAddress = ExtractResidentialAddress(text);
            var sections = BuildSections(text);
            var summary = sections.FirstOrDefault(section => section.SectionCode == "SUMMARY")?.Content
                ?? sections.FirstOrDefault()?.Content
                ?? "";
            var structured = ExtractStructuredFacts(text, sections, residentialAddress);
            var facts = new ResumeParsedFacts(
                email,
                phone,
                fullName,
                residentialAddress,
                text.Length,
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                "und",
                summary.Length <= 1000 ? summary : summary[..1000],
                ExtractTotalExperienceMonths(text, structured.Experience))
            {
                CurrentCompany = structured.CurrentCompany,
                CurrentTitle = structured.CurrentTitle,
                CurrentLocation = structured.CurrentLocation,
                HighestQualification = structured.HighestQualification,
                Skills = structured.Skills,
                Certifications = structured.Certifications,
                Experience = structured.Experience,
                Education = structured.Education
            };
            return new ResumeParseResult(status, text, facts, sections, parserName, parserVersion, status == "Parsed" ? "" : "Text could not be extracted reliably. The resume remains available for manual review.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Built-in resume parsing failed for {FileName} ({FileLength} bytes).", Path.GetFileName(fileName), fileLength);
            return ResumeParseResult.WithoutContent("Failed", "BuiltIn", ParserVersion, "Resume text could not be extracted safely. The original document remains available for authorized manual review.");
        }
    }

    private async Task<ResumeParseResult> EnhanceWithAiAsync(
        ResumeParseResult local,
        string fileName,
        int clientId,
        IEnumerable<string>? jobContext,
        byte[]? sourceDocument,
        CancellationToken cancellationToken)
    {
        // Public/background stream intake must use the same fast path as admin uploads.
        if (HasCompleteLocalIdentity(local)) return local;
        try
        {
            RecruitmentDocumentRagContext retrieval;
            if (sourceDocument is { Length: > 0 })
            {
                // A vision-only response cannot be checked against a local text layer.
                // Keep retrieved JD language out of that request so job requirements can
                // never be copied into the candidate's evidence text.
                retrieval = RecruitmentDocumentRagContext.Empty(clientId, RecruitmentDocumentRagService.ResumeDocumentKind);
            }
            else
            {
                var queryParts = (jobContext ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Append(local.Text);
                retrieval = await documentRag.RetrieveAsync(clientId, RecruitmentDocumentRagService.ResumeDocumentKind,
                    queryParts, cancellationToken);
            }
            var ai = await aiScoring.SuggestResumeDocumentAsync(clientId, local.Text, sourceDocument,
                sourceDocument is { Length: > 0 } ? "application/pdf" : "", retrieval, cancellationToken);
            return MergeAiSuggestion(local, ai, retrieval.HasContext, sourceDocument is { Length: > 0 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AI-assisted resume parsing was unavailable for {FileName}; local parsing was retained.", Path.GetFileName(fileName));
            return local;
        }
    }

    internal static ResumeParseResult MergeAiSuggestion(
        ResumeParseResult local,
        RecruitmentAiResumeDocumentSuggestion ai,
        bool hasRetrievalContext,
        bool documentWasAttached)
    {
        if (ai.Status is not ("Completed" or "LowConfidence")) return local;

        var text = local.Text;
        var sourceHasReliableText = LooksLikeUsefulResumeText(text);
        var fullName = local.Facts.FullName;
        var email = local.Facts.Email;
        var phone = local.Facts.Phone;
        var address = local.Facts.ResidentialAddress;
        var languageCode = local.Facts.LanguageCode;
        var summary = local.Facts.SummaryText;
        var totalExperienceMonths = local.Facts.TotalExperienceMonths;
        var applied = false;

        if ((string.IsNullOrWhiteSpace(fullName) || !ContainsNormalizedEvidence(text, fullName))
            && IsPlausibleName(ai.FullName)
            && CanUseExactAiField(ai, "fullName", ai.FullName, text, documentWasAttached))
        {
            fullName = ai.FullName.Trim();
            applied = true;
        }
        var aiEmail = EmailPattern.Match(ai.Email ?? "").Value;
        if (string.IsNullOrWhiteSpace(email)
            && !string.IsNullOrWhiteSpace(aiEmail)
            && aiEmail.Equals((ai.Email ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            && CanUseExactAiField(ai, "email", aiEmail, text, documentWasAttached))
        {
            email = aiEmail;
            applied = true;
        }
        var aiPhone = PhonePattern.Match(ai.Phone ?? "").Value;
        if (string.IsNullOrWhiteSpace(phone)
            && !string.IsNullOrWhiteSpace(aiPhone)
            && CanUseExactAiField(ai, "phone", aiPhone, text, documentWasAttached, comparePhoneDigits: true))
        {
            phone = aiPhone;
            applied = true;
        }
        if (string.IsNullOrWhiteSpace(address)
            && (ai.ResidentialAddress ?? "").Trim().Length is >= 8 and <= 180
            && CanUseExactAiField(ai, "residentialAddress", ai.ResidentialAddress ?? "", text, documentWasAttached))
        {
            address = (ai.ResidentialAddress ?? "").Trim();
            applied = true;
        }

        if (ai.Status == "Completed" && !string.IsNullOrWhiteSpace(ai.SummaryText))
        {
            summary = ai.SummaryText.Trim();
            if (summary.Length > 1000) summary = summary[..1000];
            applied = true;
        }
        if (ai.Status == "Completed" && languageCode.Equals("und", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ai.LanguageCode) && !ai.LanguageCode.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            languageCode = ai.LanguageCode;
            applied = true;
        }
        if (!totalExperienceMonths.HasValue && ai.TotalExperienceMonths is >= 0 and <= 720
            && CanUseAiDerivedField(ai, "totalExperienceMonths"))
        {
            totalExperienceMonths = ai.TotalExperienceMonths;
            applied = true;
        }

        var aiSections = ai.Sections
            .Where(section => !string.IsNullOrWhiteSpace(section.Content))
            .Where(section => documentWasAttached
                ? section.Confidence >= .80m
                : ai.Status == "Completed" || EffectiveConfidence(section.Confidence, ai.Confidence) >= .75m)
            .Where(section => sourceHasReliableText
                ? ContainsNormalizedEvidence(text, section.Content)
                : documentWasAttached)
            .Select(section => new ResumeParsedSection(
                section.SectionCode,
                section.Heading,
                section.Content,
                section.DisplayOrder,
                EffectiveConfidence(section.Confidence, ai.Confidence)))
            .ToList();

        IReadOnlyList<ResumeParsedSection> sections = local.Sections;
        if (aiSections.Count > 0)
        {
            if (sections.Count == 0 || sections.All(section => section.SectionCode.Equals("GENERAL", StringComparison.OrdinalIgnoreCase)))
                sections = aiSections;
            else
            {
                var merged = sections.ToList();
                foreach (var section in aiSections)
                {
                    if (merged.Any(existing => existing.SectionCode.Equals(section.SectionCode, StringComparison.OrdinalIgnoreCase))) continue;
                    merged.Add(section);
                }
                sections = merged.OrderBy(section => section.DisplayOrder).ToList();
            }
            if (!sourceHasReliableText)
            {
                text = string.Join("\n\n", sections.Select(section => section.Content));
                text = NormalizeText(text);
            }
            applied = true;
        }

        if (!applied) return local;
        // AI-only text from an unreadable/scanned document is useful for recruiter
        // review, but cannot become ATS evidence until a human verifies it.
        var status = local.Status.Equals("Parsed", StringComparison.OrdinalIgnoreCase)
            ? "Parsed"
            : local.Status;
        if (string.IsNullOrWhiteSpace(summary))
            summary = sections.FirstOrDefault(section => section.SectionCode.Equals("SUMMARY", StringComparison.OrdinalIgnoreCase))?.Content
                ?? sections.FirstOrDefault()?.Content
                ?? "";
        if (summary.Length > 1000) summary = summary[..1000];
        var facts = local.Facts with
        {
            Email = email,
            Phone = phone,
            FullName = fullName,
            ResidentialAddress = address,
            CharacterCount = text.Length,
            LineCount = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            LanguageCode = languageCode,
            SummaryText = summary,
            TotalExperienceMonths = totalExperienceMonths,
            // Sections above have already passed source-evidence checks. Populate the
            // editable skill facts too, retaining locally extracted values first.
            Skills = local.Facts.Skills.Concat(ExtractSkills(sections))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList()
        };
        var parserParts = new List<string> { local.ParserName };
        if (hasRetrievalContext) parserParts.Add("LocalRAG");
        if (!string.IsNullOrWhiteSpace(ai.Provider)) parserParts.Add(ai.Provider);
        var parserName = string.Join(" + ", parserParts.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (parserName.Length > 100) parserName = parserName[..100];
        var parserVersion = string.IsNullOrWhiteSpace(local.ParserVersion) ? "ai1" : $"{local.ParserVersion}+ai1";
        if (parserVersion.Length > 50) parserVersion = parserVersion[..50];
        var error = status == "Parsed" ? "" : local.Error;
        return new ResumeParseResult(status, text, facts, sections, parserName, parserVersion, error);
    }

    private static bool CanUseExactAiField(
        RecruitmentAiResumeDocumentSuggestion ai,
        string field,
        string value,
        string sourceText,
        bool documentWasAttached,
        bool comparePhoneDigits = false)
    {
        if (!ai.FieldMetadata.TryGetValue(field, out var trace)
            || !trace.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase)
            || trace.Confidence < .65m) return false;
        if (ai.Status == "LowConfidence" && trace.Confidence < .75m) return false;
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            if (comparePhoneDigits)
            {
                var expected = new string((value ?? "").Where(char.IsDigit).ToArray());
                if (expected.Length >= 10 && PhonePattern.Matches(sourceText).Cast<Match>().Any(match =>
                    new string(match.Value.Where(char.IsDigit).ToArray()).EndsWith(expected[^10..], StringComparison.Ordinal)))
                    return true;
            }
            else if (ContainsNormalizedEvidence(sourceText, value)) return true;
        }
        // Model-only identity must not drive candidate de-duplication or profile
        // linking. A scanned resume can still be retained for manual review, while
        // identity is filled only when independently present in the local text layer.
        return false;
    }

    private static bool CanUseAiDerivedField(RecruitmentAiResumeDocumentSuggestion ai, string field)
    {
        if (ai.Status != "Completed") return false;
        if (!ai.FieldMetadata.TryGetValue(field, out var trace)) return false;
        return (trace.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase)
            || trace.SourceType.Equals("inferred", StringComparison.OrdinalIgnoreCase))
            && trace.Confidence >= .65m;
    }

    private static bool ContainsNormalizedEvidence(string source, string evidence)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(evidence)) return false;
        static string NormalizeEvidence(string value) => Regex.Replace(value, @"\s+", " ").Trim();
        return NormalizeEvidence(source).Contains(NormalizeEvidence(evidence), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlausibleName(string value)
    {
        var clean = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return clean.Length is >= 2 and <= 120
            && clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5
            && clean.Any(char.IsLetter)
            && clean.All(character => char.IsLetter(character) || char.IsWhiteSpace(character) || character is '.' or '\'' or '-');
    }

    private static decimal EffectiveConfidence(decimal fieldConfidence, decimal overallConfidence) =>
        Math.Clamp(fieldConfidence > 0 ? fieldConfidence : overallConfidence, 0m, 1m);

    private static IReadOnlyList<ResumeParsedSection> BuildSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<ResumeParsedSection>();
        var content = new List<string>();
        var currentCode = "GENERAL";
        var currentHeading = "";
        var order = 10;

        void Flush()
        {
            var value = string.Join('\n', content).Trim();
            content.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            result.Add(new ResumeParsedSection(currentCode, currentHeading, value, order, string.IsNullOrWhiteSpace(currentHeading) ? 0.55m : 0.92m));
            order += 10;
        }

        foreach (var rawLine in text.Split('\n').Take(10000))
        {
            var line = rawLine.Trim();
            var code = SectionCode(line);
            var inlineContent = "";
            if (code is null)
            {
                var inline = Regex.Match(line, @"^(?<heading>[^:]{2,45})\s*:\s*(?<content>.+)$");
                if (inline.Success)
                {
                    code = SectionCode(inline.Groups["heading"].Value);
                    inlineContent = inline.Groups["content"].Value.Trim();
                }
            }
            if (code is not null)
            {
                Flush();
                currentCode = code;
                currentHeading = inlineContent.Length == 0 ? line.Trim().TrimEnd(':') : line[..line.IndexOf(':')].Trim();
                if (inlineContent.Length > 0) content.Add(inlineContent);
                continue;
            }
            content.Add(rawLine);
        }
        Flush();
        if (result.Count == 0)
            result.Add(new ResumeParsedSection("GENERAL", "", text, 10, 0.45m));
        return result;
    }

    private static string? SectionCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80) return null;
        var heading = Regex.Replace(value.Trim().TrimEnd(':').ToUpperInvariant(), @"[^A-Z0-9&/ ]+", " ");
        heading = Regex.Replace(heading, @"\s+", " ").Trim();
        if (Regex.IsMatch(heading, @"^(PROFILE|PROFILE SUMMARY|PROFESSIONAL SUMMARY|CAREER SUMMARY|CAREER OVERVIEW|EXECUTIVE SUMMARY|EXECUTIVE PROFILE|EXPERIENCE SUMMARY|SUMMARY|ABOUT ME|OBJECTIVE|CAREER OBJECTIVE)$")) return "SUMMARY";
        if (Regex.IsMatch(heading, @"^(WORK|PROFESSIONAL|EMPLOYMENT|CAREER|RELEVANT)? ?(EXPERIENCE|HISTORY|PROFILE|TIMELINE|BACKGROUND)$")) return "EXPERIENCE";
        if (Regex.IsMatch(heading, @"^(EDUCATION|EDUCATIONAL (BACKGROUND|DETAILS|PROFILE|QUALIFICATIONS?)|ACADEMIC (BACKGROUND|DETAILS|PROFILE|QUALIFICATIONS?)|QUALIFICATIONS?)$")) return "EDUCATION";
        if (Regex.IsMatch(heading, @"^(TOOLS (AND|&) TECHNOLOGIES|TECHNICAL TOOLKIT|TECHNICAL KNOWLEDGE|AREAS OF EXPERTISE)$")) return "SKILLS";
        if (Regex.IsMatch(heading, @"^(TECHNICAL |CORE |KEY |PROFESSIONAL )?(SKILLS|SKILL SET|SKILLS MATRIX|COMPETENCIES|EXPERTISE|PROFICIENCIES)( (AND|&) (TOOLS|TECHNOLOGIES|FRAMEWORKS))?$|^(TOOLS|TECHNOLOGIES|TECHNOLOGY STACK|TECH STACK|TECHNOLOGIES (AND|&) FRAMEWORKS)$")) return "SKILLS";
        if (Regex.IsMatch(heading, @"^(PROFESSIONAL )?(CERTIFICATIONS?|CREDENTIALS?|LICENSES?|CERTIFICATIONS? (AND|&) (LICENSES?|TRAINING|EDUCATION)|TRAINING)$")) return "CERTIFICATIONS";
        if (Regex.IsMatch(heading, @"^(PROJECTS?|KEY PROJECTS?|PROJECT EXPERIENCE)$")) return "PROJECTS";
        if (Regex.IsMatch(heading, @"^(ACHIEVEMENTS?|AWARDS?|HONORS?|AWARDS? & HONORS?)$")) return "ACHIEVEMENTS";
        if (Regex.IsMatch(heading, @"^(PERSONAL DETAILS|CONTACT|CONTACT DETAILS)$")) return "CONTACT";
        if (Regex.IsMatch(heading, @"^(LANGUAGES?|LANGUAGES? KNOWN)$")) return "LANGUAGES";
        if (Regex.IsMatch(heading, @"^(DECLARATION|REFERENCES|PERSONAL STATEMENT)$")) return "GENERAL";
        if (Regex.IsMatch(heading, @"^(PUBLICATIONS?|RESEARCH|RESEARCH & PUBLICATIONS?)$")) return "PUBLICATIONS";
        return null;
    }

    private static ResumeStructuredFacts ExtractStructuredFacts(
        string text,
        IReadOnlyList<ResumeParsedSection> sections,
        string residentialAddress)
    {
        var experience = ExtractExperienceEntries(text, sections);
        var currentRole = experience.FirstOrDefault(row => row.IsCurrent) ?? experience.FirstOrDefault();
        var education = ExtractEducationEntries(sections, text);
        var location = LocationLabelPattern.Match(text).Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(location)) location = currentRole?.Location ?? "";
        if (string.IsNullOrWhiteSpace(location)) location = residentialAddress;
        return new ResumeStructuredFacts(
            currentRole?.Company ?? "",
            currentRole?.JobTitle ?? ExtractHeadlineTitle(text),
            CleanLocation(location),
            education.FirstOrDefault()?.Qualification ?? ExtractHighestQualification(text),
            ExtractSkills(sections),
            ExtractCertifications(sections),
            experience,
            education);
    }

    private static IReadOnlyList<ResumeParsedExperience> ExtractExperienceEntries(
        string text,
        IReadOnlyList<ResumeParsedSection> sections)
    {
        var experienceText = string.Join('\n', sections
            .Where(section => section.SectionCode.Equals("EXPERIENCE", StringComparison.OrdinalIgnoreCase))
            .Select(section => section.Content));
        if (string.IsNullOrWhiteSpace(experienceText)) experienceText = text;
        var lines = experienceText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(rawLine => Regex.Replace(rawLine, @"\s+", " ").Trim(' ', '•', '·'))
            .Where(line => line.Length is >= 4 and <= 350)
            .Take(2000)
            .ToList();
        var rows = new List<ResumeParsedExperience>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var date = FlexibleEmploymentDateRangePattern.Match(line);
            if (!date.Success) continue;
            var prefix = line[..date.Index].Trim(' ', '|', '-', '–', '—', ',', ';');
            string title = "", company = "", location = "";
            ParseRoleCompanyLocation(prefix, out title, out company, out location);

            if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(company) && index + 1 < lines.Count)
            {
                // Common PDF layout: "Role | date range" followed by
                // "Employer | location" on the next visual row.
                var next = lines[index + 1];
                if (!FlexibleEmploymentDateRangePattern.IsMatch(next) && !RoleTitlePattern.IsMatch(next) && !IsExperienceTableHeader(next))
                {
                    var metadata = Regex.Split(next, @"\s*(?:\||â€¢|Â·|\t)\s*")
                        .Select(part => CleanField(part, 180)).Where(part => part.Length > 0).ToList();
                    if (metadata.Count > 0 && metadata[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12)
                    {
                        company = metadata[0];
                        location = metadata.Skip(1).FirstOrDefault() ?? "";
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                // Common table layout: title, employer, location, date on four rows.
                for (var offset = 1; offset <= 4 && index - offset >= 0; offset++)
                {
                    var candidate = lines[index - offset];
                    if (IsExperienceTableHeader(candidate)) continue;
                    if (!RoleTitlePattern.IsMatch(candidate) || FlexibleEmploymentDateRangePattern.IsMatch(candidate)) continue;
                    title = CleanField(candidate, 180);
                    if (offset == 1 && prefix.Length > 0)
                    {
                        var metadata = Regex.Split(prefix, @"\s*(?:\||•|·|\t)\s*")
                            .Select(part => CleanField(part, 180)).Where(part => part.Length > 0).ToList();
                        company = metadata.FirstOrDefault() ?? "";
                        location = metadata.Skip(1).FirstOrDefault() ?? "";
                    }
                    else
                    {
                        company = offset > 1 ? CleanField(lines[index - offset + 1], 180) : "";
                        location = offset > 2 ? CleanField(lines[index - offset + 2], 180) : "";
                    }
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(title) && index + 1 < lines.Count)
            {
                // Timeline layout: date first, then "title — employer", then location.
                ParseRoleCompanyLocation(lines[index + 1], out title, out company, out location);
                if (location.Length == 0 && index + 2 < lines.Count && !FlexibleEmploymentDateRangePattern.IsMatch(lines[index + 2]))
                    location = CleanField(lines[index + 2], 180);
            }
            if (!RoleTitlePattern.IsMatch(title)) continue;
            var start = DatePart(date.Groups["startMonth"].Value, date.Groups["startYear"].Value, false);
            var isCurrent = date.Groups["present"].Success;
            var end = isCurrent ? null : DatePart(date.Groups["endMonth"].Value, date.Groups["endYear"].Value, true);
            rows.Add(new ResumeParsedExperience(company, title, location, start, end, isCurrent, line));
        }
        return rows
            .DistinctBy(row => $"{row.Company}|{row.JobTitle}|{row.StartDate}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(row => row.IsCurrent)
            .ThenByDescending(row => row.StartDate)
            .Take(30)
            .ToList();
    }

    private static void ParseRoleCompanyLocation(string value, out string title, out string company, out string location)
    {
        title = company = location = "";
        var clean = CleanField(value, 350);
        if (clean.Length == 0) return;
        var parts = Regex.Split(clean, @"\s*(?:\||•|·|\t)\s*")
            .Select(part => CleanField(part, 180))
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 1)
        {
            var at = Regex.Match(clean, @"^(?<title>.+?)\s+(?:at|@)\s+(?<company>.+)$", RegexOptions.IgnoreCase);
            var asciiDash = Regex.Match(clean, @"^(?<title>.+?)\s+-\s+(?<company>.+)$");
            var dash = Regex.Match(clean, @"^(?<title>.+?)\s+[–—]\s+(?<company>.+)$");
            var match = at.Success ? at : asciiDash.Success ? asciiDash : dash;
            if (match.Success)
            {
                title = CleanField(match.Groups["title"].Value, 180);
                company = CleanField(match.Groups["company"].Value, 180);
            }
            else if (RoleTitlePattern.IsMatch(clean)) title = clean;
            return;
        }
        var roleIndex = parts.FindIndex(part => RoleTitlePattern.IsMatch(part));
        if (roleIndex < 0) return;
        title = parts[roleIndex];
        company = parts.Where((_, partIndex) => partIndex != roleIndex).FirstOrDefault() ?? "";
        location = parts.Where((_, partIndex) => partIndex != roleIndex).Skip(1).FirstOrDefault() ?? "";
    }

    private static bool IsExperienceTableHeader(string value) => Regex.IsMatch(value,
        @"^(role|designation|organization|organisation|company|employer|location|dates?|duration|period|responsibilities?)$",
        RegexOptions.IgnoreCase);

    private static IReadOnlyList<ResumeParsedEducation> ExtractEducationEntries(
        IReadOnlyList<ResumeParsedSection> sections,
        string text)
    {
        var content = string.Join('\n', sections
            .Where(section => section.SectionCode.Equals("EDUCATION", StringComparison.OrdinalIgnoreCase))
            .Select(section => section.Content));
        if (string.IsNullOrWhiteSpace(content)) content = text;
        var result = new List<ResumeParsedEducation>();
        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(300))
        {
            var line = Regex.Replace(rawLine, @"\s+", " ").Trim(' ', '•', '·', '-', '–', '—');
            var match = QualificationPattern.Match(line);
            if (!match.Success || line.Length > 350 || Regex.IsMatch(line, @"(?i)\b(?:declare|declaration|authentic|best of my knowledge|hereby)\b")) continue;
            var parts = line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var qualification = CleanField(line, 250);
            var following = parts.SkipWhile(part => !QualificationPattern.IsMatch(part)).Skip(1).ToList();
            // Pipe-separated degree | specialization | institution | marks | years.
            // A bare subject is not the awarding institution; do not invent one.
            var institution = following.FirstOrDefault(part => Regex.IsMatch(part, @"(?i)\b(?:university|college|institute|school|polytechnic)\b")
                || Regex.IsMatch(part.Trim(), @"^[A-Z][A-Z.& ]{1,14}$") && !Regex.IsMatch(part, @"\d|%")
                && !Regex.IsMatch(part.Trim(), @"^(?:CS|CSE|IT|ECE|EEE|EE|ME|CE|AI|ML|AI\s*&\s*ML)$")) ?? "";
            var yearMatch = Regex.Matches(line, @"\b(?:19|20)\d{2}\b").Cast<Match>().LastOrDefault();
            result.Add(new ResumeParsedEducation(
                qualification,
                CleanField(institution, 250),
                yearMatch is not null && int.TryParse(yearMatch.Value, out var year) ? year : null,
                line));
        }
        return result
            .DistinctBy(row => $"{row.Qualification}|{row.Institution}|{row.CompletionYear}", StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractSkills(IReadOnlyList<ResumeParsedSection> sections)
    {
        var result = new List<string>();
        foreach (var section in sections.Where(section => section.SectionCode.Equals("SKILLS", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var rawLine in section.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsUnverifiedSkillClaim(rawLine)) continue;
                var line = Regex.Replace(rawLine, @"\s+", " ").Trim(' ', '•', '·', '-', '–', '—');
                var colon = line.IndexOf(':');
                if (colon >= 0 && colon < line.Length - 1)
                {
                    // Preserve explicit skill-bearing category labels, not just their tools.
                    if (Regex.IsMatch(line[..colon], @"(?i)\bCI\s*/\s*CD\b")) result.Add("CI/CD");
                    line = line[(colon + 1)..];
                }
                foreach (var token in Regex.Split(line, @"\s*(?:,|;|\||•|·|\band\b)\s*", RegexOptions.IgnoreCase))
                {
                    var skill = CleanField(token, 100);
                    var tokenColon = skill.IndexOf(':');
                    if (tokenColon >= 0 && tokenColon < skill.Length - 1) skill = CleanField(skill[(tokenColon + 1)..], 100);
                    if (IsPlausibleSkill(skill)) result.Add(skill);
                }
            }
        }
        foreach (var section in sections.Where(section => section.SectionCode.Equals("SKILLS", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var rawLine in section.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsUnverifiedSkillClaim(rawLine)) continue;
                var phrase = Regex.Replace(rawLine,
                    @"(?i)^.*?\b(?:strong\s+)?(?:experience|expertise|proficiency|proficient|skilled|knowledge)\s*(?:in|with|of|:)\s*",
                    "");
                if (phrase.Equals(rawLine, StringComparison.Ordinal)) continue;
                foreach (var token in Regex.Split(phrase, @"\s*(?:,|;|\||\band\b)\s*", RegexOptions.IgnoreCase))
                {
                    var skill = CleanField(token, 100).TrimEnd('.');
                    if (IsPlausibleSkill(skill)) result.Add(skill);
                }
            }
        }
        // Explicit candidate claims can appear in a summary without a Skills heading.
        // Do not infer a skill list from a role title, JD, or arbitrary prose.
        foreach (var section in sections.Where(section => section.SectionCode is "SUMMARY" or "GENERAL" or "EXPERIENCE"))
        {
            foreach (var rawLine in section.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsUnverifiedSkillClaim(rawLine)) continue;
                var claim = Regex.Match(rawLine,
                    @"(?:^|[.!?]\s+)(?:strong\s+|hands-on\s+|technical\s+)?(?:expertise|proficiency|proficient|skilled|experience)\s+(?:in|with)\s+(?<skills>[^\r\n]{1,250})$",
                    RegexOptions.IgnoreCase);
                if (!claim.Success) continue;
                var list = claim.Groups["skills"].Value.TrimEnd('.');
                if (Regex.IsMatch(list, @"[.!?](?:\s|$)")) continue;
                foreach (var token in Regex.Split(list, @"\s*(?:,|;|\||\band\b)\s*", RegexOptions.IgnoreCase))
                {
                    var skill = CleanField(token, 100).TrimEnd('.');
                    if (skill.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 && IsPlausibleSkill(skill)) result.Add(skill);
                }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
    }

    private static bool IsUnverifiedSkillClaim(string line) => Regex.IsMatch(line,
        @"\b(?:no|not|without|lack|lacking|seeking)\b|^\s*(?:learning(?!\s+and\b)|requirements?\s*:|required\s+skills\b)", RegexOptions.IgnoreCase);

    private static IReadOnlyList<string> ExtractCertifications(IReadOnlyList<ResumeParsedSection> sections) =>
        sections.Where(section => section.SectionCode.Equals("CERTIFICATIONS", StringComparison.OrdinalIgnoreCase))
            .SelectMany(section => Regex.Split(section.Content, @"\s*(?:\r?\n|•|·)\s*"))
            .SelectMany(value => Regex.Split(value, @"\s*(?:;|\|)\s*"))
            .Select(CleanCertificationLine)
            .Where(value => value.Length is >= 3 and <= 250)
            .Where(value => !string.Equals(SectionCode(value), "CERTIFICATIONS", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

    private static string CleanCertificationLine(string value)
    {
        var clean = CleanField(value.Trim(' ', '•', '·', '-', '–', '—'), 250);
        if (Regex.IsMatch(clean, @"^education\s*:", RegexOptions.IgnoreCase)) return "";
        return Regex.Replace(clean, @"^(?:certification|certificate)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
    }

    private static string ExtractHeadlineTitle(string text)
    {
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(15))
        {
            var line = EmailPattern.Replace(PhonePattern.Replace(rawLine, " "), " ");
            line = CleanField(line.Trim(' ', '|', '-', '–', '—', ',', ';'), 180);
            if (Regex.IsMatch(line, @"https?://|www\.|linkedin|github|@", RegexOptions.IgnoreCase)) continue;
            if (SectionCode(line) is not null) continue;
            if (line.Length is >= 3 and <= 100
                && RoleTitlePattern.IsMatch(line)
                && !FlexibleEmploymentDateRangePattern.IsMatch(line)
                && !Regex.IsMatch(line, @"[.!?]$|\b(?:years?\s+of\s+experience|experienced\s+in|responsible\s+for)\b", RegexOptions.IgnoreCase)
                && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12)
                return line;
        }
        return "";
    }

    private static string ExtractHighestQualification(string text)
    {
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = Regex.Replace(rawLine, @"\s+", " ").Trim();
            if (QualificationPattern.IsMatch(line)) return CleanField(line, 250);
        }
        return "";
    }

    private static bool IsPlausibleSkill(string value)
    {
        if (value.Length is < 1 or > 100 || !value.Any(char.IsLetter)) return false;
        if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return false;
        if (Regex.IsMatch(value, @"^(area|technologies|technology|platforms?|containers?|cloud|iac|security|monitoring|tools?|operating systems?|os|languages?|methodologies)$", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(value, @"\b(?:experience|responsibilities|declaration|education|certifications?|summary)\b", RegexOptions.IgnoreCase)) return false;
        return !value.EndsWith(".", StringComparison.Ordinal);
    }

    private static string CleanField(string? value, int maximumLength)
    {
        var clean = Regex.Replace(value ?? "", @"\s+", " ").Trim(' ', '|', ',', ';', ':', '-', '–', '—');
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string CleanLocation(string? value)
    {
        var clean = CleanField(value, 180);
        clean = Regex.Replace(clean, @"(?i)\s*client\s*:.*$", "").Trim(' ', ',', ';', '-', '–', '—');
        return clean;
    }

    private static DateOnly? DatePart(string monthValue, string yearValue, bool endOfMonth)
    {
        yearValue = yearValue.Trim().TrimStart('\'', '\u2019');
        if (!int.TryParse(yearValue, out var year)) return null;
        if (yearValue.Length <= 2) year += year <= 49 ? 2000 : 1900;
        if (year is < 1950 or > 2100) return null;
        var month = MonthNumber(monthValue);
        if (month <= 0) month = endOfMonth ? 12 : 1;
        var day = endOfMonth ? DateTime.DaysInMonth(year, month) : 1;
        return new DateOnly(year, month, day);
    }

    private static int MonthNumber(string value)
    {
        if (int.TryParse(value, out var numeric) && numeric is >= 1 and <= 12) return numeric;
        if (value.Length < 3) return 0;
        return value[..3].ToLowerInvariant() switch
        {
            "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
            "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
            _ => 0
        };
    }

    private static int? ExtractTotalExperienceMonths(string text, IReadOnlyList<ResumeParsedExperience> experience)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = Regex.Matches(text, @"(?<!\d)(?<years>\d{1,2}(?:\.\d{1,2})?)\s*\+?\s*(?:years?|yrs?)(?:\s+of)?\s+(?:relevant\s+|professional\s+|total\s+)?experience", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => decimal.TryParse(match.Groups["years"].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var years) ? years : -1)
            .Where(years => years is >= 0 and <= 60)
            .ToList();
        if (values.Count > 0) return (int)Math.Round(values.Max() * 12m, MidpointRounding.AwayFromZero);
        var ranges = experience
            .Where(row => row.StartDate.HasValue)
            .Select(row => (Start: row.StartDate!.Value, End: row.EndDate ?? DateOnly.FromDateTime(DateTime.UtcNow)))
            .Where(range => range.End >= range.Start)
            .OrderBy(range => range.Start)
            .ToList();
        if (ranges.Count == 0) return null;
        var merged = new List<(DateOnly Start, DateOnly End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End.AddMonths(1)) merged.Add(range);
            else if (range.End > merged[^1].End) merged[^1] = (merged[^1].Start, range.End);
        }
        return Math.Min(720, merged.Sum(range => Math.Max(0, (range.End.Year - range.Start.Year) * 12 + range.End.Month - range.Start.Month + 1)));
    }

    private static string ExtractFullName(string text, string fileName)
    {
        var labelled = NameLabelPattern.Match(text).Groups["value"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(labelled)) return labelled;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "resume", "curriculum vitae", "cv", "profile", "professional summary", "summary",
            "contact", "contact details", "personal details", "career objective", "objective"
        };
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(18))
        {
            var withoutIdentity = EmailPattern.Replace(PhonePattern.Replace(rawLine, " "), " ");
            foreach (var segment in Regex.Split(withoutIdentity, @"[|•\t]").Select(value => Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', '-', ',', ';', ':')))
            {
                if (segment.Length is < 4 or > 80 || ignored.Contains(segment)) continue;
                if (Regex.IsMatch(segment, @"https?://|www\.|linkedin|github|address|email|phone|mobile", RegexOptions.IgnoreCase)) continue;
                if (SectionCode(segment) is not null) continue;
                if (PersonNamePattern.IsMatch(segment) && !RoleTitlePattern.IsMatch(segment)) return segment;
            }
        }
        var fallback = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"(?i)\b(resume|cv|profile|updated|latest|final)\b", " ");
        fallback = Regex.Replace(fallback, @"[_\-\d]+", " ");
        fallback = Regex.Replace(fallback, @"\s+", " ").Trim();
        return Regex.IsMatch(fallback, @"^\p{L}[\p{L}\p{M}.'-]+(?:\s+\p{L}[\p{L}\p{M}.'-]+){0,4}$") ? fallback : "";
    }

    private static string ExtractPhone(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (Match labelled in PhoneLabelPattern.Matches(text))
        {
            var value = labelled.Groups["value"].Value;
            var international = InternationalPhonePattern.Match(value);
            if (international.Success && international.Value.StartsWith('+') && IsPlausiblePhoneDigits(international.Value))
                return international.Value.Trim();
            var indian = PhonePattern.Match(value);
            if (indian.Success) return indian.Value.Trim();
            if (international.Success && IsPlausiblePhoneDigits(international.Value))
                return international.Value.Trim();
        }
        var withCountryCode = PlausibleInternationalPhone(text);
        if (withCountryCode.Length > 0) return withCountryCode;
        foreach (Match match in PhonePattern.Matches(text))
        {
            if (!HasIdentifierContext(text, match)) return match.Value.Trim();
        }
        return PlausibleInternationalPhone(text);
    }

    private static bool HasIdentifierContext(string text, Match match)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
        var prefixStart = Math.Max(lineStart + 1, match.Index - 80);
        var prefix = text[prefixStart..match.Index];
        return Regex.IsMatch(prefix,
            @"(?i)\b(?:employee|emp|aadhaar|aadhar|account|acct|passport|pan|tax|registration|roll|pin|zip|identifier|id)\s*(?:no|number|code)?\s*[:#-]?\s*$");
    }

    private static bool IsPlausiblePhoneDigits(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 15 && digits.Distinct().Count() >= 4;
    }

    private static string PlausibleInternationalPhone(string text)
    {
        foreach (Match match in InternationalPhonePattern.Matches(text))
        {
            if (!match.Value.TrimStart().StartsWith('+') || !IsPlausiblePhoneDigits(match.Value)) continue;
            if (HasIdentifierContext(text, match)) continue;
            if (Regex.IsMatch(match.Value, @"(?:19|20)\d{2}\s*[-–—/]\s*(?:19|20)\d{2}")) continue;
            return match.Value.Trim();
        }
        return "";
    }

    private static string ExtractResidentialAddress(string text)
    {
        var match = AddressLabelPattern.Match(text);
        if (!match.Success) return "";
        var value = $"{match.Groups["value"].Value} {match.Groups["next"].Value}";
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', ',', ';', '-');
        if (EmailPattern.IsMatch(value)) value = value[..value.IndexOf(EmailPattern.Match(value).Value, StringComparison.Ordinal)].Trim(' ', ',', ';', '-');
        if (PhonePattern.IsMatch(value)) value = value[..value.IndexOf(PhonePattern.Match(value).Value, StringComparison.Ordinal)].Trim(' ', ',', ';', '-');
        return value.Length <= 180 ? value : value[..180];
    }

    private static string ReadDocx(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        var parts = archive.Entries
            .Where(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(entry.FullName, @"^word/(?:header|footer)\d*\.xml$", RegexOptions.IgnoreCase)
                || entry.FullName.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        if (parts.Count == 0) return "";
        if (parts.Sum(entry => entry.Length) > MaxExtractedBytes) throw new InvalidDataException("The DOCX document content exceeds the parser extraction limit.");
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = new List<string>();
        var extractedCharacters = 0;
        foreach (var part in parts)
        {
            using var stream = part.Open();
            var xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            foreach (var paragraph in xml.Descendants(word + "p"))
            {
                var value = new StringBuilder();
                foreach (var node in paragraph.Descendants())
                {
                    if (node.Name == word + "t") value.Append(node.Value);
                    else if (node.Name == word + "tab") value.Append('\t');
                    else if (node.Name == word + "br" || node.Name == word + "cr") value.Append('\n');
                }
                var line = value.ToString().Trim();
                if (line.Length > 0)
                {
                    paragraphs.Add(line);
                    extractedCharacters += line.Length;
                }
                if (extractedCharacters >= MaxExtractedCharacters) break;
            }
            if (extractedCharacters >= MaxExtractedCharacters) break;
        }
        return string.Join("\n", paragraphs);
    }

    private static string ReadOdt(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        var content = archive.GetEntry("content.xml");
        if (content is null) return "";
        if (content.Length > MaxExtractedBytes) throw new InvalidDataException("The ODT document content exceeds the parser extraction limit.");
        using var stream = content.Open();
        var xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace textNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var lines = xml.Descendants()
            .Where(node => node.Name == textNamespace + "p" || node.Name == textNamespace + "h")
            .Select(node => Regex.Replace(node.Value, @"\s+", " ").Trim())
            .Where(value => value.Length > 0)
            .ToList();
        var value = string.Join('\n', lines);
        return value.Length <= MaxExtractedCharacters ? value : value[..MaxExtractedCharacters];
    }

    private static async Task<(bool Succeeded, string Text)> TryReadLegacyDocAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var executable = Environment.GetEnvironmentVariable("ANTIWORD_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(executable)) executable = "antiword";
        var directory = Path.Combine(Path.GetTempPath(), "frevo-resume-doc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "resume.doc");
        await File.WriteAllBytesAsync(input, bytes, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await RunProcessAsync(executable, [input], timeout.Token);
            return result.Succeeded && LooksLikeUsefulResumeText(NormalizeText(result.Output))
                ? (true, result.Output)
                : (false, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "");
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "");
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { /* Isolated parser files are safe for normal OS temp cleanup. */ }
        }
    }

    private static async Task<(string Text, string ParserName)> ReadPdfAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        // pdftotext understands embedded ToUnicode/CMap font encodings that the
        // lightweight parser cannot decode. Prefer it before inspecting PDF binary
        // operators: image-heavy client documents can otherwise spend tens of
        // seconds in regex/decompression work and exceed the HTTP request timeout.
        var decoded = await TryReadPdfWithPdftotextAsync(bytes, cancellationToken);
        if (decoded.Succeeded && LooksLikeUsefulResumeText(NormalizeText(decoded.Text))) return (decoded.Text, "PdfToText");

        if (bytes.Length <= MaxBuiltInPdfBytes)
        {
            var builtIn = ReadPdfOperators(bytes);
            if (LooksLikeUsefulResumeText(NormalizeText(builtIn))) return (builtIn, "BuiltIn");
        }

        var ocr = await TryReadPdfWithOcrAsync(bytes, cancellationToken);
        if (ocr.Succeeded) return (ocr.Text, "LocalOCR");

        // Keep the dependency-free fallback bounded. A large PDF without an
        // external decoder remains available for manual review instead of tying up
        // a request thread for an unbounded amount of time.
        if (bytes.Length > MaxBuiltInPdfBytes) return ("", "BuiltIn");
        return (ReadPdfOperators(bytes), "BuiltIn");
    }

    private static string ReadPdfOperators(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var pieces = new List<string>();
        // Never scan raw image/compressed bytes for coincidental '(...) Tj'.
        // A scanned PDF previously passed as "Parsed" because binary noise
        // happened to resemble text operators, preventing the OCR fallback.
        foreach (Match match in Regex.Matches(raw, @"stream\r?\n(?<data>[\s\S]*?)\r?\nendstream", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            if (pieces.Sum(piece => piece.Length) >= MaxExtractedCharacters) break;
            try
            {
                var headerStart = raw.LastIndexOf("endobj", match.Index, StringComparison.Ordinal);
                headerStart = Math.Max(headerStart < 0 ? 0 : headerStart + 6, match.Index - 4096);
                var header = raw[headerStart..match.Index];
                if (Regex.IsMatch(header, @"/Subtype\s*/Image\b|/Type\s*/(?:XObject|ObjStm|XRef)\b|/Length[123]\b")) continue;
                var value = match.Groups["data"].Value;
                if (!header.Contains("/Filter", StringComparison.Ordinal))
                {
                    ExtractPdfTextObjects(value, pieces);
                    continue;
                }
                if (!Regex.IsMatch(header, @"/Filter\s*(?:/FlateDecode\b|\[\s*/FlateDecode\s*\])")) continue;
                var compressed = Encoding.Latin1.GetBytes(value);
                using var input = new MemoryStream(compressed);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                CopyToLimited(zlib, output, MaxExtractedBytes);
                ExtractPdfTextObjects(Encoding.Latin1.GetString(output.ToArray()), pieces);
            }
            catch
            {
                // Some PDF streams use image or unsupported filters. Other text streams are still inspected.
            }
        }
        return string.Join("\n", pieces);
    }

    private static void ExtractPdfTextObjects(string content, List<string> pieces)
    {
        foreach (Match textObject in Regex.Matches(content, @"(?:^|\s)BT\s+(?<text>[\s\S]*?)\s+ET(?:\s|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            ExtractPdfTextOperators(textObject.Groups["text"].Value, pieces);
    }

    private static async Task<(bool Succeeded, string Text)> TryReadPdfWithPdftotextAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("PDFTOTEXT_PATH")?.Trim();
        var executables = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) executables.Add(configured);
        executables.Add("pdftotext");
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles)) executables.Add(Path.Combine(programFiles, "Git", "mingw64", "bin", "pdftotext.exe"));
        }

        var parserDirectory = Path.Combine(Path.GetTempPath(), "frevo-resume-parser");
        Directory.CreateDirectory(parserDirectory);
        var inputPath = Path.Combine(parserDirectory, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(inputPath, bytes, cancellationToken);
        try
        {
            foreach (var executable in executables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Process? process = null;
                var started = false;
                try
                {
                    if (Path.IsPathRooted(executable) && !File.Exists(executable)) continue;
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = executable,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.StartInfo.ArgumentList.Add("-layout");
                    process.StartInfo.ArgumentList.Add("-enc");
                    process.StartInfo.ArgumentList.Add("UTF-8");
                    process.StartInfo.ArgumentList.Add(inputPath);
                    process.StartInfo.ArgumentList.Add("-");
                    if (!process.Start()) continue;
                    started = true;

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    var outputTask = ReadBoundedAsync(process.StandardOutput, MaxExtractedCharacters, timeout.Token);
                    var errorTask = ReadBoundedAsync(process.StandardError, 32_768, timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    var output = await outputTask;
                    _ = await errorTask;
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        return (true, output.Length <= MaxExtractedCharacters ? output : output[..MaxExtractedCharacters]);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The external decoder timed out. The safe in-process result remains the fallback.
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // Decoder is optional and may not exist on every deployment host.
                }
                finally
                {
                    if (started)
                    {
                        try { if (process is { HasExited: false }) process.Kill(true); }
                        catch { /* The process is already exiting or inaccessible. */ }
                    }
                    process?.Dispose();
                }
            }
        }
        finally
        {
            try { File.Delete(inputPath); }
            catch { /* The OS will eventually clean this isolated temp file. */ }
        }
        return (false, "");
    }

    private static async Task<(bool Succeeded, string Text)> TryReadPdfWithOcrAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var pdftoppm = Environment.GetEnvironmentVariable("PDFTOPPM_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(pdftoppm)) pdftoppm = "pdftoppm";
        var tesseract = Environment.GetEnvironmentVariable("TESSERACT_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(tesseract)) tesseract = "tesseract";
        var root = Path.Combine(Path.GetTempPath(), "frevo-resume-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "resume.pdf");
        var outputPrefix = Path.Combine(root, "page");
        await File.WriteAllBytesAsync(input, bytes, cancellationToken);
        if (!await OcrGate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
        {
            try { Directory.Delete(root, true); }
            catch { /* Isolated parser files are safe for normal OS temp cleanup. */ }
            return (false, "");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            (bool Succeeded, string Output) rendered;
            try { rendered = await RunProcessAsync(pdftoppm,
                ["-f", "1", "-l", "5", "-scale-to", "2200", "-png", input, outputPrefix], timeout.Token); }
            catch (System.ComponentModel.Win32Exception) { rendered = (false, ""); }
            if (!rendered.Succeeded)
            {
                if (!OperatingSystem.IsWindows()) return (false, "");
                var script = Path.Combine(AppContext.BaseDirectory, "Assets", "Parsing", "WindowsPdfOcr.ps1");
                if (!File.Exists(script)) return (false, "");
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                var windows = await RunProcessAsync(shell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", script, "-InputPdf", input], timeout.Token);
                return (windows.Succeeded && LooksLikeUsefulResumeText(NormalizeText(windows.Output)), windows.Output);
            }
            var pages = Directory.GetFiles(root, "page-*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            if (pages.Count == 0) return (false, "");
            var text = new StringBuilder();
            foreach (var page in pages)
            {
                var recognized = await RunProcessAsync(tesseract, [page, "stdout", "-l", "eng", "--psm", "6"], timeout.Token);
                if (!recognized.Succeeded || string.IsNullOrWhiteSpace(recognized.Output)) continue;
                if (text.Length > 0) text.AppendLine();
                text.Append(recognized.Output);
                if (text.Length >= MaxExtractedCharacters) break;
            }
            var value = text.Length <= MaxExtractedCharacters ? text.ToString() : text.ToString(0, MaxExtractedCharacters);
            return (LooksLikeUsefulResumeText(NormalizeText(value)), value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "");
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { /* Isolated parser files are safe for normal OS temp cleanup. */ }
            OcrGate.Release();
        }
    }

    private static async Task<(bool Succeeded, string Output)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(executable) && !File.Exists(executable)) return (false, "");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return (false, "");
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaxExtractedCharacters, cancellationToken);
            var errorTask = ReadBoundedAsync(process.StandardError, 32_768, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            _ = await errorTask;
            return (process.ExitCode == 0, output);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch { /* Process already exited. */ }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var remaining = maximumCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));
        }
        return result.ToString();
    }

    private static bool LooksLikeUsefulResumeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var sample = value.Length <= 20000 ? value : value[..20000];
        var letters = sample.Count(char.IsLetter);
        var words = Regex.Matches(sample, @"\p{L}[\p{L}\p{M}.'+#-]{2,}").Count;
        return letters >= 40 && words >= 8 && letters >= sample.Length / 12;
    }

    private static void ExtractPdfTextOperators(string value, List<string> pieces)
    {
        foreach (Match match in Regex.Matches(value, @"\((?<text>(?:\\.|[^\\)])*)\)\s*(?:Tj|'|"")", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            var decoded = DecodePdfLiteral(match.Groups["text"].Value);
            AddPdfPiece(pieces, decoded);
        }
        foreach (Match array in Regex.Matches(value, @"\[(?<items>[\s\S]*?)\]\s*TJ", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            var line = string.Concat(Regex.Matches(array.Groups["items"].Value, @"\((?<text>(?:\\.|[^\\)])*)\)")
                .Select(item => DecodePdfLiteral(item.Groups["text"].Value)));
            AddPdfPiece(pieces, line);
        }
    }

    private static void AddPdfPiece(List<string> pieces, string value)
    {
        if (!LooksLikeText(value)) return;
        var used = pieces.Sum(piece => piece.Length);
        if (used >= MaxExtractedCharacters) return;
        pieces.Add(value.Length <= MaxExtractedCharacters - used ? value : value[..(MaxExtractedCharacters - used)]);
    }

    private static string DecodePdfLiteral(string value) => value
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\r", "\n", StringComparison.Ordinal)
        .Replace("\\t", " ", StringComparison.Ordinal)
        .Replace("\\(", "(", StringComparison.Ordinal)
        .Replace("\\)", ")", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static bool LooksLikeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var printable = value.Count(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
        return printable >= Math.Max(2, value.Length * 3 / 4);
    }

    private static string StripRtf(string value)
    {
        value = Regex.Replace(value, @"\\u(?<code>-?\d+)\??", match =>
        {
            if (!int.TryParse(match.Groups["code"].Value, out var code)) return " ";
            if (code < 0) code += 65536;
            return code is >= 0 and <= 0x10ffff ? char.ConvertFromUtf32(code) : " ";
        });
        value = Regex.Replace(value, @"\\'(?<hex>[0-9a-fA-F]{2})", match =>
            ((char)Convert.ToByte(match.Groups["hex"].Value, 16)).ToString());
        value = Regex.Replace(value, @"\\(?:par|line)\b\s*", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\\tab\b\s*", "\t", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\\[a-zA-Z]+-?\d* ?", " ");
        value = value.Replace("\\{", "{").Replace("\\}", "}").Replace("\\\\", "\\");
        return value.Replace("{", " ").Replace("}", " ");
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string NormalizeText(string value)
    {
        value = value.Normalize(NormalizationForm.FormKC).TrimStart('\uFEFF')
            .Replace("\u200B", "", StringComparison.Ordinal)
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\u00AD", "", StringComparison.Ordinal)
            .Replace('\0', ' ')
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2212', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2022', '|')
            .Replace('\u00b7', '|')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        // Repair spacing around email punctuation, never join separate lines/people.
        value = Regex.Replace(value,
            @"[A-Z0-9._%+\-]+[ \t]*@[ \t]*[A-Z0-9\-]+(?:[ \t]*\.[ \t]*[A-Z0-9\-]+)*[ \t]*\.[ \t]*[A-Z]{2,}",
            match => Regex.Replace(match.Value, @"[ \t]+", ""), RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        value = Regex.Replace(value, @"(?<=\S)\t+(?=\S)", " | ");
        value = Regex.Replace(value, @"(?<=\S) {2,}(?=\S)", " | ");
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        value = value.Trim();
        return value.Length <= MaxExtractedCharacters ? value : value[..MaxExtractedCharacters];
    }

    private static async Task CopyToLimitedAsync(Stream source, Stream destination, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) return;
            copied = checked(copied + read);
            if (copied > maximumBytes) throw new InvalidDataException("The resume exceeds the parser input limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void CopyToLimited(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[81920];
        var copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            copied = checked(copied + read);
            if (copied > maximumBytes) throw new InvalidDataException("Compressed resume content exceeds the parser extraction limit.");
            destination.Write(buffer, 0, read);
        }
    }

    private static bool HasPdfSignature(byte[] bytes) =>
        bytes.Length >= 5
        && bytes[0] == 0x25
        && bytes[1] == 0x50
        && bytes[2] == 0x44
        && bytes[3] == 0x46
        && bytes[4] == 0x2D;
}

public sealed record ResumeParsedFacts(
    string Email,
    string Phone,
    string FullName,
    string ResidentialAddress,
    int CharacterCount,
    int LineCount,
    string LanguageCode,
    string SummaryText,
    int? TotalExperienceMonths)
{
    public static ResumeParsedFacts Empty { get; } = new("", "", "", "", 0, 0, "und", "", null);
    public string CurrentCompany { get; init; } = "";
    public string CurrentTitle { get; init; } = "";
    public string CurrentLocation { get; init; } = "";
    public string HighestQualification { get; init; } = "";
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> Certifications { get; init; } = [];
    public IReadOnlyList<ResumeParsedExperience> Experience { get; init; } = [];
    public IReadOnlyList<ResumeParsedEducation> Education { get; init; } = [];
}

public sealed record ResumeParsedExperience(
    string Company,
    string JobTitle,
    string Location,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsCurrent,
    string Evidence);

public sealed record ResumeParsedEducation(
    string Qualification,
    string Institution,
    int? CompletionYear,
    string Evidence);

internal sealed record ResumeStructuredFacts(
    string CurrentCompany,
    string CurrentTitle,
    string CurrentLocation,
    string HighestQualification,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Certifications,
    IReadOnlyList<ResumeParsedExperience> Experience,
    IReadOnlyList<ResumeParsedEducation> Education);

public sealed record ResumeParsedSection(
    string SectionCode,
    string Heading,
    string Content,
    int DisplayOrder,
    decimal Confidence);

public sealed record ResumeParseResult(
    string Status,
    string Text,
    ResumeParsedFacts Facts,
    IReadOnlyList<ResumeParsedSection> Sections,
    string ParserName,
    string ParserVersion,
    string Error)
{
    public static ResumeParseResult WithoutContent(string status, string parserName, string parserVersion, string error) =>
        new(status, "", ResumeParsedFacts.Empty, [], parserName, parserVersion, error);
}
