import type { Component, Employee, PayrollAdjustment } from '../../types/payroll'

export type AdjustmentImportMode = 'scheduled' | 'arrears'
export type AdjustmentImportResult = { rows: PayrollAdjustment[]; skipped: number; errors: string[] }

type CsvRow = Record<string, string>

const maxImportRows = 1000

export function prepareAdjustmentImports(input: {
  text: string
  mode: AdjustmentImportMode
  employees: Employee[]
  components: Component[]
  clientId: number
  period: string
  seed: PayrollAdjustment
}): AdjustmentImportResult {
  const csvRows = parseCsv(input.text)
  const errors: string[] = []
  const rows: PayrollAdjustment[] = []
  let skipped = 0

  if (csvRows.length > maxImportRows) {
    return { rows, skipped: csvRows.length, errors: [`Import supports up to ${maxImportRows} rows at a time.`] }
  }

  csvRows.forEach((row, index) => {
    const employee = findEmployee(input.employees, row.employeeCode)
    const component = findComponent(input.components, row.componentCode)
    const amount = input.mode === 'arrears' ? arrearAmount(row) : Number(row.amount || 0)

    if (!employee || !component || amount <= 0) {
      skipped += 1
      errors.push(`Row ${index + 2}: ${!employee ? 'employeeCode not found. ' : ''}${!component ? 'componentCode not found. ' : ''}${amount <= 0 ? 'amount must be positive.' : ''}`.trim())
      return
    }

    rows.push({
      ...input.seed,
      clientId: input.clientId,
      employeeId: employee.id,
      employeeName: `${employee.firstName} ${employee.lastName}`.trim(),
      employeeCode: employee.employeeCode,
      componentId: component.id,
      componentCode: component.code,
      componentName: component.name,
      adjustmentType: componentToAdjustmentType(component),
      amount,
      payPeriod: validPayPeriod(row.payPeriod) ? row.payPeriod : input.period,
      payRunType: normalizePayRunType(row.payRunType),
      reasonCode: input.mode === 'arrears' ? 'Arrears' : row.reason || row.reasonCode || 'Bonus/Incentive',
      notes: row.notes || (input.mode === 'arrears' ? `Auto arrears: ${row.oldMonthly || 0} to ${row.newMonthly || 0} for ${row.months || 1} month(s)` : 'Scheduled earning import'),
      taxable: component.taxable,
      status: 'Approved'
    })
  })

  return { rows, skipped, errors }
}

export function componentToAdjustmentType(component: Component) {
  const text = `${component.category} ${component.componentType}`.toLowerCase()
  if (text.includes('deduction') || text.includes('recovery') || text.includes('reversal')) return 'Deduction'
  if (text.includes('reimbursement') || text.includes('benefit')) return 'Reimbursement'
  return 'Earning'
}

function parseCsv(text: string) {
  const lines = text.replace(/^\uFEFF/, '').split(/\r?\n/).filter(line => line.trim())
  if (lines.length < 2) return []
  const headers = splitCsvLine(lines[0] ?? '').map(header => header.trim())
  return lines.slice(1).map(line => {
    const cells = splitCsvLine(line)
    return headers.reduce<CsvRow>((row, header, index) => ({ ...row, [header]: (cells[index] || '').trim() }), {})
  })
}

function splitCsvLine(line: string) {
  const cells: string[] = []
  let cell = '', quoted = false
  for (let index = 0; index < line.length; index += 1) {
    const char = line[index]
    const next = line[index + 1]
    if (char === '"' && quoted && next === '"') { cell += '"'; index += 1; continue }
    if (char === '"') { quoted = !quoted; continue }
    if (char === ',' && !quoted) { cells.push(cell); cell = ''; continue }
    cell += char
  }
  cells.push(cell)
  return cells
}

function arrearAmount(row: CsvRow) {
  return (Number(row.newMonthly || 0) - Number(row.oldMonthly || 0)) * Number(row.months || 1)
}

function normalizePayRunType(value: string | undefined) {
  return String(value || '').toLowerCase().replace(/[-_]/g, ' ').includes('off') ? 'Off Cycle' : 'Regular'
}

function validPayPeriod(value: string | undefined) {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(value || '')
}

function findEmployee(employees: Employee[], code: string | undefined) {
  return employees.find(employee => sameCode(employee.employeeCode, code))
}

function findComponent(components: Component[], code: string | undefined) {
  return components.find(component => sameCode(component.code, code))
}

function sameCode(left: string | undefined, right: string | undefined) {
  return String(left || '').trim().toUpperCase() === String(right || '').trim().toUpperCase()
}
