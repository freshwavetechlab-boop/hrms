export const api = (import.meta.env.VITE_API_URL ?? '').replace(/\/$/, '')

type ApiOptions = RequestInit & { timeoutMs?: number }
type ApiResult<TResult> = { ok: boolean; data: TResult; error: string; status: number }

const jsonContent = 'application/json'

export function apiUrl(path: string) {
  return path.startsWith('http') ? path : `${api}${path}`
}

export async function apiRequest(path: string, options: ApiOptions = {}) {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), options.timeoutMs ?? 30000)
  const headers = new Headers(options.headers)
  const isFormData = options.body instanceof FormData

  if (options.body && !isFormData && !headers.has('Content-Type')) headers.set('Content-Type', jsonContent)

  try {
    const response = await fetch(apiUrl(path), { ...options, headers, credentials: 'include', signal: options.signal ?? controller.signal })
    if (response.status === 401) {
      window.dispatchEvent(new CustomEvent('payroll:unauthorized'))
    }
    return response
  } finally {
    window.clearTimeout(timeout)
  }
}

export async function getJson<T>(path: string, fallback: T): Promise<T> {
  try {
    const response = await apiRequest(path)
    return response.ok ? await readJson<T>(response, fallback) : fallback
  } catch {
    return fallback
  }
}

export async function postJson<TBody, TResult>(path: string, body: TBody, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'POST', body: JSON.stringify(body) }, fallback)
}

export async function putJson<TBody, TResult>(path: string, body: TBody, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'PUT', body: JSON.stringify(body) }, fallback)
}

export async function postEmpty<TResult>(path: string, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'POST' }, fallback)
}

export async function deleteJson<TResult>(path: string, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'DELETE' }, fallback)
}

export async function postForm<TResult>(path: string, body: FormData, fallback: TResult): Promise<ApiResult<TResult>> {
  return mutateJson(path, { method: 'POST', body }, fallback)
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
          resolve({ ok: true, data: request.responseText ? JSON.parse(request.responseText) as TResult : fallback, error: '', status: request.status })
        } catch (error) {
          resolve({ ok: false, data: fallback, error: error instanceof Error ? error.message : 'Invalid server response.', status: request.status })
        }
        return
      }
      resolve({ ok: false, data: fallback, error: readErrorText(request.responseText, request.status), status: request.status })
    }
    request.onerror = () => resolve({ ok: false, data: fallback, error: 'Network error: unable to reach the API.', status: 0 })
    request.onabort = () => resolve({ ok: false, data: fallback, error: 'Upload was cancelled.', status: 0 })
    request.ontimeout = () => resolve({ ok: false, data: fallback, error: 'Upload timed out.', status: 0 })
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
    return { ok: response.ok, data: response.ok ? await readJson<TResult>(response, fallback) : fallback, error: response.ok ? '' : await readError(response), status: response.status }
  } catch (error) {
    return { ok: false, data: fallback, error: error instanceof Error ? error.message : 'Request failed.', status: 0 }
  }
}

async function readJson<T>(response: Response, fallback: T) {
  if (response.status === 204) return fallback
  const text = await response.text()
  return text ? JSON.parse(text) as T : fallback
}
