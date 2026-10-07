export type AttendanceShift = {
  id: number; clientId: number; shiftCode: string; shiftName: string; shiftType: 'Fixed' | 'Flexible'
  startTime?: string | null; endTime?: string | null; isOvernight: boolean; graceMinutes: number; breakMinutes: number
  minimumFullDayHours: number; minimumHalfDayHours: number; effectiveFrom: string; effectiveTo?: string | null
  isActive: boolean; createdAt?: string; updatedAt?: string
}
type ShiftContext = { settings: { clientId: number; shiftId?: number | null; maximumHoursAllowedForFullDay: number }; shifts?: AttendanceShift[]; employeeShiftIds?: Record<string, number> }
export const manualHalfDayStatus = 'P.5'
export function manualHalfDayAttendance(status: string, patch: { payableValue?: number; checkInTime?: string | null; checkOutTime?: string | null }) {
  // Preserve a saved no-punch half day when normalizing again before save.
  if (status !== manualHalfDayStatus && !(status === 'Present' && patch.payableValue === .5 && patch.checkInTime === null && patch.checkOutTime === null)) return null
  return { status, payableValue: .5, checkInTime: null, checkOutTime: null, totalHours: 0 }
}
const minutes = (value: string) => { const [hours, mins, seconds = 0] = value.split(':').map(Number); return hours * 60 + mins + seconds / 60 }

export function resolveAttendanceShift(context: ShiftContext, employeeId: number, date: string) {
  const day = date.slice(0, 10)
  const find = (id?: number | null) => context.shifts?.find(shift => shift.id === id && shift.clientId === context.settings.clientId && shift.isActive && shift.effectiveFrom.slice(0, 10) <= day && (!shift.effectiveTo || shift.effectiveTo.slice(0, 10) >= day))
  return find(context.employeeShiftIds?.[employeeId]) ?? find(context.settings.shiftId)
}

// Shared admin/MSS preview. The API calculator remains authoritative when saving.
export function calculateReviewShift(context: ShiftContext, employeeId: number, date: string, checkIn?: string | null, checkOut?: string | null) {
  const shift = resolveAttendanceShift(context, employeeId, date)
  if (!shift || !checkIn || !checkOut) return null
  const scheduledStart = shift.startTime ? minutes(shift.startTime) : 0
  const scheduledEnd = (shift.endTime ? minutes(shift.endTime) : 0) + (shift.isOvernight ? 1440 : 0)
  let start = minutes(checkIn), end = minutes(checkOut)
  if (shift.isOvernight) {
    const cutoff = (scheduledStart + scheduledEnd - 1440) / 2
    if (start < cutoff) { start += 1440; end += 1440 }
    if (end < start) end += 1440
  }
  if (shift.shiftType === 'Fixed') { start = Math.max(start, scheduledStart); end = Math.min(end, scheduledEnd) }
  const hours = Math.round(Math.min(Math.max(context.settings.maximumHoursAllowedForFullDay, shift.minimumFullDayHours), Math.max(0, (end - start - shift.breakMinutes) / 60)) * 100) / 100
  return { hours, payable: hours >= shift.minimumFullDayHours ? 1 : hours >= shift.minimumHalfDayHours ? .5 : 0 }
}
