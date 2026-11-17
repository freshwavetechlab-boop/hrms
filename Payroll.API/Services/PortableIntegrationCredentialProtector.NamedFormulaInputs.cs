using System.Text;

namespace Payroll.API.Services;

public sealed partial class PortableIntegrationCredentialProtector
{
    internal string ProtectNamedFormulaInput(string value,string kind) => Protect(value,"named-formula-input:"+kind+":v1:",Encoding.UTF8.GetBytes("Payroll.API.NamedFormulaInput."+kind+".v1"));
    internal string? UnprotectNamedFormulaInput(string value,string kind) =>
        IsPortable(value,"named-formula-input:"+kind+":v1:")&&TryUnprotect(value,"named-formula-input:"+kind+":v1:",Encoding.UTF8.GetBytes("Payroll.API.NamedFormulaInput."+kind+".v1"),legacyAiProtector,out var plaintext)?plaintext:null;
}
