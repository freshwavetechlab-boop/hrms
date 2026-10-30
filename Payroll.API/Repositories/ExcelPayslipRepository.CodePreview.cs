using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal sealed record CodePreviewMaster(string EmployeeCode, bool IsActive);
    internal static string[] ValidateCodePreview(ExcelPayslipCodePreviewRequest request)
    {
        if (request.Codes is null || request.Codes.Count > 1000 || request.Codes.Any(code => code is null || code.Length > 80 || code.Any(char.IsControl)))
            throw new InvalidOperationException("Preview at most 1,000 employee codes of up to 80 characters.");
        return request.Codes.Select(ExcelPayslipVarianceService.NormalizeText).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static ExcelPayslipCodePreview BuildCodePreview(ExcelPayslipCodePreviewRequest request, IEnumerable<CodePreviewMaster> masters)
    {
        var codes = ValidateCodePreview(request).ToHashSet(StringComparer.Ordinal);
        var matches = masters.Where(master => codes.Contains(ExcelPayslipVarianceService.NormalizeText(master.EmployeeCode)))
            .GroupBy(master => ExcelPayslipVarianceService.NormalizeText(master.EmployeeCode), StringComparer.Ordinal)
            .Select(group => new ExcelPayslipCodeMatch(group.Key, group.Count(), group.Count(master => master.IsActive))).ToList();
        return new(matches, codes.Count);
    }

    // Codes only, scoped to this client. This preview neither resolves identity nor writes Master/source data.
    public async Task<ExcelPayslipCodePreview> PreviewEmployeeCodesAsync(int clientId, ExcelPayslipCodePreviewRequest request)
    {
        var codes = ValidateCodePreview(request);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        if (!await SupportedAsync(db, clientId, tx)) throw new InvalidOperationException("Select an active client.");
        var masters = codes.Length == 0 ? [] : (await db.QueryAsync<CodePreviewMaster>(
            "SELECT EmployeeCode,IsActive FROM employees WHERE ClientId=@ClientId", new { ClientId = clientId }, tx)).ToArray();
        var preview = BuildCodePreview(new(codes.ToList()), masters);
        await tx.RollbackAsync(); return preview;
    }
}
