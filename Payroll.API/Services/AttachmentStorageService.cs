using System.Net.Http.Headers;
using Payroll.API.Models;

namespace Payroll.API.Services;

public class AttachmentStorageService(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment, IConfiguration configuration)
{
    public async Task WriteAsync(AttachmentStorageServer server, string storageKey, Stream content, CancellationToken cancellationToken)
    {
        if (IsFileSystem(server))
        {
            var fullPath = ResolveFilePath(server, storageKey);
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Attachment directory is invalid.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.uploading";
            try
            {
                await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await content.CopyToAsync(target, cancellationToken);
                File.Move(temporaryPath, fullPath, false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return;
        }

        if (server.StorageType.Equals("HttpFileServer", StringComparison.OrdinalIgnoreCase))
        {
            using var request = CreateRemoteRequest(server, HttpMethod.Put, storageKey);
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await httpClientFactory.CreateClient(nameof(AttachmentStorageService)).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Remote file server rejected the upload ({(int)response.StatusCode}).");
            return;
        }

        throw new InvalidOperationException($"Storage type '{server.StorageType}' is not supported.");
    }

    public async Task<AttachmentFileHandle> OpenReadAsync(AttachmentStorageServer server, string storageKey, CancellationToken cancellationToken)
    {
        if (IsFileSystem(server))
        {
            var fullPath = ResolveFilePath(server, storageKey);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Attachment file was not found.");
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new AttachmentFileHandle(stream);
        }

        if (server.StorageType.Equals("HttpFileServer", StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateRemoteRequest(server, HttpMethod.Get, storageKey);
            var response = await httpClientFactory.CreateClient(nameof(AttachmentStorageService)).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            request.Dispose();
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new FileNotFoundException("Remote attachment file was not found.");
            }
            return new AttachmentFileHandle(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        }

        throw new InvalidOperationException($"Storage type '{server.StorageType}' is not supported.");
    }

    public async Task DeleteAsync(AttachmentStorageServer server, string storageKey, CancellationToken cancellationToken)
    {
        if (IsFileSystem(server))
        {
            var fullPath = ResolveFilePath(server, storageKey);
            if (File.Exists(fullPath)) File.Delete(fullPath);
            return;
        }

        if (server.StorageType.Equals("HttpFileServer", StringComparison.OrdinalIgnoreCase))
        {
            using var request = CreateRemoteRequest(server, HttpMethod.Delete, storageKey);
            using var response = await httpClientFactory.CreateClient(nameof(AttachmentStorageService)).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"Remote file server rejected deletion ({(int)response.StatusCode}).");
            return;
        }
    }

    public async Task<AttachmentStorageHealthResult> TestAsync(AttachmentStorageServer server, CancellationToken cancellationToken)
    {
        try
        {
            if (IsFileSystem(server))
            {
                var root = ResolveRoot(server);
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, $".attachment-health-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probe, "ok", cancellationToken);
                File.Delete(probe);
                var drive = TryDriveInfo(root);
                return new AttachmentStorageHealthResult
                {
                    Healthy = true,
                    Status = "Healthy",
                    Message = "Folder is reachable and writable.",
                    AvailableBytes = drive?.AvailableFreeSpace,
                    TotalBytes = drive?.TotalSize
                };
            }

            if (server.StorageType.Equals("HttpFileServer", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"{server.ServiceUrl.TrimEnd('/')}/health";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddCredential(request, server);
                using var response = await httpClientFactory.CreateClient(nameof(AttachmentStorageService)).SendAsync(request, cancellationToken);
                return new AttachmentStorageHealthResult
                {
                    Healthy = response.IsSuccessStatusCode,
                    Status = response.IsSuccessStatusCode ? "Healthy" : "Unhealthy",
                    Message = response.IsSuccessStatusCode ? "Remote file server is reachable." : $"Health check returned {(int)response.StatusCode}."
                };
            }

            return new AttachmentStorageHealthResult { Healthy = false, Status = "Unsupported", Message = $"Storage type '{server.StorageType}' is not supported." };
        }
        catch (Exception exception)
        {
            return new AttachmentStorageHealthResult { Healthy = false, Status = "Unhealthy", Message = exception.Message };
        }
    }

    public string ResolveRoot(AttachmentStorageServer server)
    {
        var basePath = string.IsNullOrWhiteSpace(server.BasePath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "attachments")
            : server.BasePath.Trim();
        var resolved = Path.GetFullPath(Path.IsPathRooted(basePath) ? basePath : Path.Combine(environment.ContentRootPath, basePath));
        if (!server.StorageType.Equals("LocalFileSystem", StringComparison.OrdinalIgnoreCase)) return resolved;

        var configuredDataRoot = configuration["AttachmentStorage:DataRootPath"];
        var allowedRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDataRoot)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.IsPathRooted(configuredDataRoot)
                ? configuredDataRoot
                : Path.Combine(environment.ContentRootPath, configuredDataRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = allowedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.Equals(allowedRoot, comparison) && !resolved.StartsWith(prefix, comparison))
            throw new InvalidOperationException($"Local attachment storage must stay under the configured data root '{allowedRoot}'. Use MountedFileSystem for an external volume.");
        return resolved;
    }

    private string ResolveFilePath(AttachmentStorageServer server, string storageKey)
    {
        var root = ResolveRoot(server);
        var safeKey = storageKey.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(root, safeKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment storage path is invalid.");
        return fullPath;
    }

    private static bool IsFileSystem(AttachmentStorageServer server) =>
        server.StorageType.Equals("LocalFileSystem", StringComparison.OrdinalIgnoreCase) ||
        server.StorageType.Equals("MountedFileSystem", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage CreateRemoteRequest(AttachmentStorageServer server, HttpMethod method, string storageKey)
    {
        if (string.IsNullOrWhiteSpace(server.ServiceUrl)) throw new InvalidOperationException("Remote file server URL is required.");
        var encodedKey = string.Join("/", storageKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, $"{server.ServiceUrl.TrimEnd('/')}/files/{encodedKey}");
        AddCredential(request, server);
        return request;
    }

    private static void AddCredential(HttpRequestMessage request, AttachmentStorageServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.Credential))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Credential);
    }

    private static DriveInfo? TryDriveInfo(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!); }
        catch { return null; }
    }
}
