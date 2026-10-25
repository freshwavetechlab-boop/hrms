using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipLifecycleTests
{
    [Fact]
    public void ConfiguredAttachmentLimitsAndTypesAreNeverBypassedByOriginalRetention()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("a,b"));
        var file = new FormFile(input, 0, input.Length, "file", "synthetic.csv");
        var field = new AttachmentFieldConfiguration { AttachmentAttributeId = 1, AllowedExtensionsJson = "[\"csv\"]", AllowedMimeTypesJson = "[\"text/csv\"]", MaximumFileSizeBytes = 10 };
        AttachmentRepository.ValidateWorkbookField(field, file, "text/csv");
        field.MaximumFileSizeBytes = 2;
        Assert.Throws<InvalidOperationException>(() => AttachmentRepository.ValidateWorkbookField(field, file, "text/csv"));
        field.MaximumFileSizeBytes = 10; field.AllowedExtensionsJson = "[\"xlsx\"]";
        Assert.Throws<InvalidOperationException>(() => AttachmentRepository.ValidateWorkbookField(field, file, "text/csv"));
        field.AllowedExtensionsJson = "[\"csv\"]"; field.RequiresVerification = true;
        Assert.Throws<InvalidOperationException>(() => AttachmentRepository.ValidateWorkbookField(field, file, "text/csv"));
    }
    [Fact]
    public void DeletePreviewIsBoundToSnapshotStateOriginalsAndDependencyCounts()
    {
        var preview = new ExcelPayslipDeletePreview { BatchId = "batch-one", PayslipRows = 1, OriginalLinks = 2 };
        const string snapshot = "{\"clientId\":42,\"netPay\":100}";
        var token = ExcelPayslipRepository.DeletePreviewToken(snapshot, "{}", ["a", "b"], preview);
        preview.Token = token;
        Assert.Equal(token, ExcelPayslipRepository.DeletePreviewToken(snapshot, "{}", ["b", "a"], preview));
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken(snapshot.Replace("100", "101"), "{}", ["a", "b"], preview));
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken(snapshot.Replace("42", "43"), "{}", ["a", "b"], preview));
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken(snapshot, "{\"jobs\":{\"new-job\":{}}}", ["a", "b"], preview));
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken(snapshot, "{}", ["a", "c"], preview));
        preview.Notifications = 1;
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken(snapshot, "{}", ["a", "b"], preview));
    }

    [Theory]
    [InlineData("not-a-batch", "not-a-batch", "", 42)]
    [InlineData("0123456789abcdef0123456789abcdef", "different-batch", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 42)]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef", "invalid-token", 42)]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    public async Task InvalidDeletionConfirmationIsRejectedBeforeAnyDatabaseAccess(string id, string confirmation, string token, int clientId)
    {
        var repository = new ExcelPayslipRepository(new ConfigurationBuilder().Build(), null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteBatchAsync(clientId, id, new(token, confirmation), "synthetic-actor"));
    }
    [Theory]
    [InlineData("delivery")][InlineData("job")][InlineData("request")][InlineData("notification")]
    [InlineData("notificationLog")][InlineData("masterSetup")][InlineData("derived")]
    public void FinancialAndDeliveryEvidenceBlocksPermanentDeletion(string evidence)
    {
        var preview = new ExcelPayslipDeletePreview();
        switch (evidence)
        {
            case "delivery": preview.DeliveryRecords = 1; break;
            case "job": preview.MailJobs = 1; break;
            case "request": preview.RequestReceipts = 1; break;
            case "notification": preview.Notifications = 1; break;
            case "notificationLog": preview.NotificationLogs = 1; break;
            case "masterSetup": preview.MasterSetupReceipts = 1; break;
            case "derived": preview.DerivedBatches = 1; break;
        }
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 1, false));
    }

    [Fact]
    public void UntrackedLegacyAncestryAndUnknownSendStateFailClosed()
    {
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(new(), 0, false));
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(new(), 1, true));
    }

    [Fact]
    public void UnusedBatchMayDeleteItsLinksWhileSharedOriginalBytesRemainRetained()
    {
        var preview = new ExcelPayslipDeletePreview { PayslipRows = 100, CalculationSources = 1, OriginalLinks = 2, OtherBatchOriginalLinks = 7 };
        Assert.Empty(ExcelPayslipDeletionPolicy.Blockers(preview, 1, false));
    }

    [Fact]
    public void LegacySnapshotsDoNotAcquireNewSerializedFieldsOrChangeMailHashes()
    {
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>("{\"id\":\"0123456789abcdef0123456789abcdef\",\"clientId\":1,\"rows\":[]}", ExcelPayslipRepository.JsonOptions)!;
        var json = JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions);
        Assert.DoesNotContain("lifecycleVersion", json); Assert.DoesNotContain("parentBatchId", json); Assert.DoesNotContain("originalWorkbooks", json);
    }

    [Theory]
    [InlineData("bad.xlsx")][InlineData("bad.xls")][InlineData("bad.xlsm")][InlineData("bad.exe")]
    public async Task RenamingPlainTextCannotBypassWorkbookFormatValidation(string name)
    {
        var bytes = Encoding.UTF8.GetBytes("This is not a workbook.");
        using var input = new MemoryStream(bytes);
        var file = new FormFile(input, 0, bytes.Length, "file", name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentRepository.InspectWorkbookAsync(file, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task WorkbookRetentionHashesExactBytesWithoutEvaluatingFormulaOrExternalLinks()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, value) in new[] {
                ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"),
                ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" />"),
                ("xl/worksheets/sheet1.xml", "<worksheet><c><f>WEBSERVICE(\"https://example.invalid/do-not-call\")</f></c></worksheet>") })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
        }
        var bytes = stream.ToArray(); stream.Position = 0;
        var file = new FormFile(stream, 0, stream.Length, "file", "synthetic.xlsx");
        var result = await AttachmentRepository.InspectWorkbookAsync(file, 4096, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Hash);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.Mime);
    }

    [Fact]
    public async Task FileLimitAndBinaryCsvAreRejectedBeforeStorage()
    {
        using var input = new MemoryStream(new byte[] { 65, 0, 66 });
        var file = new FormFile(input, 0, 3, "file", "synthetic.csv");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentRepository.InspectWorkbookAsync(file, 2, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentRepository.InspectWorkbookAsync(file, 10, CancellationToken.None));
    }
}
