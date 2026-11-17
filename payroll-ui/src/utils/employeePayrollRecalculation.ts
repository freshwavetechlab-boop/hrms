import type { PayRun, RunEmployee } from '../types/payroll'

export type EmployeeRecalculationTotals = { gross: number; deductions: number; net: number; presentDays: number | null; payableDays: number }
export type EmployeeRecalculationPreview = {
  payRunId: number; clientId: number; payPeriod: string; employeeId: number; employeeCode: string; employeeName: string;
  status: string; expectedFingerprint: string; canApply: boolean; blockers: string[];
  before: EmployeeRecalculationTotals | null; after: EmployeeRecalculationTotals | null;
  changes: Array<{ code: string; name: string; category: string; before: number; after: number }>
}

export function recalculationPreviewError(preview: EmployeeRecalculationPreview, run: Pick<PayRun, 'id' | 'clientId' | 'payPeriod' | 'status'>, employee: Pick<RunEmployee, 'employeeId'>): string {
  if (preview.payRunId !== run.id || preview.clientId !== run.clientId || preview.payPeriod !== run.payPeriod || preview.employeeId !== employee.employeeId) return 'Preview belongs to a different employee, client or month. Close and reopen the correct record.'
  if (run.status !== 'Draft' || preview.status !== 'Draft') return 'Recall this unpaid payroll to Draft before recalculating an employee.'
  if (!preview.canApply) return preview.blockers?.join(' ') || 'This employee cannot be recalculated. Review the payroll inputs.'
  if (!preview.expectedFingerprint || !preview.before || !preview.after) return 'A complete reviewed calculation is required before saving.'
  if ([preview.before, preview.after].some(totals => ![totals.gross, totals.deductions, totals.net, totals.payableDays].every(Number.isFinite) || totals.presentDays !== null && !Number.isFinite(totals.presentDays))) return 'Preview contains invalid amounts or attendance. Nothing was saved.'
  return ''
}
