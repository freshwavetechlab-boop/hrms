using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Payroll.API.Services;

// Opt-in renderer: existing non-branded templates retain their original renderer.
public sealed class BrandedOfferPdfService
{
    public const string Marker = "<!-- gad-uidai-offer:v1 -->";
    public const string SignatoryBlockMarker = "[[AUTHORIZED_SIGNATORY_BLOCK]]";
    public static string SigningAssetPath(string? configuredPath, string? runtimeDirectory = null) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(runtimeDirectory ?? AppContext.BaseDirectory, "PrivateAssets", "OfferSigning", "GadAuthrizedSignAndSeal.png")
            : Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(runtimeDirectory ?? AppContext.BaseDirectory, configuredPath);
    private static readonly object FontLock = new();
    public static string DefaultTemplate => System.Text.Encoding.UTF8.GetString(Resource("uidai-offer.txt"));
    public static readonly string[] JoiningDocuments = [
        "Proof of Date of Birth (Birth Certificate / SSC)", "PAN Card", "Aadhaar Card",
        "Passport size colour photographs (soft copy)", "Address / ID Proof (Passport / Voter ID / Driving License)",
        "Academic certificates (Class 10 onwards)", "Appointment letter from previous employer",
        "Resignation / Relieving letter from previous employer", "Salary slips / Bank statement for last 3 months",
        "Two professional references (preferably previous employers)", "Bank passbook with IFSC or Cancelled Cheque", "Copy of UAN Card"
    ];

