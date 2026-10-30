export const presentDayText = (value: number | null | undefined) => typeof value === 'number' && Number.isFinite(value) ? String(value) : 'Not supplied'
export function presentDayTotal(values: (number | null | undefined)[]): number | null {
  if (!values.length || values.some(value => typeof value !== 'number' || !Number.isFinite(value))) return null
  return (values as number[]).reduce((sum, value) => sum + value, 0)
}
export function reportAttendanceValue(column: string, value: string | number | boolean | null | undefined) {
  return /^present\s*days$/i.test(column.trim()) ? presentDayText(typeof value === 'number' ? value : null) : value
}
