using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    public sealed record ClientDocumentSummary(int ClientId, long RosterCount, long OfferCount);
    internal static bool CanReadRosterForSummary(AuthUser user) => RecruitmentPermissions.Has(user,
        "recruitment.candidate.manage", "recruitment.offer.manage", "recruitment.offer.issue");
    internal static bool CanReadNativeOffersForSummary(AuthUser user) => RecruitmentPermissions.Has(user,
        "recruitment.offer.manage", "recruitment.offer.issue", "recruitment.proposal.manage", "recruitment.proposal.approve");

    // Availability is a read: opening the client picker must not initialize the
    // optional roster tables, create templates or otherwise change client data.
    public async Task<List<ClientDocumentSummary>> ClientSummaryAsync(AuthUser user)
    {
        var canReadRoster = CanReadRosterForSummary(user);
        var canReadOffers = CanReadNativeOffersForSummary(user);
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !canReadRoster && !canReadOffers)
            throw new UnauthorizedAccessException();

        await using var db = Db();
        await db.OpenAsync();
        var clientIds = (await db.QueryAsync<int>("SELECT Id FROM clients WHERE Id>0"))
            .Where(id => SelectedClientAccess.Allows(user, id) && (!user.ClientId.HasValue || user.ClientId == id))
            .ToArray();
        if (clientIds.Length == 0) return [];

        var hasRoster = canReadRoster && await db.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM information_schema.tables
 WHERE table_schema=DATABASE() AND table_name='recruitment_document_roster')
""");
        var counts = new List<ClientLocationDocumentCount>();
        if (hasRoster)
        {
            var hasRosterLocation = await db.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM information_schema.columns
 WHERE table_schema=DATABASE() AND table_name='recruitment_document_roster' AND column_name='WorkLocationId')
""");
            // Older roster tables predate institute linking. Keep their rows
            // unlinked until the existing write path performs its migration.
            var rosterLocation = hasRosterLocation ? "r.WorkLocationId" : "NULL";
            counts.AddRange(await db.QueryAsync<ClientLocationDocumentCount>($"""
SELECT r.ClientId,{rosterLocation} WorkLocationId,COUNT(*) RosterCount,0 OfferCount
FROM recruitment_document_roster r
JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
WHERE r.ClientId IN @ClientIds
GROUP BY r.ClientId,{rosterLocation}
""", new { ClientIds = clientIds }));
        }

        if (canReadOffers)
            counts.AddRange(await db.QueryAsync<ClientLocationDocumentCount>("""
SELECT o.ClientId,NULLIF(p.BranchId,0) WorkLocationId,0 RosterCount,COUNT(*) OfferCount
FROM recruitment_offers o
JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId AND a.ClientId=o.ClientId
JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_open_positions p ON p.Id=a.PositionId AND p.ClientId=a.ClientId
WHERE o.ClientId IN @ClientIds
GROUP BY o.ClientId,NULLIF(p.BranchId,0)
""", new { ClientIds = clientIds }));

        var locations = new List<WorkLocation>();
        var employeeLocations = new Dictionary<int, int?>();
        if (RecruitmentAccessScope.IsRestricted(user))
        {
            locations = (await db.QueryAsync<WorkLocation>("SELECT Id,ClientId,IsActive FROM worklocations WHERE ClientId IN @ClientIds AND IsActive=TRUE", new { ClientIds = clientIds })).ToList();
            if (user.RecruitmentScopeMode.Equals("EmployeeLocation", StringComparison.OrdinalIgnoreCase) && user.EmployeeId is > 0)
                employeeLocations = (await db.QueryAsync<ClientLocationDocumentCount>("SELECT ClientId,NULLIF(WorkLocationId,0) WorkLocationId FROM employees WHERE Id=@EmployeeId AND ClientId IN @ClientIds AND IsActive=TRUE", new { user.EmployeeId, ClientIds = clientIds }))
                    .ToDictionary(row => row.ClientId, row => row.WorkLocationId);
        }
        return BuildClientSummary(user, clientIds, counts, locations, employeeLocations);
    }

    internal sealed class ClientLocationDocumentCount
    {
        public int ClientId { get; set; }
        public int? WorkLocationId { get; set; }
        public long RosterCount { get; set; }
        public long OfferCount { get; set; }
    }

    internal static List<ClientDocumentSummary> BuildClientSummary(AuthUser user, IEnumerable<int> clientIds,
        IEnumerable<ClientLocationDocumentCount> counts, IEnumerable<WorkLocation> locations,
        IReadOnlyDictionary<int, int?> employeeLocations)
    {
        var permittedClients = clientIds.Where(id => SelectedClientAccess.Allows(user, id)
            && (!user.ClientId.HasValue || user.ClientId == id)).Distinct().Order().ToArray();
        var allowedLocations = locations.Where(location => location.IsActive
            && RecruitmentAccessScope.AllowsKnownWorkLocation(user, location.ClientId, location.Id,
                employeeLocations.GetValueOrDefault(location.ClientId)))
            .Select(location => (location.ClientId, location.Id)).ToHashSet();
        var visibleCounts = counts.Where(row => !RecruitmentAccessScope.IsRestricted(user)
            || row.WorkLocationId is > 0 && allowedLocations.Contains((row.ClientId, row.WorkLocationId.Value)))
            .ToLookup(row => row.ClientId);
        return permittedClients.Select(id => new ClientDocumentSummary(id,
            CanReadRosterForSummary(user) ? visibleCounts[id].Sum(row => row.RosterCount) : 0,
            CanReadNativeOffersForSummary(user) ? visibleCounts[id].Sum(row => row.OfferCount) : 0)).ToList();
    }
}
