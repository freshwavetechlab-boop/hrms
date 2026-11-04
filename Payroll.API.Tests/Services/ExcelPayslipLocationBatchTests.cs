using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class ExcelPayslipLocationBatchTests
{
    [Fact] public void Same_month_locations_and_September_are_all_visible_while_legacy_versions_stay_current_only()
    {
        var rows=new[]{new ExcelPayslipBatchSummary{Id="A",ClientId=7,Month="2026-10",LocationBatch=true,LocationLabel="A"},
            new ExcelPayslipBatchSummary{Id="B",ClientId=7,Month="2026-10",LocationBatch=true,LocationLabel="B"},
            new ExcelPayslipBatchSummary{Id="oct-new",ClientId=7,Month="2026-10"},new ExcelPayslipBatchSummary{Id="oct-old",ClientId=7,Month="2026-10"},
            new ExcelPayslipBatchSummary{Id="sept",ClientId=7,Month="2026-09"}};
        var result=ExcelPayslipRepository.CurrentMonths(rows);Assert.Equal(4,result.Count);Assert.Contains(result,r=>r.Id=="sept");Assert.DoesNotContain(result,r=>r.Id=="oct-old");
    }
    [Fact] public void Browser_row_ids_do_not_duplicate_an_import_but_changed_pay_or_source_changes_its_receipt()
    {
        var batch=new ExcelPayslipBatch{Month="2026-10",LocationLabel="Location A",SourceFileName="test.xlsx",Rows=[new(){Id="first",SourceRow=2,EmployeeCode="TEST01",EmployeeName="Synthetic",NetPay=100}]};
        var hash=ExcelPayslipRepository.LocationBatchImportHash(batch);batch.Rows[0].Id="retry-row-id";
        Assert.Equal(hash,ExcelPayslipRepository.LocationBatchImportHash(batch));batch.Rows[0].NetPay=101;
        Assert.NotEqual(hash,ExcelPayslipRepository.LocationBatchImportHash(batch));batch.Rows[0].NetPay=100;batch.LocationLabel="Location B";
        Assert.NotEqual(hash,ExcelPayslipRepository.LocationBatchImportHash(batch));
    }
}
