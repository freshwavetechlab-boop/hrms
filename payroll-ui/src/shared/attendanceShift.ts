// Generated from shared/attendanceShift.ts; run node shared/sync-attendance-shift.mjs to update.
export type AttendanceShift = {
  rules?: { hoursMethod: 'Inherit' | 'Pairs' | 'Span'; breakMode: 'Fixed' | 'None'; dayBoundary?: string | null; maximumHours?: number | null; segments: { startMinute: number; endMinute: number }[] }
  id: number; clientId: number; shiftCode: string; shiftName: string; shiftType: 'Fixed' | 'Flexible'
  startTime?: string | null; endTime?: string | null; isOvernight: boolean; graceMinutes: number; breakMinutes: number
  minimumFullDayHours: number; minimumHalfDayHours: number; effectiveFrom: string; effectiveTo?: string | null
  isActive: boolean; createdAt?: string; updatedAt?: string
}
export type AttendanceShiftAssignment = { id: number; clientId: number; employeeId: number; employeeCode: string; employeeName: string; shiftId: number; shiftName: string; effectiveFrom: string; effectiveTo: string; weekDays: string; reason: string; isActive: boolean }
export type EmployeeShiftPolicy = { employeeId: number; clientId: number; shiftId?: number | null; attendanceMode: 'Legacy' | 'Punch' | 'Monthly'; allowedShiftIds: number[] }
type ShiftContext = { settings: { clientId: number; shiftId?: number | null; maximumHoursAllowedForFullDay: number }; shifts?: AttendanceShift[]; employeeShiftIds?: Record<string, number>; shiftAssignments?: AttendanceShiftAssignment[]; shiftPolicies?: EmployeeShiftPolicy[] }
export const manualHalfDayStatus = 'P.5'
export function manualHalfDayAttendance(status: string, patch: { payableValue?: number; checkInTime?: string | null; checkOutTime?: string | null }) {
  // Preserve a saved no-punch half day when normalizing again before save.
  if (status !== manualHalfDayStatus && !(status === 'Present' && patch.payableValue === .5 && patch.checkInTime === null && patch.checkOutTime === null)) return null
  return { status, payableValue: .5, checkInTime: null, checkOutTime: null, totalHours: 0 }
}
const minutes = (value: string) => { const [hours, mins, seconds = 0] = value.split(':').map(Number); return hours * 60 + mins + seconds / 60 }
export const hasExplicitAttendancePolicy = (context: ShiftContext, employeeId: number) => context.shiftPolicies?.some(p => p.employeeId === employeeId && p.clientId === context.settings.clientId && p.attendanceMode !== 'Legacy') ?? false

export function resolveAttendanceShift(context: ShiftContext, employeeId: number, date: string) {
  const day = date.slice(0, 10)
  const find = (id?: number | null) => context.shifts?.find(shift => shift.id === id && shift.clientId === context.settings.clientId && shift.isActive && shift.effectiveFrom.slice(0, 10) <= day && (!shift.effectiveTo || shift.effectiveTo.slice(0, 10) >= day))
  const policies = context.shiftPolicies?.filter(p => p.employeeId === employeeId && p.clientId === context.settings.clientId) ?? []
  const policy = policies[0]
  if (policy?.attendanceMode === 'Monthly' || policies.length > 1 && policies.some(p => p.attendanceMode !== 'Legacy')) return undefined
  const weekday = new Date(`${day}T12:00:00`).getDay()
  const assignments = context.shiftAssignments?.filter(a => a.clientId === context.settings.clientId && a.employeeId === employeeId && a.isActive && a.effectiveFrom.slice(0, 10) <= day && a.effectiveTo.slice(0, 10) >= day && a.weekDays.split(',').includes(String(weekday))) ?? []
  if (assignments.length && policy?.attendanceMode !== 'Punch') return undefined
  if (policy?.attendanceMode === 'Punch') {
    if (assignments.length > 1) return undefined
    const id = assignments[0]?.shiftId ?? policy.shiftId
    return id && policy.allowedShiftIds.includes(id) ? find(id) : undefined
  }
  return find(context.employeeShiftIds?.[employeeId]) ?? find(context.settings.shiftId)
}

// Shared admin/MSS preview. The API calculator remains authoritative when saving.
export function calculateReviewShift(context: ShiftContext, employeeId: number, date: string, checkIn?: string | null, checkOut?: string | null) {
  const shift = resolveAttendanceShift(context, employeeId, date)
  if (hasExplicitAttendancePolicy(context, employeeId) && (!shift || !checkIn || !checkOut))
    return { hours: 0, payable: 0, error: !shift ? 'No applicable punch-based shift. Check policy and roster.' : 'Enter actual IN and OUT times.' }
  if (!shift || !checkIn || !checkOut) return null
  const scheduledStart = shift.startTime ? minutes(shift.startTime) : 0
  const scheduledEnd = (shift.endTime ? minutes(shift.endTime) : 0) + (shift.isOvernight ? 1440 : 0)
  let start = minutes(checkIn), end = minutes(checkOut)
  if (shift.rules?.dayBoundary != null) {
    if (start < minutes(shift.rules.dayBoundary)) { start += 1440; end += 1440 }
    if (end < start) end += 1440
  }
  if (shift.isOvernight) {
    const cutoff = (scheduledStart + scheduledEnd - 1440) / 2
    if (start < cutoff) { start += 1440; end += 1440 }
    if (end < start) end += 1440
  }
  if (shift.shiftType === 'Fixed') { start = Math.max(start, scheduledStart); end = Math.min(end, scheduledEnd) }
  const duration = shift.shiftType === 'Fixed' && shift.rules?.segments?.length ? shift.rules.segments.reduce((sum, s) => sum + Math.max(0, Math.min(end, s.endMinute) - Math.max(start, s.startMinute)), 0) : end - start
  const deduction = shift.rules?.breakMode === 'None' ? 0 : shift.breakMinutes
  const hours = Math.round(Math.min(shift.rules?.maximumHours ?? Math.max(context.settings.maximumHoursAllowedForFullDay, shift.minimumFullDayHours), Math.max(0, (duration - deduction) / 60)) * 100) / 100
  return { hours, payable: hours >= shift.minimumFullDayHours ? 1 : hours >= shift.minimumHalfDayHours ? .5 : 0 }
}
