import { getJsonResult, postJson, putJson } from './apiClient'
import type { CommunicationTemplate, EmployeeCommunicationCampaign, EmployeeCommunicationPreview } from '../types/employeeCommunication'
export type EmployeeCompletionStatus = { employeeId: number; clientId: number; missingFields: string[]; canSendEmail: boolean; disabledReason: string }
export type CompletionSetup = { firstEditEnabled: boolean; template: CommunicationTemplate }
export type CompletionMail = { clientId: number; employeeIds: number[]; idempotencyKey: string }
const root = '/api/employees/profile-completion'
export const getEmployeeCompletion = (clientId: number) => getJsonResult<EmployeeCompletionStatus[]>(root + '?clientId=' + clientId, [], { loader: false, toast: false })
export const setupEmployeeCompletion = (clientId: number) => postJson<{ clientId: number }, CompletionSetup | null>(root + '/setup', { clientId }, null, { toast: false })
export const saveEmployeeCompletionPolicy = (clientId: number, firstEditEnabled: boolean) => putJson(root + '/settings', { clientId, firstEditEnabled }, null)
export const previewEmployeeCompletion = (request: CompletionMail) => postJson<CompletionMail, EmployeeCommunicationPreview | null>(root + '/preview', request, null, { toast: false })
export const sendEmployeeCompletion = (request: CompletionMail) => postJson<CompletionMail, EmployeeCommunicationCampaign | null>(root + '/send', request, null, { toast: false, timeoutMs: 120000 })
