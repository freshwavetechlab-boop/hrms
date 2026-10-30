import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter } from './excelPayslipImport'

const protectedHeading = /employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|(?:birth|joining)\s*date/i
const columnIndex = (text: string) => [...text].reduce((sum, letter) => sum * 26 + letter.charCodeAt(0) - 64, 0) - 1

// Read-only dependency hints, never a monthly rate, formula translation or an
// automatic alias between appended attendance columns and original day cells.
export function reviewMissingMonthlyInputs(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource, request: PayrollSetupRequest) {
  const recurring = request.components.filter(component => !component.isPeriodAdjustment && ['Earning', 'Reimbursement'].includes(component.category))
  const hasBasis = recurring.some(component => ['Fixed Amount', 'Daily Rate'].includes(component.calculationType)
    && (Number.isInteger(component.sourceColumnIndex) || Number(component.value) > 0))
  if (hasBasis) return []
  const outputs = new Set(request.components.map(component => component.expectedColumnIndex).filter(index => index != null))
  const dayColumns = new Set([request.payrollDaysColumnIndex, request.payableDaysColumnIndex, request.presentDaysColumnIndex].filter(index => index != null && index >= 0))
  const selected = batch.rows.filter(row => request.rowIds.includes(row.id))
  return selected.flatMap(row => recurring.map(component => {
    const sourceRows = source.rows.filter(item => item.sourceRow === row.sourceRow)
    const cell = sourceRows.length === 1 && component.expectedColumnIndex != null ? sourceRows[0].cells[component.expectedColumnIndex] : undefined
    const formula = cell?.formulaText || ''
    const sourceInvalid = sourceRows.length !== 1 || !cell || !!cell.error || !!cell.missingCachedValue || !!cell.calculationError
    const external = formula.includes('!') || formula.includes('[') || formula.includes(']')
    const references = external ? [] : [...new Set([...formula.replace(/"(?:[^"]|"")*"/g, '').toUpperCase().matchAll(/\$?([A-Z]{1,3})\$?([1-9]\d{0,6})\b/g)]
      .filter(match => Number(match[2]) === row.sourceRow && !outputs.has(columnIndex(match[1])) && !dayColumns.has(columnIndex(match[1])))
      .map(match => {
        const index = columnIndex(match[1]), heading = source.columns.find(column => column.columnIndex === index)?.sourceHeader || ''
        return protectedHeading.test(heading) ? 'Identity/contact-like dependency — not a salary basis' : `${match[1]}${match[2]}`
      }))].slice(0, 20)
    return {
      key: `${row.id}:${component.key}`, sourceRow: row.sourceRow, component: component.code,
      outputCell: component.expectedColumnIndex == null ? 'Not mapped' : `${excelColumnLetter(component.expectedColumnIndex)}${row.sourceRow}`,
      status: sourceInvalid ? 'Source output missing/error' : 'Monthly basis requires review', references: references.join(', ') || (external ? 'Captured workbook/dependency mapping required' : 'No unambiguous local input reference'),
      action: sourceInvalid ? 'Resolve the missing/error source output in Excel Payslip capture first. A formula cache or missing value cannot be adopted as a monthly salary basis.' : 'Review these captured references as monthly rate versus attendance/rule inputs. Set Source input column only after verifying its meaning; earned output is a comparison value. Preserve the exact formula and its rounding, then repreview.',
      sourceRounding: /\bROUND(?:UP|DOWN)?\s*\(/i.test(formula) ? 'Source ROUND rule must be retained; simple proration is not proof of equivalence.' : 'Source formula proof is still required.',
    }
  })).slice(0, 1000)
}
