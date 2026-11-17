type SavedBatch = { id: string; clientId: number; month: string; locationLabel?: string }

export function savedPayslipLocationKey(batch: Pick<SavedBatch, 'locationLabel'>) {
  return batch.locationLabel ? `location:${batch.locationLabel}` : 'legacy'
}

export function savedPayslipMonthLabel(month: string) {
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) return month
  const [year, number] = month.split('-').map(Number)
  return new Date(year, number - 1, 1).toLocaleDateString('en-IN', { month: 'long', year: 'numeric' })
}

// Browsing scope only: preserve every saved version and its original values.
export function savedPayslipHistoryScope<T extends SavedBatch>(history: T[], clientId: number, preferredMonth: string, preferredLocation: string, activeBatch?: SavedBatch | null) {
  const authorized = history.filter(batch => batch.clientId === clientId)
  const active = activeBatch?.clientId === clientId ? activeBatch : undefined
  const months = [...new Set([...authorized.map(batch => batch.month), ...(active ? [active.month] : [])])]
    .filter(month => /^\d{4}-(0[1-9]|1[0-2])$/.test(month)).sort().reverse()
  const month = active?.month || (months.includes(preferredMonth) ? preferredMonth : months[0] || '')
  const monthRows = authorized.filter(batch => batch.month === month)
  const locations = [...new Map([...monthRows, ...(active ? [active] : [])].map(batch => [savedPayslipLocationKey(batch), {
    value: savedPayslipLocationKey(batch), label: batch.locationLabel || 'Existing batch (no location label)',
  }])).values()].sort((a, b) => a.label.localeCompare(b.label))
  // Accept the old plain-label preference when upgrading the browsing controls.
  const preferredKey = locations.some(option => option.value === preferredLocation) ? preferredLocation
    : monthRows.find(batch => batch.locationLabel === preferredLocation && preferredLocation) ? `location:${preferredLocation}` : ''
  const location = active ? savedPayslipLocationKey(active) : preferredKey
  return { months, month, locations, location, rows: monthRows.filter(batch => !location || savedPayslipLocationKey(batch) === location) }
}
