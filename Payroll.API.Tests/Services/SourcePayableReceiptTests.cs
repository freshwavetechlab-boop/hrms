using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SourcePayableReceiptTests
{
    [Fact]
    public void SharedReceiptStillValidatesEachEmployeeDaysSourceAndIndependentFormula()
    {
        var evidence=new SourcePayableOverrideEvidence(1,11,7,"2026-09",Guid.NewGuid().ToString(),"source-row-1",Guid.NewGuid().ToString(),new string('c',64),30,20,15,"2026-09-01","2026-09-30");
        var request=new ExcelPayrollSetupRequest {OverrideSourcePayableAttendance=true,ImportAttendance=false};
        var result=new ExcelPayrollSetupResult {Transfer=new(){SourceHash=evidence.SourceHash},
            PayableOverrides=[new(7,"source-row-1","2026-09",30,15,15,15,"Monthly","",30,20),new(8,"source-row-2","2026-09",30,12,12,18,"Monthly","",30,18)],
            TransferRows=[new("source-row-1",7,30,15,20,0,[]),new("source-row-2",8,30,12,18,0,[])]};
        var first=new PayRunRepository.PayRunAttendance {EmployeeId=7,WorkingDays=30,PresentDays=15,PayableDays=20};
        var second=new PayRunRepository.PayRunAttendance {EmployeeId=8,WorkingDays=30,PresentDays=12,PayableDays=18};
        PayRunRepository.RequireSourcePayableReceipt(request,result,evidence,first,"2026-09");
        var other=evidence with {EmployeeId=8,RowId="source-row-2",PresentDays=12,PayableDays=18};
        PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-09");
        second.PayableDays=19;
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-09"));
        second.PayableDays=18;second.PresentDays=13;
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-09"));
        second.PresentDays=12;
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other with {SourceHash=new string('d',64)},second,"2026-09"));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other with {RowId="missing-row"},second,"2026-09"));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-10"));
        request.ImportAttendance=true;
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-09"));
        request.ImportAttendance=false;request.Components=[new(){Formula="PRESENT_DAYS*100"}];
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireSourcePayableReceipt(request,result,other,second,"2026-09"));
    }
}
