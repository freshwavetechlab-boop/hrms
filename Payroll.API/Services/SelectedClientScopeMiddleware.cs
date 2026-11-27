using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Selected accounts use one authorized client per business request. All legacy
// single-client/all-client requests retain their original execution path.
public sealed class SelectedClientScopeMiddleware(RequestDelegate next)
{
    public const string Header = "X-Client-Id";
    public const string IdentityKey = "SelectedClientIdentity";
    private const int BodyLimit = 16 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, ISelectedClientOwnershipLookup ownership)
    {
        if (context.Items["User"] is not AuthUser identity) { await next(context); return; }
        if (!identity.ClientAccessMode.Equals("Legacy",StringComparison.OrdinalIgnoreCase) && !SelectedClientAccess.IsSelected(identity))
        { await Deny(context,"The configured client access mode is unavailable."); return; }
        // The workbook workspace is deliberately an administrator-only exception.
        // This gate leaves its pages, mapping, calculations and exports unchanged.
        if (SelectedClientAccess.IsExcelWorkspace(context.Request.Path) && !SelectedClientAccess.IsUnrestrictedAdministrator(identity))
        { await Deny(context, "Excel Payslips is available only to unrestricted administrators."); return; }
        if (!SelectedClientAccess.IsSelected(identity)) { await next(context); return; }
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) { await next(context); return; }
        if (path.Equals("/api/clients", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(context.Request.Method))
        { await next(context); return; } // This endpoint explicitly returns assigned clients.
        // The bell requests the user's jobs together. The feed enforces both the requester
        // id and assigned-client list; a request selecting a particular client still goes through normal scoping.
        if (HttpMethods.IsGet(context.Request.Method) && path.Equals("/api/employees/import-jobs", StringComparison.OrdinalIgnoreCase)
            && !context.Request.Headers.ContainsKey(Header) && !context.Request.Query.Keys.Any(IsClientKey))
        { await next(context); return; }
        // This read-only picker summary checks assigned clients and exact work
        // locations itself. Preserve the authenticated identity so selecting a
        // client does not hide the other clients' permitted availability.
        if (path.Equals("/api/recruitment/document-roster/client-summary", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsGet(context.Request.Method))
        { await next(context); return; }
        if (IsRecruitmentConfigurationCommand(context.Request.Method, path))
        { await InvokeRecruitmentConfigurationCommand(context, identity, ownership, path); return; }
        if (HttpMethods.IsGet(context.Request.Method) && !context.Request.Headers.ContainsKey(Header)
            && !context.Request.Query.Keys.Any(IsClientKey) && IsRecruitmentAggregateRead(path))
        {
            if (!await ValidateAssignedReadReferences(context, identity, ownership, path)) return;
            context.Items[IdentityKey] = identity;
            await next(context); return;
        }
        if (!SelectedClientOwnershipCatalog.IsAllowedScopedRequest(context.Request.Method, path))
        { await Deny(context, "This operation requires unrestricted administration or a supported client-scoped route."); return; }

        var requested = new HashSet<int>();
        var references = new HashSet<ScopeOwnershipReference>();
        JsonNode? json = null;
        Dictionary<string,StringValues>? formValues = null;
        IFormCollection? form = null;
        try
        {
            if (context.Request.Headers.TryGetValue(Header,out var active)) ReadClients(active.ToString(),requested);
            foreach (var pair in context.Request.Query)
            {
                if (IsClientKey(pair.Key)) ReadClients(pair.Value.ToString(),requested);
                else AddReferences(path,pair.Key,pair.Value.ToString(),references);
            }
            foreach(var pair in new[]{("resourceType","resourceId"),("entityType","entityId"),("ownerType","ownerId")})
                if(context.Request.Query.TryGetValue(pair.Item1,out var type)&&context.Request.Query.TryGetValue(pair.Item2,out var id))
                {
                    if(!SelectedClientOwnershipCatalog.TryEntityReference(type.ToString(),id.ToString(),out var target))throw new InvalidOperationException("Unknown resource ownership.");
                    references.Add(target);
                }
            foreach (var pair in context.Request.RouteValues)
                if (IsClientKey(pair.Key)) ReadClients(Convert.ToString(pair.Value) ?? "",requested);
            foreach (var reference in SelectedClientOwnershipCatalog.RouteReferences(path)) references.Add(reference);
            if (context.Request.HasJsonContentType() && context.Request.ContentLength != 0)
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body,Encoding.UTF8,false,4096,true);
                var raw = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position=0;
                if (Encoding.UTF8.GetByteCount(raw)>BodyLimit) throw new InvalidOperationException("Use a smaller scoped request.");
                if (!string.IsNullOrWhiteSpace(raw)) { json=JsonNode.Parse(raw); Walk(json,requested,references,path,true); }
            }
            else if (context.Request.HasFormContentType)
            {
                form=await context.Request.ReadFormAsync(context.RequestAborted);
                formValues=form.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var pair in form)
                {
                    if(IsClientKey(pair.Key)) ReadClients(pair.Value.ToString(),requested);
                    else if(pair.Value.ToString().TrimStart().StartsWith('{') || pair.Value.ToString().TrimStart().StartsWith('['))
                        Walk(JsonNode.Parse(pair.Value.ToString()),requested,references,path,true);
                    else AddReferences(path,pair.Key,pair.Value.ToString(),references);
                }
            }
            if(requested.Count!=1 || !SelectedClientAccess.Allows(identity,requested.Single()))
            { await Deny(context,"Select one of your assigned clients. The header, URL and submitted records must use that same client."); return; }
            var clientId=requested.Single();
            if(ownership is SelectedClientOwnershipStore store)
            {
                store.ScopeClientId=clientId;
                await store.PrimeAsync(references,context.RequestAborted);
            }
            foreach(var reference in references)
                if(await ownership.ClientIdAsync(reference,context.RequestAborted) is var owner && owner!=clientId
                    && !(owner==0 && (reference.AllowGlobal || HttpMethods.IsGet(context.Request.Method) && IsSharedDefinition(reference.Resource))))
                { await Deny(context,"A referenced record is not owned by the selected client."); return; }

            context.Items[IdentityKey]=identity;
            context.Items["User"]=SelectedClientAccess.ForRequest(identity,clientId);
            // Repository paths accepting optional query context never fall through
            // to an unrestricted aggregate when a caller omits clientId.
            var query=context.Request.Query.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            foreach(var key in query.Keys.Where(IsClientKey).ToArray())
                if(string.IsNullOrWhiteSpace(query[key].ToString())) query[key]=clientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            query["clientId"]=clientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Query=new QueryCollection(query);
            // Optional/null context fields are still scoped, never a request to
            // create a global row. Explicit zero/foreign IDs were denied above.
            ApplyEmptyClientContext(json,clientId,true);
            if(json is not null)
            {
                var bytes=Encoding.UTF8.GetBytes(json.ToJsonString());
                context.Request.Body=new MemoryStream(bytes);context.Request.ContentLength=bytes.Length;
            }
            if(formValues is not null && form is not null)
            {
                foreach(var key in formValues.Keys.Where(IsClientKey).ToArray())
                    if(string.IsNullOrWhiteSpace(formValues[key].ToString())) formValues[key]=clientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach(var key in formValues.Keys.ToArray())
                {
                    var raw=formValues[key].ToString().TrimStart();
                    if(raw.StartsWith('{') || raw.StartsWith('['))
                    {
                        var embedded=JsonNode.Parse(raw); ApplyEmptyClientContext(embedded,clientId,true);
                        formValues[key]=embedded?.ToJsonString() ?? raw;
                    }
                }
                formValues["clientId"]=clientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Request.Form=new FormCollection(formValues,form.Files);
            }
            if (context.Request.Headers.ContainsKey("Range") || IsDownload(path)) { await next(context); return; }
            // Lists from personal inboxes/history may contain older clients even
            // when their SQL is restricted by user ID. Remove those records too.
            var original=context.Response.Body;
            await using var buffer=new MemoryStream();context.Response.Body=buffer;
            try
            {
                await next(context);
                buffer.Position=0;
                if(context.Response.StatusCode is >=200 and <300 && context.Response.ContentType?.Contains("json",StringComparison.OrdinalIgnoreCase)==true && buffer.Length>0)
                {
                    var output=await JsonNode.ParseAsync(buffer,cancellationToken:context.RequestAborted);
                    if(ownership is SelectedClientOwnershipStore responseStore)
                    {
                        var responseReferences=new HashSet<ScopeOwnershipReference>();
                        ReadResponseReferences(output,responseReferences);
                        await responseStore.PrimeAsync(responseReferences,context.RequestAborted);
                    }
                    if(!await Filter(output,clientId,ownership,context.RequestAborted))
                    { context.Response.Body=original; await Deny(context,"The requested result is outside the selected client."); return; }
                    context.Response.ContentLength=null;context.Response.Body=original;
                    await context.Response.WriteAsync(output?.ToJsonString() ?? "null",context.RequestAborted);
                }
                else { context.Response.Body=original;buffer.Position=0;await buffer.CopyToAsync(original,context.RequestAborted); }
            }
            finally { context.Response.Body=original; }
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException)
        { await Deny(context,"The request could not be validated for the selected client."); }
    }

    // These repositories apply assigned-client, work-location and stage filters
    // themselves. This is deliberately not a prefix-wide All-clients bypass.
    internal static bool IsRecruitmentAggregateRead(string path)
    {
        path = path.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();
        return path is "/api/recruitment/dashboard" or "/api/recruitment/requisitions" or "/api/recruitment/open-positions"
            or "/api/recruitment/applications" or "/api/recruitment/interviews" or "/api/recruitment/offers"
            or "/api/recruitment/hiring-cases" or "/api/recruitment/pipeline-workspace" or "/api/recruitment/pipeline-targets"
            or "/api/recruitment/client-configuration-summary" or "/api/recruitment-orchestration/job-postings"
            or "/api/recruitment-orchestration/pipelines" or "/api/recruitment-admin/templates" or "/api/recruitment-orchestration/forms"
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/recruitment-orchestration/(?:pipelines/[1-9]\d*(?:/versions)?|pipeline-versions/[1-9]\d*|forms/[1-9]\d*)$");
    }

    private static bool IsRecruitmentConfigurationCommand(string method, string path) => HttpMethods.IsPost(method)
        && (path.Equals("/api/recruitment-orchestration/pipeline-configurations", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/recruitment-orchestration/(?:pipelines|forms)/[1-9]\d*/create-similar/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/recruitment-admin/templates/[1-9]\d*/create-similar/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static async Task<bool> ValidateAssignedReadReferences(HttpContext context, AuthUser identity, ISelectedClientOwnershipLookup ownership, string path)
    {
        if (identity.Id <= 0 || !identity.IsActive || identity.MustChangePassword) { await Deny(context, "Active client access is required."); return false; }
        var references = new HashSet<ScopeOwnershipReference>(SelectedClientOwnershipCatalog.RouteReferences(path));
        foreach (var pair in context.Request.Query) AddReferences(path, pair.Key, pair.Value.ToString(), references);
        if (ownership is SelectedClientOwnershipStore store) await store.PrimeAsync(references, context.RequestAborted);
        foreach (var reference in references)
        {
            var owner = await ownership.ClientIdAsync(reference, context.RequestAborted);
            if (owner is > 0 && SelectedClientAccess.Allows(identity, owner.Value) || owner == 0 && (reference.AllowGlobal || IsSharedDefinition(reference.Resource))) continue;
            await Deny(context, "A requested recruitment record is outside your assigned clients."); return false;
        }
        return true;
    }

    private async Task InvokeRecruitmentConfigurationCommand(HttpContext context, AuthUser identity, ISelectedClientOwnershipLookup ownership, string path)
    {
        // An explicit definition/client in these configuration commands is authoritative;
        // the shell's remembered active-client header may refer to another allowed
        // client while an administrator edits a record from the All-clients list.
        if (identity.Id <= 0 || !identity.IsActive || identity.MustChangePassword || !context.Request.HasJsonContentType())
        { await Deny(context, "Select an assigned client for this configuration."); return; }
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 4096, true);
            var raw = await reader.ReadToEndAsync(context.RequestAborted); context.Request.Body.Position = 0;
            if (Encoding.UTF8.GetByteCount(raw) > BodyLimit || JsonNode.Parse(raw) is not JsonObject body) throw new InvalidOperationException();
            JsonNode? Get(JsonObject value, string name) => value.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            int Client(JsonNode? value) => int.TryParse(value?.ToString(), out var id) && SelectedClientAccess.Allows(identity, id) ? id : throw new InvalidOperationException();
            var references = new HashSet<ScopeOwnershipReference>();
            if (path.TrimEnd('/').Equals("/api/recruitment-orchestration/pipeline-configurations", StringComparison.OrdinalIgnoreCase))
            {
                if (Get(body, "definition") is not JsonObject definition || Get(body, "configuration") is not JsonObject configuration) throw new InvalidOperationException();
                var clientId = Client(Get(definition, "clientId"));
                var requested = new HashSet<int>(); Walk(body, requested, references, path, true);
                foreach (var pair in context.Request.Query.Where(p => IsClientKey(p.Key))) ReadClients(pair.Value.ToString(), requested);
                if (requested.Count != 1 || !requested.Contains(clientId)) throw new InvalidOperationException();
                AddIdentity(Get(definition, "id"), "pipeline"); AddIdentity(Get(configuration, "id"), "pipeline-version");
                if (Get(configuration, "stages") is JsonArray stages) foreach (var stage in stages.OfType<JsonObject>()) AddIdentity(Get(stage, "id"), "pipeline-stage");
                if (Get(configuration, "transitions") is JsonArray transitions) foreach (var transition in transitions.OfType<JsonObject>()) AddIdentity(Get(transition, "id"), "pipeline-transition");
                if (ownership is SelectedClientOwnershipStore store) { store.ScopeClientId = clientId; await store.PrimeAsync(references, context.RequestAborted); }
                foreach (var reference in references)
                {
                    var owner = await ownership.ClientIdAsync(reference, context.RequestAborted);
                    if (owner != clientId && !(owner == 0 && reference.AllowGlobal)) throw new InvalidOperationException();
                }
            }
            else
            {
                _ = Client(Get(body, "targetClientId"));
                var isForm = path.StartsWith("/api/recruitment-orchestration/forms/", StringComparison.OrdinalIgnoreCase);
                var isTemplate = path.StartsWith("/api/recruitment-admin/templates/", StringComparison.OrdinalIgnoreCase);
                var sourceResource = isTemplate ? "recruitment-template" : isForm ? "form" : "pipeline";
                var source = SelectedClientOwnershipCatalog.RouteReferences(path).Single(r => r.Resource == sourceResource);
                ScopeOwnershipReference? version = null;
                if (!isTemplate && Get(body, "sourceVersionId") is { } requestedVersion)
                {
                    if (!long.TryParse(requestedVersion.ToString(), out var versionId) || versionId <= 0) throw new InvalidOperationException();
                    version = new ScopeOwnershipReference(isForm ? "form-version" : "pipeline-version", versionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (!isTemplate && !isForm) throw new InvalidOperationException();
                if (ownership is SelectedClientOwnershipStore store) await store.PrimeAsync(version is not null ? [source, version] : [source], context.RequestAborted);
                var sourceClient = await ownership.ClientIdAsync(source, context.RequestAborted);
                if (!(sourceClient is > 0 && SelectedClientAccess.Allows(identity, sourceClient.Value) || sourceClient == 0 && (isForm || isTemplate))
                    || version is not null && await ownership.ClientIdAsync(version, context.RequestAborted) != sourceClient) throw new InvalidOperationException();
            }
            context.Items[IdentityKey] = identity;
            await next(context);
            void AddIdentity(JsonNode? value, string resource)
            {
                if (long.TryParse(value?.ToString(), out var id) && id > 0) references.Add(new(resource, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { await Deny(context, "The configuration or copied source is outside the assigned client scope."); }
    }

    private static bool IsClientKey(string key) => key.Replace("_","").Equals("clientId",StringComparison.OrdinalIgnoreCase)
        || key.Replace("_","").Equals("clientIds",StringComparison.OrdinalIgnoreCase)
        || key.Replace("_","").Equals("companyId",StringComparison.OrdinalIgnoreCase);
    private static void ReadClients(string text,HashSet<int> clients)
    {
        foreach(var item in text.Split([',',';','|'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            if(!int.TryParse(item,out var id)||id<=0) throw new InvalidOperationException("Invalid client context.");
            clients.Add(id);
        }
    }
    private static void AddReferences(string path,string key,string value,HashSet<ScopeOwnershipReference> references)
    {
        foreach(var part in value.Split([',',';','|'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
            if(SelectedClientOwnershipCatalog.TryReference(path,key,part,out var reference)) references.Add(reference);
    }
    private static void ApplyEmptyClientContext(JsonNode? node,int clientId,bool root=false)
    {
        if(node is JsonArray array) { foreach(var child in array) ApplyEmptyClientContext(child,clientId,root); return; }
        if(node is not JsonObject obj)return;
        foreach(var pair in obj.ToArray())
            if(IsClientKey(pair.Key) && (pair.Value is null || pair.Value is JsonValue && string.IsNullOrWhiteSpace(pair.Value.ToString()) || pair.Value is JsonArray { Count: 0 }))
                obj[pair.Key]=pair.Key.Replace("_","").Equals("clientIds",StringComparison.OrdinalIgnoreCase) ? new JsonArray(clientId) : JsonValue.Create(clientId);
            else ApplyEmptyClientContext(pair.Value,clientId);
        if(root && !obj.Any(p=>IsClientKey(p.Key)))obj["clientId"]=clientId;
    }
    private static void Walk(JsonNode? node,HashSet<int> clients,HashSet<ScopeOwnershipReference> references,string path,bool root)
    {
        if(node is JsonArray array) { foreach(var child in array) Walk(child,clients,references,path,root); return; }
        if(node is not JsonObject obj)return;
        string Value(string name)=>obj.FirstOrDefault(p=>p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "";
        foreach(var pair in new[]{("resourceType","resourceId"),("entityType","entityId"),("ownerType","ownerId")})
            if(Value(pair.Item1) is var type && type.Length>0 && Value(pair.Item2) is var value && value.Length>0)
            {
                if(!SelectedClientOwnershipCatalog.TryEntityReference(type,value,out var entity))throw new InvalidOperationException("Unknown resource ownership.");
                references.Add(entity);
            }
        foreach(var pair in obj)
        {
            if(IsClientKey(pair.Key) && pair.Value is not null)
            {
                if(pair.Value is JsonArray ids) foreach(var id in ids) ReadClients(id?.ToString() ?? "",clients);
                else ReadClients(pair.Value.ToString(),clients);
            }
            else if(pair.Value is JsonValue)
            {
                AddReferences(path,pair.Key,pair.Value.ToString(),references);
                if(root && pair.Key.Equals("id",StringComparison.OrdinalIgnoreCase) && pair.Value.ToString() is var id && id!="0" && id.Length>0)
                {
                    if(!SelectedClientOwnershipCatalog.TryRootReference(path,id,out var reference))throw new InvalidOperationException("Unknown update ownership.");
                    references.Add(reference);
                }
            }
            else if(pair.Value is JsonArray values && values.All(v=>v is JsonValue))
                foreach(var value in values)AddReferences(path,pair.Key,value?.ToString() ?? "",references);
            else Walk(pair.Value,clients,references,path,false);
        }
        if(references.Count>5000)throw new InvalidOperationException("Too many record references.");
    }
    private static void ReadResponseReferences(JsonNode? node,HashSet<ScopeOwnershipReference> references)
    {
        if(node is JsonArray array){foreach(var child in array)ReadResponseReferences(child,references);return;}
        if(node is not JsonObject obj)return;
        foreach(var pair in obj)
        {
            if(pair.Value is JsonValue && (pair.Key.Equals("employeeId",StringComparison.OrdinalIgnoreCase)
                ||pair.Key.Equals("instanceId",StringComparison.OrdinalIgnoreCase)||pair.Key.Equals("taskId",StringComparison.OrdinalIgnoreCase))
                &&SelectedClientOwnershipCatalog.TryReference(pair.Key,pair.Value.ToString(),out var reference))references.Add(reference);
            else ReadResponseReferences(pair.Value,references);
        }
    }
    private static async Task<bool> Filter(JsonNode? node,int clientId,ISelectedClientOwnershipLookup ownership,CancellationToken cancellation)
    {
        if(node is JsonArray array)
        {
            for(var i=array.Count-1;i>=0;i--) if(!await Filter(array[i],clientId,ownership,cancellation))array.RemoveAt(i);
            return true;
        }
        if(node is not JsonObject obj)return true;
        foreach(var p in obj)
        {
            if(IsClientKey(p.Key) && p.Value is not null && p.Value.ToString() is var raw && raw.Length>0 && raw!="0")
            {
                var ids=new HashSet<int>();
                foreach(var part in raw.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))ReadClients(part.Split(':',2)[0],ids);
                if(!ids.Contains(clientId))return false;
            }
            if(p.Value is JsonValue && (p.Key.Equals("employeeId",StringComparison.OrdinalIgnoreCase)
                ||p.Key.Equals("instanceId",StringComparison.OrdinalIgnoreCase)||p.Key.Equals("taskId",StringComparison.OrdinalIgnoreCase))
                && SelectedClientOwnershipCatalog.TryReference(p.Key,p.Value.ToString(),out var reference)
                &&await ownership.ClientIdAsync(reference,cancellation)!=clientId)return false;
        }
        foreach(var child in obj.ToArray())if(!await Filter(child.Value,clientId,ownership,cancellation))return false;
        return true;
    }
    private static async Task Deny(HttpContext context,string message)
    {
        if(context.Response.HasStarted)throw new InvalidOperationException("Client scope validation failed after response start.");
        context.Response.StatusCode=403;context.Response.ContentLength=null;context.Response.ContentType="application/json";
        await context.Response.WriteAsJsonAsync(new {error=message});
    }
    private static bool IsDownload(string path)=>new[]{"/download","/export","/pdf","/content","/recordings/","/stream","/formula-workbook","/import-template"}
        .Any(part=>path.Contains(part,StringComparison.OrdinalIgnoreCase));
    private static bool IsSharedDefinition(string resource)=>resource is "form" or "form-version" or "form-field" or "recruitment-template" or "workflow" or "pipeline" or "pipeline-version" or "pipeline-stage" or "pipeline-transition" or "scoring-profile";
}
