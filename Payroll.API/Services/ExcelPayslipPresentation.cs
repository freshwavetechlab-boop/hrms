using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Current Master is a response-only projection. Never write it over the source.
public static class ExcelPayslipPresentation
{
    public static string Key(string label)=>Regex.Replace(Regex.Replace(label.Trim(),@"\s*\([A-Za-z]{1,3}\)\s*$",""),"[^a-zA-Z0-9]","").ToLowerInvariant();
    public static string SourceKey(string label)=>Regex.Replace(label,"[^a-zA-Z0-9]","").ToLowerInvariant();
    private static readonly Dictionary<string,string> Aliases=new(StringComparer.OrdinalIgnoreCase){
        ["contactnumber"]="phone",["universalaccountnumber"]="uan",["universalaccountnumberuan"]="uan",["esino"]="esic",["esinumber"]="esic",
        ["designationmanpowercategory"]="designation",["manpowercategory"]="designation",["mobileno"]="phone",["mobilenumber"]="phone",["mobile"]="phone",["phoneno"]="phone",["phonenumber"]="phone",["contactno"]="phone",
        ["emailid"]="email",["workemail"]="email",["emailaddress"]="email",["uanno"]="uan",["uannumber"]="uan",["panno"]="pan",["pannumber"]="pan",["aadhar"]="aadhaar",["aadhaarno"]="aadhaar",["aadhaarnumber"]="aadhaar",
        ["esicno"]="esic",["esicnumber"]="esic",["bank"]="bankname",["accountnumber"]="accountno",["bankaccountno"]="accountno",["bankaccountnumber"]="accountno",["acno"]="accountno",["ifsccode"]="ifsc",
        ["location"]="worklocation",["locationnametehsilsubtehsil"]="worklocation",["placeofposting"]="worklocation",["posting"]="worklocation",["doj"]="joiningdate",["dateofjoining"]="joiningdate",["dob"]="birthdate",["dateofbirth"]="birthdate",["ctc"]="annualctc",["annualsalary"]="annualctc",
        ["employeecode"]="employeecode",["employeeno"]="employeecode",["idno"]="employeecode",["employeename"]="employeename",["name"]="employeename",["conductorname"]="employeename"};
    public static bool Linked(ExcelPayslipRow row)=>row.MasterDetails is {Linked:true} details&&row.Employment is {IdentityReviewRequired:false} employment&&employment.EmployeeId==details.EmployeeId;
    public static IReadOnlyList<ExcelPayslipText> Information(ExcelPayslipRow row)
    {
        if(Linked(row))
        {
            var linkedDetails=row.MasterDetails!;
            var fields=new Dictionary<string,string>(linkedDetails.Fields,StringComparer.OrdinalIgnoreCase){["employeecode"]=linkedDetails.EmployeeCode,["employeename"]=linkedDetails.EmployeeName};
            if(linkedDetails.ProfileOnly)
            {
                var hasPeriodLocation=row.Information.Any(field=>Aliases.GetValueOrDefault(Key(field.Label),Key(field.Label))=="worklocation"&&!string.IsNullOrWhiteSpace(field.Value));
                foreach(var key in fields.Keys.Where(key=>string.IsNullOrWhiteSpace(fields[key])||key is "annualctc" or "monthlyctc"||key.StartsWith("source:",StringComparison.OrdinalIgnoreCase)||key=="worklocation"&&hasPeriodLocation).ToArray())fields.Remove(key);
            }
            var currentInformation=row.Information.Select(field=>{
                var key=Key(field.Label);var canonical=Aliases.GetValueOrDefault(key,key);
                return new ExcelPayslipText{Label=field.Label,Value=fields.GetValueOrDefault("source:"+SourceKey(field.Label),fields.GetValueOrDefault(canonical,field.Value))};
            }).ToList();
            foreach(var (label,key) in new[]{("Designation","designation"),("Department","department"),("Grade","grade"),("Work Location","worklocation"),("Email","email"),("Phone","phone"),("Annual CTC","annualctc")})
                if(!currentInformation.Any(f=>Aliases.GetValueOrDefault(Key(f.Label),Key(f.Label))==key)&&fields.TryGetValue(key,out var value)&&value.Length>0)
                    currentInformation.Add(new(){Label=label,Value=value});
            return currentInformation;
        }
        var designation=row.MasterDetails is { } details && row.Employment is { IdentityReviewRequired:false, EmployeeId: >0 } employment
            && details.EmployeeId==employment.EmployeeId ? details.Designation.Trim() : "";
        if(designation.Length==0 || designation.Equals("Unassigned",StringComparison.OrdinalIgnoreCase))return row.Information;
        bool IsDesignation(string label)
        {
            var key=Regex.Replace(Regex.Replace(label.Trim(),@"\s*\([A-Za-z]{1,3}\)\s*$",""),"[^a-zA-Z0-9]","").ToLowerInvariant();
            return key is "designation" or "designationmanpowercategory" or "manpowercategory";
        }
        var result=row.Information.Select(field=>new ExcelPayslipText{Label=field.Label,Value=IsDesignation(field.Label)?designation:field.Value}).ToList();
        if(!result.Any(field=>IsDesignation(field.Label)))result.Add(new(){Label="Designation",Value=designation});
        return result;
    }

    public static ExcelPayslipRow Current(ExcelPayslipRow row)
    {
        if(!Linked(row))return row;
        var d=row.MasterDetails!;var salary=!d.ProfileOnly&&d.Salary is {Status:"Updated",NetPay:not null} s?s:null;
        return new(){Id=row.Id,SourceRow=row.SourceRow,EmployeeCode=d.ProfileOnly&&string.IsNullOrWhiteSpace(d.EmployeeCode)?row.EmployeeCode:d.EmployeeCode,EmployeeName=d.ProfileOnly&&string.IsNullOrWhiteSpace(d.EmployeeName)?row.EmployeeName:d.EmployeeName,Email=d.ProfileOnly&&string.IsNullOrWhiteSpace(d.Email)?row.Email:d.Email,
            Employment=row.Employment,MasterDetails=d,Information=Information(row).ToList(),Earnings=salary?.Earnings??row.Earnings,
            Deductions=salary?.Deductions??row.Deductions,EmployerContributions=salary?.EmployerContributions??row.EmployerContributions,
            NetPay=salary?.NetPay??row.NetPay,DeclaredGross=salary?.Gross??row.DeclaredGross,DeclaredDeductions=salary?.TotalDeductions??row.DeclaredDeductions,
            Warnings=row.Warnings,NegativeAmountReview=row.NegativeAmountReview};
    }
    public static void RequireCurrentSalary(IEnumerable<ExcelPayslipRow> rows)
    {
        var blocked=rows.FirstOrDefault(r=>Linked(r)&&!r.MasterDetails!.ProfileOnly&&r.MasterDetails.Salary is {Status:"Review required"});
        if(blocked is not null)throw new InvalidOperationException($"{blocked.MasterDetails!.EmployeeCode}: Master salary needs review. {blocked.MasterDetails.Salary!.Message}");
    }
}
