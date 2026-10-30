using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static List<ExcelPayrollSelectedSourceRow> DescribeSelectedSources(ExcelPayslipBatch batch, ExcelPayrollSetupRequest request) =>
        request.RowIds.Select(id =>
        {
            var matches = batch.Rows.Where(row => row.Id == id).ToList();
            return new ExcelPayrollSelectedSourceRow(id, matches.Count == 1 ? matches[0].SourceRow : null, matches.Count == 1);
        }).ToList();
}
