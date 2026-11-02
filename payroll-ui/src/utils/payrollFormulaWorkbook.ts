import type { FormulaWorkbookEmployee, FormulaWorkbookRequest, SimulationLine } from '../services/payrollCalculationService'
import type { XlsxCell, XlsxSheet } from './xlsx'

const letter = (index: number) => { let name = ''; for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) name = String.fromCharCode(65 + (n - 1) % 26) + name; return name }
const earning = (line: SimulationLine) => ['Earning', 'Reimbursement'].includes(line.category)
const sum = (cells: string[]) => cells.length ? `SUM(${cells.join(',')})` : '0'
// Excel ROUND uses away-from-zero ties; currency stages in the engine use to-even.
// The small tie guard accounts for binary spreadsheet representation at a cent.
export const excelCurrencyRound = (value: string) => `IF(ABS(ABS((${value})*100-ROUND((${value})*100,0))-0.5)<1E-10,ROUND((${value})*100/2,0)*2,ROUND((${value})*100,0))/100`
const formula = (text: string, value: number | string): XlsxCell => ({ formula: text, value, type: typeof value === 'number' ? 'number' : 'text', style: 'currency' })
const input = (value: number | string): XlsxCell => ({ value, style: 'input' })
const functions = new Set(['IF', 'IFERROR', 'AND', 'OR', 'SUM', 'MIN', 'MAX', 'ABS', 'ROUND', 'ROUNDUP', 'ROUNDDOWN', 'TRUE', 'FALSE'])

export function preparePayrollFormulaWorkbookRequest(clientId:number,payPeriod:string,policy:{id:string;groupIds:number[];employeeIds:number[]}|null,employeeIds:number[]):FormulaWorkbookRequest {
  if(!clientId||!policy?.id.trim()||!policy.groupIds.length)throw Error('Select an attendance policy before downloading Excel with formulas.')
  if(!employeeIds.length||employeeIds.length>1000||new Set(employeeIds).size!==employeeIds.length||employeeIds.some(id=>id<=0||!policy.employeeIds.includes(id)))
    throw Error('Select employees from the chosen attendance policy before downloading Excel with formulas.')
  return {clientId,payPeriod,attendancePolicyBatchId:policy.id,attendanceGroupIds:[...policy.groupIds],employeeIds:[...employeeIds]}
}

