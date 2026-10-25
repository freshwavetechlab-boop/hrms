using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class ExcelPayslipDeletionPolicy
{
    internal static List<string> Blockers(ExcelPayslipDeletePreview preview, int lifecycleVersion, bool unknownDeliveryState)
    {
        var blockers = new List<string>();
        if (lifecycleVersion != 1 && !preview.LegacyReviewValid) blockers.Add("Legacy provenance needs review. Verify a persisted parent or review a compatible retained final version below; missing linkage alone is not orphan evidence.");
        if (unknownDeliveryState) blockers.Add("Other delivery evidence exists; retain this batch.");
        if (preview.DeliveryRecords + preview.MailJobs + preview.RequestReceipts > 0) blockers.Add("Delivery/job/request evidence exists, including pending or completed work.");
        if (preview.Notifications + preview.NotificationLogs > 0) blockers.Add("Notification/delivery audit history exists.");
        if (preview.MasterSetupReceipts > 0) blockers.Add("Master setup receipts exist. Their employee/template/payroll provenance must be retained.");
        if (preview.DerivedBatches > 0) blockers.Add("Derived/corrected batches depend on this source batch.");
        if (preview.LegacyStorageRecords > 0) blockers.Add("Unconverted legacy storage/mail evidence exists. Complete its reviewed migration/conversion before cleanup; no raw keys are erased here.");
        if (preview.InvalidParentReferences > 0) blockers.Add("Recorded ancestry is missing, cross-client or cyclic. Reconcile the exact source references before cleanup; review cannot bypass this blocker.");
        return blockers;
    }
}
