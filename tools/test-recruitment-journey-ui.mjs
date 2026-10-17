import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = process.env.HRMS_PLAYWRIGHT_MODULE ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href) : require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Only the local UI is allowed.')
const output = path.join(os.tmpdir(), 'frevo-recruitment-journey-ui-20261017')
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-journey-fixture-${crypto.randomUUID()}.tsx`
const fixture = fileURLToPath(new URL(`../payroll-ui/${fixtureName}`, import.meta.url))
const checks = [], errors = [], writes = []
const stages = [
  { id: 1, stageCode: 'SOURCE', stageName: 'Sharing of profiles', displayOrder: 1, isInitial: true },
  { id: 2, stageCode: 'INTERVIEW', stageName: 'Interview / Panel Assessment', displayOrder: 2 },
  { id: 3, stageCode: 'NEGOTIATION', stageName: 'Negotiation & approvals', displayOrder: 3 },
  { id: 4, stageCode: 'DONE', stageName: 'Completed', displayOrder: 4, isTerminal: true },
].map(stage => ({ pipelineVersionId: 1, stageType: 'Screening', cardScope: 'Position', stageNumber: stage.displayOrder, slaDurationMinutes: 60, slaWarningMinutes: 10, allowPause: true, pauseBehavior: 'ShiftStageAndOverall', requiresApproval: false, calendarEnabled: false, allowSkip: false, isActive: true, isInitial: false, isTerminal: false, actions: [], defaultPanelMembers: [], attachmentRequirements: [], processDocumentRequirements: [], ...stage }))
const visits = [1, 2, 3, 2, 3, 1, 2].map((stageId, index) => ({ ...stages.find(stage => stage.id === stageId), id: 101 + index, pipelineStageId: stageId, hiringCaseId: 5, applicationId: 100, applicationPipelineInstanceId: 1, enteredAtUtc: `2026-10-${String(10 + index).padStart(2, '0')}T09:00:00Z`, exitedAtUtc: index < 6 ? `2026-10-${String(10 + index).padStart(2, '0')}T10:00:00Z` : null, completedAtUtc: index < 6 ? `2026-10-${String(10 + index).padStart(2, '0')}T10:00:00Z` : null, status: index === 6 ? 'Active' : 'Completed', reason: index === 3 ? 'Panel requested another assessment.' : index === 5 ? 'All linked candidates rejected; sourcing reopened.' : 'Stage requirements completed.', outcomeCode: index === 4 ? 'ALL_PROFILES_REJECTED' : 'ADVANCE', changedByName: 'Synthetic recruiter', pausedDurationSeconds: index === 3 ? 600 : 0, activeDurationSeconds: 3600, isPaused: false, isSlaBreached: index === 5, pauseHistory: [] }))
let version = { id: 1, pipelineDefinitionId: 1, versionNumber: 1, status: 'Published', scopeType: 'Position', slaMode: 'StageEntry', overallSlaMinutes: 0, allRejectedRollback: { enabled: true, targetStageCode: 'SOURCE' }, stages, transitions: stages.slice(0, -1).map((stage, index) => ({ id: index + 1, pipelineVersionId: 1, fromStageId: stage.id, toStageId: stages[index + 1].id, fromStageCode: stage.stageCode, toStageCode: stages[index + 1].stageCode, outcomeCode: 'ADVANCE', actionLabel: 'Next', requiresReason: false, isActive: true, displayOrder: index + 1, rules: [] })) }
const definition = { id: 1, clientId: 20, clientName: 'Synthetic Client', pipelineCode: 'TEST', pipelineName: 'Synthetic hiring journey', isActive: true, description: '', currentPublishedVersionId: 1 }
const hiringCase = { id: 5, clientId: 20, positionId: 10, positionName: 'Synthetic Architect', workOrderId: 1, workOrderLineId: 1, workOrderNumber: 'WO-TEST', pipelineVersionId: 1, pipelineName: definition.pipelineName, currentStageInstanceId: 107, currentStageName: visits.at(-1).stageName, status: 'Active', slaAnchorAtUtc: visits[0].enteredAtUtc, overallDueAtUtc: '2026-11-20T09:00:00Z', isPostInterviewStage: true, stages: visits, events: [{ id: 1, positionPipelineInstanceId: 5, positionStageInstanceId: 106, eventType: 'ProfilesReworkStarted', eventTitle: 'Profiles returned for sourcing', eventDetails: 'All candidates rejected. Original SLA anchor retained.', actorName: 'Synthetic recruiter', createdAtUtc: visits[5].enteredAtUtc }] }
const user = { id: 1, clientId: 20, displayName: 'Synthetic administrator', email: 'admin@example.invalid', roles: ['client_admin'], permissions: ['settings.manage', 'recruitment.manage'], isActive: true }
fs.writeFileSync(fixture, `import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import WorkOrders from '/src/components/RecruitmentWorkOrderWorkspace.tsx';import CandidateBoard from '/src/components/RecruitmentPipelineBoard.tsx';import Designer from '/src/components/RecruitmentPipelineDesigner.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';
const screen=new URLSearchParams(location.search).get('screen');ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><main style={{padding:16,height:'100vh',boxSizing:'border-box',overflow:'auto'}}>{screen==='candidate'?<CandidateBoard initialClientId={20} clientScopeManaged positionId={10} displayMode="pipeline"/>:screen==='rules'?<Designer initialClientId={20}/>:<WorkOrders initialClientId={20} clientScopeManaged postInterview/>}</main></AuthGate></ToastProvider></BrowserRouter>);`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial;background:#f6f8fc}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixtureName}');</script></body></html>`
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 20 })
const page = await browser.newPage({ viewport: { width: 1366, height: 768 } })
page.on('pageerror', error => errors.push(error.message))
await page.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/journey-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  const reply = value => route.fulfill({ contentType: 'application/json', body: JSON.stringify(value) })
  if (p === '/api/auth/me') return reply(user)
  if (p === '/api/clients') return reply([{ id: 20, name: 'Synthetic Client', isActive: true }])
  if (p === '/api/recruitment/hiring-cases') return reply([hiringCase])
  if (p === '/api/recruitment/hiring-cases/5') return reply(hiringCase)
  if (p === '/api/recruitment/open-positions') return reply([{ id: 10, clientId: 20, clientName: 'Synthetic Client', positionTitle: 'Synthetic Architect', positionCode: 'JOB-TEST', candidateCount: 1 }])
  if (p.endsWith('/applications/100/stage-history')) return reply(visits)
  if (p.endsWith('/pipeline-board')) return reply({ clientId: 20, pipelineVersionId: 1, pipelineName: definition.pipelineName, lanes: [{ stageId: 2, stageCode: 'INTERVIEW', stageName: visits.at(-1).stageName, stageType: 'Interview', slaWarningMinutes: 10, applications: [{ applicationId: 100, candidateId: 100, candidateName: 'Synthetic Candidate', applicationCode: 'APP-TEST', candidateEmail: 'candidate@example.invalid', stageStatus: 'Active', enteredAtUtc: visits.at(-1).enteredAtUtc, elapsedSeconds: 60, pausedDurationSeconds: 0, pendingBlockingActionCount: 0, failedActionCount: 0 }] }] })
  if (p === '/api/recruitment-orchestration/lookups') return reply({ lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [] })
  if (p === '/api/recruitment-orchestration/pipelines') {
    if (request.method() === 'POST') return reply(definition)
    return reply([definition])
  }
  if (p === '/api/recruitment-orchestration/pipelines/1') return reply({ definition, versions: [version] })
  if (p === '/api/recruitment-orchestration/pipelines/1/versions') {
    if (request.method() === 'POST') { const body = request.postDataJSON(); writes.push(body); version = { ...version, ...body, id: 2, versionNumber: 2, status: 'Draft' } }
    return reply(request.method() === 'POST' ? version : [version])
  }
  if (p.startsWith('/api/recruitment-orchestration/pipeline-versions/')) return reply(version)
  if (request.method() !== 'GET') { errors.push(`Unexpected mutation blocked: ${request.method()} ${p}`); return route.fulfill({ status: 403, contentType: 'application/json', body: '{}' }) }
  return reply([])
})
try {
  await page.goto(origin + '/journey-fixture?hiringCaseId=5&stageLog=1')
  const log = page.locator('.stage-time-log-modal')
  await expect(log.locator('.recruitment-journey-visit')).toHaveCount(7)
  assert.deepEqual(await log.locator('.recruitment-journey-visit').evaluateAll(rows => rows.map(row => row.dataset.direction)), ['start', 'forward', 'forward', 'back', 'forward', 'back', 'forward'])
  await expect(log.getByText('Visit 3', { exact: true }).first()).toBeVisible()
  await expect(log.locator('.recruitment-journey-current')).toContainText('Interview / Panel Assessment')
  checks.push('Real job stage log retains all seven forward/backward visits and current stage')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await expect(log.locator('.ant-modal-content')).toHaveCSS('opacity', '1')
    await page.waitForFunction(() => document.querySelectorAll('.recruitment-journey-link').length === 6)
    const geometry = await log.locator('.recruitment-journey-list').evaluate(root => {
      const box = root.getBoundingClientRect(), trackWidth = parseFloat(getComputedStyle(root).getPropertyValue('--journey-track-width'))
      return { overflow: root.scrollWidth > root.clientWidth + 2, xs: [...root.querySelectorAll('.recruitment-journey-node')].map(node => node.getBoundingClientRect().left - box.left), trackWidth }
    })
    assert.equal(geometry.overflow, false)
    assert(geometry.xs[2] > geometry.xs[3] && geometry.xs[4] > geometry.xs[5], 'Rollback route bends toward an earlier stage')
    await log.locator('.ant-modal-body').evaluate(element => { element.scrollTop = element.scrollHeight })
    await expect(log.getByTestId('journey-visit-107')).toBeInViewport()
    await log.locator('.ant-modal-body').evaluate(element => { element.scrollTop = 0 })
    await log.screenshot({ path: path.join(output, `job-journey-${width}.png`), animations: 'disabled' })
    checks.push(`${width}px: route arrows render, no horizontal overflow, final visit reachable by scrolling`)
  }
  await page.setViewportSize({ width: 1366, height: 768 })
  await log.getByTestId('journey-visit-104').scrollIntoViewIfNeeded()
  await log.screenshot({ path: path.join(output, 'job-journey-returns.png'), animations: 'disabled' })
  await log.locator('.recruitment-journey-audit summary').click()
  await expect(log.locator('.case-event-log')).toContainText('Original SLA anchor retained')
  checks.push('Full process audit remains available alongside visual journey')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(origin + '/journey-fixture?screen=candidate')
  await page.locator('.pipeline-card').getByRole('button', { name: /History/ }).click()
  const history = page.locator('.ant-drawer-content-wrapper:visible')
  await expect(history.locator('.recruitment-journey-visit')).toHaveCount(7)
  await expect(history.locator('[data-testid="journey-visit-104"]')).toContainText('Panel requested another assessment')
  await history.screenshot({ path: path.join(output, 'candidate-journey.png'), animations: 'disabled' })
  await history.getByRole('button', { name: 'Close', exact: true }).click()
  await expect(history).not.toBeVisible()
  checks.push('Real candidate History drawer reuses route and decision notes; close works')
  await page.goto(origin + '/journey-fixture?screen=rules')
  await page.locator('.form-builder-library .ant-list-item').filter({ hasText: 'Synthetic hiring journey' }).click()
  const rule = page.getByTestId('pipeline-rollback-rules')
  await expect(rule.getByRole('switch')).toBeDisabled()
  await page.getByTestId('pipeline-edit').click()
  await expect(rule.getByRole('switch')).toBeEnabled()
  await expect(rule.getByRole('switch')).toBeChecked()
  await expect(rule).toContainText('Sharing of profiles')
  await page.getByTestId('pipeline-save').click()
  await expect(page.getByTestId('pipeline-save')).not.toHaveClass(/loading/)
  await expect.poll(() => writes.length).toBe(1)
  assert.deepEqual(writes[0].allRejectedRollback, { enabled: true, targetStageCode: 'SOURCE' })
  await page.reload()
  await page.locator('.form-builder-library .ant-list-item').filter({ hasText: 'Synthetic hiring journey' }).click()
  await expect(page.getByTestId('pipeline-rollback-rules').getByRole('switch')).toBeChecked()
  checks.push('Published rules are protected; Edit pipeline clones the rule, Save draft serializes it, reload restores it')
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, errors, liveBusinessWrites: 0, mockDraftWrites: writes.length }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, evidence: output, liveBusinessWrites: 0 }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  fs.writeFileSync(path.join(output, 'failure.html'), await page.content())
  console.error(JSON.stringify({ error: String(error), checks, errors, evidence: output }))
  process.exitCode = 1
} finally { await browser.close(); fs.rmSync(fixture, { force: true }) }
