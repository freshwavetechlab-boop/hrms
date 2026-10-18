using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class AttachmentUploadDirectoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "frevo-upload-directory-tests", Guid.NewGuid().ToString("N"));
    private const string ExistingKey = "client-20/form_submission/948/resume/2026/original.pdf";
    private static AttachmentStorageServer LocalServer => new()
    {
        ServerCode = "API_LOCAL", StorageType = "LocalFileSystem", BasePath = "App_Data\\attachments"
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingApplicationKey_PreviewsAndDownloadsOriginalFromMovedUploadRoot(bool preview)
    {
        var (storage, uploads, _) = CreateStorage();
        var bytes = Encoding.UTF8.GetBytes("%PDF-original resume bytes");
        var original = Path.Combine(uploads, ExistingKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await File.WriteAllBytesAsync(original, bytes);

        var attachment = new EntityAttachment
        {
            EntityType = "CANDIDATE", StorageKey = ExistingKey, FileSizeBytes = bytes.Length,
            OriginalFileName = "Candidate Resume.pdf", DetectedMimeType = "application/pdf"
        };
        var context = new DefaultHttpContext();
        using var output = new MemoryStream();
        context.Response.Body = output;
        await new AttachmentContentResult(storage, LocalServer, attachment, preview,
            _ => throw new InvalidOperationException("An available original must not use saved text.")).ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/pdf", context.Response.ContentType);
        Assert.Equal(bytes, output.ToArray());
        Assert.StartsWith(preview ? "inline;" : "attachment;", context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(ExistingKey, attachment.StorageKey);
        Assert.False(context.Response.Headers.ContainsKey("X-Attachment-Recovery"));
    }

    [Fact]
    public async Task NewUpload_KeepsRelativeKeyAndCanBeReadByAnotherApiInstance()
    {
        var (writer, uploads, settings) = CreateStorage();
        var bytes = Encoding.UTF8.GetBytes("%PDF-new uploaded resume");
        using var source = new MemoryStream(bytes);
        var key = await writer.WriteAsync(LocalServer, ExistingKey, source, CancellationToken.None);

        var reader = new AttachmentStorageService(null!, new Host { ContentRootPath = folder }, settings, null!);
        await using var file = await reader.OpenReadAsync(LocalServer, key, CancellationToken.None);
        using var actual = new MemoryStream();
        await file.Stream.CopyToAsync(actual);
        Assert.Equal(ExistingKey, key);
        Assert.Equal(bytes, actual.ToArray());
        Assert.True(File.Exists(Path.Combine(uploads, ExistingKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(Directory.Exists(Path.Combine(folder, "legacy", "attachments")));
        Assert.Empty(Directory.GetFiles(uploads, "*.uploading", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingFile_IsNeverOverwrittenByNormalUpload()
    {
        var (storage, _, _) = CreateStorage();
        using var first = new MemoryStream([1, 2, 3]);
        await storage.WriteAsync(LocalServer, ExistingKey, first, CancellationToken.None);
        using var second = new MemoryStream([9, 9]);
        await Assert.ThrowsAsync<IOException>(() => storage.WriteAsync(LocalServer, ExistingKey, second, CancellationToken.None));
        await using var actual = await storage.OpenReadAsync(LocalServer, ExistingKey, CancellationToken.None);
        Assert.Equal(1, actual.Stream.ReadByte());
        Assert.Equal(2, actual.Stream.ReadByte());
        Assert.Equal(3, actual.Stream.ReadByte());
        Assert.Equal(-1, actual.Stream.ReadByte());
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("client-20/../../outside.pdf")]
    [InlineData("..\\outside.pdf")]
    public async Task DedicatedRoot_RejectsKeysThatEscapeItsDirectory(string invalidKey)
    {
        var (storage, _, _) = CreateStorage();
        using var source = new MemoryStream([1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteAsync(LocalServer, invalidKey, source, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.OpenReadAsync(LocalServer, invalidKey, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteAsync(LocalServer, invalidKey, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(folder, "outside.pdf")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnsetUploadDirectory_PreservesExistingConfiguredRoot(string? uploadDirectory)
    {
        var (storage, _, _) = CreateStorage(uploadDirectory, useUploadOverride: false);
        Assert.Equal(Path.Combine(folder, "legacy", "attachments"), storage.ResolveRoot(LocalServer));
    }

    [Fact]
    public void CustomFilesystemServers_KeepTheirOwnRootAndDataRootRestriction()
    {
        var (storage, _, _) = CreateStorage();
        var other = new AttachmentStorageServer
        {
            ServerCode = "OTHER_LOCAL", StorageType = "LocalFileSystem", BasePath = Path.Combine(folder, "legacy", "other")
        };
        Assert.Equal(other.BasePath, storage.ResolveRoot(other));
        other.BasePath = Path.Combine(folder, "outside-data-root");
        Assert.Throws<InvalidOperationException>(() => storage.ResolveRoot(other));

        other.StorageType = "MountedFileSystem";
        Assert.Equal(other.BasePath, storage.ResolveRoot(other));
    }

    [Fact]
    public void ConfiguredDockerUploadPath_IsPortableOnWindows()
    {
        var (storage, _, _) = CreateStorage("/app/hrms/uploads");
        Assert.Equal(OperatingSystem.IsWindows() ? Path.Combine(folder, "hrms", "uploads") : "/app/hrms/uploads",
            storage.ResolveRoot(LocalServer));
    }

    private (AttachmentStorageService Storage, string Uploads, IConfiguration Settings) CreateStorage(
        string? uploadDirectory = null, bool useUploadOverride = true)
    {
        var uploads = Path.Combine(folder, "mounted", "uploads");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AttachmentStorage:RootPath"] = Path.Combine(folder, "legacy", "attachments"),
            ["AttachmentStorage:DataRootPath"] = Path.Combine(folder, "legacy"),
            ["AttachmentStorage:DataProtectionKeyPath"] = Path.Combine(folder, "legacy", "data-protection-keys"),
            ["UPLOAD_DIR"] = useUploadOverride ? uploadDirectory ?? uploads : uploadDirectory
        }).Build();
        return (new AttachmentStorageService(null!, new Host { ContentRootPath = folder }, settings, null!), uploads, settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private sealed class Host : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
