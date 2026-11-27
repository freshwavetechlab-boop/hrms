// Run against the built Admin UI. All HTTP requests are intercepted locally.
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'

const origin = 'https://simulator.example.test'
const dist = new URL('../payroll-ui/dist/', import.meta.url)
const client = { id: 42, code: 'SYN', name: 'Synthetic client', isActive: true, activeEmployeeCount: 2 }
const employees = [{ id: 101, code: 'SYN-1', name: 'Calendar employee' }, { id: 102, code: 'SYN-2', name: 'Monthly employee' }]
const component = { id: '1', code: 'BASIC', name: 'Basic', category: 'Earning', calculationType: 'Fixed Amount', value: '3900', active: true, proRata: true, payType: 'Fixed Pay' }
const template = { id: '10', clientId: '42', name: 'Test payroll divisor', annualCtc: '', active: true,
  excelSource: { payPeriod: '2026-10', payrollDays: 25 }, lines: [{ componentId: '1' }] }
const browser = await chromium.launch(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {})
const context = await browser.newContext({ serviceWorkers: 'block' })
const unexpectedWrites = [], errors = []

function simulate(request) {
  const employee = employees.find(item => item.id === request.employeeId)
  const [year, month] = request.payPeriod.split('-').map(Number)
  const saved = employee?.id === 102 && request.payPeriod === '2026-11' ? 26 : null
  const divisor = request.payrollDays ?? saved ?? new Date(Date.UTC(year, month, 0)).getUTCDate()
  const present = request.presentDays ?? 0, payable = request.payableDays ?? 4
  const earned = Math.round(3900 * payable / divisor * 100) / 100
  return { mode: request.mode, clientId: client.id, employeeId: employee?.id, employeeCode: employee?.code,
    employeeName: employee?.name, payPeriod: request.payPeriod, templateId: template.id, complete: true, coverage: '', issues: [],
    inputs: [{ name: 'PAYROLL_DAYS', value: divisor, source: saved ? 'Monthly snapshot' : 'Calendar month' },
      { name: 'PRESENT_DAYS', value: present }, { name: 'PAYABLE_DAYS', value: payable }],
    attendanceEvidence: { sourceKind: saved ? 'Monthly snapshot' : 'Legacy daily attendance', workingDays: saved ?? 4, presentDays: present, payableDays: payable },
    lines: [{ componentId: component.id, code: 'BASIC', name: 'Basic', category: 'Earning', formula: '3900',
      raw: 3900, monthly: 3900, earned, source: 'Template', inputs: {} }], gross: earned, deductions: 0, benefits: 0, net: earned }
}

try {
  await context.route('**/*', async route => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname
    if (!path.startsWith('/api/')) {
      if (url.origin !== origin) return route.abort()
      if (path === '/settings/salary-templates') return route.fulfill({ path: fileURLToPath(new URL('index.html', dist)) })
      if (/^\/assets\/[\w.-]+$/.test(path)) return route.fulfill({ path: fileURLToPath(new URL(path.slice(1), dist)) })
      return route.fulfill({ status: 404, body: '' })
    }
    const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true',
      'access-control-allow-methods': 'GET,POST,OPTIONS', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    const reply = json => route.fulfill({ json, headers })
    if (req.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (path === '/api/auth/me') return reply({ id: 1, displayName: 'Editor', roles: ['super_admin'], permissions: ['security.manage', 'settings.manage'], clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [], isActive: true })
    if (path === '/api/clients') return reply([client])
    if (path === '/api/organization') return reply({ id: 1, name: 'Synthetic' })
    if (path === '/api/setup') return reply({ salaryComponents: [component], salaryStructures: [template] })
    if (path === '/api/payroll-calculation/employees') return reply({ items: employees, total: employees.length })
    if (path === '/api/payroll-calculation/simulate') return reply(simulate(req.postDataJSON()))
    if (path === '/api/payroll-calculation/catalogue') return reply([{ token: 'PAYROLL_DAYS', meaning: 'Payroll divisor', availability: 'Available' }])
    if (req.method() !== 'GET') unexpectedWrites.push(path)
    return reply([])
  })
  const page = await context.newPage()
  page.setDefaultTimeout(8000)
  page.on('pageerror', error => errors.push(error.message))
  await page.goto(origin + '/settings/salary-templates?clientId=42')
  await page.getByRole('combobox', { name: 'Salary template client' }).click()
  await page.locator('.ant-select-item-option-content').getByText(client.name, { exact: true }).click()
  await page.getByRole('button', { name: 'Simulate', exact: true }).first().click({ timeout: 10000 })
  const dialog = page.getByRole('dialog')
  const divisor = dialog.getByLabel('Simulation Payroll divisor', { exact: true })
  const payMonth = dialog.getByLabel('Employee simulation month')
  const totals = dialog.getByLabel('Selected simulation totals')
  await payMonth.fill('2026-11')
  await dialog.getByRole('combobox', { name: 'Simulation employees' }).click()
  for (const employee of employees) await page.locator('.ant-select-item-option-content').getByText(employee.code + ' · ' + employee.name, { exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(divisor).toHaveValue('30')
  await expect(totals).toContainText('520')
  await divisor.fill('32')
  await expect(dialog.getByRole('button', { name: /Preview payslip/ })).toBeDisabled()
  await divisor.fill('24')
  await expect(totals).toContainText('650')
  await dialog.getByRole('button', { name: /SYN-2 Monthly employee/ }).click()
  await expect(divisor).toHaveValue('26')
  await expect(totals).toContainText('600')
  await dialog.getByRole('button', { name: /SYN-1 Calendar employee/ }).click()
  await expect(divisor).toHaveValue('24')
  await divisor.fill('')
  await divisor.blur()
  await expect(divisor).toHaveValue('30')
  await expect(totals).toContainText('520')
  await divisor.fill('24')
  await expect(totals).toContainText('650')
  await dialog.getByRole('button', { name: 'Reset to saved inputs' }).click()
  await expect(divisor).toHaveValue('30')
  await payMonth.fill('2026-12')
  await expect(divisor).toHaveValue('31')
  await payMonth.fill('2028-02')
  await expect(divisor).toHaveValue('29')
  await payMonth.fill('2027-02')
  await expect(divisor).toHaveValue('28')
  await dialog.getByText('Current template what-if', { exact: true }).click()
  await page.locator('.ant-select-item-option-content').getByText('Saved employee payroll', { exact: true }).click()
  await payMonth.fill('2026-11')
  await expect(totals).toContainText('520')
  await divisor.fill('24')
  await expect(totals).toContainText('650')
  await dialog.getByRole('button', { name: 'Try template draft instead' }).click()
  const draftMonth = dialog.getByLabel('Template simulation month')
  await draftMonth.fill('2026-10')
  await expect(divisor).toHaveValue('25')
  await draftMonth.fill('2026-11')
  await expect(divisor).toHaveValue('30')
  await dialog.getByLabel('Simulation Present days').fill('4')
  await divisor.fill('24')
  await expect(totals).toContainText('650')
  await draftMonth.fill('2028-02')
  await expect(divisor).toHaveValue('29')
  await divisor.fill('26')
  await divisor.fill('')
  await divisor.blur()
  await expect(divisor).toHaveValue('29')
  assert.deepEqual(unexpectedWrites, [])
  assert.deepEqual(errors, [])
  console.log('Simulator divisor passed calendar, saved monthly, manual, clear/reset, employee isolation, proration and draft month checks.')
} catch (error) {
  console.error(await context.pages()[0]?.locator('body').innerText(), errors)
  throw error
} finally { await browser.close() }
