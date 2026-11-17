import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/utils/payslipDesign.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const { payslipDesignIssues, payslipDesignPeriodIssues, payslipDesignPeriodLabel, payslipDesignPeriodRequest, payslipDesignPreviewIssues, payslipDesignScopeCapabilityIssues, newestClientExcelPayslip, movePayslipDesignField, nativePayslipKind, PayslipPreviewLease } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
const info = { sourceColumn: 1, label: 'Designation', kind: 'info', nativeCode: '', infoToken: 'DESIGNATION', visible: true }
const earning = { sourceColumn: 2, label: 'Basic Pay', kind: 'earning', nativeCode: 'BASIC', infoToken: '', visible: true }
const employer = { sourceColumn: 3, label: 'EPF employer', kind: 'employer', nativeCode: 'EPFER', infoToken: '', visible: true }
const catalog = () => ({ clientId: 907, sourceBatchId: 'saved-oct', sourceHash: 'a'.repeat(64), simpleLayout: false, fields: [info, earning, employer].map(x => ({ ...x })), nativeCodes: [{ code: 'BASIC', name: 'Basic', category: 'Earning' }, { code: 'EPFER', name: 'Employer PF', category: 'Benefit' }], infoTokens: ['DESIGNATION', 'UAN'] })

test('selected months cannot save to an older API that would ignore the scope', () => {
  assert.ok(payslipDesignScopeCapabilityIssues(null, ['2026-09']).length)
  assert.ok(payslipDesignScopeCapabilityIssues({}, ['2026-09']).length)
  assert.ok(payslipDesignScopeCapabilityIssues({ supportsPayrollMonthScope: false }, ['2026-09']).length)
  assert.deepEqual(payslipDesignScopeCapabilityIssues({ supportsPayrollMonthScope: true }, ['2026-09']), [])
})

test('legacy All and omitted scope retain their prior saves without requiring a scope capability', () => {
  assert.deepEqual(payslipDesignScopeCapabilityIssues({}, null), [])
  assert.deepEqual(payslipDesignScopeCapabilityIssues(null, undefined), [])
  assert.deepEqual(payslipDesignPeriodRequest(null), {})
})

test('legacy all-month and unchanged selected scope do not silently widen an existing design', () => {
  assert.deepEqual(payslipDesignPeriodRequest(undefined), {})
  assert.deepEqual(payslipDesignPeriodRequest(null), {})
  assert.deepEqual(payslipDesignPeriodRequest(['2026-09']), {})
  assert.deepEqual(payslipDesignPeriodRequest(null, true), { allPayPeriods: true })
})

test('an explicitly reviewed month selection is copied and ordered without an All flag', () => {
  const months = ['2026-10', '2026-09']
  assert.deepEqual(payslipDesignPeriodRequest(months, true), { payPeriods: ['2026-09', '2026-10'] })
  assert.deepEqual(months, ['2026-10', '2026-09'])
  assert.equal(payslipDesignPeriodLabel('2026-09'), 'Sep 2026')
  assert.equal(payslipDesignPeriodLabel('0001-01'), 'Jan 0001')
})

test('selected months fail closed for empty, duplicate, malformed or oversized scopes', () => {
  const invalid = [[], ['2026-09', '2026-09'], ['2026-00'], ['2026-13'], ['2026-9'], ['2026-09-01'], [' 2026-09'], ['2026-09\n'], ['0000-01'], Array.from({ length: 121 }, (_, i) => `${String(1900 + i).padStart(4, '0')}-01`)]
  for (const months of invalid) {
    assert.ok(payslipDesignPeriodIssues(months).length, JSON.stringify(months))
    assert.throws(() => payslipDesignPeriodRequest(months, true))
  }
  const maximum = Array.from({ length: 120 }, (_, i) => `${String(1900 + i).padStart(4, '0')}-01`)
  assert.deepEqual(payslipDesignPeriodIssues(maximum), [])
  assert.deepEqual(payslipDesignPeriodIssues(['0001-01', '9999-12']), [])
  assert.equal(payslipDesignPeriodLabel('2026-13'), '2026-13')
})

