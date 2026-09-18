const hasExplicitZone = (value: string) => /(?:z|[+-]\d{2}:?\d{2})$/i.test(value.trim())

// MySQL DATETIME values in this API are stored in UTC but are serialized
// without a trailing zone. Treating them as browser-local shifts every HR
// timestamp by the user's offset.
export function apiUtcDate(value: string | Date) {
  if (value instanceof Date) return value
  const source = String(value || '').trim()
  return new Date(source && !hasExplicitZone(source) ? `${source}Z` : source)
}

export function formatApiDateTime(value: string | Date) {
  const date = apiUtcDate(value)
  return Number.isNaN(date.getTime()) ? String(value || '-') : date.toLocaleString('en-IN')
}
