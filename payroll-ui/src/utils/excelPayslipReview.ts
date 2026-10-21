import type { ExcelPayslipRow } from '../services/excelPayslipService'
import { formatExcelPayslipAmount, isPayslipAmountKind, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from './excelPayslipImport'

export function payslipReconciliation(row: ExcelPayslipRow) {
  const earnings = row.earnings.reduce((sum, item) => sum + item.amount, 0)
  const deductions = row.deductions.reduce((sum, item) => sum + item.amount, 0)
  const calculatedNet = earnings - deductions, difference = calculatedNet - row.netPay
  return { earnings, deductions, calculatedNet, difference,
    wholeRupeeMatch: formatExcelPayslipAmount(calculatedNet) === formatExcelPayslipAmount(row.netPay),
    grossDifference: row.declaredGross == null ? null : earnings - row.declaredGross,
    deductionDifference: row.declaredDeductions == null ? null : deductions - row.declaredDeductions }
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
    if (old !== null && Number(old.toPrecision(15)) === Number(amount.toPrecision(15)) && !source.cells[index]?.error && !source.cells[index]?.missingCachedValue) continue
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
