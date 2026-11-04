import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/payrollLocationAttendance.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { buildLocationAttendanceWorkbook: workbook, readLocationAttendanceUpload: read, payrollLocationOptions: options } = exports
const row = (key, location, presentDays = null, payableDays = null) => ({ key, employeeId: 1, employeeCode: '0001', employeeName: 'Synthetic employee', workLocationId: location, workLocationName: `Location ${location}`, templateId: `T${location}`, rulesHash: `rules${location}`, inputVersion: `input${key}`, nativePart: true, payrollDays: 30, presentDays, payableDays, issue: '', recorded: presentDays !== null && payableDays !== null })
const context = () => ({ clientId: 7, month: '2026-09', policyBatchId: 'P', groupIds: [3], version: 'stable-configuration', storageAvailable: true, rows: [row('A', 10), row('B', 20)] })
const cells = sheet => ({ headers: sheet.rows[0].map(c => typeof c === 'object' ? String(c.value) : String(c)), rows: sheet.rows.slice(1).map(r => r.map(c => typeof c === 'object' ? String(c.value) : String(c))) })
const fill = (data, index, present, payable = present) => { data.rows[index][data.headers.indexOf('PresentDays')] = String(present); data.rows[index][data.headers.indexOf('PayableDays')] = String(payable); return data }

test('same employee has independent location rows, stable code, salary bindings and exact day inputs', () => {
  const ctx = context(), before = JSON.stringify(ctx), data = cells(workbook(ctx, 0)[0])
  fill(data, 0, 17); fill(data, 1, 13)
  assert.deepEqual(read(data, ctx, 0).map(r => [r.key, r.employeeId, r.workLocationId, r.payableDays]), [['A', 1, 10, 17], ['B', 1, 20, 13]])
  assert.equal(data.rows[0][0], '0001'); assert.equal(JSON.stringify(ctx), before)
  assert.equal(options(ctx).length, 3)
})
test('blank location stays pending; explicit zero is an attendance input', () => {
  const ctx = context(), data = fill(cells(workbook(ctx, 0)[0]), 0, 0)
  assert.deepEqual(read(data, ctx, 0).map(r => [r.key, r.presentDays]), [['A', 0]])
  assert.throws(() => read(cells(workbook(ctx, 0)[0]), ctx, 0), /Blank days stay pending/)
  data.rows[1][4] = '13'; assert.throws(() => read(data, ctx, 0), /both present and payable/)
})
test('client, month, location, code, formula version and duplicate parts are rejected atomically', () => {
  const ctx = context()
  for (const [field, value] of [['ClientId', '8'], ['Month', '2026-10'], ['EmployeeCode', '1'], ['WorkLocationId', '20'], ['RulesHash', 'other'], ['TemplateId', 'other'], ['ContextVersion', 'old']]) {
    const data = fill(cells(workbook(ctx, 0)[0]), 0, 17); data.rows[0][data.headers.indexOf(field)] = value
    assert.throws(() => read(data, ctx, 0), /changed/)
  }
  const duplicate = fill(cells(workbook(ctx, 0)[0]), 0, 17); duplicate.rows.push([...duplicate.rows[0]])
  assert.throws(() => read(duplicate, ctx, 0), /changed/)
  assert.throws(() => read(fill(cells(workbook(ctx, 0)[0]), 1, 13), ctx, 10), /changed/)
})
test('independent location files survive another location save but not an edit to the same row', () => {
  const ctx = context(), data = fill(cells(workbook(ctx, 20)[0]), 0, 13)
  ctx.rows[0] = { ...ctx.rows[0], presentDays: 17, payableDays: 17, inputVersion: 'new-A' }
  assert.equal(read(data, ctx, 20)[0].payableDays, 13)
  ctx.rows[1].inputVersion = 'new-B'
  assert.throws(() => read(data, ctx, 20), /attendance changed/)
})
test('saved payroll and ambiguous salary mappings remain protected', () => {
  const ctx = context(), data = fill(cells(workbook(ctx, 10)[0]), 0, 17)
  ctx.rows[0].lockedPayRunId = 81; assert.throws(() => read(data, ctx, 10), /payroll #81/)
  delete ctx.rows[0].lockedPayRunId; ctx.rows[0].issue = 'Review salary assignment'; assert.throws(() => read(data, ctx, 10), /Review salary/)
})
