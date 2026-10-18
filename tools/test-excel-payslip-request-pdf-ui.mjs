import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(os.tmpdir(), 'frevo-excel-request-pdf-ui'); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-request-pdf-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const batch = { id: '0123456789abcdef0123456789abcdef', clientId: 42, clientName: 'Synthetic client', month: '2026-09', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1, createdAtUtc: '2026-10-17T12:00:00Z', createdBy: 'test', rows: [
  { id: 'row-1', sourceRow: 2, employeeName: 'Missing One', employeeCode: 'TEST001', email: '', information: [], earnings: [{ label: 'Basic', amount: 10000 }], deductions: [], employerContributions: [], netPay: 10000, declaredGross: null, declaredDeductions: null, warnings: ['Review source amounts'] },
  { id: 'row-2', sourceRow: 3, employeeName: 'Known Email', employeeCode: 'TEST002', email: 'known@example.test', information: [], earnings: [{ label: 'Basic', amount: 20000 }], deductions: [], employerContributions: [], netPay: 20000, declaredGross: null, declaredDeductions: null, warnings: [] },
  { id: 'row-3', sourceRow: 4, employeeName: 'Missing Two', employeeCode: 'TEST003', email: 'invalid', information: [], earnings: [{ label: 'Basic', amount: 15000 }], deductions: [], employerContributions: [], netPay: 15000, declaredGross: null, declaredDeductions: null, warnings: [] }
] }
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const context = await browser.newContext({ viewport: { width: 1366, height: 900 } }), page = await context.newPage()
const errors = [], checks = [], submissions = []; let unauthorizedMutations = 0, failFirst = false
page.on('pageerror', error => errors.push(error.message))
await context.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  const reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/request-pdf-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (/\/email-request(?:-pdf)?$/.test(p) && request.method() === 'POST') {
    const body = request.postDataJSON(); submissions.push({ path: p, body })
    assert.equal(url.searchParams.get('clientId'), '42'); assert.deepEqual(body.rowIds, ['row-1', 'row-3'])
    assert.equal(body.email, 'payroll@example.test')
    assert.equal(p.endsWith('/email-request-pdf'), body.attachPayslips)
    if (failFirst) { failFirst = false; return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Synthetic temporary outage' }) }) }
    return reply({ queueId: 123, email: body.email, employeeCount: 2, status: 'Queued', includesPayslips: body.attachPayslips })
  }
  if (request.method() !== 'GET') { unauthorizedMutations++; return route.fulfill({ status: 403, body: '{}' }) }
  if (p === '/api/auth/me') return reply({ id: 1, clientId: 42, displayName: 'Synthetic Admin', roles: ['client_admin'], permissions: ['payroll.run'], isActive: true })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic Client' }])
  if (p.endsWith('/batches')) return reply([{ ...batch, rows: undefined, rowCount: 3 }])
  if (p.endsWith('/batches/' + batch.id)) return reply(batch)
  if (p.endsWith('/delivery-status')) return reply({ items: batch.rows.map(row => ({ rowId: row.id, email: row.email, status: 'Pending', message: '', canSend: true })) })
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})
const drawer = page.locator('.excel-payslip-action')
async function openRequest() {
  await page.goto(`${origin}/request-pdf-fixture?clientId=42&batchId=${batch.id}`)
  await page.getByRole('button', { name: 'Email batch (3)', exact: true }).click()
  await drawer.getByRole('textbox', { name: 'Combined payslip recipient', exact: true }).fill('payroll@example.test')
  await drawer.getByRole('checkbox', { name: 'Request missing email IDs', exact: true }).check()
}
try {
  await openRequest()
  const attach = drawer.getByRole('checkbox', { name: 'Attach their salary slips as one combined PDF', exact: true })
  await expect(attach).not.toBeChecked(); await expect(drawer.getByText(/No salary PDF is attached/)).toBeVisible()
  await drawer.getByRole('button', { name: 'Request email IDs', exact: true }).click()
  assert.equal(submissions[0].body.attachPayslips, false)
  checks.push('Default OFF submits an information-only request for the two missing/invalid-email employees')
  await openRequest(); await attach.check()
  // Ant's loading icon can remain in the accessibility name during its leave animation.
  const sendPdf = drawer.locator('button').filter({ hasText: 'Request IDs & send combined PDF' })
  await expect(sendPdf).toBeDisabled()
  await drawer.getByRole('checkbox', { name: 'I reviewed these warnings and want to use the imported amounts.', exact: true }).check()
  await expect(sendPdf).toBeEnabled()
  await page.screenshot({ path: path.join(output, 'desktop-opt-in.png'), fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await attach.scrollIntoViewIfNeeded(); await expect(attach).toBeVisible(); await expect(sendPdf).toBeVisible()
  await page.screenshot({ path: path.join(output, 'mobile-opt-in.png'), fullPage: true })
  failFirst = true; await sendPdf.click()
  await expect(drawer.getByText(/Synthetic temporary outage/)).toBeVisible()
  await expect(attach).toBeDisabled()
  await expect(drawer.getByRole('checkbox', { name: 'Include company seal', exact: true })).toBeDisabled()
  await sendPdf.click()
  await expect(page.getByText(/Their salary slips are included as one combined PDF/)).toBeVisible()
  assert.deepEqual(submissions[1], submissions[2], 'Retry must preserve request ID, recipients, attachment and formatting options')
  assert.equal(submissions[1].body.includeSeal, false); assert.equal(submissions[1].body.amountDecimalPlaces, 0); assert.equal(submissions[1].body.acknowledgeWarnings, true)
  checks.push('Opt-in attaches only missing-email payslips, requires warning acknowledgement and keeps retry metadata stable')
  await openRequest(); await expect(attach).not.toBeChecked()
  checks.push('A new send action resets the salary PDF attachment checkbox to OFF; desktop/mobile actions remain reachable')
  assert.equal(unauthorizedMutations, 0); assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, simulatedRequests: submissions.length, realSmtpSends: 0, unauthorizedMutations, errors }, null, 2))
  console.log(JSON.stringify({ checks, simulatedRequests: submissions.length, realSmtpSends: 0, output }, null, 2))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  console.log(JSON.stringify({ submissions, url: page.url(), buttons: await drawer.getByRole('button').allTextContents(), body: (await page.locator('body').innerText()).slice(-1800), errors }))
  throw error
} finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
