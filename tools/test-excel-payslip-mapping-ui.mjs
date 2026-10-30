import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const modulePath = process.env.HRMS_PLAYWRIGHT_MODULE
if (!modulePath) throw Error('Set HRMS_PLAYWRIGHT_MODULE to the installed Playwright test module.')
const { chromium, expect } = await import(pathToFileURL(modulePath).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only.')
const output = path.join(os.tmpdir(), 'frevo-excel-mapping-ui-20261017')
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-mapping-${crypto.randomUUID()}.tsx`
const fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const headers = ['Employee Name', 'Basic', 'PF', 'TDS', 'Net Pay', 'BASIC+ DA=TOTAL']
const csv = Buffer.from(headers.join(',') + '\nSynthetic Person,30000,3600,1000,25400,30000')
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
const page = await context.newPage(), errors = [], checks = []
let mode = 'failed', writes = 0, requests = 0, profileStarted = 0, mappingSaves = 0
page.on('pageerror', error => errors.push(error.message))
await context.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  const reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/mapping-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (request.method() === 'PUT' && p.endsWith('/profile') && mode === 'legacy-null') {
    const payload = request.postDataJSON(); mappingSaves++
    assert.equal(payload.columns.length, 6); assert.equal(payload.columns[5].kind, 'ignore')
    assert.equal(payload.columns[1].componentId, 'salary-basic')
    assert.ok(payload.columns.every(column => !('componentId' in column) || typeof column.componentId === 'string'))
    return reply(payload)
  }
  if (request.method() !== 'GET') { writes++; return route.fulfill({ status: 403, body: '{}' }) }
  if (p === '/api/auth/me') return reply({ id: 1, clientId: 42, displayName: 'Synthetic Admin', roles: ['client_admin'], permissions: ['payroll.run'], isActive: true })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic Client' }])
  if (p.endsWith('/profile')) {
    requests++
    profileStarted = Date.now()
    if (mode === 'failed') return route.abort('failed')
    if (mode === 'slow') { await new Promise(resolve => setTimeout(resolve, 10000)); try { return await reply(null) } catch { return } }
    if (mode === 'saved' || mode === 'legacy-null') return reply({ headerSignature: url.searchParams.get('headerSignature'), columns: headers.map((sourceHeader, columnIndex) => ({ sourceHeader, columnIndex, kind: ['employeeName', 'earning', 'deduction', 'deduction', 'netPay', mode === 'legacy-null' ? 'grossTotal' : 'ignore'][columnIndex], label: columnIndex === 1 ? 'Reviewed Basic' : sourceHeader, ...(mode === 'legacy-null' ? { componentId: columnIndex === 1 ? 'salary-basic' : null } : {}) })) })
    return reply(null)
  }
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})
async function openImport() {
  await page.goto(origin + '/mapping-fixture')
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({ name: 'Synthetic.csv', mimeType: 'text/csv', buffer: csv })
  await page.getByRole('textbox', { name: 'Payslip salary month', exact: true }).fill('2026-09')
}
try {
  await openImport()
  await expect(page.getByText(/Saved mapping is temporarily unavailable/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save batch (1)', exact: true })).toBeEnabled()
  await expect(page.getByRole('combobox', { name: 'Column D use as', exact: true })).toHaveAttribute('aria-label', 'Column D use as')
  await page.screenshot({ path: path.join(output, 'failed-profile.png'), fullPage: true })
  checks.push('Unavailable saved preset falls back to reviewed suggestions without blocking import')
  mode = 'slow'; await openImport()
  await expect(page.getByText(/Saved mapping is temporarily unavailable/)).toBeVisible({ timeout: 9500 })
  assert.ok(Date.now() - profileStarted < 9500, 'Profile timeout must remain active with a cancellation signal')
  await expect(page.getByRole('button', { name: 'Save batch (1)', exact: true })).toBeEnabled()
  checks.push('Optional profile times out after eight seconds even with caller cancellation enabled')
  mode = 'saved'; await openImport()
  await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toHaveValue('Reviewed Basic')
  await expect(page.getByText('Saved mapping restored for these column headings.', { exact: true })).toBeVisible()
  checks.push('Valid saved mapping continues to take priority over suggestions')
  mode = 'none'; await openImport()
  await expect(page.getByRole('button', { name: 'Save batch (1)', exact: true })).toBeEnabled()
  await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toHaveValue('Basic')
  await expect(page.getByText(/Saved mapping is temporarily unavailable/)).toHaveCount(0)
  checks.push('A new client without a preset uses suggested mapping normally')
  mode = 'legacy-null'; await openImport()
  await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toHaveValue('Reviewed Basic')
  const total = page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Column F use as', exact: true }) })
  await total.click(); await page.locator('.ant-select-item-option').filter({ hasText: /^Ignore$/ }).click()
  await page.getByRole('button', { name: 'Save mapping', exact: true }).click()
  await expect(page.getByText('Mapping saved for this client and these column headings.', { exact: true })).toBeVisible()
  assert.equal(mappingSaves, 1)
  checks.push('Legacy componentId:null columns save after BASIC+ DA=TOTAL is changed to Ignore; assigned component IDs are retained and no salary batch is written')
  assert.equal(writes, 0); assert.equal(requests, 5); assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, requests, writes, mappingSaves, errors, realDatabaseWrites: 0, realEmails: 0 }, null, 2))
  console.log(JSON.stringify({ checks, writes, output }, null, 2))
} finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
