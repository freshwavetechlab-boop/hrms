using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipRecipientTrackingTests
{
    private static ExcelPayslipBatch Batch() => new() { Id = Guid.NewGuid().ToString("N"), ClientId = 42, Month = "2026-09", Rows = [
        new() { Id = "a", EmployeeName = "Employee A", Email = "a@example.test" },
        new() { Id = "b", EmployeeName = "Employee B", Email = "" },
        new() { Id = "c", EmployeeName = "Employee C", Email = "c@example.test" }
    ] };

    private static (NotificationRepository.ExcelPayslipMailDocument Doc, ExcelPayslipRepository.ExcelPayslipQueueStatus Queue) Copy(ExcelPayslipBatch batch, IEnumerable<string> ids, bool request = false) {
        var doc = new NotificationRepository.ExcelPayslipMailDocument { Id = Guid.NewGuid().ToString("N"), QueueId = 88, ClientId = batch.ClientId, BatchId = batch.Id, Email = "coordinator@example.test", RowIds = ids.ToList(), CreatedAtUtc = DateTime.UtcNow };
        var queue = new ExcelPayslipRepository.ExcelPayslipQueueStatus { Id = 88, ClientId = batch.ClientId, EventCode = request ? NotificationRepository.ExcelPayslipEmailRequestPdfEvent : NotificationRepository.ExcelPayslipEvent,
            ResourceType = request ? "ExcelPayslipEmailRequest" : "ExcelPayslipBatch", ResourceId = batch.Id + ":" + doc.Id + (request ? ":fingerprint" : ""), Recipient = doc.Email, Status = "Sent", SentAt = DateTime.UtcNow, SentCopyStatus = "Saved" };
        return (doc, queue);
    }

    [Fact]
    public void LegacyCombinedCopyIsNotAnEmployeeSendButBlocksTheSameRecipientAcrossNewRequestIds() {
        var batch = Batch(); var (doc, queue) = Copy(batch, batch.Rows.Select(x => x.Id));
        var state = new ExcelPayslipSendState { Deliveries = new() { [doc.Id] = doc } };
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]);
        Assert.All(statuses.Items, row => { Assert.Equal("Pending", row.Status); Assert.True(row.CanSend); Assert.Equal("Combined", Assert.Single(row.RecipientDeliveries).Mode); });
        var individual = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, new() { Mode = "Individual" }, statuses);
        Assert.Equal(["Ready", "Error", "Ready"], individual.Items.Select(x => x.Status));
        var repeated = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, new() { Mode = "Combined", Email = doc.Email.ToUpperInvariant(), RequestId = Guid.NewGuid().ToString() }, statuses);
        Assert.All(repeated.Items, row => Assert.Equal("Skipped", row.Status));
        Assert.All(statuses.Items, row => Assert.True(ExcelPayslipRepository.CanQueueDelivery(row, "Combined", "another@example.test")));
        var unverified = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, []);
        Assert.All(unverified.Items, row => Assert.False(ExcelPayslipRepository.CanQueueDelivery(row, "Combined", "another@example.test")));
    }

    [Fact]
    public void MissingEmailRequestPdfAndCombinedSendShareRecipientCoverage() {
        var batch = Batch(); var (doc, queue) = Copy(batch, ["b"], true);
        var state = new ExcelPayslipSendState { Requests = new() { ["email-request:" + doc.Id] = JsonSerializer.SerializeToElement(doc, ExcelPayslipRepository.JsonOptions) } };
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]);
        var repeated = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, new() { Mode = "Combined", Email = doc.Email }, statuses);
        Assert.Equal(["Ready", "Skipped", "Ready"], repeated.Items.Select(x => x.Status));
        Assert.Equal("Pending", statuses.Items.Single(x => x.RowId == "b").Status);
        Assert.False(ExcelPayslipRepository.CanQueueDelivery(statuses.Items.Single(x => x.RowId == "b"), "Individual", doc.Email));
    }

    [Fact]
    public void ExplicitSingleRowCoordinatorCopyDoesNotBecomeAnIndividualDelivery() {
        var batch = Batch(); var (doc, queue) = Copy(batch, ["a"]); doc.DeliveryMode = "Combined";
        var statuses = ExcelPayslipRepository.BuildDeliveryStatuses(batch, new() { Deliveries = new() { [doc.Id] = doc } }, [queue]);
        Assert.True(statuses.Items[0].CanSend);
        doc.DeliveryMode = "Individual";
        var personal = ExcelPayslipRepository.BuildDeliveryStatuses(batch, new() { Deliveries = new() { [doc.Id] = doc } }, [queue]);
        Assert.False(personal.Items[0].CanSend);
        Assert.Equal("Sent", personal.Items[0].Status);
    }

    [Fact]
    public void CombinedJobCountsOneEmailSeparatelyFromItsPayslipsAndRequiresArchivalEvidence() {
        var batch = Batch(); var (doc, queue) = Copy(batch, batch.Rows.Select(x => x.Id));
        var job = new ExcelPayslipMailJobRecord { ClientId = batch.ClientId, BatchId = batch.Id, RowIds = doc.RowIds, Deliveries = [new() { Id = doc.Id, QueueId = queue.Id, RowCount = 3 }] };
        var summary = ExcelPayslipRepository.BuildMailJobSummary(job, [queue]);
        Assert.Equal(3, summary.Total); Assert.Equal(1, summary.TotalEmails); Assert.Equal(1, summary.AcceptedEmails); Assert.Equal(1, summary.SentCopySavedEmails); Assert.Equal(0, summary.DeliveredEmails);
        Assert.Equal("Combined", summary.Mode); Assert.Equal("Completed", summary.Status);
        queue.SentCopyStatus = "Unknown";
        var legacy = ExcelPayslipRepository.BuildMailJobSummary(job, [queue]);
        Assert.Equal("Needs attention", legacy.Status); Assert.Equal(1, legacy.SentCopyUnverifiedEmails); Assert.Equal(0, legacy.SentCopySavedEmails); Assert.Equal(1, legacy.AcceptedEmails);
        Assert.Contains("do not resend", legacy.Message);
        queue.Status = "Failed"; queue.SentAt = null;
        var failed = ExcelPayslipRepository.BuildMailJobSummary(job, [queue]);
        Assert.Equal(0, failed.AcceptedEmails); Assert.Equal(3, failed.Failed);
    }

    [Theory]
    [InlineData("Bounced", 3, 0, 0)]
    [InlineData("Deferred", 0, 3, 0)]
    [InlineData("Delivered", 0, 0, 1)]
    public void SmtpAndSentCopyFactsRemainVisibleAfterADeliveryReport(string outcome, int failed, int unknown, int delivered) {
        var batch = Batch(); var (doc, queue) = Copy(batch, batch.Rows.Select(x => x.Id)); queue.DeliveryStatus = outcome;
        var job = new ExcelPayslipMailJobRecord { ClientId = batch.ClientId, BatchId = batch.Id, RowIds = doc.RowIds, Deliveries = [new() { Id = doc.Id, QueueId = queue.Id, RowCount = 3 }] };
        var summary = ExcelPayslipRepository.BuildMailJobSummary(job, [queue]);
        Assert.Equal(1, summary.AcceptedEmails); Assert.Equal(1, summary.SentCopySavedEmails);
        Assert.Equal(failed, summary.Failed); Assert.Equal(unknown, summary.Unknown); Assert.Equal(delivered, summary.DeliveredEmails);
    }
}
