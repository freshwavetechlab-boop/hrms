using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

internal static class RecruitmentAtsJobGuard
{
    internal const string RecoverySql = @"UPDATE recruitment_ats_scoring_jobs SET Status='Retry',AvailableAt=UTC_TIMESTAMP(),
LastError='Recovered after an interrupted worker execution.',UpdatedAt=UTC_TIMESTAMP()
WHERE Status='Processing' AND StartedAt<DATE_SUB(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
 AND IS_FREE_LOCK(CONCAT(@LockPrefix,Id))=1";

    // Named locks are server-wide. Include a bounded database identity so unrelated
    // HRMS databases sharing a MySQL server never contend for equal numeric job IDs.
    internal static string LockPrefix(string database) =>
        "frevo:ats:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.ToLowerInvariant())))[..16] + ":";

    internal static string LockName(string database, long jobId) =>
        LockPrefix(database) + jobId.ToString(CultureInfo.InvariantCulture);

    internal static async Task<bool> WithLockAsync(
        Func<CancellationToken, Task<bool>> tryAcquire,
        Func<Task> release,
        Func<Task<bool>> work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await tryAcquire(cancellationToken)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await work();
        }
        finally { await release(); }
    }

    // Mirror the existing AI pool's tenant-first/primary/priority ordering without
    // decrypting credentials or changing configuration. Any eligible local fallback
    // also requires background work; CPU generation must not hold the batch HTTP call.
    internal static bool RequiresBackgroundScoring(IEnumerable<RecruitmentAiScoringSettings> models, int clientId, bool autoSwitch)
    {
        var eligible = models.Where(model => model.IsActive && model.EnableAiScoring
                && (model.ClientId == clientId || model.ClientId == 0))
            .OrderBy(model => model.ClientId == clientId ? 0 : 1)
            .ThenByDescending(model => model.IsPrimary).ThenBy(model => model.Priority).ThenBy(model => model.Id);
        return (autoSwitch ? eligible : eligible.Take(1)).Any(model => LocalLlmProtocol.IsLocal(model.ProviderCode));
    }
}
