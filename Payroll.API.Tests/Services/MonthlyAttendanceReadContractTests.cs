using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public class MonthlyAttendanceReadContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-09-01")]
    [InlineData("bad-day")]
    public async Task Invalid_period_returns_without_database_access(string month)
    {
        var repository = new LeaveAttendanceRepository(null!);
        Assert.Empty(await repository.GetMonthlyAttendanceAsync(1, month));
    }

    [Fact]
    public void Monthly_read_does_not_invoke_schema_or_write_commands()
    {
        var method = typeof(LeaveAttendanceRepository).GetMethod(nameof(LeaveAttendanceRepository.GetMonthlyAttendanceAsync))!;
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var body = machine.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var calls = CalledMethods(body).ToList();
        Assert.Contains(calls, call => call.DeclaringType?.FullName == "Dapper.SqlMapper" && call.Name == "QueryAsync");
        Assert.DoesNotContain(calls, call => call.Name.StartsWith("Ensure", StringComparison.Ordinal)
            || call.Name.Contains("Initialize", StringComparison.Ordinal)
            || call.DeclaringType?.FullName == "Dapper.SqlMapper" && call.Name.StartsWith("Execute", StringComparison.Ordinal));
    }

    // Decode emitted call operands without opening a database. This guards the
    // read endpoint's schema/write boundary in the compiled async implementation.
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var index = 0; index < bytes.Length;)
        {
            ushort value = bytes[index++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[index++]);
            var code = codes[value];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, index),
                _ => 4
            };
            if (code.OperandType == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
                if (called is not null) yield return called;
            }
            index += size;
        }
    }
}
