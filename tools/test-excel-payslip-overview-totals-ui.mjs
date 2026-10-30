import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

// Focused, synthetic-only browser verification of the real overview component.
// Only static Vite assets may reach localhost. Every API request is fulfilled here
// or blocked, and no existing browser profile/session is read or reused.
const repo = fileURLToPath(new URL('../', import.meta.url))
const runtimeFile = process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json'
const runtime = JSON.parse(fs.readFileSync(runtimeFile, 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname))
const output = process.env.HRMS_OVERVIEW_TOTALS_RESULTS || path.join(os.tmpdir(), 'frevo-overview-totals-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-overview-totals-${crypto.randomUUID()}.tsx`
const fixturePath = path.join(repo, 'payroll-ui', fixtureName)
const batchId = 'a'.repeat(32)
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';
import Overview from '/src/components/ExcelPayslipOverview.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
function Fixture(){const[open,setOpen]=React.useState(false);return <main style={{padding:12,minWidth:0}}><button onClick={()=>setOpen(true)}>Open source totals overview</button><Overview open={open} canConfigure={false} onClose={()=>setOpen(false)} onOpen={()=>{throw Error('Unexpected employee/setup navigation')}}/></main>};ReactDOM.createRoot(document.getElementById('root')).render(<React.StrictMode><Fixture/></React.StrictMode>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/><style>body{margin:0;font-family:Arial}#root{min-width:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const catalog = [{ clientId: 33, batchId, clientName: 'Synthetic source client', month: '2026-09', fileName: 'Synthetic-830.xlsx', sheetName: 'Salary', rowCount: 830, createdAtUtc: '2026-10-28T00:00:00Z', hasCalculationSource: true }]
const allRows = Array.from({ length: 830 }, (_, i) => ({ ...catalog[0], key: 'synthetic-' + i, sourceRow: i + 6, employeeCode: 'SYN' + String(i + 1).padStart(4, '0'), employeeName: 'Synthetic person ' + (i + 1), masterEmployeeId: i + 1, masterStatus: 'Matched', masterReason: 'Synthetic scoped match', readiness: i < 400 ? 'Ready' : 'Review', reasons: ['Synthetic source check'], scopeOverlap: false, setupDecision: 'Not previewed' }))
const employeeCatalog = Array.from({ length: 8 }, (_, index) => ({ clientId: index < 4 ? 33 : 44, batchId: String(index + 1).repeat(32), clientName: 'Synthetic client ' + (index < 4 ? 'Alpha' : 'Beta'), month: '2026-09', fileName: `Synthetic-scope-${index + 1}.xlsx`, sheetName: 'Salary', rowCount: 9, createdAtUtc: '2026-10-28T00:00:00Z', hasCalculationSource: true }))
const employeeRows = employeeCatalog.flatMap((batch, batchIndex) => Array.from({ length: 9 }, (_, localIndex) => {
  const index = batchIndex * 9 + localIndex
  return { ...batch, key: 'employee-fixture-' + index, sourceRow: localIndex + 6, employeeCode: 'EMP' + String(index + 1).padStart(3, '0'), employeeName: 'Synthetic employee ' + (index + 1), masterEmployeeId: index + 1, masterStatus: 'Matched', masterReason: 'Synthetic scoped match', readiness: 'Ready', reasons: [], scopeOverlap: false, setupDecision: 'Not previewed' }
}))
for (const [pair, indices] of [[0, [1, 10]], [1, [20, 29]], [2, [38, 47]]]) for (const index of indices) Object.assign(employeeRows[index], { employeeCode: 'OVERLAP-' + pair, employeeName: 'Synthetic overlap employee ' + pair, masterEmployeeId: 1000 + pair, readiness: 'Review', reasons: ['Multiple selected source rows cover this client/period identity.'], scopeOverlap: true })
Object.assign(employeeRows[5], { employeeCode: '', readiness: 'Review', reasons: ['No source employee code; current master match does not create a code group.'] })
Object.assign(employeeRows[15], { employeeCode: '', masterEmployeeId: null, masterStatus: 'Missing code', readiness: 'Review', reasons: ['Missing source employee code.'] })
for (const index of [22, 34, 61]) Object.assign(employeeRows[index], { masterEmployeeId: null, masterStatus: 'Conflict', readiness: 'Review', reasons: ['Conflicting source identity requires review.'] })
const overlapKeys = employeeRows.filter(row => row.scopeOverlap).map(row => row.key).sort()
const checks = [], audits = [], failures = []
const browser = await chromium.launch({ headless: true })
const money = (sourceNetPay, sourceGrossEarnings, sourceDeductions) => ({ sourceNetPay, sourceGrossEarnings, sourceDeductions, monetaryRoundingBasis: 'Each saved payslip total rounded to two decimals using its PDF display semantics before summing.' })

async function scenario(viewport, mode = 'full') {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block' }), page = await context.newPage()
  const requests = [], unexpected = [], pageErrors = [], consoleErrors = []
  const currentCatalog = mode === 'employee-overlap' ? employeeCatalog : catalog
  let failNext = false, catalogCalls = 0, releaseHeldCatalog = null
  const failedRequests = [], staleCatalog = [{ ...catalog[0], clientId: 77, fileName: 'Stale-cancelled-catalog.xlsx', clientName: 'Stale cancelled client' }]
  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  page.on('requestfailed', request => failedRequests.push({ path: new URL(request.url()).pathname, error: request.failure()?.errorText || '' }))
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), pathname = url.pathname
    const reply = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (pathname.startsWith('/api/')) {
      requests.push({ method: request.method(), path: pathname })
      if (pathname === '/api/excel-payslips/overview-scopes' && request.method() === 'GET') {
        catalogCalls++
        if (mode === 'catalog-error' && catalogCalls === 1) return reply({ error: 'Saved batch list is unavailable in this synthetic test. Retry the same permitted client/batch list.' }, 503)
        if (mode === 'catalog-close' && catalogCalls === 1) {
          await new Promise(resolve => { releaseHeldCatalog = resolve })
          // Fulfilment after AbortController cancellation can be ignored by the
          // browser. Either way it must never replace a later permitted catalog.
          await reply(staleCatalog).catch(() => {})
          return
        }
        return reply(currentCatalog)
      }
      if (pathname === '/api/excel-payslips/overview-preview' && request.method() === 'POST') {
        const body = request.postDataJSON(); requests.at(-1).body = body
        assert.deepEqual(body.scopes, currentCatalog.map(({ clientId, batchId }) => ({ clientId, batchId }))); assert.ok(body.pageSize <= 100)
        if (failNext) { failNext = false; return reply({ error: 'Synthetic overview unavailable; same scope retry only.' }, 503) }
        if (mode === 'employee-overlap') {
          const filtered = employeeRows.filter(row => (!body.overlapOnly || row.scopeOverlap) && (body.readiness === 'All' || row.readiness === body.readiness) && (body.masterStatus === 'All' || row.masterStatus === body.masterStatus) && (!body.search || [row.employeeCode, row.employeeName].some(value => value.toLowerCase().includes(body.search.toLowerCase()))))
          const sourceNetPay = filtered.reduce((sum, row) => sum + 1000 + Number(row.key.split('-').at(-1)), 0), sourceDeductions = filtered.length * 100
          const totals = { sourceRows: filtered.length, readyRows: filtered.filter(row => row.readiness === 'Ready').length, reviewRows: filtered.filter(row => row.readiness === 'Review').length, blockedRows: 0, uniqueScopedCodeGroups: new Set(filtered.filter(row => row.employeeCode && row.masterStatus !== 'Conflict').map(row => row.clientId + ':' + row.employeeCode)).size, matchedMasterRecords: new Set(filtered.filter(row => row.masterStatus === 'Matched').map(row => row.clientId + ':' + row.masterEmployeeId)).size, matchedRows: filtered.filter(row => row.masterStatus === 'Matched').length, unmatchedRows: 0, conflictRows: filtered.filter(row => row.masterStatus === 'Conflict').length, missingCodeRows: filtered.filter(row => row.masterStatus === 'Missing code').length, uncountedEmployeeRows: filtered.filter(row => !row.employeeCode || row.masterStatus === 'Conflict').length, overlapRows: filtered.filter(row => row.scopeOverlap).length, ...money(sourceNetPay, sourceNetPay + sourceDeductions, sourceDeductions) }
          return reply({ scopeLabel: 'Eight synthetic explicit batches', snapshot: '8'.repeat(64), observedAtUtc: '2026-10-28T00:00:00Z', scopeBatchCount: 8, scopeTotalRows: 72, page: body.page, pageSize: body.pageSize, totals, rows: filtered.slice((body.page - 1) * body.pageSize, body.page * body.pageSize) })
        }
        const filtered = body.search ? allRows.slice(0, 2) : body.readiness === 'Ready' ? allRows.slice(0, 400) : allRows
        const totals = { sourceRows: filtered.length, readyRows: filtered.filter(row => row.readiness === 'Ready').length, reviewRows: filtered.filter(row => row.readiness === 'Review').length, blockedRows: 0, uniqueScopedCodeGroups: filtered.length, matchedMasterRecords: filtered.length, matchedRows: filtered.length, unmatchedRows: 0, conflictRows: 0, missingCodeRows: 0, overlapRows: mode === 'overlap' ? 2 : 0 }
        if (mode !== 'missing') Object.assign(totals, body.search ? money(22222.22, 24000, 1777.78) : body.readiness === 'Ready' ? money(4000000, 4500000, 500000) : money(7300012.34, 8500020.45, 1200008.11))
        return reply({ scopeLabel: 'Synthetic selected source scope', snapshot: 'f'.repeat(64), observedAtUtc: '2026-10-28T00:00:00Z', scopeBatchCount: 1, scopeTotalRows: 830, page: body.page, pageSize: body.pageSize, totals, rows: filtered.slice((body.page - 1) * body.pageSize, body.page * body.pageSize) })
      }
      unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
    }
    if (url.origin !== origin) { unexpected.push('External ' + request.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
    if (pathname === '/overview-totals-fixture') return route.fulfill({ contentType: 'text/html', body: html })
    if (pathname === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
    if (pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
    if (request.method() === 'GET' && (pathname === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => pathname.startsWith(prefix)))) return route.continue()
    unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
  })
  const id = `${viewport.width}x${viewport.height}-${mode}`
  try {
    await page.goto(origin + '/overview-totals-fixture', { waitUntil: 'domcontentloaded' })
    await page.getByRole('button', { name: 'Open source totals overview', exact: true }).click()
    const drawer = page.locator('.excel-payslip-overview')
    const tile = title => drawer.locator('.ant-statistic').filter({ has: page.locator('.ant-statistic-title', { hasText: title }) })
    const net = tile('Source net pay'), gross = tile('Source gross earnings'), deductions = tile('Source deductions')
    const value = locator => locator.locator('.ant-statistic-content-value')
    const checkAmount = async (locator, amount) => expect(value(locator)).toHaveText(new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount))
    await expect(net).toBeVisible(); await expect(gross).toBeVisible(); await expect(deductions).toBeVisible()
    if (mode === 'employee-overlap') {
      const employeeTile = tile('Total employees'), selectedControl = drawer.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Overview selected batches', exact: true }) })
      await selectedControl.click()
      for (const batch of currentCatalog) await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({ hasText: batch.fileName }).click()
      await page.keyboard.press('Escape')
      const preview = drawer.locator('button').filter({ hasText: 'Preview selected scope (8)' })
      await preview.click(); await expect(value(employeeTile)).toHaveText('64'); await expect(drawer.getByText('72 filtered source rows', { exact: true })).toBeVisible()
      await expect(employeeTile.locator('..')).toContainText(/5.*(?:not counted|excluded|uncounted)|(?:not counted|excluded|uncounted).*5/i)
      await expect(drawer.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(25)
      const allNet = employeeRows.reduce((sum, row) => sum + 1000 + Number(row.key.split('-').at(-1)), 0); await checkAmount(net, allNet)
      checks.push(id + ': eightbatches72sourcerowsyield64codedemployees/5uncountedincludingblankcodemastermatchbeforepagination')
      await drawer.locator('.ant-pagination-next button').click(); await expect(value(employeeTile)).toHaveText('64'); await checkAmount(net, allNet)
      checks.push(id + ': employeecountandmoneyremainstableonpage2')
      await employeeTile.scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(output, id + '-employees.png'), animations: 'disabled' })
      await drawer.getByRole('button', { name: 'Show overlapping rows (6)', exact: true }).click()
      await expect(drawer.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(6)
      const actualKeys = await drawer.locator('.ant-table-tbody tr[data-row-key]').evaluateAll(rows => rows.map(row => row.getAttribute('data-row-key')).sort())
      assert.deepEqual(actualKeys, overlapKeys); await expect(value(employeeTile)).toHaveText('3')
      await checkAmount(net, employeeRows.filter(row => row.scopeOverlap).reduce((sum, row) => sum + 1000 + Number(row.key.split('-').at(-1)), 0))
      assert.equal(requests.at(-1).body.overlapOnly, true); assert.equal(requests.at(-1).body.page, 1)
      const lastOverlapRow = drawer.locator('.ant-table-tbody tr[data-row-key]').last()
      await lastOverlapRow.scrollIntoViewIfNeeded(); await expect(lastOverlapRow).toBeInViewport()
      checks.push(id + ': overlapactionfindsall6exactsourceIDsacrosspageswithoutclaiming6uniquepeople')
      await page.screenshot({ path: path.join(output, id + '-six-overlap-rows.png'), animations: 'disabled' })
      await drawer.getByRole('button', { name: 'Show all selected rows', exact: true }).click(); await expect(value(employeeTile)).toHaveText('64'); await expect(drawer.getByText('72 filtered source rows', { exact: true })).toBeVisible(); await checkAmount(net, allNet)
      assert.equal(requests.at(-1).body.overlapOnly, false); assert.equal(requests.at(-1).body.page, 1)
      checks.push(id + ': restoreallkeepssame8explicitclient/batchscopesandresetsjustoverlapview/page')
      await drawer.locator('input[aria-label="Overview search"]').fill('EMP007'); await expect(value(employeeTile)).toHaveText(/^[—-]$/); await preview.click(); await expect(value(employeeTile)).toHaveText('1'); await expect(drawer.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(1)
      await drawer.locator('input[aria-label="Overview search"]').fill(''); await expect(value(employeeTile)).toHaveText(/^[—-]$/)
      await drawer.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Overview readiness', exact: true }) }).click(); await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({ hasText: /^Ready$/ }).click()
      await preview.click(); await expect(value(employeeTile)).toHaveText('61'); await expect(drawer.getByText('61 filtered source rows', { exact: true })).toBeVisible()
      assert.ok(requests.filter(request => request.path === '/api/excel-payslips/overview-preview').every(request => request.body.scopes.length === 8))
      checks.push(id + ': searchandstatusinvalidateoldcountsandrecountfullfilteredscopewithoutlosingselected8')
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)); return
    }
    if (mode === 'catalog-error') {
      await expect(drawer).toContainText('Saved batch list is unavailable in this synthetic test.')
      await expect(value(net)).toHaveText('—'); await expect(value(gross)).toHaveText('—'); await expect(value(deductions)).toHaveText('—')
      await expect(drawer.getByRole('button', { name: 'Preview selected scope (0)', exact: true })).toBeDisabled()
      await drawer.getByRole('button', { name: 'Retry client/batch list', exact: true }).click()
      await expect.poll(() => catalogCalls).toBe(2)
      await expect(drawer.getByText('Saved batch list is unavailable in this synthetic test.', { exact: false })).toHaveCount(0)
      checks.push(id + ': failed permitted catalog shows recovery and unknown money, then same endpoint retry succeeds')
    }
    if (mode === 'catalog-close') {
      await expect.poll(() => Boolean(releaseHeldCatalog)).toBe(true)
      await drawer.getByRole('button', { name: 'Close', exact: true }).click(); await expect(drawer).not.toBeVisible()
      await expect.poll(() => failedRequests.filter(request => request.path === '/api/excel-payslips/overview-scopes').length).toBe(1)
      assert.match(failedRequests.find(request => request.path === '/api/excel-payslips/overview-scopes').error, /ABORT|CANCEL/i)
      releaseHeldCatalog(); releaseHeldCatalog = null
      await page.getByRole('button', { name: 'Open source totals overview', exact: true }).click()
      await expect(drawer).toBeVisible(); await expect.poll(() => catalogCalls).toBe(2)
      await expect(value(net)).toHaveText('—')
      checks.push(id + ': closing aborts in-flight catalog and reopening starts a fresh permitted request')
    }
    await page.getByRole('combobox', { name: 'Overview selected batches', exact: true }).click()
    await expect(page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).not.toContainText('Stale-cancelled-catalog.xlsx')
    await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({ hasText: 'Synthetic-830.xlsx' }).click(); await page.keyboard.press('Escape')
    const preview = drawer.locator('button').filter({ hasText: 'Preview selected scope (1)' })
    await preview.click(); await expect(drawer.getByText('830 filtered source rows', { exact: true })).toBeVisible()
    if (mode === 'missing') {
      await expect(net).toContainText('Unavailable'); await expect(gross).toContainText('Unavailable'); await expect(deductions).toContainText('Unavailable')
      checks.push(id + ': missing monetary contract is Unavailable, never a false zero')
    } else {
      await checkAmount(net, 7300012.34); await checkAmount(gross, 8500020.45); await checkAmount(deductions, 1200008.11)
      checks.push(id + ': totals cover all830 source rows while table contains25')
      await expect(drawer.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(25)
      if (mode === 'full') {
        await drawer.locator('.ant-pagination-next button').click(); await expect(drawer.locator('.ant-table-tbody tr[data-row-key]').first()).toContainText('SYN0026'); await checkAmount(net, 7300012.34)
        checks.push(id + ': page2 preserves all-filtered totals instead of page25 subtotal')
        const search = drawer.locator('input[aria-label="Overview search"]')
        await search.fill('Synthetic person 1'); await expect(value(net)).toHaveText('—'); await preview.click()
        await expect(drawer.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(2); await checkAmount(net, 22222.22); await checkAmount(gross, 24000); await checkAmount(deductions, 1777.78)
        checks.push(id + ': editing filters clears stale money; new search totals match filtered scope')
        await search.fill(''); await preview.click(); await checkAmount(net, 7300012.34)
        failNext = true; await preview.click(); await expect(drawer).toContainText('Synthetic overview unavailable'); await expect(value(net)).toHaveText('—')
        checks.push(id + ': failed preview clears old monetary totals; no broader retry')
        await preview.click(); await checkAmount(net, 7300012.34)
      }
    }
    assert.ok((await drawer.innerText()).match(/not.*(?:bank|payment|disburs)|(?:bank|payment|disburs).*not/i), 'Source totals must not claim verified bank disbursement')
    checks.push(id + ': source-versus-bank-disbursement meaning is explicit')
    if (mode === 'overlap') { await expect(drawer).toContainText(/2.*overlap|overlap.*2/i); checks.push(id + ': overlapping scope warning is explicit; no automatic deduplication claim') }
    const layout = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth, bodyHeight: document.documentElement.scrollHeight }))
    assert.ok(layout.scroll <= layout.width + 1, JSON.stringify(layout))
    const table = await drawer.locator('.excel-payslip-overview-table').boundingBox(); assert.ok(table && table.height >= 70 && table.width > 0)
    const body = drawer.locator('.ant-table-body'); await expect(body).toBeVisible(); assert.ok(await body.evaluate(element => element.scrollHeight > element.clientHeight), 'Representative25-row table retains internal vertical scrolling')
    await net.scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(output, id + '-net.png'), animations: 'disabled' })
    await deductions.scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' })
    checks.push(id + ': responsive tiles fit and table remains reachable with internal scrolling')
  } catch (error) {
    await page.screenshot({ path: path.join(output, id + '-failure.png'), animations: 'disabled' }).catch(() => {})
    fs.writeFileSync(path.join(output, id + '-failure.txt'), await page.locator('body').innerText().catch(() => 'Unavailable'))
    throw error
  } finally {
    assert.deepEqual(unexpected, [], 'Unexpected traffic must be blocked, not forwarded')
    assert.deepEqual(pageErrors, [])
    const actualConsoleErrors = consoleErrors.filter(message => !/503|Accessing element\.ref was removed in React 19|\[antd: (?:Select|Drawer)\]/.test(message))
    assert.deepEqual(actualConsoleErrors, [])
    if (releaseHeldCatalog) releaseHeldCatalog()
    audits.push({ scenario: id, interceptedApiCalls: requests.length, catalogCalls, requests, failedRequests, pageErrors, consoleErrors, unexpected, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const [viewport, mode] of [[{ width: 1366, height: 768 }, 'full'], [{ width: 768, height: 1024 }, 'responsive'], [{ width: 390, height: 844 }, 'responsive'], [{ width: 1366, height: 768 }, 'missing'], [{ width: 1366, height: 768 }, 'overlap'], [{ width: 1366, height: 768 }, 'catalog-error'], [{ width: 390, height: 844 }, 'catalog-close'], [{ width: 1366, height: 768 }, 'employee-overlap'], [{ width: 390, height: 844 }, 'employee-overlap']]) {
    if (process.env.HRMS_OVERVIEW_EMPLOYEES_ONLY === '1' && mode !== 'employee-overlap') continue
    if (process.env.HRMS_OVERVIEW_TOTALS_SCENARIO && `${viewport.width}x${viewport.height}-${mode}` !== process.env.HRMS_OVERVIEW_TOTALS_SCENARIO) continue
    try { await scenario(viewport, mode) } catch (error) { failures.push({ viewport, mode, error: error.stack }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1
