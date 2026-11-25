using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed record SalaryTemplatePayslipField(int SourceColumn, string Label, string Code, IReadOnlyList<string> Replacements);
public sealed record SalaryTemplatePayslipDependency(long TemplateId, string Name, string ProfileHash, bool HasApprovedPayroll, bool CanRemoveField,
    IReadOnlyList<SalaryTemplatePayslipField> Fields)
{
    public IReadOnlyList<string> Codes => Fields.Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

public sealed partial class PayslipDesignRepository
{
    internal async Task<IReadOnlyList<SalaryTemplatePayslipDependency>> ListSalaryTemplatePayslipDependenciesAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, long templateId)
    {
        var templates = await db.QueryAsync<PayslipDesignTemplate>("""
            SELECT p.* FROM paysliptemplates p
            JOIN modulesettings m ON m.client_id=p.ClientId AND m.ModuleCode=CONCAT('payslip.design:',p.Id) AND m.IsEnabled=TRUE
            WHERE p.ClientId=@clientId AND p.Theme=@theme ORDER BY p.Name,p.Id
            """, new { clientId, theme = NativePayslipDesignService.Theme }, tx);
        var profiles = new List<(PayslipDesignTemplate Template, PayslipDesignProfile Design)>();
        foreach (var template in templates)
        {
            var profile = await ReadProfileAsync(db, clientId, template.Id, tx);
            if (profile?.SourceKind != CorePayrollPayslipDesign.SourceKind) continue;
            NativePayslipDesignService.ValidateProfile(profile, template);
            profiles.Add((template, profile));
        }
        if (profiles.Count == 0) return [];

        using var setup = JsonDocument.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx));
        var structures = ClientSalaryStructures(setup.RootElement, clientId).ToArray();
        var key = templateId.ToString(CultureInfo.InvariantCulture);
        var removed = ConfiguredSalaryCodes(setup.RootElement, structures.Where(x => ClientSalaryText(x, "id") == key));
        var other = ConfiguredSalaryCodes(setup.RootElement, structures.Where(x => ClientSalaryText(x, "id") != key));
        if (!profiles.Any(x => LostMappedFields(x.Design.Fields, removed, other).Count != 0)) return [];
        // Approved adjustments, saved payroll and statutory outputs can independently supply a mapped code.
        var retained = await WithClientPayrollOutputsAsync(db, tx, clientId, setup.RootElement, other);
        var blockers = new List<SalaryTemplatePayslipDependency>();
        foreach (var (template, profile) in profiles)
        {
            var lost = LostMappedFields(profile.Fields, removed, retained);
            if (lost.Count == 0) continue;
            var hasApprovedPayroll = template.Active && await HasApprovedDesignPayrollAsync(db, tx, clientId, profile, false);
            var fields = lost.Select(field =>
            {
                var monthly = NativePayslipMonthlyComponentInformation.TryCode(field.InfoToken, out var monthlyCode);
                var replacements = monthly
                    ? NativePayslipMonthlyComponentInformation.CatalogueTokens(retained, configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag))
                        .Where(token => NativePayslipMonthlyComponentInformation.TryCode(token, out _) && !profile.Fields.Any(f => f.InfoToken == token))
                        .Select(token => token[NativePayslipMonthlyComponentInformation.Prefix.Length..])
                    : retained.Where(c => NativePayslipDesignService.LineKind(c.Category) == field.Kind && !profile.Fields.Any(f => f.Kind == field.Kind && f.NativeCode == c.Code))
                        .Select(c => c.Code);
                return new SalaryTemplatePayslipField(field.SourceColumn, field.Label, monthly ? monthlyCode : field.NativeCode,
                    replacements.Order(StringComparer.Ordinal).ToArray());
            }).ToArray();
            blockers.Add(new(template.Id, template.Name, profile.RequestHash, hasApprovedPayroll, profile.Fields.Count > 1, fields));
        }
        return blockers;
    }

    private static async Task<bool> HasApprovedDesignPayrollAsync(MySqlConnection db, MySqlTransaction tx, int clientId, PayslipDesignProfile profile, bool lockRuns)
    {
        var scope = profile.PayPeriods is null ? "" : " AND PayPeriod IN @Periods";
        var args = new { clientId, Periods = profile.PayPeriods };
        // For a write, lock draft headers too, so approval cannot slip between the check and design save.
        if (lockRuns)
        {
            var approved = (await db.QueryAsync<bool>("SELECT Status IN ('Approved','Partially Paid','Paid') FROM payruns WHERE ClientId=@clientId" +
                scope + " ORDER BY Id LIMIT 10001 FOR UPDATE", args, tx)).AsList();
            if (approved.Count > 10000) throw new InvalidOperationException("This design spans too many payroll runs to verify a mapping removal.");
            return approved.Contains(true);
        }
        return await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM payruns WHERE ClientId=@clientId" + scope +
            " AND Status IN ('Approved','Partially Paid','Paid'))", args, tx);
    }

    internal static IReadOnlyList<PayslipDesignField> LostMappedFields(IEnumerable<PayslipDesignField> fields,
        IEnumerable<PayslipDesignCode> removed, IEnumerable<PayslipDesignCode> retained)
    {
        var remaining = retained.Select(c => (c.Code, Kind: NativePayslipDesignService.LineKind(c.Category)))
            .Where(key => key.Kind != "").ToHashSet();
        var lost = removed.Select(c => (c.Code, Kind: NativePayslipDesignService.LineKind(c.Category)))
            .Where(key => key.Kind != "" && !remaining.Contains(key)).ToHashSet();
        // A monthly information token resolves by code alone and requires exactly one supported kind.
        var lostMonthly = lost.GroupBy(key => key.Code, StringComparer.Ordinal)
            .Where(group => group.Count() == 1 && !remaining.Any(key => key.Code == group.Key))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return fields.Where(field => lost.Contains((field.NativeCode, field.Kind)) ||
            (field.Kind == "info" && NativePayslipMonthlyComponentInformation.TryCode(field.InfoToken, out var code) && lostMonthly.Contains(code))).ToArray();
    }
}
