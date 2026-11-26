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
        ["employeecode"]="employeecode",["empcode"]="employeecode",["employeeno"]="employeecode",["empno"]="employeecode",["employeenumber"]="employeecode",["empnumber"]="employeecode",["idno"]="employeecode",["employeename"]="employeename",["name"]="employeename",["conductorname"]="employeename"};
    private static bool VerifiedIdentity(ExcelPayslipRow row) =>
        row.Employment is { IdentityReviewRequired: false, EmployeeId: > 0 } employment &&
        row.MasterDetails?.EmployeeId == employment.EmployeeId;
    public static bool Linked(ExcelPayslipRow row) => VerifiedIdentity(row) && row.MasterDetails!.Linked;
    internal static bool IsEmployeeCodeLabel(string label)
    {
        var key = Key(label);
        return key != "idno" && Aliases.GetValueOrDefault(key) == "employeecode" && !IsHistoricalCodeLabel(label);
    }
    private static bool IsHistoricalCodeLabel(string label) =>
        Key(label) is "sourceemployeecode" or "sourcenewemployeecode" or "originalemployeecode" ||
        SourceKey(label) is "employeecodeold" or "employeenoold" or "employeenumberold" or "empcodeold" or "empnoold" or "empnumberold";
    private static List<ExcelPayslipText> InformationWithEmployeeCode(ExcelPayslipRow row, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !row.Information.Any(field => field.Value != code && IsEmployeeCodeLabel(field.Label)))
            return row.Information;
        return row.Information.Select(field => IsEmployeeCodeLabel(field.Label)
            ? new ExcelPayslipText { Label = field.Label, Value = code } : field).ToList();
    }
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
                var value = key switch
                {
                    _ when IsHistoricalCodeLabel(field.Label) => field.Value,
                    _ when canonical == "employeecode" => string.IsNullOrWhiteSpace(linkedDetails.EmployeeCode) ? field.Value : linkedDetails.EmployeeCode,
                    _ => fields.GetValueOrDefault("source:"+SourceKey(field.Label),fields.GetValueOrDefault(canonical,field.Value))
                };
                return new ExcelPayslipText{Label=field.Label,Value=value};
            }).ToList();
            foreach(var (label,key) in new[]{("Designation","designation"),("Department","department"),("Grade","grade"),("Work Location","worklocation"),("Email","email"),("Phone","phone"),("Annual CTC","annualctc")})
                if(!currentInformation.Any(f=>Aliases.GetValueOrDefault(Key(f.Label),Key(f.Label))==key)&&fields.TryGetValue(key,out var value)&&value.Length>0)
                    currentInformation.Add(new(){Label=label,Value=value});
            return currentInformation;
        }
        if (!VerifiedIdentity(row)) return row.Information;
        var designation = row.MasterDetails!.Designation.Trim();
        var showDesignation = designation.Length > 0 && !designation.Equals("Unassigned", StringComparison.OrdinalIgnoreCase);
        var codeFields = InformationWithEmployeeCode(row, row.Employment!.EmployeeCode.Trim());
        if (!showDesignation) return codeFields;
        bool IsDesignation(string label) => Key(label) is "designation" or "designationmanpowercategory" or "manpowercategory";
        var result = codeFields.Select(field => IsDesignation(field.Label)
            ? new ExcelPayslipText { Label = field.Label, Value = designation } : field).ToList();
        if(!result.Any(field=>IsDesignation(field.Label)))result.Add(new(){Label="Designation",Value=designation});
        return result;
    }

    public static ExcelPayslipRow Current(ExcelPayslipRow row)
    {
        if (!VerifiedIdentity(row)) return row;
        var linked = row.MasterDetails!.Linked ? row.MasterDetails : null;
        var code = linked is null ? row.Employment!.EmployeeCode.Trim() : linked.EmployeeCode;
        var information = linked is null ? InformationWithEmployeeCode(row, code) : Information(row).ToList();
        if (linked is null && (code.Length == 0 || row.EmployeeCode == code) && ReferenceEquals(information, row.Information)) return row;
        var salary = linked is {ProfileOnly:false, Salary:{Status:"Updated",NetPay:not null} currentSalary} ? currentSalary : null;
        return new(){Id=row.Id,SourceRow=row.SourceRow,
            EmployeeCode=linked is not {ProfileOnly:false} && string.IsNullOrWhiteSpace(code) ? row.EmployeeCode : code,
            EmployeeName=linked is null || linked.ProfileOnly && string.IsNullOrWhiteSpace(linked.EmployeeName) ? row.EmployeeName : linked.EmployeeName,
            Email=linked is null || linked.ProfileOnly && string.IsNullOrWhiteSpace(linked.Email) ? row.Email : linked.Email,
            Employment=row.Employment,MasterDetails=row.MasterDetails,Information=information,Earnings=salary?.Earnings??row.Earnings,
            Deductions=salary?.Deductions??row.Deductions,EmployerContributions=salary?.EmployerContributions??row.EmployerContributions,
            NetPay=salary?.NetPay??row.NetPay,DeclaredGross=salary?.Gross??row.DeclaredGross,DeclaredDeductions=salary?.TotalDeductions??row.DeclaredDeductions,
            Warnings=row.Warnings,WarningReview=linked is null ? row.WarningReview : null,
            WarningReviewToken=linked is null ? row.WarningReviewToken : null,NegativeAmountReview=row.NegativeAmountReview};
    }
    public static void RequireCurrentSalary(IEnumerable<ExcelPayslipRow> rows)
    {
        var blocked=rows.FirstOrDefault(r=>Linked(r)&&!r.MasterDetails!.ProfileOnly&&r.MasterDetails.Salary is {Status:"Review required"});
        if(blocked is not null)throw new InvalidOperationException($"{blocked.MasterDetails!.EmployeeCode}: Master salary needs review. {blocked.MasterDetails.Salary!.Message}");
    }
}
