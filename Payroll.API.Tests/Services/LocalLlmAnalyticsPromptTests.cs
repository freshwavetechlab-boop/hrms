using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class LocalLlmAnalyticsPromptTests
{
    private const string PlanSchema = "{\"properties\":{\"sql\":{},\"parameters\":{}}}";
    private const string InsightSchema = "{\"properties\":{\"summary\":{}}}";

    [Fact]
    public void CompactPlanKeepsEveryColumnRelationAndSecurityInstruction()
    {
        const string system = "SELECT only. Never expose secrets. Respect tenant scope.";
        var prompt = JsonSerializer.Serialize(new
        {
            userQuestion = "Active employees by client", mysqlDatabase = "payroll",
            relevantSchema = new[] { new { table = "employees", rowEstimate = 50, columns = new[] { "Id:int", "ClientId:int", "IsActive:tinyint" } } },
            approvedMetricsAndTerminology = new { relationships = new[] { "employees.ClientId -> clients.Id" }, rules = new[] { "IsActive=1" } },
            hrmsBusinessKnowledge = new[] { new { source = "rules.md", content = "Preserve employee grain.", relevance = 9, matchedTerms = new[] { "employee" } } },
            failedPlan = new { sql = "SELECT bad FROM employees", parameters = new[] { "RRU" }, title = "Old title" },
            validationError = "Unknown column bad"
        });
        var actual = LocalLlmAnalyticsPrompt.Build(prompt, system, PlanSchema);
        using var json = JsonDocument.Parse(actual.Prompt);
        var root = json.RootElement;
        Assert.Equal(new[] { "Id", "ClientId", "IsActive" }, root.GetProperty("relevantSchema").GetProperty("employees").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("employees.ClientId -> clients.Id", root.GetProperty("approvedMetricsAndTerminology").GetProperty("relationships")[0].GetString());
        Assert.Equal("Unknown column bad", root.GetProperty("validationError").GetString());
        Assert.Equal("RRU", root.GetProperty("failedPlan").GetProperty("parameters")[0].GetString());
        Assert.StartsWith(system, actual.SystemInstruction);
        Assert.Contains("\"sql\"", actual.SystemInstruction);
        Assert.DoesNotContain("rowEstimate", actual.Prompt);
        Assert.DoesNotContain("matchedTerms", actual.Prompt);
    }

    [Fact]
    public void OversizedMandatorySchemaFailsInsteadOfDroppingScopeOrFields()
    {
        var prompt = JsonSerializer.Serialize(new
        {
            relevantSchema = new[] { new { table = "employees", columns = Enumerable.Range(0, 300).Select(i => $"column_{i}_" + new string('x', 60) + ":varchar") } },
            approvedMetricsAndTerminology = new { rules = new[] { "ClientId=28" } }
        });
        var error = Assert.Throws<InvalidOperationException>(() => LocalLlmAnalyticsPrompt.Build(prompt, "SELECT only", PlanSchema));
        Assert.Contains("no schema or access rules were removed", error.Message);
    }

    [Fact]
    public void OversizedOptionalKnowledgeIsOmittedAsAWholeChunk()
    {
        var prompt = JsonSerializer.Serialize(new
        {
            relevantSchema = new[] { new { table = "employees", columns = new[] { "Id:int", "ClientId:int" } } },
            hrmsBusinessKnowledge = new[] { new { source = "large.md", content = new string('x', 15_000) }, new { source = "small.md", content = "Count active employees." } }
        });
        var result = LocalLlmAnalyticsPrompt.Build(prompt, "SELECT only", PlanSchema);
        using var json = JsonDocument.Parse(result.Prompt);
        var knowledge = json.RootElement.GetProperty("hrmsBusinessKnowledge");
        Assert.Equal(1, knowledge.GetArrayLength());
        Assert.Equal("small.md", knowledge[0].GetProperty("source").GetString());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Prompt) + Encoding.UTF8.GetByteCount(result.SystemInstruction), 1, 12_000);
    }

    [Fact]
    public void LargeInsightsKeepWholeRowsAndMarkTruncation()
    {
        var prompt = JsonSerializer.Serialize(new
        {
            question = "Headcount by campus", rowCount = 80, rowsTruncated = false,
            resultRows = Enumerable.Range(0, 80).Select(i => new { campus = new string('x', 300) + i, count = i + 1 }),
            metricFields = new[] { "count" }
        });
        var result = LocalLlmAnalyticsPrompt.Build(prompt, "Never invent a number.", InsightSchema);
        using var json = JsonDocument.Parse(result.Prompt);
        var root = json.RootElement;
        Assert.True(root.GetProperty("rowsTruncated").GetBoolean());
        Assert.Equal(80, root.GetProperty("rowCount").GetInt32());
        Assert.InRange(root.GetProperty("resultRows").GetArrayLength(), 1, 79);
        Assert.Equal(1, root.GetProperty("resultRows")[0].GetProperty("count").GetInt32());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Prompt) + Encoding.UTF8.GetByteCount(result.SystemInstruction), 1, 12_000);
    }

    [Fact]
    public void OriginalTruncationAndEmptyResultsAreNotRecastAsZeroTotals()
    {
        var result = LocalLlmAnalyticsPrompt.Build("{\"question\":\"Total payroll\",\"resultRows\":[],\"rowCount\":0,\"rowsTruncated\":true}", "Facts only.", InsightSchema);
        using var json = JsonDocument.Parse(result.Prompt);
        Assert.True(json.RootElement.GetProperty("rowsTruncated").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("resultRows").EnumerateArray());
        Assert.Contains("never a whole-result total", result.SystemInstruction);
    }

    [Fact]
    public void LocalInsightsOmitOptionalKnowledgeButPreserveQuestionRowsAndMandatoryInstructions()
    {
        const string mandatory = "Summarize returned rows only. Never invent advice or disclose private fields.";
        var prompt = JsonSerializer.Serialize(new
        {
            question = "Interview records by status", rowCount = 2, rowsTruncated = false,
            resultRows = new[] { new { status = "Completed", interviews = 1 }, new { status = "Scheduled", interviews = 3 } },
            metricFields = new[] { "interviews" },
            supportingBusinessKnowledge = new[] { new { source = "rules.md", content = "Optional unrelated payroll rules " + new string('x', 5000) } }
        });
        var actual = LocalLlmAnalyticsPrompt.Build(prompt, mandatory, InsightSchema);
        var root = JsonNode.Parse(actual.Prompt)!;
        Assert.StartsWith(mandatory, actual.SystemInstruction);
        Assert.Equal("Interview records by status", root["question"]!.GetValue<string>());
        Assert.Equal(2, root["rowCount"]!.GetValue<int>());
        Assert.Equal(2, root["shownRowCount"]!.GetValue<int>());
        Assert.False(root["rowsTruncated"]!.GetValue<bool>());
        Assert.Equal(2, root["resultRows"]!.AsArray().Count);
        Assert.Equal("Completed", root["resultRows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(3, root["resultRows"]![1]!["interviews"]!.GetValue<int>());
        Assert.Equal("interviews", root["metricFields"]![0]!.GetValue<string>());
        Assert.Empty(root["supportingBusinessKnowledge"]!.AsArray());
        Assert.DoesNotContain("unrelated payroll", actual.Prompt);
    }

    [Fact]
    public void UnsupportedContractFailsExplicitly() =>
        Assert.Throws<InvalidOperationException>(() => LocalLlmAnalyticsPrompt.Build("{}", "Instructions", "{\"properties\":{\"unknown\":{}}}"));

    [Theory]
    [InlineData("Count all interview records by Status. All dates, all clients.", "recruitment_interviews=Id,Status")]
    [InlineData("Count active RRU employees by Gender.", "clients=Id,Code;employees=Id,ClientId,Gender,IsActive")]
    [InlineData("Number of offers grouped by Status", "clients=Id;recruitment_offers=Id,ClientId,Status")]
    [InlineData("Total candidate applications by status", "clients=Id;recruitment_candidate_applications=Id,ClientId,CurrentStatus,ApplicationType")]
    [InlineData("Count leave requests by Status", "clients=Id;essleaverequests=Id,ClientId,Status")]
    [InlineData("Count staff by Department", "clients=Id;employees=Id,ClientId,Department")]
    [InlineData("Count active employees by client. All clients.", "clients=Id,Code;employees=Id,ClientId,IsActive")]
    public void SimpleCountSubjectsKeepRequiredColumnsAndRules(string question, string expectedSchema)
    {
        var input = CountPrompt(question);
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "SELECT only. Do not invent filters.", PlanSchema);
        var actual = JsonNode.Parse(result.Prompt)!;
        AssertProjectedSchema(actual, expectedSchema);
        Assert.Equal(question, actual["userQuestion"]!.GetValue<string>());
        Assert.Equal(input["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString(), actual["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString());
        Assert.StartsWith("SELECT only. Do not invent filters.", result.SystemInstruction);
        Assert.Contains("simple unqualified alphanumeric table aliases such as a and p, never dotted aliases", result.SystemInstruction);
    }

    [Theory]
    [InlineData("Count offer records grouped by Status, all clients and all dates.")]
    [InlineData("Count offer records grouped by Status. All dates and all clients.")]
    [InlineData("COUNT OFFER RECORDS GROUPED BY STATUS, ALL CLIENTS, AND ALL DATES.")]
    public void ConjoinedAllScopesPreserveExactCountContract(string question)
    {
        var baseline = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(
            CountPrompt("Count offer records grouped by Status, all clients, all dates.").ToJsonString(), "No implicit filters.", PlanSchema).Prompt)!;
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(
            CountPrompt(question).ToJsonString(), "No implicit filters.", PlanSchema).Prompt)!;

        Assert.NotNull(actual["queryFocus"]);
        Assert.Equal(baseline["queryFocus"]!.ToJsonString(), actual["queryFocus"]!.ToJsonString());
        Assert.Equal(baseline["relevantSchema"]!.ToJsonString(), actual["relevantSchema"]!.ToJsonString());
        Assert.Equal(question, actual["userQuestion"]!.GetValue<string>());
        Assert.Empty(actual["queryFocus"]!["requestedFilters"]!.AsArray());
        Assert.Empty(actual["queryFocus"]!["recordDefinitionFilters"]!.AsArray());
    }

    [Theory]
    [InlineData("Sum requested leave Days by Status across all clients and all dates.", "sum_values", null)]
    [InlineData("Average requested leave Days by Status across all clients and all dates. Round averages to two decimal places.", "average_values", 2)]
    [InlineData("Average requested leave Days by Status across all clients and all dates.", "average_values", null)]
    [InlineData("SUM REQUESTED LEAVE DAYS GROUPED BY STATUS ACROSS ALL DATES AND ALL CLIENTS.", "sum_values", null)]
    public void LeaveAggregateFocusPreservesQuestionMeasureGroupingAndExplicitRounding(string question, string measure, int? roundDigits)
    {
        var input = CountPrompt(question);
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "SELECT only. Tenant scope is mandatory.", PlanSchema);
        var actual = JsonNode.Parse(result.Prompt)!;
        var focus = actual["queryFocus"]!;
        Assert.Equal(question, actual["userQuestion"]!.GetValue<string>());
        Assert.Equal("simple-count-v1", focus["version"]!.GetValue<string>());
        Assert.Equal("essleaverequests", focus["recordTable"]!.GetValue<string>());
        Assert.Equal(measure, focus["measure"]!.GetValue<string>());
        Assert.Equal("essleaverequests", focus["measureColumn"]!["table"]!.GetValue<string>());
        Assert.Equal("Days", focus["measureColumn"]!["column"]!.GetValue<string>());
        Assert.Equal("essleaverequests", focus["grouping"]!["table"]!.GetValue<string>());
        Assert.Equal("Status", focus["grouping"]!["column"]!.GetValue<string>());
        Assert.Equal(roundDigits, focus["roundDigits"]?.GetValue<int>());
        Assert.Empty(focus["requestedFilters"]!.AsArray());
        Assert.Empty(focus["recordDefinitionFilters"]!.AsArray());
        Assert.True(focus["noOtherFiltersRequested"]!.GetValue<bool>());
        Assert.Equal(new[] { "clients", "essleaverequests" }, actual["relevantSchema"]!.AsObject().Select(pair => pair.Key).Order());
        AssertProjectedSchema(actual, "clients=Id;essleaverequests=Id,ClientId,Status,Days");
        Assert.Equal(input["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString(), actual["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString());
        Assert.StartsWith("SELECT only. Tenant scope is mandatory.", result.SystemInstruction);
        Assert.Contains("never replace grouped values with a single total", result.SystemInstruction);
        Assert.Contains("no other status, active, date or null filters", result.SystemInstruction);
    }

    [Fact]
    public void LeaveAggregateRetainsMandatoryBackendScope()
    {
        var input = CountPrompt("Sum requested leave Days by Status across all clients and all dates.", "RRU");
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Scope is mandatory.", PlanSchema).Prompt)!;
        var focus = result["queryFocus"]!;
        Assert.Equal("RRU", focus["clientScope"]!.GetValue<string>());
        Assert.Equal(new[] { "essleaverequests" }, focus["scopeTables"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal("ClientId", focus["requiredRelationships"]![0]!["leftColumn"]!.GetValue<string>());
        Assert.Equal("clients", focus["requiredRelationships"]![0]!["rightTable"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("essleaverequests", "Days:decimal")]
    [InlineData("essleaverequests", "Status:varchar")]
    [InlineData("essleaverequests", "ClientId:int")]
    [InlineData("clients", "Code:varchar")]
    public void LeaveAggregateWithoutMetricGroupingOrScopeColumnsKeepsFullSchema(string table, string column)
    {
        var input = CountPrompt("Sum requested leave Days by Status across all clients and all dates.");
        var columns = input["relevantSchema"]!.AsArray().Single(item => item!["table"]!.GetValue<string>() == table)!["columns"]!.AsArray();
        columns.Remove(columns.Single(value => value!.GetValue<string>() == column));
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep scope.", PlanSchema).Prompt)!;
        Assert.Null(result["queryFocus"]);
        Assert.Equal(input["relevantSchema"]!.AsArray().Count, result["relevantSchema"]!.AsObject().Count);
    }

    [Theory]
    [InlineData("Count candidate applications by job title, ApplicationType='Application' only. Include jobs having applications only.", true)]
    [InlineData("Count candidate applications by position title, ApplicationType='Application' only. Include positions having applications only.", true)]
    [InlineData("Count applications grouped by job title. Include jobs having applications only.", true)]
    [InlineData("Count candidate applications by job title", false)]
    [InlineData("Number of applications by position title", false)]
    public void ApplicationTitleFocusUsesExactRecordDefinitionAndPositionRelationship(string question, bool requireMatchingPosition)
    {
        var input = CountPrompt(question);
        var built = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep scope and privacy.", PlanSchema);
        var actual = JsonNode.Parse(built.Prompt)!;
        var focus = actual["queryFocus"]!;
        Assert.Equal(question, actual["userQuestion"]!.GetValue<string>());
        Assert.Equal("count_records", focus["measure"]!.GetValue<string>());
        Assert.Equal("recruitment_candidate_applications", focus["recordTable"]!.GetValue<string>());
        Assert.Equal("recruitment_open_positions", focus["grouping"]!["table"]!.GetValue<string>());
        Assert.Equal("PositionTitle", focus["grouping"]!["column"]!.GetValue<string>());
        Assert.Empty(focus["requestedFilters"]!.AsArray());
        var definition = Assert.Single(focus["recordDefinitionFilters"]!.AsArray())!;
        Assert.Equal("recruitment_candidate_applications", definition["table"]!.GetValue<string>());
        Assert.Equal("ApplicationType", definition["column"]!.GetValue<string>());
        Assert.Equal("Application", definition["value"]!.GetValue<string>());
        var relationships = focus["requiredRelationships"]!.AsArray();
        Assert.Equal(2, relationships.Count);
        var position = relationships.Single(relation => relation!["rightTable"]!.GetValue<string>() == "recruitment_open_positions")!;
        Assert.Equal("recruitment_candidate_applications", position["leftTable"]!.GetValue<string>());
        Assert.Equal("PositionId", position["leftColumn"]!.GetValue<string>());
        Assert.Equal("Id", position["rightColumn"]!.GetValue<string>());
        Assert.Equal(requireMatchingPosition ? "INNER JOIN" : null, position["requiredJoinType"]?.GetValue<string>());
        var client = relationships.Single(relation => relation!["rightTable"]!.GetValue<string>() == "clients")!;
        Assert.Null(client["requiredJoinType"]);
        Assert.Equal(new[] { "clients", "recruitment_candidate_applications", "recruitment_open_positions" }, actual["relevantSchema"]!.AsObject().Select(pair => pair.Key).Order());
        AssertProjectedSchema(actual, "clients=Id;recruitment_candidate_applications=Id,ClientId,PositionId,ApplicationType;recruitment_open_positions=Id,ClientId,PositionTitle");
        Assert.Equal(input["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString(), actual["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString());
        Assert.StartsWith("Keep scope and privacy.", built.SystemInstruction);
        if (requireMatchingPosition) Assert.Contains("Honor requiredJoinType only on its exact relationship", built.SystemInstruction);
    }

    [Fact]
    public void ApplicationTitleFocusPreservesMandatoryClientScope()
    {
        var input = CountPrompt("Count candidate applications by job title. Include jobs having applications only.", "RRU");
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep tenant scope.", PlanSchema).Prompt)!;
        Assert.Equal("RRU", actual["queryFocus"]!["clientScope"]!.GetValue<string>());
        Assert.Equal(new[] { "recruitment_candidate_applications" }, actual["queryFocus"]!["scopeTables"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Theory]
    [InlineData("recruitment_candidate_applications", "PositionId:int")]
    [InlineData("recruitment_candidate_applications", "ApplicationType:varchar")]
    [InlineData("recruitment_candidate_applications", "ClientId:int")]
    [InlineData("recruitment_open_positions", "PositionTitle:varchar")]
    [InlineData("recruitment_open_positions", "Id:int")]
    [InlineData("recruitment_open_positions", "ClientId:int")]
    public void ApplicationTitleWithoutRequiredColumnsKeepsFullSchema(string table, string column)
    {
        var input = CountPrompt("Count candidate applications by job title, ApplicationType='Application' only. Include jobs having applications only.");
        var columns = input["relevantSchema"]!.AsArray().Single(item => item!["table"]!.GetValue<string>() == table)!["columns"]!.AsArray();
        columns.Remove(columns.Single(value => value!.GetValue<string>() == column));
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep every required field.", PlanSchema).Prompt)!;
        Assert.Null(actual["queryFocus"]);
        Assert.Equal(input["relevantSchema"]!.AsArray().Count, actual["relevantSchema"]!.AsObject().Count);
    }

    [Fact]
    public void ScopedInterviewsKeepApplicationClientJoinPathAndScope()
    {
        var input = CountPrompt("Count interviews by Status", "Rashtriya Raksha University");
        var built = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Scope is mandatory.", PlanSchema);
        Assert.Contains("INNER JOIN recruitment_candidate_applications", built.SystemInstruction);
        var result = JsonNode.Parse(built.Prompt)!;
        Assert.Equal(new[] { "clients", "recruitment_candidate_applications", "recruitment_interviews" }, result["relevantSchema"]!.AsObject().Select(pair => pair.Key).Order());
        Assert.Equal("Rashtriya Raksha University", result["approvedMetricsAndTerminology"]!["scope"]!.GetValue<string>());
        Assert.Contains("recruitment_interviews.ApplicationId -> recruitment_candidate_applications.Id", result["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Contains("recruitment_candidate_applications.ClientId -> clients.Id", result["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Select(value => value!.GetValue<string>()));
        var joins = result["queryFocus"]!["requiredRelationships"]!.AsArray();
        Assert.Equal(2, joins.Count);
        Assert.Equal("recruitment_interviews", joins[0]!["leftTable"]!.GetValue<string>());
        Assert.Equal("ApplicationId", joins[0]!["leftColumn"]!.GetValue<string>());
        Assert.Equal("recruitment_candidate_applications", joins[0]!["rightTable"]!.GetValue<string>());
        Assert.Equal("Id", joins[0]!["rightColumn"]!.GetValue<string>());
        Assert.Equal(new[] { "recruitment_candidate_applications" }, result["queryFocus"]!["scopeTables"]!.AsArray().Select(value => value!.GetValue<string>()));
        AssertProjectedSchema(result, "clients=Id;recruitment_candidate_applications=Id,ClientId;recruitment_interviews=Id,ApplicationId,Status");
    }

    [Theory]
    [InlineData("Count interviews by status and job")]
    [InlineData("Count interviews and offers by Status")]
    [InlineData("Count interviews by status since January")]
    [InlineData("Count candidates by ATS band")]
    [InlineData("Compare active employees and salary by client")]
    [InlineData("Count active offers by status")]
    [InlineData("Count employees by campus")]
    [InlineData("Count RRU active employees by Gender")]
    [InlineData("Count offers by Status, all clients and all dates and Status='Accepted'")]
    [InlineData("Count offers by Status, all clients and all dates and only accepted offers")]
    [InlineData("Count offers by Status, all clients and all dates since January")]
    [InlineData("Count offers by Status, all clients and all dates and average OfferedCtc")]
    [InlineData("Count offers by Status, all clients and all dates and all departments")]
    [InlineData("Count offers by Status, all clients and all dates and all clients")]
    [InlineData("Count offers by Status, all clients and active clients")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates where Status is not null")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates for approved requests only")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates and count requests")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates since January")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates. Round averages to two decimal places.")]
    [InlineData("Average requested leave Days by Status across all clients and all dates. Round averages to three decimal places.")]
    [InlineData("Average requested leave Days by Status across all clients and all dates. Round averages to two decimal places and exclude cancelled requests.")]
    [InlineData("Sum requested leave Days by Status and EmployeeId across all clients and all dates.")]
    [InlineData("Sum requested leave Days across all clients and all dates.")]
    [InlineData("Sum requested leave Amount by Status across all clients and all dates.")]
    [InlineData("Count candidate applications by job title, SourceType='Application' only. Include jobs having applications only.")]
    [InlineData("Count candidate applications by job title, ApplicationType='TalentPool' only. Include jobs having applications only.")]
    [InlineData("Count candidate applications by job title, ApplicationType='Application' only. Include jobs having applications only and Status is not null.")]
    [InlineData("Count candidate applications by job title for active clients only")]
    [InlineData("Count candidate applications by job title and Status")]
    [InlineData("Count candidate applications by job title since January")]
    [InlineData("Count candidate applications by job title excluding cancelled applications")]
    [InlineData("Count candidate applications by job title. Include all jobs including those without applications.")]
    [InlineData("Count positions by job title")]
    public void UnknownOrCompoundRequestsKeepFullSchema(string question)
    {
        var input = CountPrompt(question);
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "No guesses.", PlanSchema).Prompt)!;
        Assert.Equal(input["relevantSchema"]!.AsArray().Count, result["relevantSchema"]!.AsObject().Count);
        Assert.Null(result["queryFocus"]);
        AssertFullSchema(input, result);
    }

    [Fact]
    public void MissingScopeJoinAndUnknownRelationshipDoNotNarrow()
    {
        var missing = CountPrompt("Count interviews by Status", "RRU");
        var application = missing["relevantSchema"]!.AsArray().Single(item => item!["table"]!.GetValue<string>() == "recruitment_candidate_applications");
        missing["relevantSchema"]!.AsArray().Remove(application);
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(missing.ToJsonString(), "Keep access.", PlanSchema).Prompt)!;
        Assert.Equal(missing["relevantSchema"]!.AsArray().Count, result["relevantSchema"]!.AsObject().Count);

        var unknown = CountPrompt("Count employees by Gender");
        unknown["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Add("Custom scope bridge must be included");
        var originalRelations = unknown["approvedMetricsAndTerminology"]!["relationships"]!.ToJsonString();
        result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(unknown.ToJsonString(), "Keep access.", PlanSchema).Prompt)!;
        Assert.Equal(unknown["relevantSchema"]!.AsArray().Count, result["relevantSchema"]!.AsObject().Count);
        Assert.Equal(originalRelations, result["approvedMetricsAndTerminology"]!["relationships"]!.ToJsonString());
    }

    [Theory]
    [InlineData("Count interviews by Status")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates.")]
    [InlineData("Average requested leave Days by Status across all clients and all dates. Round averages to two decimal places.")]
    [InlineData("Count candidate applications by job title, ApplicationType='Application' only. Include jobs having applications only.")]
    public void RecognizedRepairRebuildsTheSameHostFocusAndPreservesFailureEvidence(string question)
    {
        var input = CountPrompt(question);
        var initial = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Plan safely.", PlanSchema).Prompt)!;
        input["failedPlan"] = new JsonObject { ["sql"] = "SELECT invalid FROM recruitment_interviews", ["parameters"] = new JsonArray() };
        input["validationError"] = "Unknown field";
        // A failed model result cannot supply or broaden the authoritative contract.
        input["queryFocus"] = new JsonObject { ["recordTable"] = "employees" };
        input["failedPlan"]!["simpleCountContract"] = input["queryFocus"]!.DeepClone();
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Repair safely.", PlanSchema).Prompt)!;
        Assert.Equal(initial["relevantSchema"]!.ToJsonString(), result["relevantSchema"]!.ToJsonString());
        Assert.Equal(initial["queryFocus"]!.ToJsonString(), result["queryFocus"]!.ToJsonString());
        Assert.Equal(initial["approvedMetricsAndTerminology"]!.ToJsonString(), result["approvedMetricsAndTerminology"]!.ToJsonString());
        Assert.Equal("SELECT invalid FROM recruitment_interviews", result["failedPlan"]!["sql"]!.GetValue<string>());
        Assert.Equal("Unknown field", result["validationError"]!.GetValue<string>());
        Assert.Null(result["failedPlan"]!["simpleCountContract"]);
    }

    [Fact]
    public void UnknownRepairKeepsFullAuthorizedCatalogEvenWithModelSuppliedFocus()
    {
        var input = CountPrompt("Compare average leave Days and payroll by client for this month");
        input["failedPlan"] = new JsonObject { ["sql"] = "SELECT invalid FROM employees", ["parameters"] = new JsonArray("RRU") };
        input["validationError"] = "Unknown field";
        input["queryFocus"] = new JsonObject { ["recordTable"] = "essleaverequests" };
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Repair safely.", PlanSchema).Prompt)!;
        Assert.Null(result["queryFocus"]);
        Assert.Equal("RRU", result["failedPlan"]!["parameters"]![0]!.GetValue<string>());
        AssertFullSchema(input, result);
    }

    [Fact]
    public void FocusExplainsRecordTableFirstAndDoesNotRequestOptionalJoins()
    {
        var input = CountPrompt("Average requested leave Days by Status across all clients and all dates. Round averages to two decimal places.");
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "SELECT only. Respect tenant scope.", PlanSchema);
        Assert.StartsWith("SELECT only. Respect tenant scope.", result.SystemInstruction);
        Assert.Contains("FROM essleaverequests", result.SystemInstruction);
        Assert.Contains("not instructions to join every table", result.SystemInstruction);
        Assert.Contains("no subqueries", result.SystemInstruction);
        Assert.Contains("do not add any JOIN", result.SystemInstruction);
    }

    [Theory]
    [InlineData("Count employees by Gender", "All clients", true)]
    [InlineData("Count employees by Gender", "RRU", true)]
    [InlineData("Count active RRU employees by Gender", "RRU", false)]
    [InlineData("Count employees by client", "All clients", false)]
    [InlineData("Count interviews by Status", "All clients", true)]
    [InlineData("Count interviews by Status", "RRU", false)]
    [InlineData("Count candidate applications by job title", "All clients", false)]
    public void NoJoinHintNeverDropsRequiredGroupingFilterOrScopeParents(string question, string scope, bool singleTable)
    {
        var input = CountPrompt(question, scope);
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Tenant scope required.", PlanSchema);
        Assert.Equal(singleTable, result.SystemInstruction.Contains("do not add any JOIN", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptJsonKeepsReadablePunctuationAndEscapesControlsCorrectly()
    {
        var input = CountPrompt("Count employees by Gender");
        const string rule = "Use 'RRU' only when requested. A -> B; <not HTML>; quote \" and slash \\;\nnew line\tand tab";
        input["approvedMetricsAndTerminology"]!["rules"]!.AsArray().Add(rule);
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Safety unchanged.", PlanSchema);
        Assert.Contains("A -> B", result.Prompt);
        Assert.DoesNotContain("\\u0027", result.Prompt);
        Assert.DoesNotContain("\\u003E", result.Prompt);
        var parsed = JsonNode.Parse(result.Prompt)!;
        Assert.Contains(rule, parsed["approvedMetricsAndTerminology"]!["rules"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void InterviewFocusRequiresStatusGroupingWithoutInventedApplicationOrActiveFilters()
    {
        var input = CountPrompt("Count all interview records by Status. All dates, all clients.");
        var result = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "All mandatory rules.", PlanSchema);
        var focus = JsonNode.Parse(result.Prompt)!["queryFocus"]!;
        Assert.Equal("simple-count-v1", focus["version"]!.GetValue<string>());
        Assert.Equal("recruitment_interviews", focus["recordTable"]!.GetValue<string>());
        Assert.Equal("Status", focus["grouping"]!["column"]!.GetValue<string>());
        Assert.Empty(focus["requestedFilters"]!.AsArray());
        Assert.Empty(focus["recordDefinitionFilters"]!.AsArray());
        Assert.True(focus["noOtherFiltersRequested"]!.GetValue<bool>());
        Assert.Equal("All clients", focus["clientScope"]!.GetValue<string>());
        Assert.Empty(focus["scopeTables"]!.AsArray());
        Assert.Contains("Rules about other record types do not request filters or joins", result.SystemInstruction);
        Assert.Contains("never replace grouped counts with a single total", result.SystemInstruction);
    }

    [Fact]
    public void WorkforceFocusCopiesOnlyExplicitActiveAndClientCodeFilters()
    {
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(CountPrompt("Count active RRU employees by Gender.").ToJsonString(), "Scope.", PlanSchema).Prompt)!;
        var filters = result["queryFocus"]!["requestedFilters"]!.AsArray();
        Assert.Equal(2, filters.Count);
        Assert.Equal("employees", filters[0]!["table"]!.GetValue<string>());
        Assert.Equal("IsActive", filters[0]!["column"]!.GetValue<string>());
        Assert.Equal(1, filters[0]!["value"]!.GetValue<int>());
        Assert.Equal("clients", filters[1]!["table"]!.GetValue<string>());
        Assert.Equal("Code", filters[1]!["column"]!.GetValue<string>());
        Assert.Equal("RRU", filters[1]!["value"]!.GetValue<string>());
        Assert.Equal("Gender", result["queryFocus"]!["grouping"]!["column"]!.GetValue<string>());
        Assert.Empty(result["queryFocus"]!["scopeTables"]!.AsArray());
    }

    [Fact]
    public void ApplicationDefinitionRemainsSeparateFromExplicitUserFilters()
    {
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(CountPrompt("COUNT CANDIDATE APPLICATIONS BY STATUS").ToJsonString(), "Scope.", PlanSchema).Prompt)!;
        var focus = result["queryFocus"]!;
        Assert.Empty(focus["requestedFilters"]!.AsArray());
        Assert.Equal("CurrentStatus", focus["grouping"]!["column"]!.GetValue<string>());
        Assert.Equal("ApplicationType", focus["recordDefinitionFilters"]![0]!["column"]!.GetValue<string>());
        Assert.Equal("Application", focus["recordDefinitionFilters"]![0]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Count interviews by Status", "## Recruitment\nApplication records are not interviews.", "## Workforce\nActive headcount uses IsActive=1.")]
    [InlineData("Count employees by Gender", "## Workforce\nKeep unmapped gender groups.", "## Payroll\nUse stored NetPay totals.")]
    [InlineData("Count leave requests by Status", "## Attendance and leave\nCount leave requests at request grain.", "## Recruitment\nUse current ATS scores.")]
    [InlineData("Sum requested leave Days by Status across all clients and all dates.", "## Attendance and leave\nUse requested Days and Status.", "## Recruitment\nUse current ATS scores.")]
    public void FocusedKnowledgeDropsWholeUnrelatedOptionalDomainChunksOnly(string question, string relevant, string unrelated)
    {
        var input = CountPrompt(question);
        input["hrmsBusinessKnowledge"] = JsonSerializer.SerializeToNode(new[]
        {
            new { source = "docs/analytics-business-rules.md", content = relevant },
            new { source = "docs/analytics-business-rules.md", content = unrelated },
            new { source = "docs/analytics-business-rules.md", content = "## Scope and privacy\nSELECT-only, authorized client scope." },
            new { source = "other.md", content = "## Workforce\nUnknown source rule must remain intact." }
        });
        var result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Original security.", PlanSchema).Prompt)!;
        var contents = result["hrmsBusinessKnowledge"]!.AsArray().Select(item => item!["content"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, contents.Length);
        Assert.Contains(relevant, contents);
        Assert.DoesNotContain(unrelated, contents);
        Assert.Contains("## Scope and privacy\nSELECT-only, authorized client scope.", contents);
        Assert.Contains("## Workforce\nUnknown source rule must remain intact.", contents);
        Assert.Equal(input["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString(), result["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString());

        input["userQuestion"] = question + " and compare salaries";
        result = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Original security.", PlanSchema).Prompt)!;
        Assert.Null(result["queryFocus"]);
        Assert.Equal(4, result["hrmsBusinessKnowledge"]!.AsArray().Count);
    }

    [Fact]
    public void ProjectionPreservesBothEndsOfEveryRetainedRelationshipAndTenantKeys()
    {
        var input = CountPrompt("Count active RRU employees by Gender", "RRU");
        var employees = input["relevantSchema"]!.AsArray().Single(table => table!["table"]!.GetValue<string>() == "employees")!["columns"]!.AsArray();
        var clients = input["relevantSchema"]!.AsArray().Single(table => table!["table"]!.GetValue<string>() == "clients")!["columns"]!.AsArray();
        employees.Add("BillingClientCode:varchar");
        clients.Add("BillingCode:varchar");
        clients.Add("client_id:int");
        input["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Add("employees.BillingClientCode -> clients.BillingCode");
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Scope stays mandatory.", PlanSchema).Prompt)!;
        AssertProjectedSchema(actual, "clients=Id,Code,BillingCode,client_id;employees=Id,ClientId,Gender,IsActive,BillingClientCode");
        Assert.Equal(new[] { "employees" }, actual["queryFocus"]!["scopeTables"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Contains("employees.BillingClientCode -> clients.BillingCode", actual["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Theory]
    [InlineData("employees.MissingKey -> clients.Code")]
    [InlineData("employees.ClientId -> clients.Id with mandatory custom scope")]
    public void UnresolvedRetainedRelationshipSkipsColumnProjection(string relationship)
    {
        var input = CountPrompt("Count active RRU employees by Gender");
        input["approvedMetricsAndTerminology"]!["relationships"]!.AsArray().Add(relationship);
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep mandatory hints.", PlanSchema).Prompt)!;
        Assert.NotNull(actual["queryFocus"]);
        AssertProjectedSchema(actual, "clients=Id,Code,Name,IsActive;employees=Id,ClientId,Gender,IsActive,Department");
    }

    [Fact]
    public void ProjectionPreservesOriginalColumnCasing()
    {
        var input = CountPrompt("Count active RRU employees by Gender");
        foreach (var table in input["relevantSchema"]!.AsArray())
            table!["columns"] = new JsonArray(table["columns"]!.AsArray().Select(column => (JsonNode?)JsonValue.Create(column!.GetValue<string>().ToLowerInvariant())).ToArray());
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "Keep scope.", PlanSchema).Prompt)!;
        AssertProjectedSchema(actual, "clients=id,code;employees=id,clientid,gender,isactive");
    }

    [Fact]
    public void SuppliedQueryFocusCannotProjectAnUnsupportedQuestion()
    {
        var input = CountPrompt("Compare employee salary and leave days by client");
        input["queryFocus"] = JsonNode.Parse("{\"version\":\"simple-count-v1\",\"recordTable\":\"employees\",\"measure\":\"count_records\"}");
        var actual = JsonNode.Parse(LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), "No inferred metric.", PlanSchema).Prompt)!;
        Assert.Null(actual["queryFocus"]);
        AssertFullSchema(input, actual);
    }

    [Fact]
    public void RecognizedWideSchemaReducesBytesWithoutChangingContractOrRules()
    {
        var input = CountPrompt("Count active RRU employees by Gender");
        var employees = input["relevantSchema"]!.AsArray().Single(table => table!["table"]!.GetValue<string>() == "employees")!["columns"]!.AsArray();
        foreach (var name in new[] { "DesignationId", "DepartmentId", "WorkLocationId", "JoiningDate", "ConfirmationDate", "EmploymentType", "EmploymentStatus", "ReportingManagerId", "BusinessUnit", "CostCenter", "Grade", "Band", "ShiftId", "AttendancePolicyId", "LeavePolicyId", "PayrollGroupId", "SalaryTemplateId", "MonthlyGross", "AnnualCtc", "BasicSalary", "Hra", "SpecialAllowance", "ProvidentFundApplicable", "EsiApplicable", "ProfessionalTaxApplicable", "NoticePeriodDays", "ProbationDays", "RetirementDate", "ExitDate", "ExitReason", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "IsDeleted", "WorkerCategory", "ContractStartDate", "ContractEndDate", "ServiceStartDate", "LocationType", "PaymentMode", "BonusEligible", "OvertimeEligible", "PensionApplicable", "WelfareFundApplicable", "HolidayCalendarId", "WeeklyOffPattern", "AttendanceMode", "WorkScheduleId", "SkillCategory" })
            employees.Add(name + ":varchar");
        const string system = "SELECT-only. Preserve tenant scope and mandatory business rules.";
        var built = LocalLlmAnalyticsPrompt.Build(input.ToJsonString(), system, PlanSchema);
        var projected = JsonNode.Parse(built.Prompt)!;
        AssertProjectedSchema(projected, "clients=Id,Code;employees=Id,ClientId,Gender,IsActive");
        var fullSelected = projected.DeepClone();
        foreach (var table in projected["relevantSchema"]!.AsObject())
        {
            var original = input["relevantSchema"]!.AsArray().Single(item => item!["table"]!.GetValue<string>() == table.Key)!;
            fullSelected["relevantSchema"]![table.Key] = new JsonArray(original["columns"]!.AsArray().Select(column => (JsonNode?)JsonValue.Create(column!.GetValue<string>().Split(':')[0])).ToArray());
        }
        var beforeSchemaBytes = Encoding.UTF8.GetByteCount(fullSelected["relevantSchema"]!.ToJsonString());
        var afterSchemaBytes = Encoding.UTF8.GetByteCount(projected["relevantSchema"]!.ToJsonString());
        Assert.True(afterSchemaBytes * 5 < beforeSchemaBytes, $"Wide-schema bytes: {beforeSchemaBytes} -> {afterSchemaBytes}");
        Assert.Equal(fullSelected["queryFocus"]!.ToJsonString(), projected["queryFocus"]!.ToJsonString());
        Assert.Equal(input["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString(), projected["approvedMetricsAndTerminology"]!["rules"]!.ToJsonString());
        Assert.StartsWith(system, built.SystemInstruction);
    }

    private static void AssertProjectedSchema(JsonNode actual, string expectedSchema)
    {
        var expected = expectedSchema.Split(';').Select(table => table.Split('=')).ToDictionary(table => table[0], table => table[1].Split(','));
        Assert.Equal(expected.Keys.Order(), actual["relevantSchema"]!.AsObject().Select(table => table.Key).Order());
        foreach (var table in expected)
            Assert.Equal(table.Value, actual["relevantSchema"]![table.Key]!.AsArray().Select(column => column!.GetValue<string>()));
    }

    private static void AssertFullSchema(JsonObject input, JsonNode actual)
    {
        Assert.Equal(input["relevantSchema"]!.AsArray().Count, actual["relevantSchema"]!.AsObject().Count);
        foreach (var table in input["relevantSchema"]!.AsArray())
            Assert.Equal(table!["columns"]!.AsArray().Select(column => column!.GetValue<string>().Split(':')[0]),
                actual["relevantSchema"]![table["table"]!.GetValue<string>()]!.AsArray().Select(column => column!.GetValue<string>()));
    }

    private static JsonObject CountPrompt(string question, string scope = "All clients")
    {
        var tables = new Dictionary<string, string[]>
        {
            ["recruitment_open_positions"] = ["Id:int", "ClientId:int", "Status:varchar", "PositionTitle:varchar"],
            ["recruitment_candidate_applications"] = ["Id:int", "ClientId:int", "PositionId:int", "CurrentStatus:varchar", "ApplicationType:varchar"],
            ["recruitment_offers"] = ["Id:int", "ClientId:int", "ApplicationId:int", "Status:varchar"],
            ["employees"] = ["Id:int", "ClientId:int", "Gender:varchar", "IsActive:tinyint", "Department:varchar"],
            ["clients"] = ["Id:int", "Code:varchar", "Name:varchar", "IsActive:tinyint"],
            ["recruitment_interviews"] = ["Id:int", "ApplicationId:int", "Status:varchar", "ScheduledStart:datetime"],
            ["essleaverequests"] = ["Id:int", "ClientId:int", "EmployeeId:int", "Status:varchar", "Days:decimal"]
        };
        return JsonSerializer.SerializeToNode(new
        {
            userQuestion = question, mysqlDatabase = "payroll",
            relevantSchema = tables.Select(table => new { table = table.Key, columns = table.Value }),
            approvedMetricsAndTerminology = new
            {
                scope,
                relationships = new[] { "employees.ClientId -> clients.Id", "recruitment_candidate_applications.ClientId -> clients.Id", "essleaverequests.EmployeeId -> employees.Id" },
                rules = new[] { "Tenant scope is mandatory.", "Active employees means employees.IsActive=1, not clients.IsActive=1.", "Count applications separately from global talent pool: ApplicationType='Application'." }
            }
        })!.AsObject();
    }
}
