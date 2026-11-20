import type { EmployeeLoginProvisionResponse } from '../types/payroll'
import type { RecruitmentJoiningCandidate } from '../services/recruitmentJoiningService'

type SelectionStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
type Step = 'offers' | 'employees'
const key = (actorId: number, clientId: number, step: Step) => `employee.onboarding:${actorId}:${clientId}:${step}`
const validScope = (actorId: number, clientId: number) => Number.isSafeInteger(actorId) && actorId > 0 && Number.isSafeInteger(clientId) && clientId > 0
export function saveOnboardingSelection(storage: SelectionStorage, actorId: number, clientId: number, step: Step, values: string[]) {
  if (!validScope(actorId, clientId)) return
  const selected = [...new Set(values.filter(value => step === 'offers' ? /^(offer|roster):[1-9]\d*$/.test(value) : /^[1-9]\d*$/.test(value)))].slice(0, 5000)
  try { storage.setItem(key(actorId, clientId, step), JSON.stringify(selected)) } catch { /* Optional navigation convenience only. */ }
}
export function readOnboardingSelection(storage: SelectionStorage, actorId: number, clientId: number, step: Step): string[] | null {
  if (!validScope(actorId, clientId)) return null
  try {
    const selected: unknown = JSON.parse(storage.getItem(key(actorId, clientId, step)) ?? 'null')
    return Array.isArray(selected) && selected.every(value => typeof value === 'string' && (step === 'offers' ? /^(offer|roster):[1-9]\d*$/.test(value) : /^[1-9]\d*$/.test(value))) ? [...new Set(selected)].slice(0, 5000) : null
  } catch { return null }
}
export function clearOnboardingSelection(storage: SelectionStorage, actorId: number, clientId: number, step: Step) {
  if (validScope(actorId, clientId)) try { storage.removeItem(key(actorId, clientId, step)) } catch { /* Optional navigation convenience only. */ }
}
export function releasedForEmployeeMaster(row: RecruitmentJoiningCandidate, clientId: number) {
  return row.clientId === clientId && ['Offer', 'Roster'].includes(row.sourceType) && !row.employeeId && row.importStatus !== 'Imported'
    && (row.acceptanceRecorded || ['Released', 'Pending Candidate', 'Accepted'].includes(row.offerStatus))
}
/** Only freshly created credentials are downloadable. Never infer an existing password. */
export function createdEmployeeCredentials(response: EmployeeLoginProvisionResponse) {
  return response.results.filter(row => row.status === 'Created').map(row => ({
    employeeCode: row.employeeCode, employeeName: row.employeeName, loginId: row.email,
    temporaryPassword: row.temporaryPassword || response.temporaryPassword || '', essUrl: 'https://gad-ess.frevo.co.in/',
  }))
}
