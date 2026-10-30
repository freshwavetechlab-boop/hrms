import type { ExcelPayslipRow } from '../services/excelPayslipService'

export type AttendanceDayValue = {
  days: number | null
  label: string
  status: 'Recorded' | 'Not mapped' | 'Not supplied' | 'Review required'
  reason: string
}

const normalized = (label: string) => label.normalize('NFKC').trim().toUpperCase().replace(/[^A-Z0-9]/g, '')
// These are display-only source labels. Generic attendance/working days are not
// proof of actual presence or of payable leave/off-day credits.
const presentLabels = new Set(['PRESENT', 'PRESENTDAY', 'PRESENTDAYS', 'DAYSPRESENT', 'ACTUALPRESENTDAYS', 'ACTUALWORKED', 'ACTUALWORKEDDAYS', 'DAYSWORKED'])
const payableLabels = new Set(['PAIDDAYS', 'DAYSPAID', 'PAYABLEDAYS', 'DAYSPAYABLE'])
const sourceLabels = new Set(['ATTENDANCE', 'ATTENDANCEDAYS', 'WORKINGDAY', 'WORKINGDAYS', 'NOOFWORKINGDAYS', 'NUMBEROFWORKINGDAYS', 'NUMBEROFWORKINGDAYSATTENDED', 'DAYS'])

function sourceValue(row: Pick<ExcelPayslipRow, 'information'>, labels: Set<string>, kind: string): AttendanceDayValue {
  const matches = (row.information || []).filter(item => labels.has(normalized(item.label)))
  if (!matches.length) return { days: null, label: '', status: 'Not mapped', reason: `No unambiguous ${kind} field is recorded in this saved payslip. No value is inferred.` }
  if (matches.length !== 1) return { days: null, label: '', status: 'Review required', reason: `Several saved ${kind} fields match. Review the original mapping; equal values do not establish which column is authoritative.` }
  const item = matches[0], text = item.value.trim(), label = item.label.trim()
  if (!text) return { days: null, label, status: 'Not supplied', reason: `The saved ${kind} field is blank. Blank attendance is not zero.` }
  const days = /^(?:\d+(?:\.\d+)?|\.\d+)$/.test(text) ? Number(text) : NaN
  if (!Number.isFinite(days) || days < 0 || days > 31) return { days: null, label, status: 'Review required', reason: `The saved ${kind} value is not a valid day count from 0 to 31.` }
  return { days, label, status: 'Recorded', reason: kind === 'source attendance'
    ? 'Recorded under the original source heading. Its present/payable meaning has not been inferred; it may include paid leave or weekly offs.'
    : `Recorded under the saved ${kind} heading. This is source evidence, not a verified native attendance ledger.` }
}

export function calendarMonthDays(month: string): number | null {
  const match = /^(\d{4})-(0[1-9]|1[0-2])$/.exec(month)
  if (!match) return null
  const year = Number(match[1]), monthNumber = Number(match[2])
  if (year < 1) return null
  if (monthNumber === 2) return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28
  return [4, 6, 9, 11].includes(monthNumber) ? 30 : 31
}

export function savedPayslipAttendance(row: Pick<ExcelPayslipRow, 'information'>): { present: AttendanceDayValue; payable: AttendanceDayValue; source: AttendanceDayValue } {
  return { present: sourceValue(row, presentLabels, 'present days'), payable: sourceValue(row, payableLabels, 'payable days'), source: sourceValue(row, sourceLabels, 'source attendance') }
}

export const attendanceDayText = (value: AttendanceDayValue): number | string => value.days ?? value.status
export const sourceDayText = (value: AttendanceDayValue): string => value.days == null ? value.status : `${value.days} (${value.label})`
