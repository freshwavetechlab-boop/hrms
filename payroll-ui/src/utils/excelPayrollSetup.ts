import type { ExcelPayslipBatch, ExcelPayslipCalculationSource, ExcelPayslipRow } from '../services/excelPayslipService'
import type { PayrollSetupComponent, PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter, parsePayslipAmount } from './excelPayslipImport'
import { payslipAttendanceBasis } from './excelPayslipAttendance'
import { sharedPayrollDivisor } from './payrollSourceEvidence'
import { payrollSourceColumns } from './excelPayrollColumns'

const normalized = (text: string) => text.normalize('NFKC').trim().toUpperCase().replace(/[^A-Z0-9]/g, '')
const componentCode = (label: string) => {
  const name = normalized(label).replace(/^(?:EARNED|ER)(?=BASIC|HRA|SPECIAL|EXP|OTHER)/, '').replace(/^BESIC$/, 'BASIC')
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

// These properties are read from the shared component by regular payroll.
// Template-level formula and attendance overrides must not invent replacements.
export function payrollSetupMasterPriority(value: number | string | undefined): number | null {
  if (typeof value !== 'number' && (typeof value !== 'string' || !/^-?\d+$/.test(value.trim()))) return null
  const priority = typeof value === 'number' ? value : Number(value.trim())
  return Number.isSafeInteger(priority) ? priority : null
}

export function selectExcelPayrollMaster(component: PayrollSetupComponent, master?: PayrollSetupContext['components'][number]): PayrollSetupComponent {
  if (!master) return { ...component, existingComponentId: undefined }
  return { ...component, existingComponentId: master.id, code: master.code, name: master.name,
    category: master.category, componentRole: master.componentRole, statutoryType: master.statutoryType,
    taxable: master.taxable, priority: payrollSetupMasterPriority(master.priority) ?? Number.NaN, payType: master.payType }
}

export function restoreExcelPayrollMasterMetadata(request: PayrollSetupRequest, context: PayrollSetupContext): PayrollSetupRequest {
  return { ...request, components: request.components.map(component => {
    const master = component.existingComponentId ? context.components.find(item => item.id === component.existingComponentId) : undefined
    return master ? selectExcelPayrollMaster(component, master) : component
  }) }
}

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
  const position = text.indexOf('IF(')
  if (position < 0) return text
  const call = functionArgs(text, position + 3), args = call.args
  if (args.length !== 3) throw new Error('Review the conditional formula.')
  const ungroup = (value: string) => {
    while (value.startsWith('(')) { const group = functionArgs(value, 1); if (group.end !== value.length - 1 || group.args.length !== 1) break; value = group.args[0] }
    return value
  }
  const replace = (value: string) => resolveFixedCondition(position === 0 && call.end === text.length - 1 ? value : text.slice(0, position) + `(${value})` + text.slice(call.end + 1), source, row, daysColumns)
  // Convert both MIN and MAX patterns exactly as written, including calculated
  // bases. Do not infer which statutory ceiling the source ought to have used.
  const cap = ungroup(args[0]).match(/^(.+?)(<=|>=|<|>)(-?\d+(?:\.\d+)?)$/)
  if (cap) {
    const reference = ungroup(cap[1]), limit = cap[3]
    const branch = (value: string) => { const match = ungroup(value).match(/^(.+)\*([^*]+)$/); return match ? [ungroup(match[1]), match[2]] : [] }
    const yes = branch(args[1]), no = branch(args[2])
    if (yes.length && no.length && yes[1] === no[1] && ((yes[0] === reference && no[0] === limit) || (yes[0] === limit && no[0] === reference))) {
      const maximum = (cap[2].startsWith('>')) === (yes[0] === reference)
      return replace(`${maximum ? 'MAX' : 'MIN'}(${reference},${limit})*${yes[1]}`)
    }
    // A positive attendance test has a native exact indicator for nonnegative
    // day inputs, including half days. Only constant branches use this rewrite.
    const day = reference.match(/^([A-Z]+)(\d+)$/)
    if (day && Number(day[2]) === row && daysColumns.has(letterIndex(day[1])) && cap[2] === '>' && Number(limit) === 0
      && args.slice(1).every(value => /^[\d.()+*/%+-]+$/.test(value)))
      return replace(`MIN(1,MAX(0,ROUNDUP(${reference})))*(${args[1]})+(1-MIN(1,MAX(0,ROUNDUP(${reference}))))*(${args[2]})`)
  }
  const condition = args[0].match(/^([A-Z]+)(\d+)(<=|>=|<>|=|<|>)(-?\d+(?:\.\d+)?)$/)
  if (!condition || Number(condition[2]) !== row || daysColumns.has(letterIndex(condition[1]))) throw new Error('Review the conditional formula; its condition cannot be inferred from fixed inputs.')
  const limit = condition[4]
  const cell = sourceCell(source, row, letterIndex(condition[1]))
  if (cell?.formulaText || cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error('Review the conditional formula; a calculated condition cannot be frozen from its saved result.')
  const left = numeric(cell?.value), right = Number(limit)
  if (left === undefined) throw new Error('The conditional formula has a missing input.')
  const choose = ({ '<=': left <= right, '>=': left >= right, '<>': left !== right, '=': left === right, '<': left < right, '>': left > right })[condition[3]]
  return replace(args[choose ? 1 : 2])
}

export function proposeExcelPayrollSetup(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[], roles: Record<number, string> = {}) {
  const rows = batch.rows.filter(row => rowIds.includes(row.id)), notes: string[] = []
  const source = { ...context.source, columns: payrollSourceColumns(context.source, roles, rows.map(row => row.sourceRow)) }
  const dayColumn = source.columns.find(c => /^(?:WORKINGDAYS?|ATTENDANCE|PAYABLEDAYS?|NUMBEROFWORKINGDAYSATTENDED)$/.test(normalized(c.sourceHeader)))
  const periodColumn = source.columns.find(c => /^(?:MONTH|MONTHDAYS|PAYROLLDAYS|CALCULATIONDAYS)$/.test(normalized(c.sourceHeader)) && rows.every(row => { const value = numeric(sourceCell(source, row.sourceRow, c.columnIndex)?.value); return value !== undefined && value >= 1 && value <= 31 }))
  const daysIndex = dayColumn?.columnIndex ?? -1, periodIndex = periodColumn?.columnIndex ?? -1
  const locations = source.columns.filter(column => /^(?:WORKLOCATION.*|LOCATION|LOCATIONNAME.*|PLACEOFPOSTING|POSTING)$/.test(normalized(column.sourceHeader)))
  const locationIndex = locations.length === 1 ? locations[0].columnIndex : undefined
  const outputColumns = source.columns.filter(c => ['earning', 'deduction', 'employer'].includes(c.kind))
  const codes = new Map<number, string>(), components: PayrollSetupComponent[] = []
  for (const column of outputColumns) {
    let code = componentCode(column.sourceHeader || column.label)
    if (column.kind === 'employer') code = code === 'PF' ? 'EPF_ER' : code === 'ESIC' ? 'ESIC_ER' : code
    if ([...codes.values()].includes(code)) code += `_${excelColumnLetter(column.columnIndex)}`
    codes.set(column.columnIndex, code)
  }
  const inputToCode = new Map<number, string>(), selfMonthly = new Set<string>(), dailyRates = new Set<string>()
  for (const column of outputColumns.filter(c => c.kind === 'earning')) {
    const code = codes.get(column.columnIndex)!
    const matching = source.columns.filter(c => ['info', 'ignore'].includes(c.kind) && componentCode(c.sourceHeader) === code
      && !/^(ER|EARNED)/.test(normalized(c.sourceHeader))
      && !/employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|(?:birth|joining)\s*date/i.test(c.sourceHeader))
    if (matching.length === 1 && rows.every(row => {
      const cell = sourceCell(source, row.sourceRow, matching[0].columnIndex)
      return cell && !cell.formulaText?.trim() && !cell.error && !cell.calculationError && !cell.missingCachedValue && numeric(cell.value) !== undefined
    })) inputToCode.set(matching[0].columnIndex, code)
    else if (!matching.length && /^(?:BASIC|BESIC|DA|HRA|SPECIALALLOWANCE|EXPALLOWANCE|OTHERALLOWANCE|CITYALLOWANCE|CONVEYANCEALLOWANCE)$/.test(normalized(column.sourceHeader))
      && rows.every(row => { const cell = sourceCell(source, row.sourceRow, column.columnIndex); return !cell?.formulaText && numeric(cell?.value) !== undefined })) {
      inputToCode.set(column.columnIndex, code); selfMonthly.add(code)
      notes.push(`${column.label}: treated as a full-month base; payable days prorate it. Review this basis against the saved net pay.`)
    } else if (!matching.length && /^(?:WAGES|SALARY|EARNSALARY|EARNEDSALARY)$/.test(normalized(column.sourceHeader))) {
      const rates = source.columns.filter(c => /^(?:RATE|MONTHLYRATE|RATEPERDAY|DAILYRATE)$/.test(normalized(c.sourceHeader)))
      if (rates.length === 1 && daysIndex >= 0 && rows.every(row => sourceCell(source, row.sourceRow, column.columnIndex)?.formulaText)) {
        const rate = rates[0], sample = expression(sourceCell(source, rows[0].sourceRow, column.columnIndex)!.formulaText!)
        const reference = `${excelColumnLetter(rate.columnIndex)}${rows[0].sourceRow}`
        const attendance = `${excelColumnLetter(daysIndex)}${rows[0].sourceRow}`, denominator = '(?:[0-9]+|[A-Z]+[0-9]+)'
        const daily = new RegExp(`^(?:ROUND(?:UP|DOWN)?\\()?${reference}\\*${attendance}(?:,0\\))?$`).test(sample)
        const monthly = new RegExp(`^(?:ROUND(?:UP|DOWN)?\\()?${reference}(?:/${denominator}\\*${attendance}|\\*${attendance}/${denominator})(?:,0\\))?$`).test(sample)
        if (daily || monthly) {
          inputToCode.set(rate.columnIndex, code)
          if (daily) dailyRates.add(code)
        }
      }
    }
  }
  const translate = (raw: string, row: ExcelPayslipRow, expanding = new Set<number>()): string => {
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
      if (cell?.error || cell?.missingCachedValue || cell?.calculationError) throw new Error(`Review source column ${letters}.`)
      if (cell?.formulaText) {
        if (expanding.has(index) || expanding.size >= 20) throw new Error('Review the circular or deeply nested formula dependency.')
        return `(${translate(cell.formulaText, row, new Set([...expanding, index]))})`
      }
      if (value === undefined) throw new Error(`Review source column ${letters}.`)
      return String(value)
    })
    if (/!|\[|IF\(|VLOOKUP|SUM\(/.test(formula) || [...formula.matchAll(/\b([A-Z][A-Z0-9_]*)\(/g)].some(match => !['MIN', 'MAX', 'ROUND', 'ROUNDUP', 'ROUNDDOWN'].includes(match[1]))) throw new Error('This formula needs an explicit payroll rule.')
    if (formula.length > 1000) throw new Error('Split this long formula into reviewed salary components.')
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
      category, componentRole: role, statutoryType: statutory, calculationType: periodAdjustment ? 'Manual / Variable' : dailyRates.has(code) ? 'Daily Rate' : input !== undefined ? 'Fixed Amount' : 'Formula',
      value: '0', formula: input !== undefined || periodAdjustment ? '' : Object.values(rowFormulas).find(value => value && value !== '0') || '0', baseComponent: '',
      proRata: input !== undefined, taxable: master ? master.taxable : category === 'Earning', priority: master ? payrollSetupMasterPriority(master.priority) ?? Number.NaN : 100 + column.columnIndex * 10, payType: master ? master.payType : periodAdjustment ? 'Variable Pay' : 'Fixed Pay', sourceColumnIndex: input ?? (periodAdjustment ? column.columnIndex : undefined),
      expectedColumnIndex: selfMonthly.has(code) ? undefined : column.columnIndex, isPeriodAdjustment: periodAdjustment, exemptWhenSourceZero: false, blankInputAsZero: false, ...(Object.keys(rowFormulas).length ? { rowFormulas } : {}) })
  }
  const decisions = rows.map(row => {
    const classified = context.roster?.rows.find(item => item.rowId === row.id)
    if (classified) {
      if (classified.status !== 'Existing' && classified.status !== 'Applied') notes.push(`${row.employeeName}: ${classified.message}`)
      return { rowId: row.id, matchedEmployeeId: classified.status === 'Existing' ? classified.employeeId ?? undefined : undefined, allowCreate: false }
    }
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
  const suggested = context.defaults
  const sharedDivisor = periodIndex < 0 ? sharedPayrollDivisor(source, daysIndex) : null
  const fixedDays = periodIndex < 0 ? sharedDivisor?.days ?? payslipAttendanceBasis(source, daysIndex < 0 ? undefined : daysIndex).fixedDays : undefined
  const selectionKey = [...rowIds].sort().join('|').split('').reduce((value, char) => Math.imul(value ^ char.charCodeAt(0), 16777619) >>> 0, 2166136261).toString(16)
  const request: PayrollSetupRequest = { batchId: batch.id, rowIds: rows.map(r => r.id), templateName: `${(batch.clientName || 'Client').slice(0, 70)} ${batch.month} Excel ${batch.id.slice(0, 6)} ${selectionKey}`, effectiveFrom: `${batch.month}-01`,
    payrollDaysColumnIndex: periodIndex, fixedPayrollDays: fixedDays, payrollDaysCell: sharedDivisor?.cell, sourceColumnRoles: Object.fromEntries(source.columns.map(column => [column.columnIndex, column.kind])), payableDaysColumnIndex: daysIndex, workWeek: suggested?.workWeek || context.workWeeks.find(w => normalized(w.label) === 'SUNDAYOFF')?.value || context.workWeeks[0]?.value || 'Sunday off',
    attendanceCycleStartDay: suggested?.attendanceCycleStartDay ?? 1, attendanceCycleEndDay: suggested?.attendanceCycleEndDay ?? new Date(Number(batch.month.slice(0,4)), Number(batch.month.slice(5,7)), 0).getDate(), payrollReportGenerationDay: suggested?.payrollReportGenerationDay ?? 3, defaultDepartment: suggested?.department || '', defaultDesignation: suggested?.designation || '', defaultGrade: suggested?.grade || '', defaultWorkLocationName: context.workLocations.length === 1 ? context.workLocations[0].name : 'Head office', sourceWorkLocationColumnIndex: locationIndex, defaultWorkLocationState: suggested?.workLocationState || '', createMissingWorkLocations: true, createMissingDropdownValues: true, preserveExistingAttendancePolicies: true,
    components, employeeDecisions: decisions, editableDefaults: { leave: { status: 'Draft', quotaApproved: false, proposedAnnualPaidLeave: 0, active: false },
      holiday: { status: 'Draft', year: Number(batch.month.slice(0, 4)), dates: [] }, shift: { status: 'Draft', requiredForMonthlyTotals: false }, attendance: { mode: 'Monthly', payableDaysSource: 'Uploaded salary workbook' } } }
  if (suggested) notes.push(suggested.attendanceSource, ...suggested.notes)
  if (periodIndex < 0) notes.push(fixedDays ? `Saved formulas use a fixed ${fixedDays}-day divisor. Review this basis; later payroll months retain it until you change the client policy.` : 'Select the calculation-days column or enter a reviewed fixed divisor. No attendance days are invented.')
  if (daysIndex < 0) notes.push('Select the column containing payable days.')
  if (locations.length !== 1) notes.push('Select one source work-location column before matching or creating employees.')
  return { request, notes: [...new Set(notes)] }
}
