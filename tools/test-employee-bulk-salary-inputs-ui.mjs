// Start with a local Admin UI dev server. All API responses are synthetic.
import assert from 'node:assert/strict'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'
import { buildXlsxBlob } from '../payroll-ui/src/utils/xlsx.ts'

const origin = 'http://localhost:5173'
const client = { id: 42, code: 'TEST', name: 'Test client', isActive: true }
function earning(id, name, code) {
  return { id, name, code, category: 'Earning', componentType: 'Custom Allowance', componentRole: 'Regular Earning',
    statutoryType: 'None', calculationType: 'Fixed Amount', payType: 'Fixed Pay', value: '0',
    formula: '', baseComponent: '', proRata: true, active: true, priority: String(id) }
}
const components = [
  earning(10, 'Basic', 'BASIC'), earning(11, 'Telephone Allowance', 'TEL_ALLOW'),
  { ...earning(12, 'House Rent Allowance', 'HRA'), calculationType: 'Formula', formula: 'BASIC * 40%' },
  earning(13, 'Daily Wages', 'DAILY_WAGES'),
]
const line = (id, calculationType, value, formula = '') => ({ componentId: String(id), calculationType, value, formula })
const template = (id, clientId, name, lines) => ({ id, clientId: String(clientId), name, lines, active: true, annualCtc: '' })
const templates = [
  template(101, 42, 'Monthly contract', [line(10, 'Fixed Amount', '0'), line(11, 'Fixed Amount', '0'), line(12, 'Formula', '', 'BASIC * 40%')]),
  template(102, 42, 'Field contract', [line(13, 'Daily Rate', '100')]),
  template(103, 51, 'Other client contract', [line(10, 'Fixed Amount', '0')]),
]

