using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Payroll.API.Services;

public sealed class LayoutDocumentPdfService
{
    public const string Marker = "<!-- frevo-document-layout:v1 -->";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.]{0,79})\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    public static bool IsLayout(string? body) => body?.TrimStart().StartsWith(Marker, StringComparison.Ordinal) == true;

    public static RecruitmentDocumentLayout Parse(string body)
    {
        if (!IsLayout(body) || Encoding.UTF8.GetByteCount(body) > 64000) throw new InvalidOperationException("Use a supported document layout of at most 64 KB.");
        RecruitmentDocumentLayout layout;
        try { layout = JsonSerializer.Deserialize<RecruitmentDocumentLayout>(body.TrimStart()[Marker.Length..], Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The document layout JSON is invalid or contains unsupported properties."); }
        Validate(layout); return layout;
    }

    public static void Validate(RecruitmentDocumentLayout layout)
    {
        static bool Key(string? s) => s is not null && Regex.IsMatch(s, @"^[A-Za-z][A-Za-z0-9_]{0,49}$");
        static bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        if (layout.Title is null || layout.Title.Length > 300 || layout.Version != 1 || !Range(layout.PageWidth, 300, 1200) || !Range(layout.PageHeight, 400, 1600)
            || layout.Sections is not { Count: > 0 and <= 30 } || layout.Sections.Any(s => s is null || s.Pages is < 1 or > 30)
            || layout.Sections.Sum(s => s.Pages) > 30 || layout.Inputs is null || layout.Inputs.Count > 80
            || layout.Calculations is null || layout.Calculations.Count > 32 || layout.Checks is null || layout.Checks.Count > 32
            || layout.ImportPresets is null || layout.ImportPresets.Count > 20 || layout.Header is null || layout.Footer is null) throw new InvalidOperationException("Invalid document dimensions, page count or field definitions.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in layout.Inputs)
            if (f is null || !Key(f.Key) || !keys.Add(f.Key) || f.Label is null || f.Label.Length > 120
                || f.Type is not ("text" or "date" or "money" or "number" or "percent")
                || f.SourceHeaders is null || f.SourceHeaders.Count > 20 || f.SourceHeaders.Any(h => h is null || h.Length > 250)
                || f.EmptyValues is null || f.EmptyValues.Count > 20 || f.EmptyValues.Any(v => v is null || v.Length > 100)
                || f.DefaultValue is null || f.DefaultValue.Length > 4000 || f.BlankText is null || f.BlankText.Length > 100)
                throw new InvalidOperationException("A document input is invalid or duplicated.");
        foreach (var preset in layout.ImportPresets)
            if (preset is null || preset.Name is null || preset.Name.Length is < 1 or > 180 || preset.HeaderRow is < 1 or > 100
                || preset.Defaults is null || preset.Defaults.Count > 80 || preset.Defaults.Any(p => !keys.Contains(p.Key) || p.Value is null || p.Value.Length > 4000))
                throw new InvalidOperationException("A document import preset is invalid.");
        foreach (var c in layout.Calculations)
        {
            if (c is null || !Key(c.Key) || keys.Contains(c.Key) || c.Operation is not ("add" or "subtract" or "multiply" or "divide")
                || c.Inputs is not { Count: 2 } || c.Inputs.Any(k => !keys.Contains(k))) throw new InvalidOperationException("A document calculation has invalid or forward-referenced inputs.");
            keys.Add(c.Key);
        }
        foreach (var c in layout.Checks)
            if (c is null || !keys.Contains(c.Left) || !keys.Contains(c.Right) || c.Tolerance is < 0 or > 1 || c.Message is null || c.Message.Length > 250)
                throw new InvalidOperationException("A document amount check is invalid.");
        var allText = new List<DocumentText>();
        void Text(DocumentText t)
        {
            if (t is null || t.Text is null || t.Text.Length > 16000 || t.Runs is null || t.Runs.Count > 120
                || t.Runs.Any(r => r is null || r.Text is null || r.Text.Length > 16000)
                || !Range(t.FontSize, 6, 36) || !Range(t.LineHeight, 6, 60) || t.LineHeight < t.FontSize
                || t.FontFamily is not ("serif" or "sans") || t.Align is not ("left" or "center" or "right" or "justify"))
                throw new InvalidOperationException("A document text style or text block is invalid.");
            allText.Add(t);
        }
        void Elements(IEnumerable<DocumentElement> elements)
        {
            foreach (var e in elements)
            {
                if (e is null || e.Kind is not ("text" or "line" or "rect" or "image") || !Range(e.X, 0, layout.PageWidth)
                    || !Range(e.Y, 0, layout.PageHeight) || !Range(e.Width, 0, layout.PageWidth - e.X + .01)
                    || !Range(e.Height, 0, layout.PageHeight - e.Y + .01) || !Range(e.StrokeWidth, .1, 4) || e.MinFontSize != 0 && !Range(e.MinFontSize, 6, e.FontSize)
                    || e.Kind == "image" && e.Asset != "organizationLogo" || e.Kind is "text" or "rect" or "image" && (e.Width <= 0 || e.Height <= 0))
                    throw new InvalidOperationException("A document element is invalid or outside the page. Only the organization logo asset is supported.");
                Text(e);
            }
        }
        Elements(layout.Header); Elements(layout.Footer);
        foreach (var s in layout.Sections)
        {
            if (s.Blocks is null || s.Elements is null || s.Blocks.Count > 300 || s.Elements.Count > 300
                || !Range(s.Left, 0, layout.PageWidth) || !Range(s.Top, 0, layout.PageHeight)
                || !Range(s.Bottom, s.Top + 1, layout.PageHeight) || !Range(s.Width, 30, layout.PageWidth - s.Left + .01)
                || !s.Flow && s.Pages != 1 || !s.Flow && s.Blocks.Count > 0 || s.Flow && s.Elements.Count > 0)
                throw new InvalidOperationException("A document section is invalid.");
            Elements(s.Elements);
            foreach (var b in s.Blocks)
            {
                if (b is null || b.Kind is not ("paragraph" or "table" or "gap") || !Range(b.SpaceBefore, 0, 300)
                    || !Range(b.SpaceAfter, 0, 300) || !Range(b.Indent, 0, s.Width - 20) || !Range(b.Height, 0, layout.PageHeight)
                    || b.Bullet is null || b.Bullet.Length > 5 || b.Rows is null || b.ColumnWidths is null)
                    throw new InvalidOperationException("A document flow block is invalid.");
                Text(b);
                if (b.Kind == "table")
                {
                    if (b.Rows.Count is < 1 or > 100 || b.ColumnWidths.Count is < 1 or > 10 || b.ColumnWidths.Any(w => !Range(w, 20, s.Width))
                        || Math.Abs(b.ColumnWidths.Sum() - s.Width) > .1 || !Range(b.PaddingX, 0, 20) || !Range(b.PaddingY, 0, 20)
                        || !Range(b.StrokeWidth, .1, 4) || b.Rows.Any(r => r is null || r.Count != b.ColumnWidths.Count))
                        throw new InvalidOperationException("A document table is invalid.");
                    foreach (var cell in b.Rows.SelectMany(r => r)) Text(cell);
                }
            }
        }
        if (allText.Count > 2000) throw new InvalidOperationException("The document has too many elements.");
        var allowed = keys.Concat(BuiltInKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var text in allText.SelectMany(t => new[] { t.Text }.Concat(t.Runs.Select(r => r.Text))).Append(layout.Title))
            foreach (Match m in Token.Matches(text))
                if (!allowed.Contains(m.Groups[1].Value.Split('.')[0])) throw new InvalidOperationException("The document uses an undeclared field: " + m.Groups[1].Value + ".");
    }
    public static readonly string[] BuiltInKeys = ["candidateName", "candidateFirstName", "candidateLastName", "candidateEmail", "candidatePhone", "candidateCode", "clientName", "organizationName", "offerNumber", "offerDate", "date", "joiningDate", "positionTitle", "jobLocation", "currency", "formattedCtc", "offeredCtc", "expiryDate", "remarks", "companyName", "ctcInWords"];

    public static Dictionary<string, string> PrepareValues(RecruitmentDocumentLayout layout, IReadOnlyDictionary<string, string> input, bool requireAll, out List<string> missing)
    {
        var values = new Dictionary<string, string>(input, StringComparer.OrdinalIgnoreCase);
        missing = [];
        var numbers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in layout.Inputs)
        {
            var raw = values.GetValueOrDefault(f.Key, f.DefaultValue)?.Trim() ?? "";
            if (f.EmptyValues.Contains(raw, StringComparer.OrdinalIgnoreCase)) raw = "";
            if (raw.Length == 0)
            {
                if (f.Required) missing.Add(f.Label.Length > 0 ? f.Label : f.Key);
                values[f.Key] = f.BlankText; continue;
            }
            if (raw.Length > 4000) { missing.Add((f.Label.Length > 0 ? f.Label : f.Key) + " is too long"); continue; }
            values[f.Key] = raw;
            if (f.Type == "date")
            {
                var cleaned = Regex.Replace(Regex.Replace(raw, @"^\s*(?:up\s+to|upto|till|until)\s*", "", RegexOptions.IgnoreCase), @"(?<=\d)(?:st|nd|rd|th)\b", "", RegexOptions.IgnoreCase);
                if (!DateTime.TryParse(cleaned, India, DateTimeStyles.AllowWhiteSpaces, out var d)) { missing.Add(f.Label + " is not a valid date"); continue; }
                values[f.Key] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); values[f.Key + ".date"] = d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }
            if (f.Type is "money" or "number" or "percent")
            {
                var percent = raw.EndsWith('%');
                var clean = raw.Replace(",", "").Replace("₹", "").Replace("INR", "", StringComparison.OrdinalIgnoreCase).Trim().TrimEnd('%').Trim();
                if (f.Type == "money" && clean.EndsWith("/-", StringComparison.Ordinal)) clean = clean[..^2].Trim();
                if (!decimal.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > 999999999999m)
                { missing.Add(f.Label + " is not a valid number"); continue; }
                if (f.Type == "percent" && percent) n /= 100;
                numbers[f.Key] = n; values[f.Key] = n.ToString(CultureInfo.InvariantCulture);
            }
        }
        foreach (var c in layout.Calculations)
        {
            if (!numbers.TryGetValue(c.Inputs[0], out var a) || !numbers.TryGetValue(c.Inputs[1], out var b)) continue;
            try
            {
                var n = c.Operation switch { "add" => a + b, "subtract" => a - b, "multiply" => a * b, "divide" when b != 0 => a / b, _ => throw new InvalidOperationException("A document calculation divides by zero.") };
                if (Math.Abs(n) > 999999999999m) throw new OverflowException();
                numbers[c.Key] = n; values[c.Key] = n.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is OverflowException or InvalidOperationException) { missing.Add("Review document calculation " + c.Key); }
        }
        foreach (var (key, n) in numbers)
        {
            values[key + ".money"] = n.ToString("N2", India);
            values[key + ".amount"] = n.ToString(n == decimal.Truncate(n) ? "N0" : "N2", India);
            values[key + ".percent"] = (n * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
            if (n >= 0) values[key + ".words"] = BrandedOfferPdfService.AmountInWords(n);
        }
        foreach (var check in layout.Checks)
            if (numbers.TryGetValue(check.Left, out var a) && numbers.TryGetValue(check.Right, out var b) && Math.Abs(a - b) > check.Tolerance)
                missing.Add(check.Message);
        if (requireAll && missing.Count > 0) throw new InvalidOperationException("Complete the document details: " + string.Join(", ", missing.Distinct()) + ".");
        return values;
    }

    public (byte[]? Bytes, string Error) Create(string body, IReadOnlyDictionary<string, string> input, byte[]? organizationLogo = null, bool draft = true)
    {
        try
        {
            var layout = Parse(body);
            var values = PrepareValues(layout, input, true, out _);
            DocumentFontResolver.EnsureInitialized();
            using var document = new PdfDocument(); document.Info.Title = Expand(layout.Title, values);
            using var logoStream = organizationLogo is null ? null : new MemoryStream(organizationLogo);
            using var logo = logoStream is null ? null : XImage.FromStream(logoStream);
            XGraphics? gfx = null;
            void Page()
            {
                gfx?.Dispose(); var page = document.AddPage(); page.Width = XUnit.FromPoint(layout.PageWidth); page.Height = XUnit.FromPoint(layout.PageHeight);
                gfx = XGraphics.FromPdfPage(page);
                foreach (var e in layout.Header.Concat(layout.Footer)) Element(gfx, e, values, logo);
                if (draft) gfx.DrawString("UNSIGNED DRAFT", Font("sans", 7, false, false), XBrushes.Gray, new XRect(layout.PageWidth - 182, 103, 120, 10), XStringFormats.TopRight);
            }
            try
            {
                foreach (var section in layout.Sections)
                {
                    Page(); var used = 1; var y = section.Top;
                    if (!section.Flow) { foreach (var e in section.Elements) Element(gfx!, e, values, logo); continue; }
                    foreach (var block in section.Blocks)
                    {
                        var height = block.Kind == "table" ? TableHeight(gfx!, block, values) : block.Kind == "gap" ? block.Height : Lines(gfx!, block, values, section.Width - block.Indent).Count * block.LineHeight;
                        if (height + block.SpaceBefore > section.Bottom - section.Top) throw new InvalidOperationException("A document block is too tall for its page. Review the layout or field length.");
                        if (y + block.SpaceBefore + height > section.Bottom)
                        {
                            if (++used > section.Pages) throw new InvalidOperationException("The document exceeds its configured page count. Review long fields or the template layout; no text was omitted.");
                            Page(); y = section.Top;
                        }
                        y += block.SpaceBefore;
                        if (block.Kind == "table") Table(gfx!, block, values, section.Left, y);
                        else if (block.Kind != "gap")
                        {
                            if (block.Bullet.Length > 0) gfx!.DrawString(block.Bullet, Font(block.FontFamily, block.FontSize, block.Bold, block.Italic), XBrushes.Black, section.Left, y + block.FontSize);
                            Draw(gfx!, block, values, section.Left + block.Indent, y, section.Width - block.Indent);
                        }
                        y += height + block.SpaceAfter;
                    }
                    while (used++ < section.Pages) Page();
                }
            }
            finally { gfx?.Dispose(); }
            using var stream = new MemoryStream(); document.Save(stream, false); return (stream.ToArray(), "");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or JsonException)
        { return (null, e.Message); }
    }

    private static string Expand(string text, IReadOnlyDictionary<string, string> values) => Token.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new InvalidOperationException("The document field is unavailable: " + m.Groups[1].Value + "."));
    private static XFont Font(string family, double size, bool bold, bool italic) => new(family == "sans" ? "LayoutSans" : family == "rupee" ? "OfferSerif" : "LayoutSerif", size, bold && italic ? XFontStyleEx.BoldItalic : bold ? XFontStyleEx.Bold : italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
    private sealed record Word(string Text, XFont Font, double Width);
    private sealed record Line(List<Word> Words, double Width, bool Final);
    private static double Width(XGraphics gfx, string text, XFont font)
    {
        if (!text.Contains('₹')) return gfx.MeasureString(text, font).Width;
        var parts = text.Split('₹'); return parts.Sum(p => gfx.MeasureString(p, font).Width) + (parts.Length - 1) * gfx.MeasureString("₹", Font("rupee", font.Size, font.Bold, font.Italic)).Width;
    }
    private static void Paint(XGraphics gfx, Word word, double x, double baseline)
    {
        var parts = word.Text.Split('₹');
        for (var i = 0; i < parts.Length; i++)
        {
            gfx.DrawString(parts[i], word.Font, XBrushes.Black, x, baseline); x += gfx.MeasureString(parts[i], word.Font).Width;
            if (i < parts.Length - 1) { var font = Font("rupee", word.Font.Size, word.Font.Bold, word.Font.Italic); gfx.DrawString("₹", font, XBrushes.Black, x, baseline); x += gfx.MeasureString("₹", font).Width; }
        }
    }
    private static List<Line> Lines(XGraphics gfx, DocumentText text, IReadOnlyDictionary<string, string> values, double width)
    {
        var runs = text.Runs.Count > 0 ? text.Runs : [new DocumentRun { Text = text.Text, Bold = text.Bold, Italic = text.Italic }];
        var lines = new List<Line>(); var words = new List<Word>(); double used = 0;
        var space = gfx.MeasureString(" ", Font(text.FontFamily, text.FontSize, false, false)).Width;
        void Finish(bool final) { lines.Add(new(words, used, final)); words = []; used = 0; }
        void Add(string value, XFont font)
        {
            var w = Width(gfx, value, font);
            if (w > width)
            {
                if (words.Count > 0) Finish(false);
                var part = "";
                foreach (var c in value)
                { if (Width(gfx, part + c, font) > width && part.Length > 0) { var p = Width(gfx, part, font); words.Add(new(part, font, p)); used = p; Finish(false); part = ""; } part += c; }
                if (part.Length > 0) { var p = Width(gfx, part, font); words.Add(new(part, font, p)); used = p; }
                return;
            }
            if (words.Count > 0 && used + space + w > width) Finish(false);
            if (words.Count > 0) used += space;
            words.Add(new(value, font, w)); used += w;
        }
        foreach (var run in runs)
        {
            var font = Font(text.FontFamily, text.FontSize, text.Bold || run.Bold, text.Italic || run.Italic);
            foreach (Match token in Regex.Matches(Expand(run.Text, values).Replace("\r", ""), @"\n|[^\s]+"))
                if (token.Value == "\n") Finish(true); else Add(token.Value, font);
        }
        if (words.Count > 0 || lines.Count == 0) Finish(true);
        return lines;
    }
    private static void Draw(XGraphics gfx, DocumentText text, IReadOnlyDictionary<string, string> values, double x, double y, double width)
    {
        var lines = Lines(gfx, text, values, width);
        var normalSpace = gfx.MeasureString(" ", Font(text.FontFamily, text.FontSize, false, false)).Width;
        foreach (var line in lines)
        {
            var left = x + (text.Align == "center" ? (width - line.Width) / 2 : text.Align == "right" ? width - line.Width : 0);
            var space = text.Align == "justify" && !line.Final && line.Words.Count > 1 ? normalSpace + (width - line.Width) / (line.Words.Count - 1) : normalSpace;
            foreach (var word in line.Words) { Paint(gfx, word, left, y + text.FontSize); left += word.Width + space; }
            y += text.LineHeight;
        }
    }
    private static List<double> RowHeights(XGraphics gfx, DocumentBlock block, IReadOnlyDictionary<string, string> values) => block.Rows.Select(row => row.Select((cell, i) => Lines(gfx, cell, values, block.ColumnWidths[i] - block.PaddingX * 2).Count * cell.LineHeight).Max() + block.PaddingY * 2).ToList();
    private static double TableHeight(XGraphics gfx, DocumentBlock block, IReadOnlyDictionary<string, string> values) => RowHeights(gfx, block, values).Sum();
    private static void Table(XGraphics gfx, DocumentBlock block, IReadOnlyDictionary<string, string> values, double x, double y)
    {
        var heights = RowHeights(gfx, block, values); var pen = new XPen(XColors.Black, block.StrokeWidth);
        for (var r = 0; r < block.Rows.Count; r++)
        {
            var left = x;
            for (var c = 0; c < block.ColumnWidths.Count; c++)
            {
                var w = block.ColumnWidths[c]; gfx.DrawRectangle(pen, left, y, w, heights[r]);
                Draw(gfx, block.Rows[r][c], values, left + block.PaddingX, y + block.PaddingY, w - block.PaddingX * 2); left += w;
            }
            y += heights[r];
        }
    }
    private static void Element(XGraphics gfx, DocumentElement e, IReadOnlyDictionary<string, string> values, XImage? logo)
    {
        var pen = new XPen(XColors.Black, e.StrokeWidth);
        switch (e.Kind)
        {
            case "line": gfx.DrawLine(pen, e.X, e.Y, e.X + e.Width, e.Y + e.Height); break;
            case "rect": gfx.DrawRectangle(pen, e.X, e.Y, e.Width, e.Height); break;
            case "image": if (logo is null) throw new InvalidOperationException("Configure the organization logo before generating this document."); gfx.DrawImage(logo, e.X, e.Y, e.Width, e.Height); break;
            case "text":
                DocumentText fitted = e;
                if (e.MinFontSize > 0)
                    for (var size = e.FontSize - .1; Lines(gfx, fitted, values, e.Width).Count * fitted.LineHeight > e.Height + .1 && size >= e.MinFontSize - .001; size -= .1)
                        fitted = new DocumentText { Text=e.Text,Runs=e.Runs,FontSize=size,LineHeight=e.LineHeight*size/e.FontSize,FontFamily=e.FontFamily,Bold=e.Bold,Italic=e.Italic,Align=e.Align };
                if (Lines(gfx, fitted, values, e.Width).Count * fitted.LineHeight > e.Height + .1) throw new InvalidOperationException($"A document text field at ({e.X:0.##}, {e.Y:0.##}) exceeds its box. Review long fields or the template; no text was omitted.");
                Draw(gfx, fitted, values, e.X, e.Y + e.FontSize - fitted.FontSize, e.Width); break;
        }
    }
}
