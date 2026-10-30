import type { Employee } from '../types/payroll'

export type EmployeeCodeScope = { clientId: number; period: string; policyBatchId: string }
export type EmployeeCodeCandidate = Pick<Employee, 'id' | 'clientId' | 'employeeCode' | 'isActive'>
export type EmployeeCodeResolution = { employeeIds: number[]; codes: string[]; errors: string[]; fingerprint: string; inputText: string }
const normalized = (value: string) => value.trim().toLowerCase()

export function resolveRegularEmployeeCodes(inputText: string, scope: EmployeeCodeScope, eligibleEmployees: EmployeeCodeCandidate[]): EmployeeCodeResolution {
  const errors: string[] = [], ids: number[] = [], codes: string[] = []
  if (!Number.isSafeInteger(scope.clientId) || scope.clientId <= 0 || !/^\d{4}-(0[1-9]|1[0-2])$/.test(scope.period) || !scope.policyBatchId.trim()) errors.push('Choose the current client, payroll month and attendance policy first.')
  const roster = eligibleEmployees.filter(row => row.clientId === scope.clientId && row.isActive === true)
    .map(row => ({ id: row.id, code: typeof row.employeeCode === 'string' ? row.employeeCode.trim() : '' })).sort((a, b) => a.id - b.id || a.code.localeCompare(b.code))
  const fingerprint = JSON.stringify({ scope, inputText, roster })
  if (inputText.length > 10000) return { employeeIds: [], codes: [], errors: ['Enter at most 100 employee codes.'], fingerprint, inputText }
  const requested = inputText.split(/[,\r\n]+/).map(value => value.trim()).filter(Boolean)
  if (!requested.length || requested.length > 100 || requested.some(value => value.length > 100)) errors.push('Enter between 1 and 100 employee codes, separated by commas or new lines.')
  const unique = new Set<string>(), byCode = new Map<string, { id: number; code: string }[]>()
  for (const row of roster) byCode.set(normalized(row.code), [...(byCode.get(normalized(row.code)) || []), row])
  if (!errors.length) for (const code of requested) {
    const key = normalized(code)
    if (unique.has(key)) { errors.push(`Repeated input code: ${code}.`); continue }
    unique.add(key)
    const matches = byCode.get(key) || []
    if (matches.length !== 1) { errors.push(matches.length ? `More than one eligible Master record uses code ${code}.` : `Code ${code} is not in this client's current eligible attendance-policy roster.`); continue }
    const match = matches[0]
    if (!Number.isSafeInteger(match.id) || match.id <= 0 || ids.includes(match.id)) { errors.push(`Code ${code} has an invalid or repeated Master identity.`); continue }
    ids.push(match.id); codes.push(match.code)
  }
  return { employeeIds: errors.length ? [] : ids, codes: errors.length ? [] : codes, errors, fingerprint, inputText }
}

export function employeeCodeResolutionIsCurrent(reviewed: EmployeeCodeResolution | null, inputText: string, scope: EmployeeCodeScope, employees: EmployeeCodeCandidate[]) {
  if (!reviewed || reviewed.errors.length || !reviewed.employeeIds.length) return false
  const fresh = resolveRegularEmployeeCodes(inputText, scope, employees)
  return !fresh.errors.length && reviewed.fingerprint === fresh.fingerprint
}
