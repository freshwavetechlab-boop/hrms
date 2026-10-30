using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class EmployeeRepository
{
    // The editor submits the complete employee DTO even for a personal-data tab.
    // Keep current bank, salary, organization, addresses and access state from the
    // locked database row rather than copying an effective/stale client overlay.
    internal static Employee PersonalInfotypeState(Employee current, Employee proposed)
    {
        if (current.Id <= 0 || current.Id != proposed.Id || current.ClientId != proposed.ClientId)
            throw new InvalidOperationException("Personal-data correction must target the same existing employee and client.");
        var next = CloneEmployee(current);
        next.FirstName = proposed.FirstName;
        next.LastName = proposed.LastName;
        next.Gender = proposed.Gender;
        next.DateOfJoining = proposed.DateOfJoining;
        next.PersonalDetails.DateOfBirth = proposed.PersonalDetails.DateOfBirth;
        next.PersonalDetails.Mobile = proposed.PersonalDetails.Mobile;
        next.PersonalDetails.PanNumber = proposed.PersonalDetails.PanNumber;
        next.PersonalDetails.AadhaarNumber = proposed.PersonalDetails.AadhaarNumber;
        next.PersonalDetails.UanNumber = proposed.PersonalDetails.UanNumber;
        next.PersonalDetails.EsicNumber = proposed.PersonalDetails.EsicNumber;
        return next;
    }

    internal static Employee BankInfotypeState(Employee current, Employee proposed)
    {
        if (current.Id <= 0 || current.Id != proposed.Id || current.ClientId != proposed.ClientId)
            throw new InvalidOperationException("Bank-data correction must target the same existing employee and client.");
        var next = CloneEmployee(current);
        next.PaymentDetails.BankName = proposed.PaymentDetails.BankName;
        next.PaymentDetails.BankAccountNo = proposed.PaymentDetails.BankAccountNo;
        next.PaymentDetails.IfscCode = proposed.PaymentDetails.IfscCode;
        next.PaymentDetails.PaymentMode = proposed.PaymentDetails.PaymentMode;
        return next;
    }
}