    public byte[] Create(string text, byte[] logoBytes, byte[]? approvedSignature)
    {
        lock (FontLock) { GlobalFontSettings.FontResolver ??= new OfferFontResolver(); }
        using var document = new PdfDocument();
        document.Info.Title = "Offer Letter";
        using var logoStream = new MemoryStream(logoBytes);
        using var logo = XImage.FromStream(logoStream);
        using var sealStream = approvedSignature is null ? null : new MemoryStream(approvedSignature);
        using var seal = sealStream is null ? null : XImage.FromStream(sealStream);
        var normal = new XFont("OfferSerif", 10, XFontStyleEx.Regular);
        var bold = new XFont("OfferSerif", 11, XFontStyleEx.Bold);
        var small = new XFont("OfferSerif", 8, XFontStyleEx.Regular);
        XGraphics? gfx = null;
        double y = 100;
        const double left = 58, width = 480, bottom = 720, lineHeight = 13;
        void NewPage()
        {
            gfx?.Dispose();
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            // A separate graphics stream keeps watermark transparency away from text.
            var opacity = new PdfDictionary(document);
            opacity.Elements.SetName("/Type", "/ExtGState");
            opacity.Elements.SetReal("/ca", .12);
            opacity.Elements.SetReal("/CA", .12);
            document.Internals.AddObject(opacity);
            using (var watermark = XGraphics.FromPdfPage(page))
                watermark.DrawImage(logo, 205, 355, 180, 180 * logo.PixelHeight / logo.PixelWidth);
            var states = page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ExtGState");
            if (states is null)
            {
                states = new PdfDictionary(document);
                page.Elements.GetDictionary("/Resources")!.Elements["/ExtGState"] = states;
            }
            states.Elements["/OfferWatermark"] = opacity.Reference!;
            var content = page.Contents.Elements.GetDictionary(0)!;
            var bytes = content.Stream.UnfilteredValue;
            content.Elements.Remove("/Filter");
            // Transparent PNG drawing may emit its own graphics state. Apply opacity
            // immediately before each image invocation, after that state change.
            content.Stream.Value = Encoding.Latin1.GetBytes(Regex.Replace(Encoding.Latin1.GetString(bytes), @"(/[^\s]+\s+Do\b)", "/OfferWatermark gs\n$1"));
            gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.DrawImage(logo, left, 15, 62, 62 * logo.PixelHeight / logo.PixelWidth);
            gfx.DrawString("GA DIGITAL WEB WORD PVT.LTD", new XFont("OfferSerif", 19, XFontStyleEx.Bold), new XSolidBrush(XColor.FromArgb(47, 84, 150)), 137, 49);
            gfx.DrawLine(XPens.Black, 137, 69, 538, 69);
            gfx.DrawLine(XPens.LightSteelBlue, left, 753, 538, 753);
            var footer = new[] {
                "Corporate Office: S-14, 2nd Floor, Aditya City Centre, Indirapuram, Ghaziabad, U.P-201014",
                "Registered Office: Plot No.1, Hargovind Enclave, Vikas Marg Extension, Delhi-110092",
                "Landline - 0120-4218525, 0120-4118516, 0120-4125729, 0120-4156899, 0120-4549656",
                "www.gadigital.in" };
            for (var i = 0; i < footer.Length; i++)
                gfx.DrawString(footer[i], small, XBrushes.RoyalBlue, new XRect(left, 758 + i * 11, width, 11), XStringFormats.TopCenter);
            y = 100;
        }
        try
        {
            NewPage();
            foreach (var paragraph in text.Replace(Marker, "").Replace("\r", "").Trim().Split('\n'))
            {
                if (paragraph.Trim() == "[[PAGE_BREAK]]") { NewPage(); continue; }
                if (paragraph.Trim() == SignatoryBlockMarker)
                {
                    // Keep the original stamp intact and place the signatory text over it,
                    // as on the supplied reference. No pixel erasing or white patch.
                    const double blockHeight = 112;
                    if (y + blockHeight > bottom) NewPage();
                    if (seal is not null) gfx!.DrawImage(seal, left, y - 8, 140, 140 * seal.PixelHeight / seal.PixelWidth);
                    gfx!.DrawString("For GA Digital Web Word Pvt. Ltd.", normal, XBrushes.Black, left, y + 10);
                    gfx.DrawString("(Authorized Signatory)", normal, XBrushes.Black, left, y + 24);
                    if (seal is null) gfx.DrawString("[Signature and seal pending approval]", small, XBrushes.Gray, left, y + 44);
                    y += blockHeight;
                    continue;
                }
                if (paragraph.Trim() == "[[AUTHORIZED_SIGNATURE]]")
                {
                    if (y + 90 > bottom) NewPage();
                    if (seal is not null) gfx!.DrawImage(seal, left, y, 110, 110 * seal.PixelHeight / seal.PixelWidth);
                    else gfx!.DrawString("[Authorized signature and seal pending approval]", small, XBrushes.Gray, left, y + 12);
                    y += 85;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(paragraph)) { y += 7; continue; }
                if (paragraph == "## OFFER LETTER")
                {
                    gfx!.DrawString("OFFER LETTER", bold, XBrushes.Black, new XRect(left, y - 10, width, 15), XStringFormats.TopCenter);
                    y += 23;
                    continue;
                }
                if (paragraph.StartsWith("Ref:") && paragraph.Contains("Date: -"))
                {
                    var dateIndex = paragraph.IndexOf("Date: -", StringComparison.Ordinal);
                    gfx!.DrawString(paragraph[..dateIndex].Trim(), normal, XBrushes.Black, left, y);
                    gfx.DrawString(paragraph[dateIndex..], normal, XBrushes.Black, new XRect(left, y - 10, width, 15), XStringFormats.TopRight);
                    y += lineHeight;
                    continue;
                }
                var heading = paragraph.StartsWith("## ");
                var font = heading ? bold : normal;
                var words = (heading ? paragraph[3..] : paragraph).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var line = "";
                void DrawLine()
                {
                    if (y + lineHeight > bottom) NewPage();
                    gfx!.DrawString(line, font, XBrushes.Black, left, y);
                    y += lineHeight;
                    line = "";
                }
                foreach (var word in words)
                {
                    if (line.Length > 0 && gfx!.MeasureString(line + " " + word, font).Width > width) DrawLine();
                    // Break long unspaced identifiers rather than drawing outside the page.
                    foreach (var character in word)
                    {
                        if (gfx!.MeasureString(line + character, font).Width > width) DrawLine();
                        line += character;
                    }
                    line += " ";
                }
                if (line.Length > 0) DrawLine();
            }
        }
        finally { gfx?.Dispose(); }
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    public static string AmountInWords(decimal value)
    {
        string[] ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        string Words(long n) => n switch {
            < 20 => ones[n], < 100 => tens[n / 10] + (n % 10 > 0 ? " " + Words(n % 10) : ""),
            _ => Parts(n, n >= 10000000 ? 10000000 : n >= 100000 ? 100000 : n >= 1000 ? 1000 : 100)
        };
        string Parts(long n, long unit) => Words(n / unit) + (unit == 10000000 ? " crore" : unit == 100000 ? " lakh" : unit == 1000 ? " thousand" : " hundred") + (n % unit > 0 ? " " + Words(n % unit) : "");
        if (value < 0 || value > 999999999999m) throw new ArgumentOutOfRangeException(nameof(value));
        var amount = decimal.Round(value, 2);
        var paise = (int)((amount - decimal.Truncate(amount)) * 100);
        var text = Words((long)amount) + (paise > 0 ? " and " + Words(paise) + " paise" : "") + " only";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string TemplateHash(string body) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public static bool MatchesApprovedTerms(string? payload, string body, decimal ctc, string currency, DateTime joiningDate, long templateId)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload ?? "{}");
            var root = json.RootElement;
            return root.GetProperty("OfferLetterTemplateHash").GetString() == TemplateHash(body)
                && root.GetProperty("OfferedCtc").GetDecimal() == ctc
                && root.GetProperty("Currency").GetString() == currency
                && root.GetProperty("ProposedJoiningDate").GetDateTime().Date == joiningDate.Date
                && root.GetProperty("OfferTemplateId").GetInt64() == templateId;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Payroll.API.Assets.OfferLetters.{name}") ?? throw new InvalidOperationException("Offer letter asset is missing.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    private sealed class OfferFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "NotoSerif-Bold" : "NotoSerif-Regular");
        public byte[] GetFont(string faceName) => Resource(faceName + ".ttf");
    }
}
