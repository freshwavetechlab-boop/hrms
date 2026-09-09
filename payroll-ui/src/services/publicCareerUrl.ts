export function normalizePublicCareerUrl(value?: string | null, publicSlug?: string, runtimeOrigin?: string) {
  if (!value?.trim()) return ''
  try {
    const url = new URL(value.trim())
    if (!['http:', 'https:'].includes(url.protocol)) return ''
    const parts = url.pathname.split('/').filter(Boolean)
    if (parts.length < 2 || parts.at(-2)?.toLowerCase() !== 'careers') return ''
    if (publicSlug && decodeURIComponent(parts.at(-1) || '') !== publicSlug) return ''
    const currentOrigin = runtimeOrigin ?? (typeof window === 'undefined' ? '' : window.location.origin)
    const current = currentOrigin ? new URL(currentOrigin) : null
    if (current && isLoopback(url.hostname)) {
      url.protocol = current.protocol
      url.hostname = current.hostname
      url.port = current.port
      url.username = ''
      url.password = ''
    }
    url.search = ''
    url.hash = ''
    return url.toString()
  } catch {
    return ''
  }
}

function isLoopback(hostname: string) {
  const normalized = hostname.toLowerCase().replace(/^\[|\]$/g, '')
  return normalized === 'localhost' || normalized === '::1' || normalized === '0.0.0.0' || normalized.startsWith('127.')
}
