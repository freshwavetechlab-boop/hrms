namespace Payroll.API.Models;

public class ClientFileUploadRequest
{
    public int ClientId { get; set; }
    public IFormFile? File { get; set; }
}

public class LeaveBalancePreviewUploadRequest : ClientFileUploadRequest
{
    public string Encoding { get; set; } = string.Empty;
    public string? MappingJson { get; set; }
}
