import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('../', import.meta.url))
const runtime = JSON.parse(fs.readFileSync(process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json', 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://127.0.0.1:5187'
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(origin).hostname))
const output = process.env.HRMS_LOCATION_UI_RESULTS || path.join(repo, '.codex-results/location-flow-ui')
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-location-flow-${crypto.randomUUID()}.tsx`, fixturePath = path.join(repo, 'payroll-ui', fixture)
const initial = { clientId: 42, month: '2026-10', policyBatchId: 'synthetic-policy', groupIds: [1], version: 'stable-config', storageAvailable: true,
  rows: Array.from({ length: 101 }, (_, i) => ({ key: 'part-' + i, assignmentId: 'assignment-' + i, employeeId: i === 100 ? 1 : i + 1, employeeCode: i === 100 ? 'SYN001' : `SYN${String(i + 1).padStart(3, '0')}`, employeeName: i === 100 ? 'Synthetic employee 1' : 'Synthetic employee ' + (i + 1), workLocationId: i === 100 ? 2 : 1, workLocationName: i === 100 ? 'South reviewed location' : 'North reviewed location with a longer title', templateId: i === 100 ? 'B' : 'A', templateName: i === 100 ? 'South wages' : 'North wages', rulesHash: 'rules-' + (i === 100 ? 'B' : 'A'), inputVersion: 'input-' + i, nativePart: true, payrollDays: 31, monthlyPayrollDays: 31, presentDays: null, payableDays: null, lockedPayRunId: null, issue: '', recorded: false })) }
fs.writeFileSync(fixturePath, `import React from 'react';import ReactDOM from 'react-dom/client';import{BrowserRouter}from'react-router-dom';import{ConfigProvider}from'antd';import theme from'/src/theme/hrmsTheme.ts';import AuthGate from'/src/components/AuthGate.tsx';import ToastProvider from'/src/components/ToastProvider.tsx';import Attendance from'/src/components/PayrollLocationAttendance.tsx';import Slips from'/src/components/PayrollLocationPayslips.tsx';import Excel from'/src/components/ExcelPayslips.tsx';import Header from'/src/components/layout/AppPageHeader.tsx';import'/src/index.css';import'/src/OrganizationSetup.css';import'/src/ModuleDrawer.css';import'/src/AppShell.css';const initial=${JSON.stringify(initial)};function Fixture(){const[message,setMessage]=React.useState('');const kind=new URLSearchParams(location.search).get('kind');return <ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title={kind==='excel'?'Excel Payslips':'Attendance'} description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main>{kind==='excel'?<Excel/>:kind==='slips'?<Slips run={{id:82,clientName:'NHIDCL',payPeriod:'2026-10',status:'Draft'}}/>:<><Attendance clientId={42} group={{id:1,clientId:42,attendanceMode:'Monthly',name:'Synthetic policy'}} groupIds={[1]} policyBatchId="synthetic-policy" reviewMonth="2026-10" initialLocationId={0} clientControl={<span>Synthetic client · Policy</span>} onMessage={setMessage}/><output data-testid="message">{message}</output></>}</main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>}ReactDOM.createRoot(document.getElementById('root')).render(<Fixture/>);`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;min-width:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}label.attendance-file-action{cursor:pointer}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from'/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
fs.writeFileSync(fixturePath, fs.readFileSync(fixturePath, 'utf8').replace("import Excel from", "import Runs from'/src/components/PayRunsPanel.tsx';import Excel from").replace("kind==='slips'?<Slips", "kind==='runs'?<Runs/>:kind==='slips'?<Slips"))
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const browser = await chromium.launch({ headless: true }), checks = [], errors = [], unexpected = []
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } }), page = await context.newPage()
await context.addInitScript(() => { window.__locationTestClicks = []; document.addEventListener('click',e=>window.__locationTestClicks.push({tag:e.target.tagName,text:e.target.textContent?.slice(0,70)}),true); window.__locationTestDownloads = []; const click = HTMLAnchorElement.prototype.click; HTMLAnchorElement.prototype.click = function () { window.__locationTestDownloads.push({ name: this.download, href: this.href }); return click.call(this) } })
page.setDefaultTimeout(15000); page.on('pageerror', error => errors.push(error.message))
await context.routeWebSocket('**/*', socket => socket.close())
let attendance = structuredClone(initial), attendanceSaves = [], batchSaves = [], originals = 0, panelMode = false, runRequests = [], oldApi = false
const panelEmployees = Array.from({ length: 101 }, (_, i) => ({ id: i + 1, clientId: 42, employeeCode: `SYN${String(i+1).padStart(3,'0')}`, firstName: 'Synthetic employee', lastName: String(i+1), department: 'Operations', designation: 'Engineer', annualCtc: 120000, isActive: true, workLocationId: 1, salaryStructureId: 'A', salaryComponents: {} }))
const saved = new Map(), history = [{ id: 'sep-protected', month: '2026-09', clientId: 42, sourceFileName: 'September retained.xlsx', rowCount: 1, createdAtUtc: '2026-09-30T00:00:00Z' }, { id: 'oct-protected', month: '2026-10', clientId: 42, sourceFileName: 'October retained.xlsx', rowCount: 1, createdAtUtc: '2026-10-31T00:00:00Z' }]
let sourceFiles = []
const e2e = process.env.HRMS_LOCATION_E2E_RESULTS
const slips = e2e ? JSON.parse(fs.readFileSync(path.join(e2e, 'new-flow-location-slips.json'), 'utf8')) : []
await context.route('**/*', route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (url.protocol === 'blob:' && url.origin === origin) return route.continue()
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/location-flow-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (p === '/@vite/client') return route.fulfill({ contentType: 'application/javascript', body: viteClient })
  if (e2e && p === '/e2e-formula-attendance.xlsx') return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', body: fs.readFileSync(path.join(e2e, 'NHIDCL-location-formula-attendance.xlsx')) })
  if (e2e && p === '/e2e-attendance-context.json') return reply(JSON.parse(fs.readFileSync(path.join(e2e, 'nhidcl-october-context.json'), 'utf8')))
  if (!p.startsWith('/api/')) return url.origin === origin ? route.continue() : route.abort()
  if (p === '/api/auth/me') return reply({ id: 1, displayName: 'Synthetic admin', roles: ['client_admin'], permissions: ['payroll.run', 'attendance.manage'], isActive: true })
  if (p === '/api/clients') return reply([{id:42,name:'Synthetic client',isActive:true}])
  if (p === '/api/setup') return reply({})
  if (p === '/api/payroll/employee-options') return reply(panelEmployees)
  if (p === '/api/leave-attendance/groups') return reply([{id:1,clientId:42,name:'Synthetic policy',policyBatchId:'synthetic-policy',employeeIds:panelEmployees.map(e=>e.id),isActive:true,workLocationId:0}])
  if (p === '/api/leave-attendance/attendance/monthly') return reply(panelEmployees.map(e=>({employeeId:e.id,workingDays:31,presentDays:31,payableDays:31,lopDays:0,month:'2026-10'})))
  if (p === '/api/leave-attendance/attendance/context') return reply({error:'Synthetic settings unavailable; use the existing manual fallback defaults'},404)
  if (p === '/api/pay-runs') { if(request.method()==='GET')return reply([]);runRequests.push(request.postDataJSON());return reply({error:'Synthetic queue captured without a server write'},400) }
  if (p === '/api/leave-attendance/attendance/payroll-locations') {
    if(oldApi)return reply({error:'Old API fixture'},404)
    if (request.method() === 'GET') return reply(panelMode?{...attendance,month:url.searchParams.get('month'),rows:[...attendance.rows,{...attendance.rows[0],key:'employee:101',employeeId:101,employeeCode:'SYN101',nativePart:false,recorded:false,presentDays:null,payableDays:null}]}:attendance)
    const input = request.postDataJSON(); assert.equal(input.clientId, 42); assert.equal(input.month, '2026-10'); assert.equal(input.contextVersion, initial.version)
    for (const row of input.rows) { const existing = attendance.rows.find(r => r.key === row.key); assert.equal(row.inputVersion, existing.inputVersion); Object.assign(existing, row, { recorded: true, inputVersion: row.inputVersion + '-saved' }) }
    attendanceSaves.push(input)
    return reply({ requestId: input.requestId, savedParts: input.rows.length, publishedEmployees: attendanceSaves.length === 1 ? 99 : 1, pendingEmployees: attendanceSaves.length === 1 ? 1 : 0, context: attendance })
  }
  if (p === '/api/pay-runs/82/location-payslips') return reply(slips)
  if (p === '/api/pay-runs/82/location-payslips/pdf' && e2e) return route.fulfill({ contentType: 'application/pdf', body: fs.readFileSync(path.join(e2e, 'NHIDCL-new-flow-location-payslips.pdf')) })
  if (p === '/api/excel-payslips/clients') return reply([{ id: 42, name: 'Synthetic client', hasSavedBatch: true, locationBatchesSupported: !oldApi }, { id: 11, name: 'Other synthetic client', hasSavedBatch: false, locationBatchesSupported: !oldApi }])
  if (p === '/api/excel-payslips/batches' && request.method() === 'GET') return reply(url.searchParams.get('clientId') === '42' ? history : [])
  if (p === '/api/excel-payslips/batches' && request.method() === 'POST') {
    const input = request.postDataJSON(); assert.equal(input.createLocationBatch, true); assert.equal(input.locationBatch, true); assert.equal(input.month, '2026-10')
    assert.ok(input.locationLabel); assert.match(input.id, /^[0-9a-f]{32}$/); assert.ok(!saved.has(input.id)); assert.equal(input.rows[0].employeeCode, 'SYN001')
    const result = { ...input, createLocationBatch: false, clientId: 42, clientName: 'Synthetic client', createdAtUtc: new Date().toISOString() }
    batchSaves.push(input); saved.set(input.id, result); history.unshift({ ...result, rowCount: result.rows.length, rows: undefined }); return reply(result)
  }
  if (p.startsWith('/api/excel-payslips/batches/') && request.method() === 'GET') {
    if (p.endsWith('/mail-jobs')) return reply({ items: [] })
    const id = p.split('/')[4]; if (saved.has(id)) return reply(saved.get(id))
  }
  if (p === '/api/excel-payslips/originals' && request.method() === 'POST') { const file = sourceFiles[originals++]; return reply({ id: String(originals).padStart(32, '0'), fileName: file.name, sha256: createHash('sha256').update(file.buffer).digest('hex') }) }
  if (p.endsWith('/profile')) return reply(null)
  if (p.endsWith('/templates')) return reply([])
  if (p.endsWith('/employee-code-options')) return reply({ clientCode: 'SYN', digits: 3, nextSequence: '101' })
  if (p.endsWith('/employee-code-preview')) return reply({ checkedCodes: 1, matches: [] })
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  if (request.method() !== 'GET') { unexpected.push(request.method() + ' ' + p); return reply({}, 403) }
  return reply([])
})
const chooseAttendanceLocation = async name => { await page.locator('.payroll-location-attendance .app-search-select').click(); await page.locator('.ant-select-dropdown:visible').getByText(name, { exact: true }).click(); await expect(page.locator('.ant-select-dropdown:visible')).toHaveCount(0) }
const xlsx = async (ctx, loc, days) => Buffer.from(await page.evaluate(async ({ ctx, loc, days }) => {
  const { buildLocationAttendanceWorkbook } = await import('/src/utils/payrollLocationAttendance.ts'); const { buildXlsxBlob } = await import('/src/utils/xlsx.ts')
  const sheets = buildLocationAttendanceWorkbook(ctx, loc); for (const [i, row] of sheets[0].rows.slice(1).entries()) { const n = days === 17 ? (i === 0 ? 17 : 31) : days; row[4] = n; row[5] = n }
  return [...new Uint8Array(await buildXlsxBlob(sheets).arrayBuffer())]
}, { ctx, loc, days }))
try {
  await page.goto(origin + '/location-flow-fixture')
  await expect(page.getByText('101 location parts', { exact: true })).toBeVisible()
  const southFile = await xlsx(initial, 2, 14), northFile = await xlsx(initial, 1, 17)
  await chooseAttendanceLocation('North reviewed location with a longer title (100)')
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download template', exact: true }).click({timeout:6000})])
  await download.saveAs(path.join(output, 'attendance-template.xlsx')); assert.ok(fs.readFileSync(path.join(output, 'attendance-template.xlsx')).subarray(0, 2).equals(Buffer.from('PK')))
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height }); await expect(page.getByRole('button', { name: 'Download template', exact: true })).toBeInViewport()
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Attendance page width ${width}`)
    await page.locator('.data-table-scroll').evaluateAll(els => els.forEach(el => { el.scrollTop = el.scrollHeight }))
    await expect(page.getByRole('button', { name: 'Download template', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `attendance-${width}.png`) })
  }
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.getByLabel('Upload location attendance', { exact: true }).setInputFiles({ name: 'North.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: northFile })
  await expect(page.getByRole('button', { name: 'Apply attendance (100)', exact: true })).toBeEnabled(); await page.getByRole('button', { name: 'Apply attendance (100)', exact: true }).click()
  await expect(page.getByTestId('message')).toContainText('1 employees still need another location')
  await expect(page.locator('.ant-drawer-open')).toHaveCount(0)
  await chooseAttendanceLocation('South reviewed location (1)')
  await page.getByLabel('Upload location attendance', { exact: true }).setInputFiles({ name: 'South.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: southFile })
  await page.locator('.ant-drawer-open .ant-drawer-footer button').filter({hasText:'Apply attendance (1)'}).click()
  await expect(page.getByTestId('message')).toContainText('1 employee totals ready'); assert.equal(attendanceSaves.length, 2)
  assert.equal(attendanceSaves[0].rows[0].presentDays, 17); assert.equal(attendanceSaves[1].rows[0].payableDays, 14)
  await page.reload(); await expect(page.locator('.payroll-location-attendance')).toContainText('South reviewed location')
  checks.push('Real XLSX download, North 17-day/South 14-day upload and review/apply, independent pre-downloaded location files, pending monthly totals and scoped preference reload')
  checks.push('100-row attendance workspace and persistent controls at desktop/tablet/mobile sizes with no horizontal page overflow')
  panelMode = true
  await page.goto(origin + '/location-flow-fixture?kind=runs')
  await expect(page.locator('.roster-actions')).toContainText('100 selected')
  await page.locator('.pay-run-form input[type=month]').fill('2026-10')
  const locationControl = page.locator('.pay-run-form label').filter({hasText:/^Location/}).locator('.app-search-select')
  await locationControl.click(); await page.locator('.ant-select-dropdown:visible').getByText('South reviewed location (1)',{exact:true}).click()
  await expect(page.locator('.ant-select-dropdown:visible')).toHaveCount(0)
  await expect(page.locator('.roster-actions')).toContainText('1 selected')
  for(const[width,height]of[[1366,768],[768,1024],[390,844]]) { await page.setViewportSize({width,height});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);await expect(page.locator('.payroll-queue-action').filter({hasText:'Queue regular draft'})).toBeInViewport();await page.screenshot({path:path.join(output,`payroll-context-${width}.png`)}) }
  await page.locator('.payroll-queue-action').filter({hasText:'Queue regular draft'}).click()
  await expect.poll(()=>runRequests.length).toBe(1);assert.deepEqual(runRequests[0].includedEmployeeIds,[1]);assert.equal(runRequests[0].attendancePolicyBatchId,'synthetic-policy');assert.equal(runRequests[0].payPeriod,'2026-10')
  checks.push('Payroll Policy + Location selector defaults to the reviewed native cohort, excludes an extra legacy member, filters the selected employee and binds the queue request to the current policy/month')
  panelMode = false;await page.setViewportSize({width:1366,height:768})
  await page.goto(origin + '/location-flow-fixture?kind=excel')
  await page.getByRole('combobox', { name: 'Excel payslips client', exact: true }).click(); await page.locator('.ant-select-dropdown:visible').getByText('Synthetic client', { exact: true }).click()
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  sourceFiles = await page.evaluate(async () => { const { buildXlsxBlob } = await import('/src/utils/xlsx.ts'); const files = []; for (const [name, gross, pf] of [['North', 17000, 2040], ['South', 28000, 3360]]) { const bytes = [...new Uint8Array(await buildXlsxBlob([{ name: 'Salary', rows: [['Employee Name', 'Employee Code', 'Work Location', 'Basic', 'PF', 'Net Pay', 'Present days'], ['Synthetic employee 1', 'SYN001', name, gross, pf, gross - pf, name === 'North' ? 17 : 14]] }]).arrayBuffer())]; files.push({ name: name + '.xlsx', bytes }) } return files })
  sourceFiles = sourceFiles.map(f => ({ name: f.name, mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: Buffer.from(f.bytes) }))
  await page.locator('.excel-payslip-import input[type=file]').first().setInputFiles(sourceFiles)
  await page.getByRole('textbox', { name: 'Payslip salary month', exact: true }).fill('2026-10')
  await expect(page.getByRole('button', { name: 'Save & next location (1)', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: 'Save & next location (1)', exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Payslip batch location', exact: true })).toHaveValue('South')
  await expect(page.getByRole('button', { name: 'Save location batch (1)', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: 'Save location batch (1)', exact: true }).click()
  await expect(page.locator('.excel-payslip-import')).not.toBeVisible(); assert.equal(batchSaves.length, 2); assert.equal(originals, 2)
  assert.notEqual(batchSaves[0].id, batchSaves[1].id); assert.equal(batchSaves[0].rows[0].netPay, 14960); assert.equal(batchSaves[1].rows[0].netPay, 24640)
  await page.getByRole('button', { name: 'Saved payslips', exact: true }).click()
  const drawer = page.locator('.ant-drawer-open').filter({ hasText: 'Saved payslips' }).last()
  await expect(drawer).toContainText('September retained.xlsx'); await expect(drawer).toContainText('October retained.xlsx'); await expect(drawer).toContainText('North.xlsx'); await expect(drawer).toContainText('South.xlsx')
  await page.screenshot({ path: path.join(output, 'same-month-batches.png') })
  checks.push('Two same-month XLSX files queued/reviewed/saved as distinct location batches with the same employee code and different wages; original September and October batches remain visible')
  if (slips.length) {
    const attendanceRoundTrip = await page.evaluate(async () => {
      const { parseImportPreviewSheets } = await import('/src/utils/importPreview.ts'); const { readLocationAttendanceUpload } = await import('/src/utils/payrollLocationAttendance.ts')
      const file = new File([await (await fetch('/e2e-formula-attendance.xlsx')).blob()], 'NHIDCL-location-attendance.xlsx')
      const ctx = await (await fetch('/e2e-attendance-context.json')).json(); ctx.rows.forEach(row => row.lockedPayRunId = null)
      const sheets = await parseImportPreviewSheets(file), parsed = readLocationAttendanceUpload(sheets.find(s => s.name === 'Attendance'), ctx, 0)
      return { sheets: sheets.map(s=>s.name), rows: parsed.length, employees: new Set(parsed.map(r=>r.employeeId)).size, payableDays: parsed.reduce((sum,r)=>sum+r.payableDays,0) }
    })
    assert.equal(attendanceRoundTrip.rows, 294); assert.equal(attendanceRoundTrip.employees, 290); assert.equal(attendanceRoundTrip.payableDays, 8805.5)
    checks.push('Actual 294-row native formula XLSX parsed back through the attendance importer with all salary/employee/location/version bindings intact (saved-run lock removed only in the parser test copy)')
    await page.goto(origin + '/location-flow-fixture?kind=slips'); await page.getByRole('button', { name: 'Location payslips', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Download PDF (294)', exact: true })).toBeEnabled()
    await page.getByRole('button', { name: 'Preview', exact: true }).first().click(); await expect(page.getByTitle('Saved location payslip preview')).toBeVisible()
    await page.locator('.ant-drawer-open').last().getByRole('button', { name: 'Close', exact: true }).click()
    for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) { await page.setViewportSize({ width, height }); await expect(page.getByRole('button', { name: 'Download PDF (294)', exact: true })).toBeInViewport(); await page.screenshot({ path: path.join(output, `slips-${width}.png`) }) }
    const [pdfDownload] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download PDF (294)', exact: true }).click()]); await pdfDownload.saveAs(path.join(output, 'downloaded-location-payslips.pdf'))
    assert.ok(fs.readFileSync(path.join(output, 'downloaded-location-payslips.pdf')).equals(fs.readFileSync(path.join(e2e, 'NHIDCL-new-flow-location-payslips.pdf'))))
    checks.push('All 294 actual isolated-payroll saved snapshots displayed, HTML preview and unchanged generated PDF downloaded with responsive footer controls')
  }
  oldApi = true;await page.setViewportSize({width:1366,height:768});await page.goto(origin+'/location-flow-fixture?kind=excel&clientId=42')
  await expect(page.getByRole('button',{name:'Import spreadsheet',exact:true})).toBeDisabled();await expect(page.getByRole('button',{name:'Saved payslips',exact:true})).toBeEnabled()
  await expect(page.getByText('Update the API to import separate location batches. Saved payslips remain available.',{exact:true})).toBeVisible()
  await page.goto(origin+'/location-flow-fixture');await expect(page.locator('.manual-attendance')).toBeVisible();await expect(page.locator('.payroll-location-attendance')).toHaveCount(0)
  checks.push('Older API cannot receive a new independent import; saved payslips remain available and Attendance uses its existing manual fallback')
  assert.deepEqual(errors, []); assert.deepEqual(unexpected, [])
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: true, checks, attendanceSaves: attendanceSaves.length, batchSaves: batchSaves.length, mockTransport: true, pdfSource: e2e ? 'isolated real payroll' : null, errors, unexpected }, null, 2))
  console.log(JSON.stringify({ passed: true, checks, output }, null, 2))
} catch (error) { await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {}); const state = await page.evaluate(() => ({ clicks: window.__locationTestClicks, downloads: window.__locationTestDownloads, buttons: [...document.querySelectorAll('button')].filter(e=>e.textContent.includes('Download template')).map(e=>({disabled:e.disabled,react:Object.keys(e).filter(k=>k.startsWith('__reactProps')).map(k=>String(e[k].onClick))})), alerts: [...document.querySelectorAll('.ant-alert')].map(e=>e.textContent) })).catch(()=>null); fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), state, errors, unexpected }, null, 2)); throw error }
finally { await browser.close(); fs.rmSync(fixturePath, { force: true }) }
