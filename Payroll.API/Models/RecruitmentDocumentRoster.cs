namespace Payroll.API.Models;

public sealed class RecruitmentRosterImport
{
    public int ClientId { get; set; }
    public int TemplateId { get; set; }
    public string SourceGroup { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int HeaderRow { get; set; } = 4;
    public Dictionary<string, int> Mapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<int> SourceRows { get; set; } = [];
    public string PreviewToken { get; set; } = "";
}
public sealed class RecruitmentRosterPreview
{
    public string FileName { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string PreviewToken { get; set; } = "";
    public List<string> Sheets { get; set; } = [];
    public string SheetName { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public Dictionary<string, int> Mapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DocumentInput> Fields { get; set; } = [];
    public List<RecruitmentRosterPreviewRow> Rows { get; set; } = [];
}
public sealed class RecruitmentRosterPreviewRow
{
    public int SourceRow { get; set; }
    public string CandidateName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string WorkLocation { get; set; } = "";
    public string MatchStatus { get; set; } = "New profile";
    public long? CandidateId { get; set; }
    public List<string> ImportIssues { get; set; } = [];
    public List<string> DocumentIssues { get; set; } = [];
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SourceValues { get; set; } = [];
}
public sealed class RecruitmentDocumentRosterRow
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long CandidateId { get; set; }
    public int TemplateId { get; set; }
    public string TemplateName { get; set; } = "";
    public string CandidateCode { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string SourceGroup { get; set; } = "";
    public string ProfileStatus { get; set; } = "";
    public string WorkLocation { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SourceSheetName { get; set; } = "";
    public int SourceRow { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DocumentIssues { get; set; } = [];
    public string Revision { get; set; } = "";
}
public sealed class RecruitmentRosterImportResult
{
    public int CreatedProfiles { get; set; }
    public int ReusedProfiles { get; set; }
    public int SavedRows { get; set; }
    public List<string> Errors { get; set; } = [];
}
public sealed record RecruitmentRosterEdit(Dictionary<string, string> Values, string Revision);
