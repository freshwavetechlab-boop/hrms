import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const compile = source => ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const scope = {}
new Function('exports', compile(read('../src/utils/salaryTemplateContext.ts')))(scope)
const helpers = {}
new Function('exports', 'require', compile(read('../src/utils/employeeSalaryTemplate.ts')))(helpers, () => scope)

const foreign = { id: 316748801016, clientId: '33', name: 'Other client salary setup / 3', active: true, annualCtc: '900000', lines: [] }
const owned = { id: 271, clientId: '55', name: 'Selected client template', active: true, annualCtc: '', lines: [] }
const sample = () => ({ id: 5225, clientId: 55, employeeCode: 'SYN-000205', salaryStructureId: '', annualCtc: 436800, salaryJson: '{"11":"28000","12":"8400"}', salaryComponents: { 11: 28000, 12: 8400 } })

test('employee choices never fall back to a foreign client and support shared/multi-client definitions', () => {
  assert.deepEqual(helpers.employeeSalaryTemplates([foreign], 55), [])
  const shared = { ...owned, id: 272, clientId: '0' }
  const emptyShared = { ...owned, id: 273, clientId: '' }
  const multi = { ...owned, id: 274, clientId: '17:Other;55:Selected|208' }
  const inactive = { ...owned, id: 275, active: false }
  assert.deepEqual(helpers.employeeSalaryTemplates([foreign, owned, shared, emptyShared, multi, inactive], 55).map(row => row.id), [271, 272, 273, 274])
  assert.equal(helpers.assignedEmployeeSalaryTemplate([foreign, owned], 55, ''), undefined)
  assert.equal(helpers.assignedEmployeeSalaryTemplate([foreign, owned], 55, foreign.id), undefined)
  assert.equal(helpers.assignedEmployeeSalaryTemplate([owned], 55, '271:legacy label'), owned)
  assert.equal(helpers.assignedEmployeeSalaryTemplate([owned], 0, owned.id), undefined)
})

test('the salary workspace retains global zero-string and authorized multi-client definitions', () => {
  const rows = [foreign, { ...owned, clientId: '0' }, { ...owned, id: 272, clientId: '' }, { ...owned, id: 273, clientId: '17;55' }, { ...owned, id: 274, clientId: '208|33' }]
  assert.deepEqual(helpers.salaryTemplatesForVisibleClients(rows, new Set(['55'])).map(row => row.id), [271, 272, 273])
})

