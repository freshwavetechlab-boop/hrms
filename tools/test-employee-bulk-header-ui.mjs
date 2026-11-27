// Run against an existing local Admin UI dev server. Every API request is intercepted.
import assert from 'node:assert/strict'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'
import { buildXlsxBlob } from '../payroll-ui/src/utils/xlsx.ts'

const origin = 'http://localhost:5173'
const browser = await chromium.launch(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {})
const context = await browser.newContext({ serviceWorkers: 'block' })
const writes = []
try {
  await context.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (!url.pathname.startsWith('/api/')) return url.origin === origin ? route.continue() : route.abort()
    const method = route.request().method()
    const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true',
      'access-control-allow-methods': 'GET,OPTIONS', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    if (method === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (method !== 'GET') { writes.push(url.pathname); return route.abort() }
    const reply = json => route.fulfill({ json, headers })
    switch (url.pathname) {
      case '/api/auth/me': return reply({ id: 1, isActive: true, roles: ['super_admin'], permissions: ['employees.view', 'employees.manage'],
        displayName: 'Test editor', clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [] })
      case '/api/clients':
      case '/api/employees/clients': return reply([{ id: 42, code: 'TST', name: 'Test client', isActive: true }])
      case '/api/employees/field-configuration/catalog': return reply({ fields: [], forms: [], values: {} })
      case '/api/setup': return reply({ salaryComponents: [], salaryStructures: [] })
      case '/api/organization': return reply({ id: 1, name: 'Test organization' })
      default: return reply([])
    }
  })
  const page = await context.newPage()
  page.setDefaultTimeout(12000)
  await page.goto(origin + '/employees/master?clientId=42')
  await page.getByRole('button', { name: 'Bulk tools', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Bulk upload', exact: true }).click()
  const workbook = buildXlsxBlob([{ name: 'Roster', rowNumbers: [1, 2, 4, 7], rows: [
    ['Roster title'],
    ['Employee Code', 'First Name', 'Annual CTC'],
    ['TST-001', 'First', 120000],
    ['TST-002', 'Second', 240000],
  ] }])
  await page.getByTestId('smart-bulk-file-input').setInputFiles({ name: 'roster.xlsx',
    mimeType: workbook.type, buffer: Buffer.from(await workbook.arrayBuffer()) })
  const pickRow = async (selectorId, number) => {
    await page.getByTestId(selectorId).click()
    await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: new RegExp('^Row ' + number + '(?:\\D|$)', 'i') }).click()
  }
  const review = page.getByRole('button', { name: 'Review mapped data' })
  await pickRow('smart-bulk-header-row', 2)
  await expect(review).toBeEnabled()
  await pickRow('smart-bulk-header-row', 1)
  await expect(review).toBeDisabled()
  await page.getByRole('button', { name: 'Preview, split & map' }).click()
  await pickRow('smart-bulk-preview-header-row', 2)
  const sourceRows = page.getByTestId('smart-bulk-source-preview-table').locator('tbody tr')
  await expect(sourceRows).toHaveCount(2)
  await expect(sourceRows.locator('td:first-child')).toHaveText(['4', '7'])
  await expect(sourceRows.locator('td:nth-child(2)')).toHaveText(['TST-001', 'TST-002'])
  await page.getByRole('button', { name: 'Use mapped preview' }).click()
  await review.click()
  const preview = page.getByTestId('smart-bulk-preview')
  await expect(preview.getByLabel('Employee Code row 2')).toHaveValue('TST-001')
  await expect(preview.getByLabel('Annual CTC row 3')).toHaveValue('240000')
  await expect(preview.getByLabel('Employee Code row 4')).toHaveCount(0)
  await expect(page.getByTestId('bulk-preview-import')).toBeEnabled()
  assert.deepEqual(writes, [])
  console.log('Employee bulk upload passed header switching, sparse worksheet rows and mapped review.')
} finally {
  await browser.close()
}
