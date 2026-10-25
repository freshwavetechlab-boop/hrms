using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipLegacyCleanupTests
{
    [Fact]
    public void ParentReviewRejectsSelfCyclesIndirectCyclesAndUnavailableClientAncestors()
    {
        var valid = new Dictionary<string, string[]> { ["child"] = ["parent"], ["parent"] = [] };
        Assert.Null(ExcelPayslipLegacyReview.ValidateParentGraph("child", "parent", valid));
        Assert.NotNull(ExcelPayslipLegacyReview.ValidateParentGraph("child", "not-recorded", valid));
        Assert.NotNull(ExcelPayslipLegacyReview.ValidateParentGraph("child", "child", new Dictionary<string, string[]> { ["child"] = ["child"] }));
        Assert.NotNull(ExcelPayslipLegacyReview.ValidateParentGraph("child", "parent", new Dictionary<string, string[]> { ["child"] = ["parent"], ["parent"] = ["grandparent"], ["grandparent"] = ["child"] }));
        Assert.NotNull(ExcelPayslipLegacyReview.ValidateParentGraph("child", "parent", new Dictionary<string, string[]> { ["child"] = ["parent"] }));
    }

    [Theory]
    [InlineData("Applied", "{}")][InlineData("Draft", "{\"createdEmployees\":1}")][InlineData("Draft", "[]")][InlineData("Draft", "null")][InlineData("Draft", "not-json")]
    public void AppliedOrNonemptySetupEvidenceCannotBeDiscarded(string status, string json) => Assert.False(ExcelPayslipLegacyReview.IsEmptyUnappliedDraft(status, json));

    [Fact]
    public void OnlyAnEmptyUnappliedDraftCanBeDiscarded() => Assert.True(ExcelPayslipLegacyReview.IsEmptyUnappliedDraft("Draft", "{}"));

    [Fact]
    public void NewJobsOrAncestryProblemsInvalidateDeletionAfterAValidLegacyReview()
    {
        var preview = new ExcelPayslipDeletePreview { BatchId = "old", LegacyReviewValid = true, OriginalLinks = 1, OtherBatchOriginalLinks = 2 };
        var token = ExcelPayslipRepository.DeletePreviewToken("{}", "{}", ["shared-original"], preview);
        Assert.Empty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
        preview.MailJobs = 1;
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
        Assert.NotEqual(token, ExcelPayslipRepository.DeletePreviewToken("{}", "{}", ["shared-original"], preview));
        preview.MailJobs = 0; preview.InvalidParentReferences = 1;
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
    }
    private static ExcelPayslipBatch Batch(string id, decimal amount = 100) => new()
    {
        Id = id, ClientId = 42, Month = "2026-09", SourceFileName = "Synthetic September.xlsx", SheetName = "Salary", HeaderRow = 2,
        CreatedAtUtc = new DateTime(2026, 10, id == "old" ? 1 : 2, 0, 0, 0, DateTimeKind.Utc),
        Rows = [new() { Id = Guid.NewGuid().ToString(), SourceRow = 3, EmployeeCode = "SYN-001", EmployeeName = "Synthetic Person", NetPay = amount, Earnings = [new() { Label = "Basic", Amount = amount }] }]
    };
    private static ExcelPayslipCalculationSource Source(string parent = "") => new()
    {
        SourceBatchId = parent, SheetName = "Salary", HeaderRow = 2, ColumnCount = 2,
        Columns = [new() { ColumnIndex = 0, SourceHeader = "Code", Kind = "employeeCode" }, new() { ColumnIndex = 1, SourceHeader = "Basic", Kind = "earning", ComponentId = "basic" }],
        Rows = [new() { SourceRow = 3, Cells = [new() { Value = "SYN-001" }, new() { Value = "100", FormulaText = "50*2" }] }]
    };

    [Fact]
    public void ExactSnapshotsWithDifferentGeneratedIdsProvideDuplicateEvidence()
    {
        var preview = ExcelPayslipLegacyReview.Compare(Batch("old"), Source(), Batch("keeper"), Source());
        Assert.True(preview.Eligible); Assert.True(preview.ExactSnapshot);
        Assert.Contains("ancestry is not inferred", preview.Evidence);
    }

    [Fact]
    public void CorrectedVersionRequiresStableCodeCoverageAndExplicitSourceReview()
    {
        var preview = ExcelPayslipLegacyReview.Compare(Batch("old"), Source(), Batch("keeper", 110), Source());
        Assert.True(preview.Eligible); Assert.False(preview.ExactSnapshot); Assert.Equal(1, preview.ChangedRows);
        Assert.Contains("operator must review", preview.Evidence);
    }

    [Theory]
    [InlineData("other-client")][InlineData("other-month")][InlineData("older-keeper")][InlineData("dependent-keeper")]
    [InlineData("missing-code")][InlineData("duplicate-code")][InlineData("missing-employee")][InlineData("changed-mapping")][InlineData("missing-source")]
    public void AmbiguousOrUnsafeVersionRelationshipsCannotBeResolved(string problem)
    {
        var old = Batch("old"); var keeper = Batch("keeper", 110); var oldSource = Source(); ExcelPayslipCalculationSource? keeperSource = Source();
        switch (problem)
        {
            case "other-client": keeper.ClientId = 43; break;
            case "other-month": keeper.Month = "2026-08"; break;
            case "older-keeper": keeper.CreatedAtUtc = old.CreatedAtUtc.AddDays(-1); break;
            case "dependent-keeper": keeperSource.SourceBatchId = old.Id; break;
            case "missing-code": old.Rows[0].EmployeeCode = ""; break;
            case "duplicate-code": keeper.Rows.Add(keeper.Rows[0]); break;
            case "missing-employee": keeper.Rows[0].EmployeeCode = "SYN-002"; break;
            case "changed-mapping": keeperSource.Columns[1].ComponentId = "other-basic"; break;
            case "missing-source": keeperSource = null; break;
        }
        Assert.False(ExcelPayslipLegacyReview.Compare(old, oldSource, keeper, keeperSource).Eligible);
    }

    [Fact]
    public void ReviewedLegacyEligibilityDoesNotOverrideAppliedDataDependentsOrLegacyMail()
    {
        var preview = new ExcelPayslipDeletePreview { LegacyReviewValid = true };
        Assert.Empty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
        preview.MasterSetupReceipts = 1;
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
        preview.MasterSetupReceipts = 0; preview.DerivedBatches = 1;
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
        preview.DerivedBatches = 0; preview.LegacyStorageRecords = 1;
        Assert.NotEmpty(ExcelPayslipDeletionPolicy.Blockers(preview, 0, false));
    }

    private static string UnqueuedState(string resultStatus = "Error", int clientId = 42, bool delivery = false) => JsonSerializer.Serialize(new
    {
        jobs = new Dictionary<string, object> { ["job-one"] = new { id = "job-one", clientId, batchId = "batch-one", deliveries = delivery ? new[] { new { id = "delivery-one", queueId = 12, rowCount = 1 } } : [] } },
        deliveries = new Dictionary<string, object>(),
        requests = new Dictionary<string, object> { ["job-one"] = new { fingerprint = "synthetic-request-hash", result = new { items = new[] { new { status = resultStatus } } } } }
    });

    [Fact]
    public void OnlyNeverQueuedAttemptsCanBeDiscardedAndForeignScopesNeverMatch()
    {
        Assert.True(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(), "job-one", 0, 0, 42, "batch-one"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(), "job-one", 1, 0, 42, "batch-one"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(), "job-one", 0, 1, 42, "batch-one"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(delivery: true), "job-one", 0, 0, 42, "batch-one"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(clientId: 43), "job-one", 0, 0, 42, "batch-one"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(), "job-one", 0, 0, 42, "other-batch"));
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob("{\"jobs\":null}", "job-one", 0, 0, 42, "batch-one"));
    }

    [Theory]
    [InlineData("Queued")][InlineData("Sent")][InlineData("Retry")][InlineData("Processing")][InlineData("Already queued")]
    public void DispatchStateCannotBeDisguisedAsAnUnqueuedFailure(string status) =>
        Assert.False(ExcelPayslipRepository.CanDiscardUnqueuedJob(UnqueuedState(status), "job-one", 0, 0, 42, "batch-one"));

    [Fact]
    public void SourceOrSnapshotChangesInvalidateRecordedEvidenceHashes()
    {
        var hash = ExcelPayslipLegacyReview.SnapshotHash("{\"netPay\":100}", "{\"formula\":\"50*2\"}");
        Assert.NotEqual(hash, ExcelPayslipLegacyReview.SnapshotHash("{\"netPay\":101}", "{\"formula\":\"50*2\"}"));
        Assert.NotEqual(hash, ExcelPayslipLegacyReview.SnapshotHash("{\"netPay\":100}", "{\"formula\":\"25*4\"}"));
    }

    [Fact]
    public async Task MissingCleanupConfirmationNeverReachesDatabase()
    {
        var repository = new ExcelPayslipRepository(new ConfigurationBuilder().Build(), null!, null!);
        var request = new ExcelPayslipCleanupActionRequest(Guid.NewGuid().ToString(), "discard-draft", new string('a', 64), "0123456789abcdef0123456789abcdef", "draft", "draft", "", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ResolveCleanupAsync(42, request.ConfirmBatchId, request, "synthetic-actor"));
    }
}
