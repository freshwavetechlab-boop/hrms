import type { SimulationResult } from '../services/payrollCalculationService'
import { buildPayrollFormulaWorkbook } from './payrollFormulaWorkbook'
import type { XlsxCell } from './xlsx'

export type SalaryWorksheetCell = { address: string; label: string; value: string | number | null; formula: string; configuredFormula?: string; source?: string; inputKey?: string; ruleComponentId?: string; category?: string }
export const salaryColumnLetter = (index: number) => { let result = ''; for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) result = String.fromCharCode(65 + (n - 1) % 26) + result; return result }
const columnIndex = (letter: string) => [...letter].reduce((value, char) => value * 26 + char.charCodeAt(0) - 64, 0) - 1
const value = (cell: XlsxCell): string | number => typeof cell === 'object' ? cell.value : cell

/** Align heterogeneous employee results by column identity; formulas follow their own row and dependencies. */
const canonicalInputLabels: Record<string, string> = { PAYROLL_DAYS: 'Payroll days', PRESENT_DAYS: 'Present days', PAYABLE_DAYS: 'Payable days', ANNUAL_CTC: 'Annual CTC' }
export type SalaryWorksheetSelection = { rowKey?: string; columnKey?: string; row: number; column: number }
export function resolveSalaryWorksheetSelection(selection: SalaryWorksheetSelection, rowKeys: string[], columnKeys: string[]) {
  const row = selection.rowKey ? rowKeys.indexOf(selection.rowKey) : -1, column = selection.columnKey ? columnKeys.indexOf(selection.columnKey) : -1
  return { row: row >= 0 ? row : Math.max(0, Math.min(rowKeys.length - 1, selection.row)), column: column >= 0 ? column : Math.max(0, Math.min(columnKeys.length - 1, selection.column)) }
}
export function alignSalaryWorksheetRows(models: { cells: SalaryWorksheetCell[]; notice: string }[]): { keys: string[]; labels: string[]; rows: SalaryWorksheetCell[][] } {
  const labels: string[] = [], keys: string[] = []
  const rowKeys = models.map(model => {
    const counts = new Map<string, number>()
    return model.cells.map(cell => {
      const label = canonicalInputLabels[cell.inputKey || ''] || cell.label
      const identity = cell.inputKey ? `input:${cell.inputKey}` : label
      const occurrence = counts.get(identity) ?? 0; counts.set(identity, occurrence + 1)
      const key = `${identity}\u0000${occurrence}`
      if (!keys.includes(key)) { keys.push(key); labels.push(label) }
      return key
    })
  })
  // Identity and attendance remain at A:G even when CTC or salary columns arrive after an incomplete response.
  const leading = ['Employee code', 'Employee name', 'Month', 'Work location', 'input:PAYROLL_DAYS', 'input:PRESENT_DAYS', 'input:PAYABLE_DAYS']
  const ordered = keys.map((key, index) => ({ key, label: labels[index], rank: leading.indexOf(key.split('\u0000')[0]), original: index })).sort((a, b) => (a.rank < 0 ? 100 : a.rank) - (b.rank < 0 ? 100 : b.rank) || a.original - b.original)
  keys.splice(0, keys.length, ...ordered.map(item => item.key)); labels.splice(0, labels.length, ...ordered.map(item => item.label))
  return { keys, labels, rows: models.map((model, row) => {
    const indexMap = new Map(model.cells.map((cell, index) => [columnIndex(cell.address.replace(/\d+$/, '')), keys.indexOf(rowKeys[row][index])]))
    return keys.map((key, index) => {
      const original = model.cells[rowKeys[row].indexOf(key)]
      const address = `${salaryColumnLetter(index)}${row + 2}`
      if (!original) return { address, label: labels[index], value: null, formula: '', source: 'This employee has no corresponding native calculation line.' }
      return { ...original, address, label: labels[index], formula: original.formula.replace(/\b(\$?)([A-Z]{1,3})(\$?)(\d+)\b/g, (reference, absCol: string, letters: string, absRow: string, originalRow: string) => {
        const column = indexMap.get(columnIndex(letters))
        return column === undefined ? reference : `${absCol}${salaryColumnLetter(column)}${absRow}${originalRow === '2' ? row + 2 : originalRow}`
      }) }
    })
  }) }
}
export const simulationUsesCtc = (result: SimulationResult | null) => !!result?.lines.some(line => /\b(?:CTC|GROSS|MONTHLY_CTC|ANNUAL_CTC)\b/i.test(`${line.formula} ${line.baseComponent || ''}`) || line.calculationType === 'Residual / Balancing' && !line.baseComponent)

