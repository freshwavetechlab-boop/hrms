/** Employee workbooks use Indian day/month/year, independent of browser locale. */
export function normalizeEmployeeImportDate(value: string): string | null {
  let text = value.trim().replace(/^'/, '').replace(/\u00a0/g, ' ').trim()
  if (!text) return ''
  const timestamp = text.match(/^(.+?)[T ]([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:\.[0-9]{1,7})?)?(?:Z|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])?$/)
  if (timestamp) {
    if (Number(timestamp[2]) > 23 || Number(timestamp[3]) > 59 || Number(timestamp[4] || 0) > 59) return null
    text = timestamp[1].trim()
  }
  const date = (year: number, month: number, day: number) => {
    if (year < 1900 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31) return null
    const parsed = new Date(Date.UTC(year, month - 1, day))
    return parsed.getUTCFullYear() === year && parsed.getUTCMonth() === month - 1 && parsed.getUTCDate() === day ? parsed.toISOString().slice(0, 10) : null
  }
  const year = (raw: string) => raw.length === 2 ? Number(raw) + (Number(raw) <= 29 ? 2000 : 1900) : Number(raw)
  if (/^\d+(?:\.\d+)?$/.test(text)) {
    const serial = Math.floor(Number(text))
    if (serial < 1 || serial > 2958465 || serial === 60) return null
    const parsed = new Date(Date.UTC(1899, 11, serial < 60 ? 31 : 30) + serial * 86400000)
    return date(parsed.getUTCFullYear(), parsed.getUTCMonth() + 1, parsed.getUTCDate())
  }
  const iso = text.match(/^(\d{4})\s*[-/.]\s*(\d{1,2})\s*[-/.]\s*(\d{1,2})$/)
  if (iso) return date(Number(iso[1]), Number(iso[2]), Number(iso[3]))
  const numeric = text.match(/^(\d{1,2})\s*[-/.]\s*(\d{1,2})\s*[-/.]\s*(\d{2}|\d{4})$/)
  if (numeric) {
    const first = Number(numeric[1]), second = Number(numeric[2])
    return second > 12 && first <= 12 ? date(year(numeric[3]), first, second) : date(year(numeric[3]), second, first)
  }
  const months = ['january','february','march','april','may','june','july','august','september','october','november','december']
  const named = text.toLowerCase().replace(/[,./-]+/g, ' ').replace(/\s+/g, ' ').trim().split(' ')
  if (named.length === 3) {
    const monthToken = /^\d/.test(named[0]) ? named[1] : named[0]
    const month = months.findIndex(m => monthToken === m || monthToken === m.slice(0, 3) || m === 'september' && monthToken === 'sept') + 1
    const dayToken = /^\d/.test(named[0]) ? named[0] : named[1]
    if (/^\d{1,2}$/.test(dayToken) && /^(\d{2}|\d{4})$/.test(named[2])) return date(year(named[2]), month, Number(dayToken))
  }
  return null
}