const source = read('../src/pages/EmployeePage.tsx')
const ast = ts.createSourceFile('EmployeePage.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const page = ast.statements.find(node => ts.isFunctionDeclaration(node) && node.name?.text === 'EmployeePage')
const variables = page.body.statements.filter(ts.isVariableStatement).flatMap(node => [...node.declarationList.declarations])
const actual = name => {
  const declaration = variables.find(node => ts.isIdentifier(node.name) && node.name.text === name)
  assert.ok(declaration, `actual employee handler ${name} exists`)
  return `var ${declaration.getText(ast)};`
}
const selectedHandlers = ['normalizeEmployeeSalary', 'empLine', 'applyStructure', 'applyCtc', 'applyClient', 'newEmployee', 'editEmployee', 'saveEmployee']
const handlerCode = compile(selectedHandlers.map(actual).join('\n'))

function harness(overrides = {}) {
  const calls = { calc: [], saved: [], notices: [], history: [], closed: 0 }
  let context
  context = vm.createContext({
    JSON, String, Number, Error, Object, ...helpers,
    employee: sample(), setup: { salaryStructures: [foreign], salaryComponents: [] }, salaryOverrides: {}, chosenStructure: undefined,
    employee0: { ...sample(), id: 0, clientId: 0, employeeCode: '', annualCtc: 0, salaryJson: '{}', salaryComponents: {} },
    editorGeneration: { current: 0 }, currentClientFilter: { current: 55 }, clientFilter: 55,
    normalizeEmployeeDetails: row => ({ ...row }), inferSalaryOverrides: () => ({}),
    hasDailyRateComponents: () => false, dailyRateSalary: false,
    calcSalary: (ctc, structure) => { calls.calc.push([ctc, structure?.id]); return '{"11":"30000","12":"9000"}' },
    withSalary: (row, salaryJson) => ({ ...row, salaryJson, salaryComponents: Object.fromEntries(Object.entries(JSON.parse(salaryJson)).map(([key, value]) => [key, Number(value)])) }),
    salaryRecord: row => Object.fromEntries(Object.entries(row.salaryComponents).map(([key, value]) => [key, String(value)])),
    notify: (...args) => calls.notices.push(args),
    setEmployee: row => { context.employee = row; context.chosenStructure = helpers.assignedEmployeeSalaryTemplate(context.setup.salaryStructures, row.clientId, row.salaryStructureId) },
    setSalaryOverrides: value => { context.salaryOverrides = value },
    editorReferences: async () => ({ setup: context.setup }),
    setEmployeeInfotype: value => { context.employeeInfotype = value }, employeeInfotype: '0008', changeReason: '',
    setChangeReason() {}, setModalOpen: value => { context.modalOpen = value },
    loadEmployeeHistory: async id => calls.history.push(id),
    calculateSalaryDetails: () => [], toEmployeePayload: row => JSON.parse(JSON.stringify(row)),
    persistEmployee: async payload => { calls.saved.push(payload); return { ok: true, data: { id: payload.id || 99 } } },
    closeModal: () => { calls.closed++ }, load: async () => {},
    ...overrides,
  })
  context.chosenStructure = helpers.assignedEmployeeSalaryTemplate(context.setup.salaryStructures, context.employee.clientId, context.employee.salaryStructureId)
  vm.runInContext(handlerCode, context)
  return { context, calls }
}

test('opening and saving unassigned, unavailable or foreign-bound employees retains the stored pay and assignment', async () => {
  for (const assignment of ['', '0', 'missing', String(foreign.id)]) {
    const employee = { ...sample(), salaryStructureId: assignment }
    const before = JSON.stringify(employee)
    const h = harness({ employee })
    await h.context.editEmployee(employee, '0008')
    assert.equal(JSON.stringify(h.context.employee), before)
    assert.equal(h.context.modalOpen, true)
    assert.equal(h.calls.saved.length, 0, 'opening never posts or assigns a template')
    await h.context.saveEmployee()
    assert.equal(JSON.stringify(h.calls.saved[0]), before)
    assert.equal(h.calls.calc.length, 0, 'unrelated save does not run any template salary formula')
    assert.equal(JSON.stringify(employee), before, 'source directory record is not mutated')
  }
})

test('even an available client default is not automatically selected on opening or creating an employee', async () => {
  const h = harness({ setup: { salaryStructures: [foreign, owned], salaryComponents: [] } })
  const before = JSON.stringify(h.context.employee)
  await h.context.editEmployee(h.context.employee)
  assert.equal(JSON.stringify(h.context.employee), before)
  await h.context.newEmployee()
  assert.equal(h.context.employee.salaryStructureId, '')
  assert.equal(h.context.employee.annualCtc, 0)
  assert.equal(h.context.employee.clientId, 55)
  assert.equal(h.calls.calc.length, 0)
})

test('changing CTC without a binding or changing client does not invent salary components or a new binding', () => {
  const h = harness({ setup: { salaryStructures: [foreign, owned], salaryComponents: [] } })
  const stored = h.context.employee.salaryJson
  h.context.applyCtc(480000)
  assert.equal(h.context.employee.salaryStructureId, '')
  assert.equal(h.context.employee.annualCtc, 480000)
  assert.equal(h.context.employee.salaryJson, stored)
  h.context.applyClient('33:Other client')
  assert.equal(h.context.employee.clientId, 33)
  assert.equal(h.context.employee.salaryStructureId, '')
  assert.equal(h.context.employee.annualCtc, 480000)
  assert.equal(h.context.employee.salaryJson, stored)
  assert.equal(h.calls.calc.length, 0)
})

test('only an explicit active scoped selection calculates pay; clearing it retains amounts', () => {
  const h = harness({ setup: { salaryStructures: [foreign, owned, { ...owned, id: 272, active: false }], salaryComponents: [] } })
  const before = JSON.stringify(h.context.employee)
  h.context.applyStructure(String(foreign.id)); h.context.applyStructure('272'); h.context.applyStructure('999')
  assert.equal(JSON.stringify(h.context.employee), before)
  assert.equal(h.calls.calc.length, 0)
  h.context.applyStructure(String(owned.id))
  assert.equal(h.context.employee.salaryStructureId, String(owned.id))
  assert.equal(h.calls.calc.length, 1)
  h.context.applyClient('55:Selected client')
  assert.equal(h.context.employee.salaryStructureId, String(owned.id), 'reselecting the same client does not clear its valid assignment')
  const selectedPay = h.context.employee.salaryJson
  const selectedCtc = h.context.employee.annualCtc
  h.context.applyStructure('')
  assert.equal(h.context.employee.salaryStructureId, '')
  assert.equal(h.context.employee.salaryJson, selectedPay)
  assert.equal(h.context.employee.annualCtc, selectedCtc)
  assert.equal(h.calls.calc.length, 1)
})

test('manual Basic Pay edits while unassigned change only the selected stored component', () => {
  const h = harness()
  h.context.empLine('11', '29000')
  assert.equal(h.context.employee.salaryStructureId, '')
  assert.equal(h.context.employee.annualCtc, 436800)
  assert.equal(h.context.employee.salaryComponents[11], 29000)
  assert.equal(h.context.employee.salaryComponents[12], 8400)
  assert.equal(h.calls.calc.length, 0)
})

test('a template with no positive CTC preset retains the employee CTC during explicit selection', () => {
  for (const annualCtc of ['0', '', '-1', 'NaN', 'Infinity']) {
    const template = { ...owned, annualCtc }
    const h = harness({ setup: { salaryStructures: [foreign, template], salaryComponents: [] } })
    h.context.applyStructure(String(template.id))
    assert.equal(h.context.employee.salaryStructureId, String(template.id))
    assert.equal(h.context.employee.annualCtc, 436800)
    assert.deepEqual(h.calls.calc, [[436800, template.id]])
  }
  const h = harness({ setup: { salaryStructures: [{ ...owned, annualCtc: '600000' }], salaryComponents: [] } })
  h.context.applyStructure(String(owned.id))
  assert.equal(h.context.employee.annualCtc, 600000, 'an explicit positive preset is still applied')
  assert.deepEqual(h.calls.calc, [[600000, owned.id]])
})
