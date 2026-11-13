import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/services/excelPayslipUpdateService.ts', import.meta.url), 'utf8')
const mock = `export const postJson = async (path, body, fallback, options) => ({ path, body, options }); export const getJsonResult = async (path, fallback, options) => ({path, options});`
const url = `data:text/javascript;base64,${Buffer.from(mock).toString('base64')}`
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText.replace(/from ['"]\.\/apiClient['"]/, `from '${url}'`)
const service = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)

test('saved-batch update uses distinct catalog/preview/apply routes and preserves exact strings', async () => {
  assert.equal((await service.getPayslipUpdateCatalog(907, 'saved october')).path, '/api/excel-payslips/batches/saved%20october/update-catalog?clientId=907')
  const body = { month: '2026-10', sourceFileName: 'updates.xlsx', rows: [{ employeeCode: '00001-A', values: { 'master:uan': '001234567890' } }] }
  const preview = await service.previewPayslipUpdate(907, 'saved october', body)
  assert.equal(preview.path, '/api/excel-payslips/batches/saved%20october/update-preview?clientId=907'); assert.equal(preview.body, body)
  const request = { token: 'reviewed', selectedChangeIds: ['change-1'], requestId: 'stable-apply-id', acknowledged: true, acknowledgeZeroChanges: false }
  const apply = await service.applyPayslipUpdate(907, 'saved october', request), retry = await service.applyPayslipUpdate(907, 'saved october', request)
  assert.equal(apply.path, '/api/excel-payslips/batches/saved%20october/update-apply?clientId=907'); assert.deepEqual(apply.body, retry.body)
  assert.equal(apply.options.toast, false); assert.equal(apply.options.timeoutMs, 120000)
})
