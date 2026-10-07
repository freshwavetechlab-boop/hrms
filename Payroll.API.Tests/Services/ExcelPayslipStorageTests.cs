using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipStorageTests
{
    private const string BatchId = "0123456789abcdef0123456789abcdef";
    private const string Code = "excel_payslip:" + BatchId;
    private const string Snapshot = """
        {
          "id":"0123456789abcdef0123456789abcdef", "clientId":11,
          "clientName":"Example Client", "month":"2026-09", "sourceFileName":"Salary.xlsx",
          "sheetName":"September", "headerRow":5, "createdAtUtc":"2026-10-08T06:07:08.123456Z",
          "createdBy":"operator@example.test", "futureMetadata":{"keep":"unchanged"},
          "rows":[
            {
              "id":"sheet1:6", "sourceRow":6, "employeeName":"Same Name", "employeeCode":"",
              "email":"", "information":[{"label":"Account","value":"001234567890"},{"label":"Location","value":"Branch A"}],
              "earnings":[{"label":"Wages","amount":15414}],
              "deductions":[{"label":"PF","amount":1849.68},{"label":"ESI","amount":115.605}],
              "employerContributions":[{"label":"Employer ESI","amount":500.955}],
              "netPay":13448.715, "declaredGross":15414, "declaredDeductions":1965.285
            },
            {
              "id":"sheet1:7", "sourceRow":7, "employeeName":"Same Name", "employeeCode":"",
              "information":[{"label":"Location","value":"Branch B"}],
              "earnings":[{"label":"Wages","amount":-100.125}], "deductions":[],
              "netPay":-100.125
            }
          ]
        }
        """;

    [Fact]
    public void LegacyBatchPreservesSourcePrecisionIdentityTextAndAuditMetadata()
    {
        var batch = ExcelPayslipRepository.ValidateLegacyBatch(11, Code, Snapshot);
        Assert.Equal(BatchId, batch.Id);
        Assert.Equal(11, batch.ClientId);
        Assert.Equal(new DateTime(2026, 10, 8, 6, 7, 8, DateTimeKind.Utc).AddTicks(1234560), batch.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, batch.CreatedAtUtc.Kind);
        Assert.Equal("operator@example.test", batch.CreatedBy);
        Assert.Equal("2026-09", batch.Month);
        Assert.Equal("Salary.xlsx", batch.SourceFileName);
        Assert.Equal(2, batch.Rows.Count);
        Assert.Equal(batch.Rows[0].EmployeeName, batch.Rows[1].EmployeeName);
        Assert.NotEqual(batch.Rows[0].Id, batch.Rows[1].Id);
        Assert.Equal(6, batch.Rows[0].SourceRow);
        Assert.Equal(7, batch.Rows[1].SourceRow);
        Assert.Equal("001234567890", batch.Rows[0].Information[0].Value);
        Assert.Equal(115.605m, batch.Rows[0].Deductions[1].Amount);
        Assert.Equal(500.955m, batch.Rows[0].EmployerContributions[0].Amount);
        Assert.Equal(13448.715m, batch.Rows[0].NetPay);
        Assert.Equal(1965.285m, batch.Rows[0].DeclaredDeductions);
        Assert.Equal(-100.125m, batch.Rows[1].NetPay);
    }

    [Theory]
    [InlineData(0, Code)]
    [InlineData(12, Code)]
    [InlineData(11, "excel_payslip:fedcba9876543210fedcba9876543210")]
    [InlineData(11, "excel_payslip:not-a-guid")]
    [InlineData(11, "excel_payslip_profile")]
    public void LegacyBatchRejectsWrongClientOrStorageIdentity(int clientId, string code) =>
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(clientId, code, Snapshot));

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0999-12-31T23:59:59Z")]
    public void LegacyBatchRejectsDatesOutsideMysqlDatetimeRange(string date)
    {
        var invalid = JsonNode.Parse(Snapshot)!;
        invalid["createdAtUtc"] = date;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, invalid.ToJsonString()));
    }

    [Fact]
    public void LegacyBatchRejectsMissingTimestampOrEmptySnapshot()
    {
        var invalid = JsonNode.Parse(Snapshot)!.AsObject();
        invalid.Remove("createdAtUtc");
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, invalid.ToJsonString()));
        invalid = JsonNode.Parse(Snapshot)!.AsObject();
        invalid["rows"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, invalid.ToJsonString()));
        invalid["rows"] = null;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, invalid.ToJsonString()));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, "null"));
        Assert.Throws<JsonException>(() => ExcelPayslipRepository.ValidateLegacyBatch(11, Code, "{broken-json"));
    }

    [Fact]
    public void RepeatedMigrationAcceptsEquivalentObjectPropertyOrdering()
    {
        var source = JsonNode.Parse(Snapshot)!.AsObject();
        var reordered = new JsonObject(source.Reverse().Select(property =>
            new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
        ExcelPayslipRepository.EnsureSameSnapshot(null, Snapshot);
        ExcelPayslipRepository.EnsureSameSnapshot(Snapshot, Snapshot);
        ExcelPayslipRepository.EnsureSameSnapshot(reordered.ToJsonString(), Snapshot);
    }

    [Fact]
    public void RepeatedMigrationRejectsChangedAmountsRowsTextAndUnknownFields()
    {
        foreach (var mutate in new Action<JsonNode>[]
        {
            node => node["rows"]![0]!["netPay"] = 13448.72m,
            node => node["rows"]![0]!["information"]![0]!["value"] = "1234567890",
            node => node["rows"]!.AsArray().RemoveAt(1),
            node => node["futureMetadata"]!["keep"] = "changed"
        })
        {
            var changed = JsonNode.Parse(Snapshot)!;
            mutate(changed);
            Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.EnsureSameSnapshot(changed.ToJsonString(), Snapshot));
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.EnsureSameSnapshot(
            "{\"amount\":13448.71500000000000001}", "{\"amount\":13448.71500000000000002}"));
    }

    [Theory]
    [InlineData("excel_payslip_mail:")]
    [InlineData("excel_payslip_send:")]
    public void LegacyEmailsAndReceiptsStopMigrationWithoutDiscardingUnsupportedMetadata(string prefix)
    {
        var codes = new[] { Code, prefix + BatchId, "excel_payslip_profile" };
        var before = codes.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.EnsureLegacyDeliveryCanMigrate(codes));
        Assert.Contains("No existing records were changed", error.Message);
        Assert.Equal(before, codes);
    }

    [Fact]
    public void MappingProfilesAndBatchesAloneDoNotTriggerLegacyEmailGuard() =>
        ExcelPayslipRepository.EnsureLegacyDeliveryCanMigrate([Code, "excel_payslip_profile", "attendance_devices"]);
}
