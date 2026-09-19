using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

// Only the explicitly selected local provider uses this compact wire contract.
// SQL authorization, validation, tenant scope and chart construction remain downstream.
internal static class LocalLlmAnalyticsPrompt
{
    internal const int MaximumContentBytes = 12_000;
    // This JSON is plain model-input text, never HTML. Keep ordinary punctuation
    // readable; JSON quotes, backslashes and control characters remain escaped.
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static (string Prompt, string SystemInstruction) Build(string prompt, string systemInstruction, string schema)
    {
        using var contract = JsonDocument.Parse(schema);
        if (!contract.RootElement.TryGetProperty("properties", out var properties))
            throw new InvalidOperationException("The local analytics response contract is missing.");
        var planning = properties.TryGetProperty("sql", out _);
        var insights = properties.TryGetProperty("summary", out _);
        if (!planning && !insights)
            throw new InvalidOperationException("This analytics response contract is not supported by the local model yet.");
        var input = JsonNode.Parse(prompt) as JsonObject
            ?? throw new InvalidOperationException("The local analytics request is invalid.");

        // Preserve every existing safety/business instruction. Presentation metadata is
        // optional in the existing planner and chart fields are inferred from live rows.
        var instruction = systemInstruction + (planning
            ? "\nLocal compact response: return only {\"sql\":\"SELECT ...\",\"parameters\":[]}. "
              + "Omit chart/title/explanation metadata; the application derives those. "
              + "Use simple unqualified alphanumeric table aliases such as a and p, never dotted aliases. Do not change the requested filters, grouping or joins. "
              + "relevantSchema maps exact table names to their permitted column names. Never invent missing columns."
            : "\nLocal compact response: return only {\"summary\":\"one short factual sentence\",\"recommendations\":[],\"risks\":[]}. "
              + "Do not invent advice. If rowsTruncated is true, describe only the shown rows, never a whole-result total.");

        JsonObject compact;
        string knowledgeKey;
        if (planning)
        {
            compact = Copy(input, "userQuestion", "mysqlDatabase", "approvedMetricsAndTerminology", "validationError");
            var tables = new JsonObject();
            if (input["relevantSchema"] is not JsonArray originalTables)
                throw new InvalidOperationException("The local analytics request has no authorized schema.");
            foreach (var item in originalTables.OfType<JsonObject>())
            {
                var name = item["table"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || item["columns"] is not JsonArray columns)
                    throw new InvalidOperationException("The local analytics schema is invalid.");
                // Start with every exposed column. Only a complete host-derived
                // metric contract can narrow fields after preserving its dependencies.
                tables[name] = new JsonArray(columns.Select(column =>
                {
                    var field = column?.GetValue<string>() ?? "";
                    var separator = field.IndexOf(':');
                    return (JsonNode?)JsonValue.Create(separator < 0 ? field : field[..separator]);
                }).ToArray());
            }
            NarrowSimpleMetricSchema(input, compact, tables);
            if (compact["queryFocus"] is JsonObject focus)
            {
                ProjectFocusedColumns(tables, focus, compact["approvedMetricsAndTerminology"] as JsonObject);
                instruction += $"\nStart FROM {focus["recordTable"]!.GetValue<string>()}, using one plain grouped SELECT with no subqueries. "
                    + "allowedTables and requiredRelationships are permitted dependencies, not instructions to join every table. "
                    + "Join only when needed for the requested grouping, filters or backend client scope; never begin FROM a parent/dimension table.";
                var recordTable = focus["recordTable"]!.GetValue<string>();
                bool IsRecord(string? table) => recordTable.Equals(table, StringComparison.OrdinalIgnoreCase);
                // A retained client-parent schema is not a request to join it. All
                // fields and the backend's direct ClientId predicate can come from
                // the record itself. Do not apply this hint to parent-scoped interviews,
                // client-code filters or grouping on a related table.
                if (IsRecord(focus["grouping"]?["table"]?.GetValue<string>())
                    && (focus["measureColumn"] is null || IsRecord(focus["measureColumn"]?["table"]?.GetValue<string>()))
                    && focus["requestedFilters"]!.AsArray().Concat(focus["recordDefinitionFilters"]!.AsArray())
                        .All(filter => IsRecord(filter?["table"]?.GetValue<string>()))
                    && focus["scopeTables"]!.AsArray().All(table => IsRecord(table?.GetValue<string>())))
                    instruction += $"\nFor THIS query, use {recordTable} alone: do not add any JOIN. All requested grouping/measure/filter columns belong to that table. Any direct ClientId scope is added by the backend, not by joining clients.";
                instruction += focus["measure"]!.GetValue<string>() == "count_records"
                    ? "\nqueryFocus is grounded in the explicit question and supplied schema. Count its recordTable, preserve its grouping, and use only its requestedFilters/recordDefinitionFilters plus mandatory backend client scope. Rules about other record types do not request filters or joins. Empty filter lists mean no other status, active, application-type or date filters. Return the grouping value with its count; never replace grouped counts with a single total."
                    : "\nqueryFocus is grounded in the explicit question and supplied schema. Use SUM for sum_values or AVG for average_values on its exact measureColumn and preserve its grouping. Use only requestedFilters/recordDefinitionFilters plus mandatory backend client scope. Empty filter lists mean no other status, active, date or null filters. Return the grouping value with its requested measure; never replace grouped values with a single total. Apply ROUND only when roundDigits is specified.";
                if (focus["requiredRelationships"]!.AsArray().Any(relation => relation?["requiredJoinType"] is not null))
                    instruction += " Honor requiredJoinType only on its exact relationship; do not infer extra joined-table filters.";
                if (focus["recordTable"]!.GetValue<string>().Equals("recruitment_interviews", StringComparison.OrdinalIgnoreCase)
                    && focus["scopeTables"]!.AsArray().Any(table => table?.GetValue<string>().Equals("recruitment_candidate_applications", StringComparison.OrdinalIgnoreCase) == true))
                    instruction += " For scoped interviews, INNER JOIN recruitment_candidate_applications on interviews.ApplicationId=applications.Id so the backend client filter excludes other clients' interviews; LEFT JOIN would retain them.";
            }
            compact["relevantSchema"] = tables;
            if (input["failedPlan"] is JsonObject failedPlan)
                compact["failedPlan"] = Copy(failedPlan, "sql", "parameters");
            knowledgeKey = "hrmsBusinessKnowledge";
        }
        else
        {
            compact = Copy(input, "question", "rowCount", "metricFields");
            compact["resultRows"] = new JsonArray();
            compact["rowsTruncated"] = input["rowsTruncated"]?.GetValue<bool>() ?? false;
            compact["shownRowCount"] = 0;
            knowledgeKey = "supportingBusinessKnowledge";
        }

        var excerpts = new JsonArray();
        compact[knowledgeKey] = excerpts;
        EnsureFits(compact, instruction);
        if (insights && input["resultRows"] is JsonArray rows)
        {
            var selected = (JsonArray)compact["resultRows"]!;
            foreach (var row in rows)
            {
                selected.Add(row?.DeepClone());
                compact["shownRowCount"] = selected.Count;
                if (Fits(compact, instruction)) continue;
                selected.RemoveAt(selected.Count - 1);
                compact["shownRowCount"] = selected.Count;
                compact["rowsTruncated"] = true;
                break;
            }
            if (rows.Count > 0 && selected.Count == 0)
                throw new InvalidOperationException("A result row exceeds the local model input budget. Narrow the dashboard question.");
            compact["shownRowCount"] = selected.Count;
            // An explicit original count is authoritative even when a caller omitted
            // its truncation flag; missing rows are never represented as complete data.
            if (input["rowCount"] is JsonValue count && count.TryGetValue<int>(out var total) && total > selected.Count)
                compact["rowsTruncated"] = true;
        }

        // Planning keeps optional relevant RAG excerpts as complete chunks. The local
        // insight contract only summarizes already-authorized returned rows with no
        // advice: optional RAG adds unrelated facts/prefill work, so omit it entirely.
        // Original mandatory instructions, question, whole rows/counts and truncation
        // flags remain authoritative for that summary.
        if (planning && input[knowledgeKey] is JsonArray knowledge)
        {
            foreach (var item in knowledge.OfType<JsonObject>())
            {
                if (!RelevantCountKnowledge(item, compact["queryFocus"] as JsonObject)) continue;
                var excerpt = Copy(item, "source", "content");
                excerpts.Add(excerpt);
                if (!Fits(compact, instruction)) excerpts.RemoveAt(excerpts.Count - 1);
            }
        }
        EnsureFits(compact, instruction);
        return (compact.ToJsonString(PromptJson), instruction);
    }

