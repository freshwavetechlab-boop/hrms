import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/shared/attendanceShift.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { calculateReviewShift, resolveAttendanceShift, manualHalfDayAttendance } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
const shift = { id: 1, clientId: 20, shiftCode: 'DAY', shiftName: 'Day', shiftType: 'Fixed', startTime: '09:00:00', endTime: '18:00:00', breakMinutes: 60, graceMinutes: 10, minimumFullDayHours: 8, minimumHalfDayHours: 4, isOvernight: false, isActive: true, effectiveFrom: '2026-09-01', effectiveTo: null }
const context = (shifts = [shift]) => ({ settings: { clientId: 20, shiftId: 1, maximumHoursAllowedForFullDay: 12 }, shifts, employeeShiftIds: {} })

test('both independently deployable frontends retain the canonical attendance rules', () => {
  execFileSync(process.execPath, [fileURLToPath(new URL('../../shared/sync-attendance-shift.mjs', import.meta.url)), '--check'])
})

test('manual half day and saved no-punch half day survive repeated normalization without shift defaults', () => {
  const manual = manualHalfDayAttendance('P.5', {})
  assert.deepEqual(manual, { status: 'P.5', payableValue: .5, checkInTime: null, checkOutTime: null, totalHours: 0 })
  assert.deepEqual(manualHalfDayAttendance('P.5', manual), manual)
  const saved = { ...manual, status: 'Present' }
  assert.deepEqual(manualHalfDayAttendance('Present', saved), saved)
  assert.equal(manualHalfDayAttendance('Present', {}), null, 'choosing Present again must use normal shift calculation')
  assert.equal(manualHalfDayAttendance('Present', { ...saved, checkInTime: '09:00', checkOutTime: '14:00' }), null, 'real timings retain normal calculation')
  assert.equal(manualHalfDayAttendance('CL', { payableValue: .5, checkInTime: null, checkOutTime: null }), null, 'leave is separate')
})

test('fixed preview clips the window and deducts break before full/half/absent thresholds', () => {
  assert.deepEqual(calculateReviewShift(context(), 117, '2026-09-01', '08:00', '20:00'), { hours: 8, payable: 1 })
  assert.deepEqual(calculateReviewShift(context(), 117, '2026-09-01', '09:00', '14:00'), { hours: 4, payable: .5 })
  assert.deepEqual(calculateReviewShift(context(), 117, '2026-09-01', '09:00', '12:00'), { hours: 2, payable: 0 })
  assert.deepEqual(calculateReviewShift(context(), 117, '2026-09-01', '10:00', '09:00'), { hours: 0, payable: 0 })
})
test('flexible preview uses duration and supports a last-effective overnight start date', () => {
  const flexible = { ...shift, shiftType: 'Flexible', startTime: null, endTime: null }
  assert.deepEqual(calculateReviewShift(context([flexible]), 117, '2026-09-01', '13:00', '22:00'), { hours: 8, payable: 1 })
  const night = { ...shift, startTime: '21:00:00', endTime: '06:00:00', isOvernight: true, effectiveTo: '2026-09-01' }
  assert.deepEqual(calculateReviewShift(context([night]), 117, '2026-09-01', '21:00', '06:00'), { hours: 8, payable: 1 })
  assert.deepEqual(calculateReviewShift(context([night]), 117, '2026-09-01', '02:00', '06:00'), { hours: 3, payable: 0 })
  assert.deepEqual(calculateReviewShift(context([night]), 117, '2026-09-01', '21:00', '23:00'), { hours: 1, payable: 0 })
  assert.equal(calculateReviewShift(context([night]), 117, '2026-09-02', '21:00', '06:00'), null)
})
test('assignment wins and unavailable or foreign-client shifts fall back without changing no-shift previews', () => {
  const assigned = { ...shift, id: 2 }; const data = { ...context([shift, assigned]), employeeShiftIds: { 117: 2 } }
  assert.equal(resolveAttendanceShift(data, 117, '2026-09-01').id, 2)
  assigned.isActive = false; assert.equal(resolveAttendanceShift(data, 117, '2026-09-01').id, 1)
  assigned.isActive = true; assigned.clientId = 30; assert.equal(resolveAttendanceShift(data, 117, '2026-09-01').id, 1)
  assert.equal(calculateReviewShift(context([]), 117, '2026-09-01', '09:00', '18:00'), null)
  assert.equal(calculateReviewShift(context(), 117, '2026-09-01', null, '18:00'), null)
})
