import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const modules = new Map()
function load(relative) {
  if (modules.has(relative)) return modules.get(relative)
  const module = { exports: {} }, filename = path.join(root, 'payroll-ui/src', relative)
  const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('require', 'module', 'exports', code)(name => {
    assert.ok(name.startsWith('./'), 'Unexpected dependency ' + name)
    return load(path.posix.normalize(path.posix.join(path.posix.dirname(relative), name + '.ts')))
  }, module, module.exports)
  modules.set(relative, module.exports)
  return module.exports
}
const { proposeExcelPayrollSetup, selectExcelPayrollMaster, restoreExcelPayrollMasterMetadata, payrollSetupMasterPriority } = load('utils/excelPayrollSetup.ts')
const source = { sheetName: 'Salary', headerRow: 1, columnCount: 5, columns: ['Employee name', 'Basic', 'Attendance', 'Month days', 'Net'].map((sourceHeader, columnIndex) => ({ columnIndex, sourceHeader, label: sourceHeader, kind: ['employeeName', 'earning', 'info', 'info', 'netPay'][columnIndex] })), rows: [{ sourceRow: 2, cells: ['Synthetic person', '1000', '30', '30', '1000'].map(value => ({ value })) }], excludedSourceRows: [] }
const batch = { id: 'synthetic-metadata', clientId: 33, clientName: 'Synthetic client', month: '2026-09', rows: [{ id: 'row-one', sourceRow: 2, employeeCode: 'TEST001', employeeName: 'Synthetic person', information: [], earnings: [{ label: 'Basic', amount: 1000 }], deductions: [], employerContributions: [], netPay: 1000 }] }
const master = { id: '700', code: 'BASIC', name: 'Exempt monthly Basic', category: 'Earning', componentRole: 'Regular Earning', statutoryType: 'None', calculationType: 'Fixed Amount', value: '0', formula: '', baseComponent: '', proRata: true, taxable: false, priority: '0', payType: 'Variable Pay' }
const context = { source, components: [master], employees: [], workWeeks: [{ value: 'Sunday off', label: 'Sunday off' }], workLocations: [] }
const original = JSON.stringify({ source, batch, context })
const proposal = proposeExcelPayrollSetup(batch, context, ['row-one']).request
const component = proposal.components[0]
assert.equal(component.existingComponentId, master.id)
assert.equal(component.taxable, false, 'An earning may be exempt; category must not overwrite the master')
assert.equal(component.priority, 0, 'Zero is a persisted priority, not a missing default')
assert.equal(component.payType, 'Variable Pay')
assert.equal(component.isPeriodAdjustment, false, 'Variable Pay is not automatically a source-month adjustment')

const second = { ...master, id: '701', code: 'REVIEWED_BASIC', name: 'Reviewed fixed Basic', taxable: true, priority: 735, payType: 'Fixed Pay' }
assert.equal(payrollSetupMasterPriority('735'), 735)
assert.equal(payrollSetupMasterPriority(' -10 '), -10)
for (const missing of ['', '  ', 'NaN', '1.5', '9007199254740992', undefined]) assert.equal(payrollSetupMasterPriority(missing), null, 'Malformed stored priorities must not become a guessed zero')
const reviewed = { ...component, formula: 'BASIC_MONTHLY*PAYABLE_DAYS/PAYROLL_DAYS', rowFormulas: { 'row-one': '1000/30*PAYABLE_DAYS' }, proRata: false, blankInputAsZero: true }
const switched = selectExcelPayrollMaster(reviewed, second)
assert.deepEqual([switched.existingComponentId, switched.taxable, switched.priority, switched.payType], ['701', true, 735, 'Fixed Pay'])
for (const key of ['formula', 'rowFormulas', 'proRata', 'blankInputAsZero', 'sourceColumnIndex', 'expectedColumnIndex', 'isPeriodAdjustment']) assert.deepEqual(switched[key], reviewed[key], 'Switching a shared component must retain reviewed source/template inputs: ' + key)
const variant = selectExcelPayrollMaster(switched)
assert.equal(variant.existingComponentId, undefined)
for (const key of Object.keys(switched).filter(key => key !== 'existingComponentId')) assert.deepEqual(variant[key], switched[key], 'Choosing a new component retains the reviewed draft: ' + key)

const stale = structuredClone(proposal); stale.components[0] = { ...reviewed, taxable: true, priority: 999, payType: 'Fixed Pay' }
const snapshot = JSON.stringify(stale), restored = restoreExcelPayrollMasterMetadata(stale, context)
assert.deepEqual([restored.components[0].taxable, restored.components[0].priority, restored.components[0].payType], [false, 0, 'Variable Pay'])
assert.equal(restored.components[0].formula, reviewed.formula)
assert.equal(JSON.stringify(stale), snapshot, 'Restore must not mutate the stored draft/receipt object')
const fresh = proposeExcelPayrollSetup(batch, { ...context, components: [] }, ['row-one']).request.components[0]
assert.deepEqual([fresh.existingComponentId, fresh.taxable, fresh.priority, fresh.payType, fresh.isPeriodAdjustment], [undefined, true, 110, 'Fixed Pay', false], 'New-component defaults must retain their established meaning')
assert.equal(JSON.stringify({ source, batch, context }), original, 'Master/source objects must never be rewritten')
console.log('PASS: 6 focused metadata groups: existing exempt/zero-priority/Variable Pay, independent period semantics, master switch, reviewed new variant, immutable stale-draft refresh, unchanged new-component defaults. No requests or writes.')
