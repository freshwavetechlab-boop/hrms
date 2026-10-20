namespace Payroll.API.Models;

public static class RecruitmentDocumentTextQuality
{
    // Layout whitespace is legitimate; binary/control noise in extracted prose is
    // not usable evidence in any language. Do not replace it with role defaults.
    public static bool IsReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var bad = text.Count(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t' or '\f'));
        return bad < 3 && bad * 100 <= text.Length;
    }
}
