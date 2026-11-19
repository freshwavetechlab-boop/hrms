import type { EmployeeMonthlyAttendance } from '../types/payroll'

// Presentation only: the native reader authenticates the saved source and receipt.
export function hasReviewedExtraPaidUnits(row: EmployeeMonthlyAttendance, clientId?: number): boolean {
  if (row.sourceType !== 'ExcelPayableOnly' || row.presentDays !== null || typeof row.remarks !== 'string' || row.remarks.length > 600) return false
  try {
    const evidence = JSON.parse(row.remarks)
    const guid = /^(?:[a-f\d]{32}|[a-f\d]{8}-(?:[a-f\d]{4}-){3}[a-f\d]{12})$/i
    return evidence.version === 2 && evidence.reviewedExtraDutyUnits === true
      && Number.isSafeInteger(evidence.clientId) && evidence.clientId > 0 && (clientId == null || evidence.clientId === clientId)
      && evidence.employeeId === row.employeeId && evidence.payPeriod === row.month
      && evidence.payrollDays === row.workingDays && evidence.payableDays === row.payableDays
      && evidence.actualPresentDays == null && evidence.presentMeaning === 'NotMapped' && evidence.payableMeaning === 'AlreadyPayable' && evidence.mode === 'SourceSeed'
      && guid.test(evidence.batchId) && guid.test(evidence.importId) && /^[a-f\d]{64}$/i.test(evidence.sourceHash)
      && typeof evidence.rowId === 'string' && evidence.rowId.trim().length > 0 && evidence.rowId.length <= 100
      && Number.isInteger(row.workingDays) && row.workingDays >= 1 && row.workingDays <= 31
      && Number.isFinite(row.payableDays) && row.payableDays >= 0 && row.payableDays <= 999.99
      && Math.round(row.payableDays * 100) / 100 === row.payableDays
  } catch { return false }
}

export function monthlyAttendanceHasIssue(row: EmployeeMonthlyAttendance, clientId?: number): boolean {
  const working = Number(row.workingDays), present = row.presentDays, payable = Number(row.payableDays), lop = Number(row.lopDays)
  if (!Number.isFinite(working) || !Number.isFinite(payable) || !Number.isFinite(lop)
    || working <= 0 || working > 31 || payable < 0 || lop < 0
    || present !== null && (!Number.isFinite(present) || present < 0 || present > working)) return true
  return hasReviewedExtraPaidUnits(row, clientId)
    ? Math.abs(lop - Math.max(0, working - payable)) > 0.01
    : payable > working || Math.abs(payable + lop - working) > 0.01
}
