import type { ExcelPayslipBatch, ExcelPayslipCalculationSource, ExcelPayslipRow } from '../services/excelPayslipService'
import type { PayrollSetupComponent, PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter, parsePayslipAmount } from './excelPayslipImport'

const normalized = (text: string) => text.normalize('NFKC').trim().toUpperCase().replace(/[^A-Z0-9]/g, '')
const componentCode = (label: string) => {
  const name = normalized(label).replace(/^(?:EARNED|ER)(?=BASIC|HRA|SPECIAL|EXP|OTHER)/, '')
  if (/BASIC/.test(name)) return 'BASIC'
  if (name === 'HRA') return 'HRA'
  if (/SPECIALALLOW/.test(name)) return 'SPECIAL_ALLOWANCE'
  if (/^(EXP|EXPENSE)ALLOW/.test(name)) return 'EXP_ALLOWANCE'
  if (/OTHERALLOW/.test(name)) return 'OTHER_ALLOW'
  if (/BONUS/.test(name)) return 'STAT_BONUS'
  if (/TADA|TRAVELREIMBURSEMENT/.test(name)) return 'TA_DA'
  if (/RECOVERY/.test(name)) return 'RECOVERY'
  if (/PTAX|PROFESSIONALTAX/.test(name)) return 'PT'
  if (/ADMIN/.test(name)) return 'EPF_ADMIN'
  if (/EDLI/.test(name)) return 'EPF_EDLI'
  if (/ESI/.test(name)) return 'ESIC'
  if (/EPF|PROVIDENT|^PF/.test(name)) return 'PF'
  return label.toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_|_$/g, '').slice(0, 50) || 'ALLOWANCE'
}
const numeric = (value?: string) => {
  return value === undefined ? undefined : parsePayslipAmount(value) ?? undefined
}
const letterIndex = (letters: string) => [...letters.toUpperCase()].reduce((v, c) => v * 26 + c.charCodeAt(0) - 64, 0) - 1
const sourceCell = (source: ExcelPayslipCalculationSource, row: number, column: number) => source.rows.find(item => item.sourceRow === row)?.cells[column]
const expression = (text: string) => text.replace(/^=/, '').replace(/\$/g, '').replace(/\s+/g, '').toUpperCase()
const identityText = (text: string) => text.normalize('NFKC').trim().toUpperCase().replace(/[^\p{L}\p{N}]/gu, '')
const employeeName = (text: string) => identityText(text.normalize('NFKC').trim().replace(/^(?:MR|MS|MRS)\.?\s+/i, ''))

function functionArgs(text: string, start: number) {
  const args: string[] = []; let depth = 0, from = start
  for (let index = start; index < text.length; index++) {
    if (text[index] === '(') depth++
    else if (text[index] === ')') {
      if (!depth) { args.push(text.slice(from, index)); return { args, end: index } }
      depth--
    } else if (text[index] === ',' && !depth) { args.push(text.slice(from, index)); from = index + 1 }
  }
  throw new Error('Review the incomplete formula.')
}