export function salarySimulationOperands(result: SimulationResult): Omit<SalaryWorksheetCell, 'address'>[] {
  const system = new Set(['CTC', 'GROSS', 'MONTHLY_CTC', 'ANNUAL_CTC', 'PAYROLL_DAYS', 'TOTAL_DAYS', 'WORKING_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS', 'LOP_DAYS', 'EARNINGS_BEFORE_THIS', 'MONTHLY_EARNINGS_BEFORE_THIS', 'FIXED_EARNINGS', 'FIXED_EARNINGS_BEFORE_THIS', 'GROSS_EARNED', 'EARNED_EARNINGS_BEFORE_THIS'])
  for (const line of result.lines) for (const key of [line.code, line.componentId || '']) for (const token of [key, `${key}_MONTHLY`, `${key}_EARNED`]) system.add(token.toUpperCase())
  const cells = new Map<string, Omit<SalaryWorksheetCell, 'address'>>()
  const declared = new Set<string>()
  for (const line of result.lines) for (const [scope, context] of [...(line.namedOperands ? [['', line.namedOperands] as const] : []), ...Object.entries(line.namedOperandParts ?? {})]) for (const definition of context.definitions) {
    declared.add(definition.key.toUpperCase())
    const value = context.values[definition.key] ?? null
    const signature = `${scope}:${definition.key}:${value}`
    if (!cells.has(signature)) cells.set(signature, { label: `${definition.label || definition.key} [${definition.key}]${scope ? ` · ${scope}` : ''}`, value, formula: '', source: `Resolved native source operand · ${definition.unit}${scope ? ` · location part ${scope}` : ''} · read-only`, category: 'Formula input' })
  }
  for (const input of result.inputs) if (!system.has(input.name.toUpperCase()) && !declared.has(input.name.toUpperCase())) {
    const signature = `${input.name}:${input.value}`
    if (!cells.has(signature)) cells.set(signature, { label: input.name, value: input.value, formula: '', source: `${input.source} · ${input.unit} · read-only`, category: 'Formula input' })
  }
  for (const line of result.lines) for (const [key, value] of Object.entries(line.inputs)) {
    if (system.has(key.toUpperCase()) || declared.has(key.toUpperCase())) continue
    const signature = `${key}:${value}`
    if (!cells.has(signature)) cells.set(signature, { label: key, value, formula: '', source: 'Resolved native formula input · read-only', category: 'Formula input' })
  }
  return [...cells.values()]
}

