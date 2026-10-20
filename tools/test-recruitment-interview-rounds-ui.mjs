import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = process.env.HRMS_PLAYWRIGHT_MODULE ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href) : require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://127.0.0.1:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Use the local UI for this isolated fixture.')
const output = path.join(os.tmpdir(), 'frevo-interview-round-ui-20261017')
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 30 })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } })
const page = await context.newPage(), errors = [], checks = [], writes = []
page.on('pageerror', error => errors.push(error.message))
const user = { id: 1, clientId: 20, displayName: 'Synthetic scheduler', email: 'test@example.invalid', roles: ['client_admin'], permissions: ['recruitment.interview.schedule', 'recruitment.manage'], isActive: true }
const application = { id: 100, clientId: 20, positionId: 10, candidateId: 100, candidateName: 'Synthetic Candidate', candidateEmail: 'candidate@example.invalid', applicationCode: 'APP-TEST-100', positionCode: 'JOB-TEST-10', positionTitle: 'Synthetic Architect', currentStage: 'Interview / Panel Assessment', applicationType: 'Application', isInterviewReady: true, atsScore: 88, atsShortlistThreshold: 60, recruiterName: 'Test recruiter', interviewSettingsMissing: false }
const plan = { applicationId: 100, positionId: 10, stageName: 'Interview / Panel Assessment', revision: 0, isConfigured: false, canConfigure: true, complete: false, completedCount: 0, rounds: [{ name: 'Interview / Panel Assessment', interviewType: 'Technical', status: 'Pending', result: 'Pending', passed: false, canSchedule: true }] }
let stale = false
const fixtureName = `.codex-rounds-fixture-${crypto.randomUUID()}.tsx`
const fixtureFile = fileURLToPath(new URL(`../payroll-ui/${fixtureName}`, import.meta.url))
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root{height:100vh;min-height:0} .fixture-root{height:100%;display:flex;flex-direction:column;min-height:0;padding:12px;box-sizing:border-box}.fixture-root>section{flex:1;min-height:0;overflow:auto}.fixture-root>.talent-workspace{flex:1;min-height:0;overflow:auto}.fixture-toolbar{display:flex;gap:8px;margin-bottom:12px}</style></head><body><div id="root"></div><script type="module">
import RefreshRuntime from '/@react-refresh'; RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;
await import('/${fixtureName}');</script></body></html>`
const module = `
import React from 'react';
import ReactDOM from 'react-dom/client';
import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Workspace from '/src/components/RecruitmentTalentWorkspace.tsx';
import PipelineWorkspace from '/src/components/RecruitmentPipelineWorkspace.tsx';
import {RecruitmentViewContext} from '/src/hooks/useRecruitmentPreferences.ts';
import Rounds from '/src/components/RecruitmentInterviewRounds.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';
function App(){const [view,setView]=React.useState('Cards');const pipeline=new URLSearchParams(location.search).has('pipeline');return React.createElement(BrowserRouter,null,React.createElement(ToastProvider,null,React.createElement(AuthGate,null,React.createElement(RecruitmentViewContext.Provider,{value:{view,scope:'rounds-test'}},React.createElement('div',{className:'fixture-root'},React.createElement('div',{className:'fixture-toolbar'},React.createElement('button',{onClick:()=>setView('Cards')},'Cards fixture'),React.createElement('button',{onClick:()=>setView('Table')},'Table fixture')),pipeline?React.createElement(PipelineWorkspace,{initialClientId:20,clientScopeManaged:true,initialView:'hiring'}):React.createElement(Workspace,{mode:'interviewQueue',initialClientId:20}))))))}ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(App));
`
fs.writeFileSync(fixtureFile, module)
await context.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/rounds-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  const reply = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) })
  if (p === '/api/auth/me') return reply(user)
  if (p === '/api/recruitment/interviews/rounds/100') {
    if (request.method() === 'POST') {
      const body = request.postDataJSON(); writes.push(body)
      if (stale) return reply({ error: 'These rounds changed in another session. Reopen them before saving.' }, 400)
      assert.equal(body.expectedRevision, plan.revision)
      plan.rounds = body.rounds.map((row, index) => ({ ...row, status: 'Pending', result: 'Pending', passed: false, canSchedule: index === 0 }))
      plan.revision++; plan.isConfigured = true
    }
    return reply(plan)
  }
  if (p === '/api/recruitment/interviews/scheduling-context/100') {
    const next = plan.rounds.find(row => row.canSchedule)
    return reply({ applicationId: 100, isPipelineManaged: true, pipelineStageInstanceId: 51, roundConfigurationId: 1, pipelineStageName: plan.stageName, roundCode: next?.name || '', interviewType: next?.interviewType || 'Technical', defaultDurationMinutes: 60, minimumPanelCount: 1, minimumPassingScore: 60, scoreInputMode: 'PercentageWeighted', panelAggregationMethod: 'Average', feedbackRequired: true, calendarEnabled: true, allowReschedule: true, timeZoneId: 'Asia/Kolkata', nextAttemptNumber: 1, competencies: [], defaultPanelUserIds: [], roundPlan: plan })
  }
  if (p === '/api/recruitment/applications') return reply([application,
    { ...application, id: 101, candidateName: 'Ineligible high-score candidate', eligibilityStatus: 'Ineligible', scoreStatus: 'Ineligible', atsScore: 99 },
    { ...application, id: 102, candidateName: 'Unverified high-score candidate', eligibilityStatus: 'NeedsReview', scoreStatus: 'NeedsReview', atsScore: 99 }])
  if (p === '/api/recruitment/pipeline-workspace') return reply({ clientId: 20, unassignedDemandCards: [], lanes: [{ pipelineVersionId: 1, pipelineVersionNumber: 1, pipelineName: 'Synthetic hiring pipeline', stageId: 4, stageCode: 'INTERVIEW', stageName: plan.stageName, stageType: 'Interview', cardScope: 'Position', displayOrder: 1, applications: [], demandCards: [{ clientId: 20, workOrderId: 1, workOrderLineId: 1, workOrderNumber: 'WO-TEST', positionId: 10, positionName: 'Synthetic Architect', hiringCaseId: 5, candidateCount: 1, requiredCandidateCount: 1, inPanelCount: 0, selectedCandidateCount: 0, currentStageName: plan.stageName, currentStageId: 4, interviewRoundsApplicationId: 100, activeDurationSeconds: 0, pausedDurationSeconds: 0, status: 'Active' }] }] })
  if (p === '/api/recruitment/interviews') return reply([])
  if (p === '/api/clients') return reply([{ id: 20, name: 'Synthetic Client', code: 'TEST', isActive: true }])
  if (p.includes('capabilities')) return reply({ enabled: false })
  if (request.method() !== 'GET') { errors.push(`Unexpected mutation: ${request.method()} ${p}`); return reply({ error: 'Mutation blocked by isolated fixture.' }, 403) }
  return reply([])
})
try {
  await page.goto(origin + '/rounds-fixture')
  const rounds = () => page.locator('.candidate-application-card .recruitment-interview-rounds')
  await expect(rounds()).toHaveCount(1)
  await expect(page.getByText('Ineligible high-score candidate', { exact: true })).toHaveCount(0)
  await expect(page.getByText('Unverified high-score candidate', { exact: true })).toHaveCount(0)
  checks.push('Ineligible and unverified high-score candidates are excluded from interview scheduling')
  assert.equal(await rounds().getAttribute('open'), null)
  checks.push('Scheduling card starts collapsed and does not duplicate the round section')
  await rounds().locator('summary').click()
  await rounds().getByRole('button', { name: 'Configure rounds' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Round 1 name' }).fill('Technical L1')
  await dialog.getByRole('button', { name: 'Add round' }).click()
  await dialog.getByRole('textbox', { name: 'Round 2 name' }).fill('Technical L2')
  await dialog.getByRole('button', { name: 'Add round' }).click()
  await dialog.getByRole('textbox', { name: 'Round 3 name' }).fill('Client discussion')
  await dialog.getByRole('textbox', { name: 'Round 3 type' }).fill('Client')
  await dialog.getByRole('button', { name: 'Save rounds' }).click()
  await expect(dialog).toHaveCount(0)
  await expect(rounds().locator('summary')).toContainText('0/3 completed')
  assert.equal(writes.length, 1)
  checks.push('Three custom rounds save once through the existing scheduling workspace')
  await page.reload()
  await rounds().locator('summary').click()
  await expect(rounds()).toContainText('Client discussion')
  await rounds().locator('summary').click()
  assert.equal(await rounds().getAttribute('open'), null)
  await expect(rounds().locator('summary')).toContainText('0/3 completed')
  checks.push('Reopen restores names; closing retains compact progress summary')
  plan.completedCount = 1
  Object.assign(plan.rounds[0], { passed: true, canSchedule: false, status: 'Completed', result: 'Selected', interviewId: 1 })
  plan.rounds[1].canSchedule = true
  await rounds().locator('summary').click()
  await expect(rounds().locator('summary')).toContainText('1/3 completed')
  await rounds().getByRole('button', { name: 'Schedule next round' }).click()
  const drawer = page.locator('.recruitment-editor-drawer')
  await expect(drawer.locator('.ant-form-item').filter({ has: page.locator('label').getByText('Round', { exact: true }) }).getByRole('textbox')).toHaveValue('Technical L2')
  await drawer.getByRole('button', { name: 'Cancel', exact: true }).click()
  checks.push('L1 complete automatically selects L2 in the reused schedule drawer')
  await page.getByRole('button', { name: 'Table fixture' }).click()
  const tableRounds = page.locator('tbody .recruitment-interview-rounds').first()
  await expect(tableRounds).toBeVisible()
  await expect(page.getByText('Ineligible high-score candidate', { exact: true })).toHaveCount(0)
  await expect(page.getByText('Unverified high-score candidate', { exact: true })).toHaveCount(0)
  await tableRounds.locator('summary').click()
  await expect(tableRounds).toContainText('1/3 completed')
  checks.push('Table view uses the same expandable round component')
  await page.getByRole('button', { name: 'Cards fixture' }).click()
  await rounds().locator('summary').click()
  await rounds().getByRole('button', { name: 'Configure rounds' }).click()
  stale = true
  await dialog.getByRole('button', { name: 'Save rounds' }).click()
  await expect(dialog).toContainText('changed in another session')
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  stale = false
  checks.push('Stale configuration error remains visible without overwriting a saved plan')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await rounds().getByRole('button', { name: 'Configure rounds' }).click()
    await expect(dialog).not.toHaveClass(/zoom-(appear|enter)/)
    await expect(dialog).toHaveCSS('opacity', '1')
    await expect(dialog.getByRole('button', { name: 'Save rounds' })).toBeInViewport()
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1))
    await page.screenshot({ path: path.join(output, `rounds-${width}.png`), animations: 'disabled' })
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  }
  checks.push('Desktop/tablet/mobile configuration stays reachable without horizontal page overflow')
  user.permissions = ['recruitment.interview.panel']; plan.canConfigure = false
  await page.reload(); await rounds().locator('summary').click()
  await expect(rounds().getByRole('button', { name: 'Configure rounds' })).toHaveCount(0)
  checks.push('Panel viewer does not receive configuration controls')
  user.permissions = ['recruitment.interview.schedule', 'recruitment.manage']; plan.canConfigure = true
  plan.complete = true; plan.completedCount = 3
  plan.rounds.forEach(round => Object.assign(round, { passed: true, canSchedule: false, status: 'Completed', result: 'Selected' }))
  await page.reload(); await rounds().locator('summary').click()
  await expect(rounds().locator('summary')).toContainText('3/3 completed')
  await expect(rounds().getByRole('button', { name: 'Schedule next round' })).toHaveCount(0)
  checks.push('All rounds cleared removes the next-round scheduling action')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(origin + '/rounds-fixture?pipeline=1')
  const pipelineRounds = page.locator('.demand-card .recruitment-interview-rounds')
  await expect(pipelineRounds).toHaveCount(1)
  assert.equal(await pipelineRounds.getAttribute('open'), null)
  await pipelineRounds.locator('summary').click()
  await expect(pipelineRounds.locator('summary')).toContainText('3 rounds')
  await expect(pipelineRounds).toContainText('Technical L2')
  await pipelineRounds.getByText('Technical L2', { exact: false }).scrollIntoViewIfNeeded()
  await expect(pipelineRounds.getByRole('button', { name: 'Configure rounds' })).toHaveCount(0)
  await page.screenshot({ path: path.join(output, 'pipeline-rounds.png'), animations: 'disabled' })
  await pipelineRounds.locator('summary').click()
  assert.equal(await pipelineRounds.getAttribute('open'), null)
  checks.push('Hiring pipeline job card shows the same rounds behind a collapsed read-only expander')
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, errors, writes: writes.length, mockedApi: true, liveWrites: false }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, mockedApi: true, liveWrites: false, output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png') })
  fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ errors, body: await page.locator('body').innerText() }, null, 2))
  throw error
} finally { await browser.close(); fs.rmSync(fixtureFile, { force: true }) }
