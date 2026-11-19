import type { ExcelPayslipBatch, ExcelPayslipCalculationSource, ExcelPayslipRow } from '../services/excelPayslipService'
import type { PayrollSetupComponent, PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter, parsePayslipAmount } from './excelPayslipImport'
import { payslipAttendanceBasis } from './excelPayslipAttendance'
import { sharedPayrollDivisor } from './payrollSourceEvidence'
import { payrollSourceColumns } from './excelPayrollColumns'
import { evaluatePayrollFormula, inspectPayrollFormula } from './payrollFormula'

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

// Draft recognition only. The API independently evaluates the preserved entry
// expression with decimal arithmetic after the explicit fixed-input review.
// Never treat a cell-reference/lookup cache as a recurring monthly salary input.
function numericEntryInput(cell: ExcelPayslipCalculationSource['rows'][number]['cells'][number] | undefined) {
  if (!cell || cell.dataType !== 'Number' || cell.error || cell.calculationError || cell.missingCachedValue) return false
  const formula = cell.formulaText?.trim().replace(/^=/, '') || ''
  if (!formula || formula.length > 1000 || !/^[0-9eE.\s()+*/%\-]+$/.test(formula)) return false
  try {
    const value = evaluatePayrollFormula(formula, {}), cache = Number(cell.value)
    return !!cell.value.trim() && Number.isFinite(cache) && value === cache && value >= 0 && value <= 1_000_000_000 && Number.isInteger(value * 10000)
  } catch { return false }
}

