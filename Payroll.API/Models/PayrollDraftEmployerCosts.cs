namespace Payroll.API.Models;

public sealed record DraftEmployerCostRow(int EmployeeId,string ComponentCode,decimal Amount,string Reference,string Notes);
public sealed record DraftEmployerCostRequest(int ClientId,string PayPeriod,decimal ExpectedNetPay,decimal ExpectedPayrollCost,bool Confirmed,List<DraftEmployerCostRow> Rows);
public sealed record DraftEmployerCostResult(int PayRunId,int Inserted,int Unchanged,decimal EmployerCostAdded,decimal NetPay);
