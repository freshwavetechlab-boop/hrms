// Isolated UI smoke: every API response is a fixture; no client data is changed.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { chromium, expect } = require(process.env.PLAYWRIGHT_MODULE || '@playwright/test')
const base = process.env.UI_URL || 'http://localhost:5173'
const output = '.codex-validation/client-config-check'
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
const calls = [], errors = []
let setupUnavailable = false
const clients = [{ id: 1, name: 'Alpha Test', code: 'A', isActive: true }, { id: 2, name: 'Beta Test', code: 'B', isActive: true }]
const employee = (id, clientId, annualCtc) => ({ id, clientId, employeeCode: `EMP${id}`, firstName: 'Test', lastName: 'Employee', isActive: true, annualCtc, salaryStructureId: '', salaryComponents: {}, workLocationId: 0, department: '', designation: '' })
const setup = { salaryComponents: [{ id: 1, code: 'BASIC', active: true }], salaryStructures: [], payslipTemplates: [], statutory: {}, tax: { clientSettings: [], slabs: [] } }
const fixtures = {
  '/api/clients': clients,
  '/api/employees': [employee(1, 1, 0), employee(2, 2, 240000)],
  '/api/setup': setup,
  '/api/client-billing/module': { isEnabled: false, advancedCostingEnabled: false },
  '/api/client-billing/advanced': { headers: [], lines: [] },
  '/api/travel-expense/setup': { clientSettings: [], policies: [], assignments: [], rules: [], categories: [], audit: [] },
  '/api/recruitment-admin': { settings: [], masters: [], consultants: [], vendors: [], assignmentRules: [], slaRules: [], documentChecklist: [], approvalMappings: [], templates: [] },
  '/api/notifications/setup': { smtp: { isEnabled: false, deliveryPaused: true }, templates: [], rules: [] },
  '/api/leave-attendance/attendance-settings': null
}
page.on('pageerror', error => errors.push(error.message))
await page.route('**/api/**', async route => {
  const url = new URL(route.request().url())
  calls.push(url.pathname)
  await route.fulfill({ status: setupUnavailable && url.pathname === '/api/setup' ? 403 : 200,
    headers: { 'access-control-allow-origin': base, 'access-control-allow-credentials': 'true' }, contentType: 'application/json',
    body: JSON.stringify(setupUnavailable && url.pathname === '/api/setup' ? { message: 'Setup access denied' } : Object.hasOwn(fixtures, url.pathname) ? fixtures[url.pathname] : []) })
})
await page.route(`${base}/__config-check-smoke`, route => route.fulfill({ contentType: 'text/html', body: `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root" style="padding:20px"></div><script type="module">
import RefreshRuntime from '/@react-refresh'; RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => type => type; window.__vite_plugin_react_preamble_installed__ = true;
import React from '/node_modules/.vite/deps/react.js';
import ReactDOM from '/node_modules/.vite/deps/react-dom_client.js';
import '/node_modules/antd/dist/reset.css'; import '/src/index.css'; import '/src/AppShell.css';
import theme from '/src/theme/hrmsTheme.ts';
const source = await (await fetch('/src/components/ClientConfigCheck.tsx')).text();
const dependency = name => source.match(new RegExp('from "([^"\\n]*' + name + '[^"\\n]*)"'))[1];
const { BrowserRouter } = await import(dependency('react-router-dom'));
const { ConfigProvider } = await import(dependency('antd.js'));
const { default: Check } = await import('/src/components/ClientConfigCheck.tsx');
ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(ConfigProvider, { theme }, React.createElement(BrowserRouter, null, React.createElement(Check))));
</script></body></html>` }))
const choose = async name => {
  await page.locator('.config-check-client .ant-select-selector').click()
  await page.locator('.ant-select-item-option').filter({ hasText: name }).click()
}
const openCheck = async title => {
  await page.getByRole('tab', { name: 'Summary', exact: true }).click()
  await page.locator('.config-summary-grid button').filter({ hasText: title }).click()
}
try {
  await page.goto(`${base}/__config-check-smoke`)
  await expect(page.getByRole('button', { name: 'Run check' })).toBeDisabled()
  await expect.poll(() => calls.length).toBe(1)
  await choose('Alpha Test')
  assert.deepEqual(calls, ['/api/clients'])
  await page.getByRole('button', { name: 'Run check' }).click()
  await expect(page.getByText('1 configuration blocker(s) for payroll', { exact: true })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Summary', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('#employee-coverage')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Minimum requirements for payroll' })).toHaveCount(0)
  await page.getByText('Minimum requirements for regular payroll', { exact: true }).click()
  await expect(page.getByRole('region', { name: 'Minimum requirements for payroll' })).toBeVisible()
  await page.getByText('Minimum requirements for regular payroll', { exact: true }).click()
  const callsBeforeNavigation = calls.length
  await openCheck('Payroll Structure')
  await expect(page.locator('#config-payroll')).toContainText('Set a salary basis for 1 employee(s): EMP1')
  await expect(page.locator('#config-payroll')).not.toContainText('EMP2')
  await page.screenshot({ path: `${output}/payroll-tab-desktop.png`, fullPage: true, animations: 'disabled' })
  await openCheck('Travel & Expense')
  await expect(page.getByText('Module enabled for client: No.', { exact: true })).toBeVisible()
  await page.locator('.config-accordion-heading').filter({ hasText: 'Notifications & Jobs' }).click()
  await expect(page.locator('#config-travel')).not.toBeVisible()
  await expect(page.locator('#config-notifications')).toContainText('Salary calculation can continue while messages remain queued')
  assert.equal(calls.length, callsBeforeNavigation, 'Tabs and accordions do not refetch configuration')
  await page.getByRole('tab', { name: 'Summary', exact: true }).click()
  await page.screenshot({ path: `${output}/desktop.png`, fullPage: true, animations: 'disabled' })
  const requests = calls.length
  await choose('Beta Test')
  await expect(page.getByText('Configuration not checked. Click Run check.')).toBeVisible()
  assert.equal(calls.length, requests)
  await page.getByRole('button', { name: 'Run check' }).click()
  await expect(page.getByText('No configuration blockers detected; pay-period validation still required', { exact: true })).toBeVisible()
  await openCheck('Payroll Structure')
  await expect(page.locator('#config-payroll')).toContainText('These employees pass the salary-basis presence check')
  setup.salaryStructures.push({ id: 88, clientId: '1:Alpha;2:Beta', name: 'Shared template', active: true, lines: [{ componentId: '1', calculationType: 'Fixed Amount', value: '20000' }] })
  fixtures['/api/employees'][1].salaryStructureId = '88'
  await page.getByRole('button', { name: 'Run check' }).click()
  await openCheck('Payroll Structure')
  await expect(page.locator('#config-payroll')).toContainText('1 active salary template for this client')
  await expect(page.locator('#config-payroll')).not.toContainText('unavailable/inactive template assignments')
  await page.getByRole('tab', { name: 'Summary', exact: true }).click()
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: `${output}/mobile.png`, fullPage: true })
  await page.screenshot({ path: `${output}/mobile-viewport.png` })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'No horizontal page overflow')
  await page.setViewportSize({ width: 1440, height: 1000 })
  fixtures['/api/employees'] = Array.from({ length: 100 }, (_, index) => employee(index + 1, 1, 240000))
  fixtures['/api/employees'].push({ ...employee(101, 1, 0), isActive: false }, employee(102, 2, 0))
  fixtures['/api/leave-attendance/groups'] = [{ id: 1, clientId: 1, name: 'Attendance standard', isActive: true, employeeIds: Array.from({ length: 80 }, (_, index) => index + 1) }]
  await choose('Alpha Test')
  await page.getByRole('button', { name: 'Run check' }).click()
  await openCheck('Shift & Attendance Policy')
  await page.locator('#config-attendance').getByRole('button', { name: 'View employee coverage' }).first().click()
  await expect(page.getByRole('tab', { name: 'Employee coverage', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('button', { name: 'Attendance policy: 80 covered employees', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Attendance policy: 20 uncovered employees', exact: true })).toBeVisible()
  await expect(page.locator('#employee-coverage')).toContainText('101 employee records; 100 active employees checked; 1 inactive excluded')
  await page.getByRole('button', { name: 'Attendance policy: 20 uncovered employees', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('No active attendance policy membership')
  await expect(dialog.getByText('EMP100', { exact: true })).toBeVisible()
  await expect(dialog.getByText('EMP1', { exact: true })).toHaveCount(0)
  await dialog.getByPlaceholder('Search table...').fill('EMP100')
  await expect(dialog.getByText('EMP100', { exact: true })).toBeVisible()
  await expect(dialog.getByText('EMP99', { exact: true })).toHaveCount(0)
  const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('button', { name: /Export Excel/ }).click()])
  assert.match(download.suggestedFilename(), /client-coverage-attendance-coverage-uncovered.*\.xlsx$/)
  await page.screenshot({ path: `${output}/coverage-employees-desktop.png`, fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'No mobile coverage overflow')
  await page.screenshot({ path: `${output}/coverage-employees-mobile.png` })
  await dialog.getByRole('button', { name: 'Close', exact: true }).click()
  setupUnavailable = true
  await page.getByRole('button', { name: 'Run check' }).click()
  await openCheck('Payroll Structure')
  await expect(page.locator('.config-accordion-heading').filter({ hasText: 'Payroll Structure' })).toContainText('Unavailable')
  await expect(page.locator('#config-payroll')).toContainText('Setup access denied')
  await expect(page.getByText('Configuration check incomplete', { exact: true })).toBeVisible()
  await expect(page.locator('#config-payroll')).not.toContainText('Create salary components')
  await page.screenshot({ path: `${output}/payroll-tab-mobile.png`, fullPage: true, animations: 'disabled' })
  assert.deepEqual(errors, [])
  console.log('PASS: on-demand checks, client isolation, payroll blockers, CTC alternative, optional modules, API errors, desktop/mobile layout')
} catch (error) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true })
  console.error(errors)
  throw error
} finally { await browser.close() }
