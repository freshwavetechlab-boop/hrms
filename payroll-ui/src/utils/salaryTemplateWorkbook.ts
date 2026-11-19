import type { Component, Structure } from '../types/payroll'
import { calculateSalaryDetails } from './salary'
import { roundPayroll } from './payrollFormula'
import { salaryTemplateClientIds } from './salaryTemplateContext'
import { excelColumnLetter, payslipTemplateMarker, type PayslipColumn } from './excelPayslipImport'
import { excelCurrencyRound, slabFormula, translatePayrollExcelFormula } from './payrollFormulaWorkbook'
import type { XlsxCell, XlsxSheet } from './xlsx'

type Context = { clientId: number; clientName: string; month: string; payrollDays: number; locationLabel?: string; rowCount?: number }
const sum = (cells: string[]) => cells.length ? `SUM(${cells.join(',')})` : '0'
const numeric = (value: string) => value.trim() && Number.isFinite(Number(value)) && Number(value) >= 0 ? Number(value) : ''
const formula = (expression: string): Extract<XlsxCell, object> => ({ value: '', type: 'number', formula: expression, missingCachedValue: true, style: 'currency' })
const input = (value: string | number = ''): XlsxCell => ({ value, style: 'input' })

/** A reusable configuration sheet, never a payroll/attendance roster or a salary snapshot. */
export function buildSalaryTemplateWorkbook(template: Structure, components: Component[], context: Context): XlsxSheet[] {
  const clientIds = salaryTemplateClientIds(template.clientId)
  if (!context.clientId || !context.clientName.trim() || (template.clientId.trim() && !clientIds.includes(context.clientId))) throw Error('Choose a saved template belonging to the selected client.')
  if (!template.id || !template.lines.length || !/^\d{4}-(0[1-9]|1[0-2])$/.test(context.month) || !Number.isInteger(context.payrollDays) || context.payrollDays < 1 || context.payrollDays > 31) throw Error('Choose a saved salary template, valid month and payroll divisor.')
  if (template.excelSource?.workLocationLabel && context.locationLabel?.trim() !== template.excelSource.workLocationLabel) throw Error('This template belongs to another location. Choose its saved location or a client-default template.')
  if (new Set(template.lines.map(line => String(line.componentId))).size !== template.lines.length || template.lines.some(line => !components.some(component => component.active && String(component.id) === String(line.componentId)))) throw Error('The saved template contains duplicate, inactive or missing components. Review it before downloading.')
  const rows = calculateSalaryDetails(Number(template.annualCtc || 0), components, template)
  if (rows.some(row => row.error?.includes('Circular'))) throw Error('Circular salary formulas cannot be downloaded as an editable template.')
  const lines = rows.filter(row => !['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'].includes(row.component.code.toUpperCase()))
  if (!lines.length || lines.length > 100) throw Error('Choose a template containing between 1 and 100 salary components.')
  const requiresCtc = lines.some(line => /\b(?:CTC|GROSS|MONTHLY_CTC|ANNUAL_CTC)\b/i.test(`${line.source} ${line.line.baseComponent || line.component.baseComponent}`) || line.calculationType === 'Residual / Balancing' && !(line.line.baseComponent || line.component.baseComponent))
  const location = context.locationLabel?.trim() || ''
  const headers = ['Employee code', 'Employee name', 'Payroll days', 'Actual present days', 'Payable days', 'Annual CTC calculated', 'Monthly CTC', 'Input check']
  const mapping: PayslipColumn[] = headers.map((header, columnIndex) => ({ columnIndex, sourceHeader: header, label: header, kind: columnIndex === 0 ? 'employeeCode' : columnIndex === 1 ? 'employeeName' : columnIndex === 7 ? 'ignore' : 'info' }))
  const rules: XlsxCell[][] = [
    ['Salary template', template.name], ['Client', context.clientName], ['Location', location || 'Client default'], ['Month', context.month],
    ['Fill highlighted employee, days and amount/rate inputs. Blank inputs are not zero attendance.'],
    ['Add new columns and Excel formulas as needed. Recalculate and save in Excel, then use Salary Templates > Tools > Extract from Excel.'],
    ['This is a configuration worksheet. Downloading or editing it does not save employee data, assign a location, or run payroll.'],
    ['Component', 'Category', 'Calculation', 'Configured formula / input', 'Prorate'],
  ]
  for (const row of lines) {
    const { component } = row
    const header = row.calculationType === 'Daily Rate' ? `${component.name} daily rate` : `Monthly ${component.name}`
    const index = headers.length
    headers.push(header, component.name)
    const kind = ['Earning', 'Reimbursement'].includes(component.category) && component.componentRole !== 'Information' ? 'earning' : component.category === 'Deduction' ? 'deduction' : component.category === 'Benefit' ? 'employer' : 'info'
    mapping.push({ columnIndex: index, sourceHeader: header, label: header.slice(0, 80), kind: 'info' }, { columnIndex: index + 1, sourceHeader: component.name, label: component.name.slice(0, 80), kind, componentId: String(component.id) })
    rules.push([component.code, component.category, row.calculationType || 'Fixed Amount', row.source || 'Required employee input', row.proRata ? 'Yes' : 'No'])
  }
  const grossIndex = headers.length
  headers.push('Total earnings', 'Total deductions', 'Employer contributions', 'Net pay')
  for (const [offset, kind] of [[0, 'grossTotal'], [1, 'deductionTotal'], [2, 'info'], [3, 'netPay']] as const) mapping.push({ columnIndex: grossIndex + offset, sourceHeader: headers[grossIndex + offset], label: headers[grossIndex + offset], kind })
  const sheetRows: XlsxCell[][] = [
    ['Client', context.clientName, 'Location', location || 'Client default'],
    ['Salary template', template.name, 'Pay month', context.month],
    ['Fill highlighted inputs. Add columns/formulas, recalculate and save before uploading.'], [],
    headers.map(value => ({ value, style: 'header' })),
  ]
  for (let index = 0; index < Math.min(100, Math.max(1, context.rowCount ?? 10)); index++) {
    const n = index + 6, days = `C${n}`, present = `D${n}`, payable = `E${n}`, monthlyCtc = `G${n}`
    const row: XlsxCell[] = Array.from({ length: headers.length }, () => '')
    const annual = numeric(template.annualCtc)
    row[0] = { value: '', style: 'text' }; row[1] = input(); row[2] = input(context.payrollDays); row[3] = input(); row[4] = input(); row[5] = formula(`G${n}*12`)
    row[6] = input(annual === '' ? requiresCtc ? '' : 0 : roundPayroll(annual / 12))
    const monthlyEarnings: string[] = [], earnedEarnings: string[] = [], fixedEarnings: string[] = [], deductions: string[] = [], benefits: string[] = [], required = [days, payable]
    const tokens: Record<string, string> = { CTC: monthlyCtc, GROSS: monthlyCtc, MONTHLY_CTC: monthlyCtc, ANNUAL_CTC: `${monthlyCtc}*12`, PAYROLL_DAYS: days, TOTAL_DAYS: days, WORKING_DAYS: days, PRESENT_DAYS: present, PAYABLE_DAYS: payable, LOP_DAYS: `MAX(0,${days}-${payable})` }
    if (requiresCtc) required.push(`G${n}`)
    if (lines.some(line => /\bPRESENT_DAYS\b/i.test(line.source || ''))) required.push(present)
    for (const [position, item] of lines.entries()) {
      const { component, line, source = '', calculationType = '', proRata } = item
      const column = 8 + position * 2, monthly = `${excelColumnLetter(column)}${n}`, earned = `${excelColumnLetter(column + 1)}${n}`
      Object.assign(tokens, { EARNINGS_BEFORE_THIS: sum(monthlyEarnings), MONTHLY_EARNINGS_BEFORE_THIS: sum(monthlyEarnings), FIXED_EARNINGS: sum(fixedEarnings), FIXED_EARNINGS_BEFORE_THIS: sum(fixedEarnings), GROSS_EARNED: sum(earnedEarnings), EARNED_EARNINGS_BEFORE_THIS: sum(earnedEarnings) })
      try {
        if (['Fixed Amount', 'Daily Rate', 'Manual / Variable'].includes(calculationType)) { row[column] = input(numeric(source)); required.push(monthly) }
        else if (calculationType === 'Slab Based') row[column] = formula(slabFormula(source, tokens.GROSS_EARNED))
        else if (calculationType === 'Residual / Balancing' && !/SUM/i.test(source)) row[column] = formula(`MAX(0,${translatePayrollExcelFormula(line.baseComponent || component.baseComponent || 'GROSS', tokens)}-${tokens.EARNINGS_BEFORE_THIS})`)
        else row[column] = formula(translatePayrollExcelFormula(source, tokens))
        row[column + 1] = formula(calculationType === 'Daily Rate' ? `${monthly}*${payable}` : proRata ? `${monthly}/${days}*${payable}` : monthly)
      } catch (error) { throw Error(`${component.code}: ${error instanceof Error ? error.message : 'This saved rule cannot be represented in Excel.'}`) }
      const monthlyValue = calculationType === 'Daily Rate' ? `${monthly}*${days}` : monthly
      if (['Earning', 'Reimbursement'].includes(component.category) && component.componentRole !== 'Information') { monthlyEarnings.push(monthlyValue); earnedEarnings.push(earned); if (component.payType === 'Fixed Pay') fixedEarnings.push(monthlyValue) }
      else if (component.category === 'Deduction') deductions.push(earned)
      else if (component.category === 'Benefit') benefits.push(earned)
      for (const alias of [component.code.toUpperCase(), String(component.id).toUpperCase()]) Object.assign(tokens, { [alias]: monthlyValue, [`${alias}_MONTHLY`]: monthlyValue, [`${alias}_EARNED`]: earned })
    }
    const valid = `AND(COUNT(${required.join(',')})=${required.length},${days}>=1,${days}<=31,${days}=ROUND(${days},0),${payable}>=0,${payable}<=${days},OR(${present}="",AND(COUNT(${present})=1,${present}>=0,${present}<=${days})))`
    row[7] = { ...formula(`IF(${valid},"Ready","Enter required inputs")`), type: 'text' }
    const total = (cells: string[]) => sum(cells.map(cell => excelCurrencyRound(cell)))
    for (const [offset, expression] of [[0, total(earnedEarnings)], [1, total(deductions)], [2, total(benefits)], [3, `MAX(0,${excelColumnLetter(grossIndex)}${n}-${excelColumnLetter(grossIndex + 1)}${n})`]] as const) row[grossIndex + offset] = formula(`IF(${valid},${expression},0/0)`)
    sheetRows.push(row)
  }
  const meta = JSON.stringify({ version: 1, clientId: context.clientId, sourceBatchId: '', sheetName: 'Salary', notesSheetName: 'Rules', month: context.month, headerRow: 5, headers, columns: mapping, salaryTemplateId: String(template.id), warnings: [] })
  return [
    { name: 'Salary', rows: sheetRows, freezeRows: 5, freezeColumns: 2, columnWidths: headers.map((_, index) => index === 1 ? 30 : 23) },
    { name: 'Rules', rows: rules, columnWidths: [28, 24, 25, 85, 16] },
    { name: '_Frevo', hidden: true, rows: [[payslipTemplateMarker], ...meta.match(/.{1,30000}/gs)!.map(value => [value])] },
  ]
}
