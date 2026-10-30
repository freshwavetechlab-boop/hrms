namespace Payroll.API.Models;

// Review belongs to a saved batch request/receipt; this is not a leave grant or a source-file import API.
public sealed class ExcelPayrollAttendanceReview
{
    public string Mode { get; set; } = ""; // SourceSeed or VerifyExistingNative
    public string SourceHash { get; set; } = "";
    public string SourceMonth { get; set; } = "";
    public string CycleStart { get; set; } = "";
    public string CycleEnd { get; set; } = "";
    public string PayableMeaning { get; set; } = ""; // AlreadyPayable; worked-only is intentionally unresolved.
    public string PresentMeaning { get; set; } = ""; // ActualWorked or NotMapped
    public int? LwpColumnIndex { get; set; }
    public string LwpBasis { get; set; } = ""; // PayrollDivisorMinusPayable only when source proves this meaning.
    public string BoundSourceInputs { get; set; } = "";
    public bool Reviewed { get; set; }
}
public sealed record ExcelPayrollAttendanceProof(string RowId,int SourceRow,string Mode,string Status,string Reason,string SourceHash,
    string InputHash,int PayrollDays,decimal? PresentDays,decimal PayableDays,decimal? SourceLwp,string? NativeHash,NativePayrollAttendanceInput? NativeInput);
public sealed record NativePayrollAttendanceInput(int EmployeeId,int ClientId,string Month,int? PayrollDays,decimal? PresentDays,decimal PayableDays,
    string? CycleStart,string? CycleEnd,string SnapshotHash,string Error);
