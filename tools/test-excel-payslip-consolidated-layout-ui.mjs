import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const root = fileURLToPath(new URL('../', import.meta.url)), runtime = JSON.parse(fs.readFileSync(process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json', 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'; assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname))
const output = 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/consolidated-source-layout'; fs.mkdirSync(output, { recursive: true })
const filename = `.codex-consolidated-layout-${crypto.randomUUID()}.tsx`, fixturePath = path.join(root, 'payroll-ui', filename)
const batchId = 'a'.repeat(32), previousId = 'b'.repeat(32)
const rows = Array.from({ length: 830 }, (_, index) => { const sourceRow = index + 6; return { id: 'source-' + sourceRow, sourceRow, employeeCode: 'SYN' + String(sourceRow).padStart(5, '0'), employeeName: 'Synthetic employee ' + sourceRow, email: 'synthetic@example.invalid', information: [{ label: 'Number of Working Days Attended', value: '30' }], earnings: [{ label: 'Basic', amount: 15414 }], deductions: [{ label: 'Employee contribution', amount: 1965.28 }], employerContributions: [{ label: 'Employer contribution', amount: 2003.82 }], netPay: 13448.72, warnings: [], employment: { employeeId: sourceRow + 1000, employeeCode: 'SYN' + String(sourceRow).padStart(5, '0'), status: 'Active', identityReviewRequired: false, blockedForMonth: false } } })
const batch = { id: batchId, clientId: 33, clientName: 'Synthetic PLRS', month: '2026-09', sourceFileName: 'PLRS September 2026 consolidated working calculation.xlsx', sheetName: 'Salary calculation', headerRow: 5, createdAtUtc: '2026-10-28T00:00:00Z', createdBy: 'Synthetic reviewer', rows, consolidatedSourceBatchIds: [previousId] }
const summary = { ...batch, rows: undefined, rowCount: rows.length }
const consolidation = { canonicalBatchId: batchId, sourceBatchIds: [previousId], groupId: 'synthetic-history', active: true, canUndo: true, token: 'synthetic-token', canonicalRows: rows.length, createdAtUtc: '2026-10-28T00:00:00Z', warnings: [], history: [], historicalDelivery: rows.map(row => ({ rowId: row.id, sourceRowId: 'old-' + row.id, sourceBatchId: previousId, fromConsolidatedVersion: true, status: 'Sent', mode: 'Individual', sentAtUtc: '2026-10-01T12:00:00Z', message: 'Synthetic retained original delivery' })) }
fs.writeFileSync(fixturePath, `import React from'react';import ReactDOM from'react-dom/client';import{BrowserRouter}from'react-router-dom';import AuthGate from'/src/components/AuthGate.tsx';import ToastProvider from'/src/components/ToastProvider.tsx';import Excel from'/src/components/ExcelPayslips.tsx';import Header from'/src/components/layout/AppPageHeader.tsx';import'/src/AppShell.css';import'/src/OrganizationSetup.css';import'/src/RecruitmentExperience.css';import'/src/RecruitmentViewport.css';ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><div className="fixture-shell-topbar">FrevOne synthetic shell</div><div className="fixture-workspace"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main className="hrms-page-body module-content"><Excel/></main></div></div></AuthGate></ToastProvider></BrowserRouter>);`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-width:0;min-height:0;display:flex;flex-direction:column}.fixture-shell-topbar{flex:0 0 52px;display:flex;align-items:center;padding-inline:16px;background:#fff}.fixture-workspace{display:flex;flex-direction:column;flex:1;min-height:0;min-width:0}.fixture-workspace>main{display:flex!important;flex-direction:column;flex:1;min-height:0;min-width:0;overflow:hidden;padding:8px!important;box-sizing:border-box}@media(min-width:1000px){.fixture-workspace{margin-left:220px}}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from'/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${filename}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const browser = await chromium.launch({ headless: true }), checks = [], audits = [], failures = []
try {
  for (const viewport of [{ width: 1280, height: 720 }, { width: 790, height: 759 }]) {
    const id = `${viewport.width}x${viewport.height}`, context = await browser.newContext({ viewport, serviceWorkers: 'block' }), page = await context.newPage(), unexpected = [], errors = [], apiRequests = []
    await context.routeWebSocket('**/*', socket => socket.close()); page.on('pageerror', error => errors.push(error.message))
    await context.route('**/*', route => {
      const req = route.request(), url = new URL(req.url()), p = url.pathname, reply = body => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
      if (p.startsWith('/api/')) {
        apiRequests.push(req.method() + ' ' + p)
        if (req.method() !== 'GET') { unexpected.push('Mutation blocked ' + req.method() + ' ' + p); return route.abort('blockedbyclient') }
        if (p === '/api/auth/me') return reply({ id: 1, clientId: null, displayName: 'Synthetic reviewer', roles: ['super_admin'], permissions: ['payroll.run', 'employees.manage', 'settings.manage'], isActive: true })
        if (p === '/api/excel-payslips/clients') return reply([{ id: 33, name: 'Synthetic PLRS', hasSavedBatch: true }])
        if (p === '/api/excel-payslips/templates') return reply([])
        if (p === '/api/excel-payslips/batches') return reply([summary])
        if (p === '/api/excel-payslips/batches/' + batchId) return reply(batch)
        if (p === '/api/excel-payslips/batches/' + batchId + '/consolidation') return reply(consolidation)
        if (p === '/api/excel-payslips/batches/' + batchId + '/delivery-status') return reply({ items: rows.map(row => ({ rowId: row.id, status: 'Sent', email: row.email, canSend: false, message: 'Synthetic current delivery evidence', sentCopyStatus: 'Saved' })) })
        if (p === '/api/excel-payslips/batches/' + batchId + '/mail-jobs') return reply({ items: [] })
        unexpected.push(req.method() + ' ' + p); return route.abort('blockedbyclient')
      }
      if (url.origin === 'https://fonts.googleapis.com') return route.fulfill({ contentType: 'text/css', body: '' })
      if (url.origin !== origin) { unexpected.push(req.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
      if (p === '/reports/payroll-reports') return route.fulfill({ contentType: 'text/html', body: html })
      if (p === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
      if (p === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
      if (req.method() === 'GET' && (p === '/' + filename || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => p.startsWith(prefix)))) return route.continue()
      unexpected.push(req.method() + ' ' + p); return route.abort('blockedbyclient')
    })
    try {
      await page.goto(origin + '/reports/payroll-reports?report=excel-payslips&clientId=33&batchId=' + batchId, { waitUntil: 'domcontentloaded' })
      const workspace = page.getByRole('region', { name: 'Excel payslips workspace', exact: true }), table = workspace.locator(':scope > .ant-smart-table'), body = table.locator('.ant-table-body')
      await expect(workspace.getByText(/Consolidated working batch.*830 rows/)).toBeVisible({ timeout: 15000 }); await expect(table.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(10)
      assert.ok(await body.evaluate(element => element.clientHeight >= 96), 'Primary source body must retain a usable row area')
      const geometry = await table.evaluate(element => ({ toolbarHeight: element.querySelector('.ant-table-toolbar').getBoundingClientRect().height, bodyHeight: element.querySelector('.ant-table-body').clientHeight }))
      fs.writeFileSync(path.join(output, id + '-geometry.json'), JSON.stringify(geometry, null, 2)); checks.push(id + ': consolidated830-row source keeps visible body instead of collapsing beneath history/toolbar')
      const search = table.getByPlaceholder('Search table...'), targets = [62, 63, 64, 65, 66, 67, 68, 90, 99, 100, 101, 102, 103, 104, 105]
      for (const [index, sourceRow] of targets.entries()) {
        await search.fill('SYN' + String(sourceRow).padStart(5, '0'))
        const checkbox = table.locator(`.ant-table-tbody tr[data-row-key="source-${sourceRow}"] input[type=checkbox]`)
        await checkbox.scrollIntoViewIfNeeded()
        const geometry = await checkbox.evaluate(input => { const box = input.getBoundingClientRect(), hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2), owner = input.closest('.ant-checkbox'); return { height: box.height, topmost: hit === input || owner?.contains(hit) } })
        assert.ok(geometry.topmost && geometry.height > 0, 'Checkbox must receive a real pointer hit: ' + JSON.stringify(geometry))
        await checkbox.check(); await expect(checkbox).toBeChecked(); await expect(page.getByRole('button', { name: 'Configure payroll', exact: true })).toHaveText(`Configure payroll (${index + 1})`)
      }
      checks.push(id + ':15individualrowsselectthroughnormalclicks/searchandremainselectedacrossrerenders')
      await search.fill(''); await page.getByRole('button', { name: 'Refresh email status', exact: true }).scrollIntoViewIfNeeded(); await page.getByRole('button', { name: 'Refresh email status', exact: true }).click(); await expect(page.getByRole('button', { name: 'Configure payroll', exact: true })).toHaveText('Configure payroll (15)')
      await table.locator('.ant-pagination-options .ant-select-selector').click(); await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').getByText('100 / page', { exact: true }).click(); await expect(table.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(100)
      assert.ok(await body.evaluate(element => element.clientHeight >= 96)); await expect(page.getByRole('button', { name: 'Configure payroll', exact: true })).toHaveText('Configure payroll (15)')
      await workspace.getByText('Retained source history (1 versions · 830 delivery records)', { exact: true }).click(); await expect(workspace.getByText('The saved formulas remain unchanged. Earlier versions retain their original mail and payroll evidence; prior-version mail is shown separately and is not a new send from this batch.', { exact: true }).first()).toBeVisible(); assert.ok(await body.evaluate(element => element.clientHeight >= 96))
      await workspace.getByText('Retained source history (1 versions · 830 delivery records)', { exact: true }).click(); await search.scrollIntoViewIfNeeded()
      await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' }); checks.push(id + ':history/selectiontoolsretained,100rowpageandemailstatusrefreshkeep15selectionandusablebody')
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)); assert.deepEqual(unexpected, []); assert.deepEqual(errors, [])
    } catch (error) { await page.screenshot({ path: path.join(output, id + '-failure.png') }).catch(() => {}); fs.writeFileSync(path.join(output, id + '-body.txt'), await page.locator('body').innerText()); fs.writeFileSync(path.join(output, id + '-body.html'), await page.locator('body').innerHTML()); failures.push({ scenario: id, error: error.stack }) }
    finally { audits.push({ scenario: id, apiRequests, unexpected, errors, realApiCallsForwarded: 0, realBusinessWrites: 0 }); await context.close() }
    if (failures.length) break
  }
} finally { await browser.close(); fs.unlinkSync(fixturePath); fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2)); console.log(JSON.stringify({ scenarios: audits.length, checks: checks.length, failures, output, realApiCallsForwarded: 0, realBusinessWrites: 0 })) }
if (failures.length) process.exitCode = 1
