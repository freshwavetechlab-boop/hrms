using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class AttendanceApiAdapterService(AttendanceIntegrationRepository repository,
    EssMssRepository attendance, PortableIntegrationCredentialProtector protector, IHttpClientFactory clients)
{
    private sealed record Credentials(string Secret = "", string DeviceToken = "");
    private Credentials ReadCredentials(AttendanceIntegrationRepository.StoredApiAdapter row)
    {
        if (row.ProtectedCredentials.Length == 0) return new();
        var json = protector.UnprotectAttendanceAdapter(row.ProtectedCredentials);
        return json is null ? throw new InvalidOperationException("Saved credentials are unavailable. Re-enter both API credentials.") : JsonSerializer.Deserialize<Credentials>(json) ?? new();
    }

    public async Task<AttendanceApiAdapter[]> ListAsync(int clientId) =>
        (await repository.GetAdaptersAsync(clientId)).Select(r => r.Configuration).ToArray();

    public async Task<AttendanceApiAdapter> SaveAsync(SaveAttendanceApiAdapter request)
    {
        var c = request.Configuration ?? throw new InvalidOperationException("Adapter configuration is required.");
        AttendanceApiProtocol.Validate(c, allowIncompleteDraft: true);
        if ((request.Secret?.Length ?? 0) > 16000 || (request.DeviceToken?.Length ?? 0) > 100) throw new InvalidOperationException("Credential exceeds the supported length.");
        if (request.Secret?.IndexOfAny(['\r', '\n']) >= 0) throw new InvalidOperationException("API credential cannot contain a newline.");
        if (!string.IsNullOrWhiteSpace(request.DeviceToken))
        {
            var device = await repository.AuthenticateAsync(request.DeviceToken.Trim());
            if (device is null || device.ClientId != c.ClientId || device.DeviceId != c.DeviceId) throw new InvalidOperationException("The HRMS machine API key does not match this client and machine, or has expired.");
        }
        await using var gate = c.Id.Length == 0 ? null : await repository.AcquireAdapterLockAsync(c.ClientId, c.Id);
        if (c.Id.Length > 0 && gate is null) throw new InvalidOperationException("Adapter is running. Wait for it to finish before editing.");
        var originalId = c.Id;
        try
        {
            return await repository.SaveAdapterAsync(c, existing =>
            {
                // Changing machine requires its own key, never the previous machine's saved key.
                var replaceBoth = (request.ClearSecret || !string.IsNullOrWhiteSpace(request.Secret))
                    && (request.ClearDeviceToken || !string.IsNullOrWhiteSpace(request.DeviceToken));
                var old = existing is null || replaceBoth ? new Credentials() : ReadCredentials(existing);
                var secret = request.ClearSecret ? "" : string.IsNullOrWhiteSpace(request.Secret) ? old.Secret : request.Secret;
                var token = request.ClearDeviceToken ? "" : string.IsNullOrWhiteSpace(request.DeviceToken)
                    ? existing?.Configuration.DeviceId == c.DeviceId ? old.DeviceToken : "" : request.DeviceToken.Trim();
                c.HasSecret = secret.Length > 0; c.HasDeviceToken = token.Length > 0;
                if (c.IsActive && (c.AuthType != "None" && !c.HasSecret || !c.HasDeviceToken)) throw new InvalidOperationException("Enabled adapters require the vendor credential (unless unauthenticated) and the HRMS machine API key.");
                return protector.ProtectAttendanceAdapter(JsonSerializer.Serialize(new Credentials(secret, token)));
            });
        }
        finally { if (gate is not null) await AttendanceIntegrationRepository.ReleaseAdapterLockAsync(gate, c.ClientId, originalId); }
    }

    public async Task DeleteAsync(int clientId, string id)
    {
        await using var gate = await repository.AcquireAdapterLockAsync(clientId, id);
        if (gate is null) throw new InvalidOperationException("Adapter is running. Wait for it to finish before deleting.");
        try { await repository.DeleteAdapterAsync(clientId, id); }
        finally { await AttendanceIntegrationRepository.ReleaseAdapterLockAsync(gate, clientId, id); }
    }

    public async Task<AttendanceAdapterRun> RunAsync(int clientId, string id, RunAttendanceApiAdapter dates, bool preview, CancellationToken cancellationToken, bool scheduled = false)
    {
        var (from, to) = AttendanceApiProtocol.DateRange(dates);
        await using var gate = await repository.AcquireAdapterLockAsync(clientId, id);
        if (gate is null) throw new InvalidOperationException("This adapter is already running.");
        try
        {
            var row = (await repository.GetAdaptersAsync(clientId)).FirstOrDefault(r => r.Configuration.Id == id)
                ?? throw new InvalidOperationException("Adapter not found for this client.");
            var c = row.Configuration;
            if (!preview && !c.IsActive) throw new InvalidOperationException("Enable the adapter before syncing attendance.");
            if (scheduled && (c.SyncIntervalMinutes == 0 || c.LastAttemptAt?.AddMinutes(c.SyncIntervalMinutes) > DateTime.UtcNow))
                return new() { Success = true, Message = "Not due." };
            var run = new AttendanceAdapterRun { Preview = preview };
            var timer = Stopwatch.StartNew();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                AttendanceApiProtocol.Validate(c);
                var credentials = ReadCredentials(row);
                var punches = new List<MachineAttendancePunch>();
                var pages = new HashSet<string>();
                using var client = clients.CreateClient("AttendanceApiAdapter");
                client.Timeout = Timeout.InfiniteTimeSpan;
                for (var page = 0; page < (c.Pagination == "None" ? 1 : c.MaxPages); page++)
                {
                    using var request = AttendanceApiProtocol.BuildRequest(c, from, to, page, credentials.Secret);
                    // Check every request, including connections reused from the pool.
                    var addresses = await Dns.GetHostAddressesAsync(request.RequestUri!.Host, budget.Token);
                    if (addresses.Length == 0 || addresses.Any(ip => !AttendanceApiProtocol.AllowedAddress(ip, c.AllowPrivateNetwork)))
                        throw new InvalidOperationException("Endpoint uses a blocked address. Private client networks require the private-network option and server-side connectivity; loopback and metadata addresses are blocked.");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(c.TimeoutSeconds));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    run.HttpStatus = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned HTTP {(int)response.StatusCode}. Check endpoint, authentication and access. Redirects are not followed.");
                    if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidOperationException("Response exceeds 8 MB. Use pagination or a smaller date range.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var buffer = new MemoryStream();
                    var bytes = new byte[16384]; int read;
                    while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
                    {
                        if (buffer.Length + read > 8 * 1024 * 1024) throw new InvalidOperationException("Response exceeds 8 MB. Use pagination or a smaller date range.");
                        buffer.Write(bytes, 0, read);
                    }
                    buffer.Position = 0;
                    using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
                    var records = AttendanceApiProtocol.Records(c, doc.RootElement);
                    var count = records.GetArrayLength();
                    if (count > 0 && !pages.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(records.GetRawText())))))
                        throw new InvalidOperationException("Provider repeated a page. Check pagination parameters; no records imported.");
                    if (run.Fetched + count > 10000) throw new InvalidOperationException("More than 10,000 punches returned. Use a smaller date range.");
                    foreach (var record in records.EnumerateArray())
                    {
                        run.Fetched++;
                        try
                        {
                            var punch = AttendanceApiProtocol.Map(c, record, from, to);
                            if (punch is null) { run.Skipped++; continue; }
                            punches.Add(punch); run.Mapped++;
                        }
                        catch (InvalidOperationException e) { run.Rejected++; if (run.Errors.Count < 20) run.Errors.Add($"Record {run.Fetched}: {e.Message}"); }
                    }
                    if (c.Pagination == "None" || count < c.PageSize) break;
                    if (page == c.MaxPages - 1) throw new InvalidOperationException("Maximum pages reached. Increase the limit or reduce the date range; no records imported.");
                }
                run.Samples = preview ? punches.Take(20).ToList() : [];
                if (run.Rejected > 0) throw new InvalidOperationException("Fix the mapping errors and test again. No attendance imported.");
                if (!preview && punches.Count > 0)
                {
                    var device = await repository.AuthenticateAsync(credentials.DeviceToken);
                    if (device is null || device.ClientId != clientId || device.DeviceId != c.DeviceId)
                        throw new InvalidOperationException("The saved HRMS machine API key is missing, expired or revoked. Generate a matching key in API Catalog and update this adapter.");
                    // Existing importer retains location, employment, payroll-lock, leave and duplicate protections.
                    foreach (var batch in punches.Chunk(500))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        var result = await attendance.ImportMachinePunchesAsync(device, credentials.DeviceToken, new(c.DeviceId, batch.ToList()));
                        foreach (var item in result)
                        {
                            if (item.Status == "Accepted") run.Accepted++;
                            else if (item.Status == "Duplicate") run.Duplicates++;
                            else { run.Rejected++; if (run.Errors.Count < 20) run.Errors.Add(item.Error ?? "Attendance record rejected."); }
                        }
                    }
                }
                run.Success = run.Rejected == 0;
                run.Message = preview ? $"Test complete: {run.Mapped} mapped, {run.Skipped} outside device/date scope. No attendance imported."
                    : $"Sync complete: {run.Accepted} accepted, {run.Duplicates} duplicates, {run.Rejected} rejected, {run.Skipped} outside device/date scope.";
            }
            catch (OperationCanceledException) { run.Message = "Request timed out or was cancelled. Check API availability/network access. Any accepted punches are safe to retry."; }
            catch (HttpRequestException) { run.Message = "Cannot connect to the API server. Check its address, port, TLS certificate and network/VPN access from the HRMS API server."; }
            catch (System.Net.Sockets.SocketException) { run.Message = "API hostname could not be resolved. Check the configured server address and DNS/network access."; }
            catch (JsonException) { run.Message = "API did not return valid JSON. Check endpoint and response configuration."; }
            catch (Exception e) when (e is FormatException or ArgumentException) { run.Message = "Request configuration is invalid. Check URL, headers, credentials and date/time formats."; }
            catch (MySqlConnector.MySqlException) { run.Message = "Attendance storage was interrupted. Refresh the result and retry; existing punches are deduplicated."; }
            catch (InvalidOperationException e) { run.Message = e.Message; }
            run.DurationMs = timer.ElapsedMilliseconds;
            await repository.RecordAdapterRunAsync(clientId, id, c.Revision, run);
            return run;
        }
        finally { await AttendanceIntegrationRepository.ReleaseAdapterLockAsync(gate, clientId, id); }
    }
}
