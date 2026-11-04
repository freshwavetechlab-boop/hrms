using PdfSharp.Fonts;

namespace Payroll.API.Services;

// Preserve the exact existing Noto faces for payslips and UIDAI. New layout
// families are opt-in and use the bundled OFL metric-compatible fonts.
public sealed class DocumentFontResolver : IFontResolver
{
    private static readonly object Gate = new();
    public static readonly DocumentFontResolver Shared = new();
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is not null) return;
            try { GlobalFontSettings.FontResolver = Shared; }
            catch (InvalidOperationException) when (GlobalFontSettings.FontResolver is not null) { }
        }
    }
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (familyName is "LayoutSerif" or "LayoutSans")
            return new((familyName == "LayoutSerif" ? "Caladea" : "Carlito") + "-" + (bold && italic ? "BoldItalic" : bold ? "Bold" : italic ? "Italic" : "Regular"));
        return new(bold ? "NotoSerif-Bold" : "NotoSerif-Regular");
    }
    public byte[] GetFont(string faceName)
    {
        var prefix = faceName.StartsWith("Caladea-", StringComparison.Ordinal) || faceName.StartsWith("Carlito-", StringComparison.Ordinal) ? "Layout." : "";
        using var source = typeof(DocumentFontResolver).Assembly.GetManifestResourceStream("Payroll.API.Assets.OfferLetters." + prefix + faceName + ".ttf")
            ?? throw new InvalidOperationException("A document font is missing from this deployment.");
        using var output = new MemoryStream(); source.CopyTo(output); return output.ToArray();
    }
}
