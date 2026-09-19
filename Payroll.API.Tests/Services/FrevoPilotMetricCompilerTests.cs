using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class FrevoPilotMetricCompilerTests
{
    private const string Offers = "Count offer records grouped by Status, all clients and all dates.";
    private const string Applications = "Count candidate applications by job title, ApplicationType='Application' only. Include jobs having applications only.";
    private const string SumLeave = "Sum requested leave Days by Status across all clients and all dates.";
    private const string AverageLeave = "Average requested leave Days by Status across all clients and all dates. Round averages to two decimal places.";
    private sealed record Scenario(string Id, string Question, string Sql, string[] Parameters, int? ClientId = null);
    private static readonly Scenario[] Scenarios =
    [
        new("interviews", "Count all interview records by Status. All dates, all clients.", "SELECT `r`.`Status` AS `label`, COUNT(*) AS `value` FROM `recruitment_interviews` AS `r` GROUP BY `r`.`Status`", []),
        new("offers", Offers, "SELECT `r`.`Status` AS `label`, COUNT(*) AS `value` FROM `recruitment_offers` AS `r` GROUP BY `r`.`Status`", []),
        new("rru-gender", "Count active RRU employees by Gender.", "SELECT `r`.`Gender` AS `label`, COUNT(*) AS `value` FROM `employees` AS `r` LEFT JOIN `clients` AS `t1` ON `r`.`ClientId` = `t1`.`Id` WHERE `r`.`IsActive` = ? AND `t1`.`Code` = ? GROUP BY `r`.`Gender`", ["1", "RRU"], 28),
        new("applications", Applications, "SELECT `t1`.`PositionTitle` AS `label`, COUNT(*) AS `value` FROM `recruitment_candidate_applications` AS `r` INNER JOIN `recruitment_open_positions` AS `t1` ON `r`.`PositionId` = `t1`.`Id` WHERE `r`.`ApplicationType` = ? GROUP BY `t1`.`PositionTitle`", ["Application"]),
        new("leave", SumLeave, "SELECT `r`.`Status` AS `label`, SUM(`r`.`Days`) AS `value` FROM `essleaverequests` AS `r` GROUP BY `r`.`Status`", []),
        new("employees-client", "Count active employees by client. All clients.", "SELECT `t1`.`Code` AS `label`, COUNT(*) AS `value` FROM `employees` AS `r` LEFT JOIN `clients` AS `t1` ON `r`.`ClientId` = `t1`.`Id` WHERE `r`.`IsActive` = ? GROUP BY `t1`.`Code`", ["1"]),
        new("inactive-gender", "Count inactive employees by Gender. All clients.", "SELECT `r`.`Gender` AS `label`, COUNT(*) AS `value` FROM `employees` AS `r` WHERE `r`.`IsActive` = ? GROUP BY `r`.`Gender`", ["0"]),
        new("rru-departments", "Count active RRU employees by Department.", "SELECT `r`.`Department` AS `label`, COUNT(*) AS `value` FROM `employees` AS `r` LEFT JOIN `clients` AS `t1` ON `r`.`ClientId` = `t1`.`Id` WHERE `r`.`IsActive` = ? AND `t1`.`Code` = ? GROUP BY `r`.`Department`", ["1", "RRU"], 28),
        new("applications-status", "Count candidate applications by Status. All dates, all clients.", "SELECT `r`.`CurrentStatus` AS `label`, COUNT(*) AS `value` FROM `recruitment_candidate_applications` AS `r` WHERE `r`.`ApplicationType` = ? GROUP BY `r`.`CurrentStatus`", ["Application"]),
        new("rru-applications-stage", "Count RRU candidate applications by CurrentStage. All dates.", "SELECT `r`.`CurrentStage` AS `label`, COUNT(*) AS `value` FROM `recruitment_candidate_applications` AS `r` LEFT JOIN `clients` AS `t1` ON `r`.`ClientId` = `t1`.`Id` WHERE `t1`.`Code` = ? AND `r`.`ApplicationType` = ? GROUP BY `r`.`CurrentStage`", ["RRU", "Application"], 28),
        new("leave-count", "Count leave requests by Status. All clients, all dates.", "SELECT `r`.`Status` AS `label`, COUNT(*) AS `value` FROM `essleaverequests` AS `r` GROUP BY `r`.`Status`", []),
        new("leave-average", AverageLeave, "SELECT `r`.`Status` AS `label`, ROUND(AVG(`r`.`Days`), 2) AS `value` FROM `essleaverequests` AS `r` GROUP BY `r`.`Status`", [])
    ];

    [Fact]
    public void AllTwelveHostRecognizedScenariosCompileToExactFreshQueryPlansDeterministically()
    {
        var fixtures = new List<object>();
        foreach (var scenario in Scenarios)
        {
            var contract = HostContract(scenario.Question, scenario.ClientId.HasValue ? "RRU" : "All clients");
            Assert.True(FrevoPilotMetricCompiler.TryCompile(contract, out var payload), scenario.Id);
            Assert.Equal(scenario.Sql, payload.GetProperty("sql").GetString());
            Assert.Equal(scenario.Parameters, payload.GetProperty("parameters").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { "sql", "parameters" }, payload.EnumerateObject().Select(property => property.Name));
            Assert.True(FrevoPilotMetricCompiler.TryCompile(contract, out var repeated));
            Assert.Equal(payload.GetRawText(), repeated.GetRawText());
            fixtures.Add(new { scenario.Id, scenario.ClientId, contract, payload });
        }
        var scopedInterviewContract = HostContract("Count interviews by Status", "RRU");
        Assert.True(FrevoPilotMetricCompiler.TryCompile(scopedInterviewContract, out var scopedInterviewPayload));
        fixtures.Add(new { Id = "scoped-interviews", ClientId = 28, contract = scopedInterviewContract, payload = scopedInterviewPayload });
        // Optional offline fixture export lets the existing Node guards check the
        // exact .NET output. No database, network or inference is involved.
        if (Environment.GetEnvironmentVariable("FREVOPILOT_METRIC_FIXTURE_PATH") is { Length: > 0 } fixturePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
            File.WriteAllText(fixturePath, JsonSerializer.Serialize(fixtures));
        }
    }

    [Theory]
    [InlineData("Grade")]
    [InlineData("Band")]
    public void AnySafeGroupingColumnProvenByHostSchemaIsPreserved(string column)
    {
        Assert.True(FrevoPilotMetricCompiler.TryCompile(HostContract("Count employees by " + column), out var payload));
        Assert.Equal($"SELECT `r`.`{column}` AS `label`, COUNT(*) AS `value` FROM `employees` AS `r` GROUP BY `r`.`{column}`", payload.GetProperty("sql").GetString());
    }

    [Fact]
    public void ScopedInterviewsRetainRequiredParentWithoutImplicitApplicationFilter()
    {
        var contract = HostContract("Count interviews by Status", "RRU");
        Assert.True(FrevoPilotMetricCompiler.TryCompile(contract, out var payload));
        Assert.Equal("SELECT `r`.`Status` AS `label`, COUNT(*) AS `value` FROM `recruitment_interviews` AS `r` INNER JOIN `recruitment_candidate_applications` AS `t1` ON `r`.`ApplicationId` = `t1`.`Id` GROUP BY `r`.`Status`", payload.GetProperty("sql").GetString());
        Assert.Empty(payload.GetProperty("parameters").EnumerateArray());
    }

    [Fact]
    public void ClientGroupingUsesTwoRequiredParentJoinsAndNoUnrequestedFilters()
    {
        Assert.True(FrevoPilotMetricCompiler.TryCompile(HostContract("Count interviews by client"), out var payload));
        Assert.Equal("SELECT `t2`.`Code` AS `label`, COUNT(*) AS `value` FROM `recruitment_interviews` AS `r` LEFT JOIN `recruitment_candidate_applications` AS `t1` ON `r`.`ApplicationId` = `t1`.`Id` LEFT JOIN `clients` AS `t2` ON `t1`.`ClientId` = `t2`.`Id` GROUP BY `t2`.`Code`", payload.GetProperty("sql").GetString());
        Assert.Empty(payload.GetProperty("parameters").EnumerateArray());
    }

    [Fact]
    public void OptionalJobsUseLeftJoinAndAverageIsNotRoundedUnlessRequested()
    {
        Assert.True(FrevoPilotMetricCompiler.TryCompile(HostContract("Count applications by job title"), out var jobs));
        Assert.Contains(" LEFT JOIN `recruitment_open_positions`", jobs.GetProperty("sql").GetString());
        Assert.True(FrevoPilotMetricCompiler.TryCompile(HostContract("Average requested leave Days by Status across all clients and all dates."), out var average));
        Assert.Contains("AVG(`r`.`Days`) AS `value`", average.GetProperty("sql").GetString());
        Assert.DoesNotContain("ROUND", average.GetProperty("sql").GetString());
    }

    [Fact]
    public void MalformedUnsupportedAndMeaningChangingMetadataFailsClosed()
    {
        var cases = new (string Question, Action<JsonObject> Change)[]
        {
            (Offers, value => value["version"] = "unknown"),
            (Offers, value => value["measure"] = "maximum_values"),
            (Offers, value => value["recordTable"] = "users"),
            (Offers, value => value["grouping"]!["column"] = "Status;DROP"),
            (Offers, value => value["grouping"]!["column"] = "r.Status"),
            (Offers, value => value["grouping"]!["table"] = "employees"),
            (Offers, value => value["where"] = "Status IS NOT NULL"),
            (Offers, value => value["timeScope"] = "Current month"),
            (Offers, value => value["noOtherFiltersRequested"] = false),
            (Offers, value => value["allowedTables"]!.AsArray().Add("users")),
            (Offers, value => value["allowedTables"]!.AsArray().Add("recruitment_offers")),
            (Offers, value => value["requiredRelationships"]![0]!["leftColumn"] = "UnknownJoin"),
            (Offers, value => value["requiredRelationships"]![0]!["rightColumn"] = "Code"),
            (Offers, value => value["requiredRelationships"]![0]!["requiredJoinType"] = "INNER JOIN"),
            (Offers, value => value["requestedFilters"]!.AsArray().Add(Filter("recruitment_offers", "Status", "Approved"))),
            (Offers, value => value["requestedFilters"]!.AsArray().Add(Filter("clients", "Code", "RRU' OR 1=1"))),
            (Offers, value => value["recordDefinitionFilters"]!.AsArray().Add(Filter("recruitment_offers", "ApplicationType", "Application"))),
            ("Count active employees by Gender", value => value["requestedFilters"]![0]!["operator"] = "<>"),
            ("Count active employees by Gender", value => value["requestedFilters"]![0]!["value"] = 2),
            ("Count active employees by Gender", value => value["requestedFilters"]![0]!["value"] = "1"),
            ("Count active employees by Gender", value => value["requestedFilters"]!.AsArray().Add(value["requestedFilters"]![0]!.DeepClone())),
            ("Count employees by client", value => value["requiredRelationships"] = new JsonArray()),
            (Applications, value => value["recordDefinitionFilters"] = new JsonArray()),
            (Applications, value => value["recordDefinitionFilters"]![0]!["column"] = "SourceType"),
            (Applications, value => value["recordDefinitionFilters"]![0]!["value"] = "TalentPool"),
            (Applications, value => value["requiredRelationships"]![1]!["requiredJoinType"] = "RIGHT JOIN"),
            (SumLeave, value => value["measureColumn"]!["column"] = "EmployeeId"),
            (SumLeave, value => value["measureColumn"]!["table"] = "employees"),
            (SumLeave, value => value["grouping"]!["column"] = "Days"),
            (SumLeave, value => value["roundDigits"] = 2),
            (AverageLeave, value => value["roundDigits"] = 3),
            (Offers, value => value["measureColumn"] = new JsonObject { ["table"] = "recruitment_offers", ["column"] = "OfferedCtc" })
        };
        foreach (var (question, change) in cases)
        {
            var contract = JsonNode.Parse(HostContract(question).GetRawText())!.AsObject();
            change(contract);
            Assert.False(FrevoPilotMetricCompiler.TryCompile(JsonSerializer.SerializeToElement(contract), out var result), contract.ToJsonString());
            Assert.Equal(JsonValueKind.Undefined, result.ValueKind);
        }
        Assert.False(FrevoPilotMetricCompiler.TryCompile(default, out _));
        Assert.False(FrevoPilotMetricCompiler.TryCompile(JsonSerializer.SerializeToElement(new { }), out _));
        var duplicate = HostContract(Offers).GetRawText()[..^1] + ",\"version\":\"simple-count-v1\"}";
        using var document = JsonDocument.Parse(duplicate);
        Assert.False(FrevoPilotMetricCompiler.TryCompile(document.RootElement, out _));
    }

    private static JsonObject Filter(string table, string column, string value) =>
        new() { ["table"] = table, ["column"] = column, ["operator"] = "=", ["value"] = value };

    private static JsonElement HostContract(string question, string scope = "All clients")
    {
        var tables = new Dictionary<string, string[]>
        {
            ["employees"] = ["Id:int", "ClientId:int", "Gender:varchar", "Department:varchar", "Grade:varchar", "Band:varchar", "IsActive:tinyint"],
            ["clients"] = ["Id:int", "Code:varchar", "Name:varchar", "IsActive:tinyint"],
            ["recruitment_interviews"] = ["Id:int", "ApplicationId:int", "Status:varchar"],
            ["recruitment_offers"] = ["Id:int", "ClientId:int", "Status:varchar"],
            ["recruitment_candidate_applications"] = ["Id:int", "ClientId:int", "PositionId:int", "ApplicationType:varchar", "CurrentStatus:varchar", "CurrentStage:varchar"],
            ["recruitment_open_positions"] = ["Id:int", "ClientId:int", "PositionTitle:varchar", "Status:varchar"],
            ["essleaverequests"] = ["Id:int", "ClientId:int", "EmployeeId:int", "Days:decimal", "Status:varchar"]
        };
        var prompt = JsonSerializer.Serialize(new
        {
            userQuestion = question, mysqlDatabase = "payroll",
            relevantSchema = tables.Select(table => new { table = table.Key, columns = table.Value }),
            approvedMetricsAndTerminology = new
            {
                scope, relationships = new[] { "employees.ClientId -> clients.Id", "recruitment_candidate_applications.ClientId -> clients.Id" },
                rules = new[] { "Tenant scope is mandatory.", "ApplicationType='Application' defines job applications." }
            }
        });
        using var compact = JsonDocument.Parse(LocalLlmAnalyticsPrompt.Build(prompt, "SELECT-only. Keep exact meaning.", "{\"properties\":{\"sql\":{},\"parameters\":{}}}").Prompt);
        return compact.RootElement.GetProperty("queryFocus").Clone();
    }
}
