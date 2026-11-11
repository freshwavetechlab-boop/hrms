using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    // The existing employee identity/import rules and infotype save run in the
    // release transaction. Repeated releases link the same employee, never rehire
    // an inactive employee or overwrite an existing master's salary/bank values.
    private static async Task LinkReleasedEmployeeAsync(MySqlConnection db, MySqlTransaction tx,
        RecruitmentDocumentRosterRow row, Dictionary<string,string> values, WorkLocation location,
        List<Employee> current, AuthUser user, string reference, IReadOnlySet<int> permittedLocations)
    {
        var candidateEmployeeId = await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM recruitment_candidates WHERE Id=@CandidateId AND ClientId=@ClientId FOR UPDATE", row, tx);
        if(row.EmployeeId.HasValue && candidateEmployeeId.HasValue && row.EmployeeId!=candidateEmployeeId)throw new InvalidOperationException("The source profile and roster have conflicting employee links. Review Employee Master before release.");
        var linkedId=row.EmployeeId??candidateEmployeeId;
        if (linkedId.HasValue)
        {
            var linked = current.SingleOrDefault(employee => employee.Id == linkedId);
            if (linked is null) throw new InvalidOperationException($"{row.CandidateCode}: the employee link is outside this client or no longer exists.");
            if (!linked.IsActive) throw new InvalidOperationException($"{row.CandidateCode}: rehire the inactive employee from Employee Master before releasing another offer.");
            RequireEmployeePosting(linked,row,user,permittedLocations);
            if(await db.ExecuteScalarAsync<bool>("SELECT COALESCE("+EmployeeEmploymentRules.LatestAction("e","@Today")+",'Rehire') IN ('Resign','Retire','Terminate') FROM employees e WHERE e.Id=@Id AND e.ClientId=@ClientId",new{Id=linked.Id,row.ClientId,Today=EmployeeEmploymentRules.Today},tx))
                throw new InvalidOperationException("Rehire the exited employee in Employee Master before releasing another offer.");
            await RecordRosterEmployeeLinkAsync(db,tx,row,linked,user,reference);
            return;
        }
        var incoming = BuildRosterEmployee(row, values, location);
        var identity = EmployeeRepository.ExcelPayrollIdentityRows([incoming], current).Single();
        if (identity.BlockingReasons.Count > 0 || identity.MatchStatus == "ProbableDuplicate")
            throw new InvalidOperationException($"{row.CandidateCode}: review the Employee Master identity match before release. " + string.Join(" ", identity.BlockingReasons));
        if (identity.MatchedEmployeeId.HasValue)
        {
            var existing = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, identity.MatchedEmployeeId.Value, tx)
                ?? throw new InvalidOperationException("The matched employee is unavailable.");
            if (existing.ClientId != row.ClientId || !existing.IsActive)
                throw new InvalidOperationException($"{row.CandidateCode}: the matched employee is inactive or belongs to another client.");
            RequireEmployeePosting(existing,row,user,permittedLocations);
            if(NormalizedRosterName(existing.FirstName+existing.LastName)!=NormalizedRosterName(incoming.FirstName+incoming.LastName))
                throw new InvalidOperationException($"{row.CandidateCode}: contact/bank identifiers match an employee with a different name. Review the identity in Employee Master before release.");
            incoming = FillRosterEmployeeBlanks(existing, incoming);
        }
        incoming.Id = await EmployeeRepository.SaveRecruitmentEmployeeAsync(db, tx, incoming, user, reference);
        await RecordRosterEmployeeLinkAsync(db,tx,row,incoming,user,reference);
        current.RemoveAll(employee => employee.Id == incoming.Id); current.Add(incoming);
    }
    private static async Task RecordRosterEmployeeLinkAsync(MySqlConnection db,MySqlTransaction tx,RecruitmentDocumentRosterRow row,Employee employee,AuthUser user,string reference)
    {
        if(row.EmployeeId==employee.Id)return;
        await db.ExecuteAsync("UPDATE recruitment_document_roster SET EmployeeId=@EmployeeId WHERE Id=@Id AND ClientId=@ClientId",new{EmployeeId=employee.Id,row.Id,row.ClientId},tx);
        // Native recruitment has one canonical candidate per employee. Additional
        // imported source profiles link through their roster without weakening
        // that uniqueness constraint or creating another employee.
        if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidates WHERE EmployeeId=@EmployeeId AND Id<>@CandidateId",new{EmployeeId=employee.Id,row.CandidateId},tx)==0)
            await db.ExecuteAsync("UPDATE recruitment_candidates SET EmployeeId=@EmployeeId,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@CandidateId AND ClientId=@ClientId AND EmployeeId IS NULL",new{EmployeeId=employee.Id,row.CandidateId,row.ClientId},tx);
        await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Employee linked',@Receipt,@UserId)",new {row.Id,row.ClientId,Receipt=JsonSerializer.Serialize(new {EmployeeId=employee.Id,employee.EmployeeCode,OfferNumber=reference},Json),UserId=user.Id},tx);
        row.EmployeeId=employee.Id;
    }
    private static string NormalizedRosterName(string name)=>string.Concat(name.Normalize().Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static void RequireEmployeePosting(Employee employee,RecruitmentDocumentRosterRow row,AuthUser user,IReadOnlySet<int> permittedLocations)
    {
        if(RecruitmentAccessScope.IsRestricted(user) && !permittedLocations.Contains(employee.WorkLocationId))throw new UnauthorizedAccessException("The matched employee is outside your permitted work locations.");
        if(employee.WorkLocationId>0 && employee.WorkLocationId!=row.WorkLocationId)throw new InvalidOperationException($"{row.CandidateCode}: update the existing employee's work location in Employee Master before releasing an offer for a different posting.");
    }

    internal static Employee BuildRosterEmployee(RecruitmentDocumentRosterRow row, IReadOnlyDictionary<string,string> values, WorkLocation location)
    {
        string Value(params string[] keys) => keys.Select(key=>values.GetValueOrDefault(key, "").Trim()).FirstOrDefault(value=>value.Length>0) ?? "";
        var date = Value("joiningDate");
        if (!DateOnly.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var joining))
            throw new InvalidOperationException($"{row.CandidateCode}: enter a valid joining date before release and automatic onboarding.");
        var name = row.CandidateName.Trim().Split(' ',2,StringSplitOptions.RemoveEmptyEntries);
        if (name.Length == 0 || row.ClientId<=0 || row.WorkLocationId!=location.Id || location.ClientId!=row.ClientId)
            throw new InvalidOperationException("Employee name, client and linked work location must be available before release.");
        // Monthly emoluments are not an annual CTC. Copy CTC only when the
        // template explicitly supplies it; payroll configuration remains intact.
        var annual = Value("annualCtc"); decimal ctc=0;
        if (annual.Length>0 && (!decimal.TryParse(annual.Replace(",", ""),NumberStyles.Number,CultureInfo.InvariantCulture,out ctc) || ctc<0))
            throw new InvalidOperationException($"{row.CandidateCode}: annual CTC is invalid.");
        var employee=new Employee { ClientId=row.ClientId,EmployeeCode=Value("employeeCode") is {Length:>0} code ? code : row.CandidateCode,
            FirstName=name[0],LastName=name.Length>1?name[1]:"",WorkEmail=row.Email,DateOfJoining=joining.ToString("yyyy-MM-dd"),
            Designation=Value("positionTitle"),Department=Value("department"),WorkLocationId=location.Id,AnnualCtc=ctc,
            PersonalDetails=new(){Mobile=row.Phone,DateOfBirth=Value("dateOfBirth"),PanNumber=Value("pan","panNumber"),AadhaarNumber=Value("aadhaar","aadhaarNumber"),UanNumber=Value("uan","uanNumber"),EsicNumber=Value("esicNumber"),Source="Recruitment offer",SourceLocation=location.Name,City=location.City,State=location.State},
            PaymentDetails=new(){BankName=Value("bankName"),BankAccountNo=Value("accountNo","bankAccountNo"),IfscCode=Value("ifsc","ifscCode")}};
        employee.PersonalJson=JsonSerializer.Serialize(employee.PersonalDetails);employee.PaymentJson=JsonSerializer.Serialize(employee.PaymentDetails);
        return employee;
    }

    internal static Employee FillRosterEmployeeBlanks(Employee existing, Employee incoming)
    {
        existing=RecruitmentTalentRepository.FillEmployeeBlanks(existing,incoming);
        static string Fill(string value,string source)=>string.IsNullOrWhiteSpace(value)?source:value;
        existing.PersonalDetails.PanNumber=Fill(existing.PersonalDetails.PanNumber,incoming.PersonalDetails.PanNumber);
        existing.PersonalDetails.AadhaarNumber=Fill(existing.PersonalDetails.AadhaarNumber,incoming.PersonalDetails.AadhaarNumber);
        existing.PersonalDetails.UanNumber=Fill(existing.PersonalDetails.UanNumber,incoming.PersonalDetails.UanNumber);
        existing.PersonalDetails.EsicNumber=Fill(existing.PersonalDetails.EsicNumber,incoming.PersonalDetails.EsicNumber);
        existing.PaymentDetails.BankName=Fill(existing.PaymentDetails.BankName,incoming.PaymentDetails.BankName);
        existing.PaymentDetails.BankAccountNo=Fill(existing.PaymentDetails.BankAccountNo,incoming.PaymentDetails.BankAccountNo);
        existing.PaymentDetails.IfscCode=Fill(existing.PaymentDetails.IfscCode,incoming.PaymentDetails.IfscCode);
        existing.PersonalJson=JsonSerializer.Serialize(existing.PersonalDetails);existing.PaymentJson=JsonSerializer.Serialize(existing.PaymentDetails);
        return existing;
    }
}
