using System.Text.Json;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    private sealed class CoverageRun { public int Id{get;set;}public string PayPeriod{get;set;}="";public string Status{get;set;}=""; }
    private sealed class CoverageEmployee { public int ClientId{get;set;}public int PayRunId{get;set;}public decimal Gross{get;set;}public decimal Deductions{get;set;}public decimal NetPay{get;set;}public string DetailsJson{get;set;}=""; }
    private static string FinancialCoverageMapHash(PayslipDesignProfile profile)=>Hash(JsonSerializer.Serialize(profile.Fields.Where(f=>f.Kind!="info")
        .OrderBy(f=>f.SourceColumn).Select(f=>new{f.SourceColumn,f.Label,f.Kind,f.NativeCode}),Json));

    private static async Task RequireApprovedFinancialCoverageAsync(MySqlConnection db,MySqlTransaction tx,
        PayslipDesignTemplate template,PayslipDesignProfile profile,PayslipDesignTemplate? previousTemplate,PayslipDesignProfile? previous)
    {
        if(!template.Active)return;
        var financialChange=previous is null||FinancialCoverageMapHash(previous)!=FinancialCoverageMapHash(profile);
        var activation=previousTemplate is not{Active:true,Theme:NativePayslipDesignService.Theme};
        var widening=previous?.PayPeriods is not null&&(profile.PayPeriods is null||profile.PayPeriods.Except(previous.PayPeriods,StringComparer.Ordinal).Any());
        if(!financialChange&&!activation&&!widening)return;
        var scope=profile.PayPeriods is null?"":" AND PayPeriod IN @Periods";
        if(!financialChange&&!activation&&previous?.PayPeriods is not null)scope+=" AND PayPeriod NOT IN @PreviousPeriods";
        // Lock relevant headers, including Draft headers, before reading current
        // approved rows. An approval cannot race the coverage review. Existing
        // client/period index and the caller's client lock keep this scoped.
        var runs=(await db.QueryAsync<CoverageRun>("SELECT Id,PayPeriod,Status FROM payruns WHERE ClientId=@ClientId"+scope+" ORDER BY Id LIMIT 10001 FOR UPDATE",
            new{template.ClientId,Periods=profile.PayPeriods,PreviousPeriods=previous?.PayPeriods},tx)).ToArray();
        if(runs.Length>10000)throw CoverageBound();
        var approved=runs.Where(r=>r.Status is "Approved" or "Partially Paid" or "Paid").ToDictionary(r=>r.Id);
        if(approved.Count==0)return;
        var employees=(await db.QueryAsync<CoverageEmployee>("SELECT ClientId,PayRunId,GrossPay+OneTimeEarnings Gross,StatutoryDeductions+OneTimeDeductions Deductions,NetPay,CAST(DetailsJson AS CHAR) DetailsJson FROM payrunemployees WHERE PayRunId IN @Ids AND IsSkipped=FALSE ORDER BY Id LIMIT 5001 FOR UPDATE",new{Ids=approved.Keys.ToArray()},tx)).ToArray();
        if(employees.Length>5000)throw CoverageBound();
        var mapped=profile.Fields.Where(f=>f.Kind!="info").Select(f=>(f.Kind,f.NativeCode)).ToHashSet();
        var missing=new HashSet<string>(StringComparer.Ordinal);var lineCount=0;
        foreach(var employee in employees)
        {
            if(employee.ClientId!=template.ClientId)throw new InvalidOperationException("An approved employee belongs to another client; no payslip design was saved.");
            using var document=JsonDocument.Parse(employee.DetailsJson);
            if(document.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("Approved payroll component evidence is invalid; no design was saved.");
            decimal gross=0,deductions=0;var financialCount=0;
            foreach(var line in document.RootElement.EnumerateArray())
            {
                if(++lineCount>100000)throw CoverageBound();
                string Text(string name)
                {
                    if(line.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Approved payroll component evidence is malformed; no design was saved.");
                    var properties=line.EnumerateObject().Where(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(properties.Length>1)throw new InvalidOperationException("Approved payroll component evidence is ambiguous; no design was saved.");
                    return properties.Length==0?"":properties[0].Value.ToString();
                }
                var category=Text("Category");if(category.Equals("Summary",StringComparison.OrdinalIgnoreCase))continue;
                var amount=Text("amount");
                using var number=JsonDocument.Parse(amount);
                var kind=NativePayslipDesignService.LineKind(category);var code=Text("Id");
                if(kind.Length==0||number.RootElement.ValueKind!=JsonValueKind.Number
                    ||!decimal.TryParse(amount,NumberStyles.Float,CultureInfo.InvariantCulture,out var earned)
                    ||!NativeRecaptureJson.Equivalent(number.RootElement.GetRawText(),earned.ToString("G29",CultureInfo.InvariantCulture)))
                    throw new InvalidOperationException("Approved payroll has an unsupported or nonnumeric component; no design was saved.");
                var monthly=Text("monthlyAmount");using var monthlyNumber=JsonDocument.Parse(monthly);
                if(monthlyNumber.RootElement.ValueKind!=JsonValueKind.Number||!decimal.TryParse(monthly,NumberStyles.Float,CultureInfo.InvariantCulture,out var rate)
                    ||!NativeRecaptureJson.Equivalent(monthlyNumber.RootElement.GetRawText(),rate.ToString("G29",CultureInfo.InvariantCulture)))
                    throw new InvalidOperationException("Approved payroll monthly component evidence is incomplete; no design was saved.");
                financialCount++;if(kind=="earning")gross+=earned;else if(kind=="deduction")deductions+=earned;
                if(earned==0)continue;
                if(code.Length==0||!mapped.Contains((kind,code)))missing.Add(approved[employee.PayRunId].PayPeriod+": "+(code.Length==0?"missing code":code)+" ("+category+")");
            }
            if(financialCount==0||gross!=employee.Gross||deductions!=employee.Deductions||gross-deductions!=employee.NetPay)
                throw new InvalidOperationException("Approved payroll's complete component totals are missing or differ from saved gross/deductions/net; no design was saved.");
        }
        if(missing.Count>0)throw new InvalidOperationException("This design would omit approved payroll components: "+string.Join(", ",missing.Order(StringComparer.Ordinal).Take(8))+". Choose Selected payroll months with compatible components or review the complete financial mappings. No design was saved.");
        static InvalidOperationException CoverageBound()=>new("Approved payroll coverage exceeds the bounded review. Choose Selected payroll months with fewer records; no approved employees or components were omitted and no design was saved.");
    }
}
