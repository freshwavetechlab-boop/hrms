import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollLocationAllocations, PayrollLocationGroup, PayrollLocationPart, PayrollSetupContext } from '../services/excelPayrollSetupService'
import { buildPayslipEmployeeGroups } from './excelPayslipEmployeeGroups'

export function selectedLocationAllocationGroups(batch: ExcelPayslipBatch, rowIds: string[]) {
  const selected = new Set(rowIds), all = buildPayslipEmployeeGroups(batch)
  const groups = all.groups.filter(group => group.rows.some(row => selected.has(row.id)))
  return { groups, incomplete: groups.filter(group => group.rows.some(row => !selected.has(row.id))), unresolved: groups.filter(group => group.reviewReason && !group.isReviewedLocationSplit), selectedCount: rowIds.length }
}

export function newLocationAllocationReview(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[]): PayrollLocationAllocations | null {
  const selection = selectedLocationAllocationGroups(batch, rowIds)
  if (context.locationAllocationSupported !== true || !/^\d{4}-\d{2}-\d{2}$/.test(context.locationAllocationImportDate || '') || !selection.groups.length || new Set(rowIds).size !== rowIds.length || selection.groups.reduce((count, group) => count + group.rows.length, 0) !== rowIds.length || selection.incomplete.length || selection.unresolved.length) return null
  return { version: 1, payrollPeriod: batch.month, configurationEffectiveFrom: context.locationAllocationImportDate!, reviewed: false, reviewedSettingsApplicability: false,
    groups: selection.groups.map(group => ({ employeeId: 0, parts: group.rows.map(row => ({ rowId: row.id, workLocationId: 0, templateId: '', componentScopes: {} })) })) }
}

export function isLocationAllocationReviewOnly(previous: PayrollLocationAllocations | undefined, next: PayrollLocationAllocations | undefined): boolean {
  if (!previous || !next) return false
  const inputs = (value: PayrollLocationAllocations) => ({ ...value, reviewed: false, reviewedSettingsApplicability: false, groups: value.groups.map(group => ({ ...group, referenceAssignmentId: undefined })) })
  return JSON.stringify(inputs(previous)) === JSON.stringify(inputs(next))
}

export function patchLocationAllocationGroup(review: PayrollLocationAllocations, index: number, change: Partial<PayrollLocationGroup>): PayrollLocationAllocations {
  return { ...review, reviewed: false, reviewedSettingsApplicability: false, groups: review.groups.map((group, groupIndex) => groupIndex === index ? { ...group, ...change, referenceAssignmentId: Object.hasOwn(change, 'referenceAssignmentId') ? change.referenceAssignmentId : undefined } : group) }
}

export function patchLocationAllocationPart(review: PayrollLocationAllocations, groupIndex: number, rowId: string, change: Partial<PayrollLocationPart>): PayrollLocationAllocations {
  const group = review.groups[groupIndex]
  return patchLocationAllocationGroup(review, groupIndex, { parts: group.parts.map(part => part.rowId === rowId ? { ...part, ...change } : part) })
}
