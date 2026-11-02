using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static class ExcelPayslipMasterSynchronization
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json)))).ToLowerInvariant();
    public static string IdentityHash(ExcelPayslipRow row)=>Hash(new{row.SourceRow,row.EmployeeCode,row.EmployeeName});
    public static string SalaryHash(Employee employee,string setupJson,JsonObject? setupDocument=null)
    {
        var setup=setupDocument??JsonNode.Parse(setupJson) as JsonObject ?? new();
        var template=(setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(item=>item["id"]?.ToString()==employee.SalaryStructureId);
        var ids=(template?["lines"] as JsonArray ?? []).OfType<JsonObject>().Select(item=>item["componentId"]?.ToString()).ToHashSet();
        var components=(setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().Where(item=>ids.Contains(item["id"]?.ToString())).OrderBy(item=>item["id"]?.ToString()).ToArray();
        string Number(decimal value,int precision=4)=>decimal.Round(value,precision,MidpointRounding.AwayFromZero).ToString("G29",CultureInfo.InvariantCulture);
        return Hash(new{employee.SalaryStructureId,AnnualCtc=Number(employee.AnnualCtc,2),Inputs=employee.SalaryComponents.OrderBy(item=>item.Key).Select(item=>new{item.Key,Value=Number(item.Value)}).ToArray(),Template=template,Components=components,
            employee.Gender,employee.WorkLocationId,employee.PersonalDetails.DateOfBirth,employee.PersonalDetails.State,employee.PersonalDetails.UanNumber,employee.PersonalDetails.EsicNumber,
            EsicEmployee=Number(employee.PersonalDetails.EsicEmployee),Pt=Number(employee.PersonalDetails.PtLwfWorkmenComp),Tds=Number(employee.PersonalDetails.Tds),Recovery=Number(employee.PersonalDetails.Recovery)});
    }
    public static ExcelPayslipMasterDetails Details(Employee employee,string workLocation,bool linked)
    {
        var p=employee.PersonalDetails;var bank=employee.PaymentDetails;
        return new(employee.Id,employee.Designation){Linked=linked,EmployeeCode=employee.EmployeeCode,EmployeeName=$"{employee.FirstName} {employee.LastName}".Trim(),Email=employee.WorkEmail,
            AnnualCtc=employee.AnnualCtc,SalaryStructureId=employee.SalaryStructureId,Fields=new(StringComparer.OrdinalIgnoreCase){
                ["designation"]=employee.Designation,["department"]=employee.Department,["grade"]=employee.Grade,["worklocation"]=workLocation,
                ["email"]=employee.WorkEmail,["phone"]=p.Mobile,["uan"]=p.UanNumber,["pan"]=p.PanNumber,["aadhaar"]=p.AadhaarNumber,["esic"]=p.EsicNumber,
                ["bankname"]=bank.BankName,["accountno"]=bank.BankAccountNo,["ifsc"]=bank.IfscCode,["paymentmode"]=bank.PaymentMode,
                ["gender"]=employee.Gender,["joiningdate"]=employee.DateOfJoining,["birthdate"]=p.DateOfBirth,["employmenttype"]=p.EmploymentType,["skillcategory"]=p.SkillCategory,
                ["reportingmanagerid"]=employee.ReportingManagerId.ToString(CultureInfo.InvariantCulture),["employmentstatus"]=employee.EmploymentStatus,["lastworkingdate"]=employee.LastWorkingDate??"",
                ["address"]=p.Address,["permanentaddress"]=p.PermanentAddress,["correspondenceaddress"]=p.CorrespondenceAddress,["city"]=p.City,["district"]=p.District,["state"]=p.State,
                ["annualctc"]=employee.AnnualCtc.ToString(CultureInfo.InvariantCulture),["monthlyctc"]=decimal.Round(employee.AnnualCtc/12m,2).ToString(CultureInfo.InvariantCulture)}};
    }
}
