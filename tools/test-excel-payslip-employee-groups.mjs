import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// Execute the actual pure TypeScript helpers. No HTTP, database, login or saved payroll writes.
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const cache = new Map()
function load(name) {
  if (cache.has(name)) return cache.get(name)
  assert.ok(['excelPayslipImport', 'excelPayslipEmployeeGroups'].includes(name))
  const source = fs.readFileSync(path.join(root, `payroll-ui/src/utils/${name}.ts`), 'utf8')
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const entry = { exports: {} }; cache.set(name, entry.exports)
  new Function('require', 'module', 'exports', compiled)(dependency => {
    assert.equal(dependency, './excelPayslipImport'); return load('excelPayslipImport')
  }, entry, entry.exports)
  return entry.exports
}
const { buildPayslipEmployeeGroups } = load('excelPayslipEmployeeGroups')
const passed = []
function check(name, action) { action(); passed.push(name) }
const info = (label, value) => ({ label, value })
function row(index, attendance = '17', location = 'Synthetic Location A') {
  return {
    id: `synthetic-row-${index}`, sourceRow: index, employeeCode: 'SYNTH-1', employeeName: 'Synthetic Person', email: '',
    information: [info('Work location', location), info('Attendance', attendance), info('PAN', 'SYNTH1234Z')],
    earnings: [{ label: 'Basic', amount: 100 }], deductions: [{ label: 'PF', amount: 10 }],
    employerContributions: [{ label: 'Employer PF', amount: 12 }], netPay: 90, declaredGross: null, declaredDeductions: null, warnings: [],
  }
}
function batch(rows, overrides = {}) {
  return { id: 'a'.repeat(32), clientId: 42, month: '2026-09', rows, ...overrides }
}
function transferRows() { return [row(53), row(54, '13', 'Synthetic Location B')] }
function unitRows(confirmed = true) {
  return transferRows().map((part, index) => ({ ...part, information: [
    ...part.information.filter(item => item.label !== 'Work location'), info('Work location', 'Synthetic Same Location'),
    info('Source workbook', `Synthetic Unit ${index + 1}.xlsx`), info('Source worksheet', 'Salary'),
    info('Source Excel row', String(index + 5)), info('Payroll unit', `Synthetic Unit ${index + 1}`),
    ...(confirmed ? [info('Reviewed source split', 'Confirmed')] : []),
  ] }))
}
function deepFreeze(value) {
  if (value && typeof value === 'object') { Object.values(value).forEach(deepFreeze); Object.freeze(value) }
  return value
}

check('Two verified locations display one employee with both 17/13-day source parts', () => {
  const parts = transferRows(); parts[0].netPay = 42749; parts[1].netPay = 33860
  const result = buildPayslipEmployeeGroups(batch(parts)), group = result.groups[0]
  assert.equal(result.sourceRowCount, 2); assert.equal(result.employeeCount, 1); assert.equal(result.reviewedLocationSplitCount, 1)
  assert.equal(result.unresolvedRowCount, 0); assert.equal(result.groups.length, 1)
  assert.equal(group.hasMultipleParts, true); assert.equal(group.isReviewedLocationSplit, true)
  assert.equal(group.sourceAttendanceTotal, 30); assert.equal(group.hasDatedCoverage, false)
  assert.equal(group.totals.netPay, 76609); assert.deepEqual(group.sourceRows, [53, 54])
  assert.deepEqual(group.locations, ['Synthetic Location A', 'Synthetic Location B'])
  assert.equal(group.rows[0], parts[0]); assert.equal(group.rows[1], parts[1])
})

check('Same-location reviewed source units remain two retained segments in one group', () => {
  const result = buildPayslipEmployeeGroups(batch(unitRows()))
  assert.equal(result.employeeCount, 1); assert.equal(result.groups[0].isReviewedLocationSplit, true)
  assert.deepEqual(result.groups[0].sourceUnits, ['Synthetic Unit 1', 'Synthetic Unit 2'])
  assert.equal(result.groups[0].sourceAttendanceTotal, 30)
})

check('Unresolved existing Master identity is retained without undoing verified source-part presentation', () => {
  const parts = transferRows().map(part => ({ ...part, employment: { employeeId: null, identityReviewRequired: true } }))
  const result = buildPayslipEmployeeGroups(batch(parts)), group = result.groups[0]
  assert.equal(result.employeeCount, 1); assert.equal(result.reviewedLocationSplitCount, 1)
  assert.equal(group.requiresIdentityReview, true); assert.match(group.reviewReason, /Source parts match; existing master identity still needs review/)
  assert.equal(group.rows[0].employment.identityReviewRequired, true)
})

check('Conflicting names are not merged or counted as an established source employee', () => {
  const parts = transferRows(); parts[1].employeeName = 'Different Synthetic Person'
  const result = buildPayslipEmployeeGroups(batch(parts))
  assert.equal(result.groups.length, 2); assert.equal(result.employeeCount, 0); assert.equal(result.unresolvedRowCount, 2)
  assert.ok(result.groups.every(group => group.requiresIdentityReview && !group.hasMultipleParts))
})

