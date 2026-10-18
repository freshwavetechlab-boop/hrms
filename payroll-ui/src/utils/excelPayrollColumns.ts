import type { ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import { defaultPayslipMapping, type PayslipColumn, type PayslipColumnKind } from './excelPayslipImport'

const key = (value: string) => value.toUpperCase().replace(/[^A-Z0-9]/g, '').replace(/^BESIC$/, 'BASIC').replace(/^(?:ER|EARNED)(?=BASIC|HRA|SPECIAL|EXP|OTHER)/, '')
const earning = /^(?:BASIC|BESIC|DA|HRA|HRS\d*|SPECIALALLOWANCE|EXPALLOWANCE|EXPENSEALLOWANCE|OTHERALLOWANCE|CITYALLOWANCE|CONVEYANCEALLOWANCE|TRAVELALLOWANCE|TRANSPORTALLOWANCE|MEDICALALLOWANCE|LEAVEENCASHMENT|BONUS\d*|INCENTIVE|WAGES|EARNSALARY|EARNEDSALARY)$/

// Payroll discovery is independent of payslip presentation. A simplified PDF may
// hide every base/earned column while the authoritative source still retains it.
export function payrollSourceColumns(source: ExcelPayslipCalculationSource, roles: Record<number, string> = {}, employeeRows?: number[]): PayslipColumn[] {
  const employeeSet = employeeRows ? new Set(employeeRows) : null
  const defaults = defaultPayslipMapping(source.columns.map(column => column.sourceHeader))
  const result = source.columns.map(column => ({ ...column }))
  const net = result.find(column => column.kind === 'netPay')?.columnIndex ?? Infinity
  const groups = new Map<string, PayslipColumn[]>()
  for (const column of result) {
    const name = key(column.sourceHeader)
    if (!earning.test(name) || column.columnIndex > net && /^BONUS/.test(name)) continue
    const group = groups.get(name) || []; group.push(column); groups.set(name, group)
  }
  for (const group of groups.values()) {
    const outputs = group.filter(column => source.rows.some(row => row.sourceRow > source.headerRow && (!employeeSet || employeeSet.has(row.sourceRow)) && row.cells[column.columnIndex]?.formulaText))
    const chosen = group.find(column => column.kind === 'earning') || (outputs.length === 1 ? outputs[0] : group.length === 1 ? group[0] : undefined)
    if (!chosen) continue
    for (const column of group) column.kind = column === chosen ? 'earning' : 'info'
  }
  const granular = result.some(column => column.kind === 'earning' && earning.test(key(column.sourceHeader)))
  for (const column of result) {
    const name = key(column.sourceHeader)
    if (granular && /^(?:GROSS|GROSSSALARY|EARNEDGROSS|FINALSALARY|TOTAL|TOTALEARNING|TOTALEARNINGS|TOTALSALARY)$/.test(name)) column.kind = 'grossTotal'
    else if (column.kind === 'ignore' && defaults[column.columnIndex] && ['deduction', 'employer'].includes(defaults[column.columnIndex].kind)) {
      // Keep explicitly combined PF output; do not add its individual split twice.
      if (/^(?:EPF|PF)\d+$/.test(name) && result.some(item => item.kind === 'deduction' && /^(?:TOTALEPF|TOTALPF)$/.test(key(item.sourceHeader)))) continue
      column.kind = defaults[column.columnIndex].kind
    }
    if (roles[column.columnIndex]) column.kind = roles[column.columnIndex] as PayslipColumnKind
  }
  return result
}
