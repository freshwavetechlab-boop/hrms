using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

/// <summary>Declarative JSON protocol only: no scripts, expressions or executable templates.</summary>
public static class AttendanceApiProtocol
{
    public static readonly HttpRequestOptionsKey<bool> PrivateNetworkOption = new("AttendanceAdapter.AllowPrivateNetwork");
    private static readonly string[] Tokens = ["fromDate", "toDate", "toDateExclusive", "deviceId", "page", "offset", "pageSize", "secret"];
    private static bool ForbiddenHeader(string key) => new[] { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Authorization", "Proxy-Connection" }.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static void Validate(AttendanceApiAdapter c, bool allowIncompleteDraft = false)
    {
        if (typeof(AttendanceApiAdapter).GetProperties().Any(p => p.PropertyType == typeof(string) && p.GetValue(c) is null))
            throw new InvalidOperationException("Configuration text fields cannot be null; use an empty string for optional fields.");
        var draft = allowIncompleteDraft && !c.IsActive;
        if (c.ClientId <= 0 || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 120 || (!draft && string.IsNullOrWhiteSpace(c.DeviceId)) || c.DeviceId.Length > 100)
            throw new InvalidOperationException("Client, adapter name and registered machine are required.");
        if ((!draft && string.IsNullOrWhiteSpace(c.SourceDeviceId)) || c.SourceDeviceId.Length > 100) throw new InvalidOperationException("Enter the vendor's device identifier.");
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || c.Url.Length > 4096 || c.Url.Contains("{{"))
            throw new InvalidOperationException("Enter a complete HTTP/HTTPS endpoint without credentials or placeholders in the URL. Configure query parameters below.");
        if (c.Method is not ("GET" or "POST")) throw new InvalidOperationException("Select GET or POST.");
        if (c.AuthType is not ("None" or "ApiKey" or "Bearer" or "Basic")) throw new InvalidOperationException("Select a supported authentication type.");
        if (c.AuthType == "ApiKey" && (!Regex.IsMatch(c.AuthHeader, @"^[A-Za-z0-9-]{1,80}$") || ForbiddenHeader(c.AuthHeader))) throw new InvalidOperationException("Enter a valid API key header name.");
        if (c.AuthType == "Basic" && (string.IsNullOrWhiteSpace(c.Username) || c.Username.Contains(':'))) throw new InvalidOperationException("Basic authentication requires a username without a colon.");
        if (c.TimeoutSeconds is < 3 or > 60 || c.LookbackDays is < 1 or > 7 || c.SyncIntervalMinutes is < 0 or > 1440 || c.SyncIntervalMinutes is > 0 and < 5)
            throw new InvalidOperationException("Timeout must be 3–60 seconds; lookback 1–7 days; automatic sync 0 (manual) or 5–1440 minutes.");
        if (c.Pagination is not ("None" or "Page" or "Offset") || c.PageSize is < 1 or > 1000 || c.MaxPages is < 1 or > 100 || c.FirstPage is < 0 or > 1)
            throw new InvalidOperationException("Check pagination: page size 1–1000, maximum pages 1–100, first page 0 or 1.");
        if (c.Headers is null || c.Query is null || c.EmployeeMap is null || c.InValues is null || c.OutValues is null || c.Headers.Count > 30 || c.Query.Count > 30 || c.EmployeeMap.Count > 10000)
            throw new InvalidOperationException("Headers, query, employee mapping and action values must be supplied within their limits.");
        if (c.Query.Any(q => q.Value is null) || c.Headers.Any(h => h.Value is null)) throw new InvalidOperationException("Query/header values must be strings.");
        foreach (var h in c.Headers)
            if (!Regex.IsMatch(h.Key, @"^[A-Za-z0-9-]{1,80}$") || ForbiddenHeader(h.Key) || h.Value.Contains('\r') || h.Value.Contains('\n')) throw new InvalidOperationException("A custom header is invalid or reserved.");
        if ((!draft && (c.InValues.Length == 0 || c.OutValues.Length == 0)) || c.InValues.Concat(c.OutValues).Any(string.IsNullOrWhiteSpace) || c.InValues.Intersect(c.OutValues, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException("Configure distinct, non-empty vendor values for IN and OUT. Confirm numeric meanings with the vendor.");
        if (new[] { c.EmployeePath, c.TimePath, c.ActionPath }.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Employee, punch time and action paths are required.");
        if (c.EmployeeMap.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value) || kv.Key.Length > 100 || kv.Value.Length > 100)) throw new InvalidOperationException("Employee mappings require vendor IDs and employee codes, each up to 100 characters.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId); _ = DateTime.UtcNow.ToString(c.RequestDateFormat, CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or FormatException) { throw new InvalidOperationException("Check the timezone and request date format."); }
        if (c.BodyTemplate is null || c.BodyTemplate.Length > 100000) throw new InvalidOperationException("JSON body template is too large.");
        try { if (JsonNode.Parse(c.BodyTemplate) is not JsonObject) throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The request body must be a JSON object. Put placeholders inside JSON strings."); }
        var template = c.BodyTemplate + JsonSerializer.Serialize(c.Query) + JsonSerializer.Serialize(c.Headers);
        foreach (Match match in Regex.Matches(template, @"\{\{([^{}]+)\}\}"))
            if (!Tokens.Contains(match.Groups[1].Value)) throw new InvalidOperationException("Unknown placeholder: " + match.Value);
        if (c.Pagination != "None" && !(c.BodyTemplate + JsonSerializer.Serialize(c.Query)).Contains(c.Pagination == "Page" ? "{{page}}" : "{{offset}}"))
            throw new InvalidOperationException("Add the page/offset placeholder to the JSON body or query parameters.");
    }

    public static (DateTime From, DateTime To) DateRange(RunAttendanceApiAdapter request)
    {
        if (!DateTime.TryParseExact(request.FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact(request.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            || to < from || to == DateTime.MaxValue.Date || (to - from).Days > 30) throw new InvalidOperationException("Select a date range of up to 31 days (yyyy-MM-dd).");
        return (from, to);
    }

    public static HttpRequestMessage BuildRequest(AttendanceApiAdapter c, DateTime from, DateTime to, int pageIndex, string secret)
    {
        var values = new Dictionary<string, string> {
            ["fromDate"] = from.ToString(c.RequestDateFormat, CultureInfo.InvariantCulture), ["toDate"] = to.AddDays(1).AddTicks(-1).ToString(c.RequestDateFormat, CultureInfo.InvariantCulture),
            ["toDateExclusive"] = to.AddDays(1).ToString(c.RequestDateFormat, CultureInfo.InvariantCulture),
            ["deviceId"] = c.SourceDeviceId, ["page"] = (c.FirstPage + pageIndex).ToString(CultureInfo.InvariantCulture),
            ["offset"] = (pageIndex * c.PageSize).ToString(CultureInfo.InvariantCulture), ["pageSize"] = c.PageSize.ToString(CultureInfo.InvariantCulture), ["secret"] = secret
        };
        string Expand(string s) => Regex.Replace(s, @"\{\{([^{}]+)\}\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new InvalidOperationException("Unknown template placeholder."));
        JsonNode? ExpandNode(JsonNode? node)
        {
            if (node is JsonObject obj) { foreach (var key in obj.Select(p => p.Key).ToArray()) obj[key] = ExpandNode(obj[key]); return obj; }
            if (node is JsonArray arr) { for (var i = 0; i < arr.Count; i++) arr[i] = ExpandNode(arr[i]); return arr; }
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
            {
                if (s is "{{page}}" or "{{offset}}" or "{{pageSize}}") return JsonValue.Create(int.Parse(Expand(s), CultureInfo.InvariantCulture));
                return JsonValue.Create(Expand(s));
            }
            return node;
        }
        var url = c.Url;
        foreach (var q in c.Query) url += (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(Expand(q.Value));
        var request = new HttpRequestMessage(new HttpMethod(c.Method), url);
        request.Options.Set(PrivateNetworkOption, c.AllowPrivateNetwork);
        request.Headers.Accept.ParseAdd("application/json");
        foreach (var h in c.Headers)
            if (!request.Headers.TryAddWithoutValidation(h.Key, Expand(h.Value))) throw new InvalidOperationException("Custom headers must be HTTP request headers; JSON content type is set automatically.");
        if (c.AuthType != "None" && string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Enter the vendor API credential before testing.");
        if (c.AuthType == "ApiKey") { request.Headers.Remove(c.AuthHeader); request.Headers.TryAddWithoutValidation(c.AuthHeader, secret); }
        if (c.AuthType == "Bearer") request.Headers.Authorization = new("Bearer", secret);
        if (c.AuthType == "Basic") request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(c.Username + ":" + secret)));
        if (c.Method == "POST") request.Content = new StringContent(ExpandNode(JsonNode.Parse(c.BodyTemplate))!.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    public static JsonElement At(JsonElement root, string path)
    {
        if (path is "" or "$") return root;
        if (path.StartsWith("$.")) path = path[2..];
        foreach (var part in path.Split('.'))
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(part, out var child)) root = child;
            else if (root.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < root.GetArrayLength()) root = root[index];
            else return default;
        }
        return root;
    }

    public static string Scalar(JsonElement root, string path)
    {
        var value = At(root, path);
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(), _ => "" };
    }

    public static JsonElement Records(AttendanceApiAdapter c, JsonElement root)
    {
        if (c.SuccessPath.Length > 0 && !string.Equals(Scalar(root, c.SuccessPath), c.SuccessValue, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider response did not match the configured success field/value. No records imported.");
        var rows = At(root, c.RecordsPath);
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The records path does not point to a JSON array.");
        return rows;
    }

    public static MachineAttendancePunch? Map(AttendanceApiAdapter c, JsonElement row, DateTime from, DateTime to)
    {
        if (c.DevicePath.Length > 0)
        {
            var device = Scalar(row, c.DevicePath).Trim();
            if (device.Length == 0) throw new InvalidOperationException("Device identifier is missing.");
            if (device != c.SourceDeviceId) return null;
        }
        var userId = Scalar(row, c.EmployeePath).Trim();
        var employee = c.EmployeeMap.GetValueOrDefault(userId, userId);
        if (userId.Length == 0 || employee.Length > 100) throw new InvalidOperationException("Employee identifier is missing or too long.");
        var mode = Scalar(row, c.ActionPath).Trim();
        var action = c.InValues.Contains(mode, StringComparer.OrdinalIgnoreCase) ? "IN" : c.OutValues.Contains(mode, StringComparer.OrdinalIgnoreCase) ? "OUT" : throw new InvalidOperationException("Unmapped IN/OUT value. Update the configured action values.");
        var time = Scalar(row, c.TimePath);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId);
        DateTimeOffset instant;
        if (Regex.IsMatch(time, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase))
        {
            if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant)) throw new InvalidOperationException("Invalid punch timestamp.");
        }
        else
        {
            DateTime local;
            var valid = c.PunchTimeFormat.Length > 0
                ? DateTime.TryParseExact(time, c.PunchTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out local)
                : DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
            if (!valid) throw new InvalidOperationException("Invalid punch timestamp. Check the punch time format.");
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) throw new InvalidOperationException("Ambiguous/invalid local punch time requires an explicit timezone offset.");
            instant = new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        var date = TimeZoneInfo.ConvertTime(instant, zone).Date;
        if (date < from || date > to) return null;
        var id = c.PunchIdPath.Length > 0 ? Scalar(row, c.PunchIdPath).Trim() : "";
        if (c.PunchIdPath.Length > 0 && id.Length == 0) throw new InvalidOperationException("Configured unique punch ID is missing.");
        // The fallback intentionally treats identical employee/device/time/direction records as one punch.
        var identity = id.Length > 0 ? new[] { c.SourceDeviceId, "vendor-id", id } : new[] { c.SourceDeviceId, userId, instant.ToUniversalTime().ToString("O"), action };
        id = "api:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
        return new(id, employee, action, instant.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
    }

    public static bool AllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Parse("fd00:ec2::254")) || address.Equals(IPAddress.Parse("168.63.129.16"))) return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254) return false;
            var isPrivate = bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
            return !isPrivate || allowPrivate;
        }
        // Only globally routable unicast or explicitly permitted unique-local IPv6.
        return bytes[0] is >= 0x20 and <= 0x3f || allowPrivate && (bytes[0] & 0xfe) == 0xfc;
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(PrivateNetworkOption, out var value) && value;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(ip => !AllowedAddress(ip, allowPrivate))) throw new HttpRequestException("Endpoint resolves to a blocked network address.");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); if (ct.IsCancellationRequested) throw; }
            }
            throw new HttpRequestException("Unable to connect to the configured API server.");
        }
    };
}
