import type { LocationAttendanceContext, LocationAttendanceInput } from '../services/payrollLocationService'
import type { ImportPreviewData } from './importPreview'
import type { XlsxSheet } from './xlsx'

export function payrollLocationOptions(context: LocationAttendanceContext | null) {
  const locations = new Map<number, { name: string; employees: Set<number> }>()
  for (const row of context?.rows || []) {
    if (row.workLocationId <= 0) continue
    const location = locations.get(row.workLocationId) || { name: row.workLocationName, employees: new Set<number>() }
    location.employees.add(row.employeeId); locations.set(row.workLocationId, location)
  }
  return [{ value: 0, label: 'All locations' }, ...[...locations].sort((a, b) => a[1].name.localeCompare(b[1].name))
    .map(([id, value]) => ({ value: id, label: `${value.name} (${value.employees.size})` }))]
}

const headers = ['EmployeeCode', 'EmployeeName', 'Location', 'PayrollDays', 'PresentDays', 'PayableDays', 'ClientId', 'Month', 'PartKey', 'EmployeeId', 'WorkLocationId', 'TemplateId', 'RulesHash', 'ContextVersion', 'InputVersion']
export function buildLocationAttendanceWorkbook(context: LocationAttendanceContext, workLocationId: number): XlsxSheet[] {
  const rows = context.rows.filter(row => !workLocationId || row.workLocationId === workLocationId)
  if (!rows.length) throw Error('No employees belong to this policy/location.')
  return [{ name: 'Attendance', freezeRows: 1, freezeColumns: 3, columnWidths: [22, 30, 28, 17, 17, 17, 12, 14, 44, 14, 17, 24, 38, 38], rows: [
    headers.map(value => ({ value, style: 'header' })),
    ...rows.map(row => [
      { value: row.employeeCode, style: 'text' as const }, row.employeeName, row.workLocationName,
      { value: row.payrollDays, style: 'input' as const }, { value: row.presentDays ?? '', style: 'input' as const }, { value: row.payableDays ?? '', style: 'input' as const },
      context.clientId, context.month, row.key, row.employeeId, row.workLocationId, row.templateId, row.rulesHash, context.version, row.inputVersion,
    ]),
  ] }, { name: 'Instructions', rows: [
    ['Location attendance', context.month], ['Fill PayrollDays, PresentDays and PayableDays, including explicit 0 where applicable. Blank day inputs remain pending.'],
    ['Keep employee codes, locations and the identity columns unchanged. Each reviewed location part has its own row.'],
    ['Upload the filled file, review and apply. Missing locations do not become absent days; monthly totals publish only when every part for that employee is recorded.'],
    ['Saved payroll months are locked. This workbook does not change salary templates, original Excel files or payroll by itself.'],
  ], columnWidths: [110, 18] }]
}

export function readLocationAttendanceUpload(data: ImportPreviewData, context: LocationAttendanceContext, workLocationId: number): LocationAttendanceInput[] {
  for (const name of headers) if (data.headers.filter(value => value.trim() === name).length !== 1) throw Error(`Location template requires ${name}. Download the current template.`)
  const seen = new Set<string>(), results: LocationAttendanceInput[] = []
  if (!data.rows.length || data.rows.length > 1000) throw Error('Upload 1–1000 location rows at a time.')
  for (const [index, cells] of data.rows.entries()) {
    const get = (name: string) => (cells[data.headers.indexOf(name)] ?? '').trim()
    const row = context.rows.find(row => row.key === get('PartKey'))
    if (!row || seen.has(row.key) || row.employeeCode !== get('EmployeeCode') || row.employeeId !== Number(get('EmployeeId'))
      || row.workLocationId !== Number(get('WorkLocationId')) || row.workLocationName !== get('Location') || row.templateId !== get('TemplateId')
      || row.rulesHash !== get('RulesHash') || context.clientId !== Number(get('ClientId')) || context.month !== get('Month') || context.version !== get('ContextVersion')
      || workLocationId > 0 && row.workLocationId !== workLocationId) throw Error(`Row ${index + 2}: client, month, policy, employee or location changed. Use the current location template.`)
    seen.add(row.key)
    const present = get('PresentDays'), payable = get('PayableDays'), payroll = get('PayrollDays')
    if (!present && !payable) continue
    if (row.inputVersion !== get('InputVersion')) throw Error(`Row ${index + 2}: this location's attendance changed. Refresh this row before replacing it.`)
    if (!present || !payable || !payroll) throw Error(`Row ${index + 2}: enter both present and payable days, including 0 when intended.`)
    if (row.lockedPayRunId) throw Error(`Row ${index + 2}: payroll #${row.lockedPayRunId} already covers this employee/month.`)
    if (row.issue) throw Error(`Row ${index + 2}: ${row.issue}`)
    const days = [payroll, present, payable].map(Number)
    if (days.some(value => !Number.isFinite(value) || value < 0 || Math.abs(value * 10000 - Math.round(value * 10000)) > 0.000001)
      || !Number.isInteger(days[0]) || days[0] < 1 || days[0] > 31 || days[1] > days[0] || days[2] > days[0]) throw Error(`Row ${index + 2}: invalid payroll/present/payable days.`)
    results.push({ key: row.key, employeeId: row.employeeId, workLocationId: row.workLocationId, payrollDays: days[0], presentDays: days[1], payableDays: days[2], inputVersion: row.inputVersion })
  }
  if (!results.length) throw Error('No filled attendance rows. Blank days stay pending.')
  return results
}
