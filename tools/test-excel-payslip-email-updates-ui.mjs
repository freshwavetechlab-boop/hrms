import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5174'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(os.tmpdir(), 'frevo-excel-email-updates-ui'); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-email-updates-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const batchId = '0123456789abcdef0123456789abcdef'
let batch = { id: batchId, clientId: 42, clientName: 'Synthetic', month: '2026-09', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1, simpleLayout: true,
  createdAtUtc: '2026-10-23T01:00:00Z', createdBy: 'test', rows: Array.from({ length: 48 }, (_, i) => ({
    id: 'row-' + i, sourceRow: i + 2, employeeCode: 'TEST' + String(i === 1 ? 0 : i).padStart(3, '0'), employeeName: i < 2 ? 'Same Name' : 'Person ' + i,
    email: i >= 45 ? `sent${i}@example.test` : '', information: [{ label: 'Work location', value: i < 2 ? 'Copied location' : 'Site ' + i }], earnings: [{ label: 'Wages', amount: 1000.12345678 }], deductions: [{ label: 'PF', amount: 120 }], employerContributions: [], netPay: 880.12345678, declaredGross: null, declaredDeductions: null, warnings: []
  })) }
const original = structuredClone(batch), statuses = { items: batch.rows.map((r, i) => ({ rowId: r.id, status: i >= 45 ? 'Sent' : 'Pending', email: r.email, message: i >= 45 ? 'Accepted earlier' : 'Not yet sent', canSend: i < 45, sentCopyStatus: i >= 45 ? 'Saved' : '', recipientDeliveries: [] })) }
let readOnly = false, failSave = true, forbidden = 0
const submissions = [], errors = [], checks = []
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' }), context = await browser.newContext({ viewport: { width: 1366, height: 768 }, acceptDownloads: true }), page = await context.newPage()
page.on('pageerror', e => errors.push(e.message))
await context.route('**/*', async route => {
  const q = route.request(), u = new URL(q.url()), p = u.pathname, reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (!['localhost', '127.0.0.1'].includes(u.hostname)) return route.abort()
  if (p === '/email-updates-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (q.method() === 'POST' && p.endsWith('/email-updates')) {
    const body = q.postDataJSON(); submissions.push(body)
    assert.equal(u.searchParams.get('clientId'), '42'); assert.equal(body.month, '2026-09'); assert.equal(body.updates.length, 2)
    assert.deepEqual(body.updates.map(r => r.rowId), ['row-0', 'row-1']); assert.equal(body.updates[0].employeeCode, 'TEST000')
    assert.ok(body.updates.every(r => !('netPay' in r))); assert.equal(batch.id, original.id)
    if (failSave) { failSave = false; return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Synthetic save unavailable; retry safely.' }) }) }
    batch = structuredClone(batch); for (const update of body.updates) batch.rows.find(r => r.id === update.rowId).email = update.email
    return reply(batch)
  }
  if (q.method() !== 'GET') { forbidden++; return route.fulfill({ status: 403, body: '{}' }) }
  if (p === '/api/auth/me') return reply({ id: 1, clientId: 42, displayName: 'Synthetic', roles: ['client_admin'], permissions: readOnly ? ['reports.read'] : ['payroll.run'], isActive: true })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic' }])
  if (p.endsWith('/batches')) return reply([{ ...batch, rowCount: batch.rows.length, rows: undefined }])
  if (p.endsWith('/batches/' + batchId)) return reply(batch)
  if (p.endsWith('/delivery-status')) return reply(statuses)
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})
const drawer = page.locator('.excel-payslip-email-updates')
async function downloadReport() {
  const downloading = page.waitForEvent('download'); await page.getByText('Export Excel', { exact: true }).click()
  const download = await downloading, dest = path.join(output, 'export-' + crypto.randomUUID() + '.xlsx'); await download.saveAs(dest)
  return await page.evaluate(async bytes => {
    const { parseExcelPayslipFile } = await import('/src/utils/excelPayslipImport.ts'); return (await parseExcelPayslipFile(new File([new Uint8Array(bytes)], 'export.xlsx')))[0]
  }, [...fs.readFileSync(dest)])
}
async function uploadRows(rows, input = drawer.locator('input[type=file]')) {
  const bytes = await page.evaluate(async rows => { const { buildXlsxBlob } = await import('/src/utils/xlsx.ts'); return [...new Uint8Array(await buildXlsxBlob([{ name: 'Records', rows }]).arrayBuffer())] }, rows)
  await input.setInputFiles({ name: 'contacts.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: Buffer.from(bytes) })
}
async function openImport() { await page.getByRole('button', { name: 'More payslip actions' }).click(); await page.getByRole('menuitem', { name: 'Import email updates' }).click() }
try {
  await page.goto(origin + '/email-updates-fixture?clientId=42&batchId=' + batchId)
  await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Table email status' }) }).click(); await page.locator('.ant-select-item-option').filter({ hasText: 'Not sent (45)' }).click()
  const exported = await downloadReport(); assert.equal(exported.rows.length, 46)
  let rows = exported.rows.map(r => r.cells.map(c => c.value)), emailColumn = rows[0].indexOf('Email')
  assert.equal(rows[0][3], 'Employee code'); assert.equal(rows[1][3], 'TEST000'); assert.equal(rows[1][1], 'row-0')
  checks.push('Not-sent filter exports 45 actual rows with employee codes, location and stable batch/payslip IDs')
  const table = page.locator('.excel-payslips > .ant-smart-table'); await table.locator('.ant-table-tbody tr[data-row-key]').first().getByRole('checkbox').check()
  assert.equal((await downloadReport()).rows.length, 2); await page.getByRole('button', { name: 'Clear selection (1)' }).click()
  checks.push('An explicit selection exports only the selected filtered rows')
  rows[1][emailColumn] = 'first@example.test'; rows[2][emailColumn] = 'second@example.test'
  await openImport(); await uploadRows([...rows, [...rows[1]]]); await expect(drawer.getByText(/duplicate payslip row/)).toBeVisible(); await expect(drawer.getByRole('button', { name: 'Save email updates (0)' })).toBeDisabled(); assert.equal(submissions.length, 0)
  await uploadRows(rows); await expect(drawer.getByText('43 blank skipped', { exact: true })).toBeVisible()
  await drawer.getByRole('button', { name: 'Cancel', exact: true }).click()
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await uploadRows(rows, page.locator('.excel-payslip-import input[type=file]'))
  await expect(page.locator('.excel-payslip-import')).toBeHidden()
  checks.push('Normal Import spreadsheet detects the exported contact report and opens email-update preview instead of creating a salary batch')
  const save = drawer.getByRole('button', { name: 'Save email updates (2)' }); await expect(save).toBeEnabled()
  await page.screenshot({ path: path.join(output, 'desktop.png'), fullPage: false })
  for (const [name, width, height] of [['tablet', 768, 1024], ['mobile', 390, 844]]) {
    await page.setViewportSize({ width, height }); await expect(save).toBeInViewport({ ratio: 1 })
    await expect.poll(() => drawer.evaluate(el => { const rect = el.getBoundingClientRect(); return rect.left >= -0.5 && rect.right <= innerWidth + 0.5 })).toBe(true)
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: false, animations: 'disabled' })
  }
  await save.click(); await expect(drawer.getByText('Synthetic save unavailable; retry safely.')).toBeVisible(); await expect(save).toBeEnabled(); await save.click(); await expect(drawer).toHaveCount(0)
  assert.equal(submissions.length, 2); assert.deepEqual(submissions[0], submissions[1]); assert.equal(batch.rows.length, 48)
  const restored = structuredClone(batch); restored.rows[0].email = ''; restored.rows[1].email = ''; assert.deepEqual(restored, original)
  assert.equal(statuses.items.filter(r => r.status === 'Sent').length, 3); assert.equal(statuses.items[45].email, original.rows[45].email)
  checks.push('Blank cells skip, same-code/same-location transfer rows match distinct IDs, duplicate rows block; a failed save retains preview and retries safely')
  await page.reload(); await page.setViewportSize({ width: 1366, height: 768 }); const reopened = await downloadReport()
  assert.equal(reopened.rows[1].cells[emailColumn].value, 'first@example.test'); assert.equal(reopened.rows[2].cells[emailColumn].value, 'second@example.test')
  checks.push('Reload retains updated emails in the same 48-slip batch; salary and three sent deliveries are unchanged')
  readOnly = true; await page.reload(); await page.getByRole('button', { name: 'More payslip actions' }).click(); await expect(page.getByRole('menuitem', { name: 'Import email updates' })).toHaveCount(0)
  assert.equal(forbidden, 0); assert.deepEqual(errors, [])
  const proof = { checks, forbidden, errors, realDatabaseWrites: 0, realEmails: 0, output }; console.log(JSON.stringify(proof, null, 2)); fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(proof, null, 2))
} catch (error) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); console.log((await page.locator('body').innerText()).slice(-2000)); throw error }
finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
