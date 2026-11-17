using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Internal authority only: the current blank Master, independently derived
    // source budget and planned target must agree. A retained target cannot
    // prove itself. Public DTOs and the global unconfigured-history rule stay
    // unchanged; normal source/identity/scope/PlanHash checks remain mandatory.
    internal static decimal? ReviewedInitialBudgetAnnual(ExcelPayrollSetupRequest request,Employee current,
        decimal plannedAnnual,Func<decimal> independentlyDerivedAnnual)
    {
        if(!request.UpdateExistingConfiguration||!request.ReviewedFixedInputs||request.CorrectionOfImportId is not null
            ||request.LocationAllocations is not null||current.Id<=0||current.ClientId<=0
            ||!string.IsNullOrWhiteSpace(current.SalaryStructureId)||current.SalaryComponents is not{Count:0}
            ||!EmployeeRepository.IsExactEmptyInitialSalaryInputs(current.SalaryJson)
            ||current.AnnualCtc is <=0 or >1_000_000_000m||decimal.Round(current.AnnualCtc,2)!=current.AnnualCtc
            ||plannedAnnual!=current.AnnualCtc)return null;
        try {return independentlyDerivedAnnual()==current.AnnualCtc?current.AnnualCtc:null;}
        catch(InvalidOperationException){return null;} // No new authority when independent proof is unavailable.
    }
}
