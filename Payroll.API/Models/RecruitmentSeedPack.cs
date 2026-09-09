namespace Payroll.API.Models;

public sealed class RecruitmentSeedPackImportResult
{
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Reused { get; set; }
    public int Deferred { get; set; }
    public int Failed { get; set; }
    public bool Success => Failed == 0;
    public List<RecruitmentSeedPackImportItem> Items { get; set; } = [];
}

public sealed class RecruitmentSeedPackImportItem
{
    public string Sheet { get; set; } = "";
    public int RowNumber { get; set; }
    public string SourceKey { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Message { get; set; } = "";
    public long? RequisitionId { get; set; }
    public long? JobDescriptionId { get; set; }
    public long? CandidateId { get; set; }
    public long? ApplicationId { get; set; }
    public decimal? AtsScore { get; set; }
}
