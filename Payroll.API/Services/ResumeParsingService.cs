using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Payroll.API.Services;

public sealed class ResumeParsingService(ILogger<ResumeParsingService> logger)
{
    private const int MaxInputBytes = 10 * 1024 * 1024;
    private const int MaxExtractedBytes = 20 * 1024 * 1024;
    private const int MaxExtractedCharacters = 2_000_000;
    private const int MaxBuiltInPdfBytes = 512 * 1024;
    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"(?<!\d)(?:\+?91[\s\-]?)?[6-9]\d{9}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex NameLabelPattern = new(@"(?im)^\s*(?:candidate\s+)?(?:full\s+)?name\s*[:\-]\s*(?<value>[A-Z][A-Za-z.'-]+(?:\s+[A-Z][A-Za-z.'-]+){1,4})\s*$", RegexOptions.Compiled);
    private static readonly Regex AddressLabelPattern = new(@"(?im)^\s*(?:(?:current|permanent|residential|postal|mailing)\s+)?address\s*[:\-]\s*(?<value>[^\r\n]{8,300})(?:\r?\n(?<next>[^\r\n]{8,180}))?", RegexOptions.Compiled);

    public async Task<ResumeParseResult> ParseAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();
        return await ParseAsync(source, file.FileName, file.Length, cancellationToken);
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
            var line = Regex.Replace(rawLine.Trim(), @"\s+", " ").Trim(' ', '-', '|');
            if (line.Length is < 4 or > 80 || ignored.Contains(line) || line.Contains('@') || PhonePattern.IsMatch(line)) continue;
            if (Regex.IsMatch(line, @"https?://|www\.|linkedin|github|address|email|phone|mobile", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(line, @"^[A-Za-z][A-Za-z.'-]+(?:\s+[A-Za-z][A-Za-z.'-]+){1,4}$")) return line;
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
        return value.Length <= 500 ? value : value[..500];
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
                    if (process.ExitCode == 0)
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
