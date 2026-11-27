// Run after the Admin UI build. Requests and employee/client data are synthetic.
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'
import { catalogueFixture } from './payroll-calculation-fixtures.mjs'

const origin = 'https://salary-library.example.test'
const dist = new URL('../payroll-ui/dist/', import.meta.url)
const clients = [
  { id: 42, name: 'Selected client', code: 'SELECT', isActive: true },
  { id: 57, name: 'Another client', code: 'OTHER', isActive: true },
]
const master = (id, code, name, formula) => ({ id, code, name, formula, category: 'Earning', componentType: 'Custom Allowance',
  componentRole: 'Regular Earning', statutoryType: 'None', payType: 'Fixed Pay', calculationType: 'Formula',
  value: '', baseComponent: '', active: true, taxable: true, proRata: true, priority: '100' })
const masters = [master(1, 'BASIC', 'Basic', 'GROSS * 50%'), master(2, 'HRA', 'HRA', 'BASIC * 40%'), master(3, 'EXISTING', 'Existing pay', '19')]
let saved = { id: 10, clientId: '42', name: 'Selected payroll', annualCtc: '', active: true,
  lines: [{ componentId: '3', calculationType: 'Formula', formula: '12 + 3', value: '' }] }
const other = { ...saved, id: 20, name: 'Other payroll', clientId: '57', lines: [{ componentId: '1', value: '' }, { componentId: '2', value: '' }] }
const creates = [], templateUpdates = [], unexpectedWrites = [], errors = []
const browser = await chromium.launch(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {})
const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 1450, height: 950 } })

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
      'access-control-allow-methods': 'GET,POST,PUT,OPTIONS', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    const reply = json => route.fulfill({ headers, json })
    if (req.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (path === '/api/auth/me') return reply({ id: 1, isActive: true, displayName: 'Editor', roles: ['super_admin'],
      permissions: ['settings.manage', 'security.manage'], clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [] })
    if (path === '/api/organization') return reply({ id: 1, name: 'Synthetic' })
    if (path === '/api/clients') return reply(clients)
    if (path === '/api/setup' && req.method() === 'GET') return reply({ salaryComponents: masters, salaryStructures: [saved, other] })
    if (path.endsWith('/payroll-calculation/catalogue')) return reply(catalogueFixture(req.postDataJSON()))
    if (path === '/api/salary-components' && req.method() === 'POST') {
      const body = req.postDataJSON()
      creates.push(body)
      const created = { ...body.component, id: 911 }
      masters.push(created)
      return reply(created)
    }
    if (path === '/api/clients/42/salary-templates/10' && req.method() === 'PUT') {
      const body = req.postDataJSON()
      templateUpdates.push(body)
      saved = { ...saved, ...body.template }
      return reply(saved)
    }
    if (req.method() !== 'GET') unexpectedWrites.push(path)
    return reply([])
  })
  const page = await context.newPage()
  page.setDefaultTimeout(8000)
  page.on('pageerror', e => errors.push(e.message))
  await page.goto(origin + '/settings/salary-templates')
  await page.getByRole('combobox', { name: 'Salary template client' }).click()
  await page.locator('.ant-select-item-option-content').getByText(clients[0].name, { exact: true }).click()
  await page.getByRole('button', { name: 'Edit formulas', exact: true }).click()
  const formula = name => page.getByLabel(name + ' formula or value', { exact: true })
  await expect(formula('Existing pay')).toHaveValue('12 + 3')
  await page.getByLabel('Salary template name').fill('Edited draft')
  await page.getByRole('button', { name: /Add components$/ }).click()
  const library = page.getByLabel('Salary component library', { exact: true })
  await expect(library.getByRole('checkbox', { name: /Show masters/ })).toHaveCount(0)
  await expect(library.getByText(/Used by .+/)).toHaveCount(0)
  await library.getByRole('button', { name: 'Add Basic', exact: true }).click()
  await library.getByRole('button', { name: 'Drag HRA to template', exact: true })
    .dragTo(page.getByLabel('Template components', { exact: true }), { targetPosition: { x: 30, y: 35 } })
  for (const name of ['Basic', 'HRA']) {
    await expect(formula(name)).toHaveValue('0')
    await expect(formula(name)).toHaveAttribute('placeholder', '0')
  }
  await formula('HRA').fill('BASIC * 30%')
  await formula('HRA').fill('')
  await formula('HRA').blur()
  await expect(formula('HRA')).toHaveValue('0')
  await library.getByRole('button', { name: 'Deductions', exact: true }).click()
  const create = library.getByRole('button', { name: /Create deduction$/ })
  await create.click()
  const editor = page.locator('.salary-component-editor')
  await editor.getByLabel('Component name', { exact: true }).fill('Discard me')
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click()
  assert.equal(creates.length, 0)
  await expect(page.getByLabel('Salary template name')).toHaveValue('Edited draft')
  await library.getByRole('button', { name: 'All', exact: true }).click()
  await library.getByRole('button', { name: /Create component$/ }).click()
  await editor.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Category', exact: true }) }).click()
  const categories = page.locator('.ant-select-dropdown:visible .ant-select-item-option-content')
  await expect(categories.getByText('Employer Benefit', { exact: true })).toBeVisible()
  await categories.getByText('Deduction', { exact: true }).click()
  await editor.getByLabel('Component name', { exact: true }).fill('Loan instalment')
  await editor.getByLabel('Component code', { exact: true }).fill('LOAN_INSTALMENT')
  await editor.getByRole('button', { name: 'Save component', exact: true }).click()
  await library.getByRole('button', { name: 'Add Loan instalment', exact: true }).click()
  assert.equal(creates.length, 1)
  assert.equal(creates[0].component.category, 'Deduction')
  assert.equal(templateUpdates.length, 0)
  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(formula('Loan instalment')).toHaveValue('0')
  await page.getByRole('button', { name: 'Save template', exact: true }).click()
  await expect.poll(() => templateUpdates.length).toBe(1)
  assert.equal(saved.name, 'Edited draft')
  assert.deepEqual(saved.lines.map(l => String(l.componentId)), ['3', '1', '2', '911'])
  for (const line of saved.lines.slice(1)) assert.equal(line.formula || line.value, '0')
  assert.equal(saved.lines[0].formula, '12 + 3')
  await page.getByRole('button', { name: 'Edit formulas', exact: true }).click()
  await expect(formula('Basic')).toHaveValue('0')
  await expect(formula('Existing pay')).toHaveValue('12 + 3')
  assert.deepEqual(unexpectedWrites, [])
  assert.deepEqual(errors, [])
  console.log('Shared component discovery, zero defaults, inline creation/cancel and saved templates passed.')
} finally { await browser.close() }
