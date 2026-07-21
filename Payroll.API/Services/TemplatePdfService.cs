using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed class TemplatePdfService
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*(?<key>[A-Za-z0-9_.]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex HtmlBreakPattern = new(
        @"<(br\s*/?|/p|/div|/li|/tr|/h[1-6])\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex HtmlTagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public (byte[]? Bytes, string Error) Create(
        string subjectTemplate,
        string bodyTemplate,
        IReadOnlyDictionary<string, string> values)
    {
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subject = Render(subjectTemplate, values, unresolved);
        var body = Render(bodyTemplate, values, unresolved);
        if (unresolved.Count > 0)
            return (null, $"Offer template contains unsupported placeholder(s): {string.Join(", ", unresolved.OrderBy(value => value))}.");

        subject = HtmlToText(subject);
        body = HtmlToText(body);
        if (string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(body))
            return (null, "The configured offer template has no printable content.");

        var text = string.Join("\n\n", new[] { subject, body }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return (BuildPdf(Wrap(text, 86)), "");
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values, ISet<string> unresolved) =>
        PlaceholderPattern.Replace(template ?? "", match =>
        {
            var key = match.Groups["key"].Value;
            if (values.TryGetValue(key, out var value)) return value ?? "";
            unresolved.Add(key);
            return "";
        });

    private static string HtmlToText(string value)
    {
        value = HtmlBreakPattern.Replace(value ?? "", "\n");
        value = Regex.Replace(value, @"<li\b[^>]*>", "- ", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        value = HtmlTagPattern.Replace(value, " ");
        value = WebUtility.HtmlDecode(value).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = value.Split('\n').Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                if (result.Count == 0 || result[^1].Length > 0) result.Add("");
                continue;
            }

            var line = new StringBuilder();
            foreach (var sourceWord in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = sourceWord;
                while (word.Length > width)
                {
                    if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }
                    result.Add(word[..width]);
                    word = word[width..];
                }
                if (word.Length == 0) continue;
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) result.Add(line.ToString());
        }
        return result.Count == 0 ? ["Offer letter"] : result;
    }

    private static byte[] BuildPdf(IReadOnlyList<string> lines)
    {
        const int linesPerPage = 48;
        var pages = lines.Chunk(linesPerPage).ToList();
        var objects = new Dictionary<int, byte[]>();
        var pageIds = new List<int>();
        objects[1] = Bytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[3] = Bytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var nextObjectId = 4;
        foreach (var pageLines in pages)
        {
            var pageId = nextObjectId++;
            var contentId = nextObjectId++;
            pageIds.Add(pageId);
            var content = new StringBuilder("BT\n/F1 11 Tf\n15 TL\n50 790 Td\n");
            foreach (var line in pageLines)
                content.Append('(').Append(EscapePdf(line)).Append(") Tj\nT*\n");
            content.Append("ET\n");
            var contentBytes = Bytes(content.ToString());
            objects[contentId] = Combine(
                Bytes($"<< /Length {contentBytes.Length} >>\nstream\n"),
                contentBytes,
                Bytes("endstream"));
            objects[pageId] = Bytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>");
        }
        objects[2] = Bytes($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");

        using var output = new MemoryStream();
        Write(output, "%PDF-1.4\n%HRMS\n");
        var maximumId = objects.Keys.Max();
        var offsets = new long[maximumId + 1];
        for (var id = 1; id <= maximumId; id++)
        {
            offsets[id] = output.Position;
            Write(output, $"{id} 0 obj\n");
            output.Write(objects[id]);
            Write(output, "\nendobj\n");
        }
        var xrefOffset = output.Position;
        Write(output, $"xref\n0 {maximumId + 1}\n0000000000 65535 f \n");
        for (var id = 1; id <= maximumId; id++) Write(output, $"{offsets[id]:D10} 00000 n \n");
        Write(output, $"trailer\n<< /Size {maximumId + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static string EscapePdf(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '(' or ')') result.Append('\\').Append(character);
            else if (character is >= ' ' and <= '\u00ff') result.Append(character);
            else result.Append('?');
        }
        return result.ToString();
    }

    private static byte[] Bytes(string value) => Encoding.Latin1.GetBytes(value);
    private static byte[] Combine(params byte[][] chunks)
    {
        var result = new byte[chunks.Sum(chunk => chunk.Length)];
        var offset = 0;
        foreach (var chunk in chunks) { Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length); offset += chunk.Length; }
        return result;
    }
    private static void Write(Stream stream, string value) => stream.Write(Bytes(value));
}
