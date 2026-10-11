import assert from 'node:assert/strict'
import fs from 'node:fs'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const ts = require('../payroll-ui/node_modules/typescript')
const source = fs.readFileSync('payroll-ui/src/utils/recruitmentSla.ts', 'utf8')
const { recruitmentJobSla } = await import(`data:text/javascript;base64,${Buffer.from(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText).toString('base64')}`)
const now = Date.parse('2026-10-10T12:00:00Z')
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-12T12:00:00' }, now).text, '2d 00:00:00 left') // MySQL UTC timestamps without Z.
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-08T12:00:00Z' }, now).text, '0d left · 2d 00:00:00 overdue')
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-10T12:00:00Z' }, now).breached, false)
assert.equal(recruitmentJobSla({}, now).label, 'SLA not configured')
assert.equal(recruitmentJobSla({ overallDueAtUtc: 'invalid' }, now).label, 'SLA not configured')
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-12T12:00:00Z', overallSlaPaused: true }, now + 60000, now).text, 'Paused · 2d 00:00:00 left')
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-08T12:00:00Z', completedAtUtc: '2026-10-07T12:00:00Z' }, now).breached, false)
assert.equal(recruitmentJobSla({ overallDueAtUtc: '2026-10-08T12:00:00Z', completedAtUtc: '2026-10-09T12:00:00Z' }, now).text, '0d left · 1d 00:00:00 overdue')

// Full pipeline rendering, including the formerly conflicting stale stage-breach flag.
// Every API request is mocked; any attempted write fails the test.
const { chromium } = require(process.env.HRMS_PLAYWRIGHT_MODULE || '../playwright-e2e/node_modules/@playwright/test')
const browser = await chromium.launch({ headless: true, ...(process.env.HRMS_CHROMIUM_PATH ? { executablePath: process.env.HRMS_CHROMIUM_PATH } : {}) })
const output = '.codex-validation/mom-standard/ui'
fs.mkdirSync(output, { recursive: true })
try {
  for (const breached of [false, true]) {
    const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await context.route('**/api/**', async route => {
      const request = route.request(), p = new URL(request.url()).pathname
      assert.equal(request.method(), 'GET', `Unexpected mutation ${p}`)
      let body = []
      if (p === '/api/auth/me') body = { id: 901, email: 'synthetic@example.invalid', displayName: 'Viewer', clientId: 20, roles: [], permissions: ['dashboard.view', 'recruitment.pipeline.view', 'recruitment.hiring-case.view', 'recruitment.work-order.view', 'recruitment.position.view', 'recruitment.candidate.view'] }
      else if (p === '/api/clients') body = [{ id: 20, code: 'UIDAI', name: 'Test UIDAI' }]
      else if (p === '/api/recruitment/pipeline-workspace') body = { clientId: 20, unassignedDemandCards: [], lanes: [{ stageId: 65, pipelineVersionId: 8, pipelineName: 'Test hiring', pipelineVersionNumber: 1, stageName: 'Order for Hiring', stageType: 'Intake', cardScope: 'Position', displayOrder: 1, targetOffsetMinutes: 0, applications: [], demandCards: [{ workOrderLineId: 1, workOrderId: 1, clientId: 20, workOrderNumber: 'TEST-WO-001', positionName: 'Database Architect', hiringCaseId: 35, positionId: 26, status: 'Active', hasWorkOrder: true, isSlaBreached: true, dueAtUtc: new Date(Date.now() - 86400000).toISOString(), overallDueAtUtc: new Date(Date.now() + (breached ? -2 : 27) * 86400000).toISOString(), candidateCount: 0 }] }] }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
    })
    await page.goto(`${process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'}/recruitment/hiring-pipeline?clientId=20`)
    const card = page.getByTestId('pipeline-demand-1')
    await card.getByText(breached ? 'SLA breached' : 'SLA on track', { exact: true }).waitFor({ timeout: 20000 })
    const timer = await card.getByTestId('demand-sla-timer-1').innerText()
    assert.match(timer, breached ? /0d left.*overdue/ : /left$/)
    assert.equal(await card.getByText('Business SLA breach', { exact: true }).count(), 0)
    assert.equal(await card.evaluate(element => element.classList.contains('is-breached')), breached)
    assert.deepEqual(errors, [])
    await page.screenshot({ path: `${output}/pipeline-sla-${breached ? 'breached' : 'on-track'}.png`, fullPage: true })
    await context.close()
  }
  console.log('Passed: 8 SLA calculations and 2 mocked pipeline browser scenarios.')
} finally { await browser.close() }
