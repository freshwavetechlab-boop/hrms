namespace Payroll.API.Models;

public sealed class DocumentLayoutMigrationResult
{
    public int CreatedTemplates { get; set; }
    public int ReboundRows { get; set; }
    public int SkippedTemplates { get; set; }
    public int SkippedRows { get; set; }
    public List<DocumentLayoutMigrationItem> Items { get; set; } = [];
}

public sealed record DocumentLayoutMigrationItem(int SourceTemplateId, int TemplateId, int ClientId, bool Created, int ReboundRows);
