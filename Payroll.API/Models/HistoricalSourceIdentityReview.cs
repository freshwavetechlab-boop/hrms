namespace Payroll.API.Models;

// Explicit source ownership review for a closed salary period. This does not
// change an employee, bank account, government identifier or source snapshot.
public sealed class HistoricalSourceIdentityReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public string SourceHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<HistoricalSourceIdentityBinding> Bindings { get; set; } = [];
}

public sealed record HistoricalSourceIdentityBinding(string RowId, int EmployeeId, int BankColumnIndex,
    string ReviewedSourceName, string ReviewedMasterName, bool ReviewedNameChange);

public sealed record HistoricalSourceIdentityEvidence(int ClientId, int EmployeeId, string RowId, int SourceRow,
    int BankColumnIndex, string SourceHash, string SourceCellHash, string BankOwnershipHash,
    string ReviewedNamesHash, bool ReviewedNameChange, string Reason);
