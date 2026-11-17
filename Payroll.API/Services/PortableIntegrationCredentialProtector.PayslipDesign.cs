using System.Text;
namespace Payroll.API.Services;
public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectPayslipDesign(string value)=>Protect(value,"payslip-design:v1:",Encoding.UTF8.GetBytes("Payroll.API.NativePayslipDesign.v1"));
    internal string? UnprotectPayslipDesign(string value)=>IsPortable(value,"payslip-design:v1:") && TryUnprotect(value,"payslip-design:v1:",Encoding.UTF8.GetBytes("Payroll.API.NativePayslipDesign.v1"),legacyAiProtector,out var plaintext)?plaintext:null;
}
