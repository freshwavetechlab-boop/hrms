using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static (DateTime From, DateTime To) ClosedHistoricalPeriod(string period, DateTime today)
    {
        if (!DateTime.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || from.Year < 1900 || from >= new DateTime(today.Year, today.Month, 1))
            throw new InvalidOperationException("Select a complete salary month before the current month. Current/future salary changes use the existing Master workflow.");
        return (from, from.AddMonths(1).AddDays(-1));
    }

    internal static void ValidateHistoricalSalaryRequest(HistoricalPeriodSalaryRequest request)
    {
        if(request?.Setup?.ReviewedSourceMonthlyCtcOperand==true)
            throw new InvalidOperationException("Normal source monthly CTC operands cannot authorize historical Employee payroll.");
        if(request?.Setup?.RecurringBlankInputReview is not null)
            throw new InvalidOperationException("Recurring blank operand review cannot authorize historical Employee payroll; it belongs to normal configuration only.");
        if(request?.Setup?.ConfigurationDifferenceReview is not null)
            throw new InvalidOperationException("Configuration monetary review cannot authorize historical Employee payroll. Use its separate native evidence and historical review.");
        if(request?.Setup?.ReportedPeriodInputs is not null)
        {
            ValidateReportedHistoricalSalaryRequest(request); return;
        }
        if (request?.Setup is not { } setup || request.Assignments is null || request.Assignments.Count is < 1 or > 100
            || request.SalaryTargetMode is not ("PreserveEffectiveHistoricalTarget" or "ReviewedInitialSourceTarget")
            || request.Assignments.Any(a => a is null || a.EmployeeId <= 0 || string.IsNullOrWhiteSpace(a.RowId)
                || a.RowId.Length > 100 || !Regex.IsMatch(a.TemplateId ?? "", @"^[0-9]{1,20}$"))
            || setup.RowIds is null || request.Assignments.Select(a => a.EmployeeId).Distinct().Count() != request.Assignments.Count
            || request.Assignments.Select(a => a.RowId).Distinct(StringComparer.Ordinal).Count() != request.Assignments.Count
            || !setup.RowIds.Order(StringComparer.Ordinal).SequenceEqual(request.Assignments.Select(a => a.RowId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Review one existing employee/template per unique saved source row; this lane never creates or aggregates employees.");
        ValidateRequest(setup);
        ValidateInitialSourceTargetReview(request);
        ValidateHistoricalLiteralSelfInputReview(request);
        ValidateHistoricalSourceIdentityReview(request);
        ValidateHistoricalPeriodProfessionalTaxReview(request);
        ValidateHistoricalAmountDifferenceReview(request);
        ValidateHistoricalNamedInputReview(request);
        if (setup.ImportAttendance || setup.ImportPayableOnlyAttendance || setup.OverrideSourcePayableAttendance || setup.LocationAllocations is not null
            || setup.CorrectionOfImportId is not null || setup.QuickPreparation is not null || setup.MonthlyCtcColumnIndex is not null && request.SalaryTargetMode!="ReviewedInitialSourceTarget"
            || setup.ReviewedExtraDutyUnits || setup.EmployeeDecisions.Any(d => d.AllowCreate)
            || setup.Components.Any(c => c.IsPeriodAdjustment || c.CalculationType is "Manual / Variable" or "Residual / Balancing"
                || c.ExemptWhenSourceZero || string.IsNullOrWhiteSpace(c.ExistingComponentId)))
            throw new InvalidOperationException("Historical salary review reuses existing components and templates, existing actual attendance and the effective historical annual target. New identities, attendance writes, CTC declarations, balancing, exemptions and frozen period outputs are outside this lane.");
        if (!setup.ReviewedFixedInputs || !setup.ReviewedPolicyScope || setup.TransferReviewVersion != 1 || setup.TemplateScopeReviewVersion != 1
            || setup.AttendanceReview is not { Mode: "VerifyExistingNative", Reviewed: true, PayableMeaning: "AlreadyPayable" })
            throw new InvalidOperationException("Review the independent source bases, unchanged native policy and exact existing-native attendance meanings before historical salary proof.");
        if (setup.Components.Select(c => c.ExpectedColumnIndex).Any(i => i is null)
            || setup.Components.Select(c => c.ExpectedColumnIndex).Distinct().Count() != setup.Components.Count)
            throw new InvalidOperationException("Every historical component needs one distinct saved financial output for complete source comparison.");
    }

    internal static IReadOnlyList<string> HistoricalSourceCoverage(ExcelPayslipCalculationSource source, ExcelPayrollSetupRequest request,
        IReadOnlyList<ResolvedComponent> components,IReadOnlySet<string>? verifiedLiteralSelfInputKeys=null)
    {
        var errors = new List<string>();
        var financial = source.Columns.Where(c => c.Kind is "earning" or "deduction" or "employer").ToArray();
        if (financial.Select(c => c.ColumnIndex).Distinct().Count() != financial.Length
            || !financial.Select(c => c.ColumnIndex).Order().SequenceEqual(components.Select(c => c.Definition.ExpectedColumnIndex!.Value).Order()))
            errors.Add("Historical comparison must cover every saved earning, deduction and employer output exactly once.");
        foreach (var resolved in components)
        {
            var component = resolved.Definition;
            var output = source.Columns.SingleOrDefault(c => c.ColumnIndex == component.ExpectedColumnIndex);
            var expected = component.Category switch { "Earning" or "Reimbursement" => "earning", "Deduction" => "deduction", "Benefit" => "employer", _ => "" };
            if (output is null || output.Kind != expected || request.SourceColumnRoles.GetValueOrDefault(output.ColumnIndex, output.Kind) != expected)
                errors.Add(component.Code + ": saved financial output classification differs; historical review cannot hide or reclassify saved pay.");
            if (component.SourceColumnIndex is int input)
            {
                if(verifiedLiteralSelfInputKeys?.Contains(component.Key)==true)continue;
                var column = source.Columns.SingleOrDefault(c => c.ColumnIndex == input);
                if (column is null || column.Kind is not ("info" or "ignore") || request.SourceColumnRoles.GetValueOrDefault(input, column.Kind) != "info"
                    || ProtectedRecurringInputHeader(column.SourceHeader + " " + column.Label)
                    || input == request.PayableDaysColumnIndex || input == request.PayrollDaysColumnIndex || input == request.PresentDaysColumnIndex)
                    errors.Add(component.Code + ": monthly/rate input must be an explicitly reviewed independent Information column; earned pay, identifiers and attendance are not salary bases.");
            }
        }
        return errors;
    }

    internal const string MissingHistoricalSourceIdentity = "A source-verified existing Master identity is required; a unique employee code or fuzzy name alone cannot authorize historical salary.";
    internal const string ChangedHistoricalSourceIdentity = "The source code/name differs from this employee and has no durable reviewed source link. Resolve identity before historical salary review.";

    internal static IReadOnlyList<string> HistoricalIdentityIssues(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        ExcelPayslipRow row, Identity target, IReadOnlyList<Identity> identities, HistoricalVerifiedSourceIdentity? reviewed = null)
    {
        var errors = new List<string>(); var anchors = 0;
        var cells = source.Rows.SingleOrDefault(r => r.SourceRow == row.SourceRow);
        if (cells is null) return ["The selected source identity row is missing."];
        static string Key(ExcelPayslipCalculationColumn column)
        {
            foreach (var heading in new[] { column.SourceHeader, column.Label })
            {
                var key = ExcelPayslipMasterSynchronization.ProfileIdentityKey(heading);
                if (key.Length > 0) return key;
                if (Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9]", "") is "adhaar" or "adhar" or "adhaarno" or "adharno" or "adhaarnumber" or "adharnumber") return "aadhaar";
            }
            return "";
        }
        static string Canonical(string? text, string kind)
        {
            var value = Regex.Replace((text ?? "").ToUpperInvariant(), @"[\s-]", "");
            if (kind != "pan" && value.Length > 0 && value.All(c => c == value[0])) return "";
            return Regex.IsMatch(value, kind == "pan" ? @"^[A-Z]{5}[0-9]{4}[A-Z]$" : kind == "aadhaar" ? @"^[2-9][0-9]{11}$" : @"^[0-9]{12}$") ? value : "";
        }
        foreach (var kind in new[] { "uan", "pan", "aadhaar" })
        {
            var values = source.Columns.Where(c => Key(c) == kind).Select(c => cells.Cells.ElementAtOrDefault(c.ColumnIndex))
                .Where(c => c is not null && string.IsNullOrWhiteSpace(c.FormulaText) && c.Error is null && c.CalculationError is null && !c.MissingCachedValue)
                .Select(c => Canonical(c!.Value, kind)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 1) errors.Add(kind + ": the source contains conflicting identity columns; resolve the exact reviewed column first.");
            foreach (var value in values)
            {
                var owners = identities.Where(e => Canonical(kind == "uan" ? e.Uan : kind == "pan" ? e.Pan : e.Aadhaar, kind) == value).Select(e => e.Id).Distinct().ToArray();
                if (owners.Length == 1 && owners[0] == target.Id) anchors++;
                else if (owners.Length > 0) errors.Add(kind + ": this identity is shared or belongs to a different employee; historical payroll cannot select an owner by code alone.");
            }
        }
        // Reuse the normal server-owned link validator, but accept only an
        // existing retained link, never a newly inferred code association.
        var retained = batch.ProfileLinks ?? [];
        var masters = identities.Select(identity => new ExcelPayslipRepository.EmploymentMaster { Id = identity.Id,
            ClientId = identity.ClientId, EmployeeCode = identity.EmployeeCode, Pan = identity.Pan, Aadhaar = identity.Aadhaar, Uan = identity.Uan }).ToArray();
        var durable = ExcelPayslipRepository.ProfileBindings(batch, source, masters).Any(link => retained.Contains(link)
            && link.EmployeeId == target.Id && link.RowId == row.Id && link.SourceRow == row.SourceRow);
        var bankReviewed = reviewed is not null && reviewed.Evidence.ClientId == batch.ClientId && reviewed.Evidence.EmployeeId == target.Id
            && reviewed.Evidence.RowId == row.Id && reviewed.Evidence.SourceRow == row.SourceRow;
        if (anchors == 0 && !durable && !bankReviewed) errors.Add(MissingHistoricalSourceIdentity);
        if (!durable && (Normalize(target.EmployeeCode) != Normalize(row.EmployeeCode)
            || Normalize(target.Name) != Normalize(row.EmployeeName) && !(bankReviewed && reviewed!.ReviewedNameChange)))
            errors.Add(ChangedHistoricalSourceIdentity);
        if (!target.EligibleForMonth) errors.Add("The employee is not employed in this source month.");
        return errors;
    }
}
