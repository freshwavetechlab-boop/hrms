import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js')), cache = new Map()
function load(name) {
  if (cache.has(name)) return cache.get(name)
  assert.ok(['excelPayslipImport', 'excelPayrollLocationVerification'].includes(name))
  const entry = { exports: {} }; cache.set(name, entry.exports)
  const compiled = ts.transpileModule(fs.readFileSync(path.join(root, `payroll-ui/src/utils/${name}.ts`), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('require', 'module', 'exports', compiled)(dependency => { assert.equal(dependency, './excelPayslipImport'); return load('excelPayslipImport') }, entry, entry.exports)
  return entry.exports
}
const { locationPayrollVerificationTargets: targets, compareLocationTransferPayroll } = load('excelPayrollLocationVerification')
const compare = (result, observations) => compareLocationTransferPayroll(result, observations, 42)
let passed = 0
function test(name, fn) { fn(); passed++; console.log(`PASS ${name}`) }
function fixture() {
  const batch = { id: 'saved-source', clientId: 42, month: '2026-09', rows: [1, 2].map((id, index) => ({
    id: `r${id}`, sourceRow: id + 4, employeeCode: 'SOURCE-ONE', employeeName: 'Synthetic Employee',
    information: [{ label: 'Work location', value: `Synthetic Location ${id}` }, { label: 'PAN', value: 'SYNTH1234Z' }],
    employment: { employeeId: 101, employeeCode: 'MASTER-ONE', identityReviewRequired: false, blockedForMonth: false },
  })) }
  const parts = batch.rows.map((row, index) => ({ rowId: row.id, sourceRow: row.sourceRow, partKey: `part${index}`, assignmentId: `assignment${index}`,
    workLocationId: index + 1, templateId: 'settings-template', payrollDays: 30, presentDays: index ? 13 : 17, payableDays: index ? 13 : 17, rulesHash: 'settings-rules', sourceHash: `source-hash-${index}` }))
  const result = { importId: 'receipt', employeeIds: [101], locationAllocations: { version: 1, payrollPeriod: batch.month, configurationEffectiveFrom: '2026-10-29', groups: [{ employeeId: 101, parts }] },
    locationAssignmentReceipts: parts.map(part => ({ ...part, employeeId: 101, payPeriod: batch.month, configurationEffectiveFrom: '2026-10-29' })),
    transfer: { version: 1, components: [{ code: 'BASE', category: 'Earning' }, { code: 'EE', category: 'Deduction' }, { code: 'ER', category: 'Benefit' }] },
    transferRows: parts.map(part => ({ rowId: part.rowId, employeeId: 101, sourceNet: 90, lines: [
      { code: 'BASE', sourceAmount: 100 }, { code: 'EE', sourceAmount: 10 }, { code: 'ER', sourceAmount: 13 },
    ] })) }
  const context = { calculationOnlySimulationSupported: true, employees: [{ id: 101, employeeCode: 'MASTER-ONE' }], roster: { rows: batch.rows.map(row => ({ rowId: row.id, employeeId: 101, status: 'Applied' })) },
    lastImport: { status: 'Applied', importId: 'receipt', result, savedRequest: { batchId: batch.id, rowIds: ['r1', 'r2'] } } }
  const observations = [{ employeeId: 101, ok: true, error: '', data: { complete: true, mode: 'Employee', issues: [], gross: 200, deductions: 20, benefits: 26, net: 180,
    clientId: 42, employeeId: 101, payPeriod: batch.month,
    locationAllocations: { version: 1, clientId: 42, employeeId: 101, payPeriod: batch.month, parts: parts.map(part => ({ ...part, salaryStructureId: part.templateId })) },
    inputs: [{ name: 'PAYROLL_DAYS', value: 30 }, { name: 'PRESENT_DAYS', value: 30 }, { name: 'PAYABLE_DAYS', value: 30 }],
    lines: [{ code: 'BASE', category: 'Earning', earned: 200 }, { code: 'EE', category: 'Deduction', earned: 20 }, { code: 'ER', category: 'Benefit', earned: 26 }] } }]
  return { batch, result, context, observations }
}
const eligible = f => targets(42, f.batch, ['r1', 'r2'], f.context, f.result)[0]
test('Two source parts select one employee, never two payroll targets', () => {
  const f = fixture(), before = JSON.stringify(f), result = eligible(f)
  assert.equal(result.eligible, true); assert.equal(result.employeeId, 101); assert.equal(result.payableDays, 30)
  assert.equal(compare(f.result, f.observations)[0].status, 'EXACT'); assert.equal(JSON.stringify(f), before)
})
test('Repeated assignment remains valid when stable part keys differ', () => {
  const f = fixture(); f.result.locationAllocations.groups[0].parts[1].assignmentId = 'assignment0'; f.result.locationAssignmentReceipts[1].assignmentId = 'assignment0'
  assert.equal(eligible(f).eligible, true)
  f.result.locationAllocations.groups[0].parts[1].partKey = 'part0'; assert.equal(eligible(f).eligible, false)
})
test('Wrong period, source batch, receipt, rules or current identity stays blocked', () => {
  for (const edit of [f => f.result.locationAllocations.payrollPeriod = '2026-10', f => f.context.lastImport.savedRequest.batchId = 'different',
    f => f.result.locationAssignmentReceipts[0].rulesHash = 'changed', f => f.batch.rows[1].employment.identityReviewRequired = true,
    f => f.context.employees[0].employeeCode = 'OTHER', f => f.result.employeeIds.push(101), f => f.result.locationAllocations.groups[0].referenceAssignmentId = 'unrelated']) {
    const f = fixture(); edit(f); assert.equal(eligible(f).eligible, false)
  }
})
test('Missing group parts, repeated original rows and invalid totals cannot pass', () => {
  for (const edit of [f => f.result.locationAllocations.groups[0].parts.pop(), f => f.batch.rows[1].sourceRow = f.batch.rows[0].sourceRow,
    f => f.result.locationAllocations.groups[0].parts[1].payableDays = 20, f => f.batch.rows[1].employeeName = 'Another Person',
    f => f.result.transferRows.pop()]) { const f = fixture(); edit(f); assert.equal(eligible(f).eligible, false) }
})
test('Known source currency quantization retains raw delta as ROUNDING_ONLY', () => {
  const f = fixture(); f.result.transferRows[0].lines[1].sourceAmount = 10.004; f.result.transferRows[0].sourceNet = 89.996
  const result = compare(f.result, f.observations)[0]
  assert.equal(result.status, 'ROUNDING_ONLY'); assert.equal(result.lines.find(line => line.code === 'EE').rawDelta, Number((20 - 20.004).toPrecision(15)))
  assert.equal(result.displayNetDelta, 0)
})
test('Source part versus aggregate currency rounding needs exact native raw-basis evidence', () => {
  const f = fixture(); f.result.transferRows[0].lines[1].sourceAmount = 10.006; f.result.transferRows[1].lines[1].sourceAmount = 10.006
  f.result.transferRows[0].sourceNet = 89.994; f.result.transferRows[1].sourceNet = 89.994
  f.observations[0].data.lines[1].earned = 20.01; f.observations[0].data.lines[1].unroundedEarned = 20.012
  f.observations[0].data.deductions = 20.01; f.observations[0].data.net = 179.99
  assert.equal(compare(f.result, f.observations)[0].status, 'ROUNDING_ONLY')
  delete f.observations[0].data.lines[1].unroundedEarned
  assert.equal(compare(f.result, f.observations)[0].status, 'REAL_MISMATCH')
})
test('Unexplained small and large differences remain real mismatches', () => {
  for (const delta of [.01, 28.70, 100]) { const f = fixture(); f.observations[0].data.lines[1].earned += delta; f.observations[0].data.deductions += delta; f.observations[0].data.net -= delta
    assert.equal(compare(f.result, f.observations)[0].status, 'REAL_MISMATCH') }
})
test('Employer contribution or category differences block even with matching net', () => {
  const f = fixture(); f.observations[0].data.lines[2].earned = 25; f.observations[0].data.benefits = 25
  assert.equal(compare(f.result, f.observations)[0].status, 'REAL_MISMATCH')
  f.observations[0].data.lines[2].earned = 26; f.observations[0].data.lines[2].category = 'Deduction'
  assert.equal(compare(f.result, f.observations)[0].status, 'BLOCKED')
})
test('Wrong attendance, template-only, missing simulation and inconsistent summaries block', () => {
  for (const edit of [f => f.observations[0].data.inputs[2].value = 29, f => f.observations[0].data.mode = 'Template',
    f => f.observations[0].data.gross = 201, f => f.observations[0].ok = false]) { const f = fixture(); edit(f); assert.notEqual(compare(f.result, f.observations)[0].status, 'EXACT') }
})
test('A stale month, different assignment or missing actual scope cannot certify matching amounts', () => {
  for (const edit of [f => f.observations[0].data.payPeriod = '2026-10', f => f.observations[0].data.employeeId = 999,
    f => { f.observations[0].data.clientId = 50; f.observations[0].data.locationAllocations.clientId = 50 },
    f => f.observations[0].data.locationAllocations.parts[0].assignmentId = 'OTHER', f => delete f.observations[0].data.locationAllocations]) {
    const f = fixture(); edit(f); assert.equal(compare(f.result, f.observations)[0].status, 'BLOCKED')
  }
})
test('Subcent actual outputs cannot be disguised as a currency match', () => {
  const f = fixture(); f.observations[0].data.lines[1].earned = 20.001
  assert.equal(compare(f.result, f.observations)[0].status, 'BLOCKED')
})
console.log(`PASS: ${passed} location receipt and actual payroll comparison groups; no API/DB calls or writes.`)
