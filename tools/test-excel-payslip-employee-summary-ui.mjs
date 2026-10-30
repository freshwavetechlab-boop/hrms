import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('../', import.meta.url))
const runtime = JSON.parse(fs.readFileSync(process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json', 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(origin).hostname))
const output = process.env.HRMS_EMPLOYEE_SUMMARY_RESULTS || path.join(os.tmpdir(), 'frevo-employee-summary-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-employee-summary-${crypto.randomUUID()}.tsx`, fixturePath = path.join(repo, 'payroll-ui', fixtureName)
const specifications = [
  { code: '1075', name: 'Synthetic Vijay', pan: 'AAAAA1111A', parts: [[53, 17, 42749, 'RO-Gangtok', 'Sikkim'], [54, 13, 33860, 'RO-Siliguri', 'West Bengal']] },
  { code: '1190', name: 'Synthetic Suraj', pan: 'BBBBB2222B', parts: [[140, 10, 29681, 'RO-Shillong', 'Meghalaya'], [743, 20, 65375, 'RO-Siliguri', 'West Bengal']] },
  { code: '1127', name: 'Synthetic Mohd', pan: 'CCCCC3333C', parts: [[684, 11, 33584, 'RO-Agartala', 'Tripura'], [744, 15, 45796, 'RO-Siliguri', 'Tripura']] },
]
const rows = specifications.flatMap((employee, employeeIndex) => employee.parts.map(([sourceRow, days, netPay, location, state], partIndex) => ({
  id: 'original-source-' + sourceRow, sourceRow, employeeCode: employee.code, employeeName: employee.name, email: 'private-contact@example.invalid',
  information: [{ label: 'PAN', value: employee.pan }, { label: 'Bank account', value: '555566667777' }, { label: 'Mobile', value: '9999988888' }, { label: 'Work Location', value: location }, { label: 'State', value: state }, { label: 'Attendance', value: String(days) }, { label: 'Source workbook', value: `Synthetic-${employeeIndex + 1}-${partIndex + 1}.xlsx` }, { label: 'Source worksheet', value: 'Salary source' }, { label: 'Source Excel row', value: String(partIndex ? 15 : 7) }, { label: 'Payroll unit', value: `Unit-${employeeIndex + 1}-${partIndex + 1}` }],
  employment: { employeeId: null, employeeCode: employee.code, status: 'Active', lastWorkingDate: null, blockedForMonth: false, identityReviewRequired: true },
  earnings: [{ label: 'Basic salary', amount: netPay + 100 }], deductions: [{ label: 'Employee PF', amount: 100 }], employerContributions: [{ label: 'Employer PF', amount: 200 }], netPay, declaredGross: netPay + 100, declaredDeductions: 100, warnings: [],
})))
const batch = { id: 'a'.repeat(32), clientId: 33, clientName: 'Synthetic source client', month: '2026-09', sourceFileName: 'Combined synthetic salary.xlsx', sheetName: 'Combined salary', headerRow: 1, createdAtUtc: '2026-10-28T00:00:00Z', createdBy: 'Synthetic reviewer', rows, calculationSource: { sheetName: 'Combined salary', headerRow: 1, columnCount: 3, columns: [], rows: rows.map(row => ({ sourceRow: row.sourceRow, cells: [{ value: String(row.netPay), formulaText: `SUM(D${row.sourceRow}:F${row.sourceRow})-G${row.sourceRow}` }] })), excludedSourceRows: [] } }
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import Summary from '/src/components/ExcelPayslipEmployeeSummary.tsx';
import '/src/AppShell.css';import '/src/OrganizationSetup.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
const batch=${JSON.stringify(batch)};const original=JSON.stringify(batch);function freeze(value){if(value&&typeof value==='object'){Object.freeze(value);Object.values(value).forEach(freeze)}}freeze(batch);
function Fixture(){const[view,setView]=React.useState('Table'),[busy,setBusy]=React.useState(false),[review,setReview]=React.useState(''),[integrity,setIntegrity]=React.useState('Not checked');if(JSON.stringify(batch)!==original)throw Error('Original saved source changed');return <div className="fixture-root module-content"><header><strong>Synthetic employee source summary</strong><button onClick={()=>setView('Table')}>Table layout</button><button onClick={()=>setView('Cards')}>Cards layout</button><button onClick={()=>setBusy(value=>!value)}>Toggle busy</button><button onClick={()=>{if(JSON.stringify(batch)!==original)throw Error('Original formulas/rows changed');setIntegrity('Original source unchanged: 6 row IDs and formulas retained')}}>Check source integrity</button><output data-testid="source-integrity">{integrity}</output><output role="status" data-testid="source-reviewed">{review}</output></header><main className="hrms-page-body" style={new URLSearchParams(location.search).has('constrained')?{height:240,flex:'0 0 240px',boxSizing:'border-box'}:undefined}><Summary batch={batch} busy={busy} view={view} onReviewSource={row=>{if(!batch.rows.includes(row))throw Error('Review must receive original row reference');setReview('Reviewed '+row.id)}}/></main></div>};ReactDOM.createRoot(document.getElementById('root')).render(<React.StrictMode><Fixture/></React.StrictMode>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"/><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-width:0;min-height:0;display:flex;flex-direction:column}.fixture-root{display:flex!important}.fixture-root>header{display:flex;gap:8px;flex-wrap:wrap;align-items:center;padding:8px}.fixture-root>main{display:flex;flex:1;width:100%;box-sizing:border-box;min-width:0;min-height:0;overflow:hidden;padding:8px}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const checks = [], audits = [], failures = [], browser = await chromium.launch({ headless: true })

async function scenario(viewport, constrained = false) {
  const context = await browser.newContext({ viewport, serviceWorkers: 'block' }), page = await context.newPage()
  const unexpected = [], consoleErrors = [], pageErrors = []
  page.on('pageerror', error => pageErrors.push(error.message)); page.on('console', event => { if (event.type() === 'error') consoleErrors.push(event.text()) })
  await context.routeWebSocket('**/*', socket => socket.close())
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), pathname = url.pathname
    if (url.origin === 'https://fonts.googleapis.com') return route.fulfill({ contentType: 'text/css', body: '' })
    if (pathname.startsWith('/api/')) { unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient') }
    if (url.origin !== origin) { unexpected.push('External ' + request.method() + ' ' + url.origin); return route.abort('blockedbyclient') }
    if (pathname === '/employee-summary-fixture') return route.fulfill({ contentType: 'text/html', body: html })
    if (pathname === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
    if (pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' })
    if (request.method() === 'GET' && (pathname === '/' + fixtureName || ['/src/', '/node_modules/', '/@', '/assets/'].some(prefix => pathname.startsWith(prefix)))) return route.continue()
    unexpected.push(request.method() + ' ' + pathname); return route.abort('blockedbyclient')
  })
  const id = `${viewport.width}x${viewport.height}${constrained ? '-constrained' : ''}`
  try {
    await page.goto(origin + '/employee-summary-fixture' + (constrained ? '?constrained=1' : ''), { waitUntil: 'domcontentloaded' })
    const summary = page.getByLabel('Saved source employee summary', { exact: true })
    await expect(summary).toContainText('3 source employee groups'); await expect(summary).toContainText('6 original source parts')
    await expect(summary.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(3)
    if (constrained) {
      const action = summary.getByRole('button', { name: 'Source parts for 1075', exact: true })
      await expect(action).toBeInViewport()
      const geometry = await action.evaluate(button => { const rectangle = button.getBoundingClientRect(), main = button.closest('main').getBoundingClientRect(), top = document.elementFromPoint(rectangle.left + rectangle.width / 2, rectangle.top + rectangle.height / 2), toolbar = button.closest('.ant-smart-table').querySelector('.ant-table-toolbar'), body = button.closest('.ant-table-body'); return { withinAvailableMain: rectangle.top >= main.top - 1 && rectangle.bottom <= main.bottom + 1, topmostButton: top === button || button.contains(top), mainHeight: main.height, toolbarHeight: toolbar.getBoundingClientRect().height, bodyHeight: body?.clientHeight ?? 0 } })
      assert.equal(geometry.mainHeight, 240); assert.ok(geometry.withinAvailableMain && geometry.topmostButton, JSON.stringify(geometry)); assert.ok(geometry.bodyHeight > 50, JSON.stringify(geometry))
      fs.writeFileSync(path.join(output, id + '-geometry.json'), JSON.stringify(geometry, null, 2))
      await page.screenshot({ path: path.join(output, id + '-table.png'), animations: 'disabled' })
      checks.push(id + ': sourcepartsbuttonisnotclippedandistopmostinside240pxremainingworkspace')
    }
    const netIndex = await summary.locator('.ant-table-thead tr').first().locator('th').evaluateAll(headers => headers.findIndex(header => header.textContent.trim() === 'Source net pay'))
    const attendanceIndex = await summary.locator('.ant-table-thead tr').first().locator('th').evaluateAll(headers => headers.findIndex(header => header.textContent.trim() === 'Source attendance total'))
    let total = 0
    for (const [code, expected] of [['1075', 76609], ['1190', 95056], ['1127', 79380]]) {
      const row = summary.locator('.ant-table-tbody tr[data-row-key]').filter({ hasText: code })
      const net = row.locator('td').nth(netIndex); await expect(net).toHaveText(new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(expected)); total += Number((await net.innerText()).replace(/,/g, ''))
      await expect(row).toContainText('Employee Master identity review'); await expect(row).toContainText('Transfer dates not verified')
      await expect(row.locator('td').nth(attendanceIndex)).toHaveText(code === '1127' ? '26' : '30')
    }
    assert.equal(total, 251045)
    checks.push(id + ': sixsourcesdisplayas3sourceemployeegroupswith76609/95056/79380netand251045sum')
    const assertPrivateValuesAbsent = async () => { const text = await page.locator('body').innerText(); for (const value of ['AAAAA1111A', 'BBBBB2222B', 'CCCCC3333C', '555566667777', '9999988888', 'private-contact@example.invalid']) assert.ok(!text.includes(value), 'Private information must not enter summary or source-parts display') }
    await assertPrivateValuesAbsent()
    for (const employee of specifications) {
      await summary.getByRole('button', { name: 'Source parts for ' + employee.code, exact: true }).click()
      const drawer = page.locator('.excel-payslip-employee-parts')
      await expect(drawer).toContainText('dated coverage is not verified'); await expect(drawer).toContainText('Employee Master identity review is still required')
      for (const [sourceRow, days, netPay, location, state] of employee.parts) {
        const part = page.getByRole('region', { name: 'Original source part ' + sourceRow, exact: true })
        const attendanceDescription = part.locator('.ant-descriptions-row').filter({ has: page.locator('.ant-descriptions-item-label').filter({ hasText: /^Source attendance$/ }) })
        await expect(attendanceDescription.getByText(String(days), { exact: true })).toBeVisible(); await expect(part.getByText(location, { exact: true })).toBeVisible(); await expect(part.getByText(state, { exact: true })).toBeVisible()
        await expect(part).toContainText(new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(netPay))
        await expect(part).toContainText('Basic salary'); await expect(part).toContainText('Employee PF'); await expect(part).toContainText('Employer PF')
      }
      await assertPrivateValuesAbsent()
      if (employee.code === '1127') {
        const original = page.getByRole('region', { name: 'Original source part 744', exact: true })
        await original.getByRole('button', { name: 'Review original source row 744', exact: true }).scrollIntoViewIfNeeded(); await expect(original.getByRole('button', { name: 'Review original source row 744', exact: true })).toBeInViewport()
        await page.screenshot({ path: path.join(output, id + '-location-state-source-parts.png'), animations: 'disabled' })
        await original.getByRole('button', { name: 'Review original source row 744', exact: true }).click(); await expect(page.getByTestId('source-reviewed')).toHaveText('Reviewed original-source-744'); await expect(drawer).not.toBeVisible()
      } else await drawer.getByRole('button', { name: 'Close source parts', exact: true }).click()
    }
    checks.push(id + ': each17/13,10/20,11/15partretainsoriginalsource/attendance/components;row744Siliguri/Tripuraremainsunmodified')
    checks.push(id + ': revieworiginalcallbackreceivesoriginal744reference;identity/datewarningspreserved')
    await expect(summary.getByRole('button', { name: /PDF|Email|Configure|Apply|Send/i })).toHaveCount(0)
    await page.getByRole('button', { name: 'Toggle busy', exact: true }).click(); await expect(summary.getByRole('button', { name: 'Source parts for 1075', exact: true })).toBeDisabled(); await page.getByRole('button', { name: 'Toggle busy', exact: true }).click()
    await page.getByRole('button', { name: 'Cards layout', exact: true }).click(); await expect(summary.locator('.shared-record-cards article')).toHaveCount(3)
    const firstCard = summary.locator('.shared-record-cards article').filter({ hasText: '1075' }); await firstCard.scrollIntoViewIfNeeded(); await expect(firstCard).toContainText('76,609.00')
    assert.ok(await firstCard.evaluate(card => [...card.querySelectorAll('.ant-tag')].every(tag => tag.getBoundingClientRect().right <= card.getBoundingClientRect().right + 1)), 'Review badges must wrap inside the card instead of being clipped')
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
    await page.screenshot({ path: path.join(output, id + '-cards.png'), animations: 'disabled' })
    checks.push(id + ': table/cardsremainread-onlywithresponsiveinternalcontentandnooutboundactions')
    await page.getByRole('button', { name: 'Check source integrity', exact: true }).click(); await expect(page.getByTestId('source-integrity')).toHaveText('Original source unchanged: 6 row IDs and formulas retained')
    await assertPrivateValuesAbsent(); checks.push(id + ': frozenoriginalrows/formulas/IDsunchangedandprivatefieldsneverdisplayed')
  } catch (error) {
    await page.screenshot({ path: path.join(output, id + '-failure.png'), animations: 'disabled' }).catch(() => {})
    fs.writeFileSync(path.join(output, id + '-failure-error.txt'), error.stack || String(error))
    fs.writeFileSync(path.join(output, id + '-failure.txt'), await page.locator('body').innerText().catch(() => 'Unavailable')); throw error
  } finally {
    assert.deepEqual(unexpected, [], 'This display component must make no API/application requests')
    assert.deepEqual(pageErrors, [])
    assert.deepEqual(consoleErrors.filter(message => !/Accessing element\.ref was removed in React 19|\[antd: (?:Drawer|Descriptions)\]|\[antd: Select\].*dropdownMatchSelectWidth/.test(message)), [])
    audits.push({ scenario: id, unexpected, pageErrors, consoleErrors, realApiCallsForwarded: 0, realBusinessWrites: 0 })
    await context.close()
  }
}
try {
  for (const [viewport, constrained] of [[{ width: 1366, height: 768 }, false], [{ width: 390, height: 844 }, false], [{ width: 790, height: 759 }, true]]) {
    const id = `${viewport.width}x${viewport.height}${constrained ? '-constrained' : ''}`
    if (process.env.HRMS_EMPLOYEE_SUMMARY_SCENARIO && id !== process.env.HRMS_EMPLOYEE_SUMMARY_SCENARIO) continue
    try { await scenario(viewport, constrained) } catch (error) { failures.push({ viewport, constrained, error: error.stack }); break }
  }
} finally {
  await browser.close(); fs.unlinkSync(fixturePath)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, audits, failures, realApiCallsForwarded: 0, realBusinessWrites: 0 }, null, 2))
  console.log(JSON.stringify({ checks: checks.length, scenarios: audits.length, failures: failures.length, output, realApiCallsForwarded: 0, realBusinessWrites: 0, details: failures }))
}
if (failures.length) process.exitCode = 1
