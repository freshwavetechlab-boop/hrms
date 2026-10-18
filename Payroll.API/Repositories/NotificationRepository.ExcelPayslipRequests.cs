using System.Text.Json;
using Dapper;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    internal static ExcelPayslipMailDocument PrepareExcelPayslipEmailRequestAttachment(ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> rows, string email, string actor, NotificationTemplate template, ExcelPayslipEmailRequest request)
    {
        if (!request.AttachPayslips) throw new InvalidOperationException("Select the salary PDF attachment option first.");
        var selected = ExcelPayslipRepository.SelectEmailRequestRows(batch, rows.Select(r => r.Id).ToList(), request);
        var document = PrepareExcelPayslipMailMetadata(batch, selected, email, request.RequestId, actor,
            request.IncludeSeal, request.AmountDecimalPlaces, template);
        var content = PrepareExcelPayslipEmailRequest(batch, selected, email, actor, template);
        document.Subject = content.Subject; document.BodyHtml = content.BodyHtml;
        return document;
    }

    private async Task AttachExcelPayslipEmailRequestPdfAsync(MySqlConnection db, NotificationQueueItem row, BodyBuilder builder)
    {
        var ids = row.ResourceId?.Split(':');
        if (row.ClientId is not int clientId || ids is not { Length: 3 } || !Guid.TryParseExact(ids[0], "N", out _)
            || !Guid.TryParseExact(ids[1], "N", out _)) throw new InvalidOperationException("The queued email request reference is invalid.");
        var stored = await db.QuerySingleOrDefaultAsync<ExcelPayslipMailSource>(@"SELECT CAST(batch_json AS CHAR) AS BatchJson,
CAST(send_state_json AS CHAR) AS SendStateJson FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId",
            new { ClientId = clientId, BatchId = ids[0] });
        var batch = stored is null ? null : JsonSerializer.Deserialize<ExcelPayslipBatch>(stored.BatchJson, ExcelPayslipRepository.JsonOptions);
        using var state = JsonDocument.Parse(stored?.SendStateJson ?? "{}");
        if (batch is null || !state.RootElement.TryGetProperty("requests", out var receipts)
            || !receipts.TryGetProperty(ExcelPayslipRepository.EmailRequestAttachmentKey(ids[1]), out var saved))
            throw new InvalidOperationException("The queued salary PDF attachment information is unavailable.");
        var document = saved.Deserialize<ExcelPayslipMailDocument>(ExcelPayslipRepository.JsonOptions)
            ?? throw new InvalidOperationException("The queued salary PDF attachment information is invalid.");
        var source = batch.SimpleLayout ? await ExcelPayslipRepository.ReadCalculationSourceAsync(db, clientId, batch.Id) : null;
        await ExcelPayslipRepository.AnnotateEmploymentAsync(db, batch);
        AttachExcelPayslipEmailRequestPdf(builder, row, document, batch, source);
    }

    internal static void AttachExcelPayslipEmailRequestPdf(BodyBuilder builder, NotificationQueueItem row,
        ExcelPayslipMailDocument document, ExcelPayslipBatch batch, ExcelPayslipCalculationSource? calculationSource = null)
    {
        var selected = ExcelPayslipRepository.SelectEmailRequestRows(batch, document.RowIds);
        var fingerprint = ExcelPayslipRepository.EmailRequestFingerprint(document.Email, selected,
            new() { AttachPayslips = true, IncludeSeal = document.IncludeSeal, AmountDecimalPlaces = document.AmountDecimalPlaces }, document.BatchSha256);
        var expected = batch.Id + ":" + document.Id + ":" + fingerprint;
        AttachVerifiedExcelPayslipPdf(builder, row, document, batch, calculationSource,
            ExcelPayslipEmailRequestPdfEvent, "ExcelPayslipEmailRequest", expected);
    }
}