/** Display model only: every amount comes from native simulation, never a second JS payroll engine. */
export function salarySimulationWorksheet(result: SimulationResult, context: { employeeCode?: string; employeeName?: string; month: string; clientName: string }): { cells: SalaryWorksheetCell[]; notice: string } {
  const code = result.employeeCode || context.employeeCode || '', name = result.employeeName || context.employeeName || result.employee || 'Template scenario'
  const location = result.locations?.length ? [...new Set(result.locations.map(item => item.workLocationName || (item.workLocationId ? `Location #${item.workLocationId}` : 'Not supplied')))].join(' / ') : result.workLocationName || (result.mode === 'Template' ? 'Template scenario · no employee location' : 'Location not provided by current API')
  const input = (token: string) => result.inputs.find(item => item.name === token)?.value ?? null
  const first: Omit<SalaryWorksheetCell, 'address'>[] = [
    { label: 'Employee code', value: code, formula: '', ...(result.mode === 'Template' ? { inputKey: 'SCENARIO_EMPLOYEE_CODE' } : {}) },
    { label: 'Employee name', value: name, formula: '', ...(result.mode === 'Template' ? { inputKey: 'SCENARIO_EMPLOYEE_NAME' } : {}) },
    { label: 'Month', value: result.payPeriod || context.month, formula: '' }, { label: 'Work location', value: location, formula: '' },
  ]
  const usesCtc = simulationUsesCtc(result)
  const sourceOperands = salarySimulationOperands(result)
  const sourceNotice = sourceOperands.length ? sourceOperands.some(cell => /DAYS|UNITS|HOURS/i.test(cell.label + cell.source))
    ? 'Independent source-period units drive this template. Their resolved input columns are read-only here; changing aggregate payable days does not redistribute old/new-rate or extra-duty units.'
    : 'Resolved independent source inputs are shown as read-only columns; aggregate attendance does not replace those source values.' : ''
  // Native policies are resolved by the server. Their amount cells are read-only
  // dependencies, not invented PF/TDS formulas or editable cached money.
  const safeLines = result.lines.map(line => line.source.includes('Effective-dated PF') || line.source.startsWith('Projected TDS')
    ? { ...line, source: 'Native policy output (read-only)', calculationType: 'Fixed Amount', formula: '', amountOverride: true, dailyRate: null } : line)
  const named = result.lines.map(line => line.namedOperands).find(Boolean)
  try {
    const sheets = buildPayrollFormulaWorkbook(context.clientName, context.month, [{ employeeId: result.employeeId || 1, employeeCode: code || 'Scenario', employeeName: name, calculation: { ...result, lines: safeLines }, namedInputs: named,
      // The native complete result already validated this exact source's units.
      reviewedExtraDutyUnits: result.complete && input('PAYABLE_DAYS') != null && input('PAYROLL_DAYS') != null && input('PAYABLE_DAYS')! > input('PAYROLL_DAYS')!,
    }])
    const headers = sheets[0].rows[0], row = sheets[0].rows[1]
    const keep = [0, 1, ...(usesCtc ? [3, 4] : []), 5, 6, 7, 8, 9, 10, 11, 12, ...headers.map((_, index) => index).filter(index => index >= 15)]
    const mapped = new Map(keep.map((oldIndex, index) => [oldIndex, index < 2 ? index : index + 2]))
    const translate = (formula: string) => formula.replace(/\b(\$?)([A-Z]{1,3})(\$?)(\d+)\b/g, (_, absCol: string, letters: string, absRow: string, rowNumber: string) => {
      const position = mapped.get(columnIndex(letters)); if (position === undefined) throw Error('Unrepresented native dependency')
      return `${absCol}${salaryColumnLetter(position)}${absRow}${rowNumber}`
    })
    const cells: Omit<SalaryWorksheetCell, 'address'>[] = keep.map(index => {
      const cell = row[index], nativeLine = index >= 15 && index < 15 + result.lines.length * 3 ? result.lines[Math.floor((index - 15) / 3)] : undefined
      const policy = nativeLine && safeLines[Math.floor((index - 15) / 3)] !== nativeLine
      const raw = index >= 15 && (index - 15) % 3 === 0
      return { label: String(value(headers[index])), value: value(cell), formula: typeof cell === 'object' && cell.formula ? `=${translate(cell.formula)}` : '',
        configuredFormula: raw && nativeLine && !policy ? nativeLine.formula : undefined, source: nativeLine?.source, category: nativeLine?.category,
        inputKey: ({ 3: 'ANNUAL_CTC', 5: 'PAYROLL_DAYS', 6: 'PRESENT_DAYS', 7: 'PAYABLE_DAYS' } as Record<number, string>)[index]
          || (raw && !policy && nativeLine?.componentId && typeof cell === 'object' && cell.style === 'input' ? `COMPONENT:${nativeLine.componentId}` : undefined),
        ruleComponentId: raw && !policy ? nativeLine?.componentId : undefined }
    })
    cells.splice(2, 0, first[2], first[3]); cells[0] = first[0]; cells[1] = first[1]
    // Normally the exporter already included declared operands. Retain any
    // additional resolved dependency without inventing a new editable binding.
    for (const operand of sourceOperands) if (!cells.some(cell => cell.label === operand.label || cell.label.includes(operand.label.match(/\[[^\]]+\]/)?.[0] || operand.label))) cells.push(operand)
    return { cells: cells.map((cell, index) => ({ ...cell, address: `${salaryColumnLetter(index)}2` })), notice: [sourceNotice, result.lines.some((line, index) => safeLines[index] !== line) ? 'Dated PF/TDS policy outputs recalculate on the server; their resolved amount cells are read-only.' : ''].filter(Boolean).join(' ') }
  } catch {
    // An unsupported offline representation never becomes a false template error.
    // Preserve the actual native result and its configured rule in the formula bar.
    const cells = [...first,
      { label: 'Payroll days', value: input('PAYROLL_DAYS'), formula: '', inputKey: 'PAYROLL_DAYS' },
      { label: 'Present days', value: input('PRESENT_DAYS'), formula: '', inputKey: 'PRESENT_DAYS' },
      { label: 'Payable days', value: input('PAYABLE_DAYS'), formula: '', inputKey: 'PAYABLE_DAYS' },
      ...(usesCtc ? [{ label: 'Annual CTC', value: input('ANNUAL_CTC'), formula: '', inputKey: 'ANNUAL_CTC' }] : []),
      ...sourceOperands,
      ...result.lines.flatMap(line => [
        { label: `${line.name} · full period`, value: line.monthly, formula: '', configuredFormula: line.source.includes('Effective-dated PF') || line.source.startsWith('Projected TDS') ? undefined : line.formula, source: line.source, category: line.category, ruleComponentId: line.source === 'Component / template default' ? line.componentId : undefined },
        { label: `${line.name} · earned`, value: line.earned, formula: '', configuredFormula: line.source.includes('Effective-dated PF') || line.source.startsWith('Projected TDS') ? undefined : line.formula, source: line.substituted || line.source, category: line.category },
      ]),
      { label: 'Gross earnings', value: result.gross, formula: '' }, { label: 'Deductions', value: result.deductions, formula: '' }, { label: 'Employer benefits', value: result.benefits, formula: '' }, { label: 'Net pay', value: result.net, formula: '' },
    ]
    return { cells: cells.map((cell, index) => ({ ...cell, address: `${salaryColumnLetter(index)}2` })), notice: [sourceNotice, 'Native payroll rules shown in the formula bar. This result contains rules without a safe offline Excel equivalent; all what-if amounts still recalculate through the payroll engine.'].filter(Boolean).join(' ') }
  }
}
