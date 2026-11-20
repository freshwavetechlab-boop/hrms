namespace Payroll.API.Models;

public sealed class RecruitmentJoiningSelection
{
    public int ClientId { get; set; }
    public List<long> OfferIds { get; set; } = [];
    public List<long> RosterRowIds { get; set; } = [];
    // Explicit Employee Master intake. Importing a released letter does not
    // attest candidate acceptance or create a candidate signature.
    public bool ImportReleasedForEss { get; set; }
}

public sealed class RecruitmentJoiningCandidate
{
    public string SourceType { get; set; } = "Offer";
    public long SourceId { get; set; }
    public string RowKey { get; set; } = "";
    public long? RosterRowId { get; set; }
    public long? ReleaseId { get; set; }
    public bool AcceptanceRecorded { get; set; }
    public long OfferId { get; set; }
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public string CandidateCode { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public int WorkLocationId { get; set; }
    public DateTime ProposedJoiningDate { get; set; }
    public decimal OfferedCtc { get; set; }
    public string Currency { get; set; } = "INR";
    public string OfferStatus { get; set; } = "Accepted";
    public int? EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string ImportStatus { get; set; } = "Ready";
    public string Issue { get; set; } = "";
}

public sealed class RecruitmentJoiningItemResult
{
    public string SourceType { get; set; } = "Offer";
    public long SourceId { get; set; }
    public string RowKey { get; set; } = "";
    public long? RosterRowId { get; set; }
    public long OfferId { get; set; }
    public long ApplicationId { get; set; }
    public string CandidateName { get; set; } = "";
    public string Status { get; set; } = "Review";
    public int? EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class RecruitmentJoiningBatchResult
{
    public List<RecruitmentJoiningItemResult> Items { get; set; } = [];
    public int Accepted => Items.Count(row => row.Status == "Accepted");
    public int AlreadyAccepted => Items.Count(row => row.Status == "Already accepted");
    public int Imported => Items.Count(row => row.Status == "Imported");
    public int AlreadyImported => Items.Count(row => row.Status == "Already imported");
    public int Review => Items.Count(row => row.Status == "Review");
}
