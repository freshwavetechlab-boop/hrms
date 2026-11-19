using System.Data.Common;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class ExcelPayrollSalaryOnlySaveTests
{
    [Theory]
    [InlineData(1)][InlineData(0)]
    public async Task Attendance_only_writer_inserts_unknown_presence_without_salary_policy_or_existing_attendance_updates(int affected)
    {
        var evidence=new SourcePayableAttendanceEvidence(2,9,71,"2026-10",new('a',32),"row-7",new('b',32),new('c',64),26,40,ReviewedExtraDutyUnits:true);
        var row=new PayRunRepository.SourceAttendanceInsert(71,9,"2026-10",26,40,SourcePayableAttendance.SourceType,SourcePayableAttendance.Encode(evidence));
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();
        db.CustomExecutor=(sql,parameters,scalar)=>{
            Assert.False(scalar);Assert.StartsWith("INSERT INTO employee_monthly_attendance",sql);
            Assert.Contains("@Divisor,NULL,@Payable",sql);Assert.DoesNotContain("UPDATE",sql);Assert.DoesNotContain("DELETE",sql);
            object? Value(string name)=>parameters.Cast<DbParameter>().Single(value=>value.ParameterName.TrimStart('@')==name).Value;
            Assert.Equal(71,Convert.ToInt32(Value("EmployeeId")));Assert.Equal(9,Convert.ToInt32(Value("ClientId")));
            Assert.Equal("2026-10",Value("Month"));Assert.Equal(40m,Value("Payable"));Assert.Equal(0m,Value("Lop"));return affected;
        };
        if(affected==1)await PayRunRepository.WriteSourceMonthAttendanceAsync(db,tx,[row]);
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.WriteSourceMonthAttendanceAsync(db,tx,[row]));
        Assert.Same(tx,Assert.Single(db.Commands).Transaction);Assert.Equal("original",db.Template);Assert.Equal("123460000001",db.BaseBankText);
        Assert.Equal(new[]{"prior-exact-bank","1.2346E+11"},db.BankHistory);Assert.Equal(0,db.CommitCalls);
    }
}
