using System.Text.Json;

namespace Payroll.API.Services;

// A response-only projection of the attendance already frozen in DetailsJson.
// Never re-read current attendance or infer physical presence from paid units.
internal static class SavedPayrollAttendancePresentation
{
    internal static int? PayrollDays(string detailsJson,int clientId,int employeeId,string period,decimal? present,decimal payable)
    {
        if(present.HasValue||string.IsNullOrWhiteSpace(detailsJson))return null;
        JsonDocument document;
        try{document=JsonDocument.Parse(detailsJson);}catch(JsonException){return null;}
        using(document)
        {
            if(document.RootElement.ValueKind!=JsonValueKind.Array)return null;
            static JsonElement Field(JsonElement item,string key)=>item.ValueKind==JsonValueKind.Object
                ?item.EnumerateObject().FirstOrDefault(p=>p.Name.Equals(key,StringComparison.OrdinalIgnoreCase)).Value:default;
            static string Text(JsonElement item)=>item.ValueKind==JsonValueKind.String?item.GetString()??"":"";
            var attendance=document.RootElement.EnumerateArray().Where(line=>Text(Field(line,"id"))=="ATTENDANCE_INPUT").ToArray();
            if(!attendance.Any(line=>Text(Field(line,"sourceType"))==SourcePayableAttendance.SourceType))return null;
            if(attendance.Length!=1)throw new InvalidOperationException("Saved payroll attendance presentation is ambiguous.");
            var line=attendance[0];var days=Field(line,"payrollDays");var paid=Field(line,"payableDays");var evidence=Field(line,"sourceEvidence");
            if(Text(Field(line,"category"))!="Summary"||Text(Field(line,"sourceType"))!=SourcePayableAttendance.SourceType
                ||days.ValueKind!=JsonValueKind.Number||!days.TryGetInt32(out var divisor)
                ||paid.ValueKind!=JsonValueKind.Number||!paid.TryGetDecimal(out var savedPaid)||savedPaid!=payable
                ||Field(line,"actualPresentDays").ValueKind!=JsonValueKind.Null||evidence.ValueKind!=JsonValueKind.Object)
                throw new InvalidOperationException("Saved payroll attendance presentation does not match its captured salary units.");
            SourcePayableAttendance.Read(evidence.GetRawText(),clientId,employeeId,period,divisor,payable);
            return divisor;
        }
    }
}
