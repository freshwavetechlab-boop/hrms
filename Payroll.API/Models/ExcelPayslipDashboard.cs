namespace Payroll.API.Models;

public sealed class ExcelPayslipDashboard
{
    public int ClientId { get; set; }
    public ExcelPayslipBatchSummary? SelectedBatch { get; set; }
    public ExcelPayslipDashboardTotals? Summary { get; set; }
    public IReadOnlyList<ExcelPayslipBatchSummary> History { get; set; } = [];
    public IReadOnlyList<ExcelPayslipDashboardTotals> Trend { get; set; } = [];
}

public sealed class ExcelPayslipDashboardTotals
{
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public int RowCount { get; set; }
    public decimal Earnings { get; set; }
    public decimal Deductions { get; set; }
    public decimal EmployerContributions { get; set; }
    public decimal NetPay { get; set; }
    public int ReviewCount { get; set; }
    public bool HasCalculationSource { get; set; }
}
