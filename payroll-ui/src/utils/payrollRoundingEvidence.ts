import { formatExcelPayslipAmount } from './excelPayslipImport'

type Amount = number | null | undefined
const known = (value: Amount): value is number => typeof value === 'number' && Number.isFinite(value)

export const payrollComparisonMoney = (value: Amount) => known(value) ? formatExcelPayslipAmount(value, 2) : '-'
export const payrollRawAmount = (value: Amount) => known(value) ? String(value) : '-'
export function payrollRawDifference(value: Amount) {
  if (!known(value)) return '-'
  if (value === 0) return '0'
  if (Math.abs(value) < 1e-9) return value.toExponential(3)
  return value.toLocaleString('en-IN', { useGrouping: false, maximumFractionDigits: 9 })
}
export function payrollDisplayEvidence(source: Amount, calculated: Amount) {
  return {
    sourceDisplay: payrollComparisonMoney(source),
    calculatedDisplay: payrollComparisonMoney(calculated),
    rawDifference: known(source) && known(calculated) ? calculated - source : null,
    displayStatus: !known(source) || !known(calculated) ? 'Display comparison unavailable'
      : payrollComparisonMoney(source) === payrollComparisonMoney(calculated) ? 'Same at 2dp; raw parity separate' : 'Different at 2dp',
  }
}
