namespace Payroll.API.Models;

// Opt-in, data-only document layout. It contains no executable HTML, scripts,
// filesystem paths, remote assets or client-specific rules.
public sealed class RecruitmentDocumentLayout
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "Offer letter";
    public double PageWidth { get; set; } = 595.276;
    public double PageHeight { get; set; } = 841.89;
    public List<DocumentInput> Inputs { get; set; } = [];
    public List<DocumentImportPreset> ImportPresets { get; set; } = [];
    public List<DocumentCalculation> Calculations { get; set; } = [];
    public List<DocumentCheck> Checks { get; set; } = [];
    public List<DocumentElement> Header { get; set; } = [];
    public List<DocumentElement> Footer { get; set; } = [];
    public List<DocumentSection> Sections { get; set; } = [];
}
public sealed class DocumentImportPreset
{
    public string Name { get; set; } = "";
    public int HeaderRow { get; set; } = 1;
    public Dictionary<string, string> Defaults { get; set; } = [];
}
public sealed class DocumentInput
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public List<string> SourceHeaders { get; set; } = [];
    public List<string> EmptyValues { get; set; } = [];
    public string DefaultValue { get; set; } = "";
    public string BlankText { get; set; } = "";
}
public sealed class DocumentCalculation
{
    public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
}
public sealed class DocumentCheck
{
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
    public decimal Tolerance { get; set; } = .01m;
    public string Message { get; set; } = "Review the source amounts.";
}
public sealed class DocumentSection
{
    public bool Flow { get; set; }
    public int Pages { get; set; } = 1;
    public double Left { get; set; } = 65.1;
    public double Top { get; set; } = 117.7;
    public double Width { get; set; } = 465.6;
    public double Bottom { get; set; } = 755;
    public List<DocumentBlock> Blocks { get; set; } = [];
    public List<DocumentElement> Elements { get; set; } = [];
}
public class DocumentText
{
    public string Text { get; set; } = "";
    public List<DocumentRun> Runs { get; set; } = [];
    public double FontSize { get; set; } = 10.5;
    public double LineHeight { get; set; } = 13.2;
    public string FontFamily { get; set; } = "serif";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string Align { get; set; } = "left";
}
public sealed class DocumentRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
}
public sealed class DocumentBlock : DocumentText
{
    public string Kind { get; set; } = "paragraph";
    public double Height { get; set; }
    public double Indent { get; set; }
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; } = 4;
    public string Bullet { get; set; } = "";
    public List<double> ColumnWidths { get; set; } = [];
    public List<List<DocumentCell>> Rows { get; set; } = [];
    public double PaddingX { get; set; } = 4.25;
    public double PaddingY { get; set; } = .5;
    public double StrokeWidth { get; set; } = .6;
}
public sealed class DocumentCell : DocumentText { }
public sealed class DocumentElement : DocumentText
{
    public string Kind { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double StrokeWidth { get; set; } = .5;
    public string Asset { get; set; } = "";
    public double MinFontSize { get; set; }
}