check('Blank codes, names, source IDs and absent shared identifiers never create a combined employee', () => {
  for (const problem of ['code', 'name', 'row-id', 'strong-id']) {
    const parts = transferRows()
    if (problem === 'code') parts.forEach(part => { part.employeeCode = '' })
    if (problem === 'name') parts[1].employeeName = ''
    if (problem === 'row-id') parts[1].id = ''
    if (problem === 'strong-id') parts.forEach(part => { part.information = part.information.filter(item => item.label !== 'PAN') })
    const result = buildPayslipEmployeeGroups(batch(parts))
    assert.equal(result.groups.length, 2, problem); assert.equal(result.employeeCount, 0, problem)
    assert.equal(result.unresolvedRowCount, 2, problem)
  }
})

check('Conflicting strong identities and unreviewed same-location units remain separate', () => {
  const conflicting = transferRows(); conflicting[1].information.find(item => item.label === 'PAN').value = 'OTHER5678X'
  for (const parts of [conflicting, unitRows(false)]) {
    const result = buildPayslipEmployeeGroups(batch(parts))
    assert.equal(result.groups.length, 2); assert.equal(result.reviewedLocationSplitCount, 0)
    assert.equal(result.unresolvedRowCount, 2)
  }
})

check('Names never merge different source codes, clients or months', () => {
  const parts = transferRows(); parts[1].employeeCode = 'SYNTH-2'
  const result = buildPayslipEmployeeGroups(batch(parts))
  assert.equal(result.groups.length, 2); assert.equal(result.employeeCount, 2)
  const first = buildPayslipEmployeeGroups(batch([row(6)]))
  const client = buildPayslipEmployeeGroups(batch([row(6)], { clientId: 43 }))
  const month = buildPayslipEmployeeGroups(batch([row(6)], { month: '2026-10' }))
  assert.notEqual(first.groups[0].key, client.groups[0].key); assert.notEqual(first.groups[0].key, month.groups[0].key)
})

check('Declared totals and each slip display rounding are preserved before adding integer paise', () => {
  const parts = transferRows()
  parts[0].earnings[0].amount = 10.005; parts[0].declaredGross = 40.005; parts[0].declaredDeductions = 5.005; parts[0].netPay = 9.005
  parts[1].earnings[0].amount = 20.005; parts[1].deductions[0].amount = 1; parts[1].netPay = 19.005
  parts[0].employerContributions[0].amount = 1.005; parts[1].employerContributions[0].amount = 2.005
  const totals = buildPayslipEmployeeGroups(batch(parts)).groups[0].totals
  assert.deepEqual(totals, { gross: 60.02, deductions: 6.01, employerContributions: 3.02, netPay: 28.02 })
  assert.notEqual(totals.netPay, totals.gross - totals.deductions, 'Source net is authoritative, not recomputed to force reconciliation')
})

check('Cached binary tails, zero, negative corrections and absent money do not become false totals', () => {
  const parts = transferRows(); parts[0].netPay = 14129.499999999998; parts[1].netPay = -0.005
  assert.equal(buildPayslipEmployeeGroups(batch(parts)).groups[0].totals.netPay, 14129.49)
  parts[0].declaredGross = 0; parts[1].declaredGross = 0
  assert.equal(buildPayslipEmployeeGroups(batch(parts)).groups[0].totals.gross, 0)
  parts[0].netPay = Number.NaN; parts[1].employerContributions[0].amount = Infinity
  let totals = buildPayslipEmployeeGroups(batch(parts)).groups[0].totals
  assert.equal(totals.netPay, null); assert.equal(totals.employerContributions, null)
  parts[0].netPay = undefined; totals = buildPayslipEmployeeGroups(batch(parts)).groups[0].totals
  assert.equal(totals.netPay, null)
})

check('Attendance summary uses only explicit unambiguous Attendance fields and invents no dates or entitlement', () => {
  const parts = transferRows(); parts[1].information.find(item => item.label === 'Attendance').value = '9'
  assert.equal(buildPayslipEmployeeGroups(batch(parts)).groups[0].sourceAttendanceTotal, 26)
  parts[1].information.push(info('Attendance', '15'))
  assert.equal(buildPayslipEmployeeGroups(batch(parts)).groups[0].sourceAttendanceTotal, null)
  parts[1].information = parts[1].information.filter(item => item.label !== 'Attendance'); parts[1].information.push(info('Paid days', '13'))
  const group = buildPayslipEmployeeGroups(batch(parts)).groups[0]
  assert.equal(group.sourceAttendanceTotal, null); assert.equal(group.hasDatedCoverage, false)
})

check('Frozen source rows, formula capture, source units and identity flags remain completely unchanged', () => {
  const original = batch(transferRows(), { calculationSource: { rows: [{ sourceRow: 53, cells: [{ value: '100', formulaText: 'B53/30*C53' }] }] } })
  const before = JSON.stringify(original); deepFreeze(original)
  const result = buildPayslipEmployeeGroups(original)
  assert.equal(JSON.stringify(original), before)
  assert.equal(result.groups[0].rows[0], original.rows[0]); assert.equal(result.groups[0].rows[1], original.rows[1])
  assert.equal('pan' in result.groups[0], false); assert.equal('uan' in result.groups[0], false)
  assert.equal(result.groups[0].sourceRows.includes(53), true)
})

console.log(JSON.stringify({ passed: passed.length, checks: passed, sourceWrites: 0, networkCalls: 0, databaseConnections: 0, privateIdentifiersExported: false }))