const browser = await chromium.launch(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {})
const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 1440, height: 900 } })
const writes = []
try {
  await context.route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (!url.pathname.startsWith('/api/')) return url.origin === origin ? route.continue() : route.abort()
    const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true',
      'access-control-allow-methods': 'GET,OPTIONS', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (request.method() !== 'GET') { writes.push(url.pathname); return route.abort() }
    const reply = json => route.fulfill({ json, headers })
    switch (url.pathname) {
      case '/api/auth/me': return reply({ id: 1, isActive: true, roles: ['super_admin'], permissions: ['employees.view', 'employees.manage'],
        displayName: 'Test reviewer', clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [] })
      case '/api/clients':
      case '/api/employees/clients': return reply([client])
      case '/api/organization': return reply({ id: 1, name: 'Test organization' })
      case '/api/employees/field-configuration/catalog': return reply({ fields: [], forms: [], values: {} })
      case '/api/setup': return reply({ salaryStructures: templates, salaryComponents: components })
      default: return reply([])
    }
  })

  const page = await context.newPage()
  page.setDefaultTimeout(12000)
  await page.goto(origin + '/employees/master?clientId=42')
  await page.getByRole('button', { name: 'Bulk tools', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Bulk upload', exact: true }).click()
  const drawer = page.getByTestId('smart-bulk-drawer')
  const choices = () => page.locator('.ant-select-dropdown:visible .ant-select-item-option-content')
  await page.getByTestId('smart-bulk-operation').getByText('Add new employees only', { exact: true }).click()
  await choices().filter({ hasText: 'Update existing employees only' }).click()
  const book = buildXlsxBlob([{ name: 'Roster', rows: [
    ['Employee ID', 'Employee Code', 'First Name', 'Pay basis', 'TEL_ALLOW', 'HRA'],
    ['201', 'TEST-1', 'First', 20000, 0, 8000],
    ['202', 'TEST-2', 'Second', 21000, '', 8400],
    ['203', 'TEST-3', 'Third', '', '', 8500],
  ] }])
  await page.getByTestId('smart-bulk-file-input').setInputFiles({ name: 'salary-inputs.xlsx', mimeType: book.type, buffer: Buffer.from(await book.arrayBuffer()) })

  const templateSelect = container => container.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Salary template for mapping' }) })
  const salaryTemplate = templateSelect(drawer)
  await salaryTemplate.click()
  await expect(choices().filter({ hasText: 'Other client contract' })).toHaveCount(0)
  await choices().filter({ hasText: 'Field contract' }).click()
  const targets = drawer.getByTestId('smart-bulk-target-field')
  await expect(targets.filter({ hasText: 'Daily Wages' })).toHaveCount(1)
  await salaryTemplate.click()
  await choices().filter({ hasText: 'Monthly contract' }).click()
  await expect(targets.filter({ hasText: 'Daily Wages' })).toHaveCount(0)
  await expect(targets.filter({ hasText: 'House Rent Allowance' })).toHaveCount(0)
  await expect(targets.filter({ hasText: 'Telephone Allowance' })).toHaveCount(1)
  await expect(page.getByRole('button', { name: 'Review mapped data' })).toBeDisabled()

  await page.getByRole('button', { name: 'Preview, split & map' }).click()
  const sourcePreview = page.getByTestId('smart-bulk-source-preview')
  const mapBasic = async () => {
    const mapping = sourcePreview.getByRole('combobox', { name: 'Map Pay basis to HRMS field' })
    await sourcePreview.getByTestId('smart-bulk-preview-map-select').and(page.locator('[data-source-column="Pay basis"]')).click()
    await mapping.fill('Basic')
    await choices().filter({ hasText: /Basic/i }).click()
  }
  await mapBasic()
  const originalColumn = sourcePreview.getByTestId('smart-bulk-preview-column').and(page.locator('[data-source-column="Pay basis"]'))
  await expect(originalColumn).toHaveAttribute('data-mapped', 'true')
  const previewTemplate = templateSelect(sourcePreview)
  await previewTemplate.click()
  await choices().filter({ hasText: 'Field contract' }).click()
  await expect(originalColumn).toHaveAttribute('data-mapped', 'false')
  const codeColumn = sourcePreview.getByTestId('smart-bulk-preview-column').and(page.locator('[data-source-column="Employee Code"]'))
  await expect(codeColumn).toHaveAttribute('data-mapped', 'true')
  await previewTemplate.click()
  await choices().filter({ hasText: 'Monthly contract' }).click()
  await mapBasic()
  await sourcePreview.getByLabel('Salary effective from').fill('2026-10-01')
  await page.getByRole('button', { name: 'Use mapped preview' }).click()
  await page.getByRole('button', { name: 'Review mapped data' }).click()
  const review = page.getByTestId('smart-bulk-preview')
  await expect(review.getByLabel('Employee Code row 2')).toHaveValue('TEST-1')
  await expect(review.getByLabel('Employee Code row 3')).toHaveValue('TEST-2')
  await expect(review.getByLabel('Basic · Monthly amount [SALARY_INPUT:10] row 2', { exact: true })).toHaveValue('20000')
  await expect(review.getByLabel('Telephone Allowance · Monthly amount [SALARY_INPUT:11] row 2', { exact: true })).toHaveValue('0')
  await expect(review.getByLabel('Telephone Allowance · Monthly amount [SALARY_INPUT:11] row 3', { exact: true })).toHaveValue('')
  await expect(review.getByLabel('Salary Effective From row 2', { exact: true })).toHaveValue('2026-10-01')
  await expect(review.getByLabel('Salary Template Id row 3', { exact: true })).toHaveValue('101')
  await expect(review.getByLabel('Salary Effective From row 4', { exact: true })).toHaveValue('')
  await expect(review.getByLabel('Salary Template Id row 4', { exact: true })).toHaveValue('')
  await expect(review.getByLabel(/\[SALARY_INPUT:12\] row/i)).toHaveCount(0)
  await expect(page.getByTestId('bulk-preview-import')).toBeEnabled()
  const amount = review.getByLabel('Basic · Monthly amount [SALARY_INPUT:10] row 2', { exact: true })
  await amount.fill('20000x')
  await expect(page.getByTestId('bulk-preview-import')).toBeDisabled()
  await amount.fill('20000')
  await expect(page.getByTestId('bulk-preview-import')).toBeEnabled()
  assert.deepEqual(writes, [])
  console.log('Employee salary input mapping and review passed.')
} finally {
  await browser.close()
}
