// Run after the Admin UI production build. All requests are intercepted in a new browser context.
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'

const origin = 'https://employee-master.example.test'
const dist = new URL('../payroll-ui/dist/', import.meta.url)
const client = { id: 42, code: 'SYN', name: 'Synthetic client', isActive: true, activeEmployeeCount: 1 }
const employee = {
  id: 101, clientId: client.id, employeeCode: 'SYN-101', firstName: 'Synthetic', lastName: 'Employee',
  workEmail: 'employee@example.test', department: 'Operations', designation: 'Tester', grade: '',
  dateOfJoining: '2026-10-01', salaryStructureId: '', salaryJson: '{"1":12000}', annualCtc: 144000,
  salaryComponents: { 1: 12000 }, personalJson: '{}', paymentJson: '{}', workLocationId: 0, isActive: true,
}
const browser = await chromium.launch({ ...(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {}) })
const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, serviceWorkers: 'block' })
const posts = [], browserErrors = []
const saving = Promise.withResolvers(), reloading = Promise.withResolvers()
let delayReload = false, holdSave = false, didReload = false
try {
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname
    if (!path.startsWith('/api/')) {
      if (url.origin !== origin) return route.abort()
      if (path === '/employees/master') return route.fulfill({ path: fileURLToPath(new URL('index.html', dist)) })
      if (/^\/assets\/[\w.-]+$/.test(path)) return route.fulfill({ path: fileURLToPath(new URL(path.slice(1), dist)) })
      return route.fulfill({ status: 404, body: '' })
    }
    const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true',
      'access-control-allow-methods': 'GET,POST,OPTIONS', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    const reply = (json, status = 200) => route.fulfill({ json, status, headers })
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (path === '/api/auth/me') return reply({ id: 1, email: 'editor@example.test', roles: ['super_admin'],
      displayName: 'Synthetic editor', isActive: true, permissions: ['security.manage', 'employees.view', 'employees.manage', 'settings.manage'],
      clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [] })
    if (path === '/api/clients' || path === '/api/employees/clients') return reply([client])
    if (path === '/api/employees' && request.method() === 'GET') {
      if (delayReload) { didReload = true; await reloading.promise }
      return reply([employee])
    }
    if (path === '/api/employees' && request.method() === 'POST') {
      const body = request.postDataJSON()
      posts.push({ body, query: url.searchParams })
      if (posts.length === 1) return reply({ error: 'Synthetic code already exists.' }, 409)
      if (posts.length === 2) return reply({ id: employee.id, employeeCode: employee.employeeCode })
      if (holdSave) await saving.promise
      employee.employeeCode = body.employeeCode
      delayReload = true
      return reply({ id: employee.id, employeeCode: employee.employeeCode })
    }
    if (path === '/api/employees/field-configuration/catalog') return reply({ fields: [], forms: [], values: {} })
    if (path === '/api/organization') return reply({ id: 1, name: 'Synthetic organization' })
    if (path === '/api/setup') return reply({ salaryComponents: [], salaryStructures: [] })
    return reply([])
  })
  const page = await context.newPage()
  page.on('pageerror', error => browserErrors.push(error.message))
  await page.goto(origin + '/employees/master?clientId=42')
  const edit = page.getByRole('button', { name: 'Edit', exact: true }).first()
  await edit.click({ timeout: 12000 })
  const dialog = page.getByRole('dialog', { name: 'Employee details' })
  const input = dialog.getByRole('textbox', { name: /Employee code/i, exact: true })
  await expect(input).toHaveValue('SYN-101')
  await expect(dialog.getByRole('button', { name: 'Save infotype' })).toBeVisible()
  await input.fill('SYN-101A')
  const update = dialog.getByRole('button', { name: 'Update', exact: true })
  await expect(update).toHaveCSS('background-color', 'rgb(250, 204, 21)')
  await input.fill('SYN-101')
  await expect(dialog.getByRole('button', { name: 'Save infotype' })).toBeVisible()
  await input.fill('SYN-101A')
  await update.click()
  await expect(dialog.getByRole('alert')).toContainText('Synthetic code already exists.')
  await expect(input).toHaveValue('SYN-101A')
  await update.click()
  await expect(dialog.getByRole('alert')).toContainText('The API did not confirm the change.')
  await expect(input).toHaveValue('SYN-101A')
  const updated = page.getByText('Employee code updated to SYN-101A.', { exact: true })
  await expect(updated).toHaveCount(0)
  holdSave = true
  await update.evaluate(button => { button.click(); button.click() })
  await expect.poll(() => posts.length).toBe(3)
  await expect(dialog).toHaveAttribute('aria-busy', 'true')
  for (const { body, query } of posts) {
    assert.deepEqual(body, { id: employee.id, clientId: client.id, employeeCode: 'SYN-101A' })
    assert.equal(query.get('infotypeCode'), '0001')
    assert.equal(query.get('employeeCodeOnly'), 'true')
    assert.equal(query.get('expectedEmployeeCode'), 'SYN-101')
  }
  saving.resolve()
  await expect(updated).toBeVisible()
  await expect(dialog).toHaveCount(0)
  await expect.poll(() => didReload).toBe(true)
  await expect(page.getByText('SYN-101A', { exact: true }).first()).toBeVisible()
  reloading.resolve()
  await edit.click()
  await expect(input).toHaveValue('SYN-101A')
  await expect(dialog.getByRole('button', { name: 'Save infotype' })).toBeVisible()
  assert.deepEqual(browserErrors, [])
  console.log('Employee Master code edit passed: dirty/undo, conflict, guarded retry, payload, confirmed receipt and roster refresh.')
} finally {
  saving.resolve()
  reloading.resolve()
  await browser.close()
}
