// Run after building the Admin UI. All browser requests are served from local fixtures.
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { stripTypeScriptTypes } from 'node:module'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium, expect } from './dev-launcher/node_modules/playwright/test.mjs'

const origin = 'https://employee-bulk.example.test'
const dist = process.env.HRMS_TEST_DIST || fileURLToPath(new URL('../payroll-ui/dist/', import.meta.url))
const workbookParser = stripTypeScriptTypes(await readFile(new URL('../payroll-ui/src/utils/importPreview.ts', import.meta.url), 'utf8'))
const template = (id, clientId, name, active = true) => ({ id, clientId: String(clientId), name, active, annualCtc: '', lines: [] })
const templates = [template(101, 42, 'Monthly contract'), template(102, 42, 'Retired contract', false), template(103, 51, 'Foreign contract')]
const component = (id, name) => ({ id, name, code: 'TEST_' + id, category: 'Earning', calculationType: 'Fixed Amount', value: '0', active: true })
const components = [component(10, 'Old input'), component(11, 'New allowance'), component(12, 'Employee allowance'), component(13, 'Derived allowance')]
templates[0].lines = [{ componentId: '10', calculationType: 'Fixed Amount', value: '0' }]
templates.push({ ...template(104, 42, 'New main template'), lines: [
  { componentId: 11, calculationType: 'Fixed Amount', value: '75' },
  { componentId: '12', calculationType: 'Fixed Amount', value: '50' },
  { componentId: '13', calculationType: 'Formula', formula: 'TEST_11 * 40%' }
] })
const employees = [
  { id: 201, clientId: 42, employeeCode: 'E-001', firstName: 'First', lastName: 'Test', isActive: true, salaryStructureId: '', salaryComponents: {}, annualCtc: 0 },
  { id: 202, clientId: 42, employeeCode: 'E-002', firstName: 'Second', lastName: 'Test', isActive: true, salaryStructureId: '101', salaryTemplateName: 'Monthly contract', latestBulkSalaryAssignment: { salaryTemplateId: '102', salaryTemplateName: 'October contract', effectiveFrom: '2026-10-01', effectiveTo: '2026-10-31' }, salaryComponents: { '10': 0 }, annualCtc: 0 },
  { id: 203, clientId: 42, employeeCode: 'E-003', firstName: 'Inactive', lastName: 'Test', isActive: false },
  { id: 204, clientId: 51, employeeCode: 'F-001', firstName: 'Foreign', lastName: 'Test', isActive: true },
].map(employee => ({ ...employee, personalJson: '{}', paymentJson: '{}', dateOfJoining: '2025-01-01' }))
const client = { id: 42, name: 'Synthetic client', code: 'TEST', isActive: true }
const responseRows = employees.slice(0, 2).map((employee, i) => {
  const changes = i === 0 ? [{ field: 'salaryTemplateId', label: 'Salary template', oldValue: '', newValue: '101', payrollImpact: true }] : []
  const candidate = { employeeId: employee.id, employeeName: employee.firstName, employeeCode: employee.employeeCode, changes }
  return { rowNumber: i + 2, sheet: 'Employees', proposedEmployeeCode: employee.employeeCode, matchedEmployeeCode: employee.employeeCode,
    matchedEmployeeId: employee.id, matchedEmployeeName: employee.firstName, matchStatus: 'MatchedByEmployeeCode', changes,
    candidateEmployees: [candidate], identityEvidence: [{ field: 'employeeCode', uploadedValue: employee.employeeCode,
      candidates: [{ ...candidate, existingValue: employee.employeeCode }] }], matchReasons: ['Employee code'], blockingReasons: [] }
})
const status = { jobId: 'isolated-assignment', state: 'Completed', totalRows: 2, completedRows: 2, inserted: 0, updated: 1, skipped: 1, errors: [] }
const jobs = new Map()
const detailReads = new Map()
const dismissedJobs = new Set()
const ownerId = 1
const browser = await chromium.launch(process.env.HRMS_TEST_CHROME ? { executablePath: process.env.HRMS_TEST_CHROME } : {})
const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 1366, height: 768 } })
const posts = []
let extendedRoster = true
let updateEditorTemplate = false
const moreEmployees = Array.from({ length: 12 }, (_, i) => ({
  ...employees[0], id: 301 + i, employeeCode: `P-${i + 1}`, firstName: 'Additional', lastName: `Employee ${i + 1}`
}))
let mayManage = true
let roles = ['super_admin']
let reviewCase = 'clean'
try {
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url())
    if (!url.pathname.startsWith('/api/')) {
      if (url.origin !== origin) return route.abort()
      if (['/employees/master', '/employees/org-structure'].includes(url.pathname)) return route.fulfill({ path: join(dist, 'index.html') })
      if (/^\/assets\/[\w.-]+$/.test(url.pathname)) return route.fulfill({ path: join(dist, url.pathname.slice(1)) })
      if (url.pathname === '/src/utils/importPreview.ts') return route.fulfill({ contentType: 'application/javascript', body: workbookParser })
      return route.fulfill({ status: 404, body: '' })
    }
    const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true',
      'access-control-allow-methods': 'GET,OPTIONS,POST', 'access-control-allow-headers': 'content-type,authorization,x-client-id' }
    const reply = json => route.fulfill({ json, headers })
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (request.method() !== 'GET') {
      if (url.pathname.endsWith('/dismiss')) {
        const job = jobs.get(url.pathname.split('/').at(-2))
        assert.equal(request.method(), 'POST')
        assert.deepEqual(request.postDataJSON(), {})
        assert.equal(job?.ownerId, ownerId, 'only the job owner may dismiss it')
        assert.equal(Number(url.searchParams.get('clientId')), job.clientId)
        assert.equal(job.state, 'Failed')
        dismissedJobs.add(job.jobId)
        return reply({ ok: true })
      }
      const data = await new Response(request.postDataBuffer(), { headers: { 'content-type': request.headers()['content-type'] } }).formData()
      posts.push({ path: url.pathname, data })
      if (url.pathname === '/api/employees/import-preflight') {
        const rows = responseRows.map((row, i) => {
          if (reviewCase === 'already') return { ...row, changes: [] }
          if (reviewCase === 'background') return { ...row, changes: responseRows[0].changes }
          if (reviewCase === 'blocked' || (reviewCase === 'mixed' && i === 1)) return {
            ...row, matchStatus: 'Blocked', blockingReasons: ['Effective date is before joining.'] }
          return row
        })
        return reply({ reviewToken: 'synthetic-token', totalRows: 2, canImport: rows.some(row => row.matchStatus !== 'Blocked'),
          requiresConfirmation: true, supportsDatedSalaryInputs: true, supportsEmployeeSalaryTemplatePolicy: reviewCase !== 'legacyApi', rows, errors: [] })
      }
      if (url.pathname === '/api/employees/import-jobs') {
        const job = { ...status, jobId: `isolated-assignment-${jobs.size + 1}`, ownerId, kind: 'salary-template-assignment', operation: 'update',
          clientId: client.id, clientName: client.name,
          ...(reviewCase === 'background' ? { state: 'Processing', completedRows: 0, updated: 0, skipped: 0 } : {}) }
        jobs.set(job.jobId, job)
        return reply(job)
      }
      return route.abort()
    }
    if (url.pathname === '/api/employees/import-jobs') return reply({ items: [...jobs.values()].filter(job => job.ownerId === ownerId && !dismissedJobs.has(job.jobId)) })
    if (url.pathname.startsWith('/api/employees/import-jobs/')) {
      const job = jobs.get(url.pathname.split('/').pop())
      assert.equal(Number(url.searchParams.get('clientId')), job?.clientId)
      detailReads.set(job.jobId, (detailReads.get(job.jobId) ?? 0) + 1)
      return reply(job)
    }
    switch (url.pathname) {
      case '/api/auth/me': return reply({ id: 1, isActive: true, displayName: 'Test reviewer', roles,
        permissions: mayManage ? ['employees.view', 'employees.manage'] : ['employees.view'], clientId: null, clientAccessMode: 'Legacy', assignedClientIds: [] })
      case '/api/clients': case '/api/employees/clients': return reply([client])
      case '/api/organization': return reply({ id: 1, name: 'Local test' })
      case '/api/employees': return reply((extendedRoster ? [...employees, ...moreEmployees] : employees).map(row =>
        updateEditorTemplate && row.id === 202 ? { ...row, salaryStructureId: '104', salaryTemplateName: 'New main template',
          annualCtc: 120000, salaryTemplatePolicyId: '104', salaryComponents: { TEST_12: 0, '13': 999 } } : row))
      case '/api/employees/field-configuration/catalog': return reply({ fields: [], forms: [], values: {} })
      case '/api/setup': return reply({ salaryStructures: templates, salaryComponents: components })
      default: return reply([])
    }
  })
  const page = await context.newPage()
  page.setDefaultTimeout(12000)
  await page.goto(origin + '/employees/master?clientId=42')
  const openTool = async name => {
    await page.getByRole('button', { name: 'Bulk tools', exact: true }).click()
    await page.getByRole('menuitem', { name, exact: true }).click()
  }
  const unassigned = page.getByRole('checkbox', { name: 'Without salary template' })
  await expect(page.getByRole('columnheader', { name: /Salary template/i }).first()).toBeVisible()
  await expect(page.locator('tbody tr').filter({ hasText: 'E-002' }).last()).not.toContainText('October contract')
  const employeeDialog = page.getByRole('dialog', { name: 'Employee details' })
  await page.locator('tbody tr').filter({ hasText: 'E-002' }).last().getByRole('button', { name: 'Edit', exact: true }).click()
  await employeeDialog.getByRole('tab', { name: '0008 - Basic Pay' }).click()
  await expect(employeeDialog.getByLabel('Old input override')).toBeVisible()
  updateEditorTemplate = true
  const refreshRoster = () => page.evaluate(() => window.dispatchEvent(new CustomEvent('frevo:employee-master-saved', { detail: 42 })))
  await refreshRoster()
  await expect(employeeDialog.locator('.employee-salary-controls')).toContainText('New main template')
  await expect(employeeDialog.getByLabel('Old input override')).toHaveCount(0)
  await expect(employeeDialog.getByLabel('New allowance override')).toHaveValue('75')
  await expect(employeeDialog.getByLabel('Employee allowance override')).toHaveValue('0')
  await expect(employeeDialog.getByLabel('Derived allowance override')).toHaveValue('30')
  await expect(employeeDialog.getByLabel('Derived allowance override')).not.toBeEditable()
  await employeeDialog.getByLabel('Employee allowance override').fill('123')
  updateEditorTemplate = false
  await refreshRoster()
  await expect(employeeDialog.getByLabel('Employee allowance override')).toHaveValue('123')
  await employeeDialog.getByRole('button', { name: 'Replace salary edits with current assignment' }).click()
  await expect(employeeDialog.getByLabel('Old input override')).toBeVisible()
  await employeeDialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  await unassigned.check()
  await expect(page.locator('tbody tr').filter({ hasText: 'E-002' })).toHaveCount(0)
  await openTool('Bulk assign salary template')
  await expect(page.getByRole('button', { name: 'Assign template (13)' })).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
  await unassigned.uncheck()
  await page.getByText('Card view', { exact: true }).click()
  const assignedCard = page.locator('.recruitment-record-card').filter({ hasText: 'E-002' })
  await expect(assignedCard).toContainText('Monthly contract')
  await expect(assignedCard).not.toContainText('October contract')
  await unassigned.check()
  await expect(assignedCard).toHaveCount(0)
  await page.getByRole('textbox', { name: 'Search records' }).fill('P-12')
  await openTool('Bulk assign salary template')
  await expect(page.getByRole('button', { name: 'Assign template (1)' })).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
  await page.getByRole('textbox', { name: 'Search records' }).fill('')
  await unassigned.uncheck()
  await page.getByText('Table view', { exact: true }).click()
  await openTool('Bulk update')
  await expect(page.getByTestId('smart-bulk-operation')).toContainText('Update existing')
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  extendedRoster = false
  await page.reload()
  await openTool('Bulk assign salary template')
  const modal = page.getByRole('dialog', { name: /Assign salary template/ })
  const options = () => page.locator('.ant-select-dropdown:visible .ant-select-item-option-content')
  const openSelect = name => modal.locator('.ant-select').filter({ has: page.getByRole('combobox', { name, exact: true }) }).click()
  const review = count => modal.getByRole('button', { name: `Assign template (${count})` })
  const all = modal.getByRole('checkbox', { name: 'Select all active employees' })
  await expect(review(2)).toBeDisabled()
  await expect(all).toBeChecked()
  await openSelect('Salary template to assign')
  await expect(options().filter({ hasText: /Retired|Foreign/ })).toHaveCount(0)
  await options().filter({ hasText: 'Monthly contract' }).click()
  await expect(modal.locator('input[type=date]')).toHaveCount(0)
  await expect(review(2)).toBeEnabled()
  await openSelect('Employees to assign')
  await expect(options().filter({ hasText: /Inactive Test|Foreign Test/ })).toHaveCount(0)
  const employeeSelect = page.getByRole('combobox', { name: 'Employees to assign' })
  await employeeSelect.fill('E-001')
  await options().filter({ hasText: 'E-001' }).click()
  await employeeSelect.press('Escape')
  await expect(review(1)).toBeEnabled()
  await expect(all).toHaveAttribute('aria-checked', 'mixed')
  await expect(all.locator('..')).toHaveClass(/ant-checkbox-indeterminate/)
  await all.click()
  await expect(all).toBeChecked()
  await all.click()
  await expect(all).not.toBeChecked()
  await expect(review(0)).toBeDisabled()
  await all.click()
  await expect(all).toBeChecked()
  for (const width of [768, 390]) {
    await page.setViewportSize({ width, height: 844 })
    await expect(review(2)).toBeInViewport()
    const box = await modal.boundingBox()
    assert.ok(box.x >= 0 && box.x + box.width <= width + 1)
  }
  await page.setViewportSize({ width: 1366, height: 768 })
  assert.deepEqual(posts, [])
  await review(2).click()
  await expect.poll(() => posts.length).toBe(2)
  await expect(page.getByTestId('employee-import-review')).toHaveCount(0)
  const preflight = posts[0].data
  assert.equal(preflight.get('clientId'), '42')
  assert.equal(preflight.get('mode'), 'update')
  const file = preflight.get('file')
  const data = await page.evaluate(async ({ bytes, name }) => {
    const { parseImportPreviewFile } = await import('/src/utils/importPreview.ts')
    return parseImportPreviewFile(new File([Uint8Array.from(bytes)], name))
  }, { bytes: Array.from(new Uint8Array(await file.arrayBuffer())), name: file.name })
  assert.ok(data.headers.includes('Employee Code'))
  assert.ok(data.headers.includes('Salary Template Id'))
  assert.equal(data.headers.includes('Salary Effective From'), false)
  assert.ok(data.headers.includes('Employee ID'))
  assert.ok(data.headers.includes('Assign Salary Template Only'))
  assert.ok(data.headers.every(header => !/ctc|salary json|salary_input|amount/i.test(header)))
  assert.deepEqual(data.rows.map(row => row[data.headers.indexOf('Employee Code')]), ['E-001', 'E-002'])
  assert.deepEqual(data.rows.map(row => row[data.headers.indexOf('Employee ID')]), ['201', '202'])
  assert.deepEqual(data.rows.map(row => row[data.headers.indexOf('Assign Salary Template Only')].toUpperCase()), ['TRUE', 'TRUE'])
  assert.deepEqual(data.rows.map(row => row[data.headers.indexOf('Assign Payroll Template Policy')]), ['TRUE', 'TRUE'])
  assert.deepEqual(data.rows.map(row => row[data.headers.indexOf('Salary Template Id')]), ['101', '101'])
  assert.deepEqual(posts.map(post => post.path), ['/api/employees/import-preflight', '/api/employees/import-jobs'])
  const final = posts[1].data
  assert.equal(preflight.has('forceSalaryAssignment'), false)
  assert.equal(final.has('forceSalaryAssignment'), false)
  assert.equal(final.get('reviewToken'), 'synthetic-token')
  const selected = JSON.parse(final.get('decisionsJson'))
  assert.deepEqual(selected.filter(row => row.action === 'update').map(row => row.employeeId), [201])
  assert.deepEqual(selected[0].fieldChoices, { salaryTemplateId: 'useImported' })
  assert.deepEqual(Buffer.from(await final.get('file').arrayBuffer()), Buffer.from(await file.arrayBuffer()))
  await expect(page.getByText('1 assigned · 1 already assigned · 0 blocked')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
  await page.getByRole('button', { name: 'Bulk tools', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Bulk upload', exact: true }).click()
  await page.getByTestId('smart-bulk-file-input').setInputFiles({
    name: 'salary.csv', mimeType: 'text/csv', buffer: Buffer.from('Employee Code,Basic\nE-001,20000')
  })
  const mapper = page.getByTestId('smart-bulk-drawer')
  const templateChoice = mapper.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Salary template for mapping' }) })
  await expect(templateChoice).toContainText('Monthly contract')
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  const assignAgain = async variant => {
    reviewCase = variant
    await openTool('Bulk assign salary template')
    await openSelect('Salary template to assign')
    await options().filter({ hasText: 'Monthly contract' }).click()
    await review(2).click()
  }
  await assignAgain('mixed')
  const exceptions = modal.getByTestId('salary-assignment-exceptions')
  await expect(exceptions).toContainText('1 ready · 0 already assigned · 1 blocked')
  await expect(exceptions).toContainText('E-002 · Second Test')
  await expect(exceptions).toContainText('Effective date is before joining.')
  assert.equal(posts.length, 3)
  await modal.getByRole('button', { name: 'Assign eligible (1)' }).click()
  await expect(page.getByText('1 assigned · 0 already assigned · 1 blocked')).toBeVisible()
  assert.deepEqual(JSON.parse(posts[3].data.get('decisionsJson')), [
    { rowNumber: 2, sheet: 'Employees', action: 'update', employeeId: 201, fieldChoices: { salaryTemplateId: 'useImported' } },
    { rowNumber: 3, sheet: 'Employees', action: 'skip' }
  ])
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
  await assignAgain('blocked')
  await expect(exceptions).toContainText('0 ready · 0 already assigned · 2 blocked')
  await expect(modal.getByRole('button', { name: 'Assign eligible (0)' })).toBeDisabled()
  await modal.getByRole('button', { name: 'Edit selection' }).click()
  await expect(review(2)).toBeEnabled()
  await modal.getByRole('button', { name: 'Cancel' }).click()
  await assignAgain('already')
  await expect(page.getByText('2 employees already have this salary template.')).toBeVisible()
  assert.equal(posts.filter(post => post.path === '/api/employees/import-jobs').length, 2)
  await assignAgain('legacyApi')
  await expect(modal).toContainText('Update the API to enable employee salary template policies.')
  assert.equal(posts.filter(post => post.path === '/api/employees/import-jobs').length, 2)
  await modal.getByRole('button', { name: 'Cancel', exact: true }).click()
  roles = ['hr_manager']
  await page.reload()
  await assignAgain('background')
  const progress = page.getByRole('dialog', { name: 'Salary template assignment' })
  await progress.getByRole('button', { name: 'Run in background' }).click()
  await expect(progress).toHaveCount(0)
  const activeJob = [...jobs.values()].find(job => job.state === 'Processing')
  assert.ok(activeJob)
  const readsAtHide = detailReads.get(activeJob.jobId) ?? 0
  activeJob.completedRows = 1
  activeJob.updated = 1
  await expect.poll(() => detailReads.get(activeJob.jobId) ?? 0).toBeGreaterThan(readsAtHide)
  await expect(progress).toHaveCount(0)
  await page.getByText('Org Structure', { exact: true }).first().click()
  await expect(page).toHaveURL(/\/employees\/org-structure/)
  await page.reload()
  await expect(progress).toHaveCount(0)
  const bell = page.getByRole('button', { name: 'Notifications', exact: true })
  await bell.click()
  const runningNotice = page.getByRole('listitem').filter({ hasText: 'Synthetic client · Processing · 1/2 processed · 1 saved' })
  await expect(runningNotice).toContainText('Salary template assignment')
  await expect(runningNotice.getByRole('button', { name: /Dismiss/ })).toHaveCount(0)
  await runningNotice.getByRole('button').click()
  await expect(page).toHaveURL(/\/employees\/master/)
  await expect(progress.getByText('Completed 1')).toBeVisible()
  await expect(progress.getByText('Updated 1')).toBeVisible()
  activeJob.state = 'Failed'
  activeJob.completedRows = 2
  activeJob.errors = ['E-002: salary record changed after review.']
  await expect(progress).toContainText('Employee job needs attention')
  await expect(progress).toContainText(activeJob.errors[0])
  await expect(progress.getByText('Completed 2')).toBeVisible()
  await expect(progress.getByText('Updated 1')).toBeVisible()
  await progress.getByRole('button', { name: 'Close', exact: true }).last().click()
  await bell.click()
  const failedNotice = page.getByRole('listitem').filter({ hasText: 'Synthetic client · Failed · 2/2 processed · 1 saved' })
  await failedNotice.getByRole('button', { name: 'Dismiss Salary template assignment notification' }).click()
  await expect.poll(() => dismissedJobs.has(activeJob.jobId)).toBe(true)
  await expect(failedNotice).toHaveCount(0)
  await page.getByRole('button', { name: 'Refresh pending actions' }).click()
  await expect(failedNotice).toHaveCount(0)
  assert.equal(dismissedJobs.size, 1)
  mayManage = false
  await page.reload()
  await expect(page.getByRole('button', { name: 'Excel template', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Bulk tools', exact: true })).toHaveCount(0)
  console.log('Client-scoped Employee Master template assignment passed through reviewed import.')
} finally { await browser.close() }