    // A conservative lexical fast path, not a canned answer or SQL generator. Only
    // one simple count or explicit leave-day aggregate and one grouping are narrowed.
    // Unknown filters, multi-metric questions and unavailable joins keep the
    // original catalog, even if that subsequently exceeds the local model's budget.
    // Repairs derive the same focus again from the original question and authorized
    // schema, never from failed SQL or model-supplied metadata. Expanding a recognized
    // repair back to every table adds irrelevant context and contradicts its contract.
    private static void NarrowSimpleMetricSchema(JsonObject input, JsonObject compact, JsonObject tables)
    {
        if (input["userQuestion"] is not JsonValue questionValue || !questionValue.TryGetValue<string>(out var question)) return;
        var subjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["employee"] = "employees", ["employees"] = "employees", ["staff"] = "employees", ["workforce"] = "employees",
            ["interview"] = "recruitment_interviews", ["interviews"] = "recruitment_interviews",
            ["offer"] = "recruitment_offers", ["offers"] = "recruitment_offers",
            ["candidate application"] = "recruitment_candidate_applications", ["candidate applications"] = "recruitment_candidate_applications",
            ["application"] = "recruitment_candidate_applications", ["applications"] = "recruitment_candidate_applications",
            ["leave request"] = "essleaverequests", ["leave requests"] = "essleaverequests",
            ["leave application"] = "essleaverequests", ["leave applications"] = "essleaverequests"
        };
        var aliases = string.Join('|', subjects.Keys.OrderByDescending(value => value.Length).Select(Regex.Escape));
        var normalized = Regex.Replace(question.Replace('.', ' ').Replace(',', ' '), @"\s+", " ").Trim();
        var positionMatch = Regex.Match(normalized,
            @"\A(?:count|total(?: number of)?|number of)\s+(?:all\s+)?(?<subject>(?:candidate\s+)?applications)(?:\s+records)?"
            + @"\s+(?:by|grouped by)\s+(?<dimension>(?:job|position)\s+title)"
            + @"(?:\s+ApplicationType\s*=\s*'Application'\s+only)?"
            + @"(?<onlyJobs>\s+Include\s+(?:jobs|positions)\s+having\s+applications\s+only)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var groupByPosition = positionMatch.Success;
        var match = groupByPosition ? positionMatch : Regex.Match(normalized,
            @"\A(?:count|total(?: number of)?|number of)\s+(?:all\s+)?(?:(?<active>active|inactive)\s+)?"
            + @"(?:(?<client>(?-i:[A-Z][A-Z0-9_-]{1,15}))\s+)??(?<subject>" + aliases + @")(?:\s+records)?"
            + @"\s+(?:by|grouped by)\s+(?<dimension>[a-z][a-z ]{0,48}?)(?:\s+all\s+(?:dates|clients)(?:\s+(?:and\s+)?all\s+(?:dates|clients))?)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var aggregate = !match.Success;
        if (aggregate)
            match = Regex.Match(normalized,
                @"\A(?<aggregate>sum|average)\s+requested\s+leave\s+Days\s+(?:by|grouped by)\s+(?<dimension>Status)\s+across\s+"
                + @"(?:all\s+clients\s+and\s+all\s+dates|all\s+dates\s+and\s+all\s+clients)"
                + @"(?<round>\s+Round\s+averages\s+to\s+two\s+decimal\s+places)?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return;
        var measure = aggregate
            ? match.Groups["aggregate"].Value.Equals("sum", StringComparison.OrdinalIgnoreCase) ? "sum_values" : "average_values"
            : "count_records";
        if (match.Groups["round"].Success && measure != "average_values") return;
        var subject = aggregate ? "essleaverequests" : subjects[match.Groups["subject"].Value];
        var canonicalNames = tables.Select(table => table.Key).ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
        if (!canonicalNames.TryGetValue(subject, out var subjectName)) return;
        var availableColumns = tables.ToDictionary(table => table.Key,
            table => (table.Value as JsonArray)?.Select(value => value?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        bool Has(string table, params string[] columns) => availableColumns.TryGetValue(table, out var found) && columns.All(found.Contains);
        if (!Has(subject, "Id") || (match.Groups["active"].Success && !Has(subject, "IsActive"))) return;
        if (aggregate && !Has(subject, "Days", "Status", "ClientId")) return;
        if (groupByPosition && (!Has(subject, "PositionId", "ApplicationType", "ClientId")
            || !Has("recruitment_open_positions", "Id", "PositionTitle", "ClientId"))) return;
        var dimension = match.Groups["dimension"].Value.Replace(" ", "");
        var groupByClient = dimension.Equals("client", StringComparison.OrdinalIgnoreCase) || dimension.Equals("clients", StringComparison.OrdinalIgnoreCase);
        var groupingColumn = groupByPosition
            ? availableColumns["recruitment_open_positions"].Single(column => column.Equals("PositionTitle", StringComparison.OrdinalIgnoreCase))
            : availableColumns[subject].FirstOrDefault(column => column.Replace("_", "").Equals(dimension, StringComparison.OrdinalIgnoreCase));
        if (subject == "recruitment_candidate_applications" && dimension.Equals("status", StringComparison.OrdinalIgnoreCase))
            groupingColumn = availableColumns[subject].FirstOrDefault(column => column.Equals("CurrentStatus", StringComparison.OrdinalIgnoreCase));
        if (groupingColumn is null && !groupByClient) return;
        if (subject == "recruitment_candidate_applications" && !Has(subject, "ApplicationType")) return;

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { subjectName };
        var requiredRelationships = new List<string>();
        var context = compact["approvedMetricsAndTerminology"] as JsonObject;
        var scope = context?["scope"]?.GetValue<string>();
        var authorizedClientScope = !string.IsNullOrWhiteSpace(scope) && !scope.Equals("All clients", StringComparison.OrdinalIgnoreCase);
        var explicitClient = match.Groups["client"].Success || groupByClient
            || authorizedClientScope;
        // Directly client-owned subjects retain their client dimension even when a
        // question happens to be global. Interviews need an application join for scope.
        if (Has(subject, "ClientId"))
        {
            if (!Has("clients", "Id", "Code", "Name")) return;
            selected.Add(canonicalNames["clients"]);
            requiredRelationships.Add($"{subjectName}.ClientId -> {canonicalNames["clients"]}.Id");
        }
        else if (explicitClient)
        {
            if (subject != "recruitment_interviews" || !Has(subject, "ApplicationId")
                || !Has("recruitment_candidate_applications", "Id", "ClientId") || !Has("clients", "Id", "Code", "Name")) return;
            selected.Add(canonicalNames["recruitment_candidate_applications"]); selected.Add(canonicalNames["clients"]);
            requiredRelationships.Add($"{subjectName}.ApplicationId -> {canonicalNames["recruitment_candidate_applications"]}.Id");
            requiredRelationships.Add($"{canonicalNames["recruitment_candidate_applications"]}.ClientId -> {canonicalNames["clients"]}.Id");
        }
        if (groupByPosition)
        {
            selected.Add(canonicalNames["recruitment_open_positions"]);
            requiredRelationships.Add($"{subjectName}.PositionId -> {canonicalNames["recruitment_open_positions"]}.Id");
        }
        if (context?["relationships"] is JsonArray relationships)
        {
            var kept = new List<string>();
            foreach (var relation in relationships)
            {
                if (relation is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
                var names = Regex.Matches(text, @"(?:^|->\s*)([A-Za-z0-9_]+)\.").Select(item => item.Groups[1].Value).ToArray();
                // Unknown hint syntax may contain an unrecognized mandatory dependency.
                if (names.Length != 2) return;
                if (names.All(selected.Contains)) kept.Add(text);
            }
            context["relationships"] = new JsonArray(kept.Concat(requiredRelationships).Distinct(StringComparer.OrdinalIgnoreCase).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        else if (context is not null && context["relationships"] is null)
            context["relationships"] = new JsonArray(requiredRelationships.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        else if (context is not null) return;
        foreach (var name in tables.Select(table => table.Key).Where(name => !selected.Contains(name)).ToArray()) tables.Remove(name);
        string Column(string table, string column) => availableColumns[table].Single(value => value.Equals(column, StringComparison.OrdinalIgnoreCase));
        JsonObject Filter(string table, string column, JsonNode value) => new()
        {
            ["table"] = canonicalNames[table], ["column"] = Column(table, column), ["operator"] = "=", ["value"] = value
        };
        var requestedFilters = new JsonArray();
        if (match.Groups["active"].Success)
            requestedFilters.Add(Filter(subject, "IsActive", JsonValue.Create(match.Groups["active"].Value.Equals("active", StringComparison.OrdinalIgnoreCase) ? 1 : 0)!));
        if (match.Groups["client"].Success)
            requestedFilters.Add(Filter("clients", "Code", JsonValue.Create(match.Groups["client"].Value)!));
        var definitionFilters = new JsonArray();
        if (subject == "recruitment_candidate_applications")
            definitionFilters.Add(Filter(subject, "ApplicationType", JsonValue.Create("Application")!));
        var focus = new JsonObject
        {
            ["version"] = "simple-count-v1", ["recordTable"] = subjectName, ["measure"] = measure,
            ["grouping"] = new JsonObject { ["table"] = groupByClient ? canonicalNames["clients"] : groupByPosition ? canonicalNames["recruitment_open_positions"] : subjectName, ["column"] = groupByClient ? Column("clients", "Code") : groupingColumn },
            ["requestedFilters"] = requestedFilters, ["recordDefinitionFilters"] = definitionFilters,
            ["clientScope"] = scope ?? "All clients", ["timeScope"] = "All dates; no date filter requested", ["noOtherFiltersRequested"] = true,
            ["scopeTables"] = new JsonArray(authorizedClientScope
                ? new JsonNode?[] { JsonValue.Create(Has(subject, "ClientId") ? subjectName : canonicalNames["recruitment_candidate_applications"]) }
                : Array.Empty<JsonNode?>()),
            ["allowedTables"] = new JsonArray(selected.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["requiredRelationships"] = new JsonArray(requiredRelationships.Select(value =>
            {
                var relation = Regex.Match(value, @"\A([A-Za-z0-9_]+)\.([A-Za-z0-9_]+) -> ([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\z");
                var relationship = new JsonObject { ["leftTable"] = relation.Groups[1].Value, ["leftColumn"] = relation.Groups[2].Value, ["rightTable"] = relation.Groups[3].Value, ["rightColumn"] = relation.Groups[4].Value };
                if (groupByPosition && match.Groups["onlyJobs"].Success
                    && relation.Groups[3].Value == canonicalNames["recruitment_open_positions"])
                    relationship["requiredJoinType"] = "INNER JOIN";
                return (JsonNode?)relationship;
            }).ToArray())
        };
        if (aggregate)
            focus["measureColumn"] = new JsonObject { ["table"] = subjectName, ["column"] = Column(subject, "Days") };
        if (match.Groups["round"].Success) focus["roundDigits"] = 2;
        compact["queryFocus"] = focus;
    }

    // This runs only after the local recognizer builds queryFocus, never from a
    // model-supplied contract. Collect every dependency before removing any field;
    // an unfamiliar hint or missing dependency keeps the selected tables intact.
    private static void ProjectFocusedColumns(JsonObject tables, JsonObject focus, JsonObject? context)
    {
        var measure = focus["measure"]?.GetValue<string>();
        if (focus["version"]?.GetValue<string>() != "simple-count-v1"
            || measure is not ("count_records" or "sum_values" or "average_values")) return;
        var available = tables.ToDictionary(table => table.Key,
            table => table.Value!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var required = available.ToDictionary(table => table.Key,
            table => table.Value.Where(column => column.Equals("Id", StringComparison.OrdinalIgnoreCase)
                || column.Equals("ClientId", StringComparison.OrdinalIgnoreCase)
                || column.Equals("client_id", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        bool Require(string? table, string? column)
        {
            if (table is null || column is null || !available.TryGetValue(table, out var columns) || !columns.Contains(column)) return false;
            required[table].Add(column);
            return true;
        }
        bool RequireReference(JsonNode? reference) => reference is JsonObject field
            && Require(field["table"]?.GetValue<string>(), field["column"]?.GetValue<string>());
        if (!Require(focus["recordTable"]?.GetValue<string>(), "Id") || !RequireReference(focus["grouping"])) return;
        if (measure != "count_records" && !RequireReference(focus["measureColumn"])) return;
        foreach (var filter in focus["requestedFilters"]!.AsArray().Concat(focus["recordDefinitionFilters"]!.AsArray()))
            if (!RequireReference(filter)) return;
        foreach (var relation in focus["requiredRelationships"]!.AsArray())
            if (relation is not JsonObject link
                || !Require(link["leftTable"]?.GetValue<string>(), link["leftColumn"]?.GetValue<string>())
                || !Require(link["rightTable"]?.GetValue<string>(), link["rightColumn"]?.GetValue<string>())) return;
        foreach (var scopeTable in focus["scopeTables"]!.AsArray())
        {
            var table = scopeTable!.GetValue<string>();
            if (!available.TryGetValue(table, out var columns)) return;
            var key = table.Equals("clients", StringComparison.OrdinalIgnoreCase) ? "Id"
                : columns.FirstOrDefault(column => column.Equals("ClientId", StringComparison.OrdinalIgnoreCase)
                    || column.Equals("client_id", StringComparison.OrdinalIgnoreCase));
            if (!Require(table, key)) return;
        }
        // Retained relationship hints remain mandatory even if the exact metric
        // contract does not require their join. Never leave a hint's columns out.
        if (context?["relationships"] is JsonArray hints)
            foreach (var hint in hints)
            {
                if (hint is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
                var relation = Regex.Match(text, @"\A\s*([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s*->\s*([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s*\z");
                if (!relation.Success || !Require(relation.Groups[1].Value, relation.Groups[2].Value)
                    || !Require(relation.Groups[3].Value, relation.Groups[4].Value)) return;
            }
        foreach (var table in tables.ToArray())
            tables[table.Key] = new JsonArray(table.Value!.AsArray()
                .Where(value => required[table.Key].Contains(value!.GetValue<string>()))
                .Select(value => value!.DeepClone()).ToArray());
    }

    private static bool RelevantCountKnowledge(JsonObject item, JsonObject? focus)
    {
        if (focus is null || item["source"]?.GetValue<string>() != "docs/analytics-business-rules.md") return true;
        var heading = Regex.Match(item["content"]?.GetValue<string>() ?? "", @"\A\s*#{1,4}\s+([^\r\n]+)").Groups[1].Value.Trim();
        var table = focus["recordTable"]!.GetValue<string>();
        // Scope/privacy, generic interpretation and unknown sections are kept. Only
        // whole, known, unrelated optional domain sections are excluded.
        var relevant = table == "employees" ? "Workforce" : table == "essleaverequests" ? "Attendance and leave" : "Recruitment";
        return !new[] { "Workforce", "Payroll", "Attendance and leave", "Recruitment", "Workflow" }.Contains(heading, StringComparer.OrdinalIgnoreCase)
            || heading.Equals(relevant, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Copy(JsonObject source, params string[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
            if (source.TryGetPropertyValue(field, out var value)) result[field] = value?.DeepClone();
        return result;
    }

    private static bool Fits(JsonObject prompt, string instruction) =>
        Encoding.UTF8.GetByteCount(prompt.ToJsonString(PromptJson)) + Encoding.UTF8.GetByteCount(instruction)
            + Encoding.UTF8.GetByteCount(LocalLlmProtocol.SystemInstruction) + 1 <= MaximumContentBytes;

    private static void EnsureFits(JsonObject prompt, string instruction)
    {
        if (!Fits(prompt, instruction))
            throw new InvalidOperationException("The authorized schema and mandatory rules exceed the local model input budget. Narrow the dashboard question or increase the tested local context limit; no schema or access rules were removed.");
    }
}
