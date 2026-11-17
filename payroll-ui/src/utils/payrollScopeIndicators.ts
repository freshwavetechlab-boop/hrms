import type { Employee, PayRun } from '../types/payroll'

export type PayrollClientIndicator = { count: number; hasPayroll: boolean }

/** Counts the authorized active roster once per real ID, independently of saved payroll presence. */
export function payrollClientIndicators(employees: Employee[], runs: PayRun[]): Map<number, PayrollClientIndicator> {
  const result = new Map<number, PayrollClientIndicator>()
  const seen = new Map<number, Set<number>>()
  const client = (id: number) => {
    let indicator = result.get(id)
    if (!indicator) { indicator = { count: 0, hasPayroll: false }; result.set(id, indicator) }
    return indicator
  }
  for (const employee of employees) {
    if (!Number.isSafeInteger(employee.clientId) || employee.clientId <= 0) continue
    const indicator = client(employee.clientId)
    if (!employee.isActive || !Number.isSafeInteger(employee.id) || employee.id <= 0) continue
    let ids = seen.get(employee.clientId)
    if (!ids) { ids = new Set<number>(); seen.set(employee.clientId, ids) }
    if (!ids.has(employee.id)) { ids.add(employee.id); indicator.count++ }
  }
  for (const run of runs) {
    if (!Number.isSafeInteger(run.clientId) || run.clientId <= 0) continue
    const indicator = client(run.clientId)
    // Any saved cohort counts, including a historical month or another location;
    // merely queued/held empty requests do not imply any calculated payroll exists.
    if (Number.isFinite(run.employeeCount) && run.employeeCount > 0) indicator.hasPayroll = true
  }
  return result
}
