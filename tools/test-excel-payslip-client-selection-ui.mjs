import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

// Actual ExcelPayslips UI; all application/API requests are synthetic or blocked.
// No persistent browser profile, real credentials or live employee data is used.
const repo = fileURLToPath(new URL('../', import.meta.url))
const runtimeFile = process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json'
const runtime = JSON.parse(fs.readFileSync(runtimeFile, 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname))
const output = process.env.HRMS_CLIENT_SELECTION_RESULTS || path.join(os.tmpdir(), 'frevo-client-selection-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-client-selection-${crypto.randomUUID()}.tsx`, fixturePath = path.join(repo, 'payroll-ui', fixtureName)
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Synthetic client selection" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-height:0;min-width:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;min-height:0;min-width:0;display:flex;flex-direction:column;overflow:hidden;padding:8px;box-sizing:border-box}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const clients = [{ id: 33, name: 'Synthetic Alpha', hasSavedBatch: true }, { id: 44, name: 'Synthetic Beta', hasSavedBatch: true }]
const batches = clients.map((client, i) => ({ id: (i ? 'b' : 'a').repeat(32), clientId: client.id, clientName: client.name, month: '2026-09', sourceFileName: (i ? 'Beta' : 'Alpha') + '-source.xlsx', sheetName: 'Salary', headerRow: 1, createdAtUtc: '2026-10-28T00:00:00Z', createdBy: 'Synthetic reviewer', rows: [{ id: (i ? 'beta' : 'alpha') + '-row', sourceRow: 6, employeeName: client.name + ' employee', employeeCode: i ? 'BETA001' : 'ALPHA001', email: '', information: [{ label: 'Present days', value: '26' }, { label: 'Payable days', value: '30' }], earnings: [{ label: 'Wages', amount: 10000 }], deductions: [{ label: 'PF', amount: 1200 }], employerContributions: [], netPay: 8800, warnings: [] }] }))
const checks = [], audits = [], failures = [], browser = await chromium.launch({ headless: true })

async function scenario(viewport, mode) {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block' }), page = await context.newPage()
  const requests = [], unexpected = [], pageErrors = [], consoleErrors = []
  let holdAlphaList = mode === 'stale', releaseList = null, deleted = false, clientCatalogReads = 0, previewCount = 0, syntheticDeleteCalls = 0
  const permittedClients = mode === 'single' ? clients.slice(0, 1) : mode === 'presence' ? clients.map(client => ({ ...client, hasSavedBatch: client.id === 33 })) : clients
  const user = { id: 1, clientId: mode === 'single' ? 33 : null, displayName: 'Synthetic reviewer', roles: [mode === 'single' ? 'client_admin' : 'super_admin'], permissions: mode === 'delete-last' || mode === 'delete-protected' ? ['payroll.run', 'settings.manage'] : ['payroll.run'], isActive: true }
  // These are synthetic UI preferences created in this new test context only.
  await context.addInitScript(() => {
    sessionStorage.setItem('excel-payslips:1:client', JSON.stringify(33))
    sessionStorage.setItem('excel-payslips:1:33:batch', JSON.stringify('a'.repeat(32)))
  })
  page.on('pageerror', error => pageErrors.push(error.message)); page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), pathname = url.pathname
    const reply = body => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
    if (pathname.startsWith('/api/')) {
      requests.push({ method: request.method(), path: pathname, clientId: url.searchParams.get('clientId') })
      if (mode === 'delete-last' && pathname === '/api/excel-payslips/batches/' + batches[0].id + '/delete' && request.method() === 'POST') {
        assert.equal(url.searchParams.get('clientId'), '33')
        const body = request.postDataJSON(); assert.deepEqual(body, { token: 'lone-preview-' + previewCount, confirmBatchId: batches[0].id, discardUnusedLegacy: true })
        syntheticDeleteCalls++; assert.equal(syntheticDeleteCalls, 1, 'Fixture must receive exactly one explicit final delete')
        deleted = true; return reply({ deleted: true, alreadyDeleted: false })
      }
      if (request.method() !== 'GET') { unexpected.push('Blocked mutation ' + request.method() + ' ' + pathname); return route.abort('blockedbyclient') }
      if (pathname === '/api/auth/me') return reply(user)
      if (pathname === '/api/excel-payslips/clients') { clientCatalogReads++; return reply(permittedClients.map(client => deleted && client.id === 33 ? { ...client, hasSavedBatch: false } : client)) }
      // Keep Alpha in this mock catalog after deletion deliberately: explicit
      // fresh hasSavedBatch:false must win over previously loaded stale metadata.
      if (pathname === '/api/excel-payslips/overview-scopes') return reply((mode === 'presence' ? batches.slice(0, 1) : batches).map(batch => ({ clientId: batch.clientId, clientName: batch.clientName, batchId: batch.id, month: batch.month, fileName: batch.sourceFileName, sheetName: 'Salary', rowCount: 1, createdAtUtc: '2026-10-28T00:00:00Z', hasCalculationSource: true })))
      const clientId = Number(url.searchParams.get('clientId')); assert.ok(permittedClients.some(client => client.id === clientId), 'Every scoped request must remain permitted')
      const batch = batches.find(item => item.clientId === clientId)
      if (pathname === '/api/excel-payslips/batches') {
        if (deleted && clientId === 33) return reply([])
        if (mode === 'presence' && clientId === 44) return reply([])
        if (clientId === 33 && holdAlphaList) { holdAlphaList = false; await new Promise(resolve => { releaseList = resolve }) }
        const { rows, ...summary } = batch; return reply([{ ...summary, rowCount: rows.length }])
      }
      if (pathname === '/api/excel-payslips/templates') return reply([])
      if (pathname === '/api/excel-payslips/batches/' + batches[0].id + '/originals') return reply([])
      if (pathname === '/api/excel-payslips/batches/' + batches[0].id + '/delete-preview') {
        previewCount++; const used = mode === 'delete-protected'
        return reply({ batchId: batches[0].id, token: 'lone-preview-' + previewCount, payslipRows: 1, calculationSources: 1, deliveryRecords: used ? 1 : 0, mailJobs: 0, requestReceipts: 0, notifications: 0, notificationLogs: 0, masterSetupReceipts: 0, derivedBatches: 0, originalLinks: 0, otherBatchOriginalLinks: 0, blockers: used ? ['Delivery evidence exists; this batch must be retained.'] : ['Legacy provenance needs review.'], canDelete: false, canDiscardUnusedLegacy: !used, legacyStorageRecords: 0, cleanupAuditRecords: 0, isLegacy: true, legacyReviewValid: false, legacyReviewStatus: 'Synthetic legacy batch without ancestry', recordedParentId: '', candidates: [], candidateCount: 0, objects: [] })
      }
      const match = pathname.match(/^\/api\/excel-payslips\/batches\/([a-f0-9]{32})(?:\/(delivery-status|mail-jobs|consolidation))?$/)
      if (match) {
        assert.equal(match[1], batch.id, 'Batch must belong to the explicit client')
        if (!match[2]) return reply(batch)
        if (match[2] === 'delivery-status') return reply({ items: batch.rows.map(row => ({ rowId: row.id, status: 'Pending', email: '', canSend: true, message: 'Synthetic source' })) })
        if (match[2] === 'mail-jobs') return reply({ items: [] })
        if (match[2] === 'consolidation') return reply(null)
      }
      unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
    }
    if (url.origin !== origin) { unexpected.push('External ' + request.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
    if (pathname === '/client-selection-fixture') return route.fulfill({ contentType: 'text/html', body: html })
    if (pathname === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
    if (pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
    if (request.method() === 'GET' && (pathname === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => pathname.startsWith(prefix)))) return route.continue()
    unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
  })
  const id = `${viewport.width}x${viewport.height}-${mode}`
  try {
    const query = mode === 'linked' ? '?clientId=44&batchId=' + batches[1].id : mode === 'forbidden' ? '?clientId=999&batchId=' + batches[0].id : ''
    await page.goto(origin + '/client-selection-fixture' + query, { waitUntil: 'domcontentloaded' })
    const workspace = page.getByRole('region', { name: 'Excel payslips workspace', exact: true })
    const control = page.locator('#excel-payslips-client-controls .ant-select'), input = page.getByRole('combobox', { name: 'Excel payslips client', exact: true })
    const importButton = page.getByRole('button', { name: 'Import spreadsheet', exact: true }), historyButton = page.getByRole('button', { name: 'Batch history', exact: true })
    const history = page.getByRole('dialog').filter({ hasText: 'Saved Excel payslip batches' })
    const assertBlank = async () => { await expect(control.locator('.ant-select-selection-item')).toHaveCount(0); await expect(importButton).toBeDisabled(); await expect(historyButton).toBeDisabled(); await expect(workspace.locator('.excel-payslip-batch-bar')).toHaveCount(0) }
    const selectClient = async label => { await control.click(); await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({ hasText: new RegExp('^' + label) }).click(); await expect(control.locator('.ant-select-selection-item')).toHaveText(label) }
    const clearClient = async () => { await control.hover(); await control.locator('.ant-select-clear').click(); await assertBlank() }
    const assertHistory = async label => { await historyButton.click(); await expect(history.locator('.ant-drawer-header')).toContainText(label); await expect(history).toContainText(label === 'Synthetic Alpha' ? 'Alpha-source.xlsx' : 'Beta-source.xlsx') }
    const closeHistory = async () => history.getByRole('button', { name: 'Close', exact: true }).click()
    await expect(input).toBeVisible()
    if (mode === 'linked') {
      await expect(control.locator('.ant-select-selection-item')).toHaveText('Synthetic Beta')
      await expect(workspace).toContainText('Synthetic Beta employee'); assert.ok(!requests.some(request => request.clientId === '33'))
      await assertHistory('Synthetic Beta'); await page.screenshot({ path: path.join(output, id + '-history.png'), animations: 'disabled' })
      checks.push(id + ': validexplicitdeeplinkopensonlythepermittedclientandcorrectbatchhistorychip')
      return
    }
    if (mode === 'forbidden') {
      await expect(workspace).toContainText('not available in your access scope')
      await assertBlank(); assert.equal(requests.filter(request => request.path !== '/api/auth/me' && request.path !== '/api/excel-payslips/clients').length, 0)
      await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' })
      checks.push(id + ': unauthorizedclientdeeplinkstaysblankwithoutfirstclientfallbackorscopedrequests')
      return
    }
    await expect(input).toBeEnabled(); await assertBlank()
    assert.equal(requests.filter(request => request.path !== '/api/auth/me' && request.path !== '/api/excel-payslips/clients').length, 0)
    checks.push(id + ': genericrouteignoresoldclientandbatchpreferences; noimplicitclientevenifonlyonepermitted')
    await page.screenshot({ path: path.join(output, id + '-blank.png'), animations: 'disabled' })
    if (mode.startsWith('delete-')) {
      await selectClient('Synthetic Alpha'); await expect(importButton).toBeEnabled()
      if (mode === 'delete-last') {
        await page.getByRole('button', { name: 'All clients selected-scope overview', exact: true }).click()
        const overview = page.locator('.excel-payslip-overview'); await expect(page.getByRole('combobox', { name: 'Overview client', exact: true })).toBeEnabled()
        await overview.getByRole('button', { name: 'Close', exact: true }).click()
      }
      await assertHistory('Synthetic Alpha')
      const reviewButton = history.getByRole('button', { name: 'Review / delete batch', exact: true })
      if (mode === 'delete-user') {
        await expect(reviewButton).toHaveCount(0); await expect(history.getByRole('button', { name: 'Original workbooks', exact: true })).toBeVisible()
        checks.push(id + ': nonadminwithoutsettingspermissioncannotopendeleteconfirmation')
        await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' }); return
      }
      await reviewButton.click()
      const cleanup = page.getByRole('dialog').filter({ hasText: 'Review batch cleanup' }), finalDelete = cleanup.getByRole('button', { name: 'Permanently delete batch', exact: true })
      await expect(finalDelete).toBeDisabled()
      const discard = cleanup.getByRole('checkbox', { name: 'I choose to permanently discard this unused batch, even if it is the only version or its original file is unavailable.', exact: true })
      if (mode === 'delete-protected') {
        await expect(cleanup).toContainText('Delivery evidence exists; this batch must be retained.'); await expect(discard).toHaveCount(0); await expect(finalDelete).toBeDisabled()
        checks.push(id + ': deliveryusedbatchremainsblockeddespiteadminpermissionandlatest/soleversion')
        await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' }); return
      }
      await expect(cleanup).toContainText('Discard this unused batch'); await expect(cleanup).not.toContainText('No newer same-month version or recorded parent was found.')
      const confirmation = cleanup.getByRole('textbox', { name: 'Batch deletion confirmation ID', exact: true }), ack = cleanup.getByRole('checkbox', { name: 'I reviewed the counts and want to permanently delete this batch.', exact: true })
      await discard.check(); await expect(finalDelete).toBeDisabled(); await confirmation.fill('wrong-batch'); await ack.check(); await expect(finalDelete).toBeDisabled()
      await confirmation.fill(batches[0].id); await ack.uncheck(); await expect(finalDelete).toBeDisabled(); await ack.check(); await expect(finalDelete).toBeEnabled()
      checks.push(id + ': solelatestunusedbatchrequiresdiscardchoice/exactID/finalackwithoutkeepercandidate')
      await cleanup.getByRole('button', { name: 'Close', exact: true }).last().click(); await reviewButton.click(); await expect(discard).not.toBeChecked(); await expect(confirmation).toHaveValue(''); await expect(ack).not.toBeChecked(); await expect(finalDelete).toBeDisabled()
      await discard.check(); await confirmation.fill(batches[0].id); await ack.check(); await cleanup.getByRole('button', { name: 'Refresh dependency preview', exact: true }).click()
      await expect(discard).not.toBeChecked(); await expect(confirmation).toHaveValue(''); await expect(ack).not.toBeChecked(); await expect(finalDelete).toBeDisabled()
      checks.push(id + ': cancel/reopenandfreshpreviewresetallirreversibleconfirmationchoices')
      await discard.check(); await confirmation.fill(batches[0].id); await ack.check(); await expect(finalDelete).toBeEnabled()
      await page.screenshot({ path: path.join(output, id + '-review.png'), animations: 'disabled' })
      const readsBeforeDelete = clientCatalogReads; await finalDelete.click(); await expect(cleanup).not.toBeVisible(); await expect.poll(() => clientCatalogReads).toBeGreaterThan(readsBeforeDelete)
      assert.equal(syntheticDeleteCalls, 1); await expect(workspace.locator('.excel-payslip-batch-bar')).toHaveCount(0); await expect(importButton).toBeEnabled()
      if (await history.isVisible()) { await expect(history).toContainText('No saved batches for this client.'); await closeHistory() }
      checks.push(id + ': syntheticonlydeleteusesfreshintentpayloadandrefreshespermittedclientpresence')
      const options = () => page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option')
      const alpha = () => options().filter({ hasText: 'Synthetic Alpha' }), beta = () => options().filter({ hasText: 'Synthetic Beta' })
      const assertLastDeleteColors = async scopeName => {
        await expect(alpha()).toBeVisible(); await expect(beta()).toBeVisible(); await expect(alpha()).toBeInViewport(); await expect(beta()).toBeInViewport()
        await expect(alpha()).not.toContainText('Saved batch'); await expect(beta()).toContainText('Saved batch')
        assert.equal(await alpha().evaluate(element => getComputedStyle(element).backgroundColor), 'rgb(255, 255, 255)', scopeName + ' deleted-last client must bewhite')
        const green = await beta().evaluate(element => getComputedStyle(element).backgroundColor); assert.notEqual(green, 'rgb(255, 255, 255)', scopeName + ' otherclient staysgreen')
      }
      await control.click(); await assertLastDeleteColors('Main'); await page.screenshot({ path: path.join(output, id + '-main-white.png'), animations: 'disabled' }); await page.keyboard.press('Escape')
      await page.getByRole('button', { name: 'All clients selected-scope overview', exact: true }).click()
      const overview = page.locator('.excel-payslip-overview'), overviewInput = page.getByRole('combobox', { name: 'Overview client', exact: true })
      await overview.locator('.ant-select').filter({ has: overviewInput }).click(); await assertLastDeleteColors('Overview despite stale source catalog')
      await page.screenshot({ path: path.join(output, id + '-overview-white.png'), animations: 'disabled' })
      checks.push(id + ': lastbatchremovalturnsbothdropdownswhitewithoutreload/otherclientchanged;freshfalsebeatsstalecatalog')
      return
    }
    if (mode === 'presence') {
      const options = () => page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option')
      const option = name => options().filter({ hasText: name })
      const effectiveBackground = locator => locator.evaluate(element => {
        for (let current = element; current; current = current.parentElement) {
          const color = getComputedStyle(current).backgroundColor
          if (color !== 'transparent' && color !== 'rgba(0, 0, 0, 0)') return color
        }
        return 'transparent'
      })
      const assertPresenceColors = async scope => {
        await expect(option('Synthetic Alpha')).toBeVisible(); await expect(option('Synthetic Beta')).toBeVisible()
        await expect(option('Synthetic Alpha')).toBeInViewport(); await expect(option('Synthetic Beta')).toBeInViewport()
        await expect(option('Synthetic Alpha')).toContainText('Saved batch')
        await expect(option('Synthetic Beta')).not.toContainText('Saved batch')
        const alpha = await effectiveBackground(option('Synthetic Alpha')), beta = await effectiveBackground(option('Synthetic Beta'))
        const channels = [...alpha.matchAll(/[\d.]+/g)].map(match => Number(match[0]))
        assert.ok(channels.length >= 3 && channels[1] > channels[0] && channels[1] > channels[2] && channels[0] >= 180 && channels[2] >= 180, scope + ' saved client should be lightgreen, got ' + alpha)
        assert.equal(beta, 'rgb(255, 255, 255)', scope + ' no-batch client stayswhite')
        return { scope, alpha, beta }
      }
      await control.click(); const colorEvidence = [await assertPresenceColors('Main client dropdown')]
      await page.screenshot({ path: path.join(output, id + '-main-colors.png'), animations: 'disabled' })
      await input.fill('Beta'); await expect(option('Synthetic Beta')).toBeVisible(); await expect(option('Synthetic Alpha')).toHaveCount(0); await expect(importButton).toBeDisabled()
      await input.fill(''); await assertPresenceColors('Main client search cleared'); await page.keyboard.press('Escape'); await assertBlank()
      checks.push(id + ': maindropdownsavedbatchgreen/emptyclientwhite; searchdoesnotselectaclient')
      await page.getByRole('button', { name: 'All clients selected-scope overview', exact: true }).click()
      const overview = page.locator('.excel-payslip-overview'), overviewInput = page.getByRole('combobox', { name: 'Overview client', exact: true })
      const overviewControl = overview.locator('.ant-select').filter({ has: overviewInput })
      await overviewControl.click(); colorEvidence.push(await assertPresenceColors('Overview client dropdown'))
      await page.screenshot({ path: path.join(output, id + '-overview-colors.png'), animations: 'disabled' })
      await overviewInput.fill('Beta'); await expect(option('Synthetic Beta')).toBeVisible(); await expect(option('Synthetic Alpha')).toHaveCount(0)
      await option('Synthetic Beta').click(); await expect(overviewControl.locator('.ant-select-selection-item')).toHaveText('Synthetic Beta')
      await expect(overview.getByRole('button', { name: 'Preview selected scope (0)', exact: true })).toBeDisabled()
      await overview.getByRole('combobox', { name: 'Overview selected batches', exact: true }).click()
      const emptyBatchChoices = page.locator('.ant-select-dropdown-empty:not(.ant-select-dropdown-hidden)')
      await expect(emptyBatchChoices).toContainText('No data'); await expect(emptyBatchChoices).not.toContainText('Alpha-source.xlsx')
      await page.keyboard.press('Escape')
      assert.equal(requests.filter(request => request.path === '/api/excel-payslips/overview-preview').length, 0)
      checks.push(id + ': overviewincludeswhitezerobatchclientfrompermittedmetadataandfilterswithoutinventingbatches')
      await overview.getByRole('button', { name: 'Close', exact: true }).click(); await assertBlank()
      await selectClient('Synthetic Beta'); await expect(importButton).toBeEnabled(); await expect(historyButton).toBeEnabled(); await clearClient()
      checks.push(id + ': whitezerobatchclientremainsselectableandImportenabledforitsfirstsavedbatch')
      await selectClient('Synthetic Alpha'); await expect(importButton).toBeEnabled(); await clearClient()
      checks.push(id + ': colorindicatorspreserveexplicitselect/clearandImportdisabledguard')
      fs.writeFileSync(path.join(output, id + '-colors.json'), JSON.stringify(colorEvidence, null, 2))
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
      return
    }
    if (mode === 'stale') {
      await selectClient('Synthetic Alpha'); await expect.poll(() => Boolean(releaseList)).toBe(true)
      await selectClient('Synthetic Beta'); await expect(importButton).toBeEnabled()
      const oldListResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/excel-payslips/batches' && new URL(response.url()).searchParams.get('clientId') === '33')
      releaseList(); releaseList = null; await oldListResponse
      await assertHistory('Synthetic Beta'); await expect(history).not.toContainText('Alpha-source.xlsx'); await closeHistory()
      checks.push(id + ': switchingclientignoreslatepriorclienthistorywithoutmixingbatchlists')
      await selectClient('Synthetic Alpha'); await expect(importButton).toBeEnabled()
      // Complete a supported read before clearing. Existing busy guards protect
      // the open-batch action; the harness never forces a disabled header action.
      const closeBatch = page.getByRole('button', { name: 'Close batch', exact: true }); if (await closeBatch.isVisible()) await closeBatch.click()
      await assertHistory('Synthetic Alpha')
      await history.getByRole('button', { name: 'Open batch', exact: true }).click()
      await expect(workspace).toContainText('Synthetic Alpha employee'); await expect(importButton).toBeEnabled()
      await clearClient()
      await selectClient('Synthetic Beta'); await assertHistory('Synthetic Beta'); await expect(history).not.toContainText('Alpha-source.xlsx'); await closeHistory()
      await expect(workspace).not.toContainText('Synthetic Alpha employee')
      checks.push(id + ': clearcompletedbatchthenclientswitchremovespriorclientdatawithoutforcingbusycontrols')
      await page.screenshot({ path: path.join(output, id + '.png'), animations: 'disabled' })
      return
    }
    await selectClient('Synthetic Alpha'); await expect(importButton).toBeEnabled(); await expect(historyButton).toBeEnabled()
    await assertHistory('Synthetic Alpha'); await page.screenshot({ path: path.join(output, id + '-history.png'), animations: 'disabled' }); await closeHistory()
    checks.push(id + ': explicitclientselectionenablesactionsandhistoryheaderidentifiestheclient')
    await importButton.click(); const importDrawer = page.getByRole('dialog').filter({ hasText: 'Import Excel payslips' }); await expect(importDrawer).toBeVisible(); await importDrawer.getByRole('button', { name: 'Cancel', exact: true }).click()
    await clearClient(); await expect(importDrawer).not.toBeVisible()
    checks.push(id + ': clearclientremovesopenbatchandimportstateanddisablesscopedactions')
    if (mode !== 'single') { await selectClient('Synthetic Beta'); await assertHistory('Synthetic Beta'); await expect(history).not.toContainText('Alpha-source.xlsx'); await closeHistory() }
    const beforeReload = requests.length; await page.reload({ waitUntil: 'domcontentloaded' }); await expect(input).toBeEnabled(); await assertBlank()
    assert.equal(requests.slice(beforeReload).filter(request => request.path !== '/api/auth/me' && request.path !== '/api/excel-payslips/clients').length, 0)
    checks.push(id + ': reloadinggenericrouteagainrequiresselectiondespitestoredpastclient')
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
  } catch (error) {
    await page.screenshot({ path: path.join(output, id + '-failure.png'), animations: 'disabled' }).catch(() => {})
    fs.writeFileSync(path.join(output, id + '-failure.txt'), await page.locator('body').innerText().catch(() => 'Unavailable'))
    throw error
  } finally {
    if (releaseList) releaseList()
    assert.deepEqual(unexpected, [], 'All unknown application/API requests are blocked')
    assert.deepEqual(pageErrors, [])
    const actualConsoleErrors = consoleErrors.filter(message => !/Accessing element\.ref was removed in React 19|\[antd: (?:Select|Drawer|Spin)\]/.test(message))
    assert.deepEqual(actualConsoleErrors, [])
    audits.push({ scenario: id, interceptedApiCalls: requests.length, clientCatalogReads, syntheticDeleteCalls, requests, pageErrors, consoleErrors, unexpected, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const [viewport, mode] of [[{ width: 1366, height: 768 }, 'manager'], [{ width: 390, height: 844 }, 'single'], [{ width: 1366, height: 768 }, 'linked'], [{ width: 390, height: 844 }, 'forbidden'], [{ width: 1366, height: 768 }, 'stale'], [{ width: 1366, height: 768 }, 'presence'], [{ width: 390, height: 844 }, 'presence'], [{ width: 1366, height: 768 }, 'delete-last'], [{ width: 390, height: 844 }, 'delete-last'], [{ width: 1366, height: 768 }, 'delete-protected'], [{ width: 390, height: 844 }, 'delete-user']]) {
    if (process.env.HRMS_CLIENT_SELECTION_DELETE_ONLY === '1' && !mode.startsWith('delete-')) continue
    if (process.env.HRMS_CLIENT_SELECTION_PRESENCE_ONLY === '1' && mode !== 'presence') continue
    if (process.env.HRMS_CLIENT_SELECTION_SCENARIO && `${viewport.width}x${viewport.height}-${mode}` !== process.env.HRMS_CLIENT_SELECTION_SCENARIO) continue
    try { await scenario(viewport, mode) } catch (error) { failures.push({ viewport, mode, error: error.stack }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1
