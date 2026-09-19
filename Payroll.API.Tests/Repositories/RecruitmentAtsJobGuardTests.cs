using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public class RecruitmentAtsJobGuardTests
{
    [Fact]
    public void LockNamesMatchRecoveryPrefixAndStayBelowMysqlLimit()
    {
        var database = new string('x', 120);
        var name = RecruitmentAtsJobGuard.LockName(database, long.MaxValue);
        Assert.StartsWith(RecruitmentAtsJobGuard.LockPrefix(database), name);
        Assert.EndsWith(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
        Assert.True(name.Length <= 64);
        Assert.NotEqual(name, RecruitmentAtsJobGuard.LockName(database + "_test", long.MaxValue));
        Assert.Equal(RecruitmentAtsJobGuard.LockName("Payroll", 1), RecruitmentAtsJobGuard.LockName("payroll", 1));
        Assert.Contains("IS_FREE_LOCK(CONCAT(@LockPrefix,Id))=1", RecruitmentAtsJobGuard.RecoverySql);
        Assert.Contains("Status='Processing'", RecruitmentAtsJobGuard.RecoverySql);
        Assert.Contains("INTERVAL 10 MINUTE", RecruitmentAtsJobGuard.RecoverySql);
    }

    [Fact]
    public async Task BusyJobNeverClaimsOrReleasesAnotherSessionsLock()
    {
        var ran = false;
        var released = false;
        var result = await RecruitmentAtsJobGuard.WithLockAsync(_ => Task.FromResult(false),
            () => { released = true; return Task.CompletedTask; },
            () => { ran = true; return Task.FromResult(true); }, CancellationToken.None);
        Assert.False(result);
        Assert.False(ran);
        Assert.False(released);
    }

    [Fact]
    public async Task LockStaysHeldUntilEntireScoringWorkFinishes()
    {
        var calls = new List<string>();
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = RecruitmentAtsJobGuard.WithLockAsync(_ => { calls.Add("acquire"); return Task.FromResult(true); },
            () => { calls.Add("release"); return Task.CompletedTask; },
            async () => { calls.Add("claim-score-save"); return await finish.Task; }, CancellationToken.None);
        Assert.Equal(new[] { "acquire", "claim-score-save" }, calls);
        Assert.False(operation.IsCompleted);
        finish.SetResult(true);
        Assert.True(await operation);
        Assert.Equal(new[] { "acquire", "claim-score-save", "release" }, calls);
    }

    [Fact]
    public async Task FailedScoringStillReleasesOwnedLock()
    {
        var released = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecruitmentAtsJobGuard.WithLockAsync(_ => Task.FromResult(true),
            () => { released = true; return Task.CompletedTask; },
            () => throw new InvalidOperationException("Synthetic failure"), CancellationToken.None));
        Assert.True(released);
    }

    [Fact]
    public async Task CancellationDuringNonCancelableLegacyScoringDoesNotReleaseLockEarly()
    {
        using var cancellation = new CancellationTokenSource();
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        var operation = RecruitmentAtsJobGuard.WithLockAsync(_ => { held = true; return Task.FromResult(true); },
            () => { held = false; return Task.CompletedTask; }, () => finish.Task, cancellation.Token);
        cancellation.Cancel();
        Assert.True(held);
        Assert.False(operation.IsCompleted);
        var duplicateRan = false;
        Assert.False(await RecruitmentAtsJobGuard.WithLockAsync(_ => Task.FromResult(!held), () => Task.CompletedTask,
            () => { duplicateRan = true; return Task.FromResult(true); }, CancellationToken.None));
        Assert.False(duplicateRan);
        finish.SetResult(true);
        Assert.True(await operation);
        Assert.False(held);
    }

    [Fact]
    public async Task CancellationAfterAcquisitionReleasesWithoutStartingWork()
    {
        using var cancellation = new CancellationTokenSource();
        var released = false;
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecruitmentAtsJobGuard.WithLockAsync(
            _ => { cancellation.Cancel(); return Task.FromResult(true); },
            () => { released = true; return Task.CompletedTask; },
            () => { ran = true; return Task.FromResult(true); }, cancellation.Token));
        Assert.True(released);
        Assert.False(ran);
    }

    [Fact]
    public async Task CancelledAdmissionDoesNotAcquireLock()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var acquired = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecruitmentAtsJobGuard.WithLockAsync(
            _ => { acquired = true; return Task.FromResult(true); },
            () => Task.CompletedTask, () => Task.FromResult(true), cancellation.Token));
        Assert.False(acquired);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void LocalPrimaryOrEligibleFallbackUsesBackgroundQueue(bool localPrimary, bool autoSwitch, bool expected)
    {
        var models = new[] { Model(1, 0, "Gemini", !localPrimary), Model(2, 0, "LocalOpenAICompatible", localPrimary) };
        Assert.Equal(expected, RecruitmentAtsJobGuard.RequiresBackgroundScoring(models, 20, autoSwitch));
    }

    [Fact]
    public void TenantProviderPrecedesGlobalAndUnrelatedOrDisabledLocalIsIgnored()
    {
        var models = new[] { Model(1, 0, "LocalOpenAICompatible", true), Model(2, 20, "Groq", false) };
        Assert.False(RecruitmentAtsJobGuard.RequiresBackgroundScoring(models, 20, false));
        Assert.True(RecruitmentAtsJobGuard.RequiresBackgroundScoring(models, 20, true));
        models[0].IsActive = false;
        Assert.False(RecruitmentAtsJobGuard.RequiresBackgroundScoring(models, 20, true));
        models[0].IsActive = true;
        models[0].ClientId = 99;
        Assert.False(RecruitmentAtsJobGuard.RequiresBackgroundScoring(models, 20, true));
    }

    [Fact]
    public void CloudOnlyAndEmptyPoolsKeepSynchronousEligibility()
    {
        Assert.False(RecruitmentAtsJobGuard.RequiresBackgroundScoring([], 20, true));
        Assert.False(RecruitmentAtsJobGuard.RequiresBackgroundScoring([Model(1, 0, "Gemini", true), Model(2, 0, "Groq", false)], 20, true));
    }

    private static RecruitmentAiScoringSettings Model(long id, int clientId, string provider, bool primary) =>
        new() { Id = id, ClientId = clientId, ProviderCode = provider, IsPrimary = primary, EnableAiScoring = true, IsActive = true };
}
