using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    internal async Task<RecruitmentCandidate> CreateStandardRosterCandidateAsync(MySqlConnection db, MySqlTransaction tx, SaveRecruitmentCandidate request, AuthUser user)
    {
        if (request.ClientId <= 0 || !RecruitmentPermissions.Has(user, "recruitment.candidate.manage") || !SelectedClientAccess.Allows(user, request.ClientId)
            || user.ClientId.HasValue && user.ClientId != request.ClientId) throw new UnauthorizedAccessException();
        var email = NormalizeEmail(request.Email); var phone = NormalizePhone(request.Phone);
        if (request.FirstName.Trim().Length == 0 || email.Length == 0 && phone.Length == 0) throw new InvalidOperationException("First Name and Work Email or Mobile are required.");
        if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_candidates WHERE ProfileStatus<>'Archived' AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND RIGHT(NormalizedPhone,10)=RIGHT(@Phone,10))))", new { Email = email, Phone = phone }, tx))
            throw new InvalidOperationException("This contact already belongs to a profile. Use its Profile Code in Update Corrected Version.");
        var next = await db.ExecuteScalarAsync<int>("INSERT INTO recruitment_number_sequences(ClientId,SeriesCode,LastNumber) VALUES(@ClientId,'CAN',0) ON DUPLICATE KEY UPDATE LastNumber=LastNumber; UPDATE recruitment_number_sequences SET LastNumber=LAST_INSERT_ID(LastNumber+1) WHERE ClientId=@ClientId AND SeriesCode='CAN'; SELECT LAST_INSERT_ID();", request, tx);
        var clientCode = await db.ExecuteScalarAsync<string>("SELECT COALESCE(NULLIF(Code,''),CONCAT('C',Id)) FROM clients WHERE Id=@ClientId", request, tx) ?? $"C{request.ClientId}";
        var code = $"CAN-{clientCode}-{next:D6}";
        var retention = DateTime.UtcNow.AddMonths(Math.Max(1, configuration.GetValue<int?>("Recruitment:GlobalTalentPoolRetentionMonths") ?? 24));
        var id = await db.ExecuteScalarAsync<long>("""
INSERT INTO recruitment_candidates(CandidateCode,ClientId,FirstName,LastName,Email,NormalizedEmail,Phone,NormalizedPhone,CurrentTitle,PreferredLocationsJson,SourceType,ProfileStatus,ConsentStatus,RetentionUntil,CreatedByUserId)
VALUES(@Code,@ClientId,@FirstName,@LastName,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,@CurrentTitle,'[]','Direct','Active','Pending',@Retention,@UserId); SELECT LAST_INSERT_ID();
""", new { Code = code, request.ClientId, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), Email = request.Email.Trim(),
            NormalizedEmail = email, Phone = request.Phone.Trim(), NormalizedPhone = phone, request.CurrentTitle, Retention = retention, UserId = user.Id }, tx);
        await db.ExecuteAsync("INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES('RecruitmentCandidate',@Id,'Created',@Payload,@UserId)", new { Id = id, Payload = JsonSerializer.Serialize(request), UserId = user.Id }, tx);
        await WriteActivityAsync(db, request.ClientId, id, null, "RECRUITMENT", "CANDIDATE_CREATED", "Talent profile created", (request.FirstName + " " + request.LastName).Trim(), "RecruitmentCandidate", id.ToString(), user, transaction: tx);
        return new RecruitmentCandidate { Id = id, ClientId = request.ClientId, CandidateCode = code, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), Email = request.Email.Trim(), Phone = request.Phone.Trim() };
    }
}
