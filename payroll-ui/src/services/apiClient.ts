import { toast } from '../components/ToastProvider'
import { recruitmentDeleteActions } from './recruitmentDeleteFeedback'

export const api = import.meta.env.VITE_API_URL ?? 'http://localhost:5062'

type ToastMode = boolean | 'error-only'
export type ApiOptions = RequestInit & { timeoutMs?: number; toast?: ToastMode; successMessage?: string; loader?: boolean }
export type ApiResult<TResult> = { ok: boolean; data: TResult; error: string; status: number }
type LoadingListener = (activeRequests: number) => void

const legacyTokenKey = 'payroll.auth.token'
const jsonContent = 'application/json'
const loadingListeners = new Set<LoadingListener>()
let activeRequests = 0

export function apiUrl(path: string) {
  return path.startsWith('http') ? path : `${api}${path}`
}

export function subscribeApiLoading(listener: LoadingListener) {
  loadingListeners.add(listener)
  listener(activeRequests)
  return () => { loadingListeners.delete(listener) }
}

function startLoading(options: ApiOptions) {
  if (options.loader === false) return () => {}
  activeRequests += 1
  notifyLoading()
  return () => {
    activeRequests = Math.max(0, activeRequests - 1)
    notifyLoading()
  }
}

function notifyLoading() {
  loadingListeners.forEach(listener => listener(activeRequests))
}

export async function apiRequest(path: string, options: ApiOptions = {}) {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), options.timeoutMs ?? 30000)
  const stopLoading = startLoading(options)
  const headers = new Headers(options.headers)
  const isFormData = options.body instanceof FormData
  const legacyToken = sessionStorage.getItem(legacyTokenKey) || localStorage.getItem(legacyTokenKey)

  if (options.body && !isFormData && !headers.has('Content-Type')) headers.set('Content-Type', jsonContent)
  if (legacyToken && !headers.has('Authorization')) headers.set('Authorization', `Bearer ${legacyToken}`)

  try {
    const response = await fetch(apiUrl(path), { ...options, headers, credentials: 'include', signal: options.signal ?? controller.signal })
    if (response.status === 401) {
      sessionStorage.removeItem(legacyTokenKey)
      localStorage.removeItem(legacyTokenKey)
      window.dispatchEvent(new CustomEvent('payroll:unauthorized'))
    }
    return response
  } finally {
    window.clearTimeout(timeout)
    stopLoading()
  }
}

export async function getJson<T>(path: string, fallback: T, options: ApiOptions = {}): Promise<T> {
  try {
    const response = await apiRequest(path, options)
    return response.ok ? await readJson<T>(response, fallback) : fallback
  } catch {
    return fallback
  }
}

export async function getJsonResult<T>(path: string, fallback: T, options: ApiOptions = {}): Promise<ApiResult<T>> {
  try {
    const response = await apiRequest(path, options)
    if (!response.ok) return { ok: false, data: fallback, error: await readError(response), status: response.status }
    return { ok: true, data: await readJson<T>(response, fallback), error: '', status: response.status }
  } catch (error) {
    const message = error instanceof DOMException && error.name === 'AbortError'
      ? 'Request timed out.'
      : error instanceof Error ? error.message : 'Request failed.'
    return { ok: false, data: fallback, error: message, status: 0 }
  }
}

export async function postJson<TBody, TResult>(path: string, body: TBody, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'POST', body: JSON.stringify(body) }, fallback)
}

export async function putJson<TBody, TResult>(path: string, body: TBody, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'PUT', body: JSON.stringify(body) }, fallback)
}

export async function postEmpty<TResult>(path: string, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'POST' }, fallback)
}

export async function deleteJson<TResult>(path: string, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'DELETE' }, fallback)
}

export async function postForm<TResult>(path: string, body: FormData, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'POST', body }, fallback)
}

