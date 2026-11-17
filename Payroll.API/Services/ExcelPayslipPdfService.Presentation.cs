namespace Payroll.API.Services;

// Opt-in presentation controls for the native register. Null retains every legacy Excel PDF default.
public sealed class ExcelPayslipPdfPresentationOptions
{
    // Native receipt-only accounting total. Never accepted from a source workbook or request.
    internal decimal? NativeEmployerTotal { get; init; }
    public bool ShowLogo { get; init; } = true;
    public bool ShowClient { get; init; } = true;
    public bool KeepMappedZeroRows { get; init; }
    public bool ExplicitSimpleInformation { get; init; }
    public string? Footer { get; init; }
}
