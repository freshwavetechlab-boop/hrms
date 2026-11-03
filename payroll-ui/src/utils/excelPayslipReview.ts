import type { ExcelPayslipRow } from '../services/excelPayslipService'
import { formatExcelPayslipAmount, isPayslipAmountKind, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from './excelPayslipImport'

export const negativePayslipAmountWarning = 'This source row contains a negative amount; review it before export or sending.'

export function payslipReconciliation(row: ExcelPayslipRow) {
  const earnings = row.earnings.reduce((sum, item) => sum + item.amount, 0)
  const deductions = row.deductions.reduce((sum, item) => sum + item.amount, 0)
  const calculatedNet = earnings - deductions, difference = calculatedNet - row.netPay
  return { earnings, deductions, calculatedNet, difference,
    wholeRupeeMatch: formatExcelPayslipAmount(calculatedNet) === formatExcelPayslipAmount(row.netPay),
    grossDifference: row.declaredGross == null ? null : earnings - row.declaredGross,
    deductionDifference: row.declaredDeductions == null ? null : deductions - row.declaredDeductions }
}

// Normalize only the displayed difference. Source amounts and comparisons retain full precision.
export function formatPayslipDifference(value: number) {
  const text = formatExcelPayslipAmount(value, 2)
  return text === '-0.00' ? '0.00' : text
}

export function payslipWarningDetails(row: ExcelPayslipRow) {
  const values = payslipReconciliation(row), amount = (value: number) => formatExcelPayslipAmount(value, 2)
  return row.warnings.map(warning => {
    if (warning.startsWith('Mapped earnings do not match the source gross total.') && row.declaredGross != null) {
      const matching = row.earnings.filter(item => Math.abs(item.amount - values.grossDifference!) <= .01)
      const hint = Math.abs(values.difference) <= .01 && matching.length === 1
        ? ` Gross difference equals ${matching[0].label} (${amount(matching[0].amount)}). Check whether source gross excludes this component.`
        : ' Check earning mappings and the source gross formula.'
      return `Gross: mapped earnings ${amount(values.earnings)} − source gross ${amount(row.declaredGross)} = ${formatPayslipDifference(values.grossDifference!)}.${hint}`
    }
    if (warning.startsWith('Mapped deductions do not match the source deduction total.') && row.declaredDeductions != null)
      return `Deductions: mapped ${amount(values.deductions)} − source total ${amount(row.declaredDeductions)} = ${formatPayslipDifference(values.deductionDifference!)}. Check deduction mappings and the source total formula.`
    if (warning.startsWith('Mapped earnings less deductions do not match source net pay.'))
      return `Net: earnings ${amount(values.earnings)} − deductions ${amount(values.deductions)} = ${amount(values.calculatedNet)}; source net ${amount(row.netPay)}; difference ${formatPayslipDifference(values.difference)}. Check missing or repeated components and the source net formula.`
    if (warning === negativePayslipAmountWarning) {
      const negatives = [...row.earnings, ...row.deductions, ...row.employerContributions, { label: 'Source net', amount: row.netPay }].filter(item => item.amount < 0)
      return `Negative source amounts: ${negatives.map(item => `${item.label} ${amount(item.amount)}`).join('; ')}. Review whether these are intentional adjustments.`
    }
    return warning
  })
}

// The previous source stays immutable. A corrected formula cache applies to this
// snapshot; retain its formula and block reuse until the formula is reviewed.
export function correctPayslipAmounts(sheet: PayslipSheet, sourceRow: number, columns: PayslipColumn[], edits: Record<number, string | null>) {
  const source = sheet.rows.find(row => row.sourceRow === sourceRow)
  if (!source || sourceRow <= (sheet.template?.headerRow ?? 0) || sheet.template?.excludedSourceRows.includes(sourceRow)) throw new Error('Choose an included employee source row.')
  const cells = [...source.cells]
  let changed = 0
  for (const [key, value] of Object.entries(edits)) {
    const index = Number(key), column = columns.find(item => item.columnIndex === index)
    if (!Number.isInteger(index) || !column || !isPayslipAmountKind(column.kind)) throw new Error('Only mapped salary amounts can be corrected here.')
    const amount = value === null ? null : parsePayslipAmount(value)
    if (amount === null || Math.abs(amount) > 10000000000) throw new Error(`Enter a valid amount for ${column.label} (maximum 10,000,000,000).`)
    const old = parsePayslipAmount(source.cells[index]?.value || '')
    if (old !== null && Number(old.toPrecision(15)) === Number(amount.toPrecision(15)) && !source.cells[index]?.error && !source.cells[index]?.missingCachedValue && !source.cells[index]?.calculationError) continue
    const original = source.cells[index]
    const formula = original?.formula || original?.formulaText || original?.calculationError
    cells[index] = { value: value!.trim(), ...(formula ? {
      ...(original.formulaText ? { formulaText: original.formulaText } : {}),
      calculationError: 'This formula amount was manually corrected for this batch. Re-import a reviewed salary formula before creating another month.'
    } : {}) }
    changed++
  }
  return { changed, sheet: changed ? { ...sheet, rows: sheet.rows.map(row => row === source ? { ...row, cells } : row) } : sheet }
}

export function correctPayslipBatchAmounts(sheet: PayslipSheet, columns: PayslipColumn[], edits: Record<number, Record<number, string | null>>) {
  let updated = sheet, changed = 0
  for (const [key, values] of Object.entries(edits)) {
    const row = Number(key)
    if (!Number.isSafeInteger(row)) throw new Error('Choose an included employee source row.')
    const result = correctPayslipAmounts(updated, row, columns, values)
    updated = result.sheet; changed += result.changed
  }
  return { sheet: updated, changed }
}

// Re-run a correction saved by the older cell-only editor, including a saved 0
// whose input would otherwise not emit another onChange event.
export function savedPayslipCorrectionInputs(sheet: PayslipSheet, columns: PayslipColumn[], sourceRows: number[]) {
  const result: Record<number, Record<number, string | null>> = {}
  for (const row of sheet.rows.filter(row => sourceRows.includes(row.sourceRow)))
    for (const column of columns.filter(column => isPayslipAmountKind(column.kind))) {
      const cell = row.cells[column.columnIndex]
      if (cell?.calculationError?.includes('manually corrected') && parsePayslipAmount(cell.value) != null)
        (result[row.sourceRow] ||= {})[column.columnIndex] = cell.value
    }
  return result
}
