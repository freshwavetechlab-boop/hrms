using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public static partial class PayrollDataTableStore
{
    private sealed record DesignProfileOwner(int ClientId, string ModuleCode);

    private static async Task SaveLegacyPayslipTemplatesInTransactionAsync(MySqlConnection connection,
        MySqlTransaction transaction, IReadOnlyCollection<PayslipTemplateDto> submitted, int? selectedClientId)
    {
        // Current DB ownership, not a stale settings payload, defines the atomic
        // designer's reserved rows. Lock before reading any profile identities.
        var current = (await connection.QueryAsync<PayslipTemplateRow>(
            "SELECT * FROM paysliptemplates ORDER BY Id FOR UPDATE", transaction: transaction)).ToDictionary(row => row.Id);
        var owners = new Dictionary<long, HashSet<int>>();
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='modulesettings'", transaction: transaction) > 0)
        {
            var profiles = await connection.QueryAsync<DesignProfileOwner>(
                "SELECT client_id ClientId,ModuleCode FROM modulesettings WHERE ModuleCode LIKE 'payslip.design:%'", transaction: transaction);
            foreach (var profile in profiles)
            {
                const string prefix = "payslip.design:";
                if (!profile.ModuleCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !long.TryParse(profile.ModuleCode.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                    || profile.ModuleCode[prefix.Length..] != id.ToString(CultureInfo.InvariantCulture)) continue;
                if (!owners.TryGetValue(id, out var clients)) owners[id] = clients = [];
                clients.Add(profile.ClientId);
            }
        }
        bool Owned(PayslipTemplateRow row) => selectedClientId is null
            || SettingsRepository.IsPureClientRow(JsonSerializer.SerializeToNode(ToDto(row), JsonOptions)!.AsObject(), selectedClientId.Value);
        var reserved = new HashSet<long>();
        foreach (var row in current.Values)
        {
            if (owners.TryGetValue(row.Id, out var clients))
            {
                if (clients.Count != 1 || !clients.Contains(row.ClientId) || row.ClientId <= 0)
                {
                    if (Owned(row)) throw new InvalidOperationException("A payslip design profile has conflicting client ownership. No template changes were saved.");
                    continue;
                }
                reserved.Add(row.Id);
            }
            if (string.Equals(row.Theme, NativePayslipDesignService.Theme, StringComparison.OrdinalIgnoreCase)) reserved.Add(row.Id);
        }
        var ordinary = new List<PayslipTemplateDto>();
        foreach (var row in submitted.Where(row => row.Id > 0))
        {
            if (reserved.Contains(row.Id))
            {
                var owner = current[row.Id];
                if (!SettingsRepository.IsPureClientRow(JsonSerializer.SerializeToNode(row, JsonOptions)!.AsObject(), owner.ClientId))
                    throw new InvalidOperationException("This reviewed payslip design belongs to another client. Its ID cannot be reused.");
                // An unrelated or stale whole-setup save must preserve the
                // committed design, including flags, active state and footer.
                continue;
            }
            if (owners.ContainsKey(row.Id)) throw new InvalidOperationException("This payslip design profile ID cannot be reused by legacy settings.");
            if (string.Equals(row.Theme, NativePayslipDesignService.Theme, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Create or convert an Excel Payslip design through its reviewed template editor.");
            if (selectedClientId is int scope && (RefId(row.ClientId) != scope || current.TryGetValue(row.Id, out var existing) && !Owned(existing)))
                throw new InvalidOperationException("A payslip template belongs to another client or is shared.");
            ordinary.Add(row);
        }
        var ids = ordinary.Select(row => row.Id).ToHashSet();
        var removed = current.Values.Where(row => Owned(row) && !reserved.Contains(row.Id) && !ids.Contains(row.Id)).Select(row => row.Id).ToArray();
        // Explicit current IDs cannot catch an atomic design inserted after the
        // read, even with a database isolation level that does not gap-lock.
        if (removed.Length > 0) await connection.ExecuteAsync("DELETE FROM paysliptemplates WHERE Id IN @Ids", new { Ids = removed }, transaction);
        foreach (var row in ordinary.GroupBy(row => row.Id).Select(group => group.Last()))
        {
            var values = new { row.Id, ClientId = RefId(row.ClientId), ClientRef = row.ClientId ?? "", row.Name, row.Theme, row.ShowLogo, row.ShowClient, row.ShowYtd, row.ShowBank, row.Note, row.Active };
            if (current.ContainsKey(row.Id))
                await connection.ExecuteAsync(@"UPDATE paysliptemplates SET ClientId=@ClientId,ClientRef=@ClientRef,Name=@Name,Theme=@Theme,ShowLogo=@ShowLogo,ShowClient=@ShowClient,ShowYtd=@ShowYtd,ShowBank=@ShowBank,Note=@Note,Active=@Active WHERE Id=@Id", values, transaction);
            else
                // A concurrent allocation of this global primary key must fail,
                // never overwrite the atomic design (or another client).
                await connection.ExecuteAsync(@"INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active)
VALUES(@Id,@ClientId,@ClientRef,@Name,@Theme,@ShowLogo,@ShowClient,@ShowYtd,@ShowBank,@Note,@Active)", values, transaction);
        }
    }
}
