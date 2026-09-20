using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

// Shared private Egress volume; reuse existing attachment containment/read/delete primitives.
public sealed class InternalInterviewMediaStore(IConfiguration configuration, AttachmentStorageService storage)
{
    private InternalInterviewOptions Options => configuration.GetSection("InternalInterviews").Get<InternalInterviewOptions>() ?? new();
    public long MaxBytes => Math.Clamp(Options.MaxRecordingBytes, 1024 * 1024, 4L * 1024 * 1024 * 1024);
    public void ValidateRoot()
    {
        Require(Path.IsPathFullyQualified(Options.RecordingDirectory), "Configure the API's private shared recording directory.", 503);
        var directory = new DirectoryInfo(Options.RecordingDirectory);
        Require(directory.Exists && directory.Parent is not null, "The private recording volume is unavailable; recording cannot start.", 503);
    }
    private string SafeKey(string key)
    {
        ValidateRoot();
        Require(key.EndsWith(".mp4", StringComparison.Ordinal) && Guid.TryParseExact(key[..^4], "N", out _), "Invalid recording identifier.");
        var file = new FileInfo(Path.Combine(Options.RecordingDirectory, key));
        Require(file.LinkTarget is null, "Recording symlinks are not permitted.", 403);
        return key;
    }
    // This is an explicitly configured private shared volume, not the API_LOCAL App_Data directory.
    private AttachmentStorageServer Server => new() { StorageType = "MountedFileSystem", BasePath = Options.RecordingDirectory };
    public long GetSize(string key) => new FileInfo(Path.Combine(Options.RecordingDirectory, SafeKey(key))).Length;
    public Task<AttachmentFileHandle> OpenAsync(string key, CancellationToken ct) => storage.OpenReadAsync(Server, SafeKey(key), ct);
    public Task DeleteAsync(string key, CancellationToken ct) => storage.DeletePathAsync(Server, SafeKey(key), ct);
}
