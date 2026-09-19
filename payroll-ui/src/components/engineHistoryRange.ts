export type HistoryPeriod = 'live' | 'today' | 'yesterday' | 'week' | 'custom'
export const localDateInput = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
export function engineHistoryRange(period: HistoryPeriod, start: string, end: string, now = new Date()) {
  const from = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const until = new Date(from); until.setDate(until.getDate() + 1)
  if (period === 'yesterday') { from.setDate(from.getDate() - 1); until.setDate(until.getDate() - 1) }
  if (period === 'week') from.setDate(from.getDate() - 6)
  if (period === 'custom') {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(start) || !/^\d{4}-\d{2}-\d{2}$/.test(end)) throw new Error('Choose both dates.')
    const a = new Date(`${start}T00:00:00`); const b = new Date(`${end}T00:00:00`)
    if (!Number.isFinite(a.getTime()) || !Number.isFinite(b.getTime()) || localDateInput(a) !== start || localDateInput(b) !== end) throw new Error('Choose valid dates.')
    b.setDate(b.getDate() + 1); from.setTime(a.getTime()); until.setTime(b.getTime())
  }
  if (until <= from || until.getTime() - from.getTime() > 31 * 86400000 || from > now || from.getTime() < now.getTime() - 31 * 86400000) throw new Error('Choose a valid range within the last 30 days.')
  return { from: from.toISOString(), until: until.toISOString() }
}
