import type { EmployeeMonthlyAttendance } from '../types/payroll'

// Aggregate totals are deliberate inputs. Never manufacture dated attendance.
export function prepareMonthlyAttendanceImport(cells: string[][], roster: EmployeeMonthlyAttendance[], month: string) {
  const headers = cells[0]?.map(value => value.trim()) ?? []
  const required = ['EmployeeCode', 'WorkingDays', 'PresentDays', 'PayableDays', 'LopDays']
  if (required.some(name => headers.filter(value => value === name).length !== 1)) throw Error(`Monthly totals CSV requires ${required.join(', ')}.`)
  const data = cells.slice(1).filter(row => row.some(value => value.trim()))
  if (!data.length || data.length > 1000) throw Error('Import 1–1000 monthly attendance rows at a time.')
  const seen = new Set<string>()
  return data.map((row, index) => {
    const get = (name: string) => (row[headers.indexOf(name)] ?? '').trim()
    const code = get('EmployeeCode').toUpperCase()
    const matches = roster.filter(employee => employee.employeeCode.trim().toUpperCase() === code)
    if (!code || seen.has(code) || matches.length !== 1) throw Error(`Row ${index + 2}: employee code is missing, duplicated or outside the selected policy.`)
    seen.add(code)
    const amounts = required.slice(1).map(name => {
      const text = get(name), value = Number(text)
      if (!text || !Number.isFinite(value) || value < 0 || value > 31 || Math.abs(value * 100 - Math.round(value * 100)) > 0.000001) throw Error(`Row ${index + 2}: enter an explicit valid ${name}, including 0 when intended.`)
      return value
    })
    const [workingDays, presentDays, payableDays, lopDays] = amounts
    if (!workingDays || presentDays > workingDays || payableDays > workingDays || Math.abs(payableDays + lopDays - workingDays) > 0.000001) throw Error(`Row ${index + 2}: present/payable days must fit working days and payable + LOP must equal working days.`)
    return { ...matches[0], month, workingDays, presentDays, payableDays, lopDays, actualPresenceKnown: true, sourceType: 'Monthly', remarks: get('Remarks') || 'Reviewed monthly attendance totals import.' }
  })
}
