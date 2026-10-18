using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

// Recovery and scoring share this lock so an in-flight score cannot reattach an old resume.
// Callers use dedicated, non-pooled connections for the lifetime of the lock.
internal static class RecruitmentApplicationResumeGuard
{
    internal static string LockName(string database, long applicationId) =>
        RecruitmentAtsJobGuard.LockPrefix(database).Replace("frevo:ats:", "frevo:resume:") + applicationId;

    internal static Task<int?> AcquireAsync(MySqlConnection db, long applicationId, CancellationToken cancellationToken) =>
        db.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT GET_LOCK(@LockName,0)",
            new { LockName = LockName(db.Database, applicationId) }, commandTimeout: 3, cancellationToken: cancellationToken));

    internal static Task<int?> ReleaseAsync(MySqlConnection db, long applicationId) =>
        db.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT RELEASE_LOCK(@LockName)",
            new { LockName = LockName(db.Database, applicationId) }, commandTimeout: 3));
}
