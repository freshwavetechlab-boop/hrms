import type { RunEmployee } from '../types/payroll'

// Display only: use the source attendance frozen in this saved calculation.
// Existing ordinary/daily attendance keeps the run's existing day basis.
export function savedPayrollDays(employee: RunEmployee, fallback: number, clientId: number, month: string): number {
  if (employee.presentDays !== null || !employee.detailsJson) return fallback
  try {
    const values: unknown = JSON.parse(employee.detailsJson)
    if (!Array.isArray(values)) return fallback
    const field = (value: unknown, name: string): unknown => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined
      const matches = Object.entries(value).filter(([key]) => key.toLowerCase() === name.toLowerCase())
      return matches.length === 1 ? matches[0][1] : undefined
    }
    const rows = values.filter(line => field(line, 'id') === 'ATTENDANCE_INPUT')
    if (rows.length !== 1) return fallback
    const line: unknown = rows[0], proof = field(line, 'sourceEvidence'), days = field(line, 'payrollDays')
    const paid = field(proof, 'payableDays'), version = field(proof, 'version'), extra = field(proof, 'reviewedExtraDutyUnits') === true
    const validMode = version === (extra ? 2 : 1) && field(proof, 'mode') === 'SourceSeed' && !field(proof, 'origin')
      || version === 3 && !extra && field(proof, 'mode') === 'ClosedReportedSourceSeed' && field(proof, 'origin') === 'ClosedReportedPeriodSalaryV2'
        && /^[a-f0-9]{64}$/.test(String(field(proof, 'reportedBoundaryHash') || ''))
    if (field(line, 'category') !== 'Summary' || field(line, 'sourceType') !== 'ExcelPayableOnly'
      || !Number.isInteger(days) || typeof days !== 'number' || days < 1 || days > 31
      || field(line, 'actualPresentDays') !== null || field(line, 'payableDays') !== employee.payableDays
      || field(proof, 'clientId') !== clientId || field(proof, 'employeeId') !== employee.employeeId || field(proof, 'payPeriod') !== month
      || field(proof, 'payrollDays') !== days || paid !== employee.payableDays || typeof paid !== 'number' || paid < 0
      || paid > (extra ? 999.99 : days) || Math.abs(paid * 100 - Math.round(paid * 100)) > 0.0000001
      || field(proof, 'actualPresentDays') !== null || field(proof, 'presentMeaning') !== 'NotMapped'
      || field(proof, 'payableMeaning') !== 'AlreadyPayable' || !validMode
      || !/^[a-f0-9]{64}$/i.test(String(field(proof, 'sourceHash') || ''))
      || !/^[a-f0-9]{32}$|^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(String(field(proof, 'batchId') || ''))
      || !/^[a-f0-9]{32}$|^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(String(field(proof, 'importId') || ''))
      || typeof field(proof, 'rowId') !== 'string' || !String(field(proof, 'rowId')).trim()) return fallback
    return days
  } catch { return fallback }
}