test('review accepts exact metadata and category-aware native mapping without source money', () => {
  const data = catalog()
  assert.deepEqual(payslipDesignIssues(data, data.fields, 907), [])
  assert.equal(nativePayslipKind('Benefit'), 'employer')
  assert.equal(nativePayslipKind('Reimbursement'), 'earning')
  assert.equal(nativePayslipKind('Other'), '')
  assert.equal(JSON.stringify(data).includes('amount'), false)
})
test('client, source fingerprint and immutable label metadata fail closed', () => {
  const data = catalog()
  assert.ok(payslipDesignIssues(data, data.fields, 908).length)
  assert.ok(payslipDesignIssues({ ...data, sourceHash: '' }, data.fields, 907).length)
  assert.ok(payslipDesignIssues(data, [{ ...info, label: 'Changed' }, earning, employer], 907).length)
  assert.ok(payslipDesignIssues(data, [info, info, employer], 907).length)
  assert.ok(payslipDesignIssues(data, [info, earning], 907).length)
})
test('hidden information may stay unmapped; visible information and every financial field require valid mapping', () => {
  const data = catalog()
  assert.deepEqual(payslipDesignIssues(data, [{ ...info, visible: false, infoToken: '' }, earning, employer], 907), [])
  assert.ok(payslipDesignIssues(data, [{ ...info, infoToken: '' }, earning, employer], 907).length)
  assert.ok(payslipDesignIssues(data, [info, { ...earning, visible: false, nativeCode: '' }, employer], 907).length)
  assert.ok(payslipDesignIssues(data, [info, { ...earning, nativeCode: 'EPFER' }, employer], 907).length)
})
test('two source financial labels cannot map the same native line twice', () => {
  const data = catalog(), copy = { ...earning, sourceColumn: 4, label: 'Basic again' }
  data.fields.push(copy)
  assert.ok(payslipDesignIssues(data, data.fields, 907).some(x => x.includes('already mapped')))
})
test('supplemental Master information keeps its verified token while visibility remains editable', () => {
  const data = catalog(), master = { ...info, sourceColumn: 1503, label: 'Work Location', infoToken: 'WORK_LOCATION' }
  data.fields.push(master); data.infoTokens.push('WORK_LOCATION')
  assert.deepEqual(payslipDesignIssues(data, [...data.fields.slice(0, 3), { ...master, visible: false }], 907), [])
  assert.ok(payslipDesignIssues(data, [...data.fields.slice(0, 3), { ...master, infoToken: 'UAN' }], 907).some(x => x.includes('verified Master')))
})
test('synthetic layout preview allows unmapped fields while actual save still requires full mapping', () => {
  const data = catalog(), fields = data.fields.map(field => ({ ...field, infoToken: '', nativeCode: '' }))
  assert.deepEqual(payslipDesignPreviewIssues(data, fields, 907), [])
  assert.ok(payslipDesignIssues(data, fields, 907).length)
  assert.ok(payslipDesignPreviewIssues(data, fields, 908).length)
  assert.ok(payslipDesignPreviewIssues(data, [{ ...fields[0], label: 'Invented' }, ...fields.slice(1)], 907).length)
  assert.ok(payslipDesignPreviewIssues(data, [fields[0], { ...fields[1], nativeCode: 'UNKNOWN' }, fields[2]], 907).length)
})
test('explicit client choice restores only its newest active managed design without mutating legacy rows', () => {
  const rows = [
    { id: 100, clientId: '907', theme: 'Excel Payslip', active: true },
    { id: 200, clientId: '908', theme: 'Excel Payslip', active: true },
    { id: 300, clientId: '907', theme: 'Classic', active: true },
    { id: 400, clientId: '907', theme: 'Excel Payslip', active: false },
    { id: 500, clientId: '0', theme: 'Excel Payslip', active: true },
    { id: 150, clientId: '907', theme: 'Excel Payslip', active: true },
  ]
  const original = structuredClone(rows)
  assert.equal(newestClientExcelPayslip(rows, 907), rows[5])
  assert.equal(newestClientExcelPayslip(rows, 908), rows[1])
  assert.equal(newestClientExcelPayslip(rows, 0), undefined)
  assert.equal(newestClientExcelPayslip(rows, 909), undefined)
  assert.deepEqual(rows, original)
})
test('reordering preserves fields, original data and boundary order', () => {
  const fields = catalog().fields
  const moved = movePayslipDesignField(fields, 3, -1)
  assert.deepEqual(moved.map(x => x.sourceColumn), [1, 3, 2])
  assert.deepEqual(fields.map(x => x.sourceColumn), [1, 2, 3])
  assert.equal(movePayslipDesignField(fields, 1, -1), fields)
  assert.equal(movePayslipDesignField(fields, 99, 1), fields)
})
test('a late PDF cannot allocate a URL after client/run/employee scope changes', () => {
  const created = [], revoked = []
  const lease = new PayslipPreviewLease(() => { const url = 'blob:' + created.length; created.push(url); return url }, url => revoked.push(url))
  const first = lease.begin('907:82:1')
  assert.equal(lease.accept('907:82:1', first, new Blob(['first'])), 'blob:0')
  const second = lease.begin('908:83:2')
  assert.deepEqual(revoked, ['blob:0'])
  assert.equal(lease.accept('907:82:1', first, new Blob(['late'])), null)
  assert.equal(created.length, 1)
  assert.equal(lease.accept('908:83:2', second, new Blob(['second'])), 'blob:1')
  lease.clear()
  assert.deepEqual(revoked, ['blob:0', 'blob:1'])
  assert.equal(lease.accept('908:83:2', second, new Blob(['closed'])), null)
})
test('replacing a PDF revokes its former URL and clear is idempotent', () => {
  const revoked = []
  let number = 0
  const lease = new PayslipPreviewLease(() => 'blob:' + number++, url => revoked.push(url))
  const request = lease.begin('907:82:1')
  lease.accept('907:82:1', request, new Blob(['a']))
  lease.accept('907:82:1', request, new Blob(['b']))
  lease.clear(); lease.clear()
  assert.deepEqual(revoked, ['blob:0', 'blob:1'])
})
