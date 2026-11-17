import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/services/payslipService.ts', import.meta.url), 'utf8')
const mock = "export const postJson = async (path, body, fallback, options) => ({ path, body, options }); export const getJsonResult = async (path, fallback, options) => ({path, options}); export const apiRequest = async (path, options) => { globalThis.__payslipCall = {path,options}; if(globalThis.__payslipResponse instanceof Error) throw globalThis.__payslipResponse; return globalThis.__payslipResponse }; export const readError = async response => response.text();"
const url = 'data:text/javascript;base64,' + Buffer.from(mock).toString('base64')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText.replace(/from ['"]\.\/apiClient['"]/, "from '" + url + "'")
const service = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))

test('design metadata reads and review are explicitly bound to selected client and saved source', async () => {
  assert.equal((await service.getPayslipDesignSources(907)).path, '/api/payslip-designs/sources?clientId=907')
  assert.equal((await service.getPayslipDesign(907, 12)).path, '/api/payslip-designs/12?clientId=907')
  const result = await service.previewPayslipDesign(907, 'saved-oct')
  assert.equal(result.path, '/api/payslip-designs/preview')
  assert.deepEqual(result.body, { clientId: 907, sourceBatchId: 'saved-oct' })
})
test('atomic design retry retains exact request identity, mapping and previous profile hash', async () => {
  const request = { requestId: 'stable-review', template: { id: 12, clientId: 907, theme: 'Excel Payslip', name: 'Reviewed' }, sourceBatchId: 'saved-oct', sourceHash: 'a'.repeat(64), existingProfileHash: 'b'.repeat(64), amountDecimalPlaces: 2, fields: [], reviewed: true }
  const first = await service.savePayslipDesign(request), retry = await service.savePayslipDesign(request)
  assert.equal(first.path, '/api/payslip-designs/save')
  assert.equal(first.body, request); assert.equal(retry.body, request)
  assert.equal(first.options.toast, false); assert.equal(first.options.timeoutMs, 120000)
})

test('service preserves omitted scope and exact reviewed selection on retries without fallback settings', async () => {
  const legacy = { requestId: 'legacy-review', template: { id: 12, clientId: 907, theme: 'Excel Payslip', name: 'Reviewed' }, sourceBatchId: 'saved-oct', sourceHash: 'a'.repeat(64), existingProfileHash: 'b'.repeat(64), amountDecimalPlaces: 2, fields: [], reviewed: true }
  const unchanged = await service.savePayslipDesign(legacy)
  assert.equal(Object.hasOwn(unchanged.body, 'payPeriods'), false)
  assert.equal(Object.hasOwn(unchanged.body, 'allPayPeriods'), false)
  const selected = { ...legacy, requestId: 'selected-review', payPeriods: ['2026-09'] }
  assert.equal((await service.savePayslipDesign(selected)).body, selected)
  assert.equal((await service.savePayslipDesign(selected)).body, selected)
  const all = { ...legacy, requestId: 'all-reviewed', allPayPeriods: true }
  assert.deepEqual((await service.savePayslipDesign(all)).body, all)
  assert.equal(Object.hasOwn(selected, 'previousClassicTemplate'), false)
})

