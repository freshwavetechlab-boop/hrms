import type { SaveRecruitmentRequisition } from '../types/payroll'

export type RequisitionTextLimit = { field: string; label: string; maximum: number; unit: 'characters' | 'UTF-8 bytes' }

export function requisitionTextError(value: unknown, rule: RequisitionTextLimit): string {
  const text = typeof value === 'string' ? value : ''
  const length = rule.unit === 'characters' ? Array.from(text).length : new TextEncoder().encode(text).length
  return length > rule.maximum
    ? `${rule.label}: maximum ${rule.maximum} ${rule.unit}; received ${length}. Shorten this field before saving; keep the full detail in the source document or source notes.`
    : ''
}

export function requisitionTextErrors(values: Partial<SaveRecruitmentRequisition>, limits: RequisitionTextLimit[]) {
  return limits.flatMap(rule => {
    const error = requisitionTextError(values[rule.field as keyof SaveRecruitmentRequisition], rule)
    return error ? [{ name: rule.field, errors: [error] }] : []
  })
}
