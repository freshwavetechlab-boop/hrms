using System.Text.Json;
using MimeKit;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipEmailRequestTests
{
    private static ExcelPayslipBatch Batch() => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 51, ClientName = "Test & Client", Month = "2026-09",
        Rows = [new() { Id = "row-1", EmployeeCode = "TEST001", EmployeeName = "Person <One>", Email = "", NetPay = 17160,
            Information = [new() { Label = "Location Name (Tehsil/Sub Tehsil)", Value = "Site <02>" }, new() { Label = "UAN", Value = "private-uan" }],
            Earnings = [new() { Label = "Private earning", Amount = 20000 }], Warnings = ["Review salary amounts"] },
            new() { Id = "row-2", EmployeeName = "Person Two", Email = "person@example.test" }]
    };

    private static NotificationTemplate Template() => new()
    {
        Code = NotificationRepository.ExcelPayslipEmailRequestTemplateCode, IsHtml = true, IsActive = true,
        SubjectTemplate = "Email IDs | {{clientName}} | {{monthLabel}}",
        BodyTemplate = "<p>Dear Sir, kindly provide email IDs for {{employeeCount}} employee(s).</p>{{employeeTable}}"
    };

    [Fact]
    public void RequestContainsEscapedIdentityOnlyAndDoesNotChangeSavedBatch()
    {
        var batch = Batch(); var before = JsonSerializer.Serialize(batch);
        var rows = ExcelPayslipRepository.SelectEmailRequestRows(batch, ["row-1"]);
        var content = NotificationRepository.PrepareExcelPayslipEmailRequest(batch, rows, "recipient@example.test", "operator", Template());
        Assert.Equal("Email IDs | Test & Client | September 2026", content.Subject);
        Assert.Contains("Person &lt;One&gt;", content.BodyHtml); Assert.Contains("Site &lt;02&gt;", content.BodyHtml); Assert.Contains("TEST001", content.BodyHtml);
        Assert.DoesNotContain("17160", content.BodyHtml); Assert.DoesNotContain("20000", content.BodyHtml);
        Assert.DoesNotContain("Private earning", content.BodyHtml); Assert.DoesNotContain("private-uan", content.BodyHtml);
        Assert.DoesNotContain("Person Two", content.BodyHtml); Assert.DoesNotContain("{{employeeTable}}", content.BodyHtml);
        Assert.Equal(before, JsonSerializer.Serialize(batch));
    }

    [Theory]
    [InlineData("known-email")]
    [InlineData("other-batch")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void OnlyDistinctMissingEmailRowsFromThisBatchAreAllowed(string selection)
    {
        var ids = selection switch { "known-email" => new List<string> { "row-2" }, "other-batch" => ["row-3"], "duplicate" => ["row-1", "row-1"], _ => [] };
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.SelectEmailRequestRows(Batch(), ids));
    }

    [Fact]
    public void RequestFingerprintIsStableAcrossOrderingButChangesWithRecipientOrSelection()
    {
        var rows = Batch().Rows;
        var fingerprint = ExcelPayslipRepository.EmailRequestFingerprint("Recipient@example.test", rows);
        Assert.Equal(32, fingerprint.Length);
        Assert.True((Batch().Id + ":" + Guid.NewGuid().ToString("N") + ":" + fingerprint).Length <= 120);
        Assert.Equal(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows.AsEnumerable().Reverse().ToArray()));
        Assert.NotEqual(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("another@example.test", rows));
        Assert.NotEqual(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", [rows[0]]));
    }

    [Fact]
    public void RequestTemplateMustIncludeEmployeeListAndInlineLogosNeedNoPayslipAttachment()
    {
        var batch = Batch(); var template = Template();
        template.BodyTemplate = "<p>Dear Sir</p>";
        Assert.Throws<InvalidOperationException>(() => NotificationRepository.PrepareExcelPayslipEmailRequest(batch, [batch.Rows[0]], "recipient@example.test", "operator", template));
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQWQAAAAASUVORK5CYII=";
        template.BodyTemplate += "{{employeeTable}}<img src='data:image/png;base64," + png + "'>";
        var content = NotificationRepository.PrepareExcelPayslipEmailRequest(batch, [batch.Rows[0]], "recipient@example.test", "operator", template);
        var builder = new BodyBuilder { HtmlBody = content.BodyHtml };
        NotificationRepository.EmbedExcelPayslipImages(builder);
        Assert.Single(builder.LinkedResources); Assert.Empty(builder.Attachments);
        Assert.DoesNotContain("data:image", builder.HtmlBody); Assert.Contains("cid:", builder.HtmlBody);
    }

    [Fact]
    public void TextTemplatePreservesEmployeeIdentityAndEscapesMarkup()
    {
        var batch = Batch(); var template = Template(); template.IsHtml = false;
        template.BodyTemplate = "Dear Sir\n{{employeeTable}}";
        var content = NotificationRepository.PrepareExcelPayslipEmailRequest(batch, [batch.Rows[0]], "recipient@example.test", "operator", template);
        Assert.Contains("Dear Sir<br />TEST001 | Person &lt;One&gt; | Site &lt;02&gt;", content.BodyHtml);
    }

    [Fact]
    public void SalaryAttachmentIsOptInAndRequiresFinancialWarningAcknowledgement()
    {
        var request = new ExcelPayslipEmailRequest();
        Assert.False(request.AttachPayslips); Assert.False(request.IncludeSeal);
        request.AttachPayslips = true;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.SelectEmailRequestRows(Batch(), ["row-1"], request));
        request.AcknowledgeWarnings = true;
        Assert.Single(ExcelPayslipRepository.SelectEmailRequestRows(Batch(), ["row-1"], request));
        request.AmountDecimalPlaces = 3;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.SelectEmailRequestRows(Batch(), ["row-1"], request));
    }

    [Fact]
    public void AttachmentOptionsAndSnapshotArePartOfRetryIdentityButLegacyRequestsKeepTheirFingerprint()
    {
        var rows = Batch().Rows;
        var before = ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows);
        Assert.Equal(before, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows, new()));
        var request = new ExcelPayslipEmailRequest { AttachPayslips = true, IncludeSeal = false, AmountDecimalPlaces = 0 };
        var fingerprint = ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows, request, "original");
        Assert.NotEqual(before, fingerprint);
        request.IncludeSeal = true; Assert.NotEqual(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows, request, "original"));
        request.IncludeSeal = false; request.AmountDecimalPlaces = 2; Assert.NotEqual(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows, request, "original"));
        request.AmountDecimalPlaces = 0; Assert.NotEqual(fingerprint, ExcelPayslipRepository.EmailRequestFingerprint("recipient@example.test", rows, request, "changed"));
    }

    [Fact]
    public void RequestAttachesExactlyOneCombinedPdfForTwoMissingEmailEmployeesWithoutMarkingPersonalDeliverySent()
    {
        var batch = Batch();
        batch.Rows.Add(new() { Id = "row-3", SourceRow = 3, EmployeeName = "Another missing email", Earnings = [new() { Label = "Basic", Amount = 10000 }], NetPay = 10000 });
        var before = JsonSerializer.Serialize(batch);
        var (document, queue) = Attachment(batch);
        var builder = new BodyBuilder { HtmlBody = document.BodyHtml };
        NotificationRepository.AttachExcelPayslipEmailRequestPdf(builder, queue, document, batch);
        var attachment = Assert.IsType<MimePart>(Assert.Single(builder.Attachments));
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        using var bytes = new MemoryStream(); attachment.Content.DecodeTo(bytes);
        using var pdf = PdfReader.Open(new MemoryStream(bytes.ToArray()), PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount); Assert.DoesNotContain("row-2", document.RowIds);
        Assert.Contains("{{employeeTable}}", Template().BodyTemplate); Assert.DoesNotContain("{{employeeTable}}", builder.HtmlBody);
        Assert.DoesNotContain("Private earning", builder.HtmlBody); Assert.Equal(before, JsonSerializer.Serialize(batch));
        var state = new ExcelPayslipSendState();
        state.Requests[ExcelPayslipRepository.EmailRequestAttachmentKey(document.Id)] = JsonSerializer.SerializeToElement(document, ExcelPayslipRepository.JsonOptions);
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, []);
        Assert.All(statuses.Items, item => { Assert.Equal("Pending", item.Status); Assert.True(item.CanSend); });
        Assert.DoesNotContain("pdfBase64", JsonSerializer.Serialize(state));
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("client")]
    [InlineData("selection")]
    [InlineData("snapshot")]
    [InlineData("queue")]
    [InlineData("resource")]
    [InlineData("cc")]
    [InlineData("employment")]
    public void QueuedRequestPdfRejectsMismatchedRecipientsSourceAndOptions(string change)
    {
        var batch = Batch(); var (document, queue) = Attachment(batch);
        switch (change)
        {
            case "recipient": queue.ToJson = "[\"other@example.test\"]"; break;
            case "client": queue.ClientId++; break;
            case "selection": document.RowIds = ["row-2"]; break;
            case "snapshot": batch.Rows[0].NetPay++; break;
            case "queue": document.QueueId++; break;
            case "resource": document.IncludeSeal = true; break;
            case "cc": queue.CcJson = "[\"other@example.test\"]"; break;
            case "employment": batch.Rows[0].Employment = new(10, "TEST001", "Resigned", "2026-08-31", true, false); break;
        }
        var builder = new BodyBuilder();
        Assert.Throws<InvalidOperationException>(() => NotificationRepository.AttachExcelPayslipEmailRequestPdf(builder, queue, document, batch));
        Assert.Empty(builder.Attachments);
    }

    private static (NotificationRepository.ExcelPayslipMailDocument Document, NotificationQueueItem Queue) Attachment(ExcelPayslipBatch batch)
    {
        var request = new ExcelPayslipEmailRequest { AttachPayslips = true, IncludeSeal = false, AmountDecimalPlaces = 0, AcknowledgeWarnings = true,
            RequestId = Guid.NewGuid().ToString("N"), RowIds = batch.Rows.Where(row => !row.Email.Contains('@')).Select(row => row.Id).ToList() };
        var rows = ExcelPayslipRepository.SelectEmailRequestRows(batch, request.RowIds, request);
        var document = NotificationRepository.PrepareExcelPayslipEmailRequestAttachment(batch, rows, "recipient@example.test", "operator", Template(), request);
        document.QueueId = 123; document.Id = request.RequestId;
        return (document, new() { Id = 123, ClientId = batch.ClientId, EventCode = NotificationRepository.ExcelPayslipEmailRequestPdfEvent,
            ResourceType = "ExcelPayslipEmailRequest", ResourceId = batch.Id + ":" + document.Id + ":" + ExcelPayslipRepository.EmailRequestFingerprint(document.Email, rows, request, document.BatchSha256),
            ToJson = "[\"recipient@example.test\"]", CcJson = "[]", BccJson = "[]" });
    }
}
