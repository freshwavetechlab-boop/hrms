import { toast } from '../components/ToastProvider'
import { resolveRuntimeApiBase } from './apiBaseUrl'
import { recruitmentDeleteActions } from './recruitmentDeleteFeedback'
import { activeClientHeader, beginClientScopedWrite, configureClientRequestScope } from './clientRequestScope'
import { readHttpErrorText } from '../utils/httpErrorMessage'
import { apiTransportError, isApiWrite, isFetchNetworkFailure, noReadableApiResponse, serverRequestId, unreadableApiResponse } from '../utils/apiTransportFeedback'

export const api = resolveRuntimeApiBase(
  import.meta.env.VITE_API_URL,
  typeof window === 'undefined' ? undefined : window.location.origin,
  import.meta.env.DEV,
)

type ToastMode = boolean | 'error-only'
export type ApiOptions = RequestInit & { timeoutMs?: number; timeoutMessage?: string; toast?: ToastMode; successMessage?: string; loader?: boolean }
export type ApiResult<TResult> = { ok: boolean; data: TResult; error: string; status: number; requestId?: string }
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
  const stopScopedWrite = beginClientScopedWrite(path, options.method)
  const headers = new Headers(options.headers)
  const isFormData = options.body instanceof FormData
  const legacyToken = sessionStorage.getItem(legacyTokenKey) || localStorage.getItem(legacyTokenKey)

  if (options.body && !isFormData && !headers.has('Content-Type')) headers.set('Content-Type', jsonContent)
  if (legacyToken && !headers.has('Authorization')) headers.set('Authorization', `Bearer ${legacyToken}`)
  const activeClient = activeClientHeader(path, options.method)
  if (activeClient) headers.set('X-Client-Id', activeClient)

  try {
    const signal = options.signal ? AbortSignal.any([options.signal, controller.signal]) : controller.signal
    const response = await fetch(apiUrl(path), { ...options, headers, credentials: 'include', signal })
    if (response.status === 403 && activeClient) window.dispatchEvent(new Event('payroll:client-access-refresh'))
    if (response.status === 401) {
      configureClientRequestScope(null)
      sessionStorage.removeItem(legacyTokenKey)
      localStorage.removeItem(legacyTokenKey)
      window.dispatchEvent(new CustomEvent('payroll:unauthorized'))
    }
    return response
  } catch (error) {
    if (isFetchNetworkFailure(error)) throw new TypeError(noReadableApiResponse(isApiWrite(options.method)))
    throw error
  } finally {
    window.clearTimeout(timeout)
    stopLoading()
    stopScopedWrite()
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
  let response: Response | undefined
  let requestId: string | undefined
  try {
    response = await apiRequest(path, options)
    requestId = serverRequestId(name => response?.headers?.get(name))
    const diagnostic = requestId ? { requestId } : {}
    if (!response.ok) return { ok: false, data: fallback, error: await readError(response), status: response.status, ...diagnostic }
    return { ok: true, data: await readJson<T>(response, fallback), error: '', status: response.status, ...diagnostic }
  } catch (error) {
    const message = response ? unreadableApiResponse(response.status, false)
      : apiTransportError(error, false, options.timeoutMessage, options.signal?.aborted)
    return { ok: false, data: fallback, error: message, status: response?.status ?? 0, ...(requestId ? { requestId } : {}) }
  }
}

export async function postJson<TBody, TResult>(path: string, body: TBody, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'POST', body: JSON.stringify(body) }, fallback)
}

