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
const output = path.join(os.tmpdir(), 'frevo-excel-bootstrap-ui-20261017')
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-bootstrap-${crypto.randomUUID()}.tsx`
const fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Employee from '/src/pages/EmployeePage.tsx';
import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
const master=new URLSearchParams(location.search).has('master');
ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title={master?'Employees':'Excel Payslips'} description="" icon={null} breadcrumbs={[]} workspace={master?'employees':'excel-payslips'} /><main>{master?<Employee/>:<Excel/>}</main></div></AuthGate></ToastProvider></BrowserRouter>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const user = { id: 1, clientId: 33, displayName: 'Synthetic admin', roles: ['client_admin'], permissions: ['employees.manage', 'payroll.run', 'settings.manage'], isActive: true }
const employees = Array.from({ length: 26 }, (_, i) => ({ id: i + 1, clientId: 33, employeeCode: `TEST${String(i + 1).padStart(3, '0')}`, firstName: 'Employee', lastName: String(i + 1).padStart(3, '0'), isActive: i !== 1, employmentStatus: i === 1 ? 'Resigned' : 'Active', lastWorkingDate: i === 1 ? '2026-08-31' : null, department: 'Operations', designation: 'Executive', workLocationId: 1, grade: 'G1', workEmail: '', personalDetails: { skillCategory: 'Skilled' }, paymentDetails: {}, salaryComponents: {} }))
const batch = { id: 'a'.repeat(32), clientId: 33, clientName: 'Synthetic Client', month: '2026-09', createdAtUtc: '2026-10-17T10:00:00Z', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1, rows: employees.map((e, i) => ({ id: `row-${e.id}`, sourceRow: i + 2, employeeName: `${e.firstName} ${e.lastName}`, employeeCode: e.employeeCode, email: '', information: [], earnings: [{ label: 'Basic', amount: 10000 }], deductions: [{ label: 'PF', amount: 1200 }], employerContributions: [], netPay: 8800, warnings: [], employment: { employeeId: e.id, employeeCode: e.employeeCode, status: e.employmentStatus, lastWorkingDate: e.lastWorkingDate, blockedForMonth: i === 1, identityReviewRequired: false } })) }
Object.assign(batch.rows[2].employment, { employeeId: null, identityReviewRequired: true })
const source = { sheetName: 'Salary', headerRow: 1, columnCount: 6, columns: ['Name', 'Basic', 'Month days', 'Working Day', 'PF', 'Net'].map((name, i) => ({ columnIndex: i, sourceHeader: name, label: name, kind: ['employeeName', 'earning', 'info', 'info', 'deduction', 'netPay'][i] })), rows: batch.rows.map(r => ({ sourceRow: r.sourceRow, cells: [{ value: r.employeeName }, { value: '10000' }, { value: '30' }, { value: '30' }, { value: '1200', formulaText: `B${r.sourceRow}*12%` }, { value: '8800' }] })), excludedSourceRows: [] }
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
const page = await context.newPage(), errors = [], checks = [], writes = []
let rejectExit = true
page.on('pageerror', e => errors.push(e.message))
await context.route('**/*', async route => {
  const req = route.request(), url = new URL(req.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/bootstrap-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/api/auth/me') return reply(user)
  if (p === '/api/employees/resignation-defaults') return reply(req.postDataJSON().employeeIds.map(employeeId => ({ employeeId, lastPayrollMonth: '2026-09', lastWorkingDate: '2026-09-30' })))
  if (p === '/api/employees/mark-resigned' && req.method() === 'POST') {
    const body = req.postDataJSON(); writes.push(body)
    assert.equal(body.clientId, 33); assert.deepEqual(body.employeeIds, [1]); assert.equal(body.lastWorkingDate, '2026-09-30')
    if (rejectExit) return reply({ error: 'Review existing payroll before changing employment dates.' }, 400)
    Object.assign(employees[0], { isActive: false, employmentStatus: 'Resigned', lastWorkingDate: body.lastWorkingDate })
    Object.assign(batch.rows[0].employment, { status: 'Resigned', lastWorkingDate: body.lastWorkingDate })
    return reply({ marked: 1, alreadyMarked: 0, lastWorkingDate: body.lastWorkingDate, employeeIds: [1] })
  }
  if (req.method() !== 'GET') { errors.push(`Unexpected mutation ${req.method()} ${p}`); return reply({ error: 'Fixture blocks this write' }, 403) }
  if (p.endsWith('/clients') || p === '/api/clients') return reply([{ id: 33, code: 'TEST', name: 'Synthetic Client', isActive: true }])
  if (p === '/api/employees') return reply(employees)
  if (p === '/api/work-locations') return reply([{ id: 1, clientId: 33, name: 'Synthetic office', state: 'Uttar Pradesh', isActive: true }])
  if (p === '/api/setup') return reply({ salaryStructures: [], salaryComponents: [] })
  if (p.includes('field-configuration/catalog')) return reply({ forms: [], fields: [], values: {} })
  if (p.endsWith('/context') && p.includes('payroll-setup')) return reply({ source, employees: employees.map(e => ({ id: e.id, employeeCode: e.employeeCode, employeeName: `${e.firstName} ${e.lastName}`, workLocationId: 1 })), roster: { rows: batch.rows.map(row => ({ rowId: row.id, sourceRow: row.sourceRow, employeeName: row.employeeName, employeeCode: row.employeeCode, status: row.employment.identityReviewRequired ? 'Review' : 'Existing', employeeId: row.employment.employeeId, message: '' })), missingEmployees: [] }, components: [], workWeeks: [{ value: 'Friday off', label: 'Friday off' }], workLocations: [{ id: 1, name: 'Synthetic office' }], defaults: { department: 'Operations', designation: 'Executive', grade: 'G1', workWeek: 'Friday off', attendanceCycleStartDay: 1, attendanceCycleEndDay: 30, payrollReportGenerationDay: 3, attendanceSource: 'Existing client policy: Operations', notes: ['Existing settings retained'] } })
  if (p.endsWith('/delivery-status')) return reply({ items: batch.rows.map(row => ({ rowId: row.id, status: 'Pending', email: '', canSend: true, message: '' })) })
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  if (p === '/api/excel-payslips/batches') return reply([{ ...batch, rows: undefined, rowCount: batch.rows.length }])
  if (p === `/api/excel-payslips/batches/${batch.id}`) return reply(batch)
  return reply([])
})
async function openBatch() {
  await page.goto(origin + '/bootstrap-fixture')
  await page.getByRole('button', { name: 'Batch history', exact: true }).click()
  await page.getByRole('button', { name: 'Open batch', exact: true }).click()
  await expect(page.getByText('Employee 001', { exact: true })).toBeVisible()
}
try {
  await openBatch()
  await expect(page.locator('tr.employee-exit-record').filter({ hasText: 'Employee 002' })).toBeVisible()
  await expect(page.locator('tr').filter({ hasText: 'Employee 002' }).getByRole('button', { name: 'Preview', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: /Email batch \(25\)/ })).toBeEnabled()
  checks.push('Future exited row stays visible red; preview and batch email eligibility exclude it')
  await page.locator('tr').filter({ hasText: 'Employee 003' }).getByRole('checkbox').check()
  await expect(page.getByRole('button', { name: 'Mark as resigned', exact: true })).toBeDisabled()
  await page.locator('tr').filter({ hasText: 'Employee 003' }).getByRole('checkbox').uncheck()
  await page.locator('tr').filter({ hasText: 'Employee 001' }).getByRole('checkbox').check()
  await page.getByRole('button', { name: 'Configure payroll', exact: true }).click()
  await page.getByRole('button', { name: 'Configure next 1 existing', exact: true }).click()
  await expect(page.getByText('Existing client policy: Operations', { exact: true }).first()).toBeVisible()
  await page.getByText('Employee Master matches', { exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Payroll setup Default department' })).toHaveValue('Operations')
  await page.getByText('Client defaults and master creation', { exact: true }).click()
  await expect(page.getByRole('checkbox', { name: /Preserve existing attendance/ })).toBeChecked()
  await page.getByRole('button', { name: 'Close', exact: true }).last().click()
  await page.getByRole('button', { name: 'Close', exact: true }).last().click()
  await expect(page.locator('.excel-payroll-setup')).not.toBeVisible()
  checks.push('Configure payroll preloads this client defaults and preserves existing memberships')
  await page.getByRole('button', { name: 'Mark as resigned (1)', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Mark selected employees as resigned', exact: true })
  await expect(dialog.getByRole('textbox', { name: 'Last working date', exact: true })).toHaveValue('2026-09-30')
  await dialog.getByRole('textbox', { name: 'Last working date' }).fill('2026-09-30')
  await dialog.getByRole('button', { name: /Mark as resigned/ }).click()
  await expect(dialog).toContainText('Review existing payroll')
  rejectExit = false
  await dialog.getByRole('button', { name: /Mark as resigned/ }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.locator('tr.employee-exit-record').filter({ hasText: 'Employee 001' })).toBeVisible()
  await expect(page.locator('tr').filter({ hasText: 'Employee 001' }).getByRole('button', { name: 'Preview', exact: true })).toBeEnabled()
  checks.push('Required date, server validation, success refresh and historical-month preview verified with mocked writes')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await page.getByText('Cards', { exact: true }).click()
    await expect(page.locator('.shared-record-cards article.employee-exit-record')).toHaveCount(2)
    await page.getByRole('checkbox', { name: 'Select record row-4' }).check()
    await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeEnabled()
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false)
    await page.screenshot({ path: path.join(output, `excel-cards-${width}.png`), fullPage: true })
    await page.getByText('Table', { exact: true }).click()
    const body = page.locator('.ant-table-body').first()
    await body.evaluate(el => { el.scrollTop = el.scrollHeight; el.scrollLeft = el.scrollWidth })
    await expect(page.getByRole('button', { name: 'Batch history', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `excel-table-${width}.png`), fullPage: true })
  }
  checks.push('Card selection, red exit cards, bounded table scrolling and persistent actions at desktop/tablet/mobile')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(origin + '/bootstrap-fixture?master=1')
  await expect(page.getByText('TEST001', { exact: true })).toBeVisible()
  await expect(page.locator('tr.employee-exit-record')).toHaveCount(2)
  await page.getByRole('button', { name: 'Select employees', exact: true }).click()
  const departed = page.locator('tr').filter({ hasText: 'TEST001' })
  await expect(departed.getByRole('checkbox')).toBeDisabled()
  await page.locator('tr').filter({ hasText: 'TEST003' }).getByRole('checkbox').check()
  await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeEnabled()
  await page.getByText('Card view', { exact: true }).click()
  await expect(page.locator('.recruitment-record-card.employee-exit-record')).toHaveCount(2)
  await page.screenshot({ path: path.join(output, 'master-cards.png'), fullPage: true })
  for (const [width, height] of [[768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false)
    await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `master-cards-${width}.png`), fullPage: true })
    await page.getByText('Table view', { exact: true }).click()
    await expect(page.locator('tr.employee-exit-record')).toHaveCount(2)
    await page.screenshot({ path: path.join(output, `master-table-${width}.png`), fullPage: true })
    await page.getByText('Card view', { exact: true }).click()
  }
  checks.push('Employee Master preserves exited history and disables selecting already resigned employees in table/card views')
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, mockedWrites: writes.length, actualDatabaseWrites: 0, actualEmails: 0 }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  console.error(errors)
  console.error(JSON.stringify(await page.locator('[role="dialog"]').evaluateAll(nodes => nodes.map(node => ({ hidden: node.getAttribute('aria-hidden'), buttons: [...node.querySelectorAll('button')].map(button => ({ text: button.textContent, disabled: button.disabled, hidden: button.getAttribute('aria-hidden'), icons: [...button.querySelectorAll('[aria-label]')].map(icon => icon.getAttribute('aria-label')) })) }))), null, 2))
  throw error
} finally { fs.rmSync(fixturePath, { force: true }); await browser.close() }
