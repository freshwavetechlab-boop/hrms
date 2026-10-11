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
async function scenario(name, { action = 'Generate', permissions = ['recruitment.document.manage'], canSign = false, noDocument = false, width = 1366, height = 768, sla = {}, firstSigned = false, templateId = null, incomingPing = false } = {}) {
  console.log(`Checking ${name}`)
  const context = await browser.newContext({ viewport: { width, height } })
  const page = await context.newPage()
  page.setDefaultTimeout(15000)
  const errors = [], writes = []
  page.on('pageerror', e => errors.push(e.message))
  let document = noDocument ? null : makeDocument()
  if (document) document.templateId = templateId
  let supportingFiles = []
  const owner = { applicationId: 120, interviewId: 22, userId: 902, name: 'Second Panel', email: 'panel@example.invalid', designation: 'Director', role: 'Chairperson', stageName: 'Interview / Panel Assessment', task: 'Feedback' }
  let currentAction = action
  if (document && ['Sign', 'Approval', 'Offers', 'Revise', 'ApprovalSetup'].includes(action)) Object.assign(document, { status: ['Sign', 'ApprovalSetup'].includes(action) ? 'Prepared' : 'Signed', bodySnapshot: 'Synthetic MoM for the selected job candidates.', isCurrentJobMom: action !== 'Revise', signatureCount: ['Approval', 'Offers', 'Revise'].includes(action) ? 2 : firstSigned ? 1 : 0 })
  const readiness = () => ({ action: currentAction, jobName: hiring.positionName, jobStatus: hiring.status, workOrderNumber: hiring.workOrderNumber, positionId: 26,
    message: currentAction === 'Generate' ? '3 selected candidates are ready. Prepare MoM to include their confirmed terms, interview scores and assigned panel automatically.' : currentAction === 'Terms' ? 'Confirm agreed terms in Negotiation & approvals for: Test Candidate.' : currentAction === 'Interview' ? 'Panel feedback is pending for Test Candidate. Second Panel must submit feedback in Interview Tracker.' : currentAction === 'Sign' ? `Waiting for panel signatures: ${firstSigned ? '' : 'First Panel, '}Second Panel. The final signature starts HR approval automatically.` : 'Panel signing is complete. The assigned HR approver continues from My Tasks.',
    canGenerate: permissions.includes('recruitment.document.manage') && currentAction === 'Generate', canSign, canPing: permissions.includes('recruitment.document.manage'), selectedCandidateCount: 3, requiredCandidateCount: 3,
    candidateChecks: [{ applicationId: 120, candidateName: 'Test Candidate', interviewStatus: 'Completed, Selected', interviewSelected: true, termsConfirmed: currentAction !== 'Terms', pendingFeedbackFrom: currentAction === 'Interview' ? ['Second Panel'] : [], scoresPending: false, pendingWorkOwners: currentAction === 'Interview' ? [owner] : [] }],
    panelMembers: [{ userId: 901, name: 'First Panel', isSigned: firstSigned || ['Approval', 'Offers'].includes(currentAction) }, { userId: 902, name: 'Second Panel', isSigned: ['Approval', 'Offers'].includes(currentAction) }], pendingSigners: ['First Panel', 'Second Panel'] })
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
      } else if (p === '/api/recruitment/process-documents/13/ping') {
        assert.deepEqual(request.postDataJSON(), { applicationId: 120, userId: 902, task: 'Feedback' }); body = { ok: true }
      } else if (p === '/api/attachments') {
        const data = request.postDataBuffer().toString()
        assert.match(data, /RECRUITMENT_PROCESS_SUPPORT/); assert.match(data, /evidence.pdf/)
        body = { id: 100, publicId: 'synthetic-support', fieldConfigurationId: 700, attachmentAttributeId: 701, entityType: 'RECRUITMENT_PROCESS_SUPPORT', entityId: 13, originalFileName: 'evidence.pdf', fileSizeBytes: 25, versionNumber: 1, uploadedAtUtc: '2026-10-11T10:00:00Z', verificationStatus: 'NotRequired' }
        supportingFiles = [body]
      } else throw new Error(`Unexpected mutation: ${p}`)
    } else if (p === '/api/auth/me') body = { id: 901, email: 'synthetic@example.invalid', displayName: 'Synthetic User', clientId: 20, roles: ['test'], permissions: [...shared, ...permissions] }
    else if (p === '/api/clients') body = [{ id: 20, code: 'UIDAI', name: 'Test UIDAI' }]
    else if (p === '/api/recruitment/hiring-cases') body = [{ ...hiring, ...sla }]
    else if (p === '/api/recruitment/hiring-cases/35') body = { ...hiring, ...sla }
    else if (p === '/api/recruitment/process-documents') body = document ? [document] : []
    else if (p.endsWith('/readiness')) body = readiness()
    else if (p === '/api/recruitment/mom-pings') body = incomingPing ? [{ id: 77, title: 'MoM reminder · MDD Manager', detail: 'Hiring administrator requested Feedback for Test Candidate', route: '/recruitment/interviews?clientId=20&feedbackInterviewId=22' }] : []
    else if (p === '/api/attachment-configurations/effective') body = url.searchParams.get('formCode') === 'PROCESS_SUPPORT' ? [{ id: 700, attachmentAttributeId: 701, attributeName: 'MoM supporting attachment', fieldLabel: 'Supporting files', helpText: 'Optional supporting records. These files do not replace MoM or its panel signatures.', dataClassification: 'Restricted', allowMultiple: true, maximumFileCount: 10, maximumFileSizeBytes: 10485760, allowedExtensionsJson: '["pdf","docx","xlsx","png"]', ownerCanView: true }] : []
    else if (p === '/api/attachments') body = url.searchParams.get('entityType') === 'RECRUITMENT_PROCESS_SUPPORT' ? supportingFiles : []
    else if (p === '/api/recruitment-admin') body = { templates: [{ id: 44, clientId: 20, templateType: 'MoM', templateCode: 'JOB_MOM', templateName: 'Client MoM layout', subjectTemplate: 'Job {{positionName}}', bodyTemplate: 'Configured client MoM content {{panelMembersList}}', isHtml: false, isActive: true }], settings: [], masters: [], consultants: [], vendors: [], assignmentRules: [], slaRules: [], documentChecklist: [], approvalMappings: [] }
    else if (p === '/api/recruitment/pipeline-workspace') body = { clientId: 20, lanes: [], unassignedDemandCards: [] }
    else if (p.endsWith('/operations/options')) body = { recruiters: [], vendors: [], consultants: [], positionStatuses: [], publishingChannels: [], assignmentPriorities: [] }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  await page.goto(`${ui}/recruitment/mom-and-negotiation?clientId=20&hiringCaseId=35`)
  try {
    await page.getByTestId('mom-next-action').waitFor({ timeout: 20000 })
    if (document) await page.getByTestId('mom-panel-status').waitFor({ state: 'visible' })
  } catch (error) {
    await page.screenshot({ path: path.join(output, `${name}-failure.png`), fullPage: true })
    fs.writeFileSync(path.join(output, `${name}-failure.json`), JSON.stringify({ errors, text: await page.locator('body').innerText() }, null, 2))
    throw error
  }
  return { page, writes, async done() {
    const mom = page.getByTestId('mom-next-action')
    const url = new URL(page.url())
    if (url.pathname === '/recruitment/mom-and-negotiation' && url.searchParams.get('section') !== 'negotiation' && await mom.count() && !await page.getByRole('dialog', { name: 'Job MoM & panel signatures', exact: true }).count() && !await page.getByRole('dialog', { name: 'Sign job MoM', exact: true }).count()) await mom.scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true, animations: 'disabled' })
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
    await s.page.getByText('Preparation pending · v1 · Draft', { exact: true }).waitFor()
    await s.page.getByTestId('mom-job-identity').getByText('Job: Active', { exact: true }).waitFor()
    await s.page.getByRole('button', { name: 'Prepare MoM', exact: true }).click()
    await s.page.getByRole('button', { name: 'View MoM & signatures', exact: true }).waitFor()
    await s.page.getByText('Current · v1 · Prepared', { exact: true }).waitFor()
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
  await terms.page.getByTestId('mom-candidate-checks').getByText('Test Candidate', { exact: true }).waitFor()
  await terms.page.getByTestId('mom-candidate-checks').getByRole('cell', { name: 'Pending', exact: true }).waitFor()
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
  const feedback = await scenario('pending-feedback-mobile', { action: 'Interview', permissions: ['recruitment.interview.panel'], width: 390, height: 844 })
  await feedback.page.getByTestId('mom-candidate-checks').getByText('Feedback: Second Panel', { exact: true }).waitFor()
  assert.deepEqual(feedback.writes, [])
  assert.equal(await feedback.page.getByTestId('mom-candidate-checks').getByRole('button', { name: 'Ping', exact: true }).count(), 0)
  await feedback.done()
  const ping = await scenario('admin-pings-assigned-user', { action: 'Interview' })
  await ping.page.getByTestId('mom-candidate-checks').getByText('panel@example.invalid', { exact: false }).waitFor()
  await ping.page.getByTestId('mom-candidate-checks').getByRole('button', { name: 'Ping', exact: true }).click()
  await ping.page.getByRole('button', { name: 'Ping sent', exact: true }).waitFor()
  assert.deepEqual(ping.writes, ['/api/recruitment/process-documents/13/ping'])
  await ping.done()
  const attachments = await scenario('mom-supporting-attachment-upload')
  await attachments.page.getByText('MoM supporting attachments', { exact: true }).click()
  const supporting = attachments.page.locator('.employee-document-panel').filter({ hasText: 'Additional attachments' })
  await supporting.locator('input[type=file]').setInputFiles({ name: 'evidence.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\nSynthetic supporting file') })
  await supporting.getByRole('button', { name: 'Upload', exact: true }).click()
  await supporting.getByText('evidence.pdf', { exact: true }).waitFor()
  await supporting.getByRole('button', { name: 'Preview', exact: true }).waitFor()
  assert.deepEqual(attachments.writes, ['/api/attachments'])
  await attachments.done()
  const bell = await scenario('recipient-bell-ping', { permissions: ['recruitment.interview.panel'], incomingPing: true })
  await bell.page.locator('.hiring-journey-drawer .ant-drawer-close').click()
  await bell.page.getByRole('button', { name: 'Notifications', exact: true }).click()
  await bell.page.getByText('MoM reminder · MDD Manager', { exact: true }).waitFor()
  await bell.page.getByRole('button').filter({ hasText: 'MoM reminder · MDD Manager' }).click()
  await bell.page.waitForURL(/feedbackInterviewId=22/)
  await bell.done()
  for (const templateId of [null, 44]) {
    const template = await scenario(`mom-template-${templateId || 'standard'}`, { permissions: ['recruitment.manage', 'recruitment.document.manage'], templateId })
    await template.page.getByRole('button', { name: 'Edit MoM template', exact: true }).click()
    await template.page.waitForURL(/tool=templates/)
    await template.page.getByRole('button', { name: 'Save template', exact: true }).waitFor()
    const editor = template.page.locator('.recruitment-admin-drawer')
    if (templateId) assert.equal(await editor.locator('textarea').inputValue(), 'Configured client MoM content {{panelMembersList}}')
    else assert.match(await editor.locator('textarea').inputValue(), /MINUTES OF MEETING OF THE SELECTION COMMITTEE/)
    if (!templateId) assert.match(await editor.innerText(), /select it in the MoM document requirement/)
    assert.deepEqual(template.writes, [])
    await template.done()
  }
  const partial = await scenario('partial-panel-signatures', { action: 'Sign', firstSigned: true, permissions: [] })
  await partial.page.getByTestId('mom-panel-status').getByText('First Panel · Signed', { exact: true }).waitFor()
  await partial.page.getByTestId('mom-panel-status').getByText('Second Panel · Pending', { exact: true }).waitFor()
  await partial.done()
  for (const action of ['Approval', 'Revise', 'ApprovalSetup']) {
    const history = await scenario(`view-${action}`, { action, permissions: [] })
    await history.page.getByRole('button', { name: 'View MoM & signatures', exact: true }).click()
    await history.page.getByRole('dialog', { name: 'Job MoM & panel signatures', exact: true }).getByTestId('mom-job-identity').getByText('Job MoM · MDD Manager', { exact: true }).waitFor()
    assert.equal(await history.page.getByRole('button', { name: 'Capture signature', exact: true }).isVisible(), false)
    if (action === 'Revise') await history.page.getByRole('dialog', { name: 'Job MoM & panel signatures', exact: true }).getByText('Previous · v1 · Signed', { exact: true }).waitFor()
    assert.deepEqual(history.writes, [])
    await history.done()
  }
  const breached = await scenario('overall-breached', { sla: { overallDueAtUtc: new Date(Date.now() - 2 * 86400000).toISOString() } })
  const timers = breached.page.getByTestId('hiring-case-live-timers')
  await timers.getByText('SLA breached', { exact: true }).waitFor()
  assert.match(await timers.innerText(), /0d left.*overdue/)
  await breached.done()
  const paused = await scenario('overall-paused', { sla: { overallSlaPaused: true, overallDueAtUtc: new Date(Date.now() + 2 * 86400000).toISOString() } })
  await paused.page.getByTestId('hiring-case-live-timers').getByText('SLA on track', { exact: true }).waitFor()
  const clock = paused.page.getByTestId('hiring-case-live-timers').locator('b').nth(1)
  const frozen = await clock.innerText()
  await paused.page.waitForTimeout(1500)
  assert.equal(await clock.innerText(), frozen)
  assert.match(frozen, /Paused/)
  await paused.done()
  console.log(JSON.stringify({ passed: results.length, scenarios: results }, null, 2))
} finally { await browser.close() }
