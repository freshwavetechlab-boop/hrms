import type { Component } from '../types/payroll'
import type { ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import { buildPayslipCalculationSource, defaultPayslipMapping, excelColumnLetter, isPayslipIdentifierLabel, parsePayslipAmount, payslipHeaders, translatePayslipSharedFormula, type PayslipColumn, type PayslipColumnKind, type PayslipSheet, type PayslipSourceRow } from './excelPayslipImport'
import { buildPayslipMappingEvidence } from './excelPayslipMapping'
import { payrollSourceColumns } from './excelPayrollColumns'
import { suggestPayslipWorksheet } from './excelPayslipConsolidation'

export type SalaryTemplateExcelRole = 'ignore' | 'input' | 'earning' | 'deduction' | 'employer' | 'payrollDays' | 'presentDays' | 'payableDays' | 'monthlyCtc' | 'annualCtc' | 'netPay' | 'grossTotal' | 'deductionTotal'
export type SalaryExcelFormulaVariant = { signature: string; formula: string; sourceRows: number[]; count: number }
export type SalaryTemplateExcelColumn = {
  columnIndex: number; letter: string; header: string; label: string; suggestedRole: SalaryTemplateExcelRole; suggestedCode: string;
  matchedComponentId?: string; sampleValue: string; sampleFormula: string; hasFormula: boolean; dependencies: number[];
  formulaVariants: SalaryExcelFormulaVariant[]; totalFormulaVariants: number; isInputDependency: boolean; issues: string[];
}
export type SalaryTemplateSheetAnalysis = {
  headerRow: number; sampleRow: number; sampleRows: { sourceRow: number; label: string }[]; candidateRowCount: number; sourceRows: number[];
  columns: SalaryTemplateExcelColumn[]; issues: string[]; hasFormulaVariants: boolean;
}

/** Bind selected columns only; the independent capture retains the entire used range. */
export function selectedSalaryTemplateExcelBindings<T extends { kind: string }>(columns: T[]): T[] {
  return columns.filter(column => column.kind !== 'ignore')
}
const normal = (value: string) => value.normalize('NFKC').toUpperCase().replace(/[^A-Z0-9]/g, '')
const financial = (role: SalaryTemplateExcelRole) => role === 'earning' || role === 'deduction' || role === 'employer'
const baseName = (value: string) => {
  // Percent annotations name a familiar earning basis, but pairing still needs
  // the actual dependency below. Do not strip statutory employee/employer labels.
  const label = value.normalize('NFKC').trim().replace(/^(B(?:A|E)SIC(?:\s*(?:PAY|SALARY|WAGES))?|HRA|HOUSE\s*RENT\s*ALLOWANCE|DA|DEARNESS\s*ALLOWANCE|STAT(?:UTORY)?\s*BONUS|BONUS)\s*@?\s*\d+(?:\.\d+)?\s*%\s*(?:(?:ON|OF)\s*)?(?:(?:THE\s*)?(?:BASIC(?:\s*PAY)?|GROSS(?:\s*SALARY)?|WAGES?))?\s*$/i, '$1')
  const key = normal(label).replace(/^(?:MONTHLY|FULLMONTH)/, '').replace(/^(?:ER|EARNED)(?=BASIC|DA|HRA|SPECIAL|EXP|OTHER|MEDICAL|BONUS|WAGES)/, '').replace(/(?:MONTHLY|FULLMONTH|EARNED)$/, '')
  // A source relationship is still required below. Canonicalize only established
  // earning aliases, never similarly named employee/employer statutory columns.
  return /^B(?:A|E)SIC(?:PAY|SALARY|WAGES)?$/.test(key) ? 'BASIC' : key === 'HOUSERENTALLOWANCE' ? 'HRA' : key === 'DEARNESSALLOWANCE' ? 'DA' : /^(?:STAT(?:UTORY)?BONUS|BONUS)$/.test(key) ? 'STATBONUS' : key
}
const roleFor = (header: string, kind: PayslipColumnKind): SalaryTemplateExcelRole => {
  const key = normal(header)
  if (/^(?:ANNUALCTC|YEARLYCTC|ANNUALCOSTTOCOMPANY)$/.test(key)) return 'annualCtc'
  if (/^(?:CTC|MONTHLYCTC|COSTTOCOMPANY|MONTHLYCOSTTOCOMPANY)$/.test(key)) return 'monthlyCtc'
  if (/^(?:ACTUALPRESENTDAYS?|PRESENTDAYS?|DAYSPRESENT)$/.test(key)) return 'presentDays'
  if (/^(?:PAYABLEDAYS?|PAYBLEDAYS?|PAIDDAYS?|DAYS(?:PAID|PAYABLE)|NUMBEROFWORKINGDAYSATTENDED|ATTENDANCE|ATTEDANCE)$/.test(key)) return 'payableDays'
  if (/^(?:PAYROLLDAYS?|CALCULATIONDAYS?|MONTHDAYS?|DAYSOFMONTH|TOTALDAYS?|TOTALWORKINGDAYS?|WORKINGDAYS?)$/.test(key)) return 'payrollDays'
  return ['earning', 'deduction', 'employer', 'grossTotal', 'deductionTotal', 'netPay'].includes(kind) ? kind as SalaryTemplateExcelRole : 'ignore'
}
const codeFor = (header: string, role: SalaryTemplateExcelRole) => {
  const key = baseName(header)
  let code = /^B(?:A|E)SIC(?:PAY|SALARY|WAGES)?$/.test(key) ? 'BASIC'
    : /^(?:HRA|HOUSERENTALLOWANCE)$/.test(key) ? 'HRA'
    : /^(?:DA|DEARNESSALLOWANCE)$/.test(key) ? 'DA'
    : key === 'STATBONUS' ? 'STAT_BONUS'
    : /^(?:(?:EMPLOYEE|EMPLOYER)?(?:EPF|PF|PROVIDENTFUND))(?:\d+)?$/.test(key) ? 'PF'
    : /^(?:(?:EMPLOYEE|EMPLOYER)?(?:ESI|ESIC))(?:\d+)?$/.test(key) ? 'ESIC'
    : /^(?:PT|PTAX|PROFESSIONALTAX)$/.test(key) ? 'PT'
    : header.normalize('NFKC').toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 40) || 'COLUMN'
  if (/^\d/.test(code)) code = `SRC_${code}`.slice(0, 40)
  if (role === 'employer' && ['PF', 'ESIC'].includes(code)) code += '_ER'
  return code
}

