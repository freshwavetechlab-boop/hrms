import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

// Real saved-batch component with synthetic HTTP responses only. This creates a
// separate test browser and never reads the user's authenticated browser profile.
const repo = fileURLToPath(new URL('../', import.meta.url))
const runtimeFile = process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json'
const runtime = JSON.parse(fs.readFileSync(runtimeFile, 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname))
const output = process.env.HRMS_BATCH_ATTENDANCE_RESULTS || path.join(os.tmpdir(), 'frevo-batch-attendance-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-batch-attendance-${crypto.randomUUID()}.tsx`, fixturePath = path.join(repo, 'payroll-ui', fixtureName)
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Synthetic saved payslip attendance" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-height:0;min-width:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;min-height:0;min-width:0;display:flex;flex-direction:column;overflow:hidden;padding:8px;box-sizing:border-box}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const information = pairs => pairs.map(([label, value]) => ({ label, value }))
const sourceCases = [
  [['Present Days', '26'], ['Payable Days', '30']],
  [['Present Days', '0'], ['Payable Days', '0']],
  [['Working Days', '27']],
  [['Present Days', ''], ['Payable Days', '']],
  [],
  [['Attendance', '30']],
  [['Present Days', '26'], ['Present', '27'], ['Payable Days', '30']],
  [['Present Days', '-1'], ['Payable Days', '30']],
  [['Month Days', '26'], ['Present Days', '26'], ['Payable Days', '30']],
  [['Present Days', '29'], ['Payable Days', '29']],
  [['Present Days', '25'], ['Payable Days', '26']],
  [['Present Days', '24'], ['Payable Days', '25']],
]
const makeBatch = (id, month, prefix) => ({ id, clientId: 33, clientName: 'Synthetic Client', month, sourceFileName: `Synthetic-${month}.xlsx`, sheetName: 'Salary', headerRow: 1, createdAtUtc: `${month}-01T00:00:00Z`, createdBy: 'Synthetic reviewer', rows: sourceCases.map((pairs, i) => ({ id: prefix + '-row-' + (i + 1), sourceRow: i + 6, employeeName: 'Synthetic employee ' + String(i + 1).padStart(2, '0'), employeeCode: 'SYN' + String(i + 1).padStart(3, '0'), email: '', information: information(pairs), earnings: [{ label: 'Wages', amount: 10000 }], deductions: [{ label: 'PF', amount: 1200 }], employerContributions: [{ label: 'Employer PF', amount: 1300 }], netPay: 8800, warnings: [], employment: { employeeId: i + 1, employeeCode: 'SYN' + String(i + 1).padStart(3, '0'), status: 'Active', lastWorkingDate: null, blockedForMonth: false, identityReviewRequired: false } })) })
const batches = [makeBatch('a'.repeat(32), '2026-09', 'sep'), makeBatch('b'.repeat(32), '2024-02', 'feb'), makeBatch('c'.repeat(32), '2026-10', 'oct')]
const user = { id: 1, clientId: 33, displayName: 'Synthetic reader', roles: ['client_admin'], permissions: ['payroll.view'], isActive: true }
const checks = [], audits = [], failures = []
const browser = await chromium.launch({ headless: true })

function exportedRows(filePath) {
  const buffer = fs.readFileSync(filePath), files = new Map(); let offset = 0
  while (offset + 30 <= buffer.length && buffer.readUInt32LE(offset) === 0x04034b50) {
    assert.equal(buffer.readUInt16LE(offset + 8), 0, 'Repository XLSX writer uses stored ZIP entries')
    const size = buffer.readUInt32LE(offset + 18), nameLength = buffer.readUInt16LE(offset + 26), extraLength = buffer.readUInt16LE(offset + 28)
    const name = buffer.subarray(offset + 30, offset + 30 + nameLength).toString('utf8'), start = offset + 30 + nameLength + extraLength
    files.set(name, buffer.subarray(start, start + size).toString('utf8')); offset = start + size
  }
  assert.ok(files.has('xl/workbook.xml'), 'Download must be an actual XLSX workbook')
  const decode = value => value.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&')
  return [...files.get('xl/worksheets/sheet1.xml').matchAll(/<row\b[^>]*>([\s\S]*?)<\/row>/g)].map(match => [...match[1].matchAll(/<c\b[^>]*>([\s\S]*?)<\/c>/g)].map(cell => decode(cell[1].match(/<t[^>]*>([\s\S]*?)<\/t>/)?.[1] ?? cell[1].match(/<v>([\s\S]*?)<\/v>/)?.[1] ?? '')))
}

async function scenario(viewport) {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block', acceptDownloads: true }), page = await context.newPage()
  const requests = [], unexpected = [], pageErrors = [], consoleErrors = []
  page.on('pageerror', error => pageErrors.push(error.message)); page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), pathname = url.pathname
    const reply = body => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
    if (pathname.startsWith('/api/')) {
      requests.push({ method: request.method(), path: pathname })
      if (request.method() !== 'GET') { unexpected.push('Blocked mutation ' + request.method() + ' ' + pathname); return route.abort('blockedbyclient') }
      if (pathname === '/api/auth/me') return reply(user)
      if (pathname === '/api/excel-payslips/clients') return reply([{ id: 33, name: 'Synthetic Client' }])
      if (pathname === '/api/excel-payslips/batches') { assert.equal(url.searchParams.get('clientId'), '33'); return reply(batches.map(({ rows, ...batch }) => ({ ...batch, rowCount: rows.length }))) }
      const matched = pathname.match(/^\/api\/excel-payslips\/batches\/([a-f0-9]{32})(?:\/(delivery-status|mail-jobs|consolidation))?$/)
      if (matched) {
        assert.equal(url.searchParams.get('clientId'), '33'); const batch = batches.find(item => item.id === matched[1]); assert.ok(batch)
        if (!matched[2]) return reply(batch)
        if (matched[2] === 'delivery-status') return reply({ items: batch.rows.map(row => ({ rowId: row.id, status: 'Pending', email: '', canSend: true, message: 'Synthetic source row' })) })
        if (matched[2] === 'mail-jobs') return reply({ items: [] })
        if (matched[2] === 'consolidation') return reply(null)
      }
      unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
    }
    if (url.origin !== origin) { unexpected.push('External ' + request.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
    if (pathname === '/batch-attendance-fixture') return route.fulfill({ contentType: 'text/html', body: html })
    if (pathname === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
    if (pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
    if (request.method() === 'GET' && (pathname === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => pathname.startsWith(prefix)))) return route.continue()
    unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
  })
  const id = `${viewport.width}x${viewport.height}`
  try {
    await page.goto(origin + '/batch-attendance-fixture?clientId=33&batchId=' + batches[0].id, { waitUntil: 'domcontentloaded' })
    const workspace = page.getByRole('region', { name: 'Excel payslips workspace', exact: true })
    await expect(workspace.locator('.excel-payslip-batch-identity strong')).toHaveText('2026-09')
    await expect(workspace.locator('tr[data-row-key="sep-row-1"]')).toBeVisible()
    const headerIndex = async label => workspace.locator('.ant-table-thead tr').first().locator('th').evaluateAll((headers, title) => headers.findIndex(header => header.textContent.trim() === title), label)
    const rowCell = async (rowId, label) => {
      const index = await headerIndex(label); assert.ok(index >= 0, 'Missing column ' + label)
      return workspace.locator(`tr[data-row-key="${rowId}"]`).locator('td').nth(index)
    }
    await expect(await rowCell('sep-row-1', 'Month days (calendar)')).toHaveText('30')
    await expect(await rowCell('sep-row-1', 'Present days')).toContainText('26')
    await expect(await rowCell('sep-row-1', 'Payable days')).toContainText('30')
    checks.push(id + ': September calendar30 with explicitpresent26/payable30 remains distinct')
    await expect(await rowCell('sep-row-2', 'Present days')).toHaveText('0'); await expect(await rowCell('sep-row-2', 'Payable days')).toHaveText('0')
    checks.push(id + ': explicitzero attendance stays0 rather than missing/default30')
    await expect(await rowCell('sep-row-3', 'Present days')).not.toContainText('27'); await expect(await rowCell('sep-row-3', 'Other source days')).toContainText('Working Days'); await expect(await rowCell('sep-row-3', 'Other source days')).toContainText('27')
    await expect(await rowCell('sep-row-6', 'Present days')).not.toContainText('30'); await expect(await rowCell('sep-row-6', 'Other source days')).toContainText('Attendance')
    checks.push(id + ': WorkingDays/Attendance stay source-labelled without inferredpresent')
    await expect(await rowCell('sep-row-4', 'Present days')).toContainText(/review|missing|not supplied|not mapped|blank|—/i)
    await expect(await rowCell('sep-row-5', 'Payable days')).toContainText(/review|missing|not supplied|not mapped|—/i)
    checks.push(id + ': blank and absentdayvalues remain actionable unknowns')
    await expect(await rowCell('sep-row-7', 'Present days')).toContainText('Review required')
    await expect(await rowCell('sep-row-8', 'Present days')).toContainText('Review required')
    checks.push(id + ': conflictingandinvalidpresentvaluesremainreviewablewithoutguessedcorrection')
    const search = workspace.getByPlaceholder('Search table...', { exact: true })
    await search.fill('SYN001'); await expect(workspace.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(1)
    const downloadEvent = page.waitForEvent('download'); await workspace.getByRole('button', { name: 'Export Excel', exact: false }).click(); const download = await downloadEvent
    const downloadPath = path.join(output, id + '-filtered-attendance.xlsx'); await download.saveAs(downloadPath)
    const exported = exportedRows(downloadPath), headers = exported[0]; assert.equal(exported.length, 2, 'Export follows the one-row tablefilter')
    for (const [label, expected] of [['Month days (calendar)', '30'], ['Present days (source)', '26'], ['Payable days (source)', '30']]) { const index = headers.indexOf(label); assert.ok(index >= 0, 'XLSX header ' + label); assert.equal(exported[1][index], expected) }
    checks.push(id + ': realxlsxdownload retainsfilteredattendancevalues withoutproductionrequests')
    await search.fill('')
    const tableBody = workspace.locator('.ant-table-body'); assert.ok(await tableBody.evaluate(element => element.scrollWidth > element.clientWidth), 'Wide table scrolls internally')
    await tableBody.evaluate(element => { element.scrollLeft = 1000 })
    assert.ok(await tableBody.evaluate(element => element.scrollLeft > 0)); assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
    await page.screenshot({ path: path.join(output, id + '-table.png'), animations: 'disabled' })
    checks.push(id + ': attendancecolumnsare reachable throughinternalhorizontal scrollwithoutpageoverflow')
    await page.getByText('Cards', { exact: true }).click()
    const card = workspace.locator('.shared-record-cards article').filter({ hasText: 'Synthetic employee 01' })
    await expect(card).toContainText('Month days (calendar)'); await expect(card).toContainText('Present days'); await expect(card).toContainText('Payable days')
    await expect(card.locator('dl > div').filter({ has: page.locator('dt', { hasText: 'Present days' }) }).locator('dd')).toContainText('26')
    await page.screenshot({ path: path.join(output, id + '-cards.png'), animations: 'disabled' })
    checks.push(id + ': cards expose sameexplicitattendanceas table')
    await page.getByText('Table', { exact: true }).click()
    for (const [batchIndex, expectedDays, prefix] of [[1, '29', 'feb'], [2, '31', 'oct']]) {
      await page.getByRole('button', { name: 'Batch history', exact: true }).click()
      const history = page.getByRole('dialog').filter({ hasText: 'Saved Excel payslip batches' })
      await history.locator('tr[data-row-key]').filter({ hasText: batches[batchIndex].sourceFileName }).getByRole('button', { name: 'Open batch', exact: true }).click()
      await expect(workspace.locator('.excel-payslip-batch-identity strong')).toHaveText(batches[batchIndex].month)
      await expect(await rowCell(prefix + '-row-1', 'Month days (calendar)')).toHaveText(expectedDays)
      await expect(await rowCell(prefix + '-row-1', 'Present days')).toContainText('26')
      checks.push(id + ': savedbatchnavigation' + batches[batchIndex].month + 'usescalendar' + expectedDays + 'withoutstaleSeptemberdays')
    }
  } catch (error) {
    await page.screenshot({ path: path.join(output, id + '-failure.png'), animations: 'disabled' }).catch(() => {})
    fs.writeFileSync(path.join(output, id + '-failure.txt'), await page.locator('body').innerText().catch(() => 'Unavailable'))
    throw error
  } finally {
    assert.deepEqual(unexpected, [], 'All unknown API/application traffic is blocked')
    assert.deepEqual(pageErrors, [])
    const actualConsoleErrors = consoleErrors.filter(message => !/Accessing element\.ref was removed in React 19|\[antd: (?:Select|Drawer|Spin)\]/.test(message))
    assert.deepEqual(actualConsoleErrors, [])
    audits.push({ scenario: id, interceptedApiCalls: requests.length, requests, pageErrors, consoleErrors, unexpected, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const viewport of [{ width: 1366, height: 768 }, { width: 390, height: 844 }]) {
    if (process.env.HRMS_BATCH_ATTENDANCE_SCENARIO && `${viewport.width}x${viewport.height}` !== process.env.HRMS_BATCH_ATTENDANCE_SCENARIO) continue
    try { await scenario(viewport) } catch (error) { failures.push({ viewport, error: error.stack }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1
