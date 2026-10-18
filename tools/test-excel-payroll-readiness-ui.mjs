import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = process.env.HRMS_PLAYWRIGHT_MODULE ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href) : require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only.')
const output = path.join(os.tmpdir(), 'frevo-excel-readiness-ui-20261017')
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-readiness-${crypto.randomUUID()}.tsx`
const fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React,{useEffect,useState} from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import Workspace from '/src/components/ExcelPayrollSetupWorkspace.tsx';import 'antd/dist/reset.css';import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
function Test(){const [batch,setBatch]=useState(null);useEffect(()=>{fetch('/readiness-data').then(r=>r.json()).then(setBatch)},[]);return batch?<Workspace clientId={33} batch={batch} rowIds={batch.rows.map(row=>row.id)} onClose={()=>{}} onApplied={()=>{}}/>:null}
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><Test/></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const batch = { id: 'a'.repeat(32), clientId: 33, clientName: 'Synthetic Client', month: '2026-09', headerRow: 1, sourceFileName: 'Synthetic.xlsx', rows: Array.from({ length: 214 }, (_, i) => ({ id: `row-${i + 2}`, sourceRow: i + 2, employeeCode: `EXCEL${i + 1}`, employeeName: `Employee ${String(i + 1).padStart(3, '0')}`, information: [], earnings: [], deductions: [], employerContributions: [], netPay: 8800, warnings: [] })) }
const source = { headerRow: 1, columnCount: 8, columns: ['Name', 'Work Location', 'Month days', 'Attendance', 'Basic', 'ER Basic', 'EPF @12%', 'Net Pay'].map((sourceHeader, columnIndex) => ({ columnIndex, sourceHeader, label: sourceHeader, kind: columnIndex === 0 ? 'employeeName' : columnIndex === 5 ? 'earning' : columnIndex === 6 ? 'deduction' : columnIndex === 7 ? 'netPay' : 'ignore' })), rows: batch.rows.map(row => ({ sourceRow: row.sourceRow, cells: [{ value: row.employeeName }, { value: 'Pune' }, { value: '26' }, { value: '26' }, { value: '10000' }, { value: '10000', formulaText: `E${row.sourceRow}/C${row.sourceRow}*D${row.sourceRow}` }, { value: '1200', formulaText: `F${row.sourceRow}*12%` }, { value: '8800' }] })) }
const applied = new Set(batch.rows.slice(209).map(row => row.id)), missingIds = new Set([9001, 9002, 9003])
const employees = batch.rows.slice(0, 200).map((row, i) => ({ id: i + 1, employeeName: row.employeeName, employeeCode: `MASTER${i + 1}`, workLocationId: 1, workLocation: 'Pune' }))
const roster = () => ({ rows: batch.rows.map((row, i) => ({ rowId: row.id, sourceRow: row.sourceRow, employeeName: row.employeeName, employeeCode: i < 200 ? employees[i].employeeCode : row.employeeCode, employeeId: i < 200 ? employees[i].id : null, status: applied.has(row.id) ? 'Applied' : i < 200 ? 'Existing' : i < 208 ? 'New' : 'Review', message: i < 200 ? 'Matched master' : i < 208 ? 'Review new employee creation' : 'Ambiguous identity' })), missingEmployees: [...missingIds].map(id => ({ employeeId: id, employeeCode: `OLD${id}`, employeeName: `Previous employee ${id}`, workLocation: 'Pune' })) })
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const page = await browser.newPage({ viewport: { width: 1366, height: 768 } }), errors = [], writes = [], checks = []
let lostApplyResponse = true, simulations = 0, activeSimulations = 0, maximumSimulations = 0
page.on('pageerror', error => errors.push(error.message))
await page.route('**/*', async route => {
  const req = route.request(), url = new URL(req.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/readiness-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (p === '/readiness-data') return reply(batch)
  if (!p.startsWith('/api/')) return route.continue()
  if (p === '/api/auth/me') return reply({ id: 1, displayName: 'Synthetic admin', roles: ['client_admin'], permissions: ['employees.manage', 'settings.manage', 'payroll.run'], isActive: true })
  if (p.endsWith('/context')) {
    const saved = writes.find(row => row.type === 'setup')?.body, selectedIds = url.searchParams.get('rowIds')?.split(',') || []
    const same = saved && selectedIds.join('|') === saved.request.rowIds.join('|')
    return reply({ source, employees, components: [], workLocations: [{ id: 1, name: 'Pune' }], workWeeks: [{ value: 'Sunday off', label: 'Sunday off' }], roster: roster(), appliedSetups: saved ? [saved.request.rowIds] : [], lastImport: same ? { status: 'Applied', requestId: saved.requestId, savedRequest: saved.request, result: { importId: 'receipt-1', employeeIds: saved.request.employeeDecisions.map(row => row.matchedEmployeeId), templateId: 'template-1', updatedCount: 100, createdCount: 0 } } : null })
  }
  if (p === '/api/payroll-calculation/simulate') {
    activeSimulations++; maximumSimulations = Math.max(maximumSimulations, activeSimulations); simulations++
    await new Promise(resolve => setTimeout(resolve, 15)); activeSimulations--
    return reply({ complete: true, employee: 'Synthetic employee', net: 8800, lines: [], inputs: [{ name: 'PAYROLL_DAYS', value: 26 }, { name: 'PAYABLE_DAYS', value: 26 }], issues: [] })
  }
  if (p.endsWith('/preview')) {
    const body = req.postDataJSON(), missingCreation = body.employeeDecisions.some(row => !row.matchedEmployeeId && !row.allowCreate)
    return reply({ canApply: !missingCreation, migrationRequired: false, planHash: 'b'.repeat(64), coverage: 'Synthetic arithmetic only', issues: missingCreation ? [{ severity: 'Error', message: 'Review and explicitly allow creation of this unmatched employee.' }] : [], rows: body.rowIds.map(id => ({ ...batch.rows.find(row => row.id === id), rowId: id, lines: [], net: 8800, payrollDays: 26, payableDays: 26 })) })
  }
  if (p.endsWith('/apply')) {
    const body = req.postDataJSON(); writes.push({ type: 'setup', body })
    assert.equal(body.request.rowIds.length, 100)
    assert.ok(body.request.employeeDecisions.every(row => row.matchedEmployeeId && !row.allowCreate))
    const replayed = body.request.rowIds.every(id => applied.has(id))
    body.request.rowIds.forEach(id => applied.add(id))
    if (lostApplyResponse) { lostApplyResponse = false; return reply({ error: 'Simulated interrupted response; retry the same reviewed setup.' }, 503) }
    return reply({ importId: 'receipt-1', employeeIds: body.request.employeeDecisions.map(row => row.matchedEmployeeId), templateId: 'template-1', createdCount: 0, updatedCount: 100, replayed })
  }
  if (p === '/api/employees/resignation-defaults') return reply(req.postDataJSON().employeeIds.map(employeeId => ({ employeeId, lastPayrollMonth: employeeId === 9001 ? '2026-08' : employeeId === 9002 ? '2026-09' : null, lastWorkingDate: employeeId === 9001 ? '2026-08-31' : employeeId === 9002 ? '2026-09-30' : null })))
  if (p === '/api/employees/mark-resigned') {
    const body = req.postDataJSON(); writes.push({ type: 'exit', body })
    assert.deepEqual(body.lastWorkingDates, { 9001: '2026-08-31', 9002: '2026-09-30', 9003: '2026-09-15' })
    body.employeeIds.forEach(id => missingIds.delete(id))
    return reply({ marked: 3, alreadyMarked: 0, employeeIds: body.employeeIds })
  }
  if (req.method() !== 'GET') { errors.push(`Unexpected write ${p}`); return reply({}, 403) }
  return reply([])
})
const editor = () => page.getByRole('dialog').filter({ has: page.getByText('Configure payroll from Excel', { exact: true }) })
try {
  await page.goto(origin + '/readiness-fixture')
  await expect(page.getByRole('button', { name: 'Existing (200)', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New (8)', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Needs review (1)', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Configure next 100 existing', exact: true }).click()
  await expect(editor().getByRole('textbox', { name: 'Payroll setup Template name' })).not.toHaveValue('')
  await editor().getByRole('button', { name: 'Add payroll component', exact: true }).click()
  await editor().getByText('CUSTOM_3', { exact: true }).click()
  await editor().getByRole('button', { name: 'Remove this payroll component', exact: true }).click()
  await expect(editor().getByText('CUSTOM_3', { exact: true })).toHaveCount(0)
  await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Payroll divisor source', exact: true }) }).click()
  await page.getByText('Fixed days (e.g. 26)', { exact: true }).last().click()
  await expect(editor().getByRole('spinbutton', { name: 'Reviewed fixed payroll days', exact: true })).toHaveValue('26')
  const firstTemplate = await editor().getByRole('textbox', { name: 'Payroll setup Template name' }).inputValue()
  await editor().getByRole('button', { name: /Preview calculations/ }).click()
  await editor().getByRole('checkbox', { name: 'Review payroll setup', exact: true }).check()
  await editor().getByRole('button', { name: /Apply reviewed setup/ }).click()
  await expect(editor().getByText(/Simulated interrupted response/)).toBeVisible()
  await editor().getByRole('button', { name: /Apply reviewed setup/ }).click()
  await expect(editor().getByRole('button', { name: /Verify saved payroll/ })).toBeVisible()
  assert.equal(writes.length, 2); assert.equal(writes[0].body.requestId, writes[1].body.requestId)
  assert.deepEqual(writes[0].body, writes[1].body)
  await editor().getByRole('button', { name: 'Close', exact: true }).last().click()
  await expect(page.getByRole('button', { name: 'Applied (105)', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Existing (100)', exact: true })).toBeVisible()
  checks.push('214-row batch groups existing/new/review/applied; reviewed 100 existing process without blocking on new employees; interrupted apply retries one stable request')
  checks.push('Component add/remove and reviewed fixed payroll divisor are editable without changing the payslip mapping')
  await page.getByRole('button', { name: 'Applied (105)', exact: true }).click()
  await page.getByRole('button', { name: 'View saved setup', exact: true }).first().click()
  await editor().getByRole('button', { name: /Verify saved payroll/ }).click()
  await expect.poll(() => simulations).toBe(100)
  await expect(editor().getByText('Saved payroll compared with Excel', { exact: true })).toBeVisible()
  assert.ok(maximumSimulations <= 4)
  await editor().getByRole('button', { name: 'Close', exact: true }).last().click()
  await page.getByRole('button', { name: 'Existing (100)', exact: true }).click()
  checks.push('Applied groups reopen for Employee simulation; verification of 100 employees uses at most four concurrent requests')
  await page.getByRole('button', { name: 'Configure next 100 existing', exact: true }).click()
  await expect(editor().getByRole('textbox', { name: 'Payroll setup Template name' })).not.toHaveValue(firstTemplate)
  await editor().getByRole('button', { name: 'Close', exact: true }).last().click()
  await page.getByRole('button', { name: 'New (8)', exact: true }).click()
  await page.getByRole('button', { name: 'Configure next 8 new', exact: true }).click()
  await expect(editor().getByRole('checkbox', { name: 'Create employee for row 202', exact: true })).not.toBeChecked()
  await editor().getByRole('button', { name: /Preview calculations/ }).click()
  await expect(editor().getByRole('checkbox', { name: 'Review payroll setup', exact: true })).toBeDisabled()
  await expect(editor().getByText(/explicitly allow creation/)).toBeVisible()
  await editor().getByRole('button', { name: 'Close', exact: true }).last().click()
  checks.push('Next cohort gets a unique template name; unmatched employee creation requires review and cannot silently insert a master')
  await page.getByText('Active master employees absent from this sheet (3)', { exact: true }).click()
  await page.getByRole('button', { name: 'Select all missing', exact: true }).click()
  await page.getByRole('button', { name: 'Mark as resigned (3)', exact: true }).click()
  const modal = page.getByRole('dialog', { name: 'Mark selected employees as resigned', exact: true })
  await expect(modal.getByRole('textbox', { name: 'Last working date OLD9001', exact: true })).toHaveValue('2026-08-31')
  await expect(modal.getByRole('textbox', { name: 'Last working date OLD9002', exact: true })).toHaveValue('2026-09-30')
  await expect(modal.getByRole('textbox', { name: 'Last working date OLD9003', exact: true })).toHaveValue('')
  await expect(modal.getByRole('button', { name: /Mark as resigned/ })).toBeDisabled()
  await modal.getByRole('textbox', { name: 'Last working date OLD9003', exact: true }).fill('2026-09-15')
  await expect.poll(() => modal.evaluate(element => getComputedStyle(element).animationName)).toBe('none')
  await expect(modal.getByRole('button', { name: /Mark as resigned/ })).toBeInViewport()
  await page.screenshot({ path: path.join(output, 'resignation-defaults.png'), fullPage: true })
  await modal.getByRole('button', { name: /Mark as resigned/ }).click()
  await expect(modal).toHaveCount(0)
  await expect(page.getByText('Active master employees absent from this sheet (3)', { exact: true })).toHaveCount(0)
  checks.push('Missing rows require explicit selection; mixed latest-payroll month-end defaults remain per employee; no-history date stays blank; reviewed dates submit separately')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    const drawer = page.getByRole('dialog').filter({ has: page.getByText('Master setup · 2026-09', { exact: true }) })
    await expect.poll(async () => drawer.evaluate(element => { const r = element.closest('.ant-drawer-content-wrapper').getBoundingClientRect(); return r.x >= -1 && r.right <= innerWidth + 1 })).toBe(true)
    await expect(page.getByRole('button', { name: 'Configure next 8 new', exact: true })).toBeInViewport()
    await drawer.locator('.ant-drawer-body').evaluate(el => { el.scrollTop = el.scrollHeight })
    await page.screenshot({ path: path.join(output, `readiness-${width}.png`), fullPage: true })
  }
  checks.push('Desktop/tablet/mobile readiness drawer keeps cohort actions visible with internal scrolling')
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, mockedMutations: writes.length, actualDatabaseWrites: 0, actualEmails: 0 }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, output }))
} catch (error) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); console.error(errors); throw error }
finally { fs.rmSync(fixturePath, { force: true }); await browser.close() }
