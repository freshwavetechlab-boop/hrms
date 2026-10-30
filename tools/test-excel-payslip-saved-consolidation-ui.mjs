import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('../', import.meta.url))
const runtimeFile = process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json'
const runtime = JSON.parse(fs.readFileSync(runtimeFile, 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname), 'Only the existing local UI may serve static modules.')
const output = process.env.HRMS_CONSOLIDATION_RESULTS || path.join(os.tmpdir(), 'frevo-saved-consolidation-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-saved-consolidation-${crypto.randomUUID()}.tsx`
const fixturePath = path.join(repo, 'payroll-ui', fixtureName)
const canonical = 'a'.repeat(32), sourceId = 'b'.repeat(32), groupId = 'd'.repeat(32)
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';
import Consolidate,{ExcelPayslipConsolidationHistory,ExcelPayslipRetainedSource} from '/src/components/ExcelPayslipConsolidation.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
const canonical='${canonical}',source='${sourceId}';
const rows=Array.from({length:830},(_,i)=>({id:'canonical-row-'+i,sourceRow:i+6,employeeCode:'SYN'+i,employeeName:'Synthetic person '+i,email:'',information:[],earnings:[{label:'Basic',amount:100}],deductions:[],employerContributions:[],netPay:100,warnings:[]}));
const batches=[{id:canonical,clientId:33,month:'2026-09',sourceFileName:'Synthetic current.xlsx',sheetName:'Salary',rowCount:830,createdAtUtc:'2026-10-28T00:00:00Z',createdBy:'Synthetic reviewer'},
{id:source,clientId:33,month:'2026-09',sourceFileName:'Synthetic earlier.xlsx',sheetName:'Salary',rowCount:new URLSearchParams(location.search).has('subset')?5:830,createdAtUtc:'2026-10-01T00:00:00Z',createdBy:'Synthetic reviewer'}];
function Fixture(){const[open,setOpen]=React.useState(false),[revision,setRevision]=React.useState(0),[retained,setRetained]=React.useState('');return <main style={{padding:12,minWidth:0,maxWidth:'100%',boxSizing:'border-box'}}><h1>Synthetic consolidation test</h1><button onClick={()=>setOpen(true)}>Open consolidation</button><Consolidate key="scope33" open={open} clientId={33} batches={batches} onClose={()=>setOpen(false)} onChanged={()=>setRevision(r=>r+1)}/><ExcelPayslipConsolidationHistory key={revision} clientId={33} batchId={canonical} rows={rows} canManage={true} onChanged={()=>setRevision(r=>r+1)} onRetainedSource={setRetained}/>{retained&&<ExcelPayslipRetainedSource clientId={33} batchId={retained} onClose={()=>setRetained('')}/>}</main>};ReactDOM.createRoot(document.getElementById('root')).render(<React.StrictMode><Fixture/></React.StrictMode>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/></head><body style="margin:0"><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const browser = await chromium.launch({ headless: true })
const checks = [], audits = [], failures = []
async function scenario(viewport, mode = 'success') {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block' }), page = await context.newPage()
  let active = false, applyCalls = 0, groupWrites = 0, undoCalls = 0
  const requests = [], unexpected = [], pageErrors = [], consoleErrors = []
  const sourceRows = mode === 'subset' ? 5 : 830
  const status = () => ({ groupId, canonicalBatchId: canonical, sourceBatchIds: [sourceId], active, canUndo: active, token: 'f'.repeat(64), canonicalRows: 830, createdAtUtc: '2026-10-28T00:00:00Z', warnings: [], historicalDelivery: active ? [{ rowId: 'canonical-row-0', sourceRowId: 'old-row-0', sourceBatchId: sourceId, fromConsolidatedVersion: true, status: 'Sent', mode: 'Individual', queuedAtUtc: '2026-10-01T00:00:00Z', sentAtUtc: '2026-10-01T00:01:00Z', message: 'Original retained dispatch evidence; not a new send.' }] : [], history: [] })
  page.on('pageerror', error => pageErrors.push(error.message)); page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), p = url.pathname
    const reply = (body, code = 200) => route.fulfill({ status: code, contentType: 'application/json', body: JSON.stringify(body) })
    if (p.startsWith('/api/')) {
      requests.push({ method: request.method(), path: p })
      assert.equal(url.searchParams.get('clientId'), '33')
      if (p === '/api/excel-payslips/consolidation-preview' && request.method() === 'POST') {
        const body = request.postDataJSON(); assert.deepEqual(Object.keys(body).sort(), ['canonicalBatchId', 'sourceBatchIds']); assert.equal(body.canonicalBatchId, canonical); assert.deepEqual(body.sourceBatchIds, [sourceId])
        return reply({ ...body, token: 'e'.repeat(64), canConsolidate: mode !== 'mismatch', blockers: mode === 'mismatch' ? ['Synthetic employer contribution differs; source review required.'] : [], canonicalRows: 830, matchedRows: sourceRows, sources: [{ batchId: sourceId, rows: sourceRows, matchedRows: sourceRows, financialMatch: mode !== 'mismatch', identityMatch: true, formulaSourcePresent: true, formulaSourceUnchanged: true, batchHash: 'c'.repeat(64), sourceHash: 'd'.repeat(64) }], differences: mode === 'mismatch' ? [{ sourceBatchId: sourceId, sourceRow: 6, field: 'Employer PF', reason: 'Synthetic financial mismatch remains blocked.' }] : [], evidence: 'Canonical formulas and immutable source versions are preserved; matching source rows are linked once.' })
      }
      if (p === '/api/excel-payslips/consolidations' && request.method() === 'POST') {
        const body = request.postDataJSON(); applyCalls++; assert.equal(body.canonicalBatchId, canonical); assert.deepEqual(body.sourceBatchIds, [sourceId]); assert.equal(body.token, 'e'.repeat(64)); assert.equal(body.acknowledged, true)
        assert.match(body.requestId, /^[a-f0-9-]{36}$/); requests.at(-1).requestId = body.requestId
        if (!active) { active = true; groupWrites++ }
        await new Promise(resolve => setTimeout(resolve, 180))
        if (mode === 'uncertain' && applyCalls === 1) return reply({ error: 'Synthetic response lost after metadata commit. Retry the same reviewed request.' }, 503)
        return reply(status())
      }
      if (p === '/api/excel-payslips/batches/' + canonical + '/consolidation' && request.method() === 'GET') return reply(mode === 'stale' ? { ...status(), active: false, warnings: ['Saved source changed. Consolidation history is stale; review the original versions.'] } : active ? status() : null)
      if (p === '/api/excel-payslips/consolidations/' + groupId + '/undo' && request.method() === 'POST') { const body = request.postDataJSON(); undoCalls++; assert.equal(body.token, 'f'.repeat(64)); assert.match(body.requestId, /^[a-f0-9-]{36}$/); active = false; return reply(status()) }
      if (p === '/api/excel-payslips/batches/' + sourceId && request.method() === 'GET') return reply({ id: sourceId, clientId: 33, month: '2026-09', sourceFileName: 'Synthetic earlier.xlsx', sheetName: 'Salary', headerRow: 1, createdAtUtc: '2026-10-01T00:00:00Z', createdBy: 'Synthetic reviewer', rows: Array.from({ length: sourceRows }, (_, i) => ({ id: 'old-row-' + i, sourceRow: i + 6, employeeCode: 'SYN' + i, employeeName: 'Synthetic person ' + i, email: '', information: [], earnings: [{ label: 'Basic', amount: 100 }], deductions: [], employerContributions: [], netPay: 100, warnings: [] })) })
      unexpected.push(request.method() + ' ' + p); return route.abort('blockedbyclient')
    }
    if (url.origin !== origin) return route.abort('blockedbyclient')
    if (p === '/saved-consolidation-fixture') return route.fulfill({ contentType: 'text/html', body: html })
    if (p === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
    if (request.method() === 'GET' && (p === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => p.startsWith(prefix)))) return route.continue()
    if (p === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
    unexpected.push(request.method() + ' ' + p); return route.abort('blockedbyclient')
  })
  const id = `${viewport.width}x${viewport.height}-${mode}`
  try {
    await page.goto(origin + '/saved-consolidation-fixture' + (mode === 'subset' ? '?subset=1' : ''), { waitUntil: 'domcontentloaded' })
    if (mode === 'stale') { await expect(page.getByText('Saved source changed. Consolidation history is stale; review the original versions.', { exact: true })).toBeVisible(); checks.push(id + ': stale inactive history warning remains visible'); return }
    await page.getByRole('button', { name: 'Open consolidation', exact: true }).click()
    const dialog = page.getByRole('dialog').filter({ hasText: 'Consolidate saved payslip versions' })
    await page.getByRole('combobox', { name: 'Consolidation source versions', exact: true }).click()
    await page.getByText(new RegExp('Synthetic earlier\\.xlsx.*'+sourceRows+' rows')).last().click()
    const preview = page.getByRole('button', { name: 'Preview consolidation', exact: true }), apply = page.getByRole('button', { name: 'Consolidate into one working batch', exact: true })
    const refreshPreview = async () => {
      const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/excel-payslips/consolidation-preview' && response.request().method() === 'POST')
      await preview.click(); await response; await expect(preview).toBeEnabled()
    }
    await expect(apply).toBeDisabled(); await refreshPreview()
    if (mode === 'mismatch') { await expect(page.getByText('Synthetic employer contribution differs; source review required.', { exact: true })).toBeVisible(); await expect(apply).toBeDisabled(); checks.push(id + ': genuine component mismatch blocks Apply'); await page.screenshot({ animations: 'disabled', path: path.join(output, id + '.png') }); return }
    await expect(page.getByText('830 rows in one working batch', { exact: true })).toBeVisible(); assert.ok(!(await dialog.innerText()).includes('1660')); checks.push(id + ': preview retains830 rows, no double counting')
    if(mode==='subset'){assert.ok(!(await dialog.innerText()).includes('835 rows'));await expect(dialog.getByText('5',{exact:true})).not.toHaveCount(0);checks.push(id+': five earlier rows link into the full830; retained history remains five, not835')}
    await page.getByRole('checkbox', { name: /I reviewed this exact client/ }).check(); await expect(apply).toBeEnabled()
    if (mode === 'success') {
      await page.getByRole('button', { name: 'Cancel', exact: true }).click(); await page.getByRole('button', { name: 'Open consolidation', exact: true }).click(); await expect(apply).toBeDisabled(); await expect(page.getByRole('checkbox', { name: /I reviewed this exact client/ })).toHaveCount(0); checks.push(id + ': cancel/reopen clears prior review acknowledgement')
      await refreshPreview(); await page.getByRole('checkbox', { name: /I reviewed this exact client/ }).check(); await expect(page.getByRole('checkbox', { name: /I reviewed this exact client/ })).toBeChecked(); await expect(apply).toBeEnabled()
    }
    const overflow = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }))
    assert.ok(overflow.scroll <= overflow.width + 1, JSON.stringify(overflow)); await page.screenshot({ animations: 'disabled', path: path.join(output, id + '-preview.png') }); checks.push(id + ': viewport has no page horizontal overflow')
    await apply.click({ clickCount: 2 })
    if (mode === 'uncertain') {
      await expect(page.getByText('Synthetic response lost after metadata commit. Retry the same reviewed request.', { exact: true })).toBeVisible(); await expect(apply).toBeEnabled(); await apply.click();
      const ids = requests.filter(request => request.path === '/api/excel-payslips/consolidations').map(request => request.requestId); assert.equal(ids.length, 2); assert.equal(ids[0], ids[1]); checks.push(id + ': uncertain retry keeps the identical request ID')
    }
    await expect(page.getByText('Consolidated working batch · 830 rows', { exact: true })).toBeVisible(); assert.equal(groupWrites, 1); assert.equal(applyCalls, mode === 'uncertain' ? 2 : 1); checks.push(id + ': repeated clicks create only one metadata group')
    await page.getByText(/Retained source history \(1 versions/).click(); await expect(page.getByText('Sent · earlier source version', { exact: true })).toBeVisible(); await expect(page.getByText('Synthetic person 0', { exact: true })).toBeVisible(); checks.push(id + ': historical status identifies employee and original source version')
    await page.screenshot({ animations: 'disabled', path: path.join(output, id + '-history.png') })
    await page.getByRole('button', { name: 'View retained version bbbbbbbb', exact: true }).click(); await expect(page.getByText('Retained source version · read only', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Email', exact: true })).toHaveCount(0); await page.getByRole('button', { name: 'Close retained version', exact: true }).click(); checks.push(id + ': original source is accessible read-only, without send or payroll controls')
    await page.getByRole('button', { name: 'Undo consolidation', exact: true }).click(); await expect(page.getByText('Consolidated working batch · 830 rows', { exact: true })).toHaveCount(0); assert.equal(undoCalls, 1); checks.push(id + ': supported Undo restores separate visibility')
  } catch (error) {
    await page.screenshot({ animations: 'disabled', path: path.join(output, id + '-failure.png') }).catch(() => {})
    fs.writeFileSync(path.join(output, id + '-failure.txt'), await page.locator('body').innerText().catch(() => 'Unavailable'))
    throw error
  } finally {
    assert.deepEqual(unexpected, [], 'All unexpected application/API traffic must be blocked and reported.'); assert.deepEqual(pageErrors, [])
    const expectedHttpErrors = consoleErrors.filter(message => /503/.test(message))
    const knownLibraryWarnings = consoleErrors.filter(message => /Accessing element\.ref was removed in React 19|\[antd: Select\].*dropdownMatchSelectWidth/.test(message))
    const actualConsoleErrors = consoleErrors.filter(message => !/503|Accessing element\.ref was removed in React 19|\[antd: Select\].*dropdownMatchSelectWidth/.test(message))
    assert.deepEqual(actualConsoleErrors, [])
    audits.push({ scenario: id, interceptedApiCalls: requests.length, applyCalls, groupWrites, undoCalls, unexpected, pageErrors, expectedHttpErrors, knownLibraryWarnings, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const [viewport, mode] of [[{ width: 1366, height: 768 }, 'success'], [{ width: 390, height: 844 }, 'success'], [{ width: 768, height: 1024 }, 'mismatch'], [{ width: 1366, height: 768 }, 'uncertain'], [{ width: 390, height: 844 }, 'stale'], [{ width: 1366, height: 768 }, 'subset']]) {
    if (process.env.HRMS_CONSOLIDATION_SCENARIO && `${viewport.width}x${viewport.height}-${mode}` !== process.env.HRMS_CONSOLIDATION_SCENARIO) continue
    try { await scenario(viewport, mode) } catch (error) { failures.push({ viewport, mode, error: error.stack }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1

