export function isApiWrite(method?: string) {
  return !['GET', 'HEAD', 'OPTIONS'].includes((method || 'GET').toUpperCase())
}

export function isFetchNetworkFailure(error: unknown) {
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
  return /^(?:failed to fetch|load failed|fetch failed|network request failed)[.!]?$|networkerror(?:\s|:|$)/i.test(message.trim())
}

export function noReadableApiResponse(write: boolean) {
  return write
    ? 'The browser did not receive a readable response from the API. The action may have completed. Check the current record or list before retrying.'
    : 'The browser did not receive a readable response from the API. Check your connection and try loading the record again.'
}

export function unreadableApiResponse(status: number, write: boolean) {
  return `The server responded (HTTP ${status}), but its response could not be read. ${write
    ? 'The action\'s final status is unknown. Check the current record or list before retrying.'
    : 'Try loading the record again.'}`
}

export function apiTransportError(error: unknown, write: boolean, timeoutMessage?: string, cancelled = false) {
  if (error && typeof error === 'object' && 'name' in error && error.name === 'AbortError') {
    if (cancelled) return 'Request was cancelled.'
    return timeoutMessage || (write
      ? 'Request timed out. Processing may still be in progress; refresh and check the current status before retrying.'
      : 'Request timed out. Check your connection and try loading the record again.')
  }
  if (isFetchNetworkFailure(error)) return noReadableApiResponse(write)
  return error instanceof Error && error.message ? error.message : 'The request could not be completed.'
}

// Use only an identifier supplied in response headers. Never derive a diagnostic
// ID from an HTTP code, local timestamp, or authorization token.
export function serverRequestId(readHeader: (name: string) => string | null | undefined): string | undefined {
  for (const name of ['X-Request-ID', 'X-Correlation-ID']) {
    let value: string | null | undefined
    try { value = readHeader(name) } catch { continue }
    if (typeof value !== 'string' || value.length > 128 || /[\r\n]/.test(value)) continue
    const candidate = value.trim()
    if (/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(candidate)) return candidate
  }
  return undefined
}
