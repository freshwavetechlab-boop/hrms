import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(path.resolve('tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local test UI only')
const output = path.join(os.tmpdir(), 'frevo-import-flow-' + Date.now()); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-import-flow-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const csv = Buffer.from('Employee Name,Employee Code,Basic,PF,Net Pay,Present days\n' + Array.from({ length: 100 }, (_, i) => `Synthetic employee with a longer source name ${i + 1},${i < 96 ? `SYN-${i + 1}` : ''},10000,1200,8800,26`).join('\n'))
const sha256 = createHash('sha256').update(csv).digest('hex')
const browser = await chromium.launch({ headless: true }), checks = [], errors = [], unexpected = []
let mode = 'configure', batchSaves = 0, contexts = 0, originals = 0, saved = null, codeOptionsUnavailable = false, masterReads = 0, releaseOriginal
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } }), page = await context.newPage()
page.setDefaultTimeout(10000)
page.on('pageerror', error => errors.push(error.message))
await context.route('**/*', route => {
  const req = route.request(), url = new URL(req.url()), p = url.pathname
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/import-flow-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return url.origin === new URL(origin).origin ? route.continue() : route.abort()
  if (p === '/api/auth/me') return reply({ id: 1, displayName: 'Synthetic admin', roles: ['client_admin'], isActive: true, permissions: mode === 'payslips' ? ['payroll.run'] : ['payroll.run', 'settings.manage', 'employees.manage'] })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic client' }])
  if (p.endsWith('/profile')) return reply(null)
  if (p === '/api/excel-payslips/employee-code-options') return codeOptionsUnavailable ? reply({ error: 'Synthetic old API' }, 404) : reply({ clientCode: 'SYN', digits: 5, nextSequence: '41' })
  if (p === '/api/excel-payslips/employee-identity-preview' && req.method() === 'POST') { const {rows}=req.postDataJSON(); return reply({clientId:42,checkedRows:rows.length,rows:rows.map(row=>({sourceRow:row.sourceRow,status:row.employeeCode==='SYN-1'?'Existing':'No match',masterCode:row.employeeCode==='SYN-1'?'SYN-1':'',active:row.employeeCode==='SYN-1',basis:row.employeeCode==='SYN-1'?['Employee Code']:[],reasons:[]}))}) }
  if (p === '/api/excel-payslips/employee-code-preview' && req.method() === 'POST') { const input = req.postDataJSON(); assert.deepEqual(Object.keys(input), ['codes']); return reply({ checkedCodes: input.codes.length, matches: input.codes.includes('SYN-1') ? [{ code: 'SYN-1', records: 1, activeRecords: 1 }] : [] }) }
  if (p === '/api/excel-payslips/employee-master-comparison' && req.method() === 'GET') { masterReads++; assert.notEqual(mode, 'payslips'); assert.equal(url.searchParams.get('clientId'), '42'); assert.equal(url.searchParams.get('employeeCode'), 'SYN-1'); return reply({ clientId: 42, employeeCode: 'SYN-1', status: 'Matched', fields: { EmployeeCode: 'SYN-1', EmployeeName: 'Previous synthetic Master name', Email: 'old@example.invalid' } }) }
  if (p === '/api/excel-payslips/templates') return reply([1, 2, 3].map(i => ({ id: String(i), name: `Synthetic earlier salary setup / ${i}`, components: [{ id: '400', code: 'BASIC', name: 'Basic', category: 'Earning' }] })))
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  if (saved && p === `/api/excel-payslips/batches/${saved.id}` && req.method() === 'GET') return reply(saved)
  if (p.endsWith('/originals') && req.method() === 'POST') { originals++; assert.equal(url.searchParams.get('clientId'), '42'); const send=()=>reply({ id: 'b'.repeat(32), fileName: 'Synthetic.csv', sha256 }); return originals===1?new Promise(resolve=>{releaseOriginal=()=>resolve(send())}):send() }
  if (p === '/api/excel-payslips/batches' && req.method() === 'POST') {
    batchSaves++; assert.equal(url.searchParams.get('clientId'), '42')
    if (mode === 'failed') return reply({ error: 'Synthetic batch save failed; inspect before retrying.' }, 503)
    const input = req.postDataJSON(); assert.equal(input.rows.length, 100); assert.equal(input.month, '2026-09')
    if (batchSaves === 1) {
      assert.deepEqual(input.employeeCodeGeneration, { enabled: true, digits: 3 })
      assert.equal(input.rows[0].employeeCode, 'SYN-1')
      assert.equal(input.rows.filter(row => !row.employeeCode).length, 4, 'The client submits blank rows, not guessed Master identities or prematurely allocated codes')
    }
    saved = { ...input, id: 'a'.repeat(32), clientId: 42, clientName: 'Synthetic client', createdAtUtc: new Date().toISOString() }
    return reply(saved)
  }
  if (p.endsWith('/context')) {
    contexts++; assert.ok(saved); assert.equal(url.searchParams.get('batchId'), saved.id); assert.equal(url.searchParams.get('clientId'), '42')
    return reply({ roster: { rows: saved.rows.map((row, i) => ({ rowId: row.id, sourceRow: row.sourceRow, employeeName: row.employeeName, employeeCode: row.employeeCode, employeeId: i + 1, status: 'Existing', message: 'Synthetic verified match' })), missingEmployees: [] }, components: [], employees: [], workLocations: [], workWeeks: [] })
  }
  if (req.method() !== 'GET') { unexpected.push(p); return reply({}, 403) }
  return reply([])
})
async function openImport() {
  // Each source-save scenario starts with fresh UI preferences in this isolated,
  // fully mocked context; no real browser/session or authentication is touched.
  if (page.url().startsWith(origin)) await page.evaluate(() => sessionStorage.clear())
  await page.goto(origin + '/import-flow-fixture')
  await page.getByRole('combobox', { name: 'Excel payslips client', exact: true }).click()
  await page.locator('.ant-select-dropdown').getByText('Synthetic client', { exact: true }).click()
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({ name: 'Synthetic.csv', mimeType: 'text/csv', buffer: csv })
  await page.getByRole('textbox', { name: 'Payslip salary month', exact: true }).fill('2026-09')
  await expect(page.getByRole('button', { name: 'Save batch (100)', exact: true })).toBeEnabled()
}
try {
  await openImport()
  const importDrawer = page.locator('.excel-payslip-import')
  await expect(page.locator('[aria-label="Excel data destinations"]')).toContainText('Salary setup')
  await expect(page.getByTestId('payslip-next-code')).toHaveValue('SYN00041')
  await expect(page.locator('[aria-label="Payslip employee code generation"]')).toContainText('4 blank source codes')
  await page.getByRole('spinbutton', { name: 'payslip sequence digits', exact: true }).fill('3')
  await expect(page.getByTestId('payslip-next-code')).toHaveValue('SYN041')
  await expect(page.getByLabel('payslip client prefix')).toHaveAttribute('readonly', '')
  const options = importDrawer.locator('.excel-payslip-import-options')
  const salaryTemplate = page.getByRole('combobox', { name: 'Payslip salary template', exact: true })
  await expect(salaryTemplate).toBeHidden()
  await options.locator('summary').click()
  await expect(options).toContainText('Use uploaded Excel (recommended)')
  await page.locator('.ant-select').filter({ has: salaryTemplate }).click()
  await page.locator('.ant-select-dropdown').getByText('Synthetic earlier salary setup / 2', { exact: true }).click()
  await expect(options.locator('summary')).toContainText('Existing template: Synthetic earlier salary setup / 2')
  assert.equal(batchSaves, 0); assert.equal(contexts, 0)
  await page.locator('.ant-select').filter({ has: salaryTemplate }).click()
  await page.locator('.ant-select-dropdown').getByText('Use uploaded Excel (recommended)', { exact: true }).click()
  await expect(options.locator('summary')).not.toContainText('Existing template:')
  await expect(page.getByRole('checkbox', { name: 'Compact payslip PDF', exact: true })).not.toBeChecked()
  await options.locator('summary').click()
  await expect(page.getByRole('button', { name: 'Save batch (100)', exact: true })).toBeEnabled()
  checks.push('Existing templates/PDF layout are optional and collapsed; uploaded Excel remains default; selection alone creates no assignments or source saves')
  checks.push('Shared Employee generator shows client prefix, chosen padding and reserved next counter without filling source blanks or changing existing Excel codes')
  assert.equal(masterReads, 0)
  const employees = page.getByRole('region', { name: 'Employee import preview' })
  await expect(employees.locator('tbody tr[data-row-key]').first()).toHaveClass(/excel-code-existing-row/)
  await employees.locator('tbody tr[data-row-key]').first().getByRole('button', { name: 'Details', exact: true }).click()
  const details = page.locator('.excel-payslip-row-review')
  await expect(details.getByRole('region', { name: 'Master versus Excel comparison' })).toContainText('Previous synthetic Master name')
  await expect(details.getByText('Earnings', { exact: true })).toBeVisible()
  assert.equal(masterReads, 1); assert.equal(originals, 0); assert.equal(batchSaves, 0)
  await details.locator('.ant-drawer-footer').getByRole('button', { name: 'Close', exact: true }).click()
  checks.push('Details on an existing red row lazily reads the permitted Master, shows Old/New beside unchanged source financial details, and performs no save')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await expect(page.getByRole('button', { name: 'Save & configure masters', exact: true })).toBeInViewport()
    await expect.poll(() => importDrawer.evaluate(el => { const rect = el.getBoundingClientRect(); return rect.left >= -1 && rect.right <= innerWidth + 1 })).toBe(true)
    const codeCard = page.locator('.excel-payslip-code-options')
    await codeCard.scrollIntoViewIfNeeded()
    assert.ok(await codeCard.evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'Code generator must fit within the import at each viewport')
    await codeCard.screenshot({ path: path.join(output, `employee-codes-${width}.png`) })
    await importDrawer.locator('.ant-drawer-body').evaluate(el => { el.scrollTop = el.scrollHeight })
    await expect(page.getByRole('button', { name: 'Save & configure masters', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `import-${width}.png`), fullPage: true })
  }
  checks.push('100-row import shows the four destinations and keeps Save/master-review actions visible at desktop/tablet/mobile widths')
  console.log('Responsive import checks passed')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.getByRole('button', { name: 'Save & configure masters', exact: true }).click()
  await expect(page.locator('.excel-payslip-save-progress')).toContainText('Saving original Excel file 1 of 1')
  await expect(importDrawer.locator('.ant-drawer-footer button').filter({hasText:'Save & configure masters'})).toBeDisabled()
  releaseOriginal()
  await expect(page.getByRole('button', { name: 'Existing (100)', exact: true })).toBeVisible()
  assert.equal(batchSaves, 1); assert.equal(contexts, 1); assert.equal(originals, 1)
  await page.locator('.excel-payroll-setup .ant-drawer-footer').getByRole('button', { name: 'Close', exact: true }).click()
  await expect(page.getByRole('button', { name: 'PDF all', exact: true })).toBeEnabled()
  checks.push('One source save retains the original, keeps payslips available and opens exact saved rows in master review; no Apply or payroll is called')
  console.log('Source save to master-review handoff passed')
  mode = 'failed'; await openImport()
  await page.getByRole('button', { name: 'Save & configure masters', exact: true }).click()
  await expect(page.locator('.excel-payslip-import .ant-alert').filter({hasText:'Synthetic batch save failed'})).toBeVisible()
  await expect(page.locator('.app-toast-notification.error').filter({hasText:'Synthetic batch save failed'})).toBeVisible()
  await expect(page.locator('.excel-payslip-save-progress')).toHaveCount(0)
  assert.equal(contexts, 1); assert.equal(batchSaves, 2)
  checks.push('Failed source save leaves the import open and never opens or applies master setup')
  mode = 'payslips'; await openImport()
  await expect(page.getByRole('button', { name: 'Save & configure masters', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Save batch (100)', exact: true }).click()
  await expect(page.getByRole('button', { name: 'PDF all', exact: true })).toBeEnabled()
  assert.equal(contexts, 1)
  checks.push('Payslip-only permissions retain normal source save/PDF access and cannot open the master shortcut')
  mode = 'configure'; await openImport()
  await expect(page.getByTestId('payslip-next-code')).toHaveValue('SYN00041')
  await page.getByRole('spinbutton', { name: 'payslip sequence digits', exact: true }).fill('6')
  codeOptionsUnavailable = true
  const savesBefore = batchSaves, originalsBefore = originals, contextsBefore = contexts
  await page.getByRole('button', { name: 'Save & configure masters', exact: true }).click()
  await expect(page.locator('.excel-payslip-import .ant-alert').filter({hasText:'The running API cannot confirm the selected employee-code format.'})).toBeVisible()
  await expect(page.locator('.app-toast-notification.error').filter({hasText:'The running API cannot confirm the selected employee-code format.'})).toBeVisible()
  assert.equal(batchSaves, savesBefore); assert.equal(originals, originalsBefore); assert.equal(contexts, contextsBefore)
  checks.push('A selected code format requires current API support before original upload/source save, preventing an older runtime from silently ignoring padding')
  assert.deepEqual(unexpected, []); assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, mockedBatchSaves: batchSaves, actualDatabaseWrites: 0, actualPayrollRuns: 0, actualEmails: 0, unexpected, errors }, null, 2))
  console.log(JSON.stringify({ checks, output, actualDatabaseWrites: 0 }, null, 2))
} catch (error) {
  const layout = await page.locator('.excel-payslip-import').evaluate(el => {
    const nodes = [el, el.parentElement, el.parentElement?.parentElement, el.querySelector('.ant-drawer-body'), el.querySelector('.ant-drawer-footer'), el.querySelector('.excel-payslip-footer')].filter(Boolean)
    return { viewport: innerWidth, nodes: nodes.map(node => { const rect = node.getBoundingClientRect(), css = getComputedStyle(node); return { className: node.className, x: rect.x, width: rect.width, right: rect.right, inline: node.getAttribute('style'), minWidth: css.minWidth, maxWidth: css.maxWidth, flexWrap: css.flexWrap } }) }
  }).catch(() => null)
  fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: error.message, errors, unexpected, batchSaves, contexts, layout }, null, 2))
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  throw error
} finally { await context.close(); await browser.close(); fs.rmSync(fixturePath, { force: true }) }
