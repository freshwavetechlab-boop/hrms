export const activityDuration = (milliseconds: number | null | undefined) => {
  if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return 'Not recorded'
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`
  if (milliseconds < 60000) return `${(milliseconds / 1000).toFixed(2)} sec`
  return `${Math.floor(milliseconds / 60000)}m ${((milliseconds % 60000) / 1000).toFixed(1)}s`
}
export const activityOutcome = (status: string) => status === 'Interrupted' ? 'Interrupted / unknown' : status === 'Rejected' ? 'Request rejected' : status