// Native rounding functions accept one argument. Remove Excel's zero precision
// only from that exact function call; MIN/MAX(...,0) must keep their zero term.
function nativeRounding(text: string): string {
  return rewrite(text)
  function rewrite(value: string): string {
    const match = /\b([A-Z][A-Z0-9_]*)\(/.exec(value)
    if (!match) return value
    const start = match.index + match[0].length, call = functionArgs(value, start)
    let args = call.args.map(rewrite)
    if (/^(ROUND|ROUNDUP|ROUNDDOWN)$/.test(match[1])) {
      if (args.length === 2 && numeric(args[1]) === 0) args = [args[0]]
      else if (args.length !== 1) throw new Error('Native rounding supports whole amounts only; review this rounding precision.')
    }
    return value.slice(0, match.index) + match[1] + '(' + args.join(',') + ')' + rewrite(value.slice(call.end + 1))
  }
}

// Resolve conditions only from imported fixed inputs. Attendance-based conditions
// require a reviewed rule, so they cannot be frozen as an employee exemption.
function resolveFixedCondition(formula: string, source: ExcelPayslipCalculationSource, row: number, daysColumns: Set<number>): string {
  const text = expression(formula)
  if (!text.startsWith('IF(')) return text
  const call = functionArgs(text, 3), args = call.args
  if (args.length !== 3 || call.end !== text.length - 1) throw new Error('Review the conditional formula.')
  const condition = args[0].match(/^([A-Z]+)(\d+)(<=|>=|<>|=|<|>)(-?\d+(?:\.\d+)?)$/)
  if (!condition || Number(condition[2]) !== row || daysColumns.has(letterIndex(condition[1]))) throw new Error('Review the conditional formula; its condition cannot be inferred from fixed inputs.')
  const reference = condition[1] + condition[2], limit = condition[4]
  const capped = condition[3] === '>' || condition[3] === '>=' ? [args[1], args[2]] : condition[3] === '<' || condition[3] === '<=' ? [args[2], args[1]] : []
  if (capped.length && capped[0].startsWith(limit + '*') && capped[1] === reference + '*' + capped[0].slice(limit.length + 1))
    return `MIN(${reference},${limit})*${capped[0].slice(limit.length + 1)}`
  const cell = sourceCell(source, row, letterIndex(condition[1]))
  if (cell?.formulaText || cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error('Review the conditional formula; a calculated condition cannot be frozen from its saved result.')
  const left = numeric(cell?.value), right = Number(limit)
  if (left === undefined) throw new Error('The conditional formula has a missing input.')
  const choose = ({ '<=': left <= right, '>=': left >= right, '<>': left !== right, '=': left === right, '<': left < right, '>': left > right })[condition[3]]
  return resolveFixedCondition(args[choose ? 1 : 2], source, row, daysColumns)
}

export function proposeExcelPayrollSetup(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[]) {
  const source = context.source, rows = batch.rows.filter(row => rowIds.includes(row.id)), notes: string[] = []
  const dayColumn = source.columns.find(c => /^(?:WORKINGDAYS?|ATTENDANCE|PAYABLEDAYS?|NUMBEROFWORKINGDAYSATTENDED)$/.test(normalized(c.sourceHeader)))
  const periodColumn = source.columns.find(c => /^(?:MONTH|MONTHDAYS|PAYROLLDAYS|CALCULATIONDAYS)$/.test(normalized(c.sourceHeader)))
  const daysIndex = dayColumn?.columnIndex ?? -1, periodIndex = periodColumn?.columnIndex ?? -1
  const locations = source.columns.filter(column => /^(?:WORKLOCATION|WORKLOCATIONNAME|LOCATION|LOCATIONNAME.*|LOCATIONNAMETEHSILSUBTEHSIL)$/.test(normalized(column.sourceHeader)))
  const locationIndex = locations.length === 1 ? locations[0].columnIndex : undefined
  const outputColumns = source.columns.filter(c => ['earning', 'deduction', 'employer'].includes(c.kind))
  const codes = new Map<number, string>(), components: PayrollSetupComponent[] = []
  for (const column of outputColumns) {
    let code = componentCode(column.sourceHeader || column.label)
    if (column.kind === 'employer') code = code === 'PF' ? 'EPF_ER' : code === 'ESIC' ? 'ESIC_ER' : code
    if ([...codes.values()].includes(code)) code += `_${excelColumnLetter(column.columnIndex)}`
    codes.set(column.columnIndex, code)
  }
  const inputToCode = new Map<number, string>()
  for (const column of outputColumns.filter(c => c.kind === 'earning')) {
    const code = codes.get(column.columnIndex)!
    const matching = source.columns.filter(c => ['info', 'ignore'].includes(c.kind) && componentCode(c.sourceHeader) === code
      && !/^(ER|EARNED)/.test(normalized(c.sourceHeader))
      && !/employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|(?:birth|joining)\s*date/i.test(c.sourceHeader))
    if (matching.length === 1) inputToCode.set(matching[0].columnIndex, code)
  }
  const translate = (raw: string, row: ExcelPayslipRow) => {
    let formula = resolveFixedCondition(raw, source, row.sourceRow, new Set([periodIndex, daysIndex]))
    formula = nativeRounding(formula)
    formula = formula.replace(/SUM\(([A-Z]+)(\d+):([A-Z]+)(\d+)\)/g, (_, first: string, firstRow: string, last: string, lastRow: string) => {
      if (Number(firstRow) !== row.sourceRow || firstRow !== lastRow) throw new Error('Review this range before configuring payroll.')
      const terms = []
      for (let index = letterIndex(first); index <= letterIndex(last); index++) terms.push(`${excelColumnLetter(index)}${firstRow}`)
      return `(${terms.join('+')})`
    })
    formula = formula.replace(/\b([A-Z]+)(\d+)\b/g, (_, letters: string, sourceRow: string) => {
      const index = letterIndex(letters)
      if (Number(sourceRow) !== row.sourceRow) {
        const value = numeric(sourceCell(source, Number(sourceRow), index)?.value)
        const cell = sourceCell(source, Number(sourceRow), index)
        if (value === undefined || cell?.formulaText || cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error('Review the shared formula input.')
        return String(value)
      }
      if (index === periodIndex) return 'PAYROLL_DAYS'
      if (index === daysIndex) return 'PAYABLE_DAYS'
      if (codes.has(index)) return `${codes.get(index)}_EARNED`
      if (inputToCode.has(index)) return `${inputToCode.get(index)}_MONTHLY`
      const cell = sourceCell(source, row.sourceRow, index), value = numeric(cell?.value)
      if (value === undefined || cell?.formulaText || cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error(`Review source column ${letters}.`)
      return String(value)
    })
    if (/!|\[|IF\(|VLOOKUP|SUM\(/.test(formula) || [...formula.matchAll(/\b([A-Z][A-Z0-9_]*)\(/g)].some(match => !['MIN', 'MAX', 'ROUND', 'ROUNDUP', 'ROUNDDOWN'].includes(match[1]))) throw new Error('This formula needs an explicit payroll rule.')
    return formula
  }
  for (const column of outputColumns) {
    const code = codes.get(column.columnIndex)!, category = column.kind === 'employer' ? 'Benefit' : column.kind === 'deduction' ? 'Deduction' : code === 'TA_DA' ? 'Reimbursement' : 'Earning'
    const master = context.components.find(c => normalized(c.code) === normalized(code) && c.category === category)
    const input = [...inputToCode].find(([, value]) => value === code)?.[0]
    const periodAdjustment = /^(TA_DA|RECOVERY|OTA)$/.test(code)
    const rowFormulas: Record<string, string> = {}
    if (input === undefined && !periodAdjustment) {
      for (const row of rows) {
        const cell = sourceCell(source, row.sourceRow, column.columnIndex)
        try {
          if (cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error('Correct the source error or missing formula result before proposing a payroll rule.')
          const amount = numeric(cell?.value)
          const blank = !cell?.value.trim() && !cell?.formulaText
          if (!cell?.formulaText && amount === undefined && !(column.blankAsZero && blank)) throw new Error('Review this blank or invalid source amount before configuring payroll.')
          rowFormulas[row.id] = cell?.formulaText ? translate(cell.formulaText, row) : String(amount ?? 0)
        } catch (error) {
          rowFormulas[row.id] = 'REVIEW_REQUIRED'
          notes.push(`${row.employeeName} / ${column.label}: ${error instanceof Error ? error.message : 'Review formula.'}`)
        }
      }
    }
    if (input !== undefined) for (const row of rows) {
      const cell = sourceCell(source, row.sourceRow, input)
      if (numeric(cell?.value) === undefined) notes.push(`${row.employeeName} / ${column.label}: review the blank monthly input; approving blank monetary outputs does not approve blank monthly inputs.`)
    }
    const statutory = master?.statutoryType || ({ PF: 'PF Employee', EPF_ER: 'PF Employer', ESIC: 'ESI Employee', ESIC_ER: 'ESI Employer', PT: 'Professional Tax' }[code] ?? 'None')
    const role = master?.componentRole || (category === 'Benefit' ? statutory === 'None' ? 'Employer Benefit' : 'Employer Contribution' : category === 'Deduction' ? 'Regular Deduction' : category === 'Reimbursement' ? 'Reimbursement' : 'Regular Earning')
    components.push({ key: `column-${column.columnIndex}`, existingComponentId: master?.id, code, name: master?.name || column.label,
      category, componentRole: role, statutoryType: statutory, calculationType: periodAdjustment ? 'Manual / Variable' : input !== undefined ? 'Fixed Amount' : 'Formula',
      value: '0', formula: input !== undefined || periodAdjustment ? '' : Object.values(rowFormulas).find(value => value && value !== '0') || '0', baseComponent: '',
      proRata: input !== undefined, taxable: category === 'Earning', priority: 100 + column.columnIndex * 10, sourceColumnIndex: input ?? (periodAdjustment ? column.columnIndex : undefined),
      expectedColumnIndex: column.columnIndex, isPeriodAdjustment: periodAdjustment, exemptWhenSourceZero: false, blankInputAsZero: false, ...(Object.keys(rowFormulas).length ? { rowFormulas } : {}) })
  }
  const decisions = rows.map(row => {
    const cell = locationIndex === undefined ? undefined : sourceCell(source, row.sourceRow, locationIndex)
    const rawLocation = cell?.error || cell?.missingCachedValue ? '' : cell?.value.trim() || ''
    const location = /^(?:N\/?A|NIL|NONE|NOT AVAILABLE|0|-+)$/i.test(rawLocation) ? '' : rawLocation
    const name = employeeName(row.employeeName)
    const matches = name && identityText(location) ? context.employees.filter(employee => {
      const employeeLocation = employee.workLocationId === undefined ? employee.workLocation || '' : context.workLocations.find(item => item.id === employee.workLocationId)?.name || employee.workLocation || ''
      return employeeName(employee.employeeName) === name && identityText(employeeLocation) === identityText(location)
    }) : []
    if (matches.length !== 1) notes.push(`${row.employeeName}: ${matches.length > 1 ? 'name and work location match several employees' : !location ? 'source work location is missing or ambiguous' : 'no unique employee matches this name and work location'}; select an employee or explicitly review creation.`)
    return { rowId: row.id, matchedEmployeeId: matches.length === 1 ? matches[0].id : undefined, allowCreate: false }
  })
  const request: PayrollSetupRequest = { batchId: batch.id, rowIds: rows.map(r => r.id), templateName: `${batch.clientName || 'Client'} ${batch.month} salary setup`, effectiveFrom: `${batch.month}-01`,
    payrollDaysColumnIndex: periodIndex, payableDaysColumnIndex: daysIndex, workWeek: context.workWeeks.find(w => normalized(w.label) === 'SUNDAYOFF')?.value || '',
    attendanceCycleStartDay: 1, attendanceCycleEndDay: 30, payrollReportGenerationDay: 3, defaultDepartment: '', defaultDesignation: '', defaultGrade: '', sourceWorkLocationColumnIndex: locationIndex, createMissingWorkLocations: false,
    components, employeeDecisions: decisions, editableDefaults: { leave: { status: 'Draft', quotaApproved: false, proposedAnnualPaidLeave: 0, active: false },
      holiday: { status: 'Draft', year: Number(batch.month.slice(0, 4)), dates: [] }, shift: { status: 'Draft', requiredForMonthlyTotals: false }, attendance: { mode: 'Monthly', payableDaysSource: 'Uploaded salary workbook' } } }
  if (periodIndex < 0) notes.push('Select the calculation-days column. A September divisor does not establish the policy for other months.')
  if (daysIndex < 0) notes.push('Select the column containing payable days.')
  if (locations.length !== 1) notes.push('Select one source work-location column before matching or creating employees.')
  return { request, notes: [...new Set(notes)] }
}
