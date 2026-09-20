import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
const root = fileURLToPath(new URL('../', import.meta.url))
const require = createRequire(path.join(root, 'playwright-e2e/package.json'))
const { chromium } = require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5184'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(root, '.codex-validation/recruitment-workflows/ui')
fs.mkdirSync(output, { recursive: true })
const user = { id: 1, displayName: 'Isolated Super Admin', email: 'fixture@example.invalid', roles: ['super_admin'], permissions: ['settings.manage', 'recruitment.manage', 'recruitment.interview.schedule', 'recruitment.interview.panel'], dashboardAccess: [], defaultDashboardCode: '', isActive: true, clientId: null, employeeId: null }
const client = { id: 20, name: 'Isolated UIDAI Test Client', code: 'UIDAI' }
const journey = (id, requisitionId, workOrderId, workOrderLineId, positionId, positionName) => ({ id, clientId: 20, clientName: client.name, requisitionId, workOrderId, workOrderLineId, positionId, positionName,
  workOrderNumber: `WO-${workOrderId}`, pipelineName: 'Hiring SLA', pipelineVersionId: 1, currentStageName: 'Order for Hiring', status: 'Active', slaAnchorAtUtc: '2026-09-01T00:00:00Z', overallDueAtUtc: '2026-10-01T00:00:00Z', stages: [], events: [] })
const cases = [journey(20, 21, 15, 16, null, 'Chief Architect'), journey(21, 21, 15, 17, 15, 'Chief Architect'), journey(23, 22, 16, 18, 16, 'Software Architect')]
const orders = [15, 16].map(id => ({ id, clientId: 20, clientName: client.name, workOrderNumber: `WO-${id}`, subject: '', status: 'Active', lineCount: 1, receivedAtUtc: '2026-09-01T00:00:00Z', lines: [] }))
const posting = (id, positionId, title) => ({ id, clientId: 20, positionId, publicTitle: title, positionTitle: title, positionCode: `POS-${positionId}`, publicSlug: `${id}`.padStart(32, '0'), status: 'Published', applicationCount: id === 7 ? 1 : 0, autoRunAts: true, enableResumeParsing: true, enableAiParsing: false, requireEmailOtp: false, clientName: client.name, createdAtUtc: '2026-09-01T00:00:00Z', publishedAtUtc: `2026-09-${id + 1}T00:00:00Z`, jobDescriptionVersionId: 1 })
const postings = [posting(6, 15, 'Chief Architect'), posting(7, 16, 'Software Architect'), posting(8, 16, 'Software Architect')]
const errors = [], unknown = new Set(), checks = []
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 60 })
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
const page = await context.newPage()
page.on('pageerror', error => errors.push(error.message))
await context.route('**/api/**', async route => {
  const request = route.request(), p = new URL(request.url()).pathname
  const reply = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  if (request.method() !== 'GET') throw Error(`Unexpected mutation in read-only UI fixture: ${p}`)
  if (p === '/api/auth/me') return reply(user)
  if (p === '/api/clients') return reply([client])
  if (p === '/api/recruitment/work-orders') return reply(orders)
  if (p === '/api/recruitment/hiring-cases') return reply(cases)
  if (p === '/api/recruitment-orchestration/job-postings') return reply(postings)
  if (p === '/api/recruitment/interviews') return reply([])
  if (p === '/api/workflows/tasks/pending') return reply([{ id: 31, stageName: 'Approve hiring budget', resourceType: 'RecruitmentBudget', resourceId: '22' }])
  if (p === '/api/recruitment/pipelines') return reply([])
  if (p.startsWith('/api/recruitment/hiring-cases/')) return p.endsWith('/transitions') ? reply([]) : reply(cases.find(row => row.id === Number(p.split('/').at(-1))) || null)
  unknown.add(p)
  // App helpers have typed empty fallbacks. No intercepted request reaches a real API.
  return route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: 'Not configured in isolated UI fixture.' }) })
})
try {
  await page.goto(`${origin}/recruitment/work-orders-and-sla`, { waitUntil: 'networkidle' })
  await page.locator('.hiring-case-list').waitFor()
  assert.equal(await page.locator('.hiring-case-list h3').filter({ hasText: 'Chief Architect' }).count(), 1)
  assert.equal(await page.locator('.hiring-case-list > button').count(), 2)
  await page.getByText('Earlier journeys — history retained', { exact: true }).waitFor()
  checks.push('Chief Architect renders once; earlier journey is in history')
  await page.screenshot({ path: path.join(output, 'work-orders-current-and-history.png'), fullPage: true })
  await page.getByRole('button', { name: 'Notifications', exact: true }).click()
  await page.getByText('Approve hiring budget', { exact: true }).waitFor()
  checks.push('global notification bell shows assigned approval')
  await page.screenshot({ path: path.join(output, 'global-pending-actions.png'), fullPage: true })
  await page.goto(`${origin}/recruitment/job-postings`, { waitUntil: 'networkidle' })
  await page.locator('.job-summary-card').first().waitFor()
  assert.equal(await page.locator('.job-summary-card h4').filter({ hasText: 'Software Architect' }).count(), 1)
  assert.equal(await page.locator('.job-summary-card').count(), 2)
  await page.getByTestId('job-posting-history').locator('summary').click()
  await page.getByRole('button', { name: /Software Architect.*Posting #7/ }).waitFor()
  checks.push('latest job card only; older posting remains in history')
  await page.screenshot({ path: path.join(output, 'jobs-current-and-history.png'), fullPage: true })
  await page.setViewportSize({ width: 768, height: 960 })
  await page.screenshot({ path: path.join(output, 'jobs-narrow.png'), fullPage: true })
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ fixture: 'Mocked APIs; actual portal UI; no production writes', checks, pageErrors: errors, fallbackRoutes: [...unknown] }, null, 2))
  console.log(JSON.stringify({ checks, pageErrors: errors, screenshots: output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  console.error(JSON.stringify({ pageErrors: errors, fallbackRoutes: [...unknown] }))
  throw error
} finally { await browser.close() }