/** Canonicalize relative rows while preserving absolute references and text-literal whitespace. */
export function salaryExcelFormulaSignature(formula: string, sourceRow: number): string {
  const shifted = translatePayslipSharedFormula(formula.replace(/^=/, ''), `A${sourceRow}`, 'A50000')
  let result = '', quoted = false
  for (let index = 0; index < shifted.length; index++) {
    const char = shifted[index]
    if (char === '"') {
      result += char
      if (quoted && shifted[index + 1] === '"') { result += shifted[++index]; continue }
      quoted = !quoted; continue
    }
    if (quoted || !/\s/.test(char)) result += quoted ? char : char.toUpperCase()
  }
  return result
}

export function suggestSalaryExcelHeaderRows(sheet: PayslipSheet): { row: number; label: string; score: number }[] {
  const existing = suggestPayslipWorksheet([sheet])
  return sheet.rows.slice(0, 100).map(row => {
    const headers = Array.from({ length: sheet.columnCount }, (_, index) => row.cells[index]?.value.trim() || '')
    const text = headers.filter(value => value && parsePayslipAmount(value) === null).length
    const formulas = row.cells.filter(cell => cell?.formula || cell?.formulaText).length
    const recognized = defaultPayslipMapping(headers).filter(column => column.kind !== 'ignore' && column.kind !== 'info').length
    const days = headers.filter(header => roleFor(header, 'ignore') !== 'ignore').length
    const score = (existing?.headerRow === row.sourceRow ? 1000 : 0) + recognized * 8 + days * 5 + Math.min(text, 30) - formulas * 5
    return { row: row.sourceRow, label: `Row ${row.sourceRow} · ${headers.filter(Boolean).slice(0, 5).join(' · ')}`.slice(0, 220), score: text >= 2 ? score : 0 }
  }).filter(row => row.score > 0).sort((a, b) => b.score - a.score || a.row - b.row).slice(0, 20)
}

function dataRows(sheet: PayslipSheet, headerRow: number, kinds: ReturnType<typeof defaultPayslipMapping>) {
  const name = kinds.find(column => column.kind === 'employeeName')?.columnIndex
  const code = kinds.find(column => column.kind === 'employeeCode')?.columnIndex
  return sheet.rows.filter(row => {
    if (row.sourceRow <= headerRow || !row.cells.some(cell => cell?.formula || cell?.formulaText || parsePayslipAmount(cell?.value || '') !== null)) return false
    const first = row.cells.find(cell => cell?.value.trim())?.value.trim() || ''
    if (/^(?:(?:GRAND|SUB)[\s_-]*)?TOTAL(?:S|\s|:|$)|^(?:NOTES?|REMARKS?|CERTIFIED|PREPARED|CHECKED|APPROVED)(?:\s|:|$)/i.test(first)) return false
    const identity = name !== undefined ? row.cells[name]?.value.trim() : code !== undefined ? row.cells[code]?.value.trim() : undefined
    return identity === undefined || !!identity && !/^(?:(?:GRAND|SUB)[\s_-]*)?TOTAL(?:S|\s|:|$)/i.test(identity)
  })
}

