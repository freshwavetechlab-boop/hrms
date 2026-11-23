using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    internal static List<RosterExchangeColumn> StandardRosterColumns() =>
    [
        new("profileCode", "Profile Code [profileCode]", "Identity", ReadOnly: true, Description: "Update matches this exact code within the selected batch. Leave blank for insertion; unknown update codes are skipped."),
        new("rowAction", "Row Action [rowAction]", "Review", Description: "Apply or Skip. Blank means Apply."),
        new("employeeCode", "Employee Code [employeeCode]", "0001"),
        new("firstName", "First Name [firstName]", "0002", RequiredOnInsert: true),
        new("lastName", "Last Name [lastName]", "0002"),
        new("gender", "Gender [gender]", "0002"),
        new("dateOfBirth", "Date of Birth [dateOfBirth]", "0002", "date"),
        new("candidateEmail", "Work Email [candidateEmail]", "0001"),
        new("candidatePhone", "Mobile [candidatePhone]", "0002"),
        new("joiningDate", "Date of Joining [joiningDate]", "0000", "date"),
        new("department", "Department [department]", "0001"),
        new("positionTitle", "Designation [positionTitle]", "0001"),
        new("grade", "Grade [grade]", "0001"),
        new("employmentType", "Employee Type [employmentType]", "0001"),
        new("skillCategory", "Employee Category [skillCategory]", "0001"),
        new("workLocation", "Work Location / Institute [workLocation]", "0001"),
        new("workLocationAddress", "Work Location Address [workLocationAddress]", "0001"),
        new("workLocationCity", "Work Location City [workLocationCity]", "0001"),
        new("workLocationState", "Work Location State [workLocationState]", "0001"),
        new("workLocationPostalCode", "Work Location PIN [workLocationPostalCode]", "0001"),
        new("reportingManagerUserId", "Reporting Manager User ID [reportingManagerUserId]", "0001", "integer"),
        new("portalAccess", "Portal Access [portalAccess]", "0001", ReadOnly: true, Description: "Current access is shown for reference. Change access through Employee Master."),
        new("active", "Active [active]", "0000", ReadOnly: true, Description: "Current status is shown for reference. Use the dated employment action for exits and rehires."),
        new("panNumber", "PAN [panNumber]", "0002"),
        new("aadhaarNumber", "Aadhaar [aadhaarNumber]", "0002"),
        new("uanNumber", "UAN Number [uanNumber]", "0002"),
        new("esicNumber", "ESIC Number [esicNumber]", "0002"),
        new("address", "Address [address]", "0006"),
        new("correspondenceAddress", "Correspondence Address [correspondenceAddress]", "0006"),
        new("permanentAddress", "Permanent Address [permanentAddress]", "0006"),
        new("city", "City [city]", "0006"),
        new("district", "District [district]", "0006"),
        new("state", "State [state]", "0006"),
        new("bankName", "Bank Name [bankName]", "0009"),
        new("bankAccountNo", "Bank Account Number [bankAccountNo]", "0009", Description: "Keep as text, including leading zeroes."),
        new("ifscCode", "IFSC [ifscCode]", "0009"),
        new("paymentMode", "Payment Mode [paymentMode]", "0009"),
        new("salaryStructureId", "Salary Template ID [salaryStructureId]", "0008"),
        new("annualCtc", "Annual CTC INR/year [annualCtc]", "0008", "money"),
        new("monthlyGross", "Monthly Gross INR/month [monthlyGross]", "Offer", "money", Description: "Enter gross remuneration, not Net Pay. No Basic or CTC split is inferred."),
        new("contractEndDate", "Contract End Date [contractEndDate]", "Offer", "date"),
        new("projectName", "Project [projectName]", "Offer"),
        new("changeReason", "Change Reason [changeReason]", "Review"),
        new("revision", "Record Version [revision]", "Identity", ReadOnly: true, Description: "Keep the downloaded version unchanged. Stale rows are skipped for review.")
    ];

    internal static Dictionary<string, string> StandardEmployeeValues(Employee employee)
    {
        var personal = employee.PersonalDetails; var payment = employee.PaymentDetails;
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["employeeCode"] = employee.EmployeeCode, ["firstName"] = employee.FirstName, ["lastName"] = employee.LastName,
            ["gender"] = employee.Gender, ["dateOfBirth"] = personal.DateOfBirth, ["candidateEmail"] = employee.WorkEmail,
            ["candidatePhone"] = personal.Mobile, ["joiningDate"] = employee.DateOfJoining, ["department"] = employee.Department,
            ["positionTitle"] = employee.Designation, ["grade"] = employee.Grade, ["employmentType"] = personal.EmploymentType,
            ["skillCategory"] = personal.SkillCategory, ["reportingManagerUserId"] = employee.ReportingManagerUserId?.ToString() ?? "",
            ["portalAccess"] = employee.PortalAccess ? "TRUE" : "FALSE", ["active"] = employee.IsActive ? "TRUE" : "FALSE",
            ["panNumber"] = personal.PanNumber, ["aadhaarNumber"] = personal.AadhaarNumber, ["uanNumber"] = personal.UanNumber,
            ["esicNumber"] = personal.EsicNumber, ["address"] = personal.Address, ["correspondenceAddress"] = personal.CorrespondenceAddress,
            ["permanentAddress"] = personal.PermanentAddress, ["city"] = personal.City, ["district"] = personal.District, ["state"] = personal.State,
            ["bankName"] = payment.BankName, ["bankAccountNo"] = payment.BankAccountNo, ["ifscCode"] = payment.IfscCode,
            ["paymentMode"] = payment.PaymentMode, ["salaryStructureId"] = employee.SalaryStructureId,
            ["annualCtc"] = employee.AnnualCtc.ToString(CultureInfo.InvariantCulture)
        };
    }

    internal static string StandardRosterRevision(RecruitmentDocumentRosterRow row, Employee? employee, IReadOnlyDictionary<string, string>? custom)
        => Hash(JsonSerializer.Serialize(new { row.Id, row.ClientId, row.CandidateId, row.TemplateId, row.WorkLocationId,
            row.EmployeeId, row.CandidateName, row.Email, row.Phone, row.ValuesJson, Employee = employee,
            Custom = custom?.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() }, Json));
}
