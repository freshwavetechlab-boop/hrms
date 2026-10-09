using MimeKit;
using PdfSharp.Pdf.IO;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipEmailTemplateTests
{
    private static ExcelPayslipBatch Batch() => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 11, ClientName = "Example & Client", Month = "2026-09",
        Rows = [new() { Id = "row-1", SourceRow = 6, EmployeeCode = "EX001", EmployeeName = "Alex <Smith>", NetPay = 100 },
            new() { Id = "row-2", SourceRow = 7, EmployeeName = "Other Person", NetPay = 200 }]
    };

    private static NotificationRepository.ExcelPayslipMailDocument Prepare(ExcelPayslipBatch batch, NotificationTemplate template, bool combined = false) =>
        NotificationRepository.PrepareExcelPayslipMailMetadata(batch, combined ? batch.Rows : [batch.Rows[0]],
            "recipient@example.test", "request", "operator", false, 0, template);

    [Fact]
    public void SavedTemplateControlsSubjectAndEscapedIndividualFieldsAndFutureChangesLeaveQueuedContentIntact()
    {
        var batch = Batch(); var template = ExcelPayslipTests.MailTemplate;
        template.SubjectTemplate = "Salary slip | {{clientName}} | {{monthLabel}}";
        template.BodyTemplate = "<p>Dear {{recipientName}}, {{employeeCode}} | {{month}} | {{payslipCount}} | {{recipientEmail}}</p>";
        var first = Prepare(batch, template);
        Assert.Equal("Salary slip | Example & Client | September 2026", first.Subject);
        Assert.Equal("<p>Dear Alex &lt;Smith&gt;, EX001 | 2026-09 | 1 | recipient@example.test</p>", first.BodyHtml);
        template.BodyTemplate = "<p>Updated message for {{employeeName}}</p>";
        var next = Prepare(batch, template);
        Assert.Contains("Updated message", next.BodyHtml);
        Assert.DoesNotContain("Updated message", first.BodyHtml);
        Assert.Equal(first.BatchSha256, next.BatchSha256);
        Assert.DoesNotContain("<Smith>", next.BodyHtml);
    }

    [Fact]
    public void CombinedMessageHasTeamGreetingAndCountWithoutUsingOneEmployeesIdentity()
    {
        var batch = Batch(); var template = ExcelPayslipTests.MailTemplate;
        template.BodyTemplate = "<p>Dear {{recipientName}}: {{payslipCount}} slips for {{monthLabel}}.</p>";
        var document = Prepare(batch, template, true);
        Assert.Equal("<p>Dear Payroll Team: 2 slips for September 2026.</p>", document.BodyHtml);
        Assert.DoesNotContain("Alex", document.BodyHtml);
        Assert.Equal(2, document.RowIds.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("subject")]
    [InlineData("body")]
    public void MissingOrIncompleteConfigurationStopsPreparationWithoutHardcodedFallback(string change)
    {
        var template = ExcelPayslipTests.MailTemplate;
        if (change == "inactive") template.IsActive = false;
        if (change == "subject") template.SubjectTemplate = " ";
        if (change == "body") template.BodyTemplate = " ";
        var error = Assert.Throws<InvalidOperationException>(() => Prepare(Batch(), change == "missing" ? null! : template));
        Assert.Contains(NotificationRepository.ExcelPayslipTemplateCode, error.Message);
    }

    [Fact]
    public void TextTemplateEscapesMarkupAndPreservesLineBreaksAndHeaderSafety()
    {
        var template = ExcelPayslipTests.MailTemplate; template.IsHtml = false;
        template.SubjectTemplate = "{{clientName}}\r\nSalary slip";
        template.BodyTemplate = "Hello {{employeeName}}\r\n<script>Text & only</script>";
        var document = Prepare(Batch(), template);
        Assert.Equal("Example & Client Salary slip", document.Subject);
        Assert.Equal("<p>Hello Alex &lt;Smith&gt;<br />&lt;script&gt;Text &amp; only&lt;/script&gt;</p>", document.BodyHtml);
    }

    [Fact]
    public void RichTextHeaderAndFooterLogosBecomeInlineMimeResourcesWithoutChangingSavedTemplate()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQWQAAAAASUVORK5CYII=";
        var html = $"<p><img alt='GAD' src='data:image/png;base64,{png}'></p><p>Message</p><p><img alt='Frevo One' src=\"data:image/png;base64,{png}\"></p>";
        var builder = new BodyBuilder { HtmlBody = html };
        NotificationRepository.EmbedExcelPayslipImages(builder);
        Assert.DoesNotContain("data:image", builder.HtmlBody);
        Assert.Contains("alt='GAD'", builder.HtmlBody); Assert.Contains("alt='Frevo One'", builder.HtmlBody);
        Assert.Equal(2, builder.LinkedResources.Count);
        foreach (var resource in builder.LinkedResources)
        {
            var image = Assert.IsType<MimePart>(resource);
            Assert.Equal("image/png", image.ContentType.MimeType);
            Assert.Contains("cid:" + image.ContentId, builder.HtmlBody);
            using var bytes = new MemoryStream(); image.Content.DecodeTo(bytes);
            Assert.Equal(Convert.FromBase64String(png), bytes.ToArray());
        }
        Assert.Contains("data:image/png", html);
        Assert.Empty(builder.Attachments);
    }

    [Fact]
    public void SerializedSalaryEmailContainsResolvableInlineLogosAndTheBrandedSimplePdf()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQWQAAAAASUVORK5CYII=";
        var batch = Batch(); batch.SimpleLayout = true;
        batch.Rows[0].Earnings = [new() { Label = "Earn Salary", Amount = 100 }];
        var template = ExcelPayslipTests.MailTemplate;
        template.BodyTemplate = $"<img alt='GAD' src='data:image/png;base64,{png}'><p>Dear {{{{recipientName}}}}</p><img alt='Frevo One' src='data:image/png;base64,{png}'>";
        var document = Prepare(batch, template); document.QueueId = 123;
        var queue = new NotificationQueueItem { Id = 123, EventCode = NotificationRepository.ExcelPayslipEvent,
            ClientId = batch.ClientId, ResourceType = "ExcelPayslipBatch", ResourceId = batch.Id + ":" + document.Id,
            ToJson = JsonSerializer.Serialize(new[] { document.Email }), CcJson = "[]", BccJson = "[]" };
        var builder = new BodyBuilder { HtmlBody = document.BodyHtml };
        NotificationRepository.AttachExcelPayslipPdf(builder, queue, document, batch);
        var message = new MimeMessage { Body = builder.ToMessageBody(), Subject = document.Subject };
        message.From.Add(MailboxAddress.Parse("sender@example.test")); message.To.Add(MailboxAddress.Parse(document.Email));
        using var wire = new MemoryStream(); message.WriteTo(wire); wire.Position = 0;
        using var received = MimeMessage.Load(wire);
        Assert.DoesNotContain("data:image", received.HtmlBody);
        var images = received.BodyParts.OfType<MimePart>().Where(part => part.ContentType.MediaType == "image").ToArray();
        Assert.Equal(2, images.Length);
        foreach (var image in images)
        {
            Assert.Contains("cid:" + image.ContentId, received.HtmlBody);
            using var content = new MemoryStream(); image.Content.DecodeTo(content);
            Assert.Equal(Convert.FromBase64String(png), content.ToArray());
        }
        var attachment = Assert.IsType<MimePart>(Assert.Single(received.Attachments));
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        using var pdfBytes = new MemoryStream(); attachment.Content.DecodeTo(pdfBytes); pdfBytes.Position = 0;
        using var pdf = PdfReader.Open(pdfBytes, PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages); var page = pdf.Pages[0];
        var text = Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue).Replace("\\(", "(").Replace("\\)", ")");
        Assert.Contains("GA DIGITAL WEB WORD (P) LTD", text); Assert.Contains("HARGOBIND ENCLAVE", text);
        Assert.Contains("Salary Slip", text);
        Assert.Single(page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements);
    }
}
