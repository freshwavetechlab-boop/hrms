using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class SelectedDraftWorkerSqlTests
{
    [Fact]
    public void Compiled_global_worker_polls_only_Queued_and_cannot_select_the_held_status()
    {
        var strings=StringsInAsyncMethod(typeof(PayRunRepository).GetMethod(nameof(PayRunRepository.ProcessQueuedAsync))!);
        var poll=Assert.Single(strings,text=>text.Contains("SELECT Id FROM payruns",StringComparison.Ordinal));
        Assert.Contains("WHERE Status='Queued'",poll,StringComparison.Ordinal);
        Assert.DoesNotContain(SelectedPayRunDraft.HeldStatus,poll,StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_worker_claim_stays_Queued_only_while_selected_claim_requires_exact_reviewed_status()
    {
        var method=typeof(PayRunRepository).GetMethod("ProcessQueuedRunCoreAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var sql=StringsInAsyncMethod(method);
        Assert.Contains(sql,text=>text.StartsWith("SELECT CAST(RequestJson AS CHAR)",StringComparison.Ordinal)&&text.Contains("Status='Queued'",StringComparison.Ordinal));
        Assert.Contains(sql,text=>text.StartsWith("UPDATE payruns SET Status='Processing'",StringComparison.Ordinal)&&text.EndsWith("AND Status='Queued'",StringComparison.Ordinal));
        Assert.Contains(sql,text=>text.StartsWith("UPDATE payruns SET Status='Processing'",StringComparison.Ordinal)&&text.EndsWith("AND Status=@ExpectedStatus",StringComparison.Ordinal));
    }

    // Inspect the SQL constants in the actual compiled async methods without
    // opening a database or replacing the production worker with a mock.
    private static List<string> StringsInAsyncMethod(MethodInfo method)
    {
        var machine=method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var move=machine.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var body=move.GetMethodBody()!.GetILAsByteArray()!;
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Select(field=>(OpCode)field.GetValue(null)!).ToDictionary(code=>unchecked((ushort)code.Value));
        var strings=new List<string>();
        for(var index=0;index<body.Length;)
        {
            ushort value=body[index++];if(value==0xfe)value=(ushort)(0xfe00|body[index++]);
            var code=codes[value];
            if(code.OperandType==OperandType.InlineString)strings.Add(move.Module.ResolveString(BitConverter.ToInt32(body,index)));
            index+=code.OperandType switch
            {
                OperandType.InlineNone=>0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(body,index),
                _=>4
            };
        }
        return strings;
    }
}
