import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/services/excelPayslipService.ts', import.meta.url), 'utf8')
const mockClient = `export const postJson = async (path, body, fallback, options) => ({ path, body, options }); export const getJsonResult = async () => null; export const apiRequest = async () => null; export const readError = async () => '';`
const mockUrl = `data:text/javascript;base64,${Buffer.from(mockClient).toString('base64')}`
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText.replace(/from ['"]\.\/apiClient['"]/, `from '${mockUrl}'`)
const service = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)

test('corrected sends use a distinct endpoint so older APIs cannot silently treat them as ordinary mail', async () => {
  const request = { rowIds: ['row-1'], includeSeal: false, acknowledgeWarnings: false, amountDecimalPlaces: 0, mode: 'Individual', email: '', emailOverrides: {}, requestId: 'stable-corrected-request', resendCorrected: true, acknowledgeResend: true }
  const sent = await service.startExcelPayslipMailJob(907, 'saved october', request)
  assert.equal(sent.path, '/api/excel-payslips/batches/saved%20october/resend-corrected-job?clientId=907')
  assert.equal(sent.body, request)
  const retry = await service.startExcelPayslipMailJob(907, 'saved october', request)
  assert.equal(retry.body.requestId, sent.body.requestId)
  assert.deepEqual(retry.body, sent.body)
})

test('ordinary mail preserves the original endpoint and does not gain resend flags', async () => {
  const request = { rowIds: ['row-2'], includeSeal: false, acknowledgeWarnings: false, amountDecimalPlaces: 0, mode: 'Combined', email: 'coordinator@example.test', emailOverrides: {}, requestId: 'normal-request' }
  const sent = await service.startExcelPayslipMailJob(907, 'original', request)
  assert.equal(sent.path, '/api/excel-payslips/batches/original/send-job?clientId=907')
  assert.equal(sent.body.resendCorrected, undefined)
  assert.equal(sent.body.acknowledgeResend, undefined)
})
