# Private attachment storage

The Payroll API stores attachment metadata in MySQL and file bytes in a private storage server. Files are never exposed through a static/public folder.

## First deployment

Run the database setup once:

```powershell
dotnet run -- --migrate
```

The setup creates the attachment configuration, storage, metadata, token, and audit tables. It also creates one default `API_LOCAL` write server.

For Coolify, mount persistent storage to:

```text
/app/App_Data
```

Recommended production environment variables:

```text
AttachmentStorage__DataRootPath=/app/App_Data
AttachmentStorage__RootPath=/app/App_Data/attachments
AttachmentStorage__GlobalMaximumFileSizeBytes=26214400
AttachmentStorage__PreviewTokenLifetimeSeconds=300
AttachmentStorage__DownloadTokenLifetimeSeconds=120
```

`App_Data` also contains the ASP.NET Data Protection keys used to encrypt remote file-server and Google Drive credentials. The directory must remain persistent when the container is redeployed.

## Switching file servers

Use **Security -> App Settings -> Storage Servers**:

1. Add and test the new server.
2. Enable read and write.
3. Mark it as the default write target.

New uploads use the new default. Each existing file keeps its original `storage_server_id`, so it continues to read from the old server. A server linked to existing files cannot be disabled for reading.

Supported server types:

- `LocalFileSystem`: private folder below `AttachmentStorage:DataRootPath`.
- `MountedFileSystem`: Docker volume, network share, or another mounted path.
- `HttpFileServer`: external HTTPS file service.
- `GoogleDrive`: a private personal Google Drive folder connected with one-click OAuth.

## One-click Google Drive connection

The Storage Servers page never asks an administrator to paste a Google token, client ID,
client secret, or folder link. Google requires one OAuth application, but its downloaded
Web OAuth JSON is uploaded once from the portal and stored in the existing encrypted
`credential_cipher_text` column—no extra configuration table is used.

Initial setup:

1. Enable the Google Drive API in a Google Cloud project.
2. Configure the OAuth consent screen and create an OAuth client of type **Web application**.
3. Copy the exact callback URL shown on the Storage Servers page into the client's
   authorized redirect URIs. For local development it is normally:

```text
http://localhost:5062/api/public/attachment-storage-servers/google/callback
```

Use the public HTTPS API URL instead of localhost in production. Google allows plain HTTP
for localhost testing; a LAN/private-IP callback should use HTTPS.

4. Download that Web OAuth client's `client_secret.json` and upload it on the Storage
   Servers page. The API accepts only a small, valid Web-client JSON whose redirect URI
   exactly matches the displayed callback URL.
5. Click **Connect Google Drive**, choose an account in Google's popup, and approve access.
   The API creates or reuses a private `Frevo HRMS Attachments` folder. A successful
   connection becomes the active write target. Existing Local, Mounted, and HTTP
   locations remain readable for files already linked to them.

The older environment configuration remains supported as a compatibility fallback:

```text
GoogleDriveOAuth__ClientId=your-google-web-client-id
GoogleDriveOAuth__ClientSecret=your-google-web-client-secret
```

Optional production hardening/overrides:

```text
GoogleDriveOAuth__RedirectUri=https://api.example.com/api/public/attachment-storage-servers/google/callback
GoogleDriveOAuth__AllowedPortalOrigins__0=https://hrms.example.com
GoogleDriveOAuth__FolderName=Frevo HRMS Attachments
```

The integration requests the limited `drive.file` scope. OAuth client credentials, the
refresh token, and connected-folder metadata are encrypted with the existing ASP.NET
Data Protection keys in `credential_cipher_text`. Short-lived access tokens are refreshed
automatically and are never returned to the browser. Rotating the secret for the same
OAuth client ID retains the connection. Changing the client ID or disconnecting is
blocked while attachments still depend on that Drive location.

For uninterrupted offline access, do not leave an External OAuth application in
**Testing** status: refresh tokens for non-basic scopes expire after seven days while the
app remains in Testing.

The API does not scan or concatenate folder contents. `entity_attachments` remains the
single attachment catalogue, so changing the write target does not display the same
document twice. Google Drive file IDs are stored as each new attachment's storage key.

## HTTP file-server contract

When `HttpFileServer` is selected, the Payroll API calls:

```text
GET    {serviceUrl}/health
PUT    {serviceUrl}/files/{storageKey}
GET    {serviceUrl}/files/{storageKey}
DELETE {serviceUrl}/files/{storageKey}
```

If an API token is configured, it is sent as:

```text
Authorization: Bearer {token}
```

The token is encrypted in the Payroll database. The remote file server should accept calls only from the Payroll API/network, enforce HTTPS, validate the bearer token, and must not expose its storage directory through a public web server.

## Secure reads

Normal content reads require the logged-in API token. Browser preview/download uses a short-lived random access ticket:

- only the SHA-256 hash of the ticket is stored;
- tickets expire quickly and have a use limit;
- every upload, preview, download, delete, verify, and reject action is audited;
- responses use private/no-store headers and do not reveal the physical storage path.
