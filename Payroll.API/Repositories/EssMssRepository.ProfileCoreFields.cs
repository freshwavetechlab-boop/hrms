using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    // Format validation is not proof of identifier ownership. Preserve untouched legacy
    // values and omitted fields from older clients; validate values being changed.
    internal static string NormalizeProfileCoreFields(SaveEssProfileRequest request, Employee employee, EmployeePersonalDetails personal)
    {
        var uan = request.UanNumber is null || request.UanNumber == personal.UanNumber ? personal.UanNumber : Clean(request.UanNumber);
        var esic = request.EsicNumber is null || request.EsicNumber == personal.EsicNumber ? personal.EsicNumber : Clean(request.EsicNumber);
        var gender = request.Gender is null || request.Gender == employee.Gender ? employee.Gender : Clean(request.Gender);
        if (uan != personal.UanNumber && uan.Length > 0 && !Regex.IsMatch(uan, @"\A[0-9]{12}\z"))
            return "Enter a 12-digit UAN, or leave it blank if unavailable.";
        if (esic != personal.EsicNumber && esic.Length > 0 && !Regex.IsMatch(esic, @"\A[0-9]{10}\z"))
            return "Enter a 10-digit ESIC number, or leave it blank if unavailable.";
        var canonicalGender = new[] { "Male", "Female", "Other" }.FirstOrDefault(value => value.Equals(gender, StringComparison.OrdinalIgnoreCase));
        if (gender != employee.Gender && gender.Length > 0 && canonicalGender is null)
            return "Select Male, Female, or Other for gender.";
        request.UanNumber = uan;
        request.EsicNumber = esic;
        request.Gender = gender == employee.Gender ? employee.Gender : canonicalGender ?? gender;
        return "";
    }
}
