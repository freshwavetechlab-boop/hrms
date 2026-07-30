using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class GoogleDriveOAuthService
{
    public const string StorageType = "GoogleDrive";
    public const string PopupMessageType = "frevo:google-drive-oauth";
    public const string CallbackPath = "/api/public/attachment-storage-servers/google/callback";

    private const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string FolderMarkerKey = "frevoHrmsFolder";
    private const string FolderMarkerValue = "attachments-v1";
    private const string LogicalPathMarkerKey = "frevoHrmsLogicalPath";
    private const string LogicalPathMarkerValue = "frevopilot-chat-v1";
    public const long MaximumOAuthCredentialFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly IDataProtector stateProtector;
    private readonly ConcurrentDictionary<string, CachedAccessToken> accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tokenLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> pathLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> folderPathLocks = new(StringComparer.Ordinal);

    public GoogleDriveOAuthService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.environment = environment;
        stateProtector = dataProtectionProvider.CreateProtector("Payroll.API.GoogleDriveOAuthState.v1");
    }

    public GoogleDriveAuthorizationRequest CreateAuthorizationRequest(
        long storageServerId,
        int actorUserId,
        string portalOrigin,
        string requestBaseUri,
        string existingFolderId,
        GoogleDriveCredential? credential)
    {
        var oauthClient = RequireOAuthClient(credential);
        var normalizedPortalOrigin = NormalizeAndValidatePortalOrigin(portalOrigin);
        var redirectUri = ResolveRedirectUri(requestBaseUri);
        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var statePayload = new GoogleDriveOAuthState
        {
            StorageServerId = storageServerId,
            ActorUserId = actorUserId,
            PortalOrigin = normalizedPortalOrigin,
            RedirectUri = redirectUri,
            ExistingFolderId = existingFolderId?.Trim() ?? "",
            OAuthClientId = oauthClient.ClientId,
            CodeVerifier = codeVerifier,
            Nonce = Base64Url(RandomNumberGenerator.GetBytes(24)),
            IssuedAtUtc = DateTime.UtcNow
        };
        var state = stateProtector.Protect(JsonSerializer.Serialize(statePayload, JsonOptions));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = oauthClient.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = $"openid email {DriveFileScope}",
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["prompt"] = "select_account consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        };
        return new GoogleDriveAuthorizationRequest(
            storageServerId,
            $"https://accounts.google.com/o/oauth2/v2/auth?{BuildQuery(query)}");
    }

    public GoogleDriveOAuthState ReadAndValidateState(string protectedState)
    {
        if (string.IsNullOrWhiteSpace(protectedState))
            throw new InvalidOperationException("Google Drive connection state is missing.");

        GoogleDriveOAuthState? state;
        try
        {
            state = JsonSerializer.Deserialize<GoogleDriveOAuthState>(
                stateProtector.Unprotect(protectedState),
                JsonOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new InvalidOperationException("Google Drive connection state is invalid or has expired.");
        }

        if (state is null ||
            state.StorageServerId <= 0 ||
            state.ActorUserId <= 0 ||
            string.IsNullOrWhiteSpace(state.CodeVerifier) ||
            string.IsNullOrWhiteSpace(state.Nonce) ||
            string.IsNullOrWhiteSpace(state.OAuthClientId) ||
            string.IsNullOrWhiteSpace(state.RedirectUri) ||
            string.IsNullOrWhiteSpace(state.PortalOrigin))
            throw new InvalidOperationException("Google Drive connection state is invalid.");
        if (state.IssuedAtUtc < DateTime.UtcNow.AddMinutes(-10) || state.IssuedAtUtc > DateTime.UtcNow.AddMinutes(1))
            throw new InvalidOperationException("Google Drive connection request expired. Start the connection again.");

        _ = NormalizeAndValidatePortalOrigin(state.PortalOrigin);
        return state;
    }

    public async Task<GoogleDriveAuthorizationResult> CompleteAuthorizationAsync(
        string authorizationCode,
        GoogleDriveOAuthState state,
        GoogleDriveOAuthClient oauthClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new InvalidOperationException("Google did not return an authorization code.");

        if (!state.OAuthClientId.Equals(oauthClient.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google OAuth client configuration changed. Start the connection again.");

        var token = await ExchangeAuthorizationCodeAsync(authorizationCode, state, oauthClient, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidOperationException("Google did not issue offline access. Start the connection again and approve Drive access.");

        var account = await ReadAccountAsync(token.AccessToken, cancellationToken);
        var folder = await EnsureFolderAsync(token.AccessToken, state.ExistingFolderId, cancellationToken);
        CacheAccessToken(token.RefreshToken, oauthClient, token.AccessToken, token.ExpiresIn);

        var connectedAt = DateTime.UtcNow;
        var credential = new GoogleDriveCredential
        {
            OAuthClientId = oauthClient.ClientId,
            OAuthClientSecret = oauthClient.ClientSecret,
            RefreshToken = token.RefreshToken,
            AccountSubject = account.Subject,
            AccountEmail = account.Email,
            AccountName = account.Name,
            FolderId = folder.Id,
            FolderName = folder.Name,
            ConnectedAtUtc = connectedAt
        };
        return new GoogleDriveAuthorizationResult
        {
            StorageServerId = state.StorageServerId,
            ActorUserId = state.ActorUserId,
            PortalOrigin = state.PortalOrigin,
            CredentialJson = JsonSerializer.Serialize(credential, JsonOptions),
            Credential = credential,
            FolderUrl = FolderUrl(folder.Id)
        };
    }

    public async Task<string> WriteAsync(
        AttachmentStorageServer server,
        string storageKey,
        Stream content,
        CancellationToken cancellationToken)
    {
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        var fileName = Path.GetFileName(storageKey.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"{Guid.NewGuid():N}.bin";
        return await UploadFileAsync(accessToken, credential.FolderId, fileName, content, cancellationToken);
    }

    public async Task<string> UpsertPathAsync(
        AttachmentStorageServer server,
        string storagePath,
        Stream content,
        CancellationToken cancellationToken)
    {
        var segments = NormalizeStoragePath(storagePath);
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        var lockKey = $"{credential.FolderId}:{string.Join('/', segments)}";
        var gate = pathLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var parentId = await ResolveFolderPathAsync(accessToken, credential.FolderId, segments[..^1], true, cancellationToken)
                           ?? throw new InvalidOperationException("Google Drive chat folder could not be created.");
            var existing = await FindChildAsync(accessToken, parentId, segments[^1], false, cancellationToken);
            return existing is null
                ? await UploadFileAsync(accessToken, parentId, segments[^1], content, cancellationToken, markLogicalPath: true)
                : await UpdateFileAsync(accessToken, existing.Id, content, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AttachmentFileHandle?> TryOpenPathAsync(
        AttachmentStorageServer server,
        string storagePath,
        CancellationToken cancellationToken)
    {
        var segments = NormalizeStoragePath(storagePath);
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        var parentId = await ResolveFolderPathAsync(accessToken, credential.FolderId, segments[..^1], false, cancellationToken);
        if (string.IsNullOrWhiteSpace(parentId)) return null;
        var file = await FindChildAsync(accessToken, parentId, segments[^1], false, cancellationToken);
        if (file is null) return null;
        return await OpenReadWithAccessTokenAsync(accessToken, file.Id, cancellationToken);
    }

    public async Task DeletePathAsync(
        AttachmentStorageServer server,
        string storagePath,
        CancellationToken cancellationToken)
    {
        var segments = NormalizeStoragePath(storagePath);
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        var parentId = await ResolveFolderPathAsync(accessToken, credential.FolderId, segments[..^1], false, cancellationToken);
        if (string.IsNullOrWhiteSpace(parentId)) return;
        var file = await FindChildAsync(accessToken, parentId, segments[^1], false, cancellationToken);
        if (file is not null) await DeleteFileWithAccessTokenAsync(accessToken, file.Id, cancellationToken);
    }

    public async Task<AttachmentFileHandle> OpenReadAsync(
        AttachmentStorageServer server,
        string fileId,
        CancellationToken cancellationToken)
    {
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        return await OpenReadWithAccessTokenAsync(accessToken, fileId, cancellationToken);
    }

    public async Task DeleteAsync(
        AttachmentStorageServer server,
        string fileId,
        CancellationToken cancellationToken)
    {
        var credential = RequireCredential(server);
        var oauthClient = RequireOAuthClient(credential);
        var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
        using var request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}",
            accessToken);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Google Drive rejected deletion ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
    }

    public async Task<AttachmentStorageHealthResult> TestAsync(
        AttachmentStorageServer server,
        CancellationToken cancellationToken)
    {
        try
        {
            var credential = RequireCredential(server);
            var oauthClient = RequireOAuthClient(credential);
            var accessToken = await GetAccessTokenAsync(credential.RefreshToken, oauthClient, cancellationToken);
            var folder = await TryGetFolderAsync(accessToken, credential.FolderId, cancellationToken);
            if (folder is null)
                return Unhealthy("Connected Google Drive folder is unavailable. Reconnect Google Drive.");

            var probeFileId = "";
            try
            {
                await using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
                probeFileId = await UploadFileAsync(
                    accessToken,
                    folder.Id,
                    $".frevo-health-{Guid.NewGuid():N}.tmp",
                    empty,
                    cancellationToken);
                using var readRequest = CreateAuthorizedRequest(
                    HttpMethod.Get,
                    $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(probeFileId)}?alt=media",
                    accessToken);
                using var readResponse = await Client().SendAsync(readRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!readResponse.IsSuccessStatusCode)
                    return Unhealthy($"Google Drive write succeeded but read verification failed ({(int)readResponse.StatusCode}).");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(probeFileId))
                    await DeleteFileWithAccessTokenAsync(accessToken, probeFileId, CancellationToken.None);
            }

            var accountText = string.IsNullOrWhiteSpace(credential.AccountEmail) ? "" : $" for {credential.AccountEmail}";
            return new AttachmentStorageHealthResult
            {
                Healthy = true,
                Status = "Healthy",
                Message = $"Google Drive folder '{folder.Name}' is connected, readable, and writable{accountText}."
            };
        }
        catch (Exception exception)
        {
            return Unhealthy(exception.Message);
        }
    }

    public GoogleDriveCredential? TryReadCredential(string credentialJson)
    {
        if (string.IsNullOrWhiteSpace(credentialJson)) return null;
        try
        {
            var credential = JsonSerializer.Deserialize<GoogleDriveCredential>(credentialJson, JsonOptions);
            return credential is not null &&
                   (!string.IsNullOrWhiteSpace(credential.OAuthClientId) ||
                    !string.IsNullOrWhiteSpace(credential.OAuthClientSecret) ||
                    !string.IsNullOrWhiteSpace(credential.RefreshToken) ||
                    !string.IsNullOrWhiteSpace(credential.FolderId))
                ? credential
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string SerializeCredential(GoogleDriveCredential credential) =>
        JsonSerializer.Serialize(credential, JsonOptions);

    public bool HasOAuthClientConfiguration(GoogleDriveCredential? credential) =>
        TryResolveOAuthClient(credential) is not null;

    public bool IsConnected(GoogleDriveCredential? credential) =>
        credential is not null &&
        !string.IsNullOrWhiteSpace(credential.RefreshToken) &&
        !string.IsNullOrWhiteSpace(credential.FolderId) &&
        HasOAuthClientConfiguration(credential);

    public string ConnectionStatus(GoogleDriveCredential? credential) =>
        IsConnected(credential)
            ? "Connected"
            : HasOAuthClientConfiguration(credential)
                ? "Ready to connect"
                : "Not configured";

    public GoogleDriveOAuthClient? TryResolveOAuthClient(GoogleDriveCredential? credential)
    {
        if (credential is not null &&
            !string.IsNullOrWhiteSpace(credential.OAuthClientId) &&
            !string.IsNullOrWhiteSpace(credential.OAuthClientSecret))
            return new GoogleDriveOAuthClient
            {
                ClientId = credential.OAuthClientId.Trim(),
                ClientSecret = credential.OAuthClientSecret.Trim()
            };

        var clientId = configuration["GoogleDriveOAuth:ClientId"]?.Trim() ?? "";
        var clientSecret = configuration["GoogleDriveOAuth:ClientSecret"]?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            ? null
            : new GoogleDriveOAuthClient { ClientId = clientId, ClientSecret = clientSecret };
    }

    public GoogleDriveOAuthClient RequireOAuthClient(GoogleDriveCredential? credential) =>
        TryResolveOAuthClient(credential)
        ?? throw new InvalidOperationException("Google OAuth is not configured. Upload the downloaded Web OAuth client JSON first.");

    public async Task<GoogleDriveOAuthClient> ParseOAuthClientConfigurationAsync(
        IFormFile credentialFile,
        string expectedRedirectUri,
        CancellationToken cancellationToken)
    {
        if (credentialFile.Length <= 0)
            throw new InvalidOperationException("Select the downloaded Google Web OAuth client JSON file.");
        if (credentialFile.Length > MaximumOAuthCredentialFileBytes)
            throw new InvalidOperationException($"Google OAuth credential JSON must be smaller than {MaximumOAuthCredentialFileBytes / 1024} KB.");
        if (!Path.GetExtension(credentialFile.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google OAuth credential file must be a .json file.");

        var contentType = (credentialFile.ContentType ?? "").Split(';', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("text/json", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google OAuth credential file must contain JSON.");

        await using var source = credentialFile.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(credentialFile.Length, MaximumOAuthCredentialFileBytes));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumOAuthCredentialFileBytes)
                throw new InvalidOperationException($"Google OAuth credential JSON must be smaller than {MaximumOAuthCredentialFileBytes / 1024} KB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Google OAuth credential file contains invalid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToList()
                : [];
            if (root.ValueKind != JsonValueKind.Object ||
                rootProperties.Count != 1 ||
                !rootProperties[0].NameEquals("web") ||
                !root.TryGetProperty("web", out var web) ||
                web.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Upload a Web application OAuth client JSON downloaded from Google Cloud.");

            var clientId = RequiredJsonString(web, "client_id", 512);
            var clientSecret = RequiredJsonString(web, "client_secret", 1024);
            var authUri = RequiredJsonString(web, "auth_uri", 500);
            var tokenUri = RequiredJsonString(web, "token_uri", 500);
            if (!clientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Google OAuth client_id is invalid.");
            if (!authUri.Equals("https://accounts.google.com/o/oauth2/auth", StringComparison.Ordinal))
                throw new InvalidOperationException("Google OAuth auth_uri is invalid.");
            if (!tokenUri.Equals("https://oauth2.googleapis.com/token", StringComparison.Ordinal))
                throw new InvalidOperationException("Google OAuth token_uri is invalid.");
            if (!web.TryGetProperty("redirect_uris", out var redirectUris) ||
                redirectUris.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Google Web OAuth JSON does not contain redirect_uris.");

            var callbackRegistered = redirectUris.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Any(item => string.Equals(item, expectedRedirectUri, StringComparison.Ordinal));
            if (!callbackRegistered)
                throw new InvalidOperationException($"Add this authorized redirect URI in Google Cloud, download the JSON again, and re-upload it: {expectedRedirectUri}");

            return new GoogleDriveOAuthClient
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            };
        }
    }

    public string ResolvePortalOrigin(string originHeader, string refererHeader)
    {
        var candidate = originHeader?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(candidate) &&
            Uri.TryCreate(refererHeader, UriKind.Absolute, out var referer))
            candidate = referer.GetLeftPart(UriPartial.Authority);
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = configuration["GoogleDriveOAuth:PortalOrigin"]?.Trim() ?? "";
        return NormalizeAndValidatePortalOrigin(candidate);
    }

    public string ResolveRedirectUri(string requestBaseUri)
    {
        var configured = configuration["GoogleDriveOAuth:RedirectUri"]?.Trim();
        var value = string.IsNullOrWhiteSpace(configured)
            ? $"{requestBaseUri.TrimEnd('/')}{CallbackPath}"
            : configured;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("GoogleDriveOAuth:RedirectUri must be an absolute HTTP or HTTPS URL.");
        if (!environment.IsDevelopment() &&
            uri.Scheme != Uri.UriSchemeHttps &&
            !uri.IsLoopback)
            throw new InvalidOperationException("Google Drive OAuth callback must use HTTPS outside development.");
        return uri.AbsoluteUri;
    }

    public static string FolderUrl(string folderId) =>
        string.IsNullOrWhiteSpace(folderId)
            ? ""
            : $"https://drive.google.com/drive/folders/{Uri.EscapeDataString(folderId)}";

    private GoogleDriveCredential RequireCredential(AttachmentStorageServer server)
    {
        var credential = TryReadCredential(server.Credential);
        if (!IsConnected(credential))
            throw new InvalidOperationException("Google Drive is not connected. Connect the Google account from Storage Servers.");
        return credential!;
    }

    private async Task<OAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        GoogleDriveOAuthState state,
        GoogleDriveOAuthClient oauthClient,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = oauthClient.ClientId,
            ["client_secret"] = oauthClient.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = state.CodeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = state.RedirectUri
        });
        using var response = await Client().PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google authorization failed ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(JsonOptions, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("Google authorization returned an invalid access token.");
        return token;
    }

    private async Task<GoogleAccountInfo> ReadAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            "https://openidconnect.googleapis.com/v1/userinfo",
            accessToken);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new GoogleAccountInfo();
        return await response.Content.ReadFromJsonAsync<GoogleAccountInfo>(JsonOptions, cancellationToken)
               ?? new GoogleAccountInfo();
    }

    private async Task<DriveFolderInfo> EnsureFolderAsync(
        string accessToken,
        string existingFolderId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingFolderId))
        {
            var existing = await TryGetFolderAsync(accessToken, existingFolderId, cancellationToken);
            if (existing is not null) return existing;
        }

        var query = $"mimeType = '{FolderMimeType}' and trashed = false and appProperties has {{ key='{FolderMarkerKey}' and value='{FolderMarkerValue}' }}";
        var listUrl = "https://www.googleapis.com/drive/v3/files?" + BuildQuery(new Dictionary<string, string>
        {
            ["spaces"] = "drive",
            ["pageSize"] = "10",
            ["fields"] = "files(id,name)",
            ["q"] = query
        });
        using (var listRequest = CreateAuthorizedRequest(HttpMethod.Get, listUrl, accessToken))
        using (var listResponse = await Client().SendAsync(listRequest, cancellationToken))
        {
            if (!listResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google Drive folder lookup failed ({await ReadGoogleErrorAsync(listResponse, cancellationToken)}).");
            var list = await listResponse.Content.ReadFromJsonAsync<DriveFileList>(JsonOptions, cancellationToken);
            var found = list?.Files.FirstOrDefault();
            if (found is not null && !string.IsNullOrWhiteSpace(found.Id)) return found;
        }

        var folderName = configuration["GoogleDriveOAuth:FolderName"]?.Trim();
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Frevo HRMS Attachments";
        var metadata = new
        {
            name = folderName,
            mimeType = FolderMimeType,
            parents = new[] { "root" },
            appProperties = new Dictionary<string, string> { [FolderMarkerKey] = FolderMarkerValue }
        };
        using var createRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/files?fields=id,name",
            accessToken);
        createRequest.Content = JsonContent.Create(metadata, options: JsonOptions);
        using var createResponse = await Client().SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive folder creation failed ({await ReadGoogleErrorAsync(createResponse, cancellationToken)}).");
        var created = await createResponse.Content.ReadFromJsonAsync<DriveFolderInfo>(JsonOptions, cancellationToken);
        return created is null || string.IsNullOrWhiteSpace(created.Id)
            ? throw new InvalidOperationException("Google Drive did not return the created folder.")
            : created;
    }

    private async Task<DriveFolderInfo?> TryGetFolderAsync(
        string accessToken,
        string folderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return null;
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}?fields=id,name,mimeType,trashed",
            accessToken);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive folder lookup failed ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var document = await response.Content.ReadFromJsonAsync<DriveFolderDocument>(JsonOptions, cancellationToken);
        return document is null ||
               document.Trashed ||
               !string.Equals(document.MimeType, FolderMimeType, StringComparison.Ordinal)
            ? null
            : new DriveFolderInfo { Id = document.Id, Name = document.Name };
    }

    private async Task<string?> ResolveFolderPathAsync(
        string accessToken,
        string rootFolderId,
        IReadOnlyList<string> segments,
        bool createMissing,
        CancellationToken cancellationToken)
    {
        if (createMissing)
        {
            var gate = folderPathLocks.GetOrAdd(rootFolderId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await ResolveFolderPathCoreAsync(accessToken, rootFolderId, segments, true, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        return await ResolveFolderPathCoreAsync(accessToken, rootFolderId, segments, false, cancellationToken);
    }

    private async Task<string?> ResolveFolderPathCoreAsync(
        string accessToken,
        string rootFolderId,
        IReadOnlyList<string> segments,
        bool createMissing,
        CancellationToken cancellationToken)
    {
        var parentId = rootFolderId;
        foreach (var segment in segments)
        {
            var existing = await FindChildAsync(accessToken, parentId, segment, true, cancellationToken);
            if (existing is not null)
            {
                parentId = existing.Id;
                continue;
            }
            if (!createMissing) return null;
            parentId = (await CreateChildFolderAsync(accessToken, parentId, segment, cancellationToken)).Id;
        }
        return parentId;
    }

    private async Task<DriveFolderInfo?> FindChildAsync(
        string accessToken,
        string parentId,
        string name,
        bool folder,
        CancellationToken cancellationToken)
    {
        var mimeClause = folder
            ? $"mimeType = '{FolderMimeType}'"
            : $"mimeType != '{FolderMimeType}'";
        var markerClause = $"appProperties has {{ key='{LogicalPathMarkerKey}' and value='{LogicalPathMarkerValue}' }}";
        var query = $"'{EscapeDriveQueryLiteral(parentId)}' in parents and name = '{EscapeDriveQueryLiteral(name)}' and {mimeClause} and {markerClause} and trashed = false";
        var listUrl = "https://www.googleapis.com/drive/v3/files?" + BuildQuery(new Dictionary<string, string>
        {
            ["spaces"] = "drive",
            ["pageSize"] = "10",
            ["orderBy"] = "modifiedTime desc",
            ["fields"] = "files(id,name)",
            ["q"] = query
        });
        using var request = CreateAuthorizedRequest(HttpMethod.Get, listUrl, accessToken);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive path lookup failed ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var list = await response.Content.ReadFromJsonAsync<DriveFileList>(JsonOptions, cancellationToken);
        return list?.Files.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Id));
    }

    private async Task<DriveFolderInfo> CreateChildFolderAsync(
        string accessToken,
        string parentId,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id,name", accessToken);
        request.Content = JsonContent.Create(new
        {
            name,
            mimeType = FolderMimeType,
            parents = new[] { parentId },
            appProperties = new Dictionary<string, string> { [LogicalPathMarkerKey] = LogicalPathMarkerValue }
        }, options: JsonOptions);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive child-folder creation failed ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var created = await response.Content.ReadFromJsonAsync<DriveFolderInfo>(JsonOptions, cancellationToken);
        return created is null || string.IsNullOrWhiteSpace(created.Id)
            ? throw new InvalidOperationException("Google Drive did not return the created child folder.")
            : created;
    }

    private async Task<string> UpdateFileAsync(
        string accessToken,
        string fileId,
        Stream content,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Patch,
            $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media&fields=id",
            accessToken);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive rejected the file update ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var updated = await response.Content.ReadFromJsonAsync<DriveFileIdentifier>(JsonOptions, cancellationToken);
        return string.IsNullOrWhiteSpace(updated?.Id) ? fileId : updated.Id;
    }

    private async Task<AttachmentFileHandle> OpenReadWithAccessTokenAsync(
        string accessToken,
        string fileId,
        CancellationToken cancellationToken)
    {
        var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media",
            accessToken);
        var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        request.Dispose();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException("Google Drive attachment file was not found.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadGoogleErrorAsync(response, cancellationToken);
            response.Dispose();
            throw new InvalidOperationException($"Google Drive rejected the attachment read ({message}).");
        }
        return new AttachmentFileHandle(await response.Content.ReadAsStreamAsync(cancellationToken), response);
    }

    private static string[] NormalizeStoragePath(string storagePath)
    {
        var segments = (storagePath ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Length > 180 || segment.IndexOf('\0') >= 0))
            throw new InvalidOperationException("Google Drive storage path is invalid.");
        return segments;
    }

    private static string EscapeDriveQueryLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private async Task<string> UploadFileAsync(
        string accessToken,
        string folderId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken,
        bool markLogicalPath = false)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException("Google Drive attachment folder is not configured.");

        var boundary = $"frevo_{Guid.NewGuid():N}";
        using var multipart = new MultipartContent("related", boundary);
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = fileName,
            ["parents"] = new[] { folderId }
        };
        if (markLogicalPath)
            metadata["appProperties"] = new Dictionary<string, string> { [LogicalPathMarkerKey] = LogicalPathMarkerValue };
        multipart.Add(new StringContent(
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8,
            "application/json"));
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id",
            accessToken);
        request.Content = multipart;
        using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive rejected the upload ({await ReadGoogleErrorAsync(response, cancellationToken)}).");
        var created = await response.Content.ReadFromJsonAsync<DriveFileIdentifier>(JsonOptions, cancellationToken);
        return string.IsNullOrWhiteSpace(created?.Id)
            ? throw new InvalidOperationException("Google Drive did not return the uploaded file ID.")
            : created.Id;
    }

    private async Task DeleteFileWithAccessTokenAsync(
        string accessToken,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateAuthorizedRequest(
                HttpMethod.Delete,
                $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}",
                accessToken);
            using var response = await Client().SendAsync(request, cancellationToken);
        }
        catch
        {
            // A failed health-probe cleanup must not hide the actual connection result.
        }
    }

    private async Task<string> GetAccessTokenAsync(
        string refreshToken,
        GoogleDriveOAuthClient oauthClient,
        CancellationToken cancellationToken)
    {
        var cacheKey = TokenCacheKey(refreshToken, oauthClient.ClientId);
        if (accessTokens.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
            return cached.AccessToken;

        var gate = tokenLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (accessTokens.TryGetValue(cacheKey, out cached) &&
                cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
                return cached.AccessToken;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = oauthClient.ClientId,
                ["client_secret"] = oauthClient.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            });
            using var response = await Client().PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Google Drive access expired or was revoked. Reconnect the Google account.");
            var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(JsonOptions, cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("Google Drive returned an invalid access token. Reconnect the Google account.");
            CacheAccessToken(refreshToken, oauthClient, token.AccessToken, token.ExpiresIn);
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private void CacheAccessToken(
        string refreshToken,
        GoogleDriveOAuthClient oauthClient,
        string accessToken,
        int expiresIn)
    {
        var cacheKey = TokenCacheKey(refreshToken, oauthClient.ClientId);
        accessTokens[cacheKey] = new CachedAccessToken(
            accessToken,
            DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
    }

    private static string TokenCacheKey(string refreshToken, string clientId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}\n{refreshToken}")));

    private string NormalizeAndValidatePortalOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.PathAndQuery.Trim('/')))
            throw new InvalidOperationException("The portal origin for Google Drive connection is invalid.");

        var normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var allowedOrigins = configuration.GetSection("GoogleDriveOAuth:AllowedPortalOrigins")
            .GetChildren()
            .Select(child => child.Value)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => NormalizeOriginWithoutPolicy(item!))
            .ToList();
        var configuredPortalOrigin = configuration["GoogleDriveOAuth:PortalOrigin"];
        if (!string.IsNullOrWhiteSpace(configuredPortalOrigin))
            allowedOrigins.Add(NormalizeOriginWithoutPolicy(configuredPortalOrigin));
        if (allowedOrigins.Count > 0 &&
            !allowedOrigins.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("This portal origin is not allowed to start Google Drive connection.");
        return normalized;
    }

    private static string NormalizeOriginWithoutPolicy(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("GoogleDriveOAuth allowed portal origin configuration is invalid.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string RequiredJsonString(JsonElement parent, string propertyName, int maximumLength)
    {
        var matches = parent.EnumerateObject()
            .Where(property => property.NameEquals(propertyName))
            .ToList();
        if (matches.Count != 1 || matches[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Google Web OAuth JSON is missing {propertyName}.");
        var value = matches[0].Value.GetString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new InvalidOperationException($"Google Web OAuth JSON contains an invalid {propertyName}.");
        return value;
    }

    private HttpClient Client() => httpClientFactory.CreateClient(nameof(GoogleDriveOAuthService));

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReadGoogleErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var fallback = $"HTTP {(int)response.StatusCode}";
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            using var json = JsonDocument.Parse(raw);
            if (!json.RootElement.TryGetProperty("error", out var error)) return fallback;
            var message = error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : error.TryGetProperty("message", out var nested)
                    ? nested.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(message) &&
                json.RootElement.TryGetProperty("error_description", out var description))
                message = description.GetString();
            if (string.IsNullOrWhiteSpace(message)) return fallback;
            return message.Length > 300 ? message[..300] : message;
        }
        catch
        {
            return fallback;
        }
    }

    private static AttachmentStorageHealthResult Unhealthy(string message) => new()
    {
        Healthy = false,
        Status = "Unhealthy",
        Message = message
    };

    private sealed record CachedAccessToken(string AccessToken, DateTime ExpiresAtUtc);

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; } = 3600;
    }

    private sealed class GoogleAccountInfo
    {
        [JsonPropertyName("sub")]
        public string Subject { get; set; } = "";
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class DriveFileList
    {
        public List<DriveFolderInfo> Files { get; set; } = [];
    }

    private sealed class DriveFolderDocument
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string MimeType { get; set; } = "";
        public bool Trashed { get; set; }
    }

    private sealed class DriveFileIdentifier
    {
        public string Id { get; set; } = "";
    }
}

public sealed record GoogleDriveAuthorizationRequest(long StorageServerId, string AuthorizationUrl);

public sealed class GoogleDriveOAuthState
{
    public long StorageServerId { get; set; }
    public int ActorUserId { get; set; }
    public string PortalOrigin { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string ExistingFolderId { get; set; } = "";
    public string OAuthClientId { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string Nonce { get; set; } = "";
    public DateTime IssuedAtUtc { get; set; }
}

public sealed class GoogleDriveAuthorizationResult
{
    public long StorageServerId { get; set; }
    public int ActorUserId { get; set; }
    public string PortalOrigin { get; set; } = "";
    public string CredentialJson { get; set; } = "";
    public GoogleDriveCredential Credential { get; set; } = new();
    public string FolderUrl { get; set; } = "";
}

public sealed class GoogleDriveCredential
{
    public string OAuthClientId { get; set; } = "";
    public string OAuthClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccountSubject { get; set; } = "";
    public string AccountEmail { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string FolderName { get; set; } = "";
    public DateTime ConnectedAtUtc { get; set; }
}

public sealed class GoogleDriveOAuthClient
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class DriveFolderInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
