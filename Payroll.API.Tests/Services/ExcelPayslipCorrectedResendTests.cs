using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCorrectedResendTests
{
    private const string BatchId = "1234567890abcdef1234567890abcdef";
    internal static (ExcelPayslipBatch Raw, ExcelPayslipBatch Current, ExcelPayslipCalculationSource Source) Fixture()
    {
        var row = new ExcelPayslipRow
        {
            Id = "saved-row", SourceRow = 7, EmployeeCode = "SYNTH-007", EmployeeName = "Captured Employee", Email = "source@example.test",
            Information = [new() { Label = "PAN", Value = "ABCDE1234F" }, new() { Label = "UAN", Value = "" }, new() { Label = "Mobile No", Value = "9000000007" },
                new() { Label = "Work Location", Value = "Historical office" }, new() { Label = "Annual CTC", Value = "480000" }],
            Earnings = [new() { Label = "Basic", Amount = 1244.5678m }, new() { Label = "Adjustment", Amount = 0 }],
            Deductions = [new() { Label = "PF", Amount = 10 }], EmployerContributions = [new() { Label = "Employer PF", Amount = 12.3456m }],
            DeclaredGross = 1244.5678m, DeclaredDeductions = 10, NetPay = 1234.5678m
        };
        var raw = new ExcelPayslipBatch { Id = BatchId, ClientId = 17, ClientName = "Synthetic client", Month = "2026-10", SheetName = "Salary", HeaderRow = 1,
            SourceFileName = "synthetic.xlsx", Rows = [row], CreatedAtUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), CreatedBy = "test" };
        var source = new ExcelPayslipCalculationSource { SheetName = "Salary", HeaderRow = 1, ColumnCount = 3,
            Columns = [new() { ColumnIndex = 0, Label = "Employee code", SourceHeader = "Employee code", Kind = "employeeCode" },
                new() { ColumnIndex = 1, Label = "Basic", SourceHeader = "Basic", Kind = "earning" }, new() { ColumnIndex = 2, Label = "Net", SourceHeader = "Net", Kind = "netPay" }],
            Rows = [new() { SourceRow = 7, Cells = [new() { Value = "SYNTH-007" }, new() { Value = "1244.5678", FormulaText = "1000+244.5678" }, new() { Value = "1234.5678", FormulaText = "B7-10" }] }] };
        raw.ProfileLinks = [new(row.Id, row.SourceRow, 707, ExcelPayslipMasterSynchronization.ProfileIdentityHash(raw, row, source))];
        var current = Clone(raw); current.Rows[0].Employment = new(707, row.EmployeeCode, "Active", null, false, false);
        current.Rows[0].MasterDetails = new(707, "Current Analyst")
        {
            Linked = true, ProfileOnly = true, EmployeeCode = row.EmployeeCode, EmployeeName = "Corrected Synthetic Employee", Email = "current@example.test",
            AnnualCtc = 9999999, Fields = new() { ["uan"] = "001234567890", ["phone"] = "9000000999", ["worklocation"] = "Current different office", ["annualctc"] = "9999999" },
            Salary = new("Ready", "Must never replace the saved payslip", [new() { Label = "Changed salary", Amount = 999999 }], [], [], 999999, 0, 999999)
        };
        return (raw, current, source);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
    private static string Salary(ExcelPayslipRow row) => JsonSerializer.Serialize(new { row.Earnings, row.Deductions, row.EmployerContributions, row.DeclaredGross, row.DeclaredDeductions, row.NetPay });
    private static SendExcelPayslipsRequest Request(bool corrected = true) => new() { RequestId = Guid.NewGuid().ToString("N"), RowIds = ["saved-row"], Mode = "Individual", AmountDecimalPlaces = 2, ResendCorrected = corrected, AcknowledgeResend = corrected };

    [Fact]
    public void CorrectedResendRequiresExplicitAcknowledgementIndividualModeAndStoredRecipient()
    {
        var request = Request(); Assert.Null(ExcelPayslipRepository.ValidateSendRequest(BatchId, request, 1000));
        request.AcknowledgeResend = false; Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(BatchId, request, 1000));
        request.AcknowledgeResend = true; request.Mode = "Combined"; Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(BatchId, request, 1000));
        request.Mode = "Individual"; request.Email = "arbitrary@example.test"; Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(BatchId, request, 1000));
        request.Email = ""; request.EmailOverrides["saved-row"] = "arbitrary@example.test"; Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(BatchId, request, 1000));
        var defaults = new SendExcelPayslipsRequest(); Assert.False(defaults.ResendCorrected); Assert.False(defaults.AcknowledgeResend);
    }

    [Theory]
    [InlineData("Sent")]
    [InlineData("Delivered")]
    public void AcceptedOldMailCanBeCorrectedWhileOrdinarySendStillSkipsIt(string outcome)
    {
        var (raw, current, _) = Fixture(); var state = new ExcelPayslipSendState(); var queues = AddDelivery(raw, state, "Sent");
        if (outcome == "Delivered") queues[0].DeliveryStatus = outcome;
        var before = JsonSerializer.Serialize(state, ExcelPayslipRepository.JsonOptions);
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(raw, state, queues);
        Assert.Equal("Skipped", Assert.Single(ExcelPayslipRepository.ReviewDeliverySelection(current.Rows, Request(false), statuses).Items).Status);
        var corrected = Assert.Single(ExcelPayslipRepository.ReviewCorrectedDeliverySelection(current.Rows, Request(), statuses).Items);
        Assert.Equal("Ready", corrected.Status); Assert.Equal("current@example.test", corrected.Email);
        Assert.Equal(before, JsonSerializer.Serialize(state, ExcelPayslipRepository.JsonOptions));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Retry")]
    [InlineData("ExcelPending")]
    [InlineData("ExcelRetry")]
    [InlineData("Processing")]
    [InlineData("Sending")]
    [InlineData("Cancelled")]
    [InlineData("missing")]
    [InlineData("Deferred")]
    public void ActiveUnknownOrDeferredHistoryBlocksCorrectedResendEvenAlongsideOldSent(string outcome)
    {
        var (raw, current, _) = Fixture(); var state = new ExcelPayslipSendState(); var queues = AddDelivery(raw, state, "Sent");
        var next = AddDelivery(raw, state, outcome == "Deferred" ? "Sent" : outcome);
        if (outcome == "Deferred") next[0].DeliveryStatus = "Deferred";
        if (outcome != "missing") queues.AddRange(next);
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(raw, state, queues);
        var item = Assert.Single(ExcelPayslipRepository.ReviewCorrectedDeliverySelection(current.Rows, Request(), statuses).Items);
        Assert.Equal("Skipped", item.Status);
    }

    [Fact]
    public void FrozenCorrectionReferencesLargeSavedSourceAndKeepsCurrentMetadataAndExactSavedSalary()
    {
        var (raw, current, source) = Fixture();
        // A large immutable dependency capture must not be copied into every
        // corrected delivery; its exact source witness still protects rendering.
        const string sourceMarker = "source-only-capacity-regression-";
        source.DependencySheets = [new() { SheetName = "Synthetic lookup", ColumnCount = 1, CompleteSheetCapture = true,
            Rows = [new() { SourceRow = 1, Cells = [new() { Value = sourceMarker + new string('Z', 1024 * 1024) }] }] }];
        var rawBefore = JsonSerializer.Serialize(raw); var sourceBefore = JsonSerializer.Serialize(source);
        var originalSalary = Salary(raw.Rows.Single()); current.Rows[0].MasterDetails = current.Rows[0].MasterDetails! with { Email = "" };
        var snapshot = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester");
        ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, raw, source);
        var frozen = ExcelPayslipPresentation.Current(Assert.Single(snapshot.Batch.Rows));
        Assert.Equal("Corrected Synthetic Employee", frozen.EmployeeName); Assert.Equal("source@example.test", frozen.Email);
        Assert.Equal("001234567890", frozen.Information.Single(f => f.Label == "UAN").Value);
        Assert.Equal("9000000999", frozen.Information.Single(f => f.Label == "Mobile No").Value);
        Assert.Equal("Historical office", frozen.Information.Single(f => f.Label == "Work Location").Value);
        Assert.Equal("480000", frozen.Information.Single(f => f.Label == "Annual CTC").Value);
        Assert.Equal(originalSalary, Salary(frozen)); Assert.DoesNotContain(frozen.Earnings, e => e.Label == "Changed salary");
        Assert.Equal(rawBefore, JsonSerializer.Serialize(raw)); Assert.Equal(sourceBefore, JsonSerializer.Serialize(source));
        var snapshotJson = JsonSerializer.SerializeToElement(snapshot, ExcelPayslipRepository.JsonOptions);
        Assert.False(snapshotJson.TryGetProperty("calculationSource", out _));
        Assert.DoesNotContain(sourceMarker, snapshotJson.GetRawText());
        Assert.True(snapshotJson.GetRawText().Length < 64 * 1024);
        var savedSourceWitness = snapshot.SourceCalculationSha256;
        Assert.Equal(64, savedSourceWitness.Length);
        current.Rows[0].MasterDetails = current.Rows[0].MasterDetails! with { EmployeeName = "Later master change", Email = "later@example.test" };
        current.Rows[0].Earnings[0].Amount = 777777; source.Rows[0].Cells[1]!.Value = "888888";
        Assert.Equal("Corrected Synthetic Employee", frozen.EmployeeName); Assert.Equal(originalSalary, Salary(frozen));
        Assert.Equal(savedSourceWitness, snapshot.SourceCalculationSha256);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, raw, source));
    }

    [Theory]
    [InlineData("client")]
    [InlineData("batch")]
    [InlineData("row")]
    [InlineData("salary")]
    [InlineData("metadata")]
    [InlineData("formula")]
    public void WorkerRejectsForgedOrTamperedCorrectionSnapshots(string change)
    {
        var (raw, current, source) = Fixture();
        var snapshot = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester");
        switch (change)
        {
            case "client": snapshot.ClientId++; break;
            case "batch": snapshot.BatchId = Guid.NewGuid().ToString("N"); break;
            case "row": snapshot.Batch.Rows[0].Id = "forged-row"; break;
            case "salary": snapshot.Batch.Rows[0].NetPay++; break;
            case "metadata": snapshot.Batch.Rows[0].EmployeeName = "Injected identity"; break;
            case "formula": source.Rows[0].Cells[1]!.FormulaText = "999999"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, raw, source));
    }

    [Fact]
    public void CorrectionRejectsMismatchedScopeAndNeverTrustsUnlinkedMasterMetadata()
    {
        var (raw, current, source) = Fixture();
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["other-row"], Guid.NewGuid().ToString("N"), "tester"));
        current.ClientId++;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester"));
        current.ClientId = raw.ClientId; current.Rows[0].MasterDetails = current.Rows[0].MasterDetails! with { Linked = false };
        var unlinked = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester");
        Assert.Equal("Captured Employee", unlinked.Batch.Rows.Single().EmployeeName);
        Assert.Equal("source@example.test", unlinked.Batch.Rows.Single().Email);
        Assert.Empty(unlinked.Batch.Rows.Single().Information.Single(f => f.Label == "UAN").Value);
    }

    [Fact]
    public void NormalRequestFingerprintRemainsLegacyCompatibleAndCorrectedPurposeCannotReuseItsReceipt()
    {
        var request = Request(false);
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            BatchId, request.Mode, request.IncludeSeal, request.AmountDecimalPlaces,
            RowIds = request.RowIds.OrderBy(id => id, StringComparer.Ordinal), Email = "",
            Overrides = request.EmailOverrides.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, Value = p.Value?.Trim().ToLowerInvariant() })
        }, ExcelPayslipRepository.JsonOptions))));
        Assert.Equal(legacy, ExcelPayslipRepository.SendFingerprint(BatchId, request));
        var receipt = JsonSerializer.Serialize(new { fingerprint = legacy, result = new ExcelPayslipDeliveryResult() }, ExcelPayslipRepository.JsonOptions);
        request.ResendCorrected = request.AcknowledgeResend = true;
        var corrected = ExcelPayslipRepository.SendFingerprint(BatchId, request); Assert.NotEqual(legacy, corrected);
        Assert.Equal(corrected, ExcelPayslipRepository.SendFingerprint(BatchId, Clone(request)));
        Assert.NotNull(ExcelPayslipRepository.RestoreSendRequest(receipt, corrected).Error);
    }

    [Fact]
    public void LaterWarningReviewKeepsCorrectionValidWhileMoneyIdentityAndFormulaChangesRemainBlocked()
    {
        var (raw, current, source) = Fixture();
        foreach (var row in new[] { raw.Rows[0], current.Rows[0] })
        {
            row.Earnings[1].Amount = -1; row.NetPay -= 1; row.DeclaredGross -= 1;
            row.Warnings = ExcelPayslipRepository.RowWarnings(row).ToList();
        }
        var snapshot = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester");
        var reviewedJson = ExcelPayslipRepository.PatchNegativeAmountReview(JsonSerializer.Serialize(raw, ExcelPayslipRepository.JsonOptions), raw.ClientId, raw.Id, "saved-row", "reviewer@example.test", DateTime.UtcNow);
        var reviewed = JsonSerializer.Deserialize<ExcelPayslipBatch>(reviewedJson, ExcelPayslipRepository.JsonOptions)!;
        Assert.DoesNotContain(ExcelPayslipRepository.NegativeAmountWarning, reviewed.Rows[0].Warnings);
        ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, reviewed, source);
        foreach (var mutate in new Action<ExcelPayslipBatch>[] { b => b.Rows[0].NetPay++, b => b.Rows[0].EmployeeCode = "another-employee" })
        {
            var changed = Clone(reviewed); mutate(changed);
            Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, changed, source));
        }
        var changedSource = Clone(source); changedSource.Rows[0].Cells[1]!.FormulaText = "999999";
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateCorrectionSnapshot(snapshot, reviewed, changedSource));
    }

    [Fact]
    public void CorrectionEvidenceSurvivesLegacySendStateJsonRoundTripWithoutReplacingOldDeliveryHistory()
    {
        var (raw, current, source) = Fixture(); var state = new ExcelPayslipSendState(); var queues = AddDelivery(raw, state, "Sent");
        var oldDoc = JsonSerializer.Serialize(Assert.Single(state.Deliveries.Values), ExcelPayslipRepository.JsonOptions);
        var snapshot = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], Guid.NewGuid().ToString("N"), "tester");
        state.Requests[ExcelPayslipRepository.CorrectionSnapshotPrefix + snapshot.Id] = JsonSerializer.SerializeToElement(snapshot, ExcelPayslipRepository.JsonOptions);
        // Existing serializers already preserve arbitrary JsonElement request entries;
        // no new top-level state property or replacement of the original batch is needed.
        var oldShapeJson = JsonSerializer.Serialize(new { state.Requests, state.Deliveries, state.Jobs }, ExcelPayslipRepository.JsonOptions);
        var restored = JsonSerializer.Deserialize<ExcelPayslipSendState>(oldShapeJson, ExcelPayslipRepository.JsonOptions)!;
        var again = JsonSerializer.Deserialize<ExcelPayslipSendState>(JsonSerializer.Serialize(restored, ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
        var restoredSnapshot = again.Requests[ExcelPayslipRepository.CorrectionSnapshotPrefix + snapshot.Id].Deserialize<ExcelPayslipCorrectionSnapshot>(ExcelPayslipRepository.JsonOptions)!;
        ExcelPayslipRepository.ValidateCorrectionSnapshot(restoredSnapshot, raw, source);
        Assert.Equal(oldDoc, JsonSerializer.Serialize(Assert.Single(again.Deliveries.Values), ExcelPayslipRepository.JsonOptions));
        Assert.Equal("Sent", Assert.Single(ExcelPayslipRepository.BuildDeliveryStatuses(raw, again, queues).Items).Status);
    }

    [Fact]
    public void ActualWorkerAttachmentRendersFrozenCurrentMetadataAndSavedAmounts()
    {
        var (raw, current, source) = Fixture();
        var requestId = Guid.NewGuid().ToString("N");
        var snapshot = ExcelPayslipRepository.CreateCorrectionSnapshot(raw, current, source, ["saved-row"], requestId, "tester");
        var request = Request(); request.RequestId = requestId;
        var ready = new ExcelPayslipDeliveryResult { Items = [new() { RowId = "saved-row", Status = "Ready", Email = "current@example.test" }] };
        var doc = Assert.Single(ExcelPayslipRepository.PrepareMailJobDocuments(current, current.Rows, request, ready, requestId, "tester", ExcelPayslipTests.MailTemplate, snapshot));
        doc.QueueId = 21;
        var queue = new NotificationQueueItem { Id = 21, ClientId = raw.ClientId, EventCode = NotificationRepository.ExcelPayslipEvent, ResourceType = "ExcelPayslipBatch", ResourceId = raw.Id + ":" + doc.Id, ToJson = "[\"current@example.test\"]", CcJson = "[]", BccJson = "[]" };
        var builder = new BodyBuilder(); NotificationRepository.AttachExcelPayslipPdf(builder, queue, doc, raw, source, snapshot);
        var attachment = Assert.IsType<MimePart>(Assert.Single(builder.Attachments)); using var bytes = new MemoryStream(); attachment.Content!.DecodeTo(bytes);
        using var pdf = PdfReader.Open(new MemoryStream(bytes.ToArray()), PdfDocumentOpenMode.Import);
        var text = string.Join('\n', pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(page => Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue)));
        Assert.Contains("Corrected Synthetic Employee", text); Assert.Contains("001234567890", text); Assert.Contains("1,234.57", text); Assert.Contains("Historical office", text);
        Assert.DoesNotContain("Changed salary", text); Assert.DoesNotContain("999,999", text);
        queue.ToJson = "[\"attacker@example.test\"]";
        Assert.Throws<InvalidOperationException>(() => NotificationRepository.AttachExcelPayslipPdf(new BodyBuilder(), queue, doc, raw, source, snapshot));
    }

    private static List<ExcelPayslipRepository.ExcelPayslipQueueStatus> AddDelivery(ExcelPayslipBatch batch, ExcelPayslipSendState state, string status)
    {
        var doc = NotificationRepository.PrepareExcelPayslipMailMetadata(batch, batch.Rows, "source@example.test", Guid.NewGuid().ToString("N"), "tester", false, 2, ExcelPayslipTests.MailTemplate);
        doc.QueueId = state.Deliveries.Count + 1; state.Deliveries.Add(doc.Id, doc);
        return [new() { Id = doc.QueueId, ClientId = batch.ClientId, EventCode = NotificationRepository.ExcelPayslipEvent, ResourceType = "ExcelPayslipBatch", ResourceId = batch.Id + ":" + doc.Id, Status = status, CreatedAt = doc.CreatedAtUtc, SentAt = status == "Sent" ? doc.CreatedAtUtc : null }];
    }
}
