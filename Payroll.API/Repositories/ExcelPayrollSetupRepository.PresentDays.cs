using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string PayableOnlyProofStatus = "Reviewed payable-only source seed - native pending";

    internal static string? PayableOnlyRequestError(ExcelPayrollSetupRequest request)
    {
        if (!request.ImportPayableOnlyAttendance) return null;
        if (request.TransferReviewVersion != 1 || !request.ImportAttendance || request.PresentDaysColumnIndex is not null
            || request.AttendanceReview is not { Mode: "SourceSeed", Reviewed: true, PresentMeaning: "NotMapped", PayableMeaning: "AlreadyPayable" })
            return "Payable-only attendance requires explicit source-seed import with reviewed already-payable units and no mapped actual-present column. It never certifies actual presence.";
        try
        {
            SourcePayableAttendance.EnsureIndependent(request.Components.SelectMany(component =>
                new[] { component.Formula, component.BaseComponent }.Concat(component.RowFormulas.Values)),
                hasLocationParts: request.LocationAllocations is not null);
        }
        catch (InvalidOperationException error) { return error.Message; }
        return null;
    }

    // CalculateRow may use payable as a draft arithmetic fallback. It is never worked-day evidence.
    internal static void FinalizePresentDaysEvidence(Plan plan, ExcelPayrollSetupRequest request, int clientId)
    {
        var sourceHash = Hash(JsonSerializer.Serialize(new { plan.Batch, plan.Source }, Json));
        foreach (var row in plan.Rows.Where(row => row.Preview.RowId == row.RowId))
        {
            var evidence = DescribePresentDays(plan, row, request, clientId, sourceHash);
            row.Preview.PresentDaysEvidence = evidence;
            if (!request.ImportAttendance || row.KeepExisting || evidence.ActualWorkedReviewed
                || evidence.Status == "ReviewedPayableOnlyUnknownPresence") continue;
            const string message = "Source-month attendance cannot be imported without a mapped, reviewed actual-worked column. Map that source column and review its meaning, or turn off Import source-month attendance to retain native attendance. Payable days may include paid leave or weekly offs and cannot be saved as actual present days by assumption.";
            if (!plan.Preview.Issues.Any(issue => issue.RowId == row.RowId && issue.Code == "Attendance mapping" && issue.Message == message))
                plan.Preview.Issues.Add(new("Error", message, row.RowId, "Attendance mapping"));
        }
        if (plan.Preview.Transfer is not null && !plan.Preview.CanApply)
            plan.Preview.Transfer.PreparationStatus = "Blocked";
    }

    private static ExcelPayrollPresentDaysEvidence DescribePresentDays(Plan plan, PlannedRow row,
        ExcelPayrollSetupRequest request, int clientId, string sourceHash)
    {
        var column = request.PresentDaysColumnIndex;
        var cell = column is int c ? TransferLetter(c) + row.SourceRow : null;
        ExcelPayrollPresentDaysEvidence Result(decimal? value, bool reviewed, string status, string message) =>
            new(column, cell, value, row.Preview.PresentDays, reviewed, status, message);
        if (column is null)
        {
            if (request.ImportPayableOnlyAttendance)
            {
                var proof = ProveAttendanceInput(plan, row, request, clientId, sourceHash);
                return proof.Status == PayableOnlyProofStatus
                    ? Result(null, false, "ReviewedPayableOnlyUnknownPresence", "Reviewed source payable units may be saved with unknown actual presence. The numeric draft present input remains an arithmetic fallback, not recorded worked days or leave entitlement.")
                    : Result(null, false, "PayableOnlyReviewBlocked", proof.Reason);
            }
            return Result(null, false, "NotMapped", "Actual worked days are not mapped. The draft uses payable days only as a calculation fallback; this is not evidence of worked days or CL. Map and review an actual worked column, or retain existing native attendance by turning off attendance import.");
        }
        var source = plan.Source.Rows.SingleOrDefault(source => source.SourceRow == row.SourceRow);
        if (source is null) return Result(null, false, "SourceUnavailable", "The mapped saved source row is unavailable. Refresh the saved-batch preview; no worked-day value is inferred.");
        decimal value;
        try { value = ReadNumber(source, column.Value); }
        catch (InvalidOperationException) { return Result(null, false, "SourceUnavailable", "The mapped worked-day cell is missing, blank or invalid. Supply/review the correct saved source value; missing does not mean zero."); }
        if (value != row.Preview.PresentDays)
            return Result(value, false, "SourceChanged", "The mapped source cell and draft present-days input differ. Refresh and review the current saved-batch mapping.");
        var review = request.AttendanceReview;
        var reviewed = review is { Reviewed: true, PresentMeaning: "ActualWorked", PayableMeaning: "AlreadyPayable" }
            && review.BoundSourceInputs == AttendanceBinding(request) && review.SourceHash == sourceHash
            && review.SourceMonth == plan.Batch.Month && plan.Batch.ClientId == clientId;
        if (reviewed)
        {
            try
            {
                var cycle = PayRunRepository.NativeAttendanceCycle(plan.Batch.Month, request.AttendanceCycleStartDay, request.AttendanceCycleEndDay);
                reviewed = review!.CycleStart == cycle.Start && review.CycleEnd == cycle.End;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException) { reviewed = false; }
        }
        return reviewed
            ? Result(value, true, "ReviewedWorkedDays", "This saved source cell is reviewed as actual worked days for the current client, source fingerprint and cycle. Original/dependency and native attendance certification remain separate checks.")
            : Result(value, false, "MeaningReviewRequired", "A numeric source cell is mapped, but its actual-worked meaning, source fingerprint or cycle review is missing/stale. Refresh and review the day mapping; payable totals alone cannot establish actual worked days.");
    }
}
