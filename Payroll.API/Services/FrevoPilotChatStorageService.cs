using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class FrevoPilotChatStorageService
{
    private const int FormatVersion = 1;
    private const int MaximumMessages = 600;
    private const int MaximumAnswers = 300;
    private const int MaximumThreadBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft", "Ready", "Running", "Passed", "Failed", "Stopped", "Archived"
    };
    private static readonly Regex SensitiveAnswerKey = new(
        "(?:PASSWORD|PASSCODE|SECRET|TOKEN|API[_-]?KEY|WORKBOOK|FILE|CREDENTIAL|AUTHORIZATION|HRMS_ORG_ADMIN_USER)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveLine = new(
        @"(?:password|passcode|secret|api[\s_-]*key|access[\s_-]*token|bearer|authorization|workbook|file\s*path)\s*(?::|=|\bis\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KeyLikeValue = new(
        @"\b(?:AIza[0-9A-Za-z_-]{20,}|AQ\.[0-9A-Za-z_-]{20,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AttachmentRepository attachments;
    private readonly AttachmentStorageService storage;
    private readonly IDataProtector threadProtector;
    private readonly IDataProtector indexProtector;
    private readonly ILogger<FrevoPilotChatStorageService> logger;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> userLocks = new();

    public FrevoPilotChatStorageService(
        AttachmentRepository attachments,
        AttachmentStorageService storage,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<FrevoPilotChatStorageService> logger)
    {
        this.attachments = attachments;
        this.storage = storage;
        this.logger = logger;
        threadProtector = dataProtectionProvider.CreateProtector("Payroll.API.FrevoPilotChatFiles.v1");
        indexProtector = dataProtectionProvider.CreateProtector("Payroll.API.FrevoPilotChatIndexes.v1");
    }

    public async Task<FrevoPilotChatStorageStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var server = await GetWriteServerAsync();
        return server is null
            ? new FrevoPilotChatStorageStatus
            {
                Available = false,
                Message = "No active default attachment write mount is configured."
            }
            : new FrevoPilotChatStorageStatus
            {
                Available = true,
                Message = "Previous chats are encrypted and saved on the active attachment mount.",
                ActiveStorageName = server.ServerName,
                ActiveStorageType = server.StorageType
            };
    }

    public async Task<IReadOnlyList<FrevoPilotChatThreadSummary>> ListAsync(int userId, CancellationToken cancellationToken)
    {
        var sources = await ReadMergedIndexAsync(userId, cancellationToken);
        return sources.Values
            .Where(source => !source.Entry.IsDeleted)
            .OrderByDescending(source => source.Entry.UpdatedAtUtc)
            .Select(source => ToSummary(source.Entry))
            .ToList();
    }

    public async Task<FrevoPilotChatThread?> GetAsync(Guid threadId, int userId, CancellationToken cancellationToken)
    {
        var sources = await ReadAllIndexEntriesAsync(userId, cancellationToken);
        var candidates = sources
            .Where(source => source.Entry.ThreadId == threadId)
            .OrderByDescending(source => source.Entry.Revision)
            .ThenByDescending(source => source.Entry.UpdatedAtUtc)
            .ToList();
        if (candidates.Count == 0 || candidates[0].Entry.IsDeleted) return null;

        foreach (var candidate in candidates.Where(source => !source.Entry.IsDeleted))
        {
            try
            {
                var document = await ReadThreadAsync(candidate.Server, userId, threadId, cancellationToken);
                if (document is not null && document.Thread.ThreadId == threadId && document.OwnerUserId == userId)
                    return document.Thread;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "FrevoPilot chat {ThreadId} could not be read from storage server {StorageServerId}.", threadId, candidate.Server.Id);
            }
        }
        return null;
    }

    public async Task<FrevoPilotChatThread> SaveAsync(
        Guid? requestedThreadId,
        SaveFrevoPilotChatThreadRequest request,
        int userId,
        CancellationToken cancellationToken)
    {
        var gate = userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var server = await GetWriteServerAsync()
                         ?? throw new InvalidOperationException("No active default attachment write mount is configured.");
            var threadId = requestedThreadId is { } value && value != Guid.Empty ? value : Guid.NewGuid();
            var merged = await ReadMergedIndexAsync(userId, cancellationToken);
            merged.TryGetValue(threadId, out var existing);
            var now = DateTime.UtcNow;
            var thread = NormalizeThread(threadId, request, existing?.Entry, now);
            var envelope = new StoredThreadEnvelope
            {
                OwnerUserId = userId,
                Thread = thread
            };
            var threadBytes = Protect(threadProtector, envelope);
            if (threadBytes.Length > MaximumThreadBytes)
                throw new InvalidOperationException("This chat is too large to save as one thread. Start a new chat and continue there.");
            await EnsureStorageMarkerAsync(server, cancellationToken);
            await using (var stream = new MemoryStream(threadBytes, writable: false))
                await storage.UpsertPathAsync(server, ThreadPath(userId, threadId), stream, cancellationToken);

            var activeIndex = await ReadIndexAsync(server, userId, cancellationToken) ?? new StoredIndexEnvelope { OwnerUserId = userId };
            activeIndex.OwnerUserId = userId;
            activeIndex.Entries.RemoveAll(item => item.ThreadId == threadId);
            activeIndex.Entries.Add(ToIndexEntry(thread, false));
            await WriteIndexAsync(server, activeIndex, cancellationToken);
            return thread;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid threadId, int userId, CancellationToken cancellationToken)
    {
        var gate = userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var server = await GetWriteServerAsync()
                         ?? throw new InvalidOperationException("No active default attachment write mount is configured.");
            var merged = await ReadMergedIndexAsync(userId, cancellationToken);
            if (!merged.TryGetValue(threadId, out var current) || current.Entry.IsDeleted) return false;
            var now = DateTime.UtcNow;
            var tombstone = current.Entry with
            {
                IsDeleted = true,
                Revision = current.Entry.Revision + 1,
                UpdatedAtUtc = now
            };
            var activeIndex = await ReadIndexAsync(server, userId, cancellationToken) ?? new StoredIndexEnvelope { OwnerUserId = userId };
            activeIndex.OwnerUserId = userId;
            activeIndex.Entries.RemoveAll(item => item.ThreadId == threadId);
            activeIndex.Entries.Add(tombstone);
            await WriteIndexAsync(server, activeIndex, cancellationToken);
            await storage.DeletePathAsync(server, ThreadPath(userId, threadId), cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AttachmentStorageServer?> GetWriteServerAsync() =>
        (await attachments.GetStorageServersAsync(true))
        .FirstOrDefault(server => server.IsDefaultWriteServer && server.IsActive && server.IsWriteEnabled);

    private async Task<List<IndexedSource>> ReadAllIndexEntriesAsync(int userId, CancellationToken cancellationToken)
    {
        var servers = (await attachments.GetStorageServersAsync(true))
            .Where(server => server.IsActive && server.IsReadEnabled)
            .OrderBy(server => server.Priority)
            .ToList();
        var result = new List<IndexedSource>();
        foreach (var server in servers)
        {
            try
            {
                var index = await ReadIndexAsync(server, userId, cancellationToken);
                if (index is null || index.OwnerUserId != userId) continue;
                result.AddRange(index.Entries.Select(entry => new IndexedSource(server, entry)));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "FrevoPilot chat index could not be read from storage server {StorageServerId}.", server.Id);
            }
        }
        return result;
    }

    private async Task<Dictionary<Guid, IndexedSource>> ReadMergedIndexAsync(int userId, CancellationToken cancellationToken) =>
        (await ReadAllIndexEntriesAsync(userId, cancellationToken))
        .GroupBy(source => source.Entry.ThreadId)
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(source => source.Entry.Revision)
                .ThenByDescending(source => source.Entry.UpdatedAtUtc)
                .First());

    private async Task<StoredIndexEnvelope?> ReadIndexAsync(AttachmentStorageServer server, int userId, CancellationToken cancellationToken)
    {
        await using var handle = await storage.TryOpenPathAsync(server, IndexPath(userId), cancellationToken);
        if (handle is null) return null;
        var protectedBytes = await ReadAllBytesAsync(handle.Stream, cancellationToken);
        return Unprotect<StoredIndexEnvelope>(indexProtector, protectedBytes);
    }

    private async Task<StoredThreadEnvelope?> ReadThreadAsync(AttachmentStorageServer server, int userId, Guid threadId, CancellationToken cancellationToken)
    {
        await using var handle = await storage.TryOpenPathAsync(server, ThreadPath(userId, threadId), cancellationToken);
        if (handle is null) return null;
        var protectedBytes = await ReadAllBytesAsync(handle.Stream, cancellationToken);
        return Unprotect<StoredThreadEnvelope>(threadProtector, protectedBytes);
    }

    private async Task WriteIndexAsync(AttachmentStorageServer server, StoredIndexEnvelope index, CancellationToken cancellationToken)
    {
        index.Version = FormatVersion;
        index.Entries = index.Entries
            .GroupBy(item => item.ThreadId)
            .Select(group => group.OrderByDescending(item => item.Revision).ThenByDescending(item => item.UpdatedAtUtc).First())
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(2_000)
            .ToList();
        var bytes = Protect(indexProtector, index);
        await using var stream = new MemoryStream(bytes, writable: false);
        await storage.UpsertPathAsync(server, IndexPath(index.OwnerUserId), stream, cancellationToken);
    }

    private async Task EnsureStorageMarkerAsync(AttachmentStorageServer server, CancellationToken cancellationToken)
    {
        await using var existing = await storage.TryOpenPathAsync(server, StorageMarkerPath, cancellationToken);
        if (existing is not null) return;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("FrevoPilot encrypted chat storage v1"), writable: false);
        await storage.UpsertPathAsync(server, StorageMarkerPath, stream, cancellationToken);
    }

    private static FrevoPilotChatThread NormalizeThread(
        Guid threadId,
        SaveFrevoPilotChatThreadRequest request,
        StoredIndexEntry? existing,
        DateTime now)
    {
        var messages = (request.Messages ?? [])
            .Where(message => message is not null && (message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) || message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)))
            .TakeLast(MaximumMessages)
            .Select((message, index) => new FrevoPilotChatMessage
            {
                Sequence = index + 1,
                Role = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                Text = SanitizeMessage(message.Text, 12_000),
                Meta = Trim(message.Meta, 300),
                OccurredAtUtc = NormalizeTimestamp(message.OccurredAtUtc, now)
            })
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .ToList();
        var answers = (request.Answers ?? [])
            .Where(answer => answer is not null)
            .Select(answer => new FrevoPilotChatAnswer
            {
                FieldKey = Trim(answer.FieldKey, 100).ToUpperInvariant(),
                Value = SanitizeMessage(answer.Value, 60_000),
                IsConfirmed = answer.IsConfirmed
            })
            .Where(answer => Regex.IsMatch(answer.FieldKey, "^[A-Z][A-Z0-9_.-]{0,99}$") && !SensitiveAnswerKey.IsMatch(answer.FieldKey) && !string.IsNullOrWhiteSpace(answer.Value))
            .GroupBy(answer => answer.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(MaximumAnswers)
            .ToList();
        var selected = (request.SelectedJourneyIds ?? []).Select(value => Trim(value, 80)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var confirmed = (request.ConfirmedFieldIds ?? []).Select(value => Trim(value, 100).ToUpperInvariant()).Where(value => !SensitiveAnswerKey.IsMatch(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumAnswers).ToList();
        var title = Trim(request.Title, 160);
        if (string.IsNullOrWhiteSpace(title))
            title = messages.FirstOrDefault(message => message.Role == "user")?.Text ?? "New FrevoPilot chat";
        title = SingleLine(title, 160);
        return new FrevoPilotChatThread
        {
            ThreadId = threadId,
            Title = title,
            ClientCode = SingleLine(request.ClientCode, 60),
            ClientName = SingleLine(request.ClientName, 180),
            JourneyId = SingleLine(request.JourneyId, 80),
            Status = AllowedStatuses.Contains(request.Status ?? string.Empty) ? AllowedStatuses.First(value => value.Equals(request.Status, StringComparison.OrdinalIgnoreCase)) : "Draft",
            RunId = SingleLine(request.RunId, 120),
            MessageCount = messages.Count,
            Revision = Math.Max(0, existing?.Revision ?? 0) + 1,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            SelectedJourneyIds = selected,
            ConfirmedFieldIds = confirmed,
            Messages = messages,
            Answers = answers
        };
    }

    private static string SanitizeMessage(string? value, int maximumLength)
    {
        var text = KeyLikeValue.Replace(value ?? string.Empty, "[credential removed]");
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => SensitiveLine.IsMatch(line)
                ? "[sensitive line omitted]"
                : line);
        return Trim(string.Join('\n', lines), maximumLength);
    }

    private static DateTime NormalizeTimestamp(DateTime value, DateTime fallback) =>
        value == default || value > fallback.AddMinutes(5) || value < new DateTime(2020, 1, 1) ? fallback : value.ToUniversalTime();

    private static string SingleLine(string? value, int maximumLength) =>
        Trim(Regex.Replace(value ?? string.Empty, "\\s+", " "), maximumLength);

    private static string Trim(string? value, int maximumLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }

    private static byte[] Protect<T>(IDataProtector protector, T value) =>
        protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static T Unprotect<T>(IDataProtector protector, byte[] protectedBytes)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(protector.Unprotect(protectedBytes), JsonOptions)
                   ?? throw new InvalidDataException("FrevoPilot chat file is empty.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new InvalidDataException("FrevoPilot chat file could not be authenticated or decoded.", exception);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumThreadBytes) throw new InvalidDataException("FrevoPilot chat file exceeds the supported size.");
        return buffer.ToArray();
    }

    private static string IndexPath(int userId) => $"FrevoPilot/Indexes/{userId}.frevoindex";
    private static string ThreadPath(int userId, Guid threadId) => $"FrevoPilot/Threads/{userId}/{threadId:N}.frevochat";
    public const string StorageMarkerPath = "FrevoPilot/.chat-store-v1";

    private static StoredIndexEntry ToIndexEntry(FrevoPilotChatThread thread, bool deleted) => new(
        thread.ThreadId,
        thread.Title,
        thread.ClientCode,
        thread.ClientName,
        thread.JourneyId,
        thread.Status,
        thread.RunId,
        thread.MessageCount,
        thread.Revision,
        thread.CreatedAtUtc,
        thread.UpdatedAtUtc,
        deleted);

    private static FrevoPilotChatThreadSummary ToSummary(StoredIndexEntry entry) => new()
    {
        ThreadId = entry.ThreadId,
        Title = entry.Title,
        ClientCode = entry.ClientCode,
        ClientName = entry.ClientName,
        JourneyId = entry.JourneyId,
        Status = entry.Status,
        RunId = entry.RunId,
        MessageCount = entry.MessageCount,
        Revision = entry.Revision,
        CreatedAtUtc = entry.CreatedAtUtc,
        UpdatedAtUtc = entry.UpdatedAtUtc
    };

    private sealed class StoredIndexEnvelope
    {
        public int Version { get; set; } = FormatVersion;
        public int OwnerUserId { get; set; }
        public List<StoredIndexEntry> Entries { get; set; } = [];
    }

    private sealed class StoredThreadEnvelope
    {
        public int Version { get; set; } = FormatVersion;
        public int OwnerUserId { get; set; }
        public FrevoPilotChatThread Thread { get; set; } = new();
    }

    private sealed record StoredIndexEntry(
        Guid ThreadId,
        string Title,
        string ClientCode,
        string ClientName,
        string JourneyId,
        string Status,
        string RunId,
        int MessageCount,
        int Revision,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        bool IsDeleted);

    private sealed record IndexedSource(AttachmentStorageServer Server, StoredIndexEntry Entry);
}
