import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/monthlyAttendanceImport.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { prepareMonthlyAttendanceImport: prepare } = exports
const header = ['EmployeeCode','WorkingDays','PresentDays','PayableDays','LopDays','Remarks']
const roster = [{employeeId:11,employeeCode:'001',sourceType:'',presentDays:null}]
test('monthly totals preserve reviewed fractional days and roster identity without creating dates', () => {
  const before=JSON.stringify(roster)
  const [row]=prepare([header,['001','26','20.5','20.5','5.5','Owner-reviewed totals']],roster,'2026-09')
  assert.equal(row.employeeId,11); assert.equal(row.employeeCode,'001'); assert.equal(row.presentDays,20.5); assert.equal(row.payableDays,20.5); assert.equal(row.month,'2026-09'); assert.equal(row.actualPresenceKnown,true)
  assert.equal(JSON.stringify(roster),before); assert.equal(row.attendanceDate,undefined)
  assert.equal(prepare([header,['001','26','0','0','26']],roster,'2026-09')[0].presentDays,0)
})
test('missing presence, duplicates, foreign codes, excess duty units and unbalanced totals are rejected', () => {
  for(const row of [['001','26','','20','6'],['001','26','27','27','0'],['001','26','20','20','4'],['unknown','26','20','20','6'],['001','26','NaN','20','6'],['001','26','20.555','20.555','5.445']]) assert.throws(()=>prepare([header,row],roster,'2026-09'))
  assert.throws(()=>prepare([header,['001','26','20','20','6'],['001','26','20','20','6']],roster,'2026-09'),/duplicated/)
  assert.throws(()=>prepare([header,['001','26','20','20','6']],[...roster,...roster],'2026-09'),/outside/)
  assert.throws(()=>prepare([['EmployeeCode'],['001']],roster,'2026-09'),/requires/)
})
