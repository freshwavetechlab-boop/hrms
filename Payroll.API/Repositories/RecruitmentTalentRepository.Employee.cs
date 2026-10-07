using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    public async Task<(EmployeeConversionPreview? Item, string Error)> PreviewEmployeeConversionAsync(long applicationId, ConvertCandidateToEmployeeRequest request, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (application is null) return (null, "Application was not found.");
        var candidate = await CandidateByIdAsync(db, application.CandidateId);
        if (candidate is null) return (null, "Candidate was not found.");
        var employee = await BuildConversionEmployeeAsync(db, application, candidate, request);
        var identity = (await employees.PreviewRecruitmentAsync(employee)).Rows.Single();
        try
        {
            var forms = await EmployeeAttributeRepository.ExchangeFormsAsync(db, application.ClientId);
            var values = await EmployeeAttributeRepository.CandidateValuesAsync(db, application.ClientId, candidate.Id, applicationId, forms);
            var changes = await EmployeeAttributeRepository.TransferChangesAsync(db, identity.MatchedEmployeeId ?? 0, application.ClientId, forms, values);
            return (new(identity, changes), "");
        }
        catch (InvalidOperationException exception) { return (null, exception.Message); }
    }

    internal static Employee FillEmployeeBlanks(Employee existing, Employee incoming)
    {
        static string Fill(string value, string proposed) => string.IsNullOrWhiteSpace(value) ? proposed : value;
        existing.FirstName = Fill(existing.FirstName, incoming.FirstName); existing.LastName = Fill(existing.LastName, incoming.LastName);
        existing.WorkEmail = Fill(existing.WorkEmail, incoming.WorkEmail); existing.Gender = Fill(existing.Gender, incoming.Gender);
        existing.DateOfJoining = Fill(existing.DateOfJoining, incoming.DateOfJoining);
        existing.Department = Fill(existing.Department, incoming.Department); existing.Designation = Fill(existing.Designation, incoming.Designation); existing.Grade = Fill(existing.Grade, incoming.Grade);
        if (existing.WorkLocationId <= 0) existing.WorkLocationId = incoming.WorkLocationId;
        if (existing.ReportingManagerId <= 0) existing.ReportingManagerId = incoming.ReportingManagerId;
        existing.ReportingManagerUserId ??= incoming.ReportingManagerUserId;
        existing.SalaryStructureId = Fill(existing.SalaryStructureId, incoming.SalaryStructureId);
        if (existing.AnnualCtc <= 0) existing.AnnualCtc = incoming.AnnualCtc;
        existing.PersonalDetails.Mobile = Fill(existing.PersonalDetails.Mobile, incoming.PersonalDetails.Mobile);
        return existing;
    }
}