export function postFormWithProgress<TResult>(path: string, body: FormData, fallback: TResult, onProgress: (percent: number) => void): Promise<ApiResult<TResult>> {
  return new Promise(resolve => {
    const request = new XMLHttpRequest()
    const legacyToken = sessionStorage.getItem(legacyTokenKey) || localStorage.getItem(legacyTokenKey)
    request.open('POST', apiUrl(path))
    request.withCredentials = true
    if (legacyToken) request.setRequestHeader('Authorization', `Bearer ${legacyToken}`)
    request.upload.onprogress = event => {
      if (event.lengthComputable) onProgress(Math.min(100, Math.round((event.loaded / event.total) * 100)))
    }
    request.onload = () => {
      if (request.status === 401) {
        sessionStorage.removeItem(legacyTokenKey)
        localStorage.removeItem(legacyTokenKey)
        window.dispatchEvent(new CustomEvent('payroll:unauthorized'))
      }
      if (request.status >= 200 && request.status < 300) {
        try {
          notifyMutation(path, 'POST', true, '')
          resolve({ ok: true, data: request.responseText ? JSON.parse(request.responseText) as TResult : fallback, error: '', status: request.status })
        } catch (error) {
          const message = error instanceof Error ? error.message : 'Invalid server response.'
          notifyMutation(path, 'POST', false, message)
          resolve({ ok: false, data: fallback, error: message, status: request.status })
        }
        return
      }
      const message = readErrorText(request.responseText, request.status)
      notifyMutation(path, 'POST', false, message)
      resolve({ ok: false, data: fallback, error: message, status: request.status })
    }
    request.onerror = () => { const message = 'Network error: unable to reach the API.'; notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.onabort = () => { const message = 'Upload was cancelled.'; notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.ontimeout = () => { const message = 'Upload timed out.'; notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.timeout = 120000
    request.send(body)
  })
}

export async function getBlob(path: string): Promise<ApiResult<Blob | null>> {
  try {
    const response = await apiRequest(path)
    return { ok: response.ok, data: response.ok ? await response.blob() : null, error: response.ok ? '' : await readError(response), status: response.status }
  } catch (error) {
    return { ok: false, data: null, error: error instanceof Error ? error.message : 'Request failed.', status: 0 }
  }
}

export async function readError(response: Response) {
  try {
    const data = await response.json()
    return data.error || data.detail || data.message || (data.errors ? JSON.stringify(data.errors) : '') || `Request failed with status ${response.status}.`
  } catch {
    return `Request failed with status ${response.status}.`
  }
}

function readErrorText(text: string, status: number) {
  if (!text) return `Request failed with status ${status}.`
  try {
    const data = JSON.parse(text)
    return data.error || data.detail || data.message || (data.errors ? JSON.stringify(data.errors) : '') || text
  } catch {
    return text
  }
}

async function mutateJson<TResult>(path: string, options: ApiOptions, fallback: TResult): Promise<ApiResult<TResult>> {
  try {
    const response = await apiRequest(path, options)
    const error = response.ok ? '' : await readError(response)
    notifyMutation(path, options.method, response.ok, error, options)
    return { ok: response.ok, data: response.ok ? await readJson<TResult>(response, fallback) : fallback, error, status: response.status }
  } catch (error) {
    const message = error instanceof DOMException && error.name === 'AbortError' ? 'Request timed out. Payroll may still be processing; refresh and check diagnostics.' : error instanceof Error ? error.message : 'Request failed.'
    notifyMutation(path, options.method, false, message, options)
    return { ok: false, data: fallback, error: message, status: 0 }
  }
}

async function readJson<T>(response: Response, fallback: T) {
  if (response.status === 204) return fallback
  const text = await response.text()
  return text ? JSON.parse(text) as T : fallback
}

function notifyMutation(path: string, method = 'POST', ok: boolean, error: string, options: ApiOptions = {}) {
  if (options.toast === false) return
  if (!ok) {
    const actions = method.toUpperCase() === 'DELETE' ? recruitmentDeleteActions(path, error) : []
    toast.error(error || 'Request failed.', actions.length ? { actions } : undefined)
    return
  }
  if (options.toast === 'error-only' || (!options.successMessage && options.toast !== true)) return
  toast.success(options.successMessage || successText(method))
}

function successText(method: string) {
  const verb = method.toUpperCase()
  if (verb === 'DELETE') return 'Deleted successfully.'
  if (verb === 'PUT') return 'Updated successfully.'
  return 'Saved successfully.'
}
