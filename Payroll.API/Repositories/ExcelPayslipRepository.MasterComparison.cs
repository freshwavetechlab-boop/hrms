using System.Globalization;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private sealed record ComparisonCode(int Id, string EmployeeCode);

    internal static ExcelPayslipMasterComparison MasterComparison(int clientId, string code, Employee employee, string workLocation)
    {
        if (employee.ClientId != clientId || ExcelPayslipVarianceService.NormalizeText(employee.EmployeeCode) != ExcelPayslipVarianceService.NormalizeText(code))
            throw new InvalidOperationException("The Master comparison target changed or belongs to another client.");
        var personal=employee.PersonalDetails??new(); var bank=employee.PaymentDetails??new();
        var fields=new Dictionary<string,string> {
            ["EmployeeCode"]=employee.EmployeeCode,["EmployeeName"]=(employee.FirstName+" "+employee.LastName).Trim(),
            ["FirstName"]=employee.FirstName,["LastName"]=employee.LastName,["Email"]=employee.WorkEmail,
            ["Department"]=employee.Department,["Designation"]=employee.Designation,["Grade"]=employee.Grade,["WorkLocation"]=workLocation,
            ["DateOfJoining"]=employee.DateOfJoining,["Gender"]=employee.Gender,["DateOfBirth"]=personal.DateOfBirth,
            ["Mobile"]=personal.Mobile,["PAN"]=personal.PanNumber,["Aadhaar"]=personal.AadhaarNumber,["UAN"]=personal.UanNumber,["ESIC"]=personal.EsicNumber,
            ["EmploymentType"]=personal.EmploymentType,["SkillCategory"]=personal.SkillCategory,["Address"]=personal.Address,
            ["City"]=personal.City,["District"]=personal.District,["State"]=personal.State,
            ["BankName"]=bank.BankName,["BankAccountNo"]=bank.BankAccountNo,["IFSC"]=bank.IfscCode,["PaymentMode"]=bank.PaymentMode,
            ["AnnualCTC"]=employee.AnnualCtc.ToString(CultureInfo.InvariantCulture)
        };
        foreach(var key in fields.Keys.ToArray()) fields[key]??="";
        return new(clientId,code,"Matched",fields);
    }

    // Called only for one clicked source row, behind Employee Master read permission.
    // No initialization, inferred person match, profile save or salary calculation occurs here.
    internal static ExcelPayslipMasterComparison SelectMasterComparisonFields(ExcelPayslipMasterComparison comparison,string? fields)
    {
        if(fields?.Length>1000)throw new InvalidOperationException("Choose a bounded set of Master comparison fields.");
        var selected=new[]{"EmployeeCode","EmployeeName","Email"}.Concat((fields??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.Ordinal);
        return comparison with {Fields=comparison.Fields.Where(field=>selected.Contains(field.Key)).ToDictionary(field=>field.Key,field=>field.Value)};
    }

    public async Task<ExcelPayslipMasterComparison> GetEmployeeMasterComparisonAsync(int clientId, string employeeCode,string? fields=null)
    {
        if(fields?.Length>1000)throw new InvalidOperationException("Choose a bounded set of Master comparison fields.");
        var normalized=ValidateCodePreview(new([employeeCode])).SingleOrDefault();
        if (normalized is null) throw new InvalidOperationException("Choose a source employee code.");
        await using var db=Db();await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        if (!await SupportedAsync(db,clientId,tx)) throw new InvalidOperationException("Select an active client.");
        var matches=(await db.QueryAsync<ComparisonCode>("SELECT Id,EmployeeCode FROM employees WHERE ClientId=@ClientId",new {ClientId=clientId},tx))
            .Where(row=>ExcelPayslipVarianceService.NormalizeText(row.EmployeeCode)==normalized).ToArray();
        if (matches.Length!=1) {await tx.RollbackAsync();return new(clientId,employeeCode,matches.Length==0?"Not found":"Ambiguous",[]);}
        var employee=await EmployeeRepository.LoadRecruitmentEmployeeAsync(db,matches[0].Id,tx)
            ?? throw new InvalidOperationException("The matched Employee Master is unavailable.");
        var location=await db.ExecuteScalarAsync<string?>("SELECT Name FROM worklocations WHERE Id=@Id AND ClientId=@ClientId",new {Id=employee.WorkLocationId,ClientId=clientId},tx)??"";
        var comparison=MasterComparison(clientId,employeeCode,employee,location);
        await tx.RollbackAsync();return SelectMasterComparisonFields(comparison,fields);
    }
}