// Resolve only the specific blank ESIC auxiliary/output collision. The saved
// payslip/source stays intact; complete numeric controls must identify the real
// deduction and exclude the blank column in every selected row.
function blankAuxiliaryEsicColumns(source: ExcelPayslipCalculationSource, rows: ExcelPayslipRow[], roles: Record<number,string>) {
  const deductions=source.columns.filter(column=>column.kind==='deduction'&&componentCode(column.sourceHeader||column.label)==='ESIC')
  const totals=source.columns.filter(column=>column.kind==='deductionTotal'),nets=source.columns.filter(column=>column.kind==='netPay')
  const result=new Set<number>()
  if(!rows.length||deductions.length<2||totals.length!==1||nets.length!==1)return result
  for(const candidate of deductions){
    if(candidate.componentId||Object.prototype.hasOwnProperty.call(roles,candidate.columnIndex))continue
    if(!rows.every(row=>{
      const captured=source.rows.filter(item=>item.sourceRow===row.sourceRow),cell=captured.length===1?captured[0].cells[candidate.columnIndex]:undefined
      const amounts=(row.deductions||[]).filter(line=>normalized(line.label)===normalized(candidate.label))
      return cell?.dataType==='Blank'&&cell.value===''&&!cell.formula&&!cell.formulaText&&!cell.error&&!cell.calculationError&&!cell.missingCachedValue&&amounts.length===1&&amounts[0].amount===0
    }))continue
    const actual=deductions.filter(column=>column.columnIndex!==candidate.columnIndex&&rows.every(row=>{
      const cells=source.rows.filter(item=>item.sourceRow===row.sourceRow),cell=cells.length===1?cells[0].cells[column.columnIndex]:undefined
      return cell?.dataType==='Number'&&!!cell.formulaText&&!cell.error&&!cell.calculationError&&!cell.missingCachedValue&&numeric(cell.value)!==undefined
    }))
    if(actual.length!==1)continue
    const proof=rows.every(row=>{
      const captured=source.rows.filter(item=>item.sourceRow===row.sourceRow)
      if(captured.length!==1)return false
      const graph=(index:number,seen=new Set<number>()):Set<number>|null=>{
        if(index<0||index>=source.columnCount||seen.has(index)||seen.size>=20)return null
        const metadata=source.columns.filter(column=>column.columnIndex===index),cell=captured[0].cells[index]
        if(metadata.length!==1||!cell||cell.error||cell.calculationError||cell.missingCachedValue)return null
        if(['info','ignore'].includes(metadata[0].kind)&&/employee|\bemp\b|uan|pan|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|birth|joining|e-?mail|\bdob\b/i.test(metadata[0].sourceHeader+' '+metadata[0].label))return null
        const visited=new Set([index])
        if(!cell.formulaText)return cell.dataType==='Number'&&numeric(cell.value)!==undefined||cell.dataType==='Blank'&&cell.value===''?visited:null
        if(cell.dataType!=='Number'||numeric(cell.value)===undefined)return null
        const formula=expression(cell.formulaText)
        if(/[!\[\]":]/.test(formula))return null
        const refs=[...formula.matchAll(/\b([A-Z]{1,3})([1-9]\d*)\b/g)]
        if(refs.some(match=>Number(match[2])!==row.sourceRow))return null
        try{if(inspectPayrollFormula(formula.replace(/\b([A-Z]{1,3})([1-9]\d*)\b/g,'1')).variables.length)return null}catch{return null}
        for(const ref of refs){const child=graph(letterIndex(ref[1]),new Set([...seen,index]));if(!child)return null;for(const dependency of child)visited.add(dependency)}
        return visited
      }
      const total=graph(totals[0].columnIndex),net=graph(nets[0].columnIndex),actualGraph=graph(actual[0].columnIndex)
      return !!total&&!!net&&!!actualGraph&&total.has(actual[0].columnIndex)&&net.has(actual[0].columnIndex)&&!total.has(candidate.columnIndex)&&!net.has(candidate.columnIndex)&&!actualGraph.has(candidate.columnIndex)
    })
    if(proof)result.add(candidate.columnIndex)
  }
  return result
}

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

// Preserve native IF and its complete dependency expression. Only exact cap and
// nonnegative-day normalisations shared by the server may rewrite its shape.
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
  return text
}

export function proposeExcelPayrollSetup(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[], roles: Record<number, string> = {}) {
  const rows = batch.rows.filter(row => rowIds.includes(row.id)), notes: string[] = []
  const source = { ...context.source, columns: payrollSourceColumns(context.source, roles, rows.map(row => row.sourceRow)) }
  const blankEsic=blankAuxiliaryEsicColumns(source,rows,roles)
  if(blankEsic.size){source.columns=source.columns.map(column=>blankEsic.has(column.columnIndex)?{...column,kind:'info' as const}:column);notes.push('A blank auxiliary ESIC field is kept as Information; the captured deduction total and net formulas identify the separate monetary ESI output.')}
  const dayColumn = source.columns.find(c => /^(?:WORKINGDAYS?|TOTALWORKINGDAYS?|ATTENDANCE|ATTEDANCE|PAIDDAYS?|PAYABLEDAYS?|PAYBLEDAYS?|NUMBEROFWORKINGDAYSATTENDED)$/.test(normalized(c.sourceHeader)))
  // A present-day heading is a separate candidate, never a substitute for paid
  // salary units. Its actual-worked meaning still needs the attendance review.
  const presentColumns = source.columns.filter(c => /^(?:ACTUALPRESENTDAYS?|PRESENTDAYS?|DAYSPRESENT)$/.test(normalized(c.sourceHeader)))
  const presentIndex = presentColumns.length === 1 ? presentColumns[0].columnIndex : undefined
  const periodColumn = source.columns.find(c => /^(?:MONTH|MONTHDAYS|PAYROLLDAYS|CALCULATIONDAYS|TOTALDAYS)$/.test(normalized(c.sourceHeader)) && rows.every(row => { const value = numeric(sourceCell(source, row.sourceRow, c.columnIndex)?.value); return value !== undefined && value >= 1 && value <= 31 }))
  const daysIndex = dayColumn?.columnIndex ?? -1, periodIndex = periodColumn?.columnIndex ?? -1
  const locations = source.columns.filter(column => /^(?:WORKLOCATION.*|LOCATION|LOCATIONNAME.*|PLACEOFPOSTING|POSTING)$/.test(normalized(column.sourceHeader)))
  const locationIndex = locations.length === 1 ? locations[0].columnIndex : undefined
  const outputColumns = source.columns.filter(c => ['earning', 'deduction', 'employer'].includes(c.kind))
  // A bounded v2 proposal names the exact typed-blank operand rows. It is not a
  // zero approval: the API proves the full source and each cell after Check.
  const blankOperands = new Map<number, Set<string>>()
  if (context.rowScopedRecurringBlankInputReviewSupported) for (const row of rows) {
    const captured = source.rows.filter(item => item.sourceRow === row.sourceRow)
    if (captured.length !== 1) continue
    const visit = (index: number, seen = new Set<number>()) => {
      if (seen.has(index) || seen.size >= 20) return
      const cell = captured[0].cells[index], metadata = source.columns.filter(column => column.columnIndex === index)
      if (!cell || metadata.length !== 1 || cell.error || cell.calculationError || cell.missingCachedValue) return
      if (cell.formulaText) {
        const formula = expression(cell.formulaText)
        if (/[!\[\]"]/.test(formula) || [...formula.matchAll(/\b([A-Z][A-Z0-9_]*)\(/g)].some(match => !['SUM','MIN','MAX','ROUND','ROUNDUP','ROUNDDOWN','IF'].includes(match[1]))) return
        const refs = [...formula.matchAll(/\b([A-Z]+)(\d+)\b/g)]
        if (refs.some(match => Number(match[2]) !== row.sourceRow)) return
        for (const match of refs) visit(letterIndex(match[1]), new Set([...seen,index]))
      } else if (cell.dataType === 'Blank' && cell.value === '' && ['info','ignore'].includes(metadata[0].kind)
        && ![periodIndex,daysIndex,presentIndex,locationIndex].includes(index) && !outputColumns.some(column=>column.columnIndex===index)
        && !/employee|\bemp\b|uan|pan|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|birth|joining|e-?mail|earned|net[\s_-]*(?:pay|salary)|\b(?:days?|hours?|attendance|ctc|salary|total|sub\s*total|gross|lwp|lop|id|code|serial|sr\s*no|name|location|city|state)\b/i.test(metadata[0].sourceHeader+' '+metadata[0].label)) {
        const ids=blankOperands.get(index)||new Set<string>();ids.add(row.id);blankOperands.set(index,ids)
      }
    }
    for (const output of outputColumns) visit(output.columnIndex)
  }
  const blankGroups = new Set([...blankOperands.values()].map(ids=>JSON.stringify([...ids].sort())))
  const recurringBlankInputReview: PayrollSetupRequest['recurringBlankInputReview'] = blankOperands.size>0 && blankOperands.size<=10 && blankGroups.size===1 ? {
    version:2,reviewed:false,sourceHash:'',columnIndices:[...blankOperands.keys()].sort((a,b)=>a-b),rowIds:[...(blankOperands.values().next().value || [])].sort(),
    reason:'Reviewed typed-blank increment operands in the displayed source rows retain Excel numeric zero semantics.',
  } : undefined
  const codes = new Map<number, string>(), components: PayrollSetupComponent[] = []
  for (const column of outputColumns) {
    let code = componentCode(column.sourceHeader || column.label)
    if (column.kind === 'employer') code = code === 'PF' ? 'EPF_ER' : code === 'ESIC' ? 'ESIC_ER' : code
    if ([...codes.values()].includes(code)) code += `_${excelColumnLetter(column.columnIndex)}`
    codes.set(column.columnIndex, code)
  }
  const inputToCode = new Map<number, string>(), selfMonthly = new Set<string>(), dailyRates = new Set<string>(), numericEntryColumns = new Set<number>()
  for (const column of outputColumns.filter(c => c.kind === 'earning')) {
    const code = codes.get(column.columnIndex)!
    const matching = source.columns.filter(c => ['info', 'ignore'].includes(c.kind) && componentCode(c.sourceHeader) === code
      && !/^(ER|EARNED)/.test(normalized(c.sourceHeader))
      && !/employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|(?:birth|joining)\s*date/i.test(c.sourceHeader))
    if (matching.length === 1 && rows.every(row => {
      const captured = source.rows.filter(item => item.sourceRow === row.sourceRow)
      const cell = captured.length === 1 ? captured[0].cells[matching[0].columnIndex] : undefined
      return cell && !cell.error && !cell.calculationError && !cell.missingCachedValue && (cell.formulaText?.trim() ? numericEntryInput(cell) : numeric(cell.value) !== undefined || !cell.value.trim())
    })) {
      inputToCode.set(matching[0].columnIndex, code)
      if (rows.some(row => sourceCell(source, row.sourceRow, matching[0].columnIndex)?.formulaText?.trim())) numericEntryColumns.add(matching[0].columnIndex)
    }
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
      if (recurringBlankInputReview?.columnIndices.includes(index) && recurringBlankInputReview.rowIds?.includes(row.id)) return '0'
      if (cell?.formulaText) {
        if (expanding.has(index) || expanding.size >= 20) throw new Error('Review the circular or deeply nested formula dependency.')
        return `(${translate(cell.formulaText, row, new Set([...expanding, index]))})`
      }
      if (value === undefined) throw new Error(`Review source column ${letters}.`)
      return String(value)
    })
    if (/!|\[|VLOOKUP|SUM\(/.test(formula) || [...formula.matchAll(/\b([A-Z][A-Z0-9_]*)\(/g)].some(match => !['MIN', 'MAX', 'ROUND', 'ROUNDUP', 'ROUNDDOWN', 'IF'].includes(match[1]))) throw new Error('This formula needs an explicit payroll rule.')
    if (formula.length > 4096) throw new Error('Split formulas longer than 4096 characters into reviewed salary components.')
    return formula
  }
  for (const column of outputColumns) {
    const code = codes.get(column.columnIndex)!, category = column.kind === 'employer' ? 'Benefit' : column.kind === 'deduction' ? 'Deduction' : code === 'TA_DA' ? 'Reimbursement' : 'Earning'
    const master = context.components.find(c => normalized(c.code) === normalized(code) && c.category === category)
    const monthlyInput = [...inputToCode].find(([, value]) => value === code)?.[0]
    const literalOutput = monthlyInput === undefined && rows.every(row => { const cell = sourceCell(source, row.sourceRow, column.columnIndex); return !cell?.formulaText && !cell?.error && !cell?.calculationError && !cell?.missingCachedValue && (numeric(cell?.value) !== undefined || column.blankAsZero && !cell?.value.trim()) })
    const input = monthlyInput ?? (literalOutput ? column.columnIndex : undefined)
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
      proRata: monthlyInput !== undefined, taxable: master ? master.taxable : category === 'Earning', priority: master ? payrollSetupMasterPriority(master.priority) ?? Number.NaN : 100 + column.columnIndex * 10, payType: master ? master.payType : periodAdjustment ? 'Variable Pay' : 'Fixed Pay', sourceColumnIndex: input ?? (periodAdjustment ? column.columnIndex : undefined),
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
  const request: PayrollSetupRequest = { updateExistingConfiguration: true, batchId: batch.id, rowIds: rows.map(r => r.id), templateName: `${(batch.clientName || 'Client').slice(0, 70)} salary template`, effectiveFrom: `${batch.month}-01`,
    payrollDaysColumnIndex: periodIndex, fixedPayrollDays: fixedDays, payrollDaysCell: sharedDivisor?.cell, sourceColumnRoles: Object.fromEntries(source.columns.map(column => [column.columnIndex, numericEntryColumns.has(column.columnIndex) || recurringBlankInputReview?.columnIndices.includes(column.columnIndex) ? 'info' : column.kind])), payableDaysColumnIndex: daysIndex, presentDaysColumnIndex: presentIndex, workWeek: suggested?.workWeek || context.workWeeks.find(w => normalized(w.label) === 'SUNDAYOFF')?.value || context.workWeeks[0]?.value || 'Sunday off',
    attendanceCycleStartDay: suggested?.attendanceCycleStartDay ?? 1, attendanceCycleEndDay: suggested?.attendanceCycleEndDay ?? new Date(Number(batch.month.slice(0,4)), Number(batch.month.slice(5,7)), 0).getDate(), payrollReportGenerationDay: suggested?.payrollReportGenerationDay ?? 3, defaultDepartment: suggested?.department || '', defaultDesignation: suggested?.designation || '', defaultGrade: suggested?.grade || '', defaultWorkLocationName: context.workLocations.length === 1 ? context.workLocations[0].name : 'Head office', sourceWorkLocationColumnIndex: locationIndex, defaultWorkLocationState: suggested?.workLocationState || '', createMissingWorkLocations: true, createMissingDropdownValues: true, preserveExistingAttendancePolicies: true,
    components, employeeDecisions: decisions, ...(recurringBlankInputReview ? {recurringBlankInputReview} : {}), editableDefaults: { leave: { status: 'Draft', quotaApproved: false, proposedAnnualPaidLeave: 0, active: false },
      holiday: { status: 'Draft', year: Number(batch.month.slice(0, 4)), dates: [] }, shift: { status: 'Draft', requiredForMonthlyTotals: false }, attendance: { mode: 'Monthly', payableDaysSource: 'Uploaded salary workbook' } } }
  if (suggested) notes.push(suggested.attendanceSource, ...suggested.notes)
  if (periodIndex < 0) notes.push(fixedDays ? `Saved formulas use a fixed ${fixedDays}-day divisor. Review this basis; later payroll months retain it until you change the client policy.` : 'Select the calculation-days column or enter a reviewed fixed divisor. No attendance days are invented.')
  if (daysIndex < 0) notes.push('Select the column containing payable days.')
  if (locations.length !== 1) notes.push('Select one source work-location column before matching or creating employees.')
  return { request, notes: [...new Set(notes)] }
}
