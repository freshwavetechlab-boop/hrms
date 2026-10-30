import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import vm from 'node:vm'

// An isolated stage can point at the project's installed test dependencies.
const dependencyRoot = process.env.PAYROLL_UI_TEST_DEPENDENCIES
  ? pathToFileURL(resolve(process.env.PAYROLL_UI_TEST_DEPENDENCIES, 'package.json'))
  : new URL('../package.json', import.meta.url)
const require = createRequire(dependencyRoot)
const ts = require('typescript')
const apiText = readFileSync(new URL('../src/services/apiClient.ts', import.meta.url), 'utf8')

function clock() {
  let now = 0, nextId = 0
  const timers = new Map()
  return {
    setTimeout(fn, delay) { const id = ++nextId; timers.set(id, { at: now + delay, fn }); return id },
    clearTimeout(id) { timers.delete(id) },
    advance(ms) {
      const target = now + ms
      for (;;) {
        const due = [...timers].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at)[0]
        if (!due) break
        now = due[1].at; timers.delete(due[0]); due[1].fn()
      }
      now = target
    },
    delays() { return [...timers.values()].map(timer => timer.at - now) },
  }
}

function apiHarness(serviceName = 'excelPayrollSetupService.ts') {
  const fakeClock = clock(), calls = []
  let reply
  const context = {
    AbortController, AbortSignal, Headers, FormData, DOMException, Response, Promise, URLSearchParams,
    window: { setTimeout: fakeClock.setTimeout, clearTimeout: fakeClock.clearTimeout, dispatchEvent() {} },
    sessionStorage: { getItem: () => null, removeItem() {} },
    localStorage: { getItem: () => null, removeItem() {} },
    legacyTokenKey: 'synthetic-only', jsonContent: 'application/json',
    apiUrl: path => path, startLoading: () => () => {}, notifyMutation() {},
    fetch: (path, options) => {
      calls.push({ path, options })
      return new Promise((resolve, reject) => {
        reply = value => resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }))
        options.signal.addEventListener('abort', () => reject(new DOMException('Synthetic timed abort', 'AbortError')), { once: true })
      })
    },
  }
  vm.createContext(context)
  const apiFile = ts.createSourceFile('apiClient.ts', apiText, ts.ScriptTarget.Latest, true)
  const names = new Set(['apiRequest', 'getJsonResult', 'postJson', 'mutateJson', 'readError', 'readJson'])
  const selected = apiFile.statements.filter(node => ts.isFunctionDeclaration(node) && names.has(node.name?.text))
    .map(node => node.getText(apiFile).replace(/^export\s+/, '')).join('\n')
  vm.runInContext(ts.transpile(selected, { target: ts.ScriptTarget.ES2022 }), context)
  const exports = {}
  const serviceText = readFileSync(new URL(`../src/services/${serviceName}`, import.meta.url), 'utf8')
  const moduleCode = ts.transpileModule(serviceText, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', moduleCode)(exports, name => {
    assert.equal(name, './apiClient')
    return { postJson: context.postJson, getJsonResult: context.getJsonResult }
  })
  return { service: exports, calls, fakeClock, reply: value => reply(value) }
}

function syntheticRequest() {
  return Object.freeze({ batchId: 'synthetic-saved-batch', rowIds: Object.freeze(Array.from({ length: 100 }, (_, i) => `source-${i + 1}`)) })
}

test('source setup context accepts a slow exact scoped read without submitting configuration', async () => {
  const h = apiHarness(), rows = Object.freeze(['source-1', 'source-2'])
  const pending = h.service.getPayrollSetupContext(11, 'saved-batch', rows)
  assert.equal(h.fakeClock.delays()[0], 120000)
  assert.equal(h.calls[0].path, '/api/excel-payroll-setup/context?clientId=11&batchId=saved-batch&rowIds=source-1%2Csource-2')
  assert.equal(h.calls[0].options.body, undefined)
  h.fakeClock.advance(45000)
  assert.equal(h.calls[0].options.signal.aborted, false)
  h.reply({ batchId: 'saved-batch' })
  assert.equal((await pending).ok, true)
  assert.equal(h.calls.length, 1)
  assert.deepEqual(rows, ['source-1', 'source-2'])
  assert.equal(h.fakeClock.delays().length, 0)
})

test('source context timeout is explicitly a read failure with no automatic retry', async () => {
  const h = apiHarness(), pending = h.service.getPayrollSetupContext(11, 'saved-batch', ['source-1'])
  h.fakeClock.advance(120000)
  const result = await pending
  assert.equal(result.ok, false)
  assert.match(result.error, /No configuration Apply was submitted/)
  assert.equal(h.calls.length, 1)
  assert.equal(h.calls[0].options.body, undefined)
  assert.equal(h.fakeClock.delays().length, 0)
})

test('Apply accepts a 145-second committed response using one unchanged reviewed request', async () => {
  const h = apiHarness(), request = syntheticRequest(), before = JSON.stringify(request)
  const pending = h.service.applyPayrollSetup(11, request, 'reviewed-plan', 'stable-request-id')
  assert.equal(h.calls.length, 1)
  assert.equal(h.calls[0].path, '/api/excel-payroll-setup/apply?clientId=11')
  assert.equal(h.fakeClock.delays()[0], 240000)
  assert.deepEqual(JSON.parse(h.calls[0].options.body), { request, planHash: 'reviewed-plan', requestId: 'stable-request-id' })
  h.fakeClock.advance(145000)
  assert.equal(h.calls[0].options.signal.aborted, false)
  h.reply({ importId: 'synthetic-receipt', updatedCount: 100, replayed: false })
  const result = await pending
  assert.equal(result.ok, true)
  assert.equal(result.data.importId, 'synthetic-receipt')
  assert.equal(h.calls.length, 1, 'waiting for a slow receipt must never submit Apply again')
  assert.equal(h.fakeClock.delays().length, 0)
  assert.equal(JSON.stringify(request), before)
})

