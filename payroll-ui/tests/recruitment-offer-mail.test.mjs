import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const calls = []
const api = { getJsonResult: (...args) => { calls.push(args); return { ok: true } }, postJson: (...args) => { calls.push(args); return { ok: true } } }
const service = {}
const compiled = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentOfferMailService.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', 'require', compiled)(service, () => api)

test('offer email review and send bind the explicit client, selection, immutable releases and retry id', () => {
  service.previewOfferMail(7, [11, 13], true)
  assert.deepEqual(calls.pop().slice(0, 2), ['/api/recruitment/document-roster/mail-preview?clientId=7', { ids: [11, 13], retryFailed: true }])
  const requestId = 'fa76b47c-8965-45c3-8a6d-d518c81f46be'
  const recipients = [{ rosterId: 11, releaseId: 123 }, { rosterId: 13, releaseId: null }]
  service.sendOfferMail(7, [11, 13], requestId, recipients, true)
  const first = calls.pop()
  assert.equal(first[0], '/api/recruitment/document-roster/mail-jobs?clientId=7')
  assert.deepEqual(first[1], { ids: [11, 13], requestId, retryFailed: true, releaseIds: { 11: 123, 13: 0 } })
  service.sendOfferMail(7, [11, 13], requestId, recipients, true)
  assert.deepEqual(calls.pop()[1], first[1], 'an uncertain response can replay the same id without another send')
})

test('job feed and dismissal do not switch client scope or use payslip endpoints', () => {
  service.getOfferMailJobs(7)
  assert.equal(calls.pop()[0], '/api/recruitment/document-roster/mail-jobs?clientId=7')
  service.getOfferMailJobs()
  assert.equal(calls.pop()[0], '/api/recruitment/document-roster/mail-jobs')
  service.dismissOfferMailJob(7, 'job/id')
  assert.equal(calls.pop()[0], '/api/recruitment/document-roster/mail-jobs/job%2Fid/dismiss?clientId=7')
})

test('malformed mail progress is rejected before replacing the last known status', () => {
  const job = { id: 'job', clientId: 7, clientName: 'Synthetic client', isComplete: false, total: 2, percentComplete: 50, items: [] }
  assert.equal(service.validOfferMailJobs([job]), true)
  for (const changes of [{ clientId: 0 }, { percentComplete: 101 }, { percentComplete: NaN }, { isComplete: 'yes' }, { items: null }, { total: -1 }]) assert.equal(service.validOfferMailJobs([{ ...job, ...changes }]), false)
})
