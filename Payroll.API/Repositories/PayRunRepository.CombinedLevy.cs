namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private static bool IsCombinedLevy(string code) => code.Equals("PT_LWF_WC",StringComparison.OrdinalIgnoreCase)
        || code.Equals("WORKMEN_COMP_LWF_PT",StringComparison.OrdinalIgnoreCase);

    // The legacy personal field combines PT/LWF/workmen costs. An effective
    // combined salary component is the same charge, not an additional charge.
    // A template's unfilled zero default remains distinct from an employee's
    // explicit amount (including zero) or an evaluated formula/slab rule.
    private static CalculatedPayrollComponent? AuthoritativeCombinedLevy(PayRunSourceEmployee employee,
        IReadOnlyList<CalculatedPayrollComponent> salary)
    {
        if(employee.LocationParts is not null || string.IsNullOrWhiteSpace(employee.SalaryStructureId))return null;
        var declared=salary.Where(row=>IsDeductionCategory(row.Component.Category)&&IsCombinedLevy(row.Component.Code))
            .Where(row=>(row.EarnedOverride??row.Monthly)>0
                || NormalizeCalculationType(row.Component.CalculationType)=="Fixed Amount"
                    && employee.SalaryComponents.TryGetValue(row.Component.Id,out var amount)&&amount>=0
                || NormalizeCalculationType(row.Component.CalculationType)=="Formula" && !string.IsNullOrWhiteSpace(row.Component.Formula)
                || NormalizeCalculationType(row.Component.CalculationType)=="Slab Based"
                    && FirstText(row.Component.Formula,row.Component.Value).Contains(':')).ToArray();
        if(declared.Length==0)return null;
        if(declared.Length!=1 || employee.ReviewedProfessionalTax is not null || salary.Any(row=>IsDeductionCategory(row.Component.Category)
            && !IsCombinedLevy(row.Component.Code)
            && Services.ReviewedPeriodProfessionalTax.IsPt(row.Component.Code,row.Component.StatutoryType)))
            throw new InvalidOperationException("Review one combined PT/LWF/workmen salary component without a second combined or separate PT component. No ambiguous charge was silently removed.");
        return declared[0];
    }
}
