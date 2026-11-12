namespace Payroll.API.Services;

// Per-process, bounded retry protection for authenticated attendance password
// confirmation. Successful verification removes its reservation immediately.
internal sealed class BrowserAttendancePasswordThrottle
{
    internal const int AttemptLimit = 5;
    internal const int MaximumUsers = 4096;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<int, (int Attempts, DateTimeOffset Until)> entries = [];

    internal bool TryBegin(int userId, DateTimeOffset now, out int retryAfterSeconds)
    {
        lock (gate)
        {
            retryAfterSeconds = 0;
            if (entries.TryGetValue(userId, out var entry) && entry.Until > now)
            {
                if (entry.Attempts >= AttemptLimit)
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((entry.Until - now).TotalSeconds));
                    return false;
                }
                entries[userId] = (entry.Attempts + 1, entry.Until);
                return true;
            }
            if (entries.Count >= MaximumUsers)
                foreach (var expired in entries.Where(item => item.Value.Until <= now).Select(item => item.Key).ToArray()) entries.Remove(expired);
            if (entries.Count >= MaximumUsers)
            {
                retryAfterSeconds = 60;
                return false;
            }
            entries[userId] = (1, now + Window);
            return true;
        }
    }

    internal void Succeeded(int userId) { lock (gate) entries.Remove(userId); }
    internal int Count { get { lock (gate) return entries.Count; } }
}
