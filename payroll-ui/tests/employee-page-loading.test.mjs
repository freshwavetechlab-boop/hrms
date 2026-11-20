import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exported = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeePageLoading.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)(exported)
const { createEmployeePageLoader } = exported
const deferred = () => { let resolve, reject; const promise = new Promise((done, fail) => { resolve = done; reject = fail }); return { promise, resolve, reject } }
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve() }

test('clients publish before a slow employee directory, without starting payroll/editor reads', async () => {
  const clients = deferred(), employees = deferred(), locations = deferred(), events = []
  let editorCalls = 0
  const loader = createEmployeePageLoader({ clients: () => clients.promise, employees: () => employees.promise, locations: () => locations.promise }, {
    clients: rows => events.push(['clients', rows]), employees: rows => events.push(['employees', rows]), locations: rows => events.push(['locations', rows]),
  }, async () => { editorCalls++; return { salaryStructures: [] } }, value => events.push(['editor', value]))
  const read = loader.load()
  clients.resolve([{ id: 42, name: 'Example client' }]); await settle()
  assert.deepEqual(events, [['clients', [{ id: 42, name: 'Example client' }]]])
  assert.equal(editorCalls, 0)
  employees.resolve([{ id: 7, clientId: 42 }]); locations.resolve([]); await read
  assert.equal(events.some(([key]) => key === 'employees'), true)
  assert.equal(editorCalls, 0)
})

test('one failed ancillary read does not discard successful clients and employees', async () => {
  const published = {}
  const loader = createEmployeePageLoader({ clients: async () => [42], employees: async () => [7], locations: async () => { throw Error('Synthetic location failure') } }, {
    clients: value => { published.clients = value }, employees: value => { published.employees = value }, locations: value => { published.locations = value },
  }, async () => ({}), () => {})
  await loader.load()
  assert.deepEqual(published, { clients: [42], employees: [7] })
})

test('newer refresh wins over a late older read', async () => {
  const first = deferred(), second = deferred(), seen = []; let calls = 0
  const loader = createEmployeePageLoader({ employees: () => ++calls === 1 ? first.promise : second.promise }, { employees: value => seen.push(value) }, async () => ({}), () => {})
  const oldRead = loader.load(), newRead = loader.load()
  second.resolve(['updated']); await newRead; first.resolve(['old']); await oldRead
  assert.deepEqual(seen, [['updated']])
})

test('scope disposal suppresses directory and editor results from the previous account', async () => {
  const directory = deferred(), references = deferred(), seen = []
  const loader = createEmployeePageLoader({ employees: () => directory.promise }, { employees: value => seen.push(value) }, () => references.promise, value => seen.push(value))
  const reading = loader.load(), editing = loader.editor(); await settle(); loader.dispose()
  directory.resolve(['old account']); references.resolve({ confidential: 'old account setup' })
  await reading; assert.equal(await editing, undefined)
  assert.deepEqual(seen, [])
  assert.equal(await loader.editor(), undefined)
})

test('a first editor action waits for the actual settings and concurrent actions share that read', async () => {
  const references = deferred(), seen = []; let reads = 0
  const loader = createEmployeePageLoader({}, {}, () => { reads++; return references.promise }, value => seen.push(value))
  const first = loader.editor(), second = loader.editor(); await settle()
  assert.equal(reads, 1); assert.deepEqual(seen, [])
  const setup = Object.freeze({ salaryStructures: Object.freeze([{ id: 12 }]), salaryComponents: Object.freeze([{ code: 'BASIC' }]) })
  references.resolve(setup)
  assert.equal(await first, setup); assert.equal(await second, setup)
  assert.deepEqual(seen, [setup])
  assert.equal(await loader.editor(), setup); assert.equal(reads, 1)
})

test('failed editor references can retry without poisoning successful directory data', async () => {
  const seen = []; let reads = 0
  const loader = createEmployeePageLoader({ employees: async () => [7] }, { employees: value => seen.push(value) }, async () => {
    if (++reads === 1) throw Error('Synthetic setup failure')
    return { salaryStructures: [{ id: 12 }] }
  }, value => seen.push(value))
  await loader.load(); await assert.rejects(loader.editor(), /Synthetic setup failure/)
  assert.deepEqual(seen, [[7]])
  assert.deepEqual(await loader.editor(), { salaryStructures: [{ id: 12 }] })
  assert.equal(reads, 2)
})

test('a disposed page does not show a late settings error in the next workspace', async () => {
  const references = deferred()
  const loader = createEmployeePageLoader({}, {}, () => references.promise, () => assert.fail('Disposed settings must not publish'))
  const editing = loader.editor(); await settle(); loader.dispose(); references.reject(Error('Previous account settings failed'))
  assert.equal(await editing, undefined)
})
