using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipEmailUpdateTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Snapshot = """
      {"id":"0123456789abcdef0123456789abcdef","clientId":42,"month":"2026-09","createdBy":"original",
       "futureField":{"keep":"untouched"},"rows":[
        {"id":"one","sourceRow":2,"employeeCode":"00001","employeeName":"Same Name","email":"",
         "information":[{"label":"Work location","value":"A"},{"label":"Account","value":"000123"}],
         "earnings":[{"label":"Basic","amount":12345.678901234567890123}],"netPay":12000.123456789012345678},
        {"id":"two","sourceRow":3,"employeeCode":"00001","employeeName":"Same Name","email":"old@example.test",
         "information":[{"label":"Work location","value":"B"}],"earnings":[],"netPay":1}]}
      """;
    private const string Source = """
      {"columns":[{"kind":"email","columnIndex":1}],"rows":[
       {"sourceRow":2,"cells":[{"value":"12345.678901234567890123","formulaText":"A1*30"},{"value":"","formulaText":"oldFormula"}]},
       {"sourceRow":3,"cells":[{"value":"001234"},{"value":"old@example.test"}]}],
       "excludedSourceRows":[7],"futureSource":{"keep":true}}
      """;
    private static ExcelPayslipEmailUpdates Request() => new() { Month = "2026-09", SourceFileName = "contacts.xlsx", Updates = [
        new() { RowId = "one", EmployeeCode = "00001", EmployeeName = "Same Name", Location = "A", PreviousEmail = "", Email = "first@example.test" }] };
    private static (string BatchJson, string? SourceJson, int Changed) Patch(ExcelPayslipEmailUpdates request, string snapshot = Snapshot, string? source = Source, int client = 42) =>
        ExcelPayslipRepository.PatchEmailDocuments(snapshot, source, client, Id, request, "editor@example.test", new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void EmailOnlyPatchPreservesSalaryPrecisionIdentifiersUnknownFieldsAndOtherLocation()
    {
        var patched = Patch(Request());
        Assert.Equal(1, patched.Changed);
        Assert.Contains("12345.678901234567890123", patched.BatchJson);
        Assert.Contains("12000.123456789012345678", patched.BatchJson);
        var before = JsonNode.Parse(Snapshot)!;
        var after = JsonNode.Parse(patched.BatchJson)!;
        Assert.Equal("first@example.test", after["rows"]![0]!["email"]!.GetValue<string>());
        Assert.Equal("editor@example.test", after["emailUpdateHistory"]![0]!["actor"]!.GetValue<string>());
        Assert.Equal("", after["emailUpdateHistory"]![0]!["changes"]![0]!["previousEmail"]!.GetValue<string>());
        after.AsObject().Remove("emailUpdateHistory"); after["rows"]![0]!["email"] = "";
        Assert.True(JsonNode.DeepEquals(before, after));
        var sourceBefore = JsonNode.Parse(Source)!;
        var sourceAfter = JsonNode.Parse(patched.SourceJson!)!;
        Assert.Equal("first@example.test", sourceAfter["rows"]![0]!["cells"]![1]!["value"]!.GetValue<string>());
        sourceAfter["rows"]![0]!["cells"]![1] = sourceBefore["rows"]![0]!["cells"]![1]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(sourceBefore, sourceAfter));
        Assert.DoesNotContain("send_state", patched.BatchJson);
    }

    [Fact]
    public void RetryDoesNotDuplicateAuditOrOverwriteAThirdPartyEdit()
    {
        var request = Request(); var saved = Patch(request);
        var retry = Patch(request, saved.BatchJson, saved.SourceJson);
        Assert.Equal(0, retry.Changed); Assert.Equal(saved.BatchJson, retry.BatchJson);
        request.Updates[0].Email = "different@example.test";
        Assert.Contains("changed after", Assert.Throws<InvalidOperationException>(() => Patch(request, saved.BatchJson, saved.SourceJson)).Message);
    }

    [Fact]
    public void RejectsWrongBatchMonthIdentityDuplicateAndInvalidEmailAtomically()
    {
        foreach (var mutate in new Action<ExcelPayslipEmailUpdates>[] {
            r => r.Month = "2026-08", r => r.Updates[0].RowId = "foreign", r => r.Updates[0].EmployeeCode = "other",
            r => r.Updates[0].Location = "B", r => r.Updates[0].EmployeeName = "different", r => r.Updates[0].Email = "",
            r => r.Updates[0].Email = "first@example.test, second@example.test", r => r.Updates[0].PreviousEmail = "stale@example.test",
            r => r.Updates.Add(r.Updates[0]), r => r.Updates.Clear() })
        { var request = Request(); mutate(request); Assert.Throws<InvalidOperationException>(() => Patch(request)); }
        Assert.Throws<InvalidOperationException>(() => Patch(Request(), client: 43));
        Assert.Equal(1, Patch(Request(), source: null).Changed);
    }
}