test('a true Apply timeout remains uncertain and never retries or replaces request identity', async () => {
  const h = apiHarness(), request = syntheticRequest()
  const pending = h.service.applyPayrollSetup(11, request, 'reviewed-plan', 'stable-request-id')
  h.fakeClock.advance(240000)
  const result = await pending
  assert.equal(result.ok, false)
  assert.equal(result.status, 0)
  assert.match(result.error, /Processing may still be in progress/)
  assert.match(result.error, /check the current status before retrying/)
  assert.equal(h.calls.length, 1)
  assert.equal(JSON.parse(h.calls[0].options.body).requestId, 'stable-request-id')
  assert.equal(h.fakeClock.delays().length, 0)
})

test('preview and draft retain their existing two-minute bounded window', async () => {
  for (const method of ['previewPayrollSetup', 'savePayrollSetupDraft']) {
    const h = apiHarness(), pending = h.service[method](11, syntheticRequest(), 'stable-request-id')
    assert.equal(h.fakeClock.delays()[0], 120000, method)
    h.reply({})
    await pending
    assert.equal(h.calls.length, 1)
  }
})

test('saved Employee simulation retains its two-minute window and is never changed to overrides', async () => {
  const h = apiHarness(), pending = h.service.verifyPayrollSetupEmployees(11, '2026-09', [101])
  assert.equal(h.fakeClock.delays()[0], 120000)
  const sent = JSON.parse(h.calls[0].options.body)
  assert.deepEqual(sent, { mode: 'Employee', clientId: 11, employeeId: 101, payPeriod: '2026-09', payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: {} })
  h.reply({})
  await pending
  assert.equal(h.calls.length, 1)
})

test('the shared API default stays 30 seconds', () => {
  assert.match(apiText, /options\.timeoutMs \?\? 30000/)
})

test('selected processing accepts a 183-second completed Draft with the same run/fingerprint and one fetch', async () => {
  const h = apiHarness('selectedPayRunService.ts')
  const body = Object.freeze({ clientId: 11, payPeriod: '2026-09', expectedFingerprint: 'reviewed-current-fingerprint', confirmed: true })
  const pending = h.service.processSelectedPayRun(69, body)
  assert.equal(h.fakeClock.delays()[0], 300000)
  assert.equal(h.calls[0].path, '/api/pay-runs/69/process-selected')
  assert.deepEqual(JSON.parse(h.calls[0].options.body), body)
  h.fakeClock.advance(183000)
  assert.equal(h.calls[0].options.signal.aborted, false)
  h.reply({ id: 69, status: 'Draft' })
  const result = await pending
  assert.equal(result.ok, true)
  assert.equal(result.data.status, 'Draft')
  assert.equal(h.calls.length, 1)
  assert.equal(h.fakeClock.delays().length, 0)
})

test('selected processing true timeout stays uncertain and never retries the consumed preview', async () => {
  const h = apiHarness('selectedPayRunService.ts')
  const body = Object.freeze({ clientId: 11, payPeriod: '2026-09', expectedFingerprint: 'reviewed-current-fingerprint', confirmed: true })
  const pending = h.service.processSelectedPayRun(69, body)
  h.fakeClock.advance(300000)
  const result = await pending
  assert.equal(result.ok, false)
  assert.equal(result.status, 0)
  assert.equal(result.error, 'Processing response timed out. Check the current run before retrying.')
  assert.equal(h.calls.length, 1)
  assert.deepEqual(JSON.parse(h.calls[0].options.body), body)
  assert.equal(h.fakeClock.delays().length, 0)
})

test('selected processing scope preview retains the 30-second shared read window', async () => {
  const h = apiHarness('selectedPayRunService.ts')
  const pending = h.service.getSelectedPayRunProcessingPreview(69, 11, '2026-09')
  assert.equal(h.fakeClock.delays()[0], 30000)
  assert.equal(h.calls[0].path, '/api/pay-runs/69/processing-preview?clientId=11&payPeriod=2026-09')
  h.reply({ payRunId: 69, clientId: 11, payPeriod: '2026-09' })
  assert.equal((await pending).ok, true)
  assert.equal(h.calls.length, 1)
})

test('regular creation and held selected-draft creation retain their existing three-minute window', () => {
  const text = readFileSync(new URL('../src/services/payrollService.ts', import.meta.url), 'utf8')
  const file = ts.createSourceFile('payrollService.ts', text, ts.ScriptTarget.Latest, true)
  for (const name of ['createPayRun', 'createSelectedPayRunDraft']) {
    const declaration = file.statements.filter(ts.isVariableStatement)
      .flatMap(statement => [...statement.declarationList.declarations]).find(node => node.name.getText(file) === name)
    assert.ok(declaration)
    assert.match(declaration.initializer.body.arguments[3].getText(file), /timeoutMs: 180000/)
  }
})
