using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectPayrollReferenceInformation(string value)=>Protect(value,"payroll-reference-info:v1:",
        Encoding.UTF8.GetBytes("Payroll.API.PayrollReferenceInformation.v1"));
    internal string? UnprotectPayrollReferenceInformation(string value)=>
        IsPortable(value,"payroll-reference-info:v1:")&&TryUnprotect(value,"payroll-reference-info:v1:",
            Encoding.UTF8.GetBytes("Payroll.API.PayrollReferenceInformation.v1"),legacyAiProtector,out var plaintext)?plaintext:null;
}