test('synthetic PDF preview retains exact selected-month metadata while forcing reviewed false', async () => {
  globalThis.__payslipResponse = new Response('%PDF-1.7 sample', { headers: { 'content-type': 'application/pdf' } })
  const draft = { requestId: '', template: { id: 12, clientId: 907, theme: 'Excel Payslip', name: 'Scoped' }, sourceBatchId: 'saved-sep', sourceHash: 'a'.repeat(64), amountDecimalPlaces: 0, payPeriods: ['2026-09'], fields: [], reviewed: true }
  assert.equal((await service.previewPayslipDesignPdf(draft)).ok, true)
  assert.deepEqual(JSON.parse(globalThis.__payslipCall.options.body).payPeriods, ['2026-09'])
  assert.equal(JSON.parse(globalThis.__payslipCall.options.body).reviewed, false)
  assert.equal(Object.hasOwn(JSON.parse(globalThis.__payslipCall.options.body), 'allPayPeriods'), false)
})
test('synthetic draft preview uses its own read-only PDF route without any save acknowledgement or employee amounts', async () => {
  const signal = new AbortController().signal
  globalThis.__payslipResponse = new Response('%PDF-1.7 sample', { headers: { 'content-type': 'application/pdf' } })
  const draft = { requestId: '', template: { id: 0, clientId: 907, theme: 'Excel Payslip', name: 'Excel sample' }, sourceBatchId: 'saved-oct', sourceHash: 'a'.repeat(64), amountDecimalPlaces: 0, fields: [{ sourceColumn: 1, kind: 'earning', label: 'Basic', nativeCode: '', infoToken: '', visible: true }], reviewed: true }
  const result = await service.previewPayslipDesignPdf(draft, { signal })
  assert.equal(result.ok, true)
  assert.equal(result.data.fileName, 'payslip-design-sample.pdf')
  assert.equal(globalThis.__payslipCall.path, '/api/payslip-designs/preview-pdf')
  assert.equal(globalThis.__payslipCall.options.method, 'POST')
  assert.equal(globalThis.__payslipCall.options.signal, signal)
  const body = JSON.parse(globalThis.__payslipCall.options.body)
  assert.equal(body.reviewed, false)
  assert.equal(body.sourceHash, draft.sourceHash)
  assert.equal(body.template.id, 0)
  assert.deepEqual(Object.keys(body.fields[0]).sort(), ['infoToken', 'kind', 'label', 'nativeCode', 'sourceColumn', 'visible'])
  assert.equal(draft.reviewed, true)
})
test('legacy HTML preview and ordinary email contract remain unchanged', async () => {
  const signal = new AbortController().signal
  const run = await service.getRegisterPayRun(82, { signal })
  assert.equal(run.path, '/api/pay-runs/82')
  assert.equal(run.options.signal, signal)
  assert.equal(run.options.loader, false)
  assert.equal(run.options.toast, false)
  assert.equal((await service.getRegisterPayslip(82, 1)).path, '/api/pay-runs/82/payslips/1')
  const sent = await service.sendRegisterPayslips(82, [1, 2])
  assert.equal(sent.path, '/api/pay-runs/82/payslips/send')
  assert.deepEqual(sent.body, { employeeIds: [1, 2] })
})
test('native PDF uses authenticated API transport, supports cancellation and safe response filename', async () => {
  const signal = new AbortController().signal
  globalThis.__payslipResponse = new Response('%PDF-1.7 synthetic', { headers: { 'content-type': 'application/pdf', 'content-disposition': "attachment; filename*=UTF-8''..%2FEmployee%20Payslip.pdf" } })
  const result = await service.getRegisterPayslipPdf(82, 1, { signal })
  assert.equal(globalThis.__payslipCall.path, '/api/pay-runs/82/payslips/1/pdf')
  assert.equal(globalThis.__payslipCall.options.signal, signal)
  assert.equal(result.ok, true); assert.equal(result.data.fileName, 'Employee Payslip.pdf')
  assert.ok(result.data.blob.size > 0)
})
test('non-PDF, empty PDF and backend errors cannot masquerade as a usable native preview', async () => {
  globalThis.__payslipResponse = new Response('<html>login</html>', { headers: { 'content-type': 'text/html' } })
  assert.equal((await service.getRegisterPayslipPdf(82, 1)).ok, false)
  globalThis.__payslipResponse = new Response('', { headers: { 'content-type': 'application/pdf' } })
  assert.match((await service.getRegisterPayslipPdf(82, 1)).error, /empty/)
  globalThis.__payslipResponse = new Response('Not approved', { status: 409 })
  assert.deepEqual(await service.getRegisterPayslipPdf(82, 1), { ok: false, data: null, error: 'Not approved', status: 409 })
})
test('PDF network failure stays a failed result without a fallback HTML export', async () => {
  globalThis.__payslipResponse = new Error('Network unavailable')
  assert.deepEqual(await service.getRegisterPayslipPdf(82, 1), { ok: false, data: null, error: 'Network unavailable', status: 0 })
})
test('ordinary native PDF retains its run/employee fallback filename', async () => {
  globalThis.__payslipResponse = new Response('%PDF-1.7 synthetic', { headers: { 'content-type': 'application/pdf' } })
  assert.equal((await service.getRegisterPayslipPdf(82, 1)).data.fileName, 'payslip-82-1.pdf')
})
