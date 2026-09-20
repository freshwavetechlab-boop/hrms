import { createRequire } from 'node:module'
import path from 'node:path'
import fs from 'node:fs'
import assert from 'node:assert/strict'
import { verifyPublicIntake } from './test-recruitment-public-intake.mjs'
import { optionalSkillYears } from './recruitment-skill-years-policy.mjs'
import { verifyOfferFlow } from './test-recruitment-offer-flow.mjs'
import { verifySigningSettings } from './test-recruitment-signing-settings.mjs'

export async function runRecruitmentPortalChecks({ root, ui, apiUrl, output, password, db, mail, seed }) {
  const require = createRequire(path.join(root, 'playwright-e2e/package.json'))
  const { chromium, expect } = require('@playwright/test')
  const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 160, args: ['--start-maximized'] })
  const checks = [], pageErrors = [], apiErrors = [], disabledFeatureResponses = []
  let activePage
  async function step(page, label) {
    activePage = page; await page.bringToFront()
    const launcherClose = page.getByRole('button', { name: 'Close app modules', exact: true })
    if (await launcherClose.isVisible()) await launcherClose.click()
    await page.evaluate(label => {
      let bar = document.getElementById('workflow-test-mode')
      if (!bar) { bar = document.createElement('div'); bar.id = 'workflow-test-mode'; document.body.append(bar) }
      Object.assign(bar.style, { position: 'fixed', bottom: '0', left: '0', right: '0', zIndex: '2147483647', background: '#102b46', color: '#fff', padding: '16px', fontSize: '16px', pointerEvents: 'none' })
      bar.textContent = `TEST MODE • Real isolated API • ${label}`
    }, label)
    console.log(label)
    await page.waitForTimeout(Number(process.env.HRMS_TEST_STEP_MS || 2200))
    await page.screenshot({ path: path.join(output, `${checks.length + 1}-${label.replace(/[^a-z0-9]/gi, '-').slice(0, 80)}.png`), fullPage: true })
    checks.push(label)
  }
  async function freshPage() {
    const context = await browser.newContext({ viewport: { width: 1440, height: 980 } })
    await context.route('**/api/**', async route => {
      const u = new URL(route.request().url())
      const response = await route.fetch({ url: apiUrl + u.pathname + u.search, maxRedirects: 0, timeout: 90000 })
      if (u.pathname === '/api/recruitment/internal-interviews/capabilities' && response.status() === 503)
        disabledFeatureResponses.push({ path: u.pathname, status: 503, reason: 'Separate internal-interview goal deliberately disabled in this fixture' })
      else if (response.status() >= 500) apiErrors.push({ path: u.pathname, status: response.status(), error: (await response.text()).slice(0, 1200) })
      await route.fulfill({ response })
    })
    const page = await context.newPage(); page.on('pageerror', e => pageErrors.push(e.message)); activePage = page
    return page
  }
  async function login(who, route = '/recruitment/applications') {
    const page = await freshPage()
    await page.goto(ui + route)
    await page.getByPlaceholder('Enter email or login ID').fill(`${who}@example.invalid`)
    await page.getByPlaceholder('Enter password').fill(password)
    await page.getByRole('button', { name: 'Sign in', exact: true }).click()
    await expect(page.getByPlaceholder('Enter password')).toHaveCount(0, { timeout: 30000 })
    await page.goto(ui + route)
    return page
  }
  const call = (page, url, data, method = data === undefined ? 'GET' : 'POST') => page.evaluate(async ({ url, data, method }) => {
    const response = await fetch(url, { method, credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data) })
    const text = await response.text(); let body; try { body = JSON.parse(text) } catch { body = text.slice(0, 1500) }
    return { status: response.status, ok: response.ok, body }
  }, { url, data, method })
  const ok = r => { assert(r.ok, JSON.stringify(r)); return r.body }
  try {
    const admin = await login('admin')
    await step(admin, 'Super-admin login and application list')
    await expect.poll(async () => {
      const [rows] = await db.query('SELECT CurrentStage FROM recruitment_candidate_applications WHERE Id=901')
      return rows[0].CurrentStage
    }, { timeout: 90000, intervals: [1000, 2000, 3000] }).toBe('Interview / Panel Assessment')
    const [review] = await db.query('SELECT CurrentStage FROM recruitment_candidate_applications WHERE Id=902')
    assert.equal(review[0].CurrentStage, 'ATS Screening')
    await admin.reload()
    await step(admin, 'v2 auto-progresses qualified candidate; NeedsReview stays held')
    const apps = ok(await call(admin, '/api/recruitment/applications'))
    assert.equal(apps.find(a => a.id === 901).canMoveToGlobalTalentPool, false)
    assert.equal((await call(admin, '/api/recruitment/applications/901/global-talent-pool', {})).ok, false)
    await admin.goto(ui + '/recruitment/interview-queue')
    await expect(admin.getByText('Qualified Candidate', { exact: true }).first()).toBeVisible({ timeout: 15000 })
    await step(admin, 'Schedule Interviews includes qualified candidate')
    // Exercise the same existing scheduling endpoint as the drawer; UI verifies its result.
    const schedule = { applicationId: 901, roundCode: 'Regression panel', interviewType: 'Technical', mode: 'Virtual', locationOrLink: 'https://meeting.example.invalid/test', scheduledStart: new Date(Date.now() + 3600000).toISOString(), scheduledEnd: new Date(Date.now() + 7200000).toISOString(), panelUserIds: [9002, 9003], status: 'Scheduled', result: 'Pending', timeZoneId: 'Asia/Kolkata' }
    const interview = ok(await call(admin, '/api/recruitment/interviews', schedule)); schedule.id = interview.id
    ok(await call(admin, `/api/recruitment/interviews/${interview.id}/invite`, {}))
    assert(mail.deliveries.filter(m => ['qualified@example.invalid', 'panel1@example.invalid', 'panel2@example.invalid'].includes(m.recipient)).length >= 3)
    await admin.goto(ui + '/recruitment/interviews')
    assert.equal((ok(await call(admin, '/api/recruitment/interviews'))).find(i => i.id === interview.id).canRecordDecision, false)
    assert.equal((await call(admin, '/api/recruitment/interviews', { ...schedule, status: 'Completed', result: 'Selected' })).ok, false)
    await step(admin, 'No Select or Reject before both panel feedbacks')
    const panel1 = await login('panel1', '/recruitment/interviews')
    await panel1.getByRole('button', { name: 'Notifications', exact: true }).click()
    await expect(panel1.getByText('Your panel feedback is pending', { exact: true })).toBeVisible()
    await step(panel1, 'Panel One bell shows own pending feedback')
    await panel1.getByRole('button', { name: /Your panel feedback is pending/ }).click()
    await expect(panel1.getByRole('button', { name: 'Save feedback', exact: true })).toBeVisible({ timeout: 15000 })
    const feedback = { panelUserId: 9002, overallScore: 88, recommendation: 'Hire', comments: 'Real API regression evidence', competencyScoresJson: '{}', competencyScores: [] }
    assert.equal((await call(panel1, `/api/recruitment/interviews/${interview.id}/feedback`, { ...feedback, panelUserId: 9003 })).ok, false)
    assert.equal((await call(panel1, '/api/recruitment/applications/901/view-as-candidate', {})).status, 403)
    ok(await call(panel1, `/api/recruitment/interviews/${interview.id}/feedback`, feedback))
    await panel1.reload(); await step(panel1, 'Panel One saved; cannot impersonate Panel Two or candidate')
    const panel2 = await login('panel2', '/recruitment/interviews')
    ok(await call(panel2, `/api/recruitment/interviews/${interview.id}/feedback`, { ...feedback, panelUserId: 9003 }))
    await panel2.reload(); await step(panel2, 'Panel Two submits own independent feedback')
    await admin.reload(); await admin.bringToFront()
    assert.equal((ok(await call(admin, '/api/recruitment/interviews'))).find(i => i.id === interview.id).canRecordDecision, true)
    await expect(admin.getByRole('button', { name: 'Select', exact: true }).first()).toBeVisible()
    await step(admin, 'Decision unlocked after both feedbacks')
    ok(await call(admin, '/api/recruitment/interviews', { ...schedule, status: 'Completed', result: 'Selected' }))
    assert.equal((await call(panel1, `/api/recruitment/interviews/${interview.id}/feedback`, feedback)).ok, false)
    await admin.reload(); await step(admin, 'Completed interview feedback is read-only')
    const preview = await call(admin, '/api/recruitment/applications/901/view-as-candidate', {})
    assert.equal(ok(preview).applications[0].processingStatus, 'Completed')
    const reviewPreview = ok(await call(admin, '/api/recruitment/applications/902/view-as-candidate', {}))
    assert.equal(reviewPreview.applications[0].processingStatus, 'NeedsReview')
    await admin.goto(ui + '/recruitment/applications')
    await admin.getByText('Qualified Candidate', { exact: true }).first().click()
    await admin.getByRole('button', { name: 'View as candidate', exact: true }).first().click()
    await expect(admin.getByText('Candidate view — read-only', { exact: true })).toBeVisible()
    await step(admin, 'Super-admin audited candidate preview without OTP or PIN exposure')
    const approver = await login('approver', '/tasks')
    await approver.getByRole('button', { name: 'Notifications', exact: true }).click()
    await expect(approver.getByRole('button', { name: /Approve hiring budget/ })).toBeVisible()
    await step(approver, 'Budget task reaches the selected approver bell')
    assert.equal((await call(admin, '/api/workflows/tasks/901/Approved', { comment: 'Wrong approver test' })).ok, false)
    ok(await call(approver, '/api/workflows/tasks/901/Approved', { comment: 'Approve isolated 10 lakh budget' }))
    const [budget] = await db.query('SELECT BudgetApprovalStatus FROM recruitment_requisitions WHERE Id=901')
    assert.equal(budget[0].BudgetApprovalStatus, 'Approved')
    await approver.reload(); await step(approver, 'Only assigned approver approves; task and budget update together')
    await verifySigningSettings({ admin, approver, panel1, call, ok, step, db, seed, ui, expect })
    await verifyOfferFlow({ admin, approver, panel1, panel2, call, ok, step, db, ui, seed, freshPage, expect })
    await admin.goto(ui + '/recruitment/work-orders-and-sla')
    await expect(admin.locator('.hiring-case-list h3').filter({ hasText: 'Chief Architect' })).toHaveCount(1)
    await expect(admin.getByText('Earlier journeys — history retained', { exact: true })).toBeVisible()
    await step(admin, 'Chief Architect current journey once; earlier record in history')
    await admin.goto(ui + '/recruitment/job-postings')
    await expect(admin.locator('.job-summary-card h4').filter({ hasText: 'Software Architect' })).toHaveCount(1)
    await admin.getByTestId('job-posting-history').locator('summary').click()
    const [slugs] = await db.query('SELECT PublicSlug FROM recruitment_job_postings WHERE Id IN (901,902) ORDER BY Id')
    assert.deepEqual(slugs.map(r => r.PublicSlug), ['test-901', 'test-902'])
    await step(admin, 'One current job card; history and original public URLs retained')
    await expect.poll(async () => {
      const [rows] = await db.query("SELECT COUNT(*) count FROM notification_queue WHERE EventCode='RECRUITMENT_HIRING_STAGE_MOVED' AND Status='Sent'")
      return rows[0].count
    }, { timeout: 90000, intervals: [2000] }).toBeGreaterThanOrEqual(4)
    assert(mail.deliveries.some(m => m.recipient === 'admin@example.invalid'))
    await step(admin, 'Interview invites and seeded hiring-stage update delivered to local mail sink')
    await db.query("INSERT INTO recruitment_jd_skill_requirements(JobDescriptionVersionId,SkillName,IsRequired,MinimumYears) VALUES(901,'Kubernetes',TRUE,5)")
    await db.query("INSERT INTO recruitment_jd_responsibilities(JobDescriptionVersionId,ResponsibilityText) VALUES(901,'Build reliable systems')")
    await db.query("INSERT INTO recruitment_jd_qualification_requirements(JobDescriptionVersionId,QualificationName,Specialization,IsMandatory) VALUES(901,'B.Tech','Computer Science',TRUE)")
    await db.query("INSERT INTO recruitment_jd_certification_requirements(JobDescriptionVersionId,CertificationName,IsMandatory) VALUES(901,'AWS',FALSE)")
    await db.query("INSERT INTO recruitment_jd_language_requirements(JobDescriptionVersionId,LanguageName,Proficiency) VALUES(901,'English','Professional')")
    await db.query("INSERT INTO recruitment_jd_benefits(JobDescriptionVersionId,BenefitName,Description) VALUES(901,'Insurance','Standard benefits')")
    const dryPolicy = await optionalSkillYears(db, { jdIds: [901], actorId: 9001 })
    assert.equal(dryPolicy[0].applied, false)
    const revisedPolicy = await optionalSkillYears(db, { jdIds: [901], actorId: 9001, apply: true })
    assert(revisedPolicy[0].newJdId > 901)
    assert.deepEqual(revisedPolicy[0].recheckApplicationIds, []) // No held duration evidence in this fixture; do not rescore completed selection.
    const repeatPolicy = await optionalSkillYears(db, { jdIds: [901], actorId: 9001, apply: true })
    assert.equal(repeatPolicy[0].skipped, 'No current duration rules')
    await admin.reload()
    await step(admin, 'Skill-years policy creates one audited version; old version and public links preserved')
    await verifyPublicIntake({ admin, call, ok, step, db, seed, freshPage, ui, expect })
    const [unrelatedPolicy] = await db.query('SELECT AutoRunAts,EnableResumeParsing,EnableAiParsing,RequireEmailOtp FROM recruitment_job_postings WHERE Id=905')
    const [[completedJobs]] = await db.query('SELECT COUNT(*) n FROM recruitment_ats_scoring_jobs WHERE ApplicationId=901')
    const [[heldJobs]] = await db.query('SELECT COUNT(*) n FROM recruitment_ats_scoring_jobs WHERE ApplicationId=902')
    const policyPath = id => `/api/recruitment-orchestration/job-postings/${id}/auto-run-ats`
    const disabledPolicy = { autoRunAts: false, enableResumeParsing: true, enableAiParsing: false, requireEmailOtp: true }
    assert.equal((await call(panel1, policyPath(901), disabledPolicy)).status, 403)
    ok(await call(admin, policyPath(901), disabledPolicy))
    const [disabledLinks] = await db.query('SELECT AutoRunAts,RequireEmailOtp FROM recruitment_job_postings WHERE Id IN (901,902)')
    assert(disabledLinks.every(row => !row.AutoRunAts && row.RequireEmailOtp))
    const toggleStarted = Date.now()
    ok(await call(admin, policyPath(902), { ...disabledPolicy, autoRunAts: true, requireEmailOtp: false }))
    assert(Date.now() - toggleStarted < 10000, 'Saving policy must enqueue, not await AI inference')
    const [enabledLinks] = await db.query('SELECT AutoRunAts,RequireEmailOtp,PublicSlug FROM recruitment_job_postings WHERE Id IN (901,902) ORDER BY Id')
    assert(enabledLinks.every(row => row.AutoRunAts && !row.RequireEmailOtp))
    assert.deepEqual(enabledLinks.map(row => row.PublicSlug), ['test-901', 'test-902'])
    assert.deepEqual((await db.query('SELECT AutoRunAts,EnableResumeParsing,EnableAiParsing,RequireEmailOtp FROM recruitment_job_postings WHERE Id=905'))[0], unrelatedPolicy)
    const [[afterCompleted]] = await db.query('SELECT COUNT(*) n FROM recruitment_ats_scoring_jobs WHERE ApplicationId=901')
    const [[afterHeld]] = await db.query('SELECT COUNT(*) n FROM recruitment_ats_scoring_jobs WHERE ApplicationId=902')
    assert.equal(afterCompleted.n, completedJobs.n, 'Accepted/interviewed application must not be requeued')
    assert(afterHeld.n > heldJobs.n, 'Existing ATS-stage application should be queued')
    const [[manual]] = await db.query("SELECT COUNT(*) n FROM person_activity_events WHERE EventType='ATS_MANUAL_REQUESTED' AND CandidateId=902")
    assert.equal(manual.n, 0, 'Changing policy must not manufacture a human override')
    await admin.goto(ui + '/recruitment/job-postings')
    await expect(admin.locator('.job-summary-card h4').filter({ hasText: 'Software Architect' })).toHaveCount(1)
    await step(admin, 'Both live URLs share intake policy; ATS queues without rescoring accepted candidates')
    assert.deepEqual(pageErrors, []); assert.deepEqual(apiErrors, [])
    await step(admin, 'BATCH COMPLETE — real API checks passed; see result.json')
    return { evidence: 'Visible actual portal + actual API + disposable loopback MySQL; seeded business data; SMTP delivers only to local synthetic sink; no cloud inference', checks, pageErrors, apiErrors, disabledFeatureResponses, localMailDeliveries: mail.deliveries.length }
  } catch (error) {
    if (activePage) await activePage.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
    const [publicState] = await db.query('SELECT a.Id,a.CurrentStage,p.PipelineVersionId,s.ScoreStatus,s.TotalScore FROM recruitment_candidate_applications a LEFT JOIN recruitment_application_pipeline_instances p ON p.ApplicationId=a.Id LEFT JOIN recruitment_application_scores s ON s.ApplicationId=a.Id AND s.IsCurrent=TRUE WHERE a.PositionId=905').catch(() => [[]])
    fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ checks, pageErrors, apiErrors, disabledFeatureResponses, publicState, error: error.message }, null, 2))
    throw error
  } finally {
    if (process.env.HRMS_TEST_HEADLESS !== '1') await new Promise(resolve => setTimeout(resolve, 12000))
    await browser.close()
  }
}
