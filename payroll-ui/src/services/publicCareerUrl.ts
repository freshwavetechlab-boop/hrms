export function normalizePublicCareerUrl(value?: string | null, publicSlug?: string) {
  if (!value?.trim()) return ''
  try {
    const url = new URL(value.trim())
    if (!['http:', 'https:'].includes(url.protocol)) return ''
    const parts = url.pathname.split('/').filter(Boolean)
    if (parts.length < 2 || parts.at(-2)?.toLowerCase() !== 'careers') return ''
    if (publicSlug && decodeURIComponent(parts.at(-1) || '') !== publicSlug) return ''
    url.search = ''
    url.hash = ''
    return url.toString()
  } catch {
    return ''
  }
}