function matchMaster(header: string, code: string, role: SalaryTemplateExcelRole, components: Component[]): string | undefined {
  if (!financial(role)) return undefined
  const category = role === 'deduction' ? 'Deduction' : role === 'employer' ? 'Benefit' : 'Earning'
  const candidates = components.filter(component => component.active && component.category === category && component.componentRole !== 'Information'
    && (normal(component.code) === normal(code) || normal(component.name) === normal(header)))
  return candidates.length === 1 ? String(candidates[0].id) : undefined
}

export function analyzeSalaryTemplateSheet(sheet: PayslipSheet, headerRow: number, sampleRow?: number, components: Component[] = [], workbookSheets: PayslipSheet[] = [sheet], mappingColumns?: PayslipColumn[]): SalaryTemplateSheetAnalysis {
  if (!Number.isInteger(headerRow) || headerRow < 1 || !sheet.rows.some(row => row.sourceRow === headerRow)) throw new Error('Choose a source row containing the column headings.')
  const headers = payslipHeaders(sheet, headerRow), defaults = defaultPayslipMapping(headers, sheet, headerRow)
  const approved = new Map((mappingColumns ?? []).filter(column => Number.isInteger(column.columnIndex) && column.columnIndex >= 0 && column.columnIndex < headers.length
    && column.sourceHeader === headers[column.columnIndex]).map(column => [column.columnIndex, column]))
  const kinds = defaults.map(column => approved.has(column.columnIndex) ? { ...column, ...approved.get(column.columnIndex) } : column)
  const rows = dataRows(sheet, headerRow, kinds), selectedRow = rows.find(row => row.sourceRow === sampleRow) ?? rows[0]
  const employeeRows = new Set(rows.map(row => row.sourceRow))
  const discovery = payrollSourceColumns({ sheetName: sheet.name, headerRow, columnCount: sheet.columnCount, columns: kinds, rows: sheet.rows, excludedSourceRows: [] }, Object.fromEntries([...approved].map(([index, column]) => [index, column.kind])), rows.map(row => row.sourceRow))
  const evidence = buildPayslipMappingEvidence({ ...sheet, rows: sheet.rows.filter(row => row.sourceRow === headerRow || employeeRows.has(row.sourceRow)) }, headerRow, discovery, '', [], workbookSheets)
  const rowsBySource = new Map(rows.map(row => [row.sourceRow, row]))
  const variants = Array.from({ length: sheet.columnCount }, () => new Map<string, SalaryExcelFormulaVariant>())
  const refs = Array.from({ length: sheet.columnCount }, () => new Set<number>())
  const columnIssues = Array.from({ length: sheet.columnCount }, () => new Set<string>())
  const numericColumns = new Set<number>(), formulaColumns = new Set<number>()
  for (const row of rows) row.cells.forEach((cell, index) => {
    if (!cell || index >= sheet.columnCount) return
    if (parsePayslipAmount(cell.value) !== null) numericColumns.add(index)
    for (const error of [cell.error, cell.calculationError]) if (error) columnIssues[index].add(error)
    if (cell.missingCachedValue) columnIssues[index].add('Source formula has no cached result; compare with native calculation before saving.')
    if (!cell.formula && !cell.formulaText) return
    formulaColumns.add(index)
    if (!cell.formulaText) { columnIssues[index].add('Source formula expression is missing; its saved amount is not a reusable rule.'); return }
    let signature: string
    try { signature = salaryExcelFormulaSignature(cell.formulaText, row.sourceRow) } catch { signature = cell.formulaText; columnIssues[index].add('Formula could not be normalized for a reusable source pattern.') }
    let variant = variants[index].get(signature)
    if (!variant) { variant = { signature, formula: cell.formulaText, sourceRows: [], count: 0 }; variants[index].set(signature, variant) }
    variant.count++; if (variant.sourceRows.length < 100) variant.sourceRows.push(row.sourceRow)
  })
  for (const column of evidence.columns) {
    column.formulaDependencies.forEach(index => refs[column.columnIndex].add(index))
    for (const dependency of column.crossSheetDependencies) columnIssues[column.columnIndex].add(dependency.status === 'supplied' ? 'Formula uses a supplied linked worksheet; retain the original workbook dependency capture.' : 'Formula uses a missing or external worksheet; review the supplied-workbook dependency before native extraction.')
    if (column.dependencyEvidenceIncomplete) columnIssues[column.columnIndex].add('Some source dependency evidence needs review; cached values are not substituted for formulas.')
  }
  const columns: SalaryTemplateExcelColumn[] = headers.map((header, columnIndex) => ({ columnIndex, letter: excelColumnLetter(columnIndex), header, label: discovery[columnIndex]?.label || header || `Column ${excelColumnLetter(columnIndex)}`,
    suggestedRole: roleFor(header, discovery[columnIndex]?.kind || 'ignore'), suggestedCode: '',
    sampleValue: selectedRow?.cells[columnIndex]?.value ?? '', sampleFormula: selectedRow?.cells[columnIndex]?.formulaText || '', hasFormula: formulaColumns.has(columnIndex),
    dependencies: [...refs[columnIndex]].sort((a, b) => a - b), formulaVariants: [...variants[columnIndex].values()].slice(0, 32), totalFormulaVariants: variants[columnIndex].size,
    isInputDependency: false, issues: [...columnIssues[columnIndex]],
  }))
  for (const column of columns.filter(column => column.hasFormula)) {
    const literals = rows.filter(row => !row.cells[column.columnIndex]?.formula && !row.cells[column.columnIndex]?.formulaText)
    if (literals.length) {
      column.totalFormulaVariants++
      column.formulaVariants.push({ signature: 'SOURCE_LITERAL_OR_BLANK', formula: '', sourceRows: literals.slice(0, 100).map(row => row.sourceRow), count: literals.length })
      column.issues.push('Some employee rows contain literal or blank amounts instead of this formula. The selected sample rule must not silently replace those rows.')
    }
  }
  // Monthly/earned headings need dependency evidence. Calculated monthly helpers
  // may share the binding, but their expressions remain formulas, never caches.
  for (const output of columns.filter(column => financial(column.suggestedRole) && column.hasFormula)) {
    const paired = output.dependencies.map(index => columns[index]).filter(column => column.columnIndex !== output.columnIndex && column.columnIndex < output.columnIndex && numericColumns.has(column.columnIndex)
      && !financial(column.suggestedRole) && baseName(column.header) === baseName(output.header) && !isPayslipIdentifierLabel(column.header)
      && !column.dependencies.some(index => ['payrollDays', 'presentDays', 'payableDays'].includes(columns[index].suggestedRole)))
    if (paired.length === 1) paired[0].suggestedRole = 'input'
  }
  const hasGranularEarnings = columns.some(column => column.suggestedRole === 'earning')
  if (hasGranularEarnings) for (const column of columns) if (!approved.has(column.columnIndex) && /^(?:TOTAL|TOTALEARNINGS|TOTALSALARY|GROSS|GROSSSALARY|EARNEDGROSS)$/.test(normal(column.header))) column.suggestedRole = 'grossTotal'
  const required = new Set<number>()
  const collect = (index: number, visited = new Set<number>()) => {
    if (visited.has(index)) return
    visited.add(index)
    for (const dependency of columns[index].dependencies) { required.add(dependency); collect(dependency, visited) }
  }
  columns.filter(column => financial(column.suggestedRole)).forEach(column => collect(column.columnIndex))
  for (const column of columns) {
    column.isInputDependency = required.has(column.columnIndex)
    if (column.suggestedRole === 'ignore' && !column.hasFormula && column.isInputDependency && numericColumns.has(column.columnIndex) && !isPayslipIdentifierLabel(column.header)
      && !/^(?:S(?:ERIAL)?NO|SRNO|SLNO|NO|ID|CODE)$/.test(normal(column.header))) column.suggestedRole = 'input'
    column.suggestedCode = codeFor(column.header, column.suggestedRole)
    if (column.totalFormulaVariants > 1) column.issues.push(`${column.totalFormulaVariants} different formula patterns occur in employee rows. Review the selected sample pattern; do not apply it to every row automatically.`)
    column.matchedComponentId = matchMaster(column.header, column.suggestedCode, column.suggestedRole, components)
  }
  const used = new Set<string>()
  for (const column of columns.filter(column => financial(column.suggestedRole))) {
    if (used.has(column.suggestedCode)) { column.suggestedCode = `${column.suggestedCode.slice(0, 35)}_${column.letter}`; column.matchedComponentId = undefined }
    used.add(column.suggestedCode)
  }
  const reserved = new Set([...components.map(component => component.code.toUpperCase()), ...columns.filter(column => financial(column.suggestedRole)).map(column => column.suggestedCode.toUpperCase())])
  for (const output of columns.filter(column => financial(column.suggestedRole))) {
    const pairedInput = columns.some(column => column.suggestedRole === 'input' && !column.sampleFormula.trim() && output.dependencies.includes(column.columnIndex) && baseName(column.header) === baseName(output.header))
    if (output.hasFormula && !pairedInput) continue
    const category = output.suggestedRole === 'deduction' ? 'Deduction' : output.suggestedRole === 'employer' ? 'Benefit' : 'Earning'
    const code = output.suggestedCode
    const inputFamily = `${code.slice(0, 39)}_XL_INPUT`
    const blankMasters = components.filter(component => component.active && component.category === category && component.componentRole !== 'Information'
      && normal(component.calculationType) === 'FIXEDAMOUNT' && !String(component.value ?? '').trim() && !String(component.formula ?? '').trim()
      && (normal(component.code) === normal(code) || normal(component.name) === normal(output.header)
        || component.code.toUpperCase() === inputFamily || new RegExp(`^${inputFamily.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}_\\d+$`, 'i').test(component.code)))
    if (blankMasters.length === 1) {
      output.suggestedCode = blankMasters[0].code
      output.matchedComponentId = String(blankMasters[0].id)
    } else {
      let proposed = inputFamily, suffix = 2
      while (reserved.has(proposed.toUpperCase())) proposed = `${inputFamily.slice(0, 45)}_${suffix++}`
      output.suggestedCode = proposed
      output.matchedComponentId = undefined
      reserved.add(proposed.toUpperCase())
      output.issues.push('Per-employee source amounts use a blank input component. Existing component defaults, including zero, are not reused as another employee’s salary.')
    }
  }
  for (const input of columns.filter(column => column.suggestedRole === 'input')) {
    const outputs = columns.filter(column => financial(column.suggestedRole) && column.dependencies.includes(input.columnIndex) && baseName(column.header) === baseName(input.header))
    if (outputs.length === 1) input.suggestedCode = outputs[0].suggestedCode
  }
  const nameColumn = kinds.find(column => column.kind === 'employeeName')?.columnIndex
  const codeColumn = kinds.find(column => column.kind === 'employeeCode')?.columnIndex
  const sampleLabel = (row: PayslipSourceRow) => {
    const identity = [codeColumn === undefined ? '' : row.cells[codeColumn]?.value, nameColumn === undefined ? '' : row.cells[nameColumn]?.value].filter(Boolean).join(' · ')
    return `Row ${row.sourceRow}${identity ? ` · ${identity}` : ''}`.slice(0, 180)
  }
  return { headerRow, sampleRow: selectedRow?.sourceRow || 0, sampleRows: [...rowsBySource.values()].map(row => ({ sourceRow: row.sourceRow, label: sampleLabel(row) })), candidateRowCount: rows.length, sourceRows: rows.map(row => row.sourceRow), columns,
    issues: [...(!rows.length ? ['No employee data rows were found below these headings. Select the correct header row.'] : []), ...(!formulaColumns.size ? ['No active Excel formulas were found. These columns contain source values only; saved amounts are not reusable formula rules.'] : [])],
    hasFormulaVariants: columns.some(column => column.totalFormulaVariants > 1) }
}

/** Preserve source coordinates, formulas and error evidence for authoritative native extraction. */
export function buildSalaryTemplateExcelSource(sheet: PayslipSheet, headerRow: number, sampleRow?: number, additionalSourceRows: number[] = []): ExcelPayslipCalculationSource {
  if (sampleRow !== undefined && (!Number.isInteger(sampleRow) || sampleRow <= headerRow || !sheet.rows.some(row => row.sourceRow === sampleRow))) throw new Error('Choose a valid employee sample row below the selected headings.')
  const included = sampleRow === undefined ? undefined : new Set([headerRow, sampleRow, ...additionalSourceRows.filter(row => Number.isInteger(row) && row > 0)])
  const capture = included ? { ...sheet, rows: sheet.rows.filter(row => included.has(row.sourceRow)) } : sheet
  return buildPayslipCalculationSource(capture, headerRow, defaultPayslipMapping(payslipHeaders(sheet, headerRow), sheet, headerRow))
}
