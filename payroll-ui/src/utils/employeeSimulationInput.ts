export type EmployeeSimulationFields = { clientId?: number; employeeId?: number; payPeriod: string; payrollDays: string; presentDays: string; payableDays: string; annualCtc: string }

// Blank means use native saved input; explicit zero remains a what-if override.
export function employeeSimulationInput(fields: EmployeeSimulationFields) {
  if (!fields.clientId || !fields.employeeId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(fields.payPeriod)) return null
  const number = (text: string) => text.trim() === '' ? null : Number(text)
  const payrollDays = number(fields.payrollDays), presentDays = number(fields.presentDays), payableDays = number(fields.payableDays), annualCtc = number(fields.annualCtc)
  if ([payrollDays, presentDays, payableDays, annualCtc].some(value => value !== null && (!Number.isFinite(value) || value < 0))) return null
  if (payrollDays !== null && (!Number.isInteger(payrollDays) || payrollDays < 1 || payrollDays > 31)) return null
  if ([presentDays, payableDays].some(value => value !== null && value > (payrollDays ?? 31)) || annualCtc !== null && annualCtc > 1000000000) return null
  return { mode: 'Employee' as const, clientId: fields.clientId, employeeId: fields.employeeId, payPeriod: fields.payPeriod, payrollDays, presentDays, payableDays, annualCtc, amountOverrides: {} }
}
