import type { PayrollAdjustment } from '../types/payroll'

// Legacy adjustment ComponentId is INT; imported component masters also use BIGINT.
// ComponentCode is the existing native identity for those master components.
export function payrollAdjustmentPayload(row: PayrollAdjustment): PayrollAdjustment {
  if (row.componentId <= 2147483647) return row
  if (!Number.isSafeInteger(row.componentId) || !row.componentCode.trim()) throw Error('Select a valid salary component code before saving this adjustment.')
  return { ...row, componentId: 0 }
}
