namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string PayableOnlyRolloutSetting = "Payroll:PayableOnlyAttendanceEnabled";

    // Schema readiness alone does not establish that every reader/worker sharing
    // this database understands unknown actual presence. The operator enables
    // this only after the compatible-reader rollout and reviewed migration.
    internal static bool PayableOnlyRolloutEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration[PayableOnlyRolloutSetting], out var enabled) && enabled;

    internal static bool PayableOnlyRolloutAvailable(IConfiguration configuration, bool schemaReady) =>
        PayableOnlyRolloutEnabled(configuration) && schemaReady;

    internal static string? PayableOnlyRolloutError(IConfiguration configuration, bool schemaReady)
    {
        if (!PayableOnlyRolloutEnabled(configuration))
            return "Payable-only attendance is disabled until all payroll readers and workers sharing this database support unknown actual presence. Complete the reviewed compatible-reader rollout and nullable-presence migration before the operator explicitly enables this mode. Preview and Apply do not enable it.";
        return schemaReady ? null
            : "Payable-only attendance requires the reviewed nullable-presence schema for monthly attendance and payroll employees. Complete that migration after backup, writer-pause and compatible-reader checks; preview and Apply do not change the schema.";
    }
}
