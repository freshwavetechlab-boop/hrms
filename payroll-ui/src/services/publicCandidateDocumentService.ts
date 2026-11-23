import { apiUrl } from './apiClient'
import { readHttpErrorText } from '../utils/httpErrorMessage'

export type CandidateDocumentRequirement = {
  id: string
  label: string
  required: boolean
  minimumFileCount: number
  maximumFileCount: number
  maximumFileSizeBytes: number
  allowedExtensions: string[]
  requiresDocumentNumber: boolean
  requiresIssueDate: boolean
  requiresExpiryDate: boolean
}
export type CandidateDocumentRequest = {
  status: 'Open' | 'Completed' | 'Expired' | 'Unavailable'
  message: string
  expiresAtUtc?: string | null
  documents: CandidateDocumentRequirement[]
}
export type CandidateDocumentSubmission = { status: 'Completed'; alreadySubmitted: boolean; message: string }
export type CandidateDocumentMetadata = { id: string; documentNumber: string; issueDate?: string; expiryDate?: string }
export type CandidateDocumentResult<T> = { ok: boolean; data: T | null; error: string; status: number }
const root = '/api/public/recruitment/document-requests'

async function request<T>(token: string, init: RequestInit, signal?: AbortSignal): Promise<CandidateDocumentResult<T>> {
  const timeout = new AbortController()
  const timer = window.setTimeout(() => timeout.abort(), init.method === 'POST' ? 180000 : 30000)
  try {
    // This external page must work independently of any HRMS login or selected client.
    const response = await fetch(apiUrl(`${root}/${encodeURIComponent(token)}`), {
      ...init, credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer',
      signal: signal ? AbortSignal.any([signal, timeout.signal]) : timeout.signal,
      headers: { Accept: 'application/json' },
    })
    const text = await response.text()
    let body: T | null = null
    try { body = text ? JSON.parse(text) as T : null } catch { /* Keep a non-JSON proxy response out of the external page. */ }
    return response.ok
      ? { ok: true, data: body as T | null, error: '', status: response.status }
      : { ok: false, data: null, error: readHttpErrorText(text, response.status, false, 'We could not complete this request. Please try again.'), status: response.status }
  } catch (error) {
    return { ok: false, data: null, error: error instanceof DOMException && error.name === 'AbortError'
      ? 'The request timed out. Your selected files are still available. Please try again.'
      : 'We could not connect. Check your connection and try again.', status: 0 }
  } finally { window.clearTimeout(timer) }
}

export const getCandidateDocumentRequest = (token: string, signal?: AbortSignal) => request<CandidateDocumentRequest>(token, { method: 'GET' }, signal)

export function submitCandidateDocuments(token: string, email: string, documents: CandidateDocumentMetadata[], files: Record<string, File[]>) {
  const form = new FormData()
  form.append('email', email.trim())
  form.append('metadata', JSON.stringify({ documents }))
  for (const [id, selected] of Object.entries(files)) for (const file of selected) form.append(`files-${id}`, file, file.name)
  return request<CandidateDocumentSubmission>(token, { method: 'POST', body: form })
}
