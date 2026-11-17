using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static readonly string[] ImportLocationNameKeys = ["jobLocation", "workLocation", "workLocationName", "instituteName", "institutionName"];
    private static readonly string[] ImportLocationNameHeaders = ["location", "joblocation", "worklocation", "worklocationname", "institute", "institutename", "institution", "institutionname", "worklocationinstitute", "worklocationinstitution", "officelocation", "placeofposting", "postinglocation", "nameofinstitute", "nameofinstitution", "nameoftheinstitute", "nameoftheinstitution"];

    internal static string ImportLocationKey(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();

    internal static bool CanCreateImportLocation(AuthUser user, int clientId) => SelectedClientAccess.Allows(user, clientId)
        && (!user.ClientId.HasValue || user.ClientId == clientId)
        && !RecruitmentAccessScope.IsRestricted(user)
        && (user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase)
            || user.Permissions.Contains("client.settings.manage", StringComparer.OrdinalIgnoreCase));

    private static bool IsImportLocationInput(DocumentInput field, string[] keys, string[] aliases) =>
        keys.Contains(field.Key, StringComparer.OrdinalIgnoreCase) || aliases.Contains(HeaderKey(field.Key)) || aliases.Contains(HeaderKey(field.Label))
        || field.SourceHeaders.Any(header => aliases.Contains(HeaderKey(header)));

    internal static WorkLocation ReadImportLocation(RecruitmentDocumentLayout layout, IReadOnlyDictionary<string, string> raw,
        int clientId, List<string> issues, IReadOnlyList<string>? sourceHeaders = null, IReadOnlyList<string>? sourceCells = null)
    {
        string Read(string[] keys, string[] aliases, string label)
        {
            var fields = layout.Inputs.Where(field => IsImportLocationInput(field, keys, aliases)).ToArray();
            // A metadata-only Location column is useful even for layouts that do
            // not print a location. A configured-but-unmapped input stays unmapped.
            var source = fields.Length == 0 && sourceHeaders is not null && sourceCells is not null
                ? sourceHeaders.Select((header, index) => (header, index)).Where(column => aliases.Contains(HeaderKey(column.header)))
                    .Select(column => sourceCells.ElementAtOrDefault(column.index) ?? "")
                : fields.Select(field => field.EmptyValues.Contains(raw.GetValueOrDefault(field.Key, "").Trim(), StringComparer.OrdinalIgnoreCase)
                    ? "" : raw.GetValueOrDefault(field.Key, ""));
            var values = source.Select(value => value.Trim()).Where(value => value.Length > 0)
                .GroupBy(ImportLocationKey).Select(group => group.First()).ToArray();
            if (values.Length > 1) issues.Add($"More than one {label} is supplied for this row; review the source mapping or select a work location explicitly.");
            return values.FirstOrDefault() ?? "";
        }
        var location = new WorkLocation
        {
            ClientId = clientId,
            Name = Read(ImportLocationNameKeys, ImportLocationNameHeaders, "work location name"),
            Address = Read(["instituteAddress", "institutionAddress", "workLocationAddress"], ["instituteaddress", "institutionaddress", "worklocationaddress"], "work location address"),
            City = Read(["city", "workLocationCity", "instituteCity", "institutionCity"], ["worklocationcity", "institutecity", "institutioncity"], "work location city"),
            State = Read(["state", "workLocationState", "instituteState", "institutionState"], ["worklocationstate", "institutestate", "institutionstate"], "work location state"),
            PostalCode = Read(["workLocationPostalCode", "institutePostalCode", "institutionPostalCode", "workLocationPin"], ["worklocationpincode", "institutepincode", "institutionpincode"], "work location PIN code"),
            IsActive = true,
            IsPrimary = false
        };
        if (location.Name.Length > 0)
        {
            location.Name = Regex.Replace(location.Name.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
            var error = ValidateImportLocation(location);
            if (error.Length > 0) issues.Add(error);
        }
        return location;
    }

    private static string ValidateImportLocation(WorkLocation location)
    {
        foreach (var (value, maximum, label) in new[] {
            (location.Name, 200, "name"), (location.Address, 500, "address"),
            (location.City, 100, "city"), (location.State, 100, "state"), (location.PostalCode, 30, "PIN code") })
            if (value.EnumerateRunes().Count() > maximum || value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                return $"Work location {label} is invalid or exceeds {maximum} characters; review the source value.";
        if (location.Name.Length == 0) return "Work location name is required.";
        if (location.PostalCode.Length > 0 && !Regex.IsMatch(location.PostalCode, @"^[1-9][0-9]{5}$"))
            return "Enter a valid 6-digit work location PIN code.";
        return "";
    }

    internal static void ResolveImportLocation(RecruitmentRosterPreviewRow row, int clientId, AuthUser user,
        IReadOnlyList<WorkLocation> clientLocations, IReadOnlySet<int> permittedIds)
    {
        row.WorkLocationStatus = "Unassigned";
        if (!SelectedClientAccess.Allows(user, clientId) || user.ClientId.HasValue && user.ClientId != clientId)
        {
            row.ImportIssues.Add("The selected client is outside your permitted client scope.");
            return;
        }
        if (row.WorkLocationId.HasValue)
        {
            var selected = clientLocations.SingleOrDefault(location => location.ClientId == clientId && location.Id == row.WorkLocationId);
            if (selected is null || !selected.IsActive || !permittedIds.Contains(selected.Id))
            {
                row.ImportIssues.Add("The linked work location is inactive, unavailable or outside your permitted work locations.");
                return;
            }
            row.WorkLocationStatus = "Existing"; row.WorkLocation = selected.Name;
            row.ProposedWorkLocation = selected;
            return;
        }
        var proposed = row.ProposedWorkLocation;
        if (proposed is null || string.IsNullOrWhiteSpace(proposed.Name))
        {
            if (RecruitmentAccessScope.IsRestricted(user)) row.ImportIssues.Add("Select or supply an existing work location within your permitted locations.");
            return;
        }
        row.WorkLocation = proposed.Name;
        var validation = ValidateImportLocation(proposed);
        if (validation.Length > 0) { row.ImportIssues.Add(validation); return; }
        var matches = clientLocations.Where(location => location.ClientId == clientId && ImportLocationKey(location.Name) == ImportLocationKey(proposed.Name)).ToArray();
        if (matches.Length > 1)
        {
            row.ImportIssues.Add("Multiple work location masters match this name in the selected client; select the correct master explicitly.");
            return;
        }
        if (matches.Length == 1)
        {
            if (!matches[0].IsActive) { row.ImportIssues.Add("An inactive work location already uses this name; review the existing master before import."); return; }
            if (!permittedIds.Contains(matches[0].Id)) { row.ImportIssues.Add("This work location is outside your permitted locations."); return; }
            row.WorkLocationId = matches[0].Id; row.WorkLocationStatus = "Existing";
            row.ProposedWorkLocation = matches[0]; row.WorkLocation = matches[0].Name;
            return;
        }
        row.WorkLocationStatus = "New";
        if (!CanCreateImportLocation(user, clientId))
            row.ImportIssues.Add(RecruitmentAccessScope.IsRestricted(user)
                ? "New work locations cannot be created from this restricted location scope; select a permitted existing master."
                : "Creating this work location requires Work Location Master permission (settings.manage or client.settings.manage).");
    }

    internal static void MergeNewImportLocationDetails(IEnumerable<RecruitmentRosterPreviewRow> rows)
    {
        foreach (var group in rows.Where(row => row.WorkLocationStatus == "New" && row.ProposedWorkLocation is not null)
            .GroupBy(row => ImportLocationKey(row.ProposedWorkLocation!.Name)))
        {
            var source = group.ToArray();
            string Merge(Func<WorkLocation, string> select, string field)
            {
                var values = source.Select(row => select(row.ProposedWorkLocation!)).Where(value => value.Length > 0)
                    .GroupBy(ImportLocationKey).Select(values => values.First()).ToArray();
                if (values.Length > 1)
                    foreach (var row in source) row.ImportIssues.Add($"The same new work location has conflicting {field} values in this list; review its source details.");
                return values.FirstOrDefault() ?? "";
            }
            var address = Merge(location => location.Address, "address"); var city = Merge(location => location.City, "city");
            var state = Merge(location => location.State, "state"); var postalCode = Merge(location => location.PostalCode, "PIN code");
            foreach (var row in source)
            {
                row.ProposedWorkLocation!.Address = address; row.ProposedWorkLocation.City = city;
                row.ProposedWorkLocation.State = state; row.ProposedWorkLocation.PostalCode = postalCode;
            }
        }
    }

    private static async Task<bool> SaveImportLocationAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, AuthUser user, RecruitmentRosterPreviewRow row)
    {
        var clientName = await db.ExecuteScalarAsync<string?>("SELECT Name FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE", new { Id = clientId }, tx)
            ?? throw new InvalidOperationException("The selected client is no longer active.");
        // Current locking read also detects masters created after preview. The existing
        // roster-intake lock serializes independent uploads; this transaction owns creation and row linking together.
        var masters = (await db.QueryAsync<WorkLocation>("SELECT * FROM worklocations WHERE ClientId=@ClientId FOR UPDATE", new { ClientId = clientId }, tx)).ToList();
        var allowed = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId, tx);
        ResolveImportLocation(row, clientId, user, masters, allowed.Select(location => location.Id).ToHashSet());
        if (row.ImportIssues.Count > 0) throw new InvalidOperationException($"Source row {row.SourceRow}: {string.Join(" ", row.ImportIssues)}");
        if (row.WorkLocationStatus == "Unassigned") return false;
        var location = row.ProposedWorkLocation!;
        var created = row.WorkLocationStatus == "New";
        if (created)
        {
            if (!CanCreateImportLocation(user, clientId)) throw new UnauthorizedAccessException("Work Location Master permission is required.");
            var validation = ValidateImportLocation(location);
            if (validation.Length > 0) throw new InvalidOperationException(validation);
            location.Id = await db.ExecuteScalarAsync<int>("""
INSERT INTO worklocations(ClientId,ClientName,Name,Address,City,State,PostalCode,GSTIN,IsPrimary,IsActive)
VALUES(@ClientId,@ClientName,@Name,@Address,@City,@State,@PostalCode,'',FALSE,TRUE);SELECT LAST_INSERT_ID();
""", new { ClientId = clientId, ClientName = clientName, location.Name, location.Address, location.City, location.State, location.PostalCode }, tx);
            if (location.State.Length > 0 || location.City.Length > 0)
                await OrganizationRepository.EnsureLocationDropdownMastersAsync(db, location.State, location.City, tx);
        }
        row.WorkLocationId = location.Id; row.WorkLocation = location.Name; row.WorkLocationStatus = "Existing";
        ApplyLocationValues(row.Values, location);
        return created;
    }
}
