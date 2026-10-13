using System.Text.Json;
using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public class AttendanceShiftRules
{
    public string HoursMethod { get; set; } = "Inherit";
    public string BreakMode { get; set; } = "Fixed";
    public TimeSpan? DayBoundary { get; set; }
    public decimal? MaximumHours { get; set; }
    public List<AttendanceShiftSegment> Segments { get; set; } = [];
}

// Minutes from the attendance start date allow split shifts across midnight.
public record AttendanceShiftSegment(int StartMinute, int EndMinute);

public class AttendancePolicyShiftConfig
{
    public string AttendanceMode { get; set; } = "Legacy";
    public List<int> AllowedShiftIds { get; set; } = [];
    [JsonIgnore]
    public string AllowedShiftIdsJson
    {
        get => JsonSerializer.Serialize(AllowedShiftIds);
        set => AllowedShiftIds = string.IsNullOrWhiteSpace(value) ? [] : JsonSerializer.Deserialize<List<int>>(value) ?? [];
    }
}

public class EmployeeShiftPolicy : AttendancePolicyShiftConfig
{
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public int? ShiftId { get; set; }
}

public class AttendanceShiftAssignment
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public int ShiftId { get; set; }
    public string ShiftName { get; set; } = "";
    public DateTime EffectiveFrom { get; set; }
    public DateTime EffectiveTo { get; set; }
    public string WeekDays { get; set; } = "0,1,2,3,4,5,6";
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool Applies(DateTime date) => IsActive && date.Date >= EffectiveFrom.Date && date.Date <= EffectiveTo.Date
        && WeekDays.Split(',').Contains(((int)date.DayOfWeek).ToString());
}

public class SaveShiftAssignmentsRequest : AttendanceShiftAssignment
{
    public List<int> EmployeeIds { get; set; } = [];
}
