import type { SimulationResult } from '../services/payrollCalculationService'

export function calendarDaysForSimulation(payPeriod: string): number | null {
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(payPeriod)) return null
  const [year, month] = payPeriod.split('-').map(Number)
  return new Date(Date.UTC(year, month, 0)).getUTCDate()
}

/** Temporary scenario only. Known native divisors and saved paid credits win over calendar defaults. */
export function employeePresentScenario(baseline: SimulationResult | null, payPeriod: string, edits: Record<string, string>) {
  const native = (token: string) => baseline?.inputs.find(input => input.name === token)?.value ?? null
  const explicit = (token: string) => !edits[token]?.trim() ? null : Number(edits[token])
  const present = explicit('PRESENT_DAYS')
  const payroll = explicit('PAYROLL_DAYS') ?? native('PAYROLL_DAYS') ?? calendarDaysForSimulation(payPeriod)
  const savedPresent = native('PRESENT_DAYS'), savedPayable = native('PAYABLE_DAYS')
  // Preserve a known paid-credit or extra-duty delta. Never guess one from a payable-only source.
  const credit = savedPresent !== null && savedPayable !== null ? savedPayable - savedPresent : 0
  const payable = explicit('PAYABLE_DAYS') ?? (present !== null && !(savedPresent === null && savedPayable !== null) ? present + credit : null)
  return { payrollDays: present === null && !edits.PAYROLL_DAYS?.trim() ? null : payroll, presentDays: present, payableDays: payable,
    annualCtc: explicit('ANNUAL_CTC'), savedCredit: credit,
    calendarDefault: native('PAYROLL_DAYS') === null && !edits.PAYROLL_DAYS?.trim(),
    hypothetical: !!baseline && baseline.attendanceEvidence?.sourceKind === 'Missing native attendance',
  }
}
