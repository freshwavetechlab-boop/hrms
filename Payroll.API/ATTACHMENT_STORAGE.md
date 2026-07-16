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

`App_Data` also contains the ASP.NET Data Protection keys used to encrypt remote file-server credentials. The directory must remain persistent when the container is redeployed.

## Switching file servers

Use **Settings → Attachments → Storage Servers**:

1. Add and test the new server.
2. Enable read and write.
3. Mark it as the default write target.

New uploads use the new default. Each existing file keeps its original `storage_server_id`, so it continues to read from the old server. A server linked to existing files cannot be disabled for reading.

Supported server types:

- `LocalFileSystem`: private folder below `AttachmentStorage:DataRootPath`.
- `MountedFileSystem`: Docker volume, network share, or another mounted path.
- `HttpFileServer`: external HTTPS file service.

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
