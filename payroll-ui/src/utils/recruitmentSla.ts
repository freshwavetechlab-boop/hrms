export const recruitmentDuration = (seconds = 0) => {
  const total = Math.max(0, Math.floor(seconds))
  const days = Math.floor(total / 86400)
  return `${days ? `${days}d ` : ''}${String(Math.floor(total % 86400 / 3600)).padStart(2, '0')}:${String(Math.floor(total % 3600 / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`
}

const utcTime = (value: string) => Date.parse(/(?:Z|[+-]\d\d:\d\d)$/i.test(value) ? value : `${value}Z`)
export function recruitmentJobSla(job: { overallDueAtUtc?: string | null; completedAtUtc?: string | null; overallSlaPaused?: boolean }, now: number, syncedAt = now) {
  const due = job.overallDueAtUtc ? utcTime(job.overallDueAtUtc) : NaN
  const reference = job.completedAtUtc ? utcTime(job.completedAtUtc) : job.overallSlaPaused ? syncedAt : now
  if (!Number.isFinite(due) || !Number.isFinite(reference)) return { breached: false, label: 'SLA not configured', text: 'No overall due date' }
  const seconds = Math.floor((due - reference) / 1000)
  const breached = reference > due
  return { breached, label: breached ? 'SLA breached' : 'SLA on track',
    text: `${job.overallSlaPaused && !job.completedAtUtc ? 'Paused · ' : ''}${breached ? `0d left · ${recruitmentDuration(Math.abs(seconds))} overdue` : `${recruitmentDuration(seconds)} left`}` }
}
