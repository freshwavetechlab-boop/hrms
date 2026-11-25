using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    public async Task<PayslipDesignPreview> CopyPreviewAsync(PayslipDesignCopyPreviewRequest request, AuthUser actor)
    {
        if (!CanManage(actor, request.ClientId) || !CanManage(actor, request.SourceClientId)) throw new UnauthorizedAccessException();
        if (request.SourceTemplateId <= 0 || string.IsNullOrWhiteSpace(request.SourceProfileHash))
            throw new InvalidOperationException("Choose a saved payslip design to copy.");
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id AND IsActive=TRUE)", new { Id = request.ClientId }, tx))
            throw new InvalidOperationException("The selected client is unavailable.");
        await RequireClientSalaryTemplateAsync(db, tx, request.ClientId);
        var sourceTemplate = await db.QuerySingleOrDefaultAsync<PayslipDesignTemplate>(
            "SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=@ClientId", new { Id = request.SourceTemplateId, ClientId = request.SourceClientId }, tx)
            ?? throw new InvalidOperationException("The source template is unavailable for this client.");
        var sourceProfile = await ReadProfileAsync(db, request.SourceClientId, request.SourceTemplateId, tx)
            ?? throw new InvalidOperationException("Save the original payslip design before copying it.");
        NativePayslipDesignService.ValidateProfile(sourceProfile, sourceTemplate);
        if (sourceProfile.RequestHash != request.SourceProfileHash)
            throw new InvalidOperationException("The source design changed. Reopen Create similar to copy its current settings.");
        var fields = sourceProfile.Fields.Select(field => new PayslipDesignField { SourceColumn = field.SourceColumn,
            Label = field.Label, Kind = field.Kind, NativeCode = field.NativeCode, InfoToken = field.InfoToken, Visible = field.Visible }).ToList();
        var codes = await ClientSalaryCodesAsync(db, tx, request.ClientId);
        var tokens = NativePayslipMonthlyComponentInformation.CatalogueTokens(codes,
            configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag));
        var result = new PayslipDesignPreview(request.ClientId, "", CorePayrollPayslipDesign.SchemaHash(request.ClientId,
            fields, sourceProfile.SimpleLayout, sourceProfile.KeepMappedZeroRows), sourceProfile.SimpleLayout, fields, codes, tokens)
        { SourceKind = CorePayrollPayslipDesign.SourceKind, KeepMappedZeroRows = sourceProfile.KeepMappedZeroRows };
        await tx.CommitAsync(); return result;
    }

    public async Task<PayslipDesignPreview> CorePreviewAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var codes = await ClientSalaryCodesAsync(db, tx, clientId);
        var templateCodes = await ClientSalaryCodesAsync(db, tx, clientId, templateOnly: true);
        var fields = new List<PayslipDesignField>();
        foreach (var (label, token) in new[] { ("Designation", "DESIGNATION"), ("Work Location", "WORK_LOCATION"),
            ("Payroll days", "PAYROLL_DAYS"), ("Payable days", "PAYABLE_DAYS"), ("Bank Account", "BANK_ACCOUNT"), ("IFSC", "IFSC") })
            fields.Add(new() { SourceColumn = fields.Count, Label = label, Kind = "info", InfoToken = token });
        foreach (var code in templateCodes.Where(code => NativePayslipDesignService.LineKind(code.Category) != ""))
            fields.Add(new() { SourceColumn = fields.Count, Label = code.Name, Kind = NativePayslipDesignService.LineKind(code.Category), NativeCode = code.Code });
        var hash = CorePayrollPayslipDesign.SchemaHash(clientId, fields, false, true);
        var tokens = NativePayslipMonthlyComponentInformation.CatalogueTokens(codes,
            configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag));
        var result = new PayslipDesignPreview(clientId, "", hash, false, fields, codes, tokens)
            { SourceKind = CorePayrollPayslipDesign.SourceKind, KeepMappedZeroRows = true };
        await tx.CommitAsync(); return result;
    }

    private async Task<(ExcelPayslipBatch Batch, List<PayslipDesignField> Fields, string Hash)> DesignMetadataAsync(
        MySqlConnection db, MySqlTransaction tx, PayslipDesignSaveRequest request, bool locked = false)
    {
        if (request.SourceKind is null or "ExcelSource")
            return await MetadataAsync(db, tx, await SourceAsync(db, request.Template.ClientId, request.SourceBatchId, tx, locked), request.Template.ClientId);
        if (request.SourceKind != CorePayrollPayslipDesign.SourceKind || request.SourceBatchId != ""
            || !request.SimpleLayout.HasValue || !request.KeepMappedZeroRows.HasValue)
            throw new InvalidOperationException("Choose a supported payslip design source and complete its layout settings.");
        var hash = CorePayrollPayslipDesign.SchemaHash(request.Template.ClientId, request.Fields,
            request.SimpleLayout.Value, request.KeepMappedZeroRows.Value);
        var clientName = await db.ExecuteScalarAsync<string?>("SELECT Name FROM clients WHERE Id=@Id AND IsActive=TRUE",
            new { Id = request.Template.ClientId }, tx) ?? throw new InvalidOperationException("The selected client is unavailable.");
        return (new ExcelPayslipBatch { ClientId = request.Template.ClientId, ClientName = clientName,
            Month = "Sample month", SimpleLayout = request.SimpleLayout.Value }, request.Fields, hash);
    }
}
