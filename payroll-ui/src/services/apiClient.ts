export const api = import.meta.env.VITE_API_URL ?? 'http://localhost:5062'

export async function getJson<T>(path: string, fallback: T): Promise<T> {
  const response = await fetch(`${api}${path}`)
  return response.ok ? response.json() : fallback
}

export async function postJson<TBody, TResult>(path: string, body: TBody, fallback: TResult): Promise<{ ok: boolean; data: TResult }> {
  const response = await fetch(`${api}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  return { ok: response.ok, data: response.ok ? await response.json() : fallback }
}

export async function putJson<TBody, TResult>(path: string, body: TBody, fallback: TResult): Promise<{ ok: boolean; data: TResult }> {
  const response = await fetch(`${api}${path}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  return { ok: response.ok, data: response.ok ? await response.json() : fallback }
}

export async function postEmpty<TResult>(path: string, fallback: TResult): Promise<{ ok: boolean; data: TResult }> {
  const response = await fetch(`${api}${path}`, { method: 'POST' })
  return { ok: response.ok, data: response.ok ? await response.json() : fallback }
}

export async function readError(response: Response) {
  try {
    const data = await response.json()
    return data.error || data.message || `Request failed with status ${response.status}.`
  } catch {
    return `Request failed with status ${response.status}.`
  }
}
