namespace Payroll.API.Models;

public class AttendanceShift
{
    public AttendanceShiftRules Rules { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public string RulesJson
    {
        get => System.Text.Json.JsonSerializer.Serialize(Rules);
        set => Rules = string.IsNullOrWhiteSpace(value) ? new() : System.Text.Json.JsonSerializer.Deserialize<AttendanceShiftRules>(value) ?? new();
    }
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ShiftCode { get; set; } = "";
    public string ShiftName { get; set; } = "";
    public string ShiftType { get; set; } = "Fixed";
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public bool IsOvernight { get; set; }
    public int GraceMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public decimal MinimumFullDayHours { get; set; } = 8;
    public decimal MinimumHalfDayHours { get; set; } = 4;
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
