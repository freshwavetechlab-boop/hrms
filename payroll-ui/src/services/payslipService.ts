import { getJsonResult, postJson } from './apiClient'

export type PayslipDocument = { payRunId: number; payPeriod: string; employeeCode: string; fileName: string; html: string }
export type PayslipDeliveryItem = { employeeId: number; employeeCode: string; status: 'Queued' | 'Already queued' | 'Excluded'; message: string }
export type PayslipDeliveryResult = { items: PayslipDeliveryItem[]; queuedCount: number; alreadyQueuedCount: number; excludedCount: number }

export const getRegisterPayslip = (payRunId: number, employeeId: number) => getJsonResult<PayslipDocument | null>(`/api/pay-runs/${payRunId}/payslips/${employeeId}`, null, { loader: false })
export const sendRegisterPayslips = (payRunId: number, employeeIds: number[]) => postJson(`/api/pay-runs/${payRunId}/payslips/send`, { employeeIds }, { items: [], queuedCount: 0, alreadyQueuedCount: 0, excludedCount: 0 } as PayslipDeliveryResult, { loader: false, toast: false, timeoutMs: 120000 })
