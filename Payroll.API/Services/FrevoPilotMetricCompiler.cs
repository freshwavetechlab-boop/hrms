using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Services;

// Only call with queryFocus created by LocalLlmAnalyticsPrompt, never a model or
// browser payload. Output still requires SQL/metric validation and fresh scope.
internal static class FrevoPilotMetricCompiler
{
    private static readonly HashSet<string> Records = new(StringComparer.OrdinalIgnoreCase)
    {
        "employees", "recruitment_interviews", "recruitment_offers", "recruitment_candidate_applications", "essleaverequests"
    };
    private sealed record Field(string Table, string Column);
    private sealed record Filter(Field Field, string Value);
    private sealed record Relationship(Field From, Field To, bool Inner);

    internal static bool TryCompile(JsonElement contract, out JsonElement payload)
    {
        payload = default;
        try
        {
            if (contract.ValueKind != JsonValueKind.Object || contract.GetRawText().Length > 16_384) return false;
            Only(contract, "version", "recordTable", "measure", "grouping", "requestedFilters", "recordDefinitionFilters",
                "requiredRelationships", "allowedTables", "scopeTables", "clientScope", "timeScope", "noOtherFiltersRequested", "measureColumn", "roundDigits");
            Require(Text(contract, "version") == "simple-count-v1" && contract.GetProperty("noOtherFiltersRequested").ValueKind == JsonValueKind.True);
            var record = Identifier(contract, "recordTable");
            Require(Records.Contains(record));
            var measure = Text(contract, "measure");
            Require(measure is "count_records" or "sum_values" or "average_values");
            var group = ReadField(contract.GetProperty("grouping"));
            var aggregate = measure != "count_records";
            Field? metric = null;
            var round = false;
            if (aggregate)
            {
                metric = ReadField(contract.GetProperty("measureColumn"));
                Require(Same(record, "essleaverequests") && Same(metric.Table, record) && Same(metric.Column, "Days")
                    && Same(group.Table, record) && Same(group.Column, "Status"));
                if (contract.TryGetProperty("roundDigits", out var digits))
                {
                    Require(measure == "average_values" && digits.TryGetInt32(out var value) && value == 2);
                    round = true;
                }
            }
            else Require(!contract.TryGetProperty("measureColumn", out _) && !contract.TryGetProperty("roundDigits", out _));
            if (contract.TryGetProperty("clientScope", out var clientScope))
                Require(clientScope.ValueKind == JsonValueKind.String && clientScope.GetString() is { Length: > 0 and <= 500 });
            if (contract.TryGetProperty("timeScope", out var timeScope))
                Require(timeScope.ValueKind == JsonValueKind.String && timeScope.GetString() == "All dates; no date filter requested");

            var allowed = Names(contract.GetProperty("allowedTables"), 3);
            Require(allowed.Count > 0 && allowed.Contains(record) && allowed.Contains(group.Table)
                && allowed.All(table => Records.Contains(table) || Same(table, "clients") || Same(table, "recruitment_open_positions")));
            Require(Same(group.Table, record) || (Same(group.Table, "clients") && Same(group.Column, "Code"))
                || (Same(record, "recruitment_candidate_applications") && Same(group.Table, "recruitment_open_positions") && Same(group.Column, "PositionTitle")));

            var requested = ReadFilters(contract.GetProperty("requestedFilters"), record, definition: false);
            var definitions = ReadFilters(contract.GetProperty("recordDefinitionFilters"), record, definition: true);
            Require(requested.Count <= 2 && definitions.Count <= 1 && (!aggregate || requested.Count + definitions.Count == 0));
            Require(!Same(record, "recruitment_candidate_applications") || definitions.Count == 1);
            var filters = requested.Concat(definitions).ToList();
            Require(filters.All(filter => allowed.Contains(filter.Field.Table))
                && filters.Select(filter => filter.Field.Table.ToLowerInvariant() + "." + filter.Field.Column.ToLowerInvariant()).Distinct().Count() == filters.Count);
            var scopes = contract.TryGetProperty("scopeTables", out var scopeTables) ? Names(scopeTables, 2) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Require(scopes.All(table => allowed.Contains(table) && !Same(table, "recruitment_interviews")));

            var relationsNode = contract.GetProperty("requiredRelationships");
            Require(relationsNode.ValueKind == JsonValueKind.Array && relationsNode.GetArrayLength() <= 2);
            var relations = new List<Relationship>();
            foreach (var item in relationsNode.EnumerateArray())
            {
                Only(item, "leftTable", "leftColumn", "rightTable", "rightColumn", "requiredJoinType");
                var from = new Field(Identifier(item, "leftTable"), Identifier(item, "leftColumn"));
                var to = new Field(Identifier(item, "rightTable"), Identifier(item, "rightColumn"));
                Require(allowed.Contains(from.Table) && allowed.Contains(to.Table) && Same(to.Column, "Id"));
                var clientLink = Same(to.Table, "clients") && Same(from.Column, "ClientId")
                    && (Same(from.Table, record) && !Same(record, "recruitment_interviews")
                        || Same(record, "recruitment_interviews") && Same(from.Table, "recruitment_candidate_applications"));
                var interviewLink = Same(record, "recruitment_interviews") && Same(from.Table, record)
                    && Same(from.Column, "ApplicationId") && Same(to.Table, "recruitment_candidate_applications");
                var positionLink = Same(record, "recruitment_candidate_applications") && Same(from.Table, record)
                    && Same(from.Column, "PositionId") && Same(to.Table, "recruitment_open_positions")
                    && Same(group.Table, to.Table) && Same(group.Column, "PositionTitle");
                Require(clientLink || interviewLink || positionLink);
                var inner = item.TryGetProperty("requiredJoinType", out var joinType);
                if (inner) Require(positionLink && !aggregate && joinType.ValueKind == JsonValueKind.String && joinType.GetString() == "INNER JOIN");
                // Interviews have no ClientId. Their scoped application parent
                // must match; LEFT would retain other-client interviews as nulls.
                if (interviewLink && scopes.Contains("recruitment_candidate_applications")) inner = true;
                Require(!relations.Any(relation => Same(relation.To.Table, to.Table)));
                relations.Add(new(from, to, inner));
            }
            // Every named table must come from this record's known parent paths.
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record };
            for (var pass = 0; pass < 2; pass++)
                foreach (var relation in relations) if (reachable.Contains(relation.From.Table)) reachable.Add(relation.To.Table);
            Require(allowed.SetEquals(reachable));

            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record, group.Table };
            needed.UnionWith(filters.Select(filter => filter.Field.Table));
            needed.UnionWith(scopes);
            for (var pass = 0; pass < 2; pass++)
                foreach (var relation in relations) if (needed.Contains(relation.To.Table)) needed.Add(relation.From.Table);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [record] = "r" };
            var joins = new StringBuilder();
            while (aliases.Count < needed.Count)
            {
                var next = relations.OrderBy(relation => relation.To.Table, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(relation => needed.Contains(relation.To.Table) && !aliases.ContainsKey(relation.To.Table) && aliases.ContainsKey(relation.From.Table));
                Require(next is not null);
                var alias = "t" + aliases.Count.ToString(CultureInfo.InvariantCulture);
                aliases[next!.To.Table] = alias;
                joins.Append(next.Inner ? " INNER JOIN " : " LEFT JOIN ").Append(Quote(next.To.Table)).Append(" AS ").Append(Quote(alias))
                    .Append(" ON ").Append(Reference(next.From, aliases)).Append(" = ").Append(Reference(next.To, aliases));
            }
            var groupSql = Reference(group, aliases);
            var metricSql = aggregate ? (measure == "sum_values" ? "SUM(" : "AVG(") + Reference(metric!, aliases) + ")" : "COUNT(*)";
            if (round) metricSql = "ROUND(" + metricSql + ", 2)";
            var sql = new StringBuilder("SELECT ").Append(groupSql).Append(" AS `label`, ").Append(metricSql)
                .Append(" AS `value` FROM ").Append(Quote(record)).Append(" AS `r`").Append(joins);
            if (filters.Count > 0) sql.Append(" WHERE ").AppendJoin(" AND ", filters.Select(filter => Reference(filter.Field, aliases) + " = ?"));
            sql.Append(" GROUP BY ").Append(groupSql);
            payload = JsonSerializer.SerializeToElement(new { sql = sql.ToString(), parameters = filters.Select(filter => filter.Value).ToArray() });
            return true;
        }
        catch { return false; }
    }

    private static List<Filter> ReadFilters(JsonElement input, string record, bool definition)
    {
        Require(input.ValueKind == JsonValueKind.Array && input.GetArrayLength() <= 3);
        var filters = new List<Filter>();
        foreach (var item in input.EnumerateArray())
        {
            Only(item, "table", "column", "operator", "value");
            var field = new Field(Identifier(item, "table"), Identifier(item, "column"));
            Require(Text(item, "operator") == "=");
            var value = item.GetProperty("value");
            string text;
            if (definition)
            {
                Require(Same(record, "recruitment_candidate_applications") && Same(field.Table, record) && Same(field.Column, "ApplicationType")
                    && value.ValueKind == JsonValueKind.String && value.GetString() == "Application");
                text = "Application";
            }
            else if (Same(field.Table, record) && Same(field.Column, "IsActive"))
            {
                Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var active) && active is 0 or 1);
                text = value.GetInt32().ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Require(Same(field.Table, "clients") && Same(field.Column, "Code") && value.ValueKind == JsonValueKind.String);
                text = value.GetString()!;
                Require(text.Length is >= 2 and <= 16 && text[0] is >= 'A' and <= 'Z'
                    && text.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'));
            }
            filters.Add(new(field, text));
        }
        return filters;
    }

    private static Field ReadField(JsonElement value)
    {
        Only(value, "table", "column");
        return new(Identifier(value, "table"), Identifier(value, "column"));
    }
    private static HashSet<string> Names(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.String);
            var name = item.GetString()!;
            Require(IsName(name) && names.Add(name));
        }
        return names;
    }
    private static string Identifier(JsonElement value, string property)
    {
        var name = Text(value, property);
        Require(IsName(name));
        return name;
    }
    private static string Text(JsonElement value, string property)
    {
        var field = value.GetProperty(property);
        Require(field.ValueKind == JsonValueKind.String);
        return field.GetString()!;
    }
    private static bool IsName(string value) => value.Length is > 0 and <= 64 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static void Only(JsonElement value, params string[] properties)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) Require(properties.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
    }
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static string Quote(string identifier) => "`" + identifier + "`";
    private static string Reference(Field field, Dictionary<string, string> aliases) => Quote(aliases[field.Table]) + "." + Quote(field.Column);
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unsupported host metric contract.");
    }
}
