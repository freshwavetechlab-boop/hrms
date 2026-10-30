using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Services;

// This is a reviewed payroll input, not evidence of actual presence or leave.
public sealed record SourcePayableAttendanceEvidence(int Version,int ClientId,int EmployeeId,string PayPeriod,
    string BatchId,string RowId,string ImportId,string SourceHash,int PayrollDays,decimal PayableDays,
    decimal? ActualPresentDays=null,string PresentMeaning="NotMapped",string PayableMeaning="AlreadyPayable",string Mode="SourceSeed");

public static class SourcePayableAttendance
{
    public const string SourceType="ExcelPayableOnly";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    internal sealed record NullablePresenceColumn(string TableName,string ColumnName,string IsNullable,string DataType);

    internal static bool NullablePresenceSchemaReady(IEnumerable<NullablePresenceColumn> values)
    {
        var columns=values.Where(column=>column.TableName.Equals("employee_monthly_attendance",StringComparison.OrdinalIgnoreCase)&&column.ColumnName.Equals("present_days",StringComparison.OrdinalIgnoreCase)
            ||column.TableName.Equals("payrunemployees",StringComparison.OrdinalIgnoreCase)&&column.ColumnName.Equals("PresentDays",StringComparison.OrdinalIgnoreCase)).ToList();
        return columns.Count==2&&columns.Select(column=>column.TableName.ToUpperInvariant()).Distinct().Count()==2
            &&columns.All(column=>column.IsNullable.Equals("YES",StringComparison.OrdinalIgnoreCase)&&column.DataType.Equals("decimal",StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<bool> SchemaReadyAsync(MySqlConnection db,MySqlTransaction? transaction=null) =>
        NullablePresenceSchemaReady(await db.QueryAsync<NullablePresenceColumn>(@"SELECT TABLE_NAME TableName,COLUMN_NAME ColumnName,IS_NULLABLE IsNullable,DATA_TYPE DataType FROM information_schema.columns
WHERE TABLE_SCHEMA=DATABASE()
AND ((TABLE_NAME='employee_monthly_attendance' AND COLUMN_NAME='present_days')
 OR (TABLE_NAME='payrunemployees' AND COLUMN_NAME='PresentDays'))",transaction:transaction));

    public static string Encode(SourcePayableAttendanceEvidence evidence)
    {
        Validate(evidence,evidence.ClientId,evidence.EmployeeId,evidence.PayPeriod,evidence.PayrollDays,evidence.PayableDays);
        var text=JsonSerializer.Serialize(evidence,Json);
        if(text.Length>600)throw new InvalidOperationException("Reviewed payable-only attendance provenance exceeds its storage limit.");
        return text;
    }

    public static SourcePayableAttendanceEvidence Read(string remarks,int clientId,int employeeId,string period,int payrollDays,decimal payableDays)
    {
        SourcePayableAttendanceEvidence? evidence;
        try { evidence=JsonSerializer.Deserialize<SourcePayableAttendanceEvidence>(remarks,Json); }
        catch(JsonException) { throw new InvalidOperationException("Payable-only attendance has invalid reviewed source provenance. Review its saved-batch import before payroll."); }
        if(evidence is null)throw new InvalidOperationException("Payable-only attendance is missing reviewed source provenance.");
        Validate(evidence,clientId,employeeId,period,payrollDays,payableDays);
        return evidence;
    }

    private static void Validate(SourcePayableAttendanceEvidence e,int clientId,int employeeId,string period,int payrollDays,decimal payableDays)
    {
        if(e.Version!=1||e.ClientId<=0||e.EmployeeId<=0||e.ClientId!=clientId||e.EmployeeId!=employeeId||e.PayPeriod!=period
            ||!DateOnly.TryParseExact(e.PayPeriod+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)
            ||!Guid.TryParse(e.BatchId,out _)||!Guid.TryParse(e.ImportId,out _)||string.IsNullOrWhiteSpace(e.RowId)||e.RowId.Length>100
            ||!Regex.IsMatch(e.SourceHash??"",@"\A[a-fA-F0-9]{64}\z")||e.PayrollDays is <1 or >31||e.PayrollDays!=payrollDays
            ||e.PayableDays<0||e.PayableDays>e.PayrollDays||e.PayableDays!=payableDays||e.ActualPresentDays is not null
            ||e.Mode!="SourceSeed"||e.PresentMeaning!="NotMapped"||e.PayableMeaning!="AlreadyPayable")
            throw new InvalidOperationException("Payable-only attendance does not match its reviewed employee, client, month, source or day inputs. Actual presence remains unknown.");
    }

    public static void EnsureIndependent(IEnumerable<string?> expressions,bool hasDailyPolicy=false,bool hasSplitPf=false,bool hasLocationParts=false)
    {
        if(expressions.Any(expression=>Regex.IsMatch(expression??"",@"\bPRESENT_DAYS\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("This salary template uses PRESENT_DAYS. Record actual presence before payroll; payable days cannot replace unknown worked days.");
        if(hasDailyPolicy||hasSplitPf||hasLocationParts)
            throw new InvalidOperationException("This payroll requires dated attendance or reviewed location presence. Payable-only source totals cannot establish those facts; record actual attendance before calculation.");
    }
}
