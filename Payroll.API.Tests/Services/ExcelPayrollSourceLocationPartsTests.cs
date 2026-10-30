using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollSourceLocationPartsTests
{
    private static ExcelPayslipRow Part(string id, int row, string location) => new()
    {
        Id=id,SourceRow=row,EmployeeCode="SYNTH-1",EmployeeName="Synthetic Person",NetPay=row,
        Information=[new(){Label="Work location",Value=location},new(){Label="PAN",Value="SYNTH1234Z"},new(){Label="Attendance",Value=row==53?"17":"13"}],
        Earnings=[new(){Label="Basic",Amount=row}]
    };
    private static ExcelPayslipBatch Batch() => new()
    {
        Id=new string('a',32),ClientId=42,Month="2026-09",Rows=[Part("first",53,"Synthetic A"),Part("second",54,"Synthetic B")],
        CalculationSource=new(){Rows=[new(){SourceRow=53,Cells=[new(){Value="53",FormulaText="B53/30*C53"}]}]}
    };

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void OneOrBothPartsCannotReachMasterPlanningEvenWhenAttendanceImportIsOff(bool both, bool importAttendance)
    {
        var batch=Batch();
        var request=new ExcelPayrollSetupRequest {RowIds=both?["first","second"]:["first"],ImportAttendance=importAttendance};
        var selected=batch.Rows.Where(row=>request.RowIds.Contains(row.Id)).ToList();
        var preview=new ExcelPayrollSetupPreview();
        var accepted=ExcelPayrollSetupRepository.ReviewSourceLocationParts(batch,selected,preview);
        Assert.Empty(accepted);Assert.False(preview.CanApply);Assert.Equal(selected.Count,preview.Issues.Count);
        Assert.All(preview.Issues,issue=>
        {
            Assert.Equal("Error",issue.Severity);Assert.Equal(ExcelPayrollSetupRepository.SourceLocationPartsReviewCode,issue.Code);
            Assert.Contains("one Employee ID/code",issue.Message);Assert.Contains("53, 54",issue.Message);
            Assert.Contains("source-period allocation review",issue.Message);Assert.DoesNotContain("effective dates",issue.Message);Assert.DoesNotContain("SYNTH1234Z",issue.Message);
        });
    }

    [Fact]
    public void FullSavedBatchOutsideTheSelectionRemainsTheProofWithoutMutatingAnySource()
    {
        var batch=Batch();var before=JsonSerializer.Serialize(batch);
        var unrelated=Part("third",70,"Synthetic C");unrelated.EmployeeCode="SYNTH-2";unrelated.EmployeeName="Other Synthetic Person";
        batch.Rows.Add(unrelated);before=JsonSerializer.Serialize(batch);
        var preview=new ExcelPayrollSetupPreview();
        var accepted=ExcelPayrollSetupRepository.ReviewSourceLocationParts(batch,[batch.Rows[0],unrelated],preview);
        Assert.Same(unrelated,Assert.Single(accepted));Assert.Equal("first",Assert.Single(preview.Issues).RowId);
        Assert.False(preview.CanApply);Assert.Equal(before,JsonSerializer.Serialize(batch));
        Assert.Equal(0,batch.Rows[0].Employment?.EmployeeId??0);
    }

    [Fact]
    public void IndependentSingleRowsInOtherClientsOrMonthsDoNotBecomeTransferGroups()
    {
        foreach(var (client,month) in new[]{(42,"2026-09"),(43,"2026-09"),(42,"2026-10")})
        {
            var batch=Batch();batch.ClientId=client;batch.Month=month;batch.Rows.RemoveAt(1);
            var preview=new ExcelPayrollSetupPreview();
            Assert.Same(batch.Rows[0],Assert.Single(ExcelPayrollSetupRepository.ReviewSourceLocationParts(batch,batch.Rows,preview)));
            Assert.Empty(preview.Issues);Assert.True(preview.CanApply);
        }
    }

    [Fact]
    public void UnverifiedSourceIdentityIsNotReclassifiedAsAReviewedLocationTransfer()
    {
        var batch=Batch();batch.Rows[1].EmployeeName="Conflicting Synthetic Person";
        var preview=new ExcelPayrollSetupPreview();
        Assert.Equal(2,ExcelPayrollSetupRepository.ReviewSourceLocationParts(batch,batch.Rows,preview).Count);
        Assert.Empty(preview.Issues); // Existing identity guards, not this source-split guard, still decide this selection.
    }

    [Fact]
    public void RealBuildPlanReviewsWholeBatchBeforeConstructingEmployeesAndApplyChecksItBeforeSaving()
    {
        var planCalls=AsyncCalls("BuildPlanAsync");
        var review=planCalls.FindIndex(call=>call.Name==nameof(ExcelPayrollSetupRepository.ReviewSourceLocationParts));
        var create=planCalls.FindIndex(call=>call.Name=="NewEmployee");
        Assert.True(review>=0&&create>review,"The actual async BuildPlan must invoke the source-part guard before its employee-construction loop.");
        var applyCalls=AsyncCalls(nameof(ExcelPayrollSetupRepository.ApplyAsync));
        var build=applyCalls.FindIndex(call=>call.Name=="BuildPlanAsync");
        var canApply=applyCalls.FindIndex(call=>call.Name=="get_CanApply");
        var save=applyCalls.FindIndex(call=>call.Name=="SaveExcelPayrollSetupEmployeeAsync");
        Assert.True(build>=0&&canApply>build&&save>canApply,"Locked Apply must rebuild and check the preview before any employee salary save.");
    }

    // Use the repository's compiled async-boundary test pattern: no SQL connection,
    // Program startup, fake payroll write or employee fixture in production.
    private static List<MethodBase> AsyncCalls(string name)
    {
        var method=typeof(ExcelPayrollSetupRepository).GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!;
        var machine=method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var body=machine.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!;
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(field=>field.FieldType==typeof(OpCode))
            .Select(field=>(OpCode)field.GetValue(null)!).ToDictionary(code=>unchecked((ushort)code.Value));
        var bytes=body.GetMethodBody()!.GetILAsByteArray()!;var calls=new List<MethodBase>();
        for(var index=0;index<bytes.Length;)
        {
            ushort value=bytes[index++];if(value==0xfe)value=(ushort)(0xfe00|bytes[index++]);var code=codes[value];
            var size=code.OperandType switch
            {
                OperandType.InlineNone=>0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(bytes,index),_=>4
            };
            if(code.OperandType==OperandType.InlineMethod)
            {
                var call=body.Module.ResolveMethod(BitConverter.ToInt32(bytes,index),body.DeclaringType?.GetGenericArguments(),body.GetGenericArguments());
                if(call is not null)calls.Add(call);
            }
            index+=size;
        }
        return calls;
    }
}
