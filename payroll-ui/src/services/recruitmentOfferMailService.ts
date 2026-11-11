import { getJsonResult, postJson } from './apiClient'
import type { BulkMailJob } from '../components/BulkMailProgress'

export type OfferMailItem = { rosterId: number; workLocationId: number | null; candidateCode: string; candidateName: string; email: string; releaseId: number | null; queueId: number | null; status: string; message: string; sentCopyStatus: string; deliveryStatus: string }
export type OfferMailJob = BulkMailJob & { clientId: number; clientName: string; createdBy: string; dismissedAtUtc: string | null; items: OfferMailItem[] }
const root = '/api/recruitment/document-roster'
export const previewOfferMail = (clientId: number, ids: number[], retryFailed = false) => postJson(`${root}/mail-preview?clientId=${clientId}`, { ids, retryFailed }, { items: [] as OfferMailItem[] }, { toast: false, loader: false })
export const sendOfferMail = (clientId: number, ids: number[], requestId: string, items: OfferMailItem[], retryFailed = false) => postJson(`${root}/mail-jobs?clientId=${clientId}`, { ids, requestId, retryFailed, releaseIds: Object.fromEntries(items.map(item => [String(item.rosterId), item.releaseId || 0])) }, null as OfferMailJob | null, { toast: false, loader: false, timeoutMs: 120000 })
export const getOfferMailJobs = (clientId?: number) => getJsonResult<{ items: OfferMailJob[] }>(`${root}/mail-jobs${clientId ? `?clientId=${clientId}` : ''}`, { items: [] }, { toast: false, loader: false })
export const dismissOfferMailJob = (clientId: number, id: string) => postJson(`${root}/mail-jobs/${encodeURIComponent(id)}/dismiss?clientId=${clientId}`, {}, { ok: false }, { toast: false, loader: false })
export const validOfferMailJobs = (items: unknown): items is OfferMailJob[] => Array.isArray(items) && items.every(job => job && typeof job.id === 'string' && Number.isSafeInteger(job.clientId) && job.clientId > 0 && typeof job.clientName === 'string' && typeof job.isComplete === 'boolean' && Number.isInteger(job.total) && job.total >= 0 && Number.isInteger(job.percentComplete) && job.percentComplete >= 0 && job.percentComplete <= 100 && Array.isArray(job.items))
