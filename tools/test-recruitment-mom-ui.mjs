import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'

// All API requests are intercepted: no real case, document, signature or approval is changed.
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.HRMS_PLAYWRIGHT_MODULE || '../playwright-e2e/node_modules/@playwright/test')
const ui = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
const output = '.codex-validation/mom-standard/ui'
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true, ...(process.env.HRMS_CHROMIUM_PATH ? { executablePath: process.env.HRMS_CHROMIUM_PATH } : {}) })
const shared = ['dashboard.view', 'dashboard.approvals.view', 'recruitment.hiring-case.view', 'recruitment.work-order.view', 'recruitment.position.view', 'recruitment.candidate.view', 'recruitment.document.view']
const makeDocument = () => ({ id: 13, clientId: 20, hiringCaseId: 35, applicationId: null, interviewId: null, pipelineStageId: 65, documentType: 'MOM', versionNumber: 1, templateId: null, status: 'Draft', bodySnapshot: null, signatureCount: 0, requiredSignatureCount: 2, capturedSignaturesComplete: false, isCurrentJobMom: false, createdAtUtc: '2026-10-01T10:00:00Z', updatedAtUtc: '2026-10-01T10:00:00Z' })
const hiring = { id: 35, clientId: 20, clientName: 'Test UIDAI', positionId: 26, positionName: 'MDD Manager', workOrderNumber: 'TEST-UIDAI-002', pipelineName: 'Standard hiring', pipelineVersionId: 8, currentStageName: 'Signing of MOM', status: 'Active', isPostInterviewStage: true, slaAnchorAtUtc: '2026-10-01T10:00:00Z', overallDueAtUtc: '2026-11-01T10:00:00Z', events: [], stages: [{ id: 171, pipelineStageId: 65, stageName: 'Signing of MOM', stageCode: 'SIGNING_MOM', status: 'Active', activeDurationSeconds: 0, pauseHistory: [], allowPause: true, processDocumentRequirements: [{ id: 15, pipelineStageId: 65, documentType: 'MOM', templateId: null, isRequired: true, requiresSignature: true }] }] }
const results = []
async function scenario(name, { action = 'Generate', permissions = ['recruitment.document.manage'], canSign = false, noDocument = false, width = 1366, height = 768 } = {}) {
  const context = await browser.newContext({ viewport: { width, height } })
  const page = await context.newPage()
  const errors = [], writes = []
  page.on('pageerror', e => errors.push(e.message))
  let document = noDocument ? null : makeDocument()
  let currentAction = action
  if (document && ['Sign', 'Approval', 'Offers', 'Revise'].includes(action)) Object.assign(document, { status: action === 'Sign' ? 'Prepared' : 'Signed', bodySnapshot: 'Synthetic MoM for the selected job candidates.', isCurrentJobMom: true })
  const readiness = () => ({ action: currentAction, message: currentAction === 'Generate' ? '3 selected candidates are ready. Prepare MoM to include their confirmed terms, interview scores and assigned panel automatically.' : currentAction === 'Terms' ? 'Confirm agreed terms in Negotiation & approvals for: Test Candidate.' : currentAction === 'Sign' ? 'Waiting for panel signatures: First Panel, Second Panel. The final signature starts HR approval automatically.' : 'Panel signing is complete. The assigned HR approver continues from My Tasks.', canGenerate: permissions.includes('recruitment.document.manage') && currentAction === 'Generate', canSign, selectedCandidateCount: 3, pendingSigners: ['First Panel', 'Second Panel'] })
  await context.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()), p = url.pathname
    let body = []
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204 })
    if (request.method() !== 'GET') {
      writes.push(p)
      if (p === '/api/recruitment/process-documents') {
        assert.equal(request.postDataJSON().templateId, null)
        assert.equal(request.postDataJSON().hiringCaseId, 35)
        document = makeDocument(); body = document
      } else if (p === '/api/recruitment/process-documents/13/generate') {
        Object.assign(document, { status: 'Prepared', bodySnapshot: 'Prepared synthetic MoM', isCurrentJobMom: true, updatedAtUtc: '2026-10-02T10:00:00Z' })
        currentAction = 'Sign'; body = document
      } else throw new Error(`Unexpected mutation: ${p}`)
    } else if (p === '/api/auth/me') body = { id: 901, email: 'synthetic@example.invalid', displayName: 'Synthetic User', clientId: 20, roles: ['test'], permissions: [...shared, ...permissions] }
    else if (p === '/api/clients') body = [{ id: 20, code: 'UIDAI', name: 'Test UIDAI' }]
    else if (p === '/api/recruitment/hiring-cases') body = [hiring]
    else if (p === '/api/recruitment/hiring-cases/35') body = hiring
    else if (p === '/api/recruitment/process-documents') body = document ? [document] : []
    else if (p.endsWith('/readiness')) body = readiness()
    else if (p.endsWith('/operations/options')) body = { recruiters: [], vendors: [], consultants: [], positionStatuses: [], publishingChannels: [], assignmentPriorities: [] }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  await page.goto(`${ui}/recruitment/mom-and-negotiation?clientId=20&hiringCaseId=35`)
  try {
    await page.getByTestId('mom-next-action').waitFor({ timeout: 20000 })
    if (document) await page.getByRole('button', { name: 'Refresh status', exact: true }).waitFor({ state: 'visible' })
  } catch (error) {
    await page.screenshot({ path: path.join(output, `${name}-failure.png`), fullPage: true })
    fs.writeFileSync(path.join(output, `${name}-failure.json`), JSON.stringify({ errors, text: await page.locator('body').innerText() }, null, 2))
    throw error
  }
  return { page, writes, async done() {
    await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true })
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
    assert.equal(overflow, false, `${name}: horizontal page overflow`)
    assert.deepEqual(errors, [], `${name}: browser errors`)
    results.push({ name, writes, width, height })
    await context.close()
  } }
}
try {
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    const s = await scenario(`ready-${width}`, { width, height })
    await s.page.getByRole('button', { name: 'Prepare MoM', exact: true }).click()
    await s.page.getByRole('button', { name: 'View MoM & signatures', exact: true }).waitFor()
    assert.deepEqual(s.writes, ['/api/recruitment/process-documents/13/generate'])
    await s.done()
  }
  const fresh = await scenario('standard-one-click', { noDocument: true })
  await fresh.page.getByRole('button', { name: 'Prepare MoM', exact: true }).click()
  await fresh.page.getByRole('button', { name: 'View MoM & signatures', exact: true }).waitFor()
  assert.deepEqual(fresh.writes, ['/api/recruitment/process-documents', '/api/recruitment/process-documents/13/generate'])
  await fresh.done()
  const terms = await scenario('confirm-terms', { action: 'Terms', permissions: ['recruitment.document.manage', 'recruitment.proposal.manage'] })
  await terms.page.getByRole('button', { name: 'Confirm agreed terms', exact: true }).waitFor()
  assert.equal(await terms.page.getByRole('button', { name: 'Prepare MoM', exact: true }).count(), 0)
  await terms.page.getByRole('button', { name: 'Confirm agreed terms', exact: true }).click()
  await terms.page.waitForURL(/section=negotiation/)
  await terms.done()
  const viewer = await scenario('viewer-cannot-prepare', { permissions: [] })
  await viewer.page.getByText('The hiring coordinator prepares the MoM.', { exact: false }).waitFor()
  assert.equal(await viewer.page.getByRole('button', { name: 'Prepare MoM', exact: true }).count(), 0)
  assert.deepEqual(viewer.writes, [])
  await viewer.done()
  const observer = await scenario('observer-cannot-sign', { action: 'Sign', permissions: [] })
  await observer.page.getByRole('button', { name: 'View MoM & signatures', exact: true }).click()
  assert.equal(await observer.page.getByRole('button', { name: 'Capture signature', exact: true }).isVisible(), false)
  await observer.done()
  const signer = await scenario('assigned-panel-can-sign', { action: 'Sign', permissions: ['recruitment.interview.panel'], canSign: true })
  await signer.page.getByRole('button', { name: 'Sign MoM', exact: true }).click()
  assert.equal(await signer.page.getByRole('button', { name: 'Capture signature', exact: true }).isEnabled(), true)
  assert.deepEqual(signer.writes, []) // Showing the action does not sign on behalf of the user.
  await signer.done()
  const approval = await scenario('approval-next-action', { action: 'Approval', permissions: ['workflow.approve'] })
  await approval.page.getByRole('button', { name: 'Open My Tasks', exact: true }).click()
  await approval.page.waitForURL(/\/tasks$/)
  assert.deepEqual(approval.writes, [])
  await approval.done()
  console.log(JSON.stringify({ passed: results.length, scenarios: results }, null, 2))
} finally { await browser.close() }
