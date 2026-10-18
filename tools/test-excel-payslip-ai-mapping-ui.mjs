import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(os.tmpdir(), 'frevo-excel-ai-mapping-ui'); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-ai-mapping-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const headers = ['Employee Name', 'Basic', 'Project reward', 'PF', 'TDS', 'Net Pay', 'Email', 'UAN']
const csv = Buffer.from(headers.join(',') + '\nSynthetic Private Person,30000,2000,3600,1000,27400,private@example.test,101555921202')
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const context = await browser.newContext({ viewport: { width: 1366, height: 900 } }), page = await context.newPage()
const errors = [], checks = []; let mode = 'ai', mutations = 0, suggestions = 0
page.on('pageerror', error => errors.push(error.message))
await context.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  const reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/ai-mapping-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (p.endsWith('/suggest-mapping') && request.method() === 'POST') {
    suggestions++; assert.equal(url.searchParams.get('clientId'), '42')
    const evidence = request.postDataJSON(), raw = request.postData()
    for (const secret of ['Synthetic Private Person', '30000', '27400', 'private@example.test', '101555921202']) assert.ok(!raw.includes(secret), 'AI request leaked ' + secret)
    assert.equal(evidence.columns.length, headers.length); assert.equal(evidence.columns[2].kind, 'ignore')
    const requestMode = mode
    await new Promise(resolve => setTimeout(resolve, requestMode === 'slow' ? 1800 : 500))
    if (requestMode === 'failed') return route.fulfill({ status: 503, body: '{}' })
    try { return await reply({ columns: evidence.columns.map((c, i) => ({ ...c, kind: i === 2 ? 'earning' : c.kind })), method: requestMode === 'rules' ? 'rules' : 'ai', model: 'Synthetic model', knowledgeVersion: 'excel-payroll-mapping-v1', references: [{ source: 'curated', title: 'Verified patterns' }], reviewColumnIndexes: [2], message: 'AI suggestions applied for review. Amounts and formulas are unchanged.' }) } catch { return }
  }
  if (request.method() !== 'GET') { mutations++; return route.fulfill({ status: 403, body: '{}' }) }
  if (p === '/api/auth/me') return reply({ id: 1, clientId: 42, displayName: 'Synthetic Admin', roles: ['client_admin'], permissions: ['payroll.run'], isActive: true })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic Client' }])
  if (p.endsWith('/profile')) return reply(null)
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})
async function openImport() {
  await page.goto(origin + '/ai-mapping-fixture')
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({ name: 'Synthetic.csv', mimeType: 'text/csv', buffer: csv })
  await page.getByRole('textbox', { name: 'Payslip salary month', exact: true }).fill('2026-09')
  await expect(page.getByRole('button', { name: 'Use suggested mapping', exact: true })).toBeEnabled()
}
try {
  await openImport()
  await page.getByRole('button', { name: 'Use suggested mapping', exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toBeDisabled()
  await expect(page.getByText(/AI suggestions applied for review.*Synthetic model/)).toBeVisible()
  await expect(page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Column C use as', exact: true }) })).toContainText('Earning')
  await expect(page.getByRole('textbox', { name: 'Column C label', exact: true })).toHaveValue('Project reward')
  await expect(page.getByRole('checkbox', { name: 'Column C blanks as zero', exact: true })).not.toBeChecked()
  await expect(page.getByRole('button', { name: 'Save batch (1)', exact: true })).toBeEnabled()
  await page.screenshot({ path: path.join(output, 'review-ai-mapping.png'), fullPage: true })
  checks.push('AI classifies an unfamiliar earning, flags it for review, keeps labels/source amounts and does not approve blank-as-zero')
  for (const fallback of ['rules', 'failed']) {
    mode = fallback
    await page.getByRole('textbox', { name: 'Column B label', exact: true }).fill('Operator reviewed pay')
    await page.getByRole('button', { name: 'Use suggested mapping', exact: true }).click()
    await expect(page.getByText(/AI.*temporarily unavailable.*current mapping is unchanged/)).toBeVisible()
    await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toHaveValue('Operator reviewed pay')
  }
  checks.push('Provider fallback and network failure preserve the current reviewed mapping')
  mode = 'slow'; await page.getByRole('button', { name: 'Use suggested mapping', exact: true }).click()
  await page.locator('.excel-payslip-import').getByRole('button', { name: 'Cancel', exact: true }).click()
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({ name: 'Synthetic.csv', mimeType: 'text/csv', buffer: csv })
  await expect(page.getByRole('button', { name: 'Use suggested mapping', exact: true })).toBeEnabled()
  await page.getByRole('textbox', { name: 'Column B label', exact: true }).fill('Mapping after close')
  await page.waitForTimeout(2100)
  await expect(page.getByRole('textbox', { name: 'Column B label', exact: true })).toHaveValue('Mapping after close')
  checks.push('Closing and reopening the drawer cancels stale AI suggestions')
  assert.equal(mutations, 0); assert.equal(suggestions, 4); assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, suggestions, mutations, errors }, null, 2))
  console.log(JSON.stringify({ checks, suggestions, mutations, output }, null, 2))
} finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
