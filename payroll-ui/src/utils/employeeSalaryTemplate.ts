import type { Employee, Structure } from '../types/payroll'
import type { EmployeeImportDecision, EmployeeImportPreflight } from '../services/settingsService'
import { salaryTemplateClientIds, scopeSalaryTemplates } from './salaryTemplateContext'

const referenceId = (value: string | number | null | undefined) => String(value ?? '').split(':')[0].trim()

export function currentSalaryTemplateId(employee: Employee) {
  const id = referenceId(employee.salaryStructureId)
  return id === '0' ? '' : id
}

export function currentSalaryTemplateLabel(employee: Employee) {
  const id = currentSalaryTemplateId(employee)
  return id ? employee.salaryTemplateName?.trim() || `Template #${id}` : 'Without salary template'
}

export function usesEmployeeTemplateInputs(employee: Employee) {
  return !!employee.salaryTemplatePolicyId || !!employee.usesDatedSalaryInputs
}

export function employeeSalaryAssignmentKey(employee: Employee) {
  return `${currentSalaryTemplateId(employee)}:${employee.salaryTemplatePolicyId || ''}`
}

/** An employee can choose an active client/shared template, never another client's fallback. */
export function employeeSalaryTemplates(templates: Structure[], clientId: string | number): Structure[] {
  return scopeSalaryTemplates(templates, Number(referenceId(clientId))).filter(template => template.active !== false)
}

/** A missing assignment stays missing until an explicit template selection. */
export function assignedEmployeeSalaryTemplate(templates: Structure[], clientId: string | number, assignment: string | number | null | undefined): Structure | undefined {
  const id = referenceId(assignment)
  return id && id !== '0' ? employeeSalaryTemplates(templates, clientId).find(template => String(template.id) === id) : undefined
}

/** Keep globally available and authorized multi-client definitions visible in the template workspace. */
export function salaryTemplatesForVisibleClients(templates: Structure[], clients: Iterable<string | number>): Structure[] {
  const ids = new Set([...clients].map(value => Number(referenceId(value))).filter(value => Number.isSafeInteger(value) && value > 0))
  return templates.filter(template => /^0?$/.test(String(template.clientId ?? '').trim())
    || salaryTemplateClientIds(template.clientId).some(id => ids.has(id)))
}

/** Standalone assignment uses the exact directory IDs used to generate its workbook. */
export function reviewedSalaryAssignment(review: EmployeeImportPreflight, selected: Employee[], templateId: string, removeTestPayroll = false) {
  const decisions: EmployeeImportDecision[] = []
  const blocked: { id: number; employee: string; reason: string }[] = []
  const rowNumbers = new Set(review.rows.map(row => row.rowNumber))
  if (review.errors?.length) throw new Error(review.errors.join(' '))
  if (!review.reviewToken || review.totalRows !== selected.length || review.rows.length !== selected.length
      || rowNumbers.size !== selected.length || selected.some((_, index) => !rowNumbers.has(index + 2)))
    throw new Error('The API could not verify every selected employee. Refresh Employee Master and try again.')
  let ready = 0, alreadyAssigned = 0
  for (const row of review.rows) {
    const employee = selected[row.rowNumber - 2]
    const sameEmployee = row.matchedEmployeeId === employee.id && row.matchedEmployeeCode?.trim().toUpperCase() === employee.employeeCode.trim().toUpperCase() && !row.canResolveConflict
    const exact = sameEmployee && ['MatchedByEmployeeId', 'MatchedByEmployeeCode'].includes(row.matchStatus)
    const payrollCount = row.testPayslips?.length ?? 0
    const removal = row.changes.find(change => change.field === 'removeTestPayslips')
    const validRemoval = removeTestPayroll && payrollCount > 0 && removal?.oldValue === String(payrollCount) && removal.newValue === '0'
    const assignmentOnly = (!payrollCount || validRemoval) && row.changes.every(change =>
      change.field === 'salaryTemplateId' && change.newValue === templateId || change.field === 'removeTestPayslips' && validRemoval)
    const reason = row.blockingReasons.join(' ') || (!exact ? 'The selected employee ID and code no longer match the active client record.'
      : !assignmentOnly ? 'The review returned a different change. Refresh Employee Master and try again.' : '')
    if (reason) {
      blocked.push({ id: employee.id, employee: `${employee.employeeCode} · ${employee.firstName} ${employee.lastName}`.trim(), reason })
      decisions.push({ rowNumber: row.rowNumber, sheet: row.sheet, action: 'skip' })
    } else if (row.changes.length) {
      ready++
      decisions.push({ rowNumber: row.rowNumber, sheet: row.sheet, action: 'update', employeeId: employee.id,
        fieldChoices: Object.fromEntries(row.changes.map(change => [change.field, 'useImported' as const])) })
    } else alreadyAssigned++
  }
  if (ready && !review.canImport) throw new Error('The API could not approve this selection. Refresh Employee Master and try again.')
  return { ready, alreadyAssigned, blocked, decisions }
}
