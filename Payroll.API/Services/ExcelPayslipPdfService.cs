using System.Globalization;
using System.Reflection;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Payroll.API.Models;

namespace Payroll.API.Services;

/// <summary>Renders only the supplied Excel snapshot. It never loads or calculates payroll.</summary>
public sealed class ExcelPayslipPdfService
{
    private const double Left = 28, Top = 30, Width = 539, BodyTop = 128, Bottom = 800;
    private static readonly object FontLock = new();
    private static readonly CultureInfo AmountCulture = CultureInfo.GetCultureInfo("en-IN");
    private static readonly XBrush Pink = new XSolidBrush(XColor.FromArgb(249, 225, 228));
    private static readonly XBrush Pale = new XSolidBrush(XColor.FromArgb(248, 248, 248));
    private static readonly XPen Border = new(XColor.FromArgb(125, 125, 125), .5);
    public static string SealPath(string? runtimeDirectory = null) => Path.Combine(runtimeDirectory ?? AppContext.BaseDirectory,
        "PrivateAssets", "ExcelPayslips", "PLRS", "GADSignatureSeal.png");

    public void ValidateLayout(ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipRow> rows)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidOperationException("Select at least one Excel payslip row.");
        EnsureFonts();
        using var measure = XGraphics.CreateMeasureContext(new XSize(595.28, 841.89), XGraphicsUnit.Point, XPageDirection.Downwards);
        foreach (var row in rows) MakePlan(measure, batch, row);
    }

    public byte[] Create(ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipRow> rows, bool includeSeal = true, int amountDecimalPlaces = 0)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(rows);
        ValidateAmountDecimalPlaces(amountDecimalPlaces);
        if (rows.Count == 0) throw new InvalidOperationException("Select at least one Excel payslip row.");
        EnsureFonts();
        using var logoStream = new MemoryStream(Resource("PrivateAssets.ExcelPayslips.GADLogo.png"));
        using var logo = XImage.FromStream(logoStream);
        using var sealStream = includeSeal ? OpenSeal() : null;
        using var seal = sealStream is null ? null : XImage.FromStream(sealStream);
        using var document = new PdfDocument();
        document.Info.Title = $"Salary slips - {MonthLabel(batch.Month)}";
        document.Info.Author = "GA DIGITAL WEB WORD (P) LTD";
        foreach (var row in rows)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            DrawPage(gfx, batch, row, logo, seal, amountDecimalPlaces);
        }
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static MemoryStream OpenSeal()
    {
        var path = SealPath();
        if (!File.Exists(path)) throw new InvalidOperationException("The supplied Excel payslip signature and seal is missing from this deployment.");
        return new MemoryStream(File.ReadAllBytes(path));
    }

    private static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is not null) return;
            try { GlobalFontSettings.FontResolver = new ExcelFontResolver(); }
            catch (InvalidOperationException) when (GlobalFontSettings.FontResolver is not null) { }
        }
    }

    // Same face names and bytes as the existing offer resolver, including OfferSerif.
    // Whichever renderer initializes first leaves the other's font output unchanged.
    private sealed class ExcelFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "NotoSerif-Bold" : "NotoSerif-Regular");
        public byte[] GetFont(string faceName) => Resource("Assets.OfferLetters." + faceName + ".ttf");
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(ExcelPayslipPdfService).Assembly.GetManifestResourceStream("Payroll.API." + name)
            ?? throw new InvalidOperationException("An Excel payslip branding asset is missing from this deployment.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    internal sealed record SlipTotals(decimal Gross, decimal Deductions, decimal Net);
    internal static SlipTotals TotalsFor(ExcelPayslipRow row) => new(row.DeclaredGross ?? row.Earnings.Sum(x => x.Amount),
        row.DeclaredDeductions ?? row.Deductions.Sum(x => x.Amount), row.NetPay);
    internal static string Amount(decimal value, int amountDecimalPlaces = 2) => DisplayAmount(value, amountDecimalPlaces).ToString($"N{amountDecimalPlaces}", AmountCulture);
    private static decimal DisplayAmount(decimal value, int amountDecimalPlaces)
    {
        ValidateAmountDecimalPlaces(amountDecimalPlaces);
        // Excel displays numeric cells using 15 significant digits. Cached binary
        // tails (e.g. 14129.499999999998) must not move the printed rupee below .5.
        // This value is local to formatting; the snapshot and arithmetic retain value.
        var displayedPrecision = decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        var rounded = decimal.Round(displayedPrecision, amountDecimalPlaces, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
    private static void ValidateAmountDecimalPlaces(int amountDecimalPlaces)
    {
        if (amountDecimalPlaces is not (0 or 2)) throw new InvalidOperationException("Choose whole rupees or two decimal places for the payslip display.");
    }
    private static XFont Font(double size, bool bold = false) => new("OfferSerif", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
    private static string MonthLabel(string month) => DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : month;

    private sealed record Cell(string Label, string Value);
    private sealed record Plan(double Size, IReadOnlyList<Cell> Information, double NameHeight, double[] InformationHeights,
        double[] MoneyHeights, double WordsHeight, double[] EmployerHeights, double TotalHeight);

    private static Plan MakePlan(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, int amountDecimalPlaces = 2)
    {
        var info = new List<Cell>();
        if (!string.IsNullOrWhiteSpace(row.EmployeeCode)) info.Add(new("Employee Code", row.EmployeeCode));
        if (!string.IsNullOrWhiteSpace(batch.ClientName) && !row.Information.Any(x => x.Label.Contains("client", StringComparison.OrdinalIgnoreCase)))
            info.Add(new("Client", batch.ClientName));
        info.AddRange(row.Information.Where(x => !string.IsNullOrWhiteSpace(x.Label)
            && !x.Label.Equals("Employee Name", StringComparison.OrdinalIgnoreCase)
            && !(!string.IsNullOrWhiteSpace(row.EmployeeCode) && x.Label.Equals("Employee Code", StringComparison.OrdinalIgnoreCase)))
            .Select(x => new Cell(x.Label, x.Value ?? "")));
        for (var size = 9d; size >= 6.5; size -= .5)
        {
            var normal = Font(size); var bold = Font(size, true);
            double Height(string text, XFont font, double width) => Math.Max(1, Wrap(gfx, text, font, width).Count) * size * 1.36 + 10;
            var name = Math.Max(24, Height(row.EmployeeName, bold, Width - 108));
            var information = Enumerable.Range(0, (info.Count + 1) / 2).Select(i =>
            {
                double CellHeight(Cell cell) => Math.Max(Height(cell.Label, normal, 79), Height(cell.Value, normal, Width / 2 - 99));
                return Math.Max(CellHeight(info[i * 2]), i * 2 + 1 < info.Count ? CellHeight(info[i * 2 + 1]) : 0);
            }).ToArray();
            var money = Enumerable.Range(0, Math.Max(row.Earnings.Count, row.Deductions.Count)).Select(i =>
                Math.Max(24, Math.Max(i < row.Earnings.Count ? Height(row.Earnings[i].Label, normal, Width / 2 - 98) : 0,
                    i < row.Deductions.Count ? Height(row.Deductions[i].Label, normal, Width / 2 - 98) : 0))).ToArray();
            var words = Math.Max(30, Height(NetWords(row.NetPay, amountDecimalPlaces), normal, Width - 18));
            var employer = row.EmployerContributions.Select(x => Math.Max(23, Height(x.Label, normal, Width / 2 - 98))).ToArray();
            var total = name + information.Sum() + 12 + 25 + money.Sum() + 25 + 30 + words + 15 + Math.Max(145, 25 + employer.Sum());
            if (BodyTop + total <= Bottom) return new(size, info, name, information, money, words, employer, total);
        }
        throw new InvalidOperationException($"Excel row {row.SourceRow} has too much text to fit legibly on one payslip page. Shorten mapped labels or reduce the information fields.");
    }

    private static void DrawPage(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, XImage logo, XImage? seal, int amountDecimalPlaces)
    {
        var plan = MakePlan(gfx, batch, row, amountDecimalPlaces);
        var normal = Font(plan.Size); var bold = Font(plan.Size, true);
        gfx.DrawRectangle(Border, Left, Top, Width, 69);
        Image(gfx, logo, Left + 10, Top + 8, 67, 50);
        Text(gfx, "GA DIGITAL WEB WORD (P) LTD", Font(16, true), Left + 85, Top + 12, Width - 98, 22, centered: true);
        Text(gfx, "NO.1, HARGOBIND ENCLAVE, VIKAS MARG EXTN., DELHI-110092", Font(8), Left + 84, Top + 39, Width - 96, 20, centered: true);
        gfx.DrawRectangle(Border, Pink, Left, 99, Width, 25);
        Text(gfx, $"SALARY SLIP - {MonthLabel(batch.Month)}", Font(11, true), Left + 6, 104, Width - 12, 17, centered: true);
        var y = BodyTop;
        gfx.DrawRectangle(Border, Left, y, Width, plan.NameHeight);
        Text(gfx, "Employee Name", bold, Left + 7, y + 5, 91, plan.NameHeight - 10);
        Text(gfx, row.EmployeeName, bold, Left + 101, y + 5, Width - 108, plan.NameHeight - 10);
        y += plan.NameHeight;
        for (var i = 0; i < plan.InformationHeights.Length; i++)
        {
            var height = plan.InformationHeights[i];
            for (var side = 0; side < 2; side++)
            {
                var x = Left + side * Width / 2;
                gfx.DrawRectangle(Border, x, y, Width / 2, height);
                var index = i * 2 + side;
                if (index >= plan.Information.Count) continue;
                var cell = plan.Information[index];
                Text(gfx, cell.Label, normal, x + 7, y + 5, 79, height - 10);
                Text(gfx, cell.Value, normal, x + 92, y + 5, Width / 2 - 99, height - 10);
            }
            y += height;
        }
        y += 12;
        MoneyHeader("Earnings", Left, y); MoneyHeader("Deductions", Left + Width / 2, y); y += 25;
        for (var i = 0; i < plan.MoneyHeights.Length; i++)
        {
            MoneyRow(i < row.Earnings.Count ? row.Earnings[i] : null, Left, y, plan.MoneyHeights[i]);
            MoneyRow(i < row.Deductions.Count ? row.Deductions[i] : null, Left + Width / 2, y, plan.MoneyHeights[i]);
            y += plan.MoneyHeights[i];
        }
        var totals = TotalsFor(row);
        MoneyRow(new ExcelPayslipAmount { Label = "Total Earnings", Amount = totals.Gross }, Left, y, 25, true);
        MoneyRow(new ExcelPayslipAmount { Label = "Total Deductions", Amount = totals.Deductions }, Left + Width / 2, y, 25, true);
        y += 25;
        gfx.DrawRectangle(Border, Pink, Left, y, Width, 30);
        Text(gfx, "IN HAND (Rs.)", Font(10, true), Left + 8, y + 7, Width - 170, 18);
        Money(gfx, totals.Net, Font(11, true), Left + Width - 158, y + 6, 150, 20, amountDecimalPlaces);
        y += 30;
        gfx.DrawRectangle(Border, Left, y, Width, plan.WordsHeight);
        Text(gfx, NetWords(totals.Net, amountDecimalPlaces), normal, Left + 9, y + 5, Width - 18, plan.WordsHeight - 10);
        y += plan.WordsHeight + 15;
        if (row.EmployerContributions.Count > 0)
        {
            MoneyHeader("Employer Contributions", Left, y);
            var employerY = y + 25;
            for (var i = 0; i < row.EmployerContributions.Count; i++)
            {
                MoneyRow(row.EmployerContributions[i], Left, employerY, plan.EmployerHeights[i]);
                employerY += plan.EmployerHeights[i];
            }
        }
        var signatureX = Left + Width / 2 + 34;
        if (seal is not null) Image(gfx, seal, signatureX, y, Width / 2 - 68, 123);
        Text(gfx, "Employer Signature", normal, signatureX, y + 126, Width / 2 - 68, 17, centered: true);

        void MoneyHeader(string label, double x, double top)
        {
            gfx.DrawRectangle(Border, Pale, x, top, Width / 2, 25);
            Text(gfx, label, bold, x + 7, top + 6, Width / 2 - 97, 16);
            Text(gfx, "Amount (Rs.)", Font(Math.Min(plan.Size, 8)), x + Width / 2 - 87, top + 7, 80, 14, centered: true);
        }
        void MoneyRow(ExcelPayslipAmount? amount, double x, double top, double height, bool total = false)
        {
            if (total) gfx.DrawRectangle(Border, Pale, x, top, Width / 2, height);
            else gfx.DrawRectangle(Border, x, top, Width / 2, height);
            gfx.DrawLine(Border, x + Width / 2 - 91, top, x + Width / 2 - 91, top + height);
            if (amount is null) return;
            Text(gfx, amount.Label, total ? bold : normal, x + 7, top + 5, Width / 2 - 98, height - 10);
            Money(gfx, amount.Amount, total ? bold : normal, x + Width / 2 - 84, top + 5, 77, height - 10, amountDecimalPlaces);
        }
    }

    internal static string NetWords(decimal value, int amountDecimalPlaces = 2)
    {
        var displayed = DisplayAmount(value, amountDecimalPlaces);
        var absolute = Math.Abs(displayed);
        var words = absolute <= 999999999999m ? BrandedOfferPdfService.AmountInWords(absolute) : Amount(absolute, amountDecimalPlaces) + " only";
        return "Rupees " + (displayed < 0 ? "minus " : "") + words;
    }

    private static void Image(XGraphics gfx, XImage image, double x, double y, double width, double height)
    {
        var scale = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        var w = image.PixelWidth * scale; var h = image.PixelHeight * scale;
        gfx.DrawImage(image, x + (width - w) / 2, y + (height - h) / 2, w, h);
    }

    private static void Money(XGraphics gfx, decimal amount, XFont font, double x, double y, double width, double height, int amountDecimalPlaces)
    {
        var text = Amount(amount, amountDecimalPlaces);
        while (gfx.MeasureString(text, font).Width > width && font.Size > 4)
            font = Font(font.Size - .25, font.Bold);
        gfx.DrawString(text, font, XBrushes.Black, new XRect(x, y, width, height), XStringFormats.TopRight);
    }

    private static void Text(XGraphics gfx, string? text, XFont font, double x, double y, double width, double height, bool centered = false)
    {
        var lines = Wrap(gfx, text ?? "", font, width);
        var lineHeight = font.Size * 1.36;
        foreach (var line in lines)
        {
            if (lineHeight > height + .1) throw new InvalidOperationException("An Excel payslip label exceeds its available layout space.");
            gfx.DrawString(line, font, XBrushes.Black, new XRect(x, y, width, lineHeight), centered ? XStringFormats.TopCenter : XStringFormats.TopLeft);
            y += lineHeight; height -= lineHeight;
        }
    }

    internal static IReadOnlyList<string> Wrap(XGraphics gfx, string text, XFont font, double width)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace('\r', ' ').Replace('\t', ' ').Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (gfx.MeasureString(candidate, font).Width <= width) { line = candidate; continue; }
                if (line.Length > 0) { result.Add(line); line = ""; }
                foreach (var character in word)
                {
                    if (line.Length > 0 && gfx.MeasureString(line + character, font).Width > width) { result.Add(line); line = ""; }
                    line += character;
                }
            }
            result.Add(line);
        }
        return result.Count == 0 ? [""] : result;
    }
}
