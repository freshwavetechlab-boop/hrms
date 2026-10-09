using System.Text;
using System.Text.Json;
using MimeKit;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipTests
{
    internal static NotificationTemplate MailTemplate => new()
    {
        Code = NotificationRepository.ExcelPayslipTemplateCode, SubjectTemplate = "{{clientName}} payslip - {{month}}",
        BodyTemplate = "<p>Payslip for {{employeeName}} for {{month}} is attached as a PDF.</p>", IsHtml = true, IsActive = true
    };
    private static ExcelPayslipBatch Batch() => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 11, ClientName = "Example Client", Month = "2026-09",
        SourceFileName = "PLRS.xlsx", SheetName = "Calcultion", HeaderRow = 5,
        Rows = [new()
        {
            Id = "sheet1:6", SourceRow = 6, EmployeeName = "Example Employee", Email = "person@example.test",
            Information = [new() { Label = "Account", Value = "001234567890" }],
            Earnings = [new() { Label = "Wages", Amount = 15414m }],
            Deductions = [new() { Label = "PF", Amount = 1849.68m }, new() { Label = "ESI", Amount = 115.605m }],
            EmployerContributions = [new() { Label = "Employer PF", Amount = 2003.82m }, new() { Label = "Employer ESI", Amount = 500.955m }],
            NetPay = 13448.715m, DeclaredGross = 15414m, DeclaredDeductions = 1965.285m
        }]
    };

    [Fact]
    public void AnyPositiveActiveClientCanUseExcelPayslips()
    {
        Assert.True(ExcelPayslipRepository.IsSupportedClient(new() { Id = 11, Name = "Punjab Land Records Society", IsActive = true }));
        Assert.True(ExcelPayslipRepository.IsSupportedClient(new() { Id = 123, Name = "Another Client", IsActive = true }));
        Assert.False(ExcelPayslipRepository.IsSupportedClient(new() { Id = 0, Name = "Another Client", IsActive = true }));
        Assert.False(ExcelPayslipRepository.IsSupportedClient(new() { Id = 11, Name = "Punjab Land Records Society", IsActive = false }));
    }

    [Fact]
    public void TemplatePresetsAreClientScopedActiveLabelsAndCategoriesWithoutFormulas()
    {
        var setup = """
        {
          "salaryStructures": [
            {"id":1,"clientId":"11:Example","name":"Source mapping","active":true,"lines":[{"componentId":"101","formula":"1/0"},{"componentId":"109"},{"componentId":"201"},{"componentId":"300"},{"componentId":"400"},{"componentId":"999"}]},
            {"id":2,"clientId":"12","name":"Other client","active":true,"lines":[{"componentId":"101"}]},
            {"id":3,"clientId":"11","name":"Disabled","active":false,"lines":[{"componentId":"101"}]}
          ],
          "salaryComponents":[
            {"id":"101","code":"BASIC","name":"Basic wages","category":"Earning","active":true,"formula":"UNKNOWN_FUNCTION()"},
            {"id":"109","code":"PF","name":"Employee PF","category":"Deduction","active":true},
            {"id":"201","code":"EPF_ER","name":"Employer PF","category":"Benefit","active":true},
            {"id":"300","code":"TRAVEL","name":"Travel reimbursement","category":"Reimbursement","active":true},
            {"id":"400","code":"NOTE","name":"Reference","category":"Summary","active":true},
            {"id":"999","code":"OLD","name":"Inactive","category":"Earning","active":false}
          ]
        }
        """;
        var templates = ExcelPayslipRepository.ReadTemplates(setup, 11);
        var template = Assert.Single(templates);
        Assert.Equal("1", template.Id); Assert.Equal("Source mapping", template.Name);
        Assert.Equal(new[] { "Earning", "Deduction", "Employer", "Earning", "Information" }, template.Components.Select(c => c.Category));
        Assert.Equal("Basic wages", template.Components[0].Name);
        var json = JsonSerializer.Serialize(templates, ExcelPayslipRepository.JsonOptions);
        Assert.DoesNotContain("formula", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ExcelPayslipRepository.ReadTemplates(setup, 999));
    }

    [Fact]
    public void ScopedTemplateProjectionKeepsLineOrderExactIdsAndClientReferences()
    {
        var rows = new ExcelPayslipRepository.TemplateMappingRow[]
        {
            new() { Id = "9007199254740993", ClientRef = "11:Example", Name = "Z template", ComponentId = "201", MasterId = "201", Code = "PF", ComponentName = "PF", Category = "Deduction" },
            new() { Id = "9007199254740993", ClientRef = "11:Example", Name = "Z template", ComponentId = "101", MasterId = "101", Code = "BASIC", ComponentName = "Basic", Category = "Earning" },
            new() { Id = "9007199254740993", ClientRef = "11:Example", Name = "Z template", ComponentId = "0101" }, // Not numeric-coerced to component 101.
            new() { Id = "1", ClientRef = "11", Name = "A empty template" },
            new() { Id = "2", ClientRef = "12:Other", Name = "Other client", ComponentId = "101", MasterId = "101", Code = "BASIC", ComponentName = "Basic", Category = "Earning" }
        };
        var templates = ExcelPayslipRepository.ReadTemplateMappings(rows, null, 11);
        Assert.Equal(new[] { "1", "9007199254740993" }, templates.Select(t => t.Id));
        Assert.Empty(templates[0].Components);
        Assert.Equal(new[] { "201", "101" }, templates[1].Components.Select(c => c.Id));
        Assert.Equal(new[] { "Deduction", "Earning" }, templates[1].Components.Select(c => c.Category));
    }

    [Fact]
    public void ScopedTemplateProjectionUsesLegacyComponentsWithoutLoadingUnrelatedSetup()
    {
        var rows = new ExcelPayslipRepository.TemplateMappingRow[]
        {
            new() { Id = "1", ClientRef = "11:Example", Name = "Legacy", ComponentId = "101" },
            new() { Id = "1", ClientRef = "11:Example", Name = "Legacy", ComponentId = "102" },
            new() { Id = "1", ClientRef = "11:Example", Name = "Legacy", ComponentId = "103" }
        };
        const string legacy = """
        [{"id":"101","code":"TRAVEL","name":"Travel","category":"Reimbursement"},
         {"id":"102","code":"OLD","name":"Old","category":"Earning","active":false},
         {"id":"103","code":"ER","name":"Employer","category":"Benefit"}]
        """;
        var template = Assert.Single(ExcelPayslipRepository.ReadTemplateMappings(rows, legacy, 11));
        Assert.Equal(new[] { "Earning", "Employer" }, template.Components.Select(c => c.Category));
        Assert.Empty(ExcelPayslipRepository.ReadTemplateMappings([], legacy, 11));
        Assert.Empty(ExcelPayslipRepository.ReadTemplateMappings(rows, "null", 11));
    }

    [Fact]
    public void SourceFinalValuesAndTextIdentifiersArePreservedWithoutEmployerDeductions()
    {
        var batch = Batch(); var row = batch.Rows[0];
        var before = JsonSerializer.Serialize(row);
        Assert.Null(ExcelPayslipRepository.ValidateBatch(batch));
        Assert.Empty(ExcelPayslipRepository.RowWarnings(row));
        Assert.Equal(before, JsonSerializer.Serialize(row));
        Assert.Equal(13448.715m, row.NetPay);
        Assert.Equal("001234567890", row.Information[0].Value);
        Assert.Empty(row.EmployeeCode); // Workbook serials are not employee codes.
    }

    [Fact]
    public void InvalidOrDuplicateSourceRowsAndUnboundedValuesAreRejected()
    {
        var batch = Batch(); batch.Rows.Add(batch.Rows[0]);
        Assert.NotNull(ExcelPayslipRepository.ValidateBatch(batch));
        batch = Batch(); batch.Rows[0].SourceRow = 5;
        Assert.Contains("follow", ExcelPayslipRepository.ValidateBatch(batch));
        batch = Batch(); batch.Rows[0].NetPay = decimal.MinValue;
        Assert.Contains("limit", ExcelPayslipRepository.ValidateBatch(batch));
        batch = Batch(); batch.Rows[0].Earnings = null!;
        Assert.NotNull(ExcelPayslipRepository.ValidateBatch(batch));
        batch = Batch(); batch.Month = "2026-13";
        Assert.NotNull(ExcelPayslipRepository.ValidateBatch(batch));
    }

    [Fact]
    public void MissingRequiredAmountsCannotSilentlyBecomeZero()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExcelPayslipAmount>("{\"label\":\"Wages\"}", ExcelPayslipRepository.JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExcelPayslipRow>("{\"id\":\"row6\",\"employeeName\":\"Example\"}", ExcelPayslipRepository.JsonOptions));
        Assert.Equal(0m, JsonSerializer.Deserialize<ExcelPayslipAmount>("{\"label\":\"Bonus\",\"amount\":0}", ExcelPayslipRepository.JsonOptions)!.Amount);
    }

    [Fact]
    public void WarningsRequireAcknowledgementAndNeverReplaceSourceNetPay()
    {
        var batch = Batch(); var row = batch.Rows[0]; row.NetPay = 10000m;
        row.Warnings = []; // Server validation does not trust warnings supplied by a client.
        var selection = new ExcelPayslipSelection { RowIds = [row.Id] };
        Assert.Contains("acknowledge", ExcelPayslipRepository.SelectRows(batch, selection).Error);
        selection.AcknowledgeWarnings = true;
        var result = ExcelPayslipRepository.SelectRows(batch, selection);
        Assert.Null(result.Error); Assert.Equal(10000m, result.Rows[0].NetPay);
        Assert.Single(ExcelPayslipRepository.RowWarnings(row));
    }

    [Fact]
    public void SelectionIsExplicitAndCannotCrossIntoAnotherBatch()
    {
        var batch = Batch();
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch, new()).Error);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch, new() { RowIds = ["other:7"] }).Error);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch, new() { RowIds = ["sheet1:6", "sheet1:6"] }).Error);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch, new() { RowIds = ["sheet1:6"], AmountDecimalPlaces = 3 }).Error);
    }

    [Fact]
    public void OptionalEmailNeverBlocksPdfExportAndMissingRecipientsAreReviewedWithoutSending()
    {
        var batch = Batch(); var row = batch.Rows[0]; row.Email = "not an email";
        Assert.Null(ExcelPayslipRepository.ValidateBatch(batch)); Assert.Empty(ExcelPayslipRepository.RowWarnings(row));
        Assert.Null(ExcelPayslipRepository.SelectRows(batch, new() { RowIds = [row.Id] }).Error);
        var review = ExcelPayslipRepository.ReviewIndividualRecipients(batch.Rows, new Dictionary<string, string>());
        Assert.Equal("Error", Assert.Single(review.Items).Status);
        var overridden = ExcelPayslipRepository.ReviewIndividualRecipients(batch.Rows, new Dictionary<string, string> { [row.Id] = "corrected@example.test" });
        Assert.Equal("Ready", Assert.Single(overridden.Items).Status);
        Assert.Equal("not an email", row.Email); // Send overrides never edit the saved source record.
        row.Email = "";
        Assert.Equal("Error", Assert.Single(ExcelPayslipRepository.ReviewIndividualRecipients(batch.Rows, new Dictionary<string, string>()).Items).Status);
    }

    [Fact]
    public void MappingProfileRequiresAFormatSignatureAndHasABoundedSize()
    {
        var signature = new string('a', 64);
        using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { headerSignature = signature, columns = new[] { new { columnIndex = 1, sourceHeader = "Wages", kind = "earning", label = "Wages" } } }));
        Assert.Null(ExcelPayslipRepository.ValidateProfile(valid.RootElement));
        using var absent = JsonDocument.Parse("{\"columns\":[]}");
        Assert.NotNull(ExcelPayslipRepository.ValidateProfile(absent.RootElement));
        using var huge = JsonDocument.Parse(JsonSerializer.Serialize(new { headerSignature = signature, columns = new[] { new string('x', 65536) } }));
        Assert.Contains("64 KiB", ExcelPayslipRepository.ValidateProfile(huge.RootElement));
        using var invalidTemplate = JsonDocument.Parse(JsonSerializer.Serialize(new { headerSignature = signature, columns = Array.Empty<string>(), salaryTemplateId = new { id = "1" } }));
        Assert.NotNull(ExcelPayslipRepository.ValidateProfile(invalidTemplate.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"columnIndex\":\"1\",\"sourceHeader\":\"Wages\",\"label\":\"Wages\",\"kind\":\"earning\"}]")]
    [InlineData("[{\"columnIndex\":-1,\"sourceHeader\":\"Wages\",\"label\":\"Wages\",\"kind\":\"earning\"}]")]
    [InlineData("[{\"columnIndex\":512,\"sourceHeader\":\"Wages\",\"label\":\"Wages\",\"kind\":\"earning\"}]")]
    [InlineData("[{\"columnIndex\":1,\"sourceHeader\":\"Wages\",\"label\":\"Wages\",\"kind\":\"formula\"}]")]
    [InlineData("[{\"columnIndex\":1,\"sourceHeader\":\"Wages\",\"label\":\"Wages\",\"kind\":\"earning\",\"formula\":\"BASIC*12%\"}]")]
    [InlineData("[{\"columnIndex\":1,\"sourceHeader\":\"A\",\"label\":\"A\",\"kind\":\"info\"},{\"columnIndex\":1,\"sourceHeader\":\"B\",\"label\":\"B\",\"kind\":\"info\"}]")]
    public void MalformedProfileColumnsAreRejectedBeforePersistence(string columnsJson)
    {
        using var profile = JsonDocument.Parse("{\"headerSignature\":\"" + new string('a', 64) + "\",\"columns\":" + columnsJson + "}");
        Assert.NotNull(ExcelPayslipRepository.ValidateProfile(profile.RootElement));
    }

    [Theory]
    [InlineData("person@example.test", true)]
    [InlineData(" Person@example.test ", true)]
    [InlineData("Person <person@example.test>", false)]
    [InlineData("a@example.test,b@example.test", false)]
    [InlineData("a@example.test\r\nBcc: other@example.test", false)]
    [InlineData("", false)]
    public void EmailOverridesAllowOnlyOnePlainMailbox(string input, bool valid) =>
        Assert.Equal(valid, ExcelPayslipRepository.TryEmail(input, out _));

    [Fact]
    public void IdempotencyFingerprintBindsRowsRecipientsAndSealChoice()
    {
        var first = new SendExcelPayslipsRequest { RowIds = ["b", "a"], RequestId = Guid.NewGuid().ToString(), EmailOverrides = new() { ["a"] = "p@example.test", ["b"] = "q@example.test" } };
        var reordered = new SendExcelPayslipsRequest { RowIds = ["a", "b"], RequestId = first.RequestId, EmailOverrides = new() { ["b"] = "q@example.test", ["a"] = "p@example.test" } };
        Assert.Equal(ExcelPayslipRepository.SendFingerprint("batch", first), ExcelPayslipRepository.SendFingerprint("batch", reordered));
        reordered.AmountDecimalPlaces = 2;
        Assert.NotEqual(ExcelPayslipRepository.SendFingerprint("batch", first), ExcelPayslipRepository.SendFingerprint("batch", reordered));
        reordered.AmountDecimalPlaces = first.AmountDecimalPlaces;
        reordered.IncludeSeal = false;
        Assert.NotEqual(ExcelPayslipRepository.SendFingerprint("batch", first), ExcelPayslipRepository.SendFingerprint("batch", reordered));
        reordered.IncludeSeal = first.IncludeSeal; reordered.EmailOverrides["a"] = "another@example.test";
        Assert.NotEqual(ExcelPayslipRepository.SendFingerprint("batch", first), ExcelPayslipRepository.SendFingerprint("batch", reordered));
    }

    [Fact]
    public void PriorSendReceiptCannotBeReusedWithChangedRecipientsEvenWhenNoneAreValid()
    {
        var batch = Batch(); var request = new SendExcelPayslipsRequest { RowIds = [batch.Rows[0].Id] };
        var fingerprint = ExcelPayslipRepository.SendFingerprint(batch.Id, request);
        var receipt = JsonSerializer.Serialize(new
        {
            fingerprint,
            result = new ExcelPayslipDeliveryResult { Items = [new() { RowId = batch.Rows[0].Id, Status = "Queued", Email = batch.Rows[0].Email }] }
        }, ExcelPayslipRepository.JsonOptions);
        Assert.Equal("Already queued", Assert.Single(ExcelPayslipRepository.RestoreSendRequest(receipt, fingerprint).Item!.Items).Status);
        request.EmailOverrides[batch.Rows[0].Id] = "";
        Assert.Equal("Error", Assert.Single(ExcelPayslipRepository.ReviewIndividualRecipients(batch.Rows, request.EmailOverrides).Items).Status);
        Assert.NotNull(ExcelPayslipRepository.RestoreSendRequest(receipt, ExcelPayslipRepository.SendFingerprint(batch.Id, request)).Error);
        Assert.Null(ExcelPayslipRepository.RestoreSendRequest(null, fingerprint).Item);
    }

    [Fact]
    public void PreparedMailStoresOnlyDeliveryMetadataAndRegeneratesTheSelectedPdf()
    {
        var batch = Batch(); batch.Rows[0].EmployeeName = "Name <script>";
        var selected = batch.Rows.ToArray();
        batch.Rows.Add(new() { Id = "sheet1:7", SourceRow = 7, EmployeeName = "Not selected", NetPay = 0 });
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nexample\n%%EOF");
        var document = NotificationRepository.PrepareExcelPayslipMail(batch, selected, "person@example.test", pdf, Guid.NewGuid().ToString(), "operator@example.test", false, 2, MailTemplate);
        Assert.StartsWith("Example Client payslip", document.Subject); Assert.StartsWith("Example-Client-payslip", document.FileName);
        Assert.Contains("&lt;script&gt;", document.BodyHtml); Assert.DoesNotContain("<script>", document.BodyHtml);
        Assert.DoesNotContain("href", document.BodyHtml);
        var json = JsonSerializer.Serialize(document, ExcelPayslipRepository.JsonOptions);
        Assert.DoesNotContain("pdfBase64", json); Assert.DoesNotContain("pdfSha256", json);
        Assert.DoesNotContain(Convert.ToBase64String(pdf), json);
        Assert.Contains("\"includeSeal\":false", json); Assert.Contains("\"amountDecimalPlaces\":2", json);
        Assert.Equal(64, document.BatchSha256.Length);
        var queue = Queue(document); var builder = new BodyBuilder { HtmlBody = document.BodyHtml };
        NotificationRepository.AttachExcelPayslipPdf(builder, queue, document, batch);
        var attachment = Assert.IsType<MimePart>(Assert.Single(builder.Attachments));
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        Assert.EndsWith(".pdf", attachment.FileName);
        using var decoded = new MemoryStream(); attachment.Content.DecodeTo(decoded);
        Assert.NotEqual(pdf, decoded.ToArray()); // The original preview bytes are never retained.
        using var regenerated = PdfReader.Open(new MemoryStream(decoded.ToArray()), PdfDocumentOpenMode.Import);
        Assert.Equal(1, regenerated.PageCount);
        Assert.Single(regenerated.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("cc")]
    [InlineData("bcc")]
    [InlineData("queue")]
    [InlineData("client")]
    [InlineData("resource")]
    [InlineData("event")]
    public void DeliveryDescriptorCannotBeReusedForAnotherRecipientOrQueue(string change)
    {
        var batch = Batch(); var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nexample\n%%EOF");
        var document = NotificationRepository.PrepareExcelPayslipMail(batch, batch.Rows, "person@example.test", pdf, Guid.NewGuid().ToString(), "operator", true, 0, MailTemplate);
        var queue = Queue(document);
        switch (change)
        {
            case "recipient": queue.ToJson = "[\"other@example.test\"]"; break;
            case "cc": queue.CcJson = "[\"other@example.test\"]"; break;
            case "bcc": queue.BccJson = "[\"other@example.test\"]"; break;
            case "queue": queue.Id++; break;
            case "client": queue.ClientId++; break;
            case "resource": queue.ResourceId = "other:" + document.Id; break;
            case "event": queue.EventCode = "OTHER.EVENT"; break;
        }
        var builder = new BodyBuilder();
        Assert.Throws<InvalidOperationException>(() => NotificationRepository.AttachExcelPayslipPdf(builder, queue, document, batch));
        Assert.Empty(builder.Attachments);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("client")]
    [InlineData("batch")]
    [InlineData("hash")]
    [InlineData("row")]
    [InlineData("duplicates")]
    [InlineData("precision")]
    public void RegenerationRejectsChangedSourceOrInvalidSavedSelection(string change)
    {
        var batch = Batch(); var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nexample\n%%EOF");
        var document = NotificationRepository.PrepareExcelPayslipMail(batch, batch.Rows, "person@example.test", pdf, Guid.NewGuid().ToString(), "operator", true, 0, MailTemplate);
        var queue = Queue(document);
        switch (change)
        {
            case "source": batch.Rows[0].NetPay++; break;
            case "client": batch.ClientId++; break;
            case "batch": batch.Id = Guid.NewGuid().ToString("N"); break;
            case "hash": document.BatchSha256 = new string('0', 64); break;
            case "row": document.RowIds = ["outside-this-batch"]; break;
            case "duplicates": document.RowIds.Add(document.RowIds[0]); break;
            case "precision": document.AmountDecimalPlaces = 3; break;
        }
        var builder = new BodyBuilder();
        Assert.Throws<InvalidOperationException>(() => NotificationRepository.AttachExcelPayslipPdf(builder, queue, document, batch));
        Assert.Empty(builder.Attachments);
    }

    [Theory]
    [InlineData("includeSeal")]
    [InlineData("amountDecimalPlaces")]
    public void StoredDeliveryRequiresExplicitRenderOptions(string missingProperty)
    {
        var batch = Batch(); var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nexample\n%%EOF");
        var document = NotificationRepository.PrepareExcelPayslipMail(batch, batch.Rows, "person@example.test", pdf, Guid.NewGuid().ToString(), "operator", true, 0, MailTemplate);
        var json = JsonSerializer.SerializeToNode(document, ExcelPayslipRepository.JsonOptions)!;
        json.AsObject().Remove(missingProperty);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NotificationRepository.ExcelPayslipMailDocument>(json.ToJsonString(), ExcelPayslipRepository.JsonOptions));
    }

    [Fact]
    public void QueuePreparationStillValidatesPdfSizeAndType()
    {
        Assert.NotNull(NotificationRepository.ValidateExcelPayslipPdf("<html>not pdf</html>"u8.ToArray()));
        var large = new byte[10 * 1024 * 1024 + 1]; "%PDF-"u8.CopyTo(large);
        Assert.Contains("10 MiB", NotificationRepository.ValidateExcelPayslipPdf(large));
    }

    private static NotificationQueueItem Queue(NotificationRepository.ExcelPayslipMailDocument document)
    {
        document.QueueId = 123;
        return new() { Id = 123, EventCode = NotificationRepository.ExcelPayslipEvent, ClientId = document.ClientId, ResourceType = "ExcelPayslipBatch", ResourceId = document.BatchId + ":" + document.Id, ToJson = JsonSerializer.Serialize(new[] { document.Email }) };
    }
}
