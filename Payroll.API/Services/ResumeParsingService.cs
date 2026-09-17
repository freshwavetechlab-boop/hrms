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
    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"(?<!\d)(?:\+?91[\s().-]*)?[6-9](?:[\s().-]*\d){9}(?![\s().-]*\d)", RegexOptions.Compiled);
    private static readonly Regex NameLabelPattern = new(@"(?im)^\s*(?:candidate\s+)?(?:full\s+)?name\s*[:\-]\s*(?<value>[A-Z][A-Za-z.'-]+(?:\s+[A-Z][A-Za-z.'-]+){1,4})\s*$", RegexOptions.Compiled);
    private static readonly Regex AddressLabelPattern = new(@"(?im)^\s*(?:(?:current|permanent|residential|postal|mailing)\s+)?address\s*[:\-]\s*(?<value>[^\r\n]{8,300})(?:\r?\n(?<next>[^\r\n]{8,180}))?", RegexOptions.Compiled);
    private static readonly Regex PersonNamePattern = new(@"^[A-Za-z][A-Za-z.'-]+(?:\s+[A-Za-z][A-Za-z.'-]+){1,4}$", RegexOptions.Compiled);
    private static readonly Regex RoleTitlePattern = new(@"\b(?:administrator|analyst|architect|consultant|coordinator|designer|developer|devops|director|engineer|executive|intern|lead|manager|officer|recruiter|specialist|supervisor|team)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            return ResumeParseResult.WithoutContent("Failed", "BuiltIn", "2.1", "Resume text could not be extracted safely. The original document remains available for authorized manual review.");
        }
    }

    public async Task<ResumeParseResult> ParseAsync(Stream source, string fileName, long fileLength, CancellationToken cancellationToken)
    {
        try
        {
            if (fileLength > MaxInputBytes)
                return ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", "2.0", "Resume parsing was skipped because the file exceeds the 10 MB parser limit.");
            using var memory = new MemoryStream();
            await CopyToLimitedAsync(source, memory, MaxInputBytes, cancellationToken);
            var bytes = memory.ToArray();
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var parserName = "BuiltIn";
            var parserVersion = "2.1";
            string text;
            if (extension == ".pdf")
            {
                var pdf = await ReadPdfAsync(bytes, cancellationToken);
                text = pdf.Text;
                parserName = pdf.ParserName;
            }
            else
            {
                text = extension switch
                {
                    ".txt" or ".csv" => DecodeText(bytes),
                    ".rtf" => StripRtf(DecodeText(bytes)),
                    ".docx" => ReadDocx(bytes),
                    _ => ""
                };
            }
            text = NormalizeText(text);
            var status = LooksLikeUsefulResumeText(text) ? "Parsed" : "NeedsReview";
            // Contact identity is intentionally more tolerant than the full parser. Some
            // text PDFs expose the email/phone in the raw PDF payload even when their font
            // encoding prevents reliable page-text extraction. Global Talent Pool intake
            // can still store and preview those resumes for a recruiter.
            var binaryText = Encoding.Latin1.GetString(bytes);
            var email = EmailPattern.Match(text).Value;
            if (string.IsNullOrWhiteSpace(email)) email = EmailPattern.Match(binaryText).Value;
            var phone = PhonePattern.Match(text).Value;
            if (string.IsNullOrWhiteSpace(phone)) phone = PhonePattern.Match(binaryText).Value;
            var fullName = ExtractFullName(text, fileName);
            var residentialAddress = ExtractResidentialAddress(text);
            var sections = BuildSections(text);
            var summary = sections.FirstOrDefault(section => section.SectionCode == "SUMMARY")?.Content
                ?? sections.FirstOrDefault()?.Content
                ?? "";
            var facts = new ResumeParsedFacts(
                email,
                phone,
                fullName,
                residentialAddress,
                text.Length,
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                "und",
                summary.Length <= 1000 ? summary : summary[..1000],
                ExtractTotalExperienceMonths(text));
            return new ResumeParseResult(status, text, facts, sections, parserName, parserVersion, status == "Parsed" ? "" : "Text could not be extracted reliably. The resume remains available for manual review.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Built-in resume parsing failed for {FileName} ({FileLength} bytes).", Path.GetFileName(fileName), fileLength);
            return ResumeParseResult.WithoutContent("Failed", "BuiltIn", "2.0", "Resume text could not be extracted safely. The original document remains available for authorized manual review.");
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
        var facts = new ResumeParsedFacts(
            email,
            phone,
            fullName,
            address,
            text.Length,
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            languageCode,
            summary,
            totalExperienceMonths);
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
            if (code is not null)
            {
                Flush();
                currentCode = code;
                currentHeading = line.Trim().TrimEnd(':');
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
        if (Regex.IsMatch(heading, @"^(PROFILE|PROFESSIONAL SUMMARY|CAREER SUMMARY|SUMMARY|ABOUT ME|OBJECTIVE|CAREER OBJECTIVE)$")) return "SUMMARY";
        if (Regex.IsMatch(heading, @"^(WORK |PROFESSIONAL |EMPLOYMENT )?(EXPERIENCE|HISTORY)$")) return "EXPERIENCE";
        if (Regex.IsMatch(heading, @"^(EDUCATION|ACADEMIC PROFILE|ACADEMIC QUALIFICATIONS?|QUALIFICATIONS?)$")) return "EDUCATION";
        if (Regex.IsMatch(heading, @"^(TECHNICAL |CORE |KEY )?(SKILLS|COMPETENCIES|EXPERTISE)( & TOOLS)?$")) return "SKILLS";
        if (Regex.IsMatch(heading, @"^(CERTIFICATIONS?|LICENSES?|CERTIFICATIONS? & LICENSES?)$")) return "CERTIFICATIONS";
        if (Regex.IsMatch(heading, @"^(PROJECTS?|KEY PROJECTS?|PROJECT EXPERIENCE)$")) return "PROJECTS";
        if (Regex.IsMatch(heading, @"^(ACHIEVEMENTS?|AWARDS?|HONORS?|AWARDS? & HONORS?)$")) return "ACHIEVEMENTS";
        if (Regex.IsMatch(heading, @"^(PERSONAL DETAILS|CONTACT|CONTACT DETAILS)$")) return "CONTACT";
        if (Regex.IsMatch(heading, @"^(LANGUAGES?|LANGUAGES? KNOWN)$")) return "LANGUAGES";
        if (Regex.IsMatch(heading, @"^(PUBLICATIONS?|RESEARCH|RESEARCH & PUBLICATIONS?)$")) return "PUBLICATIONS";
        return null;
    }

    private static int? ExtractTotalExperienceMonths(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = Regex.Matches(text, @"(?<!\d)(?<years>\d{1,2}(?:\.\d{1,2})?)\s*\+?\s*(?:years?|yrs?)(?:\s+of)?\s+(?:relevant\s+|professional\s+|total\s+)?experience", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => decimal.TryParse(match.Groups["years"].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var years) ? years : -1)
            .Where(years => years is >= 0 and <= 60)
            .ToList();
        if (values.Count == 0) return null;
        return (int)Math.Round(values.Max() * 12m, MidpointRounding.AwayFromZero);
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
                if (PersonNamePattern.IsMatch(segment) && !RoleTitlePattern.IsMatch(segment)) return segment;
            }
        }
        var fallback = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"(?i)\b(resume|cv|profile|updated|latest|final)\b", " ");
        fallback = Regex.Replace(fallback, @"[_\-\d]+", " ");
        fallback = Regex.Replace(fallback, @"\s+", " ").Trim();
        return Regex.IsMatch(fallback, @"^[A-Za-z][A-Za-z.'-]+(?:\s+[A-Za-z][A-Za-z.'-]+){0,4}$") ? fallback : "";
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
        var document = archive.GetEntry("word/document.xml");
        if (document is null) return "";
        if (document.Length > MaxExtractedBytes) throw new InvalidDataException("The DOCX document content exceeds the parser extraction limit.");
        using var stream = document.Open();
        var xml = XDocument.Load(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("\n", xml.Descendants(word + "p").Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(node => node.Value))));
    }

    private static async Task<(string Text, string ParserName)> ReadPdfAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        // pdftotext understands embedded ToUnicode/CMap font encodings that the
        // lightweight parser cannot decode. Prefer it before inspecting PDF binary
        // operators: image-heavy client documents can otherwise spend tens of
        // seconds in regex/decompression work and exceed the HTTP request timeout.
        var decoded = await TryReadPdfWithPdftotextAsync(bytes, cancellationToken);
        if (decoded.Succeeded) return (decoded.Text, "PdfToText");

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
        ExtractPdfTextOperators(raw, pieces);
        foreach (Match match in Regex.Matches(raw, @"stream\r?\n(?<data>[\s\S]*?)\r?\nendstream", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            if (pieces.Sum(piece => piece.Length) >= MaxExtractedCharacters) break;
            try
            {
                var value = match.Groups["data"].Value;
                var compressed = Encoding.Latin1.GetBytes(value);
                using var input = new MemoryStream(compressed);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                CopyToLimited(zlib, output, MaxExtractedBytes);
                ExtractPdfTextOperators(Encoding.Latin1.GetString(output.ToArray()), pieces);
            }
            catch
            {
                // Some PDF streams use image or unsupported filters. Other text streams are still inspected.
            }
        }
        return string.Join("\n", pieces);
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
                    var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
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
        value = Regex.Replace(value, @"\\'[0-9a-fA-F]{2}", " ");
        value = Regex.Replace(value, @"\\[a-zA-Z]+-?\d* ?", " ");
        return value.Replace("{", " ").Replace("}", " ");
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string NormalizeText(string value)
    {
        value = value.Replace('\0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
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
}

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