export function translatePayrollExcelFormula(source: string, values: Record<string, string>) {
  const normalized = source.trim().replace(/^=/, '').toUpperCase().replace(/×/g, '*').replace(/÷/g, '/')
    .replace(/SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)/g, (_, fixed) => fixed ? 'FIXED_EARNINGS_BEFORE_THIS' : 'EARNINGS_BEFORE_THIS')
    .replace(/(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)/g, '$2*$1/100')
  if (!normalized || /[!\[\]"';{}]/.test(normalized)) throw Error('A payroll rule cannot be represented as a bounded local Excel formula.')
  return normalized.replace(/\b[A-Z_][A-Z0-9_]*\b/g, token => {
    if (values[token] !== undefined) return `(${values[token]})`
    if (functions.has(token)) return token
    throw Error(`Formula value ${token} is unavailable; no amount was frozen or guessed.`)
  })
}

function slabFormula(source: string, base: string) {
  let result = '0'
  for (const slab of source.split(';').filter(Boolean).reverse()) {
    const parts = slab.trim().split(':')
    if (parts.length !== 2 || !Number.isFinite(Number(parts[1]))) throw Error('Review the configured salary slab before exporting formulas.')
    const range = parts[0].trim().match(/^(\d+(?:\.\d+)?)(?:-(\d+(?:\.\d+)?)|(\+))$/)
    if (!range) throw Error('The configured salary slab cannot be represented in Excel.')
    const condition = range[3] ? `${base}>=${range[1]}` : `AND(${base}>=${range[1]},${base}<=${range[2]})`
    result = `IF(${condition},${Number(parts[1])},${result})`
  }
  return result
}

export function buildPayrollFormulaWorkbook(clientName: string, period: string, employees: FormulaWorkbookEmployee[]): XlsxSheet[] {
  if (!employees.length || new Set(employees.map(row => row.employeeId)).size !== employees.length) throw Error('Select a distinct employee roster.')
  const keys = new Map<string, { code: string; name: string }>()
  const keyed = employees.map(employee => {
    const seen = new Map<string, number>()
    const lines = employee.calculation.lines.map(line => {
      const base = `${line.code}:${line.componentId || line.source}`
      const count = seen.get(base) || 0; seen.set(base, count + 1)
      const key = `${base}:${count}`
      if (!keys.has(key)) keys.set(key, { code: line.source === 'Approved period adjustment' ? `${line.code} adjustment` : line.code, name: line.name })
      return { line, key }
    })
    return { ...employee, lines }
  })
  const positions = new Map([...keys.keys()].map((key, i) => [key, 15 + i * 3]))
  const headers: XlsxCell[] = ['Employee code', 'Employee name', 'Template', 'Annual CTC input', 'Monthly CTC', 'Payroll days input', 'Present days input', 'Payable days input', 'Input check', 'Gross earnings', 'Employee deductions', 'Employer contributions', 'Net pay', 'Engine net at download', 'Net change from download']
  for (const item of keys.values()) headers.push(`${item.code} input / raw`, `${item.code} monthly`, `${item.code} earned`)
  if(headers.length>16384)throw Error('Too many component columns for Excel. Select a smaller roster.')
  const rows: XlsxCell[][] = [headers.map(cell => ({ value: String(cell), style: 'header' }))]
  const rules: XlsxCell[][] = [
    ['Payroll formula workbook', clientName, period],
    ['Change Payroll days, Present days, Payable days and highlighted amount/rate inputs on Payroll. Formula cells recalculate in Excel.'],
    ['Select a calculated cell, including PF, ESIC or net pay, to see its Excel formula in the formula bar.'],
    ['Engine net at download is a fixed comparison to the portal at export time. Changing this file does not save attendance or run payroll.'],
    ['Payroll formulas use the configured rules, employee overrides and approved regular adjustments. Employer contributions do not reduce net pay.'],
    ['Currency stages preserve engine midpoint-to-even rounding. ROUND inside a configured rule retains Excel/native away-from-zero rounding.'],
    ['Client', 'Template', 'Component', 'Category', 'Calculation', 'Configured formula', 'Prorate', 'Amount source'],
  ]
  const ruleKeys = new Set<string>()
  for (const employee of keyed) {
    const calculation = employee.calculation, rowNumber = rows.length + 1
    if (!calculation.complete || calculation.net == null || calculation.inputs.some(value => ['PAYROLL_DAYS', 'PAYABLE_DAYS'].includes(value.name) && value.value == null)) throw Error(`${employee.employeeCode}: complete native attendance and payroll calculation are required.`)
    const get = (token: string) => calculation.inputs.find(value => value.name === token)?.value
    const nativeAnnual = get('ANNUAL_CTC')
    if (nativeAnnual == null) throw Error(`${employee.employeeCode}: annual salary input is unavailable.`)
    const row: XlsxCell[] = Array.from({ length: headers.length }, () => '')
    row[0] = { value: employee.employeeCode, style: 'text' }; row[1] = employee.employeeName; row[2] = calculation.templateId
    row[3] = input(nativeAnnual)
    const target = calculation.lines.find(line => line.monthlyTarget != null)?.monthlyTarget ?? nativeAnnual / 12
    row[4] = nativeAnnual > 0 ? formula(excelCurrencyRound(`D${rowNumber}/12`), target) : input(target)
    row[5] = input(get('PAYROLL_DAYS')!); row[6] = input(get('PRESENT_DAYS') ?? ''); row[7] = input(get('PAYABLE_DAYS')!)
    const days = `F${rowNumber}`, present = `G${rowNumber}`, payable = `H${rowNumber}`
    const needsPresence = employee.lines.some(({ line }) => /\bPRESENT_DAYS\b/i.test(line.formula) && !line.amountOverride)
    const valid = `AND(COUNT(${days},${payable})=2,${days}>=1,${days}<=31,${days}=ROUND(${days},0),${payable}>=0,${payable}<=${employee.reviewedExtraDutyUnits ? '999.99' : days},${needsPresence ? `COUNT(${present})=1` : `OR(${present}="",COUNT(${present})=1)`},${present}>=0,${present}<=${days})`
    row[8] = formula(`IF(${valid},"Ready","Check attendance")`, 'Ready')
    const guarded = (expression: string) => `IF(I${rowNumber}="Ready",${expression},NA())`
    const monthlyEarnings: string[] = [], earnedEarnings: string[] = [], fixedEarnings: string[] = [], deductions: string[] = [], benefits: string[] = []
    const tokens: Record<string, string> = {
      CTC: `E${rowNumber}`, GROSS: `E${rowNumber}`, MONTHLY_CTC: `E${rowNumber}`, ANNUAL_CTC: `E${rowNumber}*12`,
      PAYROLL_DAYS: days, TOTAL_DAYS: days, WORKING_DAYS: days, PRESENT_DAYS: present, PAYABLE_DAYS: payable, LOP_DAYS: `MAX(0,${days}-${payable})`,
    }
    for (const { key, line } of employee.lines) {
      if (line.source.includes('Effective-dated PF') || line.source.startsWith('Projected TDS')) throw Error(`${line.code}: offline export cannot replace dated policy calculations.`)
      const position = positions.get(key)!, rawCell = `${letter(position)}${rowNumber}`, monthly = `${letter(position + 1)}${rowNumber}`, earned = `${letter(position + 2)}${rowNumber}`
      Object.assign(tokens, { EARNINGS_BEFORE_THIS: sum(monthlyEarnings), MONTHLY_EARNINGS_BEFORE_THIS: sum(monthlyEarnings), FIXED_EARNINGS: sum(fixedEarnings), FIXED_EARNINGS_BEFORE_THIS: sum(fixedEarnings), GROSS_EARNED: sum(earnedEarnings), EARNED_EARNINGS_BEFORE_THIS: sum(earnedEarnings) })
      let expression: string | null = null
      if (line.dailyRate != null) row[position] = input(line.dailyRate)
      else if (line.amountOverride || ['Fixed Amount', 'Manual / Variable', ''].includes(line.calculationType || '')) row[position] = input(line.raw)
      else if (line.calculationType === 'Slab Based') expression = slabFormula(line.formula, tokens.GROSS_EARNED)
      else if (line.calculationType === 'Residual / Balancing' && !/SUM/i.test(line.formula)) expression = `MAX(0,${translatePayrollExcelFormula(line.baseComponent || 'GROSS', tokens)}-${tokens.EARNINGS_BEFORE_THIS})`
      else expression = translatePayrollExcelFormula(line.formula, tokens)
      if (expression != null) row[position] = formula(guarded(expression), line.raw)
      row[position + 1] = formula(guarded(`MAX(0,${excelCurrencyRound(line.dailyRate != null ? `${rawCell}*${days}` : rawCell)})`), line.monthly)
      row[position + 2] = formula(guarded(excelCurrencyRound(line.dailyRate != null ? `${rawCell}*${payable}` : line.proRata ? `${monthly}*${payable}/${days}` : monthly)), line.earned)
      if (earning(line)) { monthlyEarnings.push(monthly); earnedEarnings.push(earned); if (line.payType === 'Fixed Pay') fixedEarnings.push(monthly) }
      else if (line.category === 'Deduction') deductions.push(earned)
      else if (line.category === 'Benefit') benefits.push(earned)
      // Adjustments are separate entries and never overwrite recurring formula dependencies.
      if (line.componentId) for (const alias of [line.code.toUpperCase(), line.componentId.toUpperCase()]) Object.assign(tokens, { [alias]: monthly, [`${alias}_MONTHLY`]: monthly, [`${alias}_EARNED`]: earned })
      const rule = [clientName, calculation.templateId, line.code, line.category, line.calculationType || 'Fixed period input', line.formula, line.proRata ? 'Yes' : 'No', line.source]
      const signature = JSON.stringify(rule)
      if (!ruleKeys.has(signature)) { rules.push(rule); ruleKeys.add(signature) }
    }
    row[9] = formula(guarded(sum(earnedEarnings)), calculation.gross!); row[10] = formula(guarded(sum(deductions)), calculation.deductions!)
    row[11] = formula(guarded(sum(benefits)), calculation.benefits!); row[12] = formula(guarded(`MAX(0,J${rowNumber}-K${rowNumber})`), calculation.net)
    row[13] = { value: calculation.net, style: 'currency' }; row[14] = formula(guarded(excelCurrencyRound(`M${rowNumber}-N${rowNumber}`)), 0)
    rows.push(row)
  }
  return [
    { name: 'Payroll', rows, freezeRows: 1, freezeColumns: 2, columnWidths: headers.map((_, index) => index === 1 ? 32 : index === 0 ? 19 : index === 2 ? 23 : 19) },
    { name: 'Rules', rows: rules, columnWidths: [24, 24, 22, 18, 23, 90, 12, 55] },
  ]
}
