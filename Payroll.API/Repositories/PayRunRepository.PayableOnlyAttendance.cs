using System.Globalization;
using System.Text.Json;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    internal static bool HasUnknownPresenceSnapshot(string details)
    {
        using var document=JsonDocument.Parse(details);
        return document.RootElement.ValueKind==JsonValueKind.Array&&document.RootElement.EnumerateArray().Any(line=>
            line.TryGetProperty("Id",out var id)&&id.GetString()=="ATTENDANCE_INPUT"
            &&line.TryGetProperty("actualPresentDays",out var actual)&&actual.ValueKind==JsonValueKind.Null);
    }
    internal static decimal? ClampKnownPresence(decimal? actualPresent,int payrollDays) =>
        actualPresent.HasValue ? Math.Clamp(actualPresent.Value,0,payrollDays) : null;

    internal static decimal NativePayableUnits(PayRunAttendance row, int payrollDays) =>
        row.PayableOnlyEvidence is { ReviewedExtraDutyUnits: true } evidence
            && evidence.PayrollDays == payrollDays && evidence.PayableDays == row.PayableDays
            ? row.PayableDays : Math.Clamp(row.PayableDays, 0, payrollDays);

    internal static bool HasReviewedExtraDutySnapshot(string details, int clientId, int employeeId, string period, int days, decimal payable)
    {
        try
        {
            using var document = JsonDocument.Parse(details);
            var inputs = document.RootElement.EnumerateArray().Where(line => line.TryGetProperty("Id", out var id)
                && id.GetString() == "ATTENDANCE_INPUT").ToArray();
            if (inputs.Length != 1 || !inputs[0].TryGetProperty("sourceEvidence", out var source)) return false;
            return SourcePayableAttendance.Read(source.GetRawText(), clientId, employeeId, period, days, payable).ReviewedExtraDutyUnits;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    internal static void ReadPayableOnlyAttendance(PayRunAttendance row,int clientId,string payPeriod)
    {
        if(row.PresentDays.HasValue)
        {
            if(row.SourceType==SourcePayableAttendance.SourceType)
                throw new InvalidOperationException("Payable-only attendance must retain unknown actual presence; a numeric present value cannot be inferred from payable days.");
            return;
        }
        if(row.SourceType!=SourcePayableAttendance.SourceType)
            throw new InvalidOperationException("Actual present days are missing without a reviewed payable-only source import. Review attendance before payroll.");
        if(row.WorkingDays is <1 or >31||decimal.Truncate(row.WorkingDays)!=row.WorkingDays)
            throw new InvalidOperationException("Reviewed payable-only attendance requires a valid salary divisor.");
        row.PayableOnlyEvidence=SourcePayableAttendance.Read(row.Remarks,clientId,row.EmployeeId,payPeriod,(int)row.WorkingDays,row.PayableDays);
        row.InputSource="Reviewed source payable-only";
    }

    private static void ValidatePayableOnlyCalculation(PayRunSourceEmployee employee,PayrollSetupData setup,int payrollDays,decimal payableDays)
    {
        var evidence=employee.PayableOnlyAttendance;
        var pending=employee.ReportedPendingSeed;
        if(pending is not null || evidence?.Origin==SourcePayableAttendance.ClosedReportedOrigin)
            RequireReportedPayableScope(employee,payrollDays,payableDays);
        if(pending is null&&(evidence is null||evidence.ClientId!=employee.ClientId||evidence.EmployeeId!=employee.Id||evidence.PayrollDays!=payrollDays||evidence.PayableDays!=payableDays))
            throw new InvalidOperationException("Unknown actual presence requires matching reviewed payable-only attendance provenance. No present value was assumed.");
        var structure=setup.Structures.SingleOrDefault(template=>template.Active&&template.Id==employee.SalaryStructureId
            &&template.ClientId.Split([';',',','|']).Any(value=>value.Split(':')[0].Trim()==employee.ClientId.ToString(CultureInfo.InvariantCulture)));
        if(structure is null)throw new InvalidOperationException("Payable-only payroll requires an explicitly assigned active salary template for this client.");
        var expressions=structure.Lines.SelectMany(line=>
        {
            var component=setup.Components.SingleOrDefault(component=>component.Id==line.ComponentId&&component.Active)
                ??throw new InvalidOperationException("Payable-only payroll references an inactive or missing salary component.");
            var type=NormalizeCalculationType(FirstText(line.CalculationType,component.CalculationType));
            return type is "Formula" or "Residual / Balancing" or "Slab Based"
                ?new[] {ComponentFormula(line,component),FirstText(line.BaseComponent,component.BaseComponent)}
                :Array.Empty<string>();
        });
        SourcePayableAttendance.EnsureIndependent(expressions,employee.AttendancePolicySnapshot is not null,
            employee.PfPolicyPeriod?.Versions.Count>1,employee.LocationParts is not null);
    }

    private static void RequireReportedPayableScope(PayRunSourceEmployee employee,int payrollDays,decimal payableDays)
    {
        var proof=employee.NamedInputs?.ReportedPeriodProof;
        var native=proof?.NativeAttendance;
        if(proof is null || native is null || proof.Profile.TemplateId!=employee.SalaryStructureId || proof.Profile.ClientId!=employee.ClientId
            || native.EmployeeId!=employee.Id || native.ClientId!=employee.ClientId || native.Month!=proof.Bound.PayPeriod
            || native.PayrollDays!=payrollDays || native.PayableDays!=payableDays || native.PresentDays.HasValue)
            throw new InvalidOperationException("Closed reported payable-only payroll requires its authenticated exact v2 profile/history/source/identity/native receipt context; formatted remarks alone are not authorization.");
        ReportedPeriodCalculationContext.Validate(employee.NamedInputs!,[],payrollDays,payableDays);
        if(employee.ReportedPendingSeed is { } pending)
        {
            if(native.ProofKind!=ExcelPayrollSetupRepository.HistoricalPayableSeedPendingKind || pending.PendingNativeInput!=native
                || pending.TemplateId!=employee.SalaryStructureId || pending.BoundInputs.ProofHash!=proof.Bound.ProofHash)
                throw new InvalidOperationException("The internal proposed attendance proof changed; no native record or closed receipt has been claimed.");
        }
        else if(native.ProofKind!=ExcelPayrollSetupRepository.HistoricalPayableSeedSavedKind || employee.NamedInputs!.ManifestHash.Length!=64
            || employee.PayableOnlyAttendance is not {Version:3} evidence || evidence.Origin!=SourcePayableAttendance.ClosedReportedOrigin
            || evidence.ReportedBoundaryHash!=proof.Bound.ProofHash || evidence.SourceHash!=proof.Bound.SourceHash)
            throw new InvalidOperationException("The saved closed reported attendance lacks its exact protected assignment; no old Applied import or legacy salary fallback is accepted.");
    }
}
