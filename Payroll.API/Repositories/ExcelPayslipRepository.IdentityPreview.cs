using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static void ValidateIdentityPreview(ExcelPayslipIdentityPreviewRequest request)
    {
        if(request.Rows is null||request.Rows.Count>1000||request.Rows.Any(row=>row is null||row.SourceRow<1||
            new[]{row.EmployeeCode,row.EmployeeName,row.Email,row.Mobile,row.Uan,row.BankAccount,row.WorkLocation,row.Department}.Any(value=>value is null||value.Length>300||value.Any(c=>char.IsControl(c)&&!char.IsWhiteSpace(c)))
            ||row.ConflictingFields?.Any(field=>!new[]{"employeeCode","employeeName","email","mobile","uan","bankAccount","workLocation","department"}.Contains(field))==true||row.ConflictingFields?.Count>8)
            ||request.Rows.Select(row=>row.SourceRow).Distinct().Count()!=request.Rows.Count)
            throw new InvalidOperationException("Preview up to 1,000 distinct source rows with bounded employee identity fields.");
    }

    internal static ExcelPayslipIdentityPreview BuildIdentityPreview(int clientId,ExcelPayslipIdentityPreviewRequest request,IReadOnlyList<Employee> employees,IReadOnlyDictionary<int,string>? locations=null)
    {
        ValidateIdentityPreview(request);
        var current=employees.Where(e=>e.ClientId==clientId).ToArray();
        string Key(string kind,string? value)=>EmployeeRepository.PreviewIdentityKey(kind,value);
        string Text(string value)=>ExcelPayslipVarianceService.NormalizeText(value);
        string MasterValue(Employee e,string field)=>field switch {
            "Employee Code"=>e.EmployeeCode,"Email"=>e.WorkEmail,"Phone"=>e.PersonalDetails?.Mobile??"",
            "UAN"=>e.PersonalDetails?.UanNumber??"","Bank Account"=>e.PaymentDetails?.BankAccountNo??"",_=>""
        };
        var fields=new[]{"Employee Code","Phone","UAN","Email","Bank Account"};
        var maps=fields.ToDictionary(field=>field,field=>current.Select(e=>(Employee:e,Key:Key(field,MasterValue(e,field)))).Where(x=>x.Key.Length>0).ToLookup(x=>x.Key,x=>x.Employee));
        var results=new List<ExcelPayslipIdentityMatch>();
        foreach(var row in request.Rows)
        {
            var values=new Dictionary<string,string>{{"Employee Code",row.EmployeeCode},{"Phone",row.Mobile},{"UAN",row.Uan},{"Email",row.Email},{"Bank Account",row.BankAccount}};
            var candidates=new Dictionary<int,Employee>();var basis=new List<string>();var reasons=new List<string>();
            if(row.ConflictingFields is {Count:>0})reasons.Add("Mapped source columns contain different values for: "+string.Join(", ",row.ConflictingFields)+". Choose the intended identity column before matching.");
            var evidence=fields.Select(field=>(Field:field,Hits:maps[field][Key(field,values[field])].ToArray())).ToArray();
            foreach(var (field,hits) in evidence.Where(item=>item.Hits.Length==1))
            {
                basis.Add(field);
                foreach(var hit in hits)candidates[hit.Id]=hit;
            }
            foreach(var (field,hits) in evidence.Where(item=>item.Hits.Length>1))
            {
                // Shared office contact details do not overrule a unique bank,
                // UAN/code or other exact identifier that points to that person.
                if((field is "Email" or "Phone")&&candidates.Count==1&&hits.Any(hit=>candidates.ContainsKey(hit.Id)))continue;
                reasons.Add($"{field} matches {hits.Length} Employee Master records in this client.");
                foreach(var hit in hits)candidates[hit.Id]=hit;
            }
            if(EmployeeRepository.IsScientificBankAccount(row.BankAccount))reasons.Add("Bank account is in scientific notation; its full original digits must be reviewed. No account number was reconstructed.");
            if(candidates.Count>1)reasons.Add("The supplied identity fields point to different employees. Review Employee Master before updating: "+string.Join(", ",candidates.Values.Select(e=>e.EmployeeCode).Take(5))+".");
            var matched=candidates.Count==1?candidates.Values.Single():null;
            if(matched is not null&&Key("UAN",row.Uan) is {Length:>0} sourceUan&&Key("UAN",matched.PersonalDetails?.UanNumber) is {Length:>0} oldUan&&sourceUan!=oldUan)
                reasons.Add("The source UAN differs from the UAN stored for the matched employee.");
            if(matched is null&&candidates.Count==0&&row.EmployeeName.Trim().Length>0)
            {
                var name=ExcelPayslipRowIdentity.NormalizeName(row.EmployeeName);
                var probable=current.Where(e=>ExcelPayslipRowIdentity.NormalizeName(e.FirstName+" "+e.LastName)==name&&
                    (row.WorkLocation.Trim().Length>0&&locations is not null&&locations.TryGetValue(e.WorkLocationId,out var location)&&Text(location)==Text(row.WorkLocation)
                    ||row.Department.Trim().Length>0&&Text(e.Department)==Text(row.Department))).ToArray();
                if(probable.Length>0)reasons.Add($"Name and location/department suggest {probable.Length} existing record(s), but no unique code, phone, UAN, email or complete bank account confirms the identity.");
                else if(current.Any(e=>ExcelPayslipRowIdentity.NormalizeName(e.FirstName+" "+e.LastName)==name))reasons.Add("An employee with this name exists; supply phone, UAN, email or the full bank account to confirm which Master record it is.");
            }
            results.Add(new(row.SourceRow,reasons.Count>0?"Review":matched is not null?"Existing":"No match",
                reasons.Count==0?matched?.EmployeeCode??"":"",matched?.IsActive??false,basis,reasons));
        }
        return new(clientId,request.Rows.Count,results);
    }

    // Existing Employee read + client access required. Never return identifiers,
    // employee IDs or the private Master snapshot to the browser.
    public async Task<ExcelPayslipIdentityPreview> PreviewEmployeeIdentitiesAsync(int clientId,ExcelPayslipIdentityPreviewRequest request)
    {
        ValidateIdentityPreview(request);
        await using var db=Db();await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        if(!await SupportedAsync(db,clientId,tx))throw new InvalidOperationException("Select an active client.");
        var current=await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db,tx,clientId);
        var locations=(await db.QueryAsync<(int Id,string Name)>("SELECT Id,Name FROM worklocations WHERE ClientId=@ClientId",new{ClientId=clientId},tx)).ToDictionary(x=>x.Id,x=>x.Name);
        var result=BuildIdentityPreview(clientId,request,current,locations);
        await tx.RollbackAsync();return result;
    }
}
