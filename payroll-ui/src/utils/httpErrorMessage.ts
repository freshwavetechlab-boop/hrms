export const uploadTooLargeMessage = 'Upload is too large. Choose a smaller file within the size limit shown for this document.'

// Reverse proxies may reject an oversized request before the API can return JSON.
// Keep useful API validation text, but never expose a proxy HTML page for HTTP 413.
export function readHttpErrorText(text: string, status: number, rawTextFallback = true, fallbackMessage?: string) {
  const fallback = status === 413 ? uploadTooLargeMessage : fallbackMessage || `Request failed with status ${status}.`
  if (!text) return fallback
  try {
    const data = JSON.parse(text)
    for (const key of ['error', 'detail', 'message'])
      if (typeof data?.[key] === 'string' && data[key].trim()) return data[key]
    if (data?.errors) return JSON.stringify(data.errors)
    return status === 413 ? fallback : rawTextFallback ? text : fallback
  } catch {
    return status === 413 ? fallback : rawTextFallback ? text : fallback
  }
}
