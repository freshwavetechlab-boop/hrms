using System.Text.Json;
using MimeKit;
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
}
