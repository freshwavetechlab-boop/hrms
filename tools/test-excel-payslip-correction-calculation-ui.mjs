import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5174'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local fixture only')
const repairMode = process.env.HRMS_TEST_REPAIR_OLD === '1'
const output = path.join(os.tmpdir(), 'frevo-excel-correction-ui' + (repairMode ? '-repair' : '')); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-correction-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const originalId = '0123456789abcdef0123456789abcdef', newId = '1123456789abcdef0123456789abcdef'
const rows = Array.from({ length: 171 }, (_, i) => ({ id: 'row-' + i, sourceRow: i + 2, employeeCode: 'TEST' + i, employeeName: i === 0 ? 'Aman Jain' : i < 4 ? 'Aman ' + i : 'Person ' + i,
  email: `test${i}@example.test`, information: [], earnings: [{ label: 'Wages', amount: i ? 1000 : 25856.2044 }, { label: 'Allowance', amount: i ? 0 : -1200 }], deductions: [{ label: 'Deduction', amount: i ? 100 : 2286.248 }], employerContributions: [], netPay: i ? 900 : 22369.9564, declaredGross: null, declaredDeductions: null, warnings: i ? [] : ['Negative allowance'] }))
const original = { id: originalId, clientId: 42, clientName: 'Synthetic', month: '2026-09', sourceFileName: 'Salary.xlsx', sheetName: 'Salary', headerRow: 1, simpleLayout: true, createdAtUtc: '2026-10-23T01:00:00Z', createdBy: 'test', rows }
const headers = ['Employee', 'Employee code', 'Wages', 'Allowance', 'Deduction', 'In Hand Salary', 'Email'], kinds = ['employeeName', 'employeeCode', 'earning', 'earning', 'deduction', 'netPay', 'email']
const source = { sheetName: 'Salary', headerRow: 1, columnCount: 7, excludedSourceRows: [], columns: headers.map((label, columnIndex) => ({ label, sourceHeader: label, columnIndex, kind: kinds[columnIndex] })), rows: [
  { sourceRow: 1, cells: headers.map(value => ({ value })) }, ...rows.map((r, i) => ({ sourceRow: r.sourceRow, cells: [r.employeeName, r.employeeCode, r.earnings[0].amount, r.earnings[1].amount, r.deductions[0].amount, r.netPay, r.email].map((value, j) => ({ value: String(value), ...(j === 5 ? { formulaText: `C${r.sourceRow}+D${r.sourceRow}-E${r.sourceRow}` } : i === 0 && j === 3 ? { formulaText: '0-1200' } : {}) })) }))] }
