import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('../', import.meta.url))
const runtime = JSON.parse(fs.readFileSync(process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json', 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = new URL(process.env.HRMS_TEST_UI_URL || 'http://localhost:5173').origin
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname), 'This fixture runs against a local UI only')
const output = process.env.HRMS_MASTER_METADATA_RESULTS || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/master-metadata-ui'
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-master-metadata-${crypto.randomUUID()}.tsx`
const fixturePath = path.join(repo, 'payroll-ui', fixtureName)
const batchId = 'a'.repeat(32), rowId = 'row-one'
const source = {
  sheetName: 'Salary', headerRow: 1, columnCount: 5,
  columns: ['Employee name', 'Basic', 'Attendance', 'Month days', 'Net'].map((label, columnIndex) => ({ columnIndex, sourceHeader: label, label, kind: ['employeeName', 'earning', 'info', 'info', 'netPay'][columnIndex] })),
  rows: [{ sourceRow: 2, cells: ['Synthetic person', '1000', '30', '30', '1000'].map(value => ({ value })) }], excludedSourceRows: [],
}
const batch = { id: batchId, clientId: 33, clientName: 'Synthetic metadata client', month: '2026-09', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1, rows: [{ id: rowId, sourceRow: 2, employeeName: 'Synthetic person', employeeCode: 'TEST001', email: '', information: [], earnings: [{ label: 'Basic', amount: 1000 }], deductions: [], employerContributions: [], netPay: 1000, warnings: [] }] }
const basic = { id: '700', key: 'master-basic', code: 'BASIC', name: 'Synthetic Basic', category: 'Earning', componentRole: 'Regular Earning', statutoryType: 'None', calculationType: 'Fixed Amount', value: '0', formula: '', baseComponent: '', proRata: true, taxable: false, priority: '0', payType: 'Variable Pay', isPeriodAdjustment: false, exemptWhenSourceZero: false }
const reviewed = { ...basic, id: '701', key: 'master-reviewed', code: 'REVIEWED_BASIC', name: 'Reviewed Basic', taxable: true, priority: '735', payType: 'Fixed Pay' }
const draft = {
  batchId, rowIds: [rowId], templateName: 'Synthetic stale metadata draft', effectiveFrom: '2026-09-01', payrollDaysColumnIndex: 3, payableDaysColumnIndex: 2,
  sourceColumnRoles: Object.fromEntries(source.columns.map(column => [column.columnIndex, column.kind])), workWeek: 'Sunday off', attendanceCycleStartDay: 1, attendanceCycleEndDay: 30, payrollReportGenerationDay: 3,
  defaultDepartment: '', defaultDesignation: '', defaultGrade: '', employeeDecisions: [{ rowId, allowCreate: false }], editableDefaults: { leave: { status: 'Draft', active: false } },
  components: [{ ...basic, key: 'column-1', existingComponentId: basic.id, taxable: true, priority: 999, payType: 'Fixed Pay', calculationType: 'Formula', formula: '1000', rowFormulas: { [rowId]: '1000' }, proRata: false, expectedColumnIndex: 1 }],
}
const baseContext = { calculationOnlySimulationSupported: true, sharedHeaderDivisorSupported: true, templateApplicabilitySupported: true, source, components: [basic, reviewed], workWeeks: [{ value: 'Sunday off', label: 'Sunday off' }], workLocations: [], employees: [] }
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import Setup from '/src/components/ExcelPayrollSetup.tsx';
function freeze(value){if(value&&typeof value==='object'){Object.freeze(value);Object.values(value).forEach(freeze)}return value}
const batch=freeze(${JSON.stringify(batch)}),originalBatch=JSON.stringify(batch);let captured;
const originalFetch=window.fetch.bind(window);window.fetch=async(...args)=>{const response=await originalFetch(...args);const target=new URL(typeof args[0]==='string'?args[0]:args[0].url,location.href);if(target.pathname==='/api/excel-payroll-setup/context'){const data=freeze(await response.clone().json());captured={data,source:JSON.stringify(data.source),draft:JSON.stringify(data.lastImport?.savedRequest)};response.json=async()=>data}return response};
window.__masterMetadataIntegrity=()=>({batchUnchanged:JSON.stringify(batch)===originalBatch,sourceUnchanged:JSON.stringify(captured?.data.source)===captured?.source,draftUnchanged:JSON.stringify(captured?.data.lastImport?.savedRequest)===captured?.draft,sourceFrozen:Object.isFrozen(captured?.data.source),draftFrozen:captured?.data.lastImport?Object.isFrozen(captured.data.lastImport.savedRequest):null,originalDraft:captured?.data.lastImport?.savedRequest});
ReactDOM.createRoot(document.getElementById('root')).render(<Setup clientId={33} batch={batch} rowIds={['${rowId}']} onClose={()=>{}} onApplied={()=>{throw Error('Unexpected Apply in metadata fixture')}}/>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial;background:#f6f8fc;--hrms-muted:#596579}#root{height:100dvh}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const checks = [], audits = [], failures = [], browser = await chromium.launch({ headless: true })
const sourceConfiguration = component => Object.fromEntries(['key', 'calculationType', 'value', 'formula', 'baseComponent', 'proRata', 'sourceColumnIndex', 'expectedColumnIndex', 'isPeriodAdjustment', 'exemptWhenSourceZero', 'blankInputAsZero', 'rowFormulas'].map(key => [key, component[key]]))

async function scenario(name, restoreDraft, missingMetadata = false) {
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, serviceWorkers: 'block' }), page = await context.newPage()
  const unexpected = [], pageErrors = [], consoleErrors = [], apiRequests = [], previewRequests = [], routeErrors = []
  const payload = structuredClone({ ...baseContext, ...(restoreDraft ? { lastImport: { importId: 'synthetic-draft', requestId: 'synthetic-request', status: 'Draft', savedRequest: draft } } : {}) })
  if (missingMetadata) delete payload.components[0].payType
  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), pathname = url.pathname
    const reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
    try {
      if (pathname.startsWith('/api/')) {
        apiRequests.push({ method: request.method(), path: pathname })
        assert.ok(['localhost', '127.0.0.1'].includes(url.hostname), 'Only intercepted local synthetic API requests are allowed')
        assert.equal(url.searchParams.get('clientId'), '33')
        if (pathname === '/api/excel-payroll-setup/context' && request.method() === 'GET') {
          assert.equal(url.searchParams.get('batchId'), batchId); assert.equal(url.searchParams.get('rowIds'), rowId)
          return reply(payload)
        }
        if (pathname === '/api/excel-payroll-setup/preview' && request.method() === 'POST') {
          const body = request.postDataJSON(); assert.equal(body.batchId, batchId); assert.deepEqual(body.rowIds, [rowId])
          assert.ok(!('source' in body) && !('file' in body), 'The saved source must not be rewritten into a preview request')
          previewRequests.push(body)
          return reply({ planHash: 'synthetic', canApply: false, migrationRequired: false, coverage: 'Synthetic comparison', issues: [], rows: [] })
        }
        unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
      }
      if (url.origin !== origin) { unexpected.push('External ' + request.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
      if (pathname === '/master-metadata-fixture') return route.fulfill({ contentType: 'text/html', body: html })
      if (pathname === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
      if (pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
      if (request.method() === 'GET' && (pathname === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => pathname.startsWith(prefix)))) return route.continue()
      unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
    } catch (error) { routeErrors.push(error.stack || String(error)); return route.abort('blockedbyclient') }
  })
  async function assertMetadata(taxable, priority, payType) {
    const panel = page.getByLabel('Persisted master metadata column-1', { exact: true })
    await expect(panel).toBeVisible(); await expect(panel).toContainText(`Taxable: ${taxable ? 'Yes' : 'No'}`); await expect(panel).toContainText(`Priority: ${priority}`); await expect(panel).toContainText(`Pay type: ${payType}`)
    await expect(panel.locator('input,textarea,select,button,[role="combobox"],[role="checkbox"],[contenteditable="true"]')).toHaveCount(0)
    return panel
  }
  async function preview() {
    const before = previewRequests.length
    await page.locator('.excel-payroll-setup-footer button').filter({ hasText: /^Preview calculations$/ }).click()
    await expect.poll(() => previewRequests.length).toBe(before + 1)
    await expect(page.getByText('Synthetic comparison', { exact: true })).toBeVisible()
    await expect(page.locator('.excel-payroll-setup-footer button').filter({ hasText: /^Apply reviewed Transfer Batch$/ })).toBeDisabled()
    return previewRequests.at(-1).components[0]
  }
  function assertMaster(component, master) {
    assert.equal(component.existingComponentId, master.id); assert.equal(component.code, master.code); assert.equal(component.taxable, master.taxable); assert.equal(component.priority, Number(master.priority)); assert.ok(Number.isInteger(component.priority)); assert.equal(component.payType, master.payType)
    assert.equal(component.isPeriodAdjustment, false, 'Variable Pay metadata must not imply a period adjustment')
  }
  try {
    await page.goto(origin + '/master-metadata-fixture', { waitUntil: 'domcontentloaded' })
    if (missingMetadata) await expect(page.getByText('Persisted shared component settings are unavailable', { exact: true })).toBeVisible()
    else await expect(page.getByRole('button', { name: 'Preview calculations', exact: true })).toBeEnabled()
    if (restoreDraft) await expect(page.getByText('Your saved setup draft has been restored.', { exact: true })).toBeVisible()
    await page.locator('.ant-collapse-header').filter({ has: page.getByText(basic.name, { exact: true }) }).click()
    const panel = await assertMetadata(false, 0, missingMetadata ? 'Unavailable' : 'Variable Pay')
    await panel.scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(output, name + '-desktop.png'), animations: 'disabled' })
    if (missingMetadata) {
      await expect(page.getByRole('button', { name: 'Preview calculations', exact: true })).toBeDisabled()
      await expect(page.getByRole('button', { name: 'Save Transfer Batch draft', exact: true })).toBeDisabled()
      await expect(page.getByRole('button', { name: 'Apply reviewed Transfer Batch', exact: true })).toBeDisabled()
      assert.deepEqual(previewRequests, [])
      checks.push(name + ': missing Pay type displays Unavailable, retains explicit false/0 and blocks preview/draft/Apply without sending a request')
    } else {
    const initial = await preview(); assertMaster(initial, basic)
    if (restoreDraft) { assert.equal(initial.formula, '1000'); assert.equal(initial.proRata, false); assert.deepEqual(initial.rowFormulas, { [rowId]: '1000' }) }
    else { assert.equal(initial.calculationType, 'Fixed Amount'); assert.equal(initial.formula, ''); assert.equal(initial.sourceColumnIndex, 1); assert.equal(initial.proRata, true) }
    checks.push(name + ': read-only persisted BASIC metadata and preview retain taxable=false, priority=0, Variable Pay, isPeriodAdjustment=false')
    if (!restoreDraft) {
      await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Component master BASIC', exact: true }) }).click()
      await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({ hasText: 'REVIEWED_BASIC' }).click()
      await assertMetadata(true, 735, 'Fixed Pay')
      const changed = await preview(); assertMaster(changed, reviewed)
      assert.deepEqual(sourceConfiguration(changed), sourceConfiguration(initial), 'Selecting a reviewed master must preserve source formulas and proration')
      checks.push(name + ': switching to master 701 carries true/735/Fixed Pay while all source formula/proration fields remain unchanged')
    }
    }
    const integrity = await page.evaluate(() => window.__masterMetadataIntegrity())
    assert.equal(integrity.batchUnchanged, true); assert.equal(integrity.sourceUnchanged, true); assert.equal(integrity.draftUnchanged, true); assert.equal(integrity.sourceFrozen, true)
    if (restoreDraft) { assert.equal(integrity.draftFrozen, true); assert.equal(integrity.originalDraft.components[0].taxable, true); assert.equal(integrity.originalDraft.components[0].priority, 999); assert.equal(integrity.originalDraft.components[0].payType, 'Fixed Pay') }
    checks.push(name + ': frozen saved source, batch and stale draft remain unchanged; Apply stays disabled')
    assert.deepEqual(unexpected, []); assert.deepEqual(routeErrors, []); assert.deepEqual(pageErrors, [])
    assert.deepEqual(consoleErrors.filter(message => !/Accessing element\.ref was removed in React 19|\[antd: (?:Drawer|Descriptions)\]|\[antd: Spin\].*tip|\[antd: Select\].*dropdownMatchSelectWidth/.test(message)), [])
    assert.deepEqual(apiRequests, [{ method: 'GET', path: '/api/excel-payroll-setup/context' }, ...previewRequests.map(() => ({ method: 'POST', path: '/api/excel-payroll-setup/preview' }))])
  } catch (error) {
    await page.screenshot({ path: path.join(output, name + '-failure.png'), animations: 'disabled' }).catch(() => {})
    fs.writeFileSync(path.join(output, name + '-failure.txt'), (error.stack || String(error)) + '\n\n' + await page.locator('body').innerText().catch(() => 'Unavailable'))
    throw error
  } finally {
    audits.push({ scenario: name, apiRequests, previewRequests, unexpected, routeErrors, pageErrors, consoleErrors, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const [name, restoreDraft, missingMetadata] of [['initial-and-switch', false, false], ['stale-draft-restore', true, false], ['missing-pay-type', false, true]]) {
    try { await scenario(name, restoreDraft, missingMetadata) } catch (error) { failures.push({ scenario: name, error: error.stack || String(error) }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1
