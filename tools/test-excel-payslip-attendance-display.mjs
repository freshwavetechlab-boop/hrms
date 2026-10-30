import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// Pure saved-source presentation: no HTTP, native attendance access or writes.
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const source = fs.readFileSync(path.join(root, 'payroll-ui/src/utils/excelPayslipAttendanceDisplay.ts'), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const module = { exports: {} }
new Function('require', 'module', 'exports', compiled)(() => { throw Error('Display utility must have no runtime service dependency') }, module, module.exports)
const { calendarMonthDays, savedPayslipAttendance, attendanceDayText, sourceDayText } = module.exports
const row = (...information) => ({ information: information.map(([label, value]) => ({ label, value })) })
const checks = []
assert.equal(calendarMonthDays('2026-09'), 30); assert.equal(calendarMonthDays('2026-10'), 31)
assert.equal(calendarMonthDays('2028-02'), 29); assert.equal(calendarMonthDays('2100-02'), 28); assert.equal(calendarMonthDays('2000-02'), 29)
for (const value of ['', '2026-13', '2026-00', '2026-9', '2026-09-01', '0000-02']) assert.equal(calendarMonthDays(value), null)
checks.push('Calendar period validates exact month and leap years; it is independent of a payroll divisor')

let result = savedPayslipAttendance(row(['Attendance', '30'], ['Working days', '26'], ['Month Days', '26']))
assert.equal(result.present.status, 'Not mapped'); assert.equal(result.payable.status, 'Not mapped'); assert.equal(result.source.status, 'Review required')
assert.equal(calendarMonthDays('2026-09'), 30)
checks.push('Ambiguous attendance/working days never become present/payable; divisor26 does not replace calendar30')
result = savedPayslipAttendance(row(['Attendance', '30']))
assert.equal(result.source.days, 30); assert.equal(sourceDayText(result.source), '30 (Attendance)')
assert.equal(result.present.days, null); assert.equal(result.payable.days, null)
assert.match(result.source.reason, /meaning has not been inferred/)
checks.push('Generic saved Attendance retains its original caption without claiming actual present days')
result = savedPayslipAttendance(row(['Present Days', '26'], ['Payable Days', '30']))
assert.equal(result.present.days, 26); assert.equal(result.payable.days, 30); assert.equal(result.source.days, null)
assert.equal(attendanceDayText(result.present), 26)
checks.push('Explicit present and payable headings remain separate; no leave or weekly-off balance is invented')
result = savedPayslipAttendance(row(['Days Present', '0'], ['Paid Days', '0.5']))
assert.equal(result.present.days, 0); assert.equal(result.payable.days, 0.5)
assert.equal(attendanceDayText(result.present), 0)
checks.push('Recorded zero and fractional days remain valid values')
result = savedPayslipAttendance(row(['Present', '  '], ['Paid Days', '']))
assert.equal(result.present.status, 'Not supplied'); assert.equal(result.payable.status, 'Not supplied')
assert.equal(result.present.days, null); assert.equal(result.payable.days, null)
checks.push('Blank saved cells are unknown rather than false zero')
for (const value of ['-1', '32', 'NaN', '3,0', '30 days', '1e1', 'Infinity']) {
  result = savedPayslipAttendance(row(['Present days', value])); assert.equal(result.present.status, 'Review required'); assert.equal(result.present.days, null)
}
checks.push('Malformed, nonnumeric and out-of-range day values require review')
result = savedPayslipAttendance(row(['Present Days', '26'], ['Days Present', '26'], ['Paid Days', '30'], ['Payable days', '30']))
assert.equal(result.present.status, 'Review required'); assert.equal(result.payable.status, 'Review required')
checks.push('Duplicate semantic headings require review even when their numbers agree')
result = savedPayslipAttendance(row(['Actual Worked', '25.5'], ['Days Worked', '25.5']))
assert.equal(result.present.status, 'Review required')
result = savedPayslipAttendance(row(['Working Days', '25.5']))
assert.equal(result.source.days, 25.5); assert.equal(result.present.status, 'Not mapped')
checks.push('Days Worked is explicit but generic Working Days is not relabelled as Present')
const original = row(['Present days', '26'], ['Attendance', '30'])
const frozen = JSON.stringify(original); Object.freeze(original.information); savedPayslipAttendance(original)
assert.equal(JSON.stringify(original), frozen)
checks.push('Reading attendance presentation does not mutate saved source values or fingerprint inputs')
console.log(JSON.stringify({ passed: checks.length, failed: 0, checks, databaseConnections: 0, sourceWrites: 0 }))
