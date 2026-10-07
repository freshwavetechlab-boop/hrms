namespace Payroll.API.Services;

/// <summary>An attendance mutation was rejected because its payroll period is closed.</summary>
public sealed class AttendancePeriodLockedException(string message) : InvalidOperationException(message);
