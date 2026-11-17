namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static MasterSyncBinding[] SelectMasterDetailBindings(
        IReadOnlyDictionary<string,List<MasterSyncBinding>> bindings,IReadOnlySet<string>? selectedRowIds)
    {
        var unambiguous=bindings.Values.Where(list=>list.Count>0 && list.Select(binding=>binding.Link.EmployeeId).Distinct().Count()==1)
            .Select(list=>list[0]).ToArray();
        // Check the complete authoritative batch before applying the PDF selection.
        // Selecting one of two source rows must never hide a duplicate identity.
        var unique=unambiguous.Where(binding=>unambiguous.Count(other=>other.Link.EmployeeId==binding.Link.EmployeeId)==1);
        return unique.Where(binding=>selectedRowIds is null || selectedRowIds.Contains(binding.Row.Id)).ToArray();
    }
}