export async function putJson<TBody, TResult>(path: string, body: TBody, fallback: TResult, options: ApiOptions = {}): Promise<ApiResult<TResult>> {
  return mutateJson(path, { ...options, method: 'PUT', body: JSON.stringify(body) }, fallback)
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

export function postFormWithProgress<TResult>(path: string, body: FormData, fallback: TResult, onProgress: (percent: number) => void, timeoutMs = 120000, timeoutMessage = 'Upload timed out.'): Promise<ApiResult<TResult>> {
  return new Promise(resolve => {
    const request = new XMLHttpRequest()
    const stopScopedWrite = beginClientScopedWrite(path, 'POST')
    let scopedWriteFinished = false
    const finishScopedWrite = () => { if (!scopedWriteFinished) { scopedWriteFinished = true; stopScopedWrite() } }
    const legacyToken = sessionStorage.getItem(legacyTokenKey) || localStorage.getItem(legacyTokenKey)
    request.open('POST', apiUrl(path))
    request.withCredentials = true
    if (legacyToken) request.setRequestHeader('Authorization', `Bearer ${legacyToken}`)
    const activeClient = activeClientHeader(path, 'POST')
    if (activeClient) request.setRequestHeader('X-Client-Id', activeClient)
    request.onloadend = finishScopedWrite
    request.upload.onprogress = event => {
      if (event.lengthComputable) onProgress(Math.min(100, Math.round((event.loaded / event.total) * 100)))
    }
    request.onload = () => {
      const requestId = serverRequestId(name => request.getResponseHeader?.(name))
      const diagnostic = requestId ? { requestId } : {}
      if (request.status === 403 && activeClient) window.dispatchEvent(new Event('payroll:client-access-refresh'))
      if (request.status === 401) {
        configureClientRequestScope(null)
        sessionStorage.removeItem(legacyTokenKey)
        localStorage.removeItem(legacyTokenKey)
        window.dispatchEvent(new CustomEvent('payroll:unauthorized'))
      }
      if (request.status >= 200 && request.status < 300) {
        try {
          const data = request.responseText ? JSON.parse(request.responseText) as TResult : fallback
          notifyMutation(path, 'POST', true, '')
          resolve({ ok: true, data, error: '', status: request.status, ...diagnostic })
        } catch (error) {
          const message = unreadableApiResponse(request.status, true)
          notifyMutation(path, 'POST', false, message)
          resolve({ ok: false, data: fallback, error: message, status: request.status, ...diagnostic })
        }
        return
      }
      const message = request.status === 0 ? noReadableApiResponse(true) : readErrorText(request.responseText, request.status)
      notifyMutation(path, 'POST', false, message)
      resolve({ ok: false, data: fallback, error: message, status: request.status, ...diagnostic })
    }
    request.onerror = () => { const message = noReadableApiResponse(true); notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.onabort = () => { const message = 'Upload was cancelled.'; notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.ontimeout = () => { const message = timeoutMessage; notifyMutation(path, 'POST', false, message); resolve({ ok: false, data: fallback, error: message, status: 0 }) }
    request.timeout = timeoutMs
    try { request.send(body) }
    catch (error) {
      finishScopedWrite()
      const message = apiTransportError(error, true)
      notifyMutation(path, 'POST', false, message)
      resolve({ ok: false, data: fallback, error: message, status: 0 })
    }
  })
}

export async function getBlob(path: string): Promise<ApiResult<Blob | null>> {
  let response: Response | undefined
  let requestId: string | undefined
  try {
    response = await apiRequest(path)
    requestId = serverRequestId(name => response?.headers?.get(name))
    return { ok: response.ok, data: response.ok ? await response.blob() : null, error: response.ok ? '' : await readError(response), status: response.status, ...(requestId ? { requestId } : {}) }
  } catch (error) {
    return { ok: false, data: null, error: response ? unreadableApiResponse(response.status, false) : apiTransportError(error, false), status: response?.status ?? 0, ...(requestId ? { requestId } : {}) }
  }
}

export async function readError(response: Response) {
  try {
    return readHttpErrorText(await response.text(), response.status, false)
  } catch {
    return readHttpErrorText('', response.status, false)
  }
}

function readErrorText(text: string, status: number) {
  return readHttpErrorText(text, status)
}

async function mutateJson<TResult>(path: string, options: ApiOptions, fallback: TResult): Promise<ApiResult<TResult>> {
  let response: Response | undefined
  let requestId: string | undefined
  try {
    response = await apiRequest(path, options)
    requestId = serverRequestId(name => response?.headers?.get(name))
    const error = response.ok ? '' : await readError(response)
    const data = response.ok ? await readJson<TResult>(response, fallback) : fallback
    notifyMutation(path, options.method, response.ok, error, options)
    return { ok: response.ok, data, error, status: response.status, ...(requestId ? { requestId } : {}) }
  } catch (error) {
    const message = response ? unreadableApiResponse(response.status, true)
      : apiTransportError(error, true, options.timeoutMessage, options.signal?.aborted)
    notifyMutation(path, options.method, false, message, options)
    return { ok: false, data: fallback, error: message, status: response?.status ?? 0, ...(requestId ? { requestId } : {}) }
  }
}

async function readJson<T>(response: Response, fallback: T) {
  if (response.status === 204) return fallback
  const text = await response.text()
  return text ? JSON.parse(text) as T : fallback
}

function notifyMutation(path: string, method = 'POST', ok: boolean, error: string, options: ApiOptions = {}) {
  if (ok && typeof window !== 'undefined' && /^\/api\/(workflows|recruitment|recruitment-orchestration|leave-attendance|work-locations|employees)(\/|$)/.test(path))
    window.dispatchEvent(new Event('hrms:actions-changed'))
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
