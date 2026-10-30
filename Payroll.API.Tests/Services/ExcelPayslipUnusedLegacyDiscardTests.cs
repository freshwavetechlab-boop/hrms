using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipUnusedLegacyDiscardTests
{
    private static ExcelPayslipDeletePreview OlderUnused() => new()
    {
        BatchId = "synthetic-old", IsLegacy = true, CandidateCount = 1, PayslipRows = 830, CalculationSources = 1,
        Blockers = ["Legacy provenance needs review."]
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExplicitDisposalDoesNotRequireANewerVersionOrChangeNormalDeletion(int candidateCount)
    {
        var preview = OlderUnused();
        preview.CandidateCount = candidateCount;
        preview.CanDiscardUnusedLegacy = ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, false);
        Assert.True(preview.CanDiscardUnusedLegacy); Assert.False(preview.CanDelete); Assert.False(preview.LegacyReviewValid);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, false));
        ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, true);
        Assert.False(new ExcelPayslipDeleteRequest("token", preview.BatchId).DiscardUnusedLegacy);
    }

    [Theory]
    [InlineData("delivery")][InlineData("job")][InlineData("request")][InlineData("notification")]
    [InlineData("log")][InlineData("setup")][InlineData("dependent")][InlineData("consolidation")][InlineData("legacy-storage")][InlineData("invalid-parent")]
    [InlineData("unknown-state")][InlineData("non-legacy")][InlineData("already-reviewed")]
    public void EveryProtectedReferenceStillBlocksExplicitDisposal(string problem)
    {
        var preview = OlderUnused();
        switch (problem)
        {
            case "delivery": preview.DeliveryRecords = 1; break;
            case "job": preview.MailJobs = 1; break;
            case "request": preview.RequestReceipts = 1; break;
            case "notification": preview.Notifications = 1; break;
            case "log": preview.NotificationLogs = 1; break;
            case "setup": preview.MasterSetupReceipts = 1; break;
            case "dependent": preview.DerivedBatches = 1; break;
            case "consolidation": preview.ConsolidationReferences = 1; break;
            case "legacy-storage": preview.LegacyStorageRecords = 1; break;
            case "invalid-parent": preview.InvalidParentReferences = 1; break;
            case "non-legacy": preview.IsLegacy = false; break;
            case "already-reviewed": preview.LegacyReviewValid = true; break;
        }
        preview.CanDiscardUnusedLegacy = ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, problem == "unknown-state");
        Assert.False(preview.CanDiscardUnusedLegacy);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, true));
    }

    [Fact]
    public void NewDependenciesChangeTheConfirmationTokenAndRevokeTheDiscardChoice()
    {
        var preview = OlderUnused(); preview.CanDiscardUnusedLegacy = true;
        var before = ExcelPayslipRepository.DeletePreviewToken("{}", "{}", [], preview);
        preview.MasterSetupReceipts = 1;
        preview.CanDiscardUnusedLegacy = ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, false);
        Assert.False(preview.CanDiscardUnusedLegacy);
        Assert.NotEqual(before, ExcelPayslipRepository.DeletePreviewToken("{}", "{}", [], preview));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, true));
    }

    [Fact]
    public void OriginalBytesAndHistoricalCleanupReceiptsRemainRetained()
    {
        var preview = OlderUnused(); preview.OriginalLinks = 2; preview.OtherBatchOriginalLinks = 4; preview.CleanupAuditRecords = 3;
        Assert.True(ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, false));
        preview.CalculationSources = 0;
        Assert.True(ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, false));
    }
}
