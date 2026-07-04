using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public class WorkflowActionMiddleware(RequestDelegate next, ILogger<WorkflowActionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, WorkflowRepository workflows)
    {
        var user = context.Items.TryGetValue("User", out var item) && item is AuthUser authUser ? authUser : null;
        (WorkflowActionRule Rule, Dictionary<string, string> RouteValues)? match = null;
        if (user is not null && IsMutation(context.Request.Method))
        {
            try
            {
                match = await FindRuleAsync(workflows, context.Request.Method, context.Request.Path.Value ?? "");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Workflow action rule lookup failed for {Method} {Path}.", context.Request.Method, context.Request.Path.Value);
            }
        }

        JsonElement? requestBody = null;
        if (match is not null && UsesBody(match.Value.Rule))
            requestBody = await ReadJsonBodyAsync(context.Request);

        JsonElement? responseBody = null;
        var captureResponse = match is not null && UsesResponse(match.Value.Rule);
        var originalResponseBody = context.Response.Body;
        using var responseBuffer = captureResponse ? new MemoryStream() : null;

        try
        {
            if (captureResponse && responseBuffer is not null)
                context.Response.Body = responseBuffer;

            await next(context);

            if (captureResponse && responseBuffer is not null)
            {
                responseBuffer.Position = 0;
                responseBody = await ReadJsonElementAsync(responseBuffer);
                responseBuffer.Position = 0;
                await responseBuffer.CopyToAsync(originalResponseBody);
            }
        }
        finally
        {
            if (captureResponse)
                context.Response.Body = originalResponseBody;
        }

        if (match is null || user is null || context.Response.StatusCode < 200 || context.Response.StatusCode >= 300)
            return;

        try
        {
            var (rule, routeValues) = match.Value;
            var resourceIdSource = string.IsNullOrWhiteSpace(rule.ResourceIdSource) ? $"route.{rule.ResourceIdRouteKey}" : rule.ResourceIdSource;
            var resourceId = ResolveSource(resourceIdSource, routeValues, context.Request.Query, requestBody, responseBody);
            if (string.IsNullOrWhiteSpace(resourceId))
                return;

            var clientIdFromSource = ResolveSource(rule.ClientIdSource, routeValues, context.Request.Query, requestBody, responseBody);
            int? clientId = int.TryParse(clientIdFromSource, out var parsedClientId) ? parsedClientId : null;
            clientId ??= await workflows.ResolveClientIdFromLookupAsync(rule.ClientLookupTable, rule.ClientLookupKeyColumn, rule.ClientLookupClientColumn, resourceId);
            clientId ??= await workflows.ResolveClientIdAsync(rule.ClientIdSql, routeValues);
            clientId ??= user.ClientId;
            var workflowId = rule.WorkflowId ?? await workflows.GetDefaultIdForActivityAsync(rule.ActivityCode, clientId);
            if (workflowId is null)
                return;

            var existingState = await workflows.GetResourceStateAsync(rule.ResourceType, resourceId);
            if (existingState?.CurrentState == "Pending")
                return;

            var payload = JsonSerializer.Serialize(new
            {
                rule.ActivityCode,
                rule.ResourceType,
                ResourceId = resourceId,
                RouteValues = routeValues,
                ClientId = clientId,
                RequestedBy = user.DisplayName,
                RequestedByEmail = user.Email,
                RequestPath = context.Request.Path.Value,
                RequestedAt = DateTime.UtcNow
            });

            await workflows.StartAsync(new StartWorkflowRequest
            {
                WorkflowId = workflowId.Value,
                ResourceType = rule.ResourceType,
                ResourceId = resourceId,
                PayloadJson = payload
            }, user.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Workflow action middleware failed for {Method} {Path}.", context.Request.Method, context.Request.Path.Value);
        }
    }

    static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    static bool UsesBody(WorkflowActionRule rule) => UsesSource(rule.ResourceIdSource, "body.") || UsesSource(rule.ClientIdSource, "body.");
    static bool UsesResponse(WorkflowActionRule rule) => UsesSource(rule.ResourceIdSource, "response.") || UsesSource(rule.ClientIdSource, "response.");
    static bool UsesSource(string? source, string prefix) => !string.IsNullOrWhiteSpace(source) && source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    static async Task<JsonElement?> ReadJsonBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is 0 || string.IsNullOrWhiteSpace(request.ContentType) || !request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return null;

        request.EnableBuffering();
        request.Body.Position = 0;
        var element = await ReadJsonElementAsync(request.Body);
        request.Body.Position = 0;
        return element;
    }

    static async Task<JsonElement?> ReadJsonElementAsync(Stream stream)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? ResolveSource(string? source, Dictionary<string, string> routeValues, IQueryCollection query, JsonElement? body, JsonElement? response)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var normalized = source.Trim();
        if (!normalized.Contains('.'))
            return routeValues.GetValueOrDefault(normalized);

        var dot = normalized.IndexOf('.');
        var scope = normalized[..dot];
        var path = normalized[(dot + 1)..];

        if (scope.Equals("route", StringComparison.OrdinalIgnoreCase))
            return routeValues.GetValueOrDefault(path);
        if (scope.Equals("query", StringComparison.OrdinalIgnoreCase))
            return query.TryGetValue(path, out var value) ? value.ToString() : null;
        if (scope.Equals("body", StringComparison.OrdinalIgnoreCase))
            return ResolveJsonPath(body, path);
        if (scope.Equals("response", StringComparison.OrdinalIgnoreCase))
            return ResolveJsonPath(response, path);

        return null;
    }

    static string? ResolveJsonPath(JsonElement? element, string path)
    {
        if (element is null || string.IsNullOrWhiteSpace(path))
            return null;

        var current = element.Value;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => current.GetRawText()
        };
    }

    static async Task<(WorkflowActionRule Rule, Dictionary<string, string> RouteValues)?> FindRuleAsync(WorkflowRepository workflows, string method, string path)
    {
        var rules = await workflows.GetActionRulesAsync();
        foreach (var rule in rules.Where(rule => method.Equals(rule.HttpMethod, StringComparison.OrdinalIgnoreCase)))
        {
            var values = Match(rule.PathPattern, path);
            if (values is not null)
                return (rule, values);
        }
        return null;
    }

    static Dictionary<string, string>? Match(string pattern, string path)
    {
        var patternParts = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (patternParts.Length != pathParts.Length)
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < patternParts.Length; i++)
        {
            var patternPart = patternParts[i];
            var pathPart = pathParts[i];
            if (patternPart.StartsWith('{') && patternPart.EndsWith('}'))
            {
                var key = patternPart.Trim('{', '}').Split(':')[0];
                values[key] = Uri.UnescapeDataString(pathPart);
                continue;
            }
            if (!patternPart.Equals(pathPart, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return values;
    }
}
