using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class ExcelPayslipDeletionPolicy
{
    // Explicit disposal is different from proving that two financial sources are equivalent.
    // Any explicitly selected unused legacy version can use this mode, including the latest/only
    // version. This does not claim source equivalence or bypass any dependency guard.
    internal static bool AllowsUnusedLegacyDiscard(ExcelPayslipDeletePreview preview, bool unknownDeliveryState) =>
        preview.IsLegacy && !preview.LegacyReviewValid && !unknownDeliveryState
        && Blockers(preview, 1, false).Count == 0;

    internal static void RequireDeletionIntent(ExcelPayslipDeletePreview preview, bool discardUnusedLegacy)
    {
        if (discardUnusedLegacy)
        {
            if (!preview.CanDiscardUnusedLegacy)
                throw new InvalidOperationException("Only an explicitly confirmed unused legacy batch can be discarded. Refresh its dependencies; delivery, setup, source and audit references must remain protected.");
        }
        else if (!preview.CanDelete) throw new InvalidOperationException(string.Join(" ", preview.Blockers));
    }

    internal static List<string> Blockers(ExcelPayslipDeletePreview preview, int lifecycleVersion, bool unknownDeliveryState)
    {
        var blockers = new List<string>();
        if (lifecycleVersion != 1 && !preview.LegacyReviewValid) blockers.Add("Legacy provenance needs review. Verify a persisted parent or review a compatible retained final version below; missing linkage alone is not orphan evidence.");
        if (unknownDeliveryState) blockers.Add("Other delivery evidence exists; retain this batch.");
        if (preview.DeliveryRecords + preview.MailJobs + preview.RequestReceipts > 0) blockers.Add("Delivery/job/request evidence exists, including pending or completed work.");
        if (preview.Notifications + preview.NotificationLogs > 0) blockers.Add("Notification/delivery audit history exists.");
        if (preview.MasterSetupReceipts > 0) blockers.Add("Master setup receipts exist. Their employee/template/payroll provenance must be retained.");
        if (preview.DerivedBatches > 0) blockers.Add("Derived/corrected batches depend on this source batch.");
        if (preview.ConsolidationReferences > 0) blockers.Add("An active consolidation retains this canonical/source snapshot. Undo the grouping before independent cleanup; delivery and payroll provenance must still be retained.");
        if (preview.LegacyStorageRecords > 0) blockers.Add("Unconverted legacy storage/mail evidence exists. Complete its reviewed migration/conversion before cleanup; no raw keys are erased here.");
        if (preview.InvalidParentReferences > 0) blockers.Add("Recorded ancestry is missing, cross-client or cyclic. Reconcile the exact source references before cleanup; review cannot bypass this blocker.");
        return blockers;
    }
}
