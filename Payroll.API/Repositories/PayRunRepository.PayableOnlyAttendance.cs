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
        if(evidence is null||evidence.ClientId!=employee.ClientId||evidence.EmployeeId!=employee.Id||evidence.PayrollDays!=payrollDays||evidence.PayableDays!=payableDays)
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
}