if (repairMode) { rows[0].earnings[1].amount = 0; source.rows[1].cells[3].value = '0'; source.rows[1].cells[3].calculationError = 'This formula amount was manually corrected for this batch. Re-import a reviewed salary formula before creating another month.' }
const before = structuredClone({ original, source }), errors = [], checks = []; let forbidden = 0, saved, previewRequests = 0, releasePreview
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' }), context = await browser.newContext({ viewport: { width: 1366, height: 900 } }), page = await context.newPage()
page.on('pageerror', e => errors.push(e.message))
await context.route('**/*', async route => {
  const q = route.request(), u = new URL(q.url()), p = u.pathname, reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (!['localhost', '127.0.0.1'].includes(u.hostname)) return route.abort()
  if (p === '/correction-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (q.method() === 'POST' && p.endsWith('/correction-preview')) {
    previewRequests++; const request = q.postDataJSON(); assert.equal(request.batch.rows.length, 171); assert.deepEqual(request.edits, [{ sourceRow: 2, columnIndex: 3, amount: 0 }])
    await new Promise(resolve => { releasePreview = resolve }); const result = structuredClone(source); result.rows[1].cells[3].value = '0'; result.rows[1].cells[3].calculationError = 'Manually corrected for this month'; result.rows[1].cells[5].value = '23569.9564'; return reply(result)
  }
  if (q.method() === 'POST' && p.endsWith('/batches')) {
    const request = q.postDataJSON(); assert.equal(request.rows.length, 171); assert.equal(request.rows[0].netPay, 23569.9564); assert.equal(request.rows[0].earnings[1].amount, 0)
    assert.deepEqual(request.rows.slice(1).map(r => r.netPay), original.rows.slice(1).map(r => r.netPay)); assert.equal(request.calculationSource.rows[1].cells[5].formulaText, 'C2+D2-E2')
    saved = { ...request, id: newId, clientId: 42, clientName: 'Synthetic', createdAtUtc: '2026-10-23T02:00:00Z', createdBy: 'test' }; return reply(saved)
  }
  if (q.method() !== 'GET') { forbidden++; return route.fulfill({ status: 403, body: '{}' }) }
  if (p === '/api/auth/me') return reply({ id: 1, clientId: 42, displayName: 'Synthetic', roles: ['client_admin'], permissions: ['payroll.run'], isActive: true })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic' }])
  if (p.endsWith('/batches')) return reply((saved ? [saved, original] : [original]).map(b => ({ ...b, rows: undefined, rowCount: b.rows.length })))
  if (p.endsWith('/batches/' + originalId)) return reply(original)
  if (p.endsWith('/batches/' + newId)) return reply(saved)
  if (p.endsWith('/calculation')) return reply(source)
  if (p.endsWith('/delivery-status')) return reply({ items: (p.includes(newId) ? saved.rows : rows).map(r => ({ rowId: r.id, email: r.email, status: 'Pending', canSend: true, message: '' })) })
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})
try {
  await page.goto(origin + '/correction-fixture?clientId=42&batchId=' + originalId)
  await page.getByRole('button', { name: 'Select with email', exact: true }).click(); await expect(page.getByRole('button', { name: 'Email selected (171)', exact: true })).toBeEnabled()
  const table = page.locator('.excel-payslips > .ant-smart-table'), search = table.getByPlaceholder('Search table...')
  await search.fill('Aman'); await expect(table.getByText('4 of 171 rows', { exact: true })).toBeVisible()
  const check = table.locator('.ant-table-tbody tr[data-row-key]').first().getByRole('checkbox'); await check.uncheck()
  await expect(page.getByRole('button', { name: 'Email selected (170)', exact: true })).toBeEnabled(); await check.check(); await expect(page.getByRole('button', { name: 'Email selected (171)', exact: true })).toBeEnabled(); await check.uncheck()
  await search.fill(''); await expect(page.getByRole('button', { name: 'Email selected (170)', exact: true })).toBeEnabled(); checks.push('171 selected, search narrows to 4, uncheck one gives 170; recheck and clear search retain all other selections')
  await search.fill('Aman Jain'); await table.getByRole('button', { name: 'Details', exact: true }).click(); await page.locator('.excel-payslip-row-review').getByRole('button', { name: 'Correct amounts', exact: true }).click()
  const editor = page.locator('.excel-payslip-amount-editor'), save = editor.getByRole('button', { name: /Save corrected batch/ })
  await expect(editor.getByRole('checkbox', { name: 'Recalculate linked Excel formulas' })).toBeChecked()
  if (repairMode) await editor.getByRole('button', { name: 'Recalculate saved corrections', exact: true }).click()
  else await editor.getByRole('spinbutton', { name: 'Correct Allowance (D)', exact: true }).fill('0')
  await expect(save).toBeDisabled()
  await expect.poll(() => typeof releasePreview).toBe('function'); releasePreview()
  await expect(editor.getByRole('spinbutton', { name: 'Correct In Hand Salary (F)', exact: true })).toHaveValue('23569.9564'); await expect(save).toBeEnabled()
  await page.screenshot({ path: path.join(output, 'desktop.png'), animations: 'disabled' }); await page.setViewportSize({ width: 390, height: 844 }); await expect(save).toBeInViewport({ ratio: 1 }); await page.screenshot({ path: path.join(output, 'mobile.png'), animations: 'disabled' })
  await save.click(); await expect(editor).toHaveCount(0); assert.equal(previewRequests, 1); assert.ok(saved); assert.deepEqual({ original, source }, before)
  checks.push('Default formula recalculation updates In Hand 22,370 to 23,570; saving is blocked until preview completes and preserves the full 171-slip batch and source formulas')
  await page.reload(); await expect(page.getByRole('button', { name: 'Close batch', exact: true })).toBeVisible(); assert.equal(forbidden, 0); assert.deepEqual(errors, [])
  const result = { checks, errors, forbidden, realDatabaseWrites: 0, realEmails: 0, output }; fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result, null, 2))
} catch (error) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); console.log((await page.locator('body').innerText()).slice(-2000)); throw error }
finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
