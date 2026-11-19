using System.Data.Common;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed partial class ExcelPayrollSalaryOnlySaveTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Empty_setup_cleanup_deletes_only_the_reviewed_empty_row_without_a_backup_or_other_writes(int affected)
    {
        var employee=new Employee { Id=71,ClientId=9,EmployeeCode="SYNTH-71",SalaryStructureId="501",SalaryJson="{\"101\":13000}" };
        var history=new List<EmployeeRepository.ClosedSalaryHistoryRow> {
            new(){Id=1,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,10,1),Status="Active",SalaryStructureId="501",SalaryJson=employee.SalaryJson},
            new(){Id=2,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,10,6),Status="Active",SalaryJson="{}"},
            new(){Id=3,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,9,1),EffectiveTo=new(2026,9,30),Status="Historical",SalaryStructureId="490",SalaryJson="{\"101\":12000}"}
        };
        var plan=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),false);
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();
        db.CustomExecutor=(sql,parameters,scalar)=> {
            Assert.False(scalar);
            Assert.StartsWith("DELETE FROM employee_it0008_basic_pay",sql);
            Assert.Contains("EmployeeId=@EmployeeId AND ClientId=@ClientId",sql);
            Assert.Contains("Id IN (@Ids1)",sql);
            Assert.Contains("Status='Active'",sql);
            Assert.Contains("EffectiveFrom>@From AND EffectiveFrom<=@To",sql);
            Assert.Contains("COALESCE(TRIM(SalaryStructureId),'')='' AND AnnualCtc=0",sql);
            Assert.Contains("JSON_LENGTH(SalaryJson)=0",sql);
            object? Value(string name)=>parameters.Cast<DbParameter>().Single(p=>p.ParameterName.TrimStart('@')==name).Value;
            Assert.Equal(71,Convert.ToInt32(Value("EmployeeId")));Assert.Equal(9,Convert.ToInt32(Value("ClientId")));Assert.Equal(2,Convert.ToInt32(Value("Ids1")));
            Assert.Equal(new DateTime(2026,10,1),Value("From"));Assert.Equal(new DateTime(2026,10,31),Value("To"));
            return affected;
        };
        if(affected==1)await EmployeeRepository.WriteEmptySalaryCleanupAsync(db,tx,plan);
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>EmployeeRepository.WriteEmptySalaryCleanupAsync(db,tx,plan));
        Assert.Same(tx,Assert.Single(db.Commands).Transaction);
        Assert.Equal("original",db.Template);Assert.Equal("123460000001",db.BaseBankText);
        Assert.Equal(new[]{"prior-exact-bank","1.2346E+11"},db.BankHistory);
        Assert.Equal(0,db.CommitCalls);
    }
}
