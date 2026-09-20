import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import assert from 'node:assert/strict'

// Launched only by the disposable-database C# fixture. All API calls reach the real API.
// No credentials are written to screenshots, traces, files or command arguments.
const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = require('@playwright/test')
const ui = process.env.HRMS_TEST_UI_URL, api = process.env.HRMS_TEST_API_URL
const mode = process.env.HRMS_TEST_INTERVIEW_MODE || 'Human'
if (!['Human', 'AI', 'Hybrid'].includes(mode)) throw Error('Unknown fixture interview mode.')
for (const value of [ui, api]) if (!['localhost', '127.0.0.1'].includes(new URL(value).hostname)) throw Error('Loopback-only fixture.')
if (!process.env.HRMS_TEST_PASSWORD) throw Error('The isolated fixture password is required.')
const output = path.resolve('.codex-validation/internal-interviews/portal', mode.toLowerCase())
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 30,
  args: ['--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows'] })
const checks = [], errors = [], failedApi = []
let interviewId = 0, link = ''
async function attach(context) {
  // The built portal may name its normal local API. Rewrite transport destination only;
  // auth middleware, repositories, response bodies and cookies all come from actual Program.cs.
  await context.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    try {
      const response = await route.fetch({ url: api + url.pathname + url.search, maxRedirects: 0 })
      if (url.pathname === '/api/auth/login' && !response.ok()) failedApi.push({ path: url.pathname, status: response.status(), body: (await response.json().catch(() => ({}))).error || 'Login denied', fixturePasswordMatches: route.request().postDataJSON()?.password === process.env.HRMS_TEST_PASSWORD })
      if (response.status() >= 500 || (response.status() >= 400 && route.request().method() !== 'GET')) failedApi.push({ path: url.pathname, status: response.status(), body: (await response.text()).slice(0, 1800) })
      if (url.pathname === '/api/recruitment/interviews' && route.request().method() === 'POST' && response.ok()) interviewId = (await response.json()).id
      if (url.pathname.endsWith('/link') && response.ok()) link = (await response.json()).url
      await route.fulfill({ response })
    } catch (error) { failedApi.push({ path: url.pathname, transport: String(error.message).split('\n')[0] }); await route.abort('failed') }
  })
  context.on('page', page => page.on('pageerror', error => errors.push(error.message)))
}
try {
  const hr = await browser.newContext({ viewport: { width: 1440, height: 1000 } }); await attach(hr)
  const page = await hr.newPage()
  await page.goto(`${ui}/recruitment/interview-queue`)
  await page.getByPlaceholder('Enter email or login ID').fill('interview.panel@example.invalid')
  await page.getByPlaceholder('Enter password').fill(process.env.HRMS_TEST_PASSWORD)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByPlaceholder('Enter password')).toHaveCount(0, { timeout: 30000 })
  checks.push('actual HR login and HTTP session cookie')
  await page.goto(`${ui}/recruitment/interview-queue`)
  await page.getByRole('button', { name: /Schedule one$/ }).click({ timeout: 30000 })
  const drawer = page.locator('.ant-drawer-content').filter({ has: page.getByText('Schedule interview', { exact: true }) })
  const field = (label, container = page) => container.locator('.ant-form-item').filter({ has: page.locator('label').filter({ hasText: new RegExp(`^${label}$`) }) })
  await field('Application', drawer).locator('.ant-select-selector').click()
  await page.locator('.ant-select-item-option').filter({ hasText: 'Isolated Candidate' }).click()
  await field('Panel members', drawer).locator('.ant-select-selector').click()
  await page.locator('.ant-select-item-option').filter({ hasText: 'Isolated Interview Panel' }).click()
  await field('Round', drawer).getByRole('textbox').click()
  await drawer.getByRole('checkbox', { name: 'Use internal HRMS interview' }).check()
  // Schedule is the ordinary date/time form. Choose now + 5m so waiting/start is in window.
  const start = new Date(Date.now() + 5 * 60000), end = new Date(start.getTime() + 60 * 60000)
  const dateParts = date => {
    const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kolkata', day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(date).map(p => [p.type, p.value]))
    const numeric = Object.fromEntries(new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kolkata', day: '2-digit', month: '2-digit', year: 'numeric' }).formatToParts(date).map(p => [p.type, p.value]))
    return { ...parts, dayKey: `${numeric.year}-${numeric.month}-${numeric.day}` }
  }
  const dates = field('Date and time', drawer).locator('input')
  for (const [index, date] of [start, end].entries()) {
    const parts = dateParts(date)
    await dates.nth(index).click()
    const picker = page.locator('.ant-picker-dropdown:not(.ant-picker-dropdown-hidden)')
    await picker.locator(`.ant-picker-cell-in-view[title="${parts.dayKey}"]`).first().click()
    const columns = picker.locator('.ant-picker-time-panel-column')
    await columns.nth(0).getByText(parts.hour, { exact: true }).click()
    await columns.nth(1).getByText(parts.minute, { exact: true }).click()
    await picker.locator('.ant-picker-ok button').click()
  }
  await field('Round', drawer).getByRole('textbox').click()
  await drawer.getByRole('button', { name: 'Save schedule & configure internal session', exact: true }).click()
  await expect.poll(() => interviewId, { timeout: 20000 }).toBeGreaterThan(0)
  await expect(page.getByRole('button', { name: 'Save internal interview', exact: true })).toBeVisible({ timeout: 15000 })
  checks.push('existing Schedule Interviews drawer saves actual interview and opens internal setup')
  await field('Skill').getByRole('textbox').fill('Integration testing')
  await field('Question').getByRole('textbox').fill('Explain a cross-client access test.')
  await field('Expected evaluation criteria').getByRole('textbox').fill('Describe both allowed and denied access.')
  await field('Approved follow-up questions').getByRole('textbox').fill('How would you confirm the denied access?')
  await page.getByRole('button', { name: 'Add question', exact: true }).click()
  if (mode !== 'Human') {
    await field('Interview mode').locator('.ant-select-selector').click()
    await page.getByText(mode === 'AI' ? 'AI Interview' : 'Human + AI Hybrid', { exact: true }).click()
  }
  await field('Question set').locator('.ant-select-selector').click()
  await page.locator('.ant-select-item-option').filter({ hasText: 'Explain a cross-client access test.' }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('checkbox', { name: 'Request transcription consent', exact: true }).check()
  await page.getByRole('button', { name: 'Save internal interview', exact: true }).click()
  await page.getByRole('button', { name: 'Generate candidate link', exact: true }).click()
  await page.getByRole('button', { name: 'OK', exact: true }).click()
  await expect.poll(() => link).not.toBe('')
  checks.push('actual scoped question bank, configuration and signed candidate link')
  const sendInvite = async () => {
    const response = page.waitForResponse(r => /\/interviews\/\d+\/invite$/.test(r.url()) && r.request().method() === 'POST')
    await page.getByRole('button', { name: 'Send candidate & panel invites', exact: true }).click()
    const delivered = await response
    assert(delivered.ok(), 'Isolated SMTP invite must succeed')
    assert.equal((await delivered.json()).recipientCount, 2)
  }
  await sendInvite()
  checks.push('actual notification service delivers separate candidate/panel invites to loopback-only SMTP sink')
  const candidate = await browser.newContext({ viewport: { width: 1100, height: 900 } }); await attach(candidate)
  const cp = await candidate.newPage()
  const signed = new URL(link)
  await cp.goto(ui + signed.pathname + signed.hash)
  await cp.getByRole('checkbox', { name: /privacy and browser-event notice/ }).check()
  await cp.getByRole('checkbox', { name: /local transcription/ }).check()
  await cp.getByRole('button', { name: 'Agree and enter waiting room' }).click()
  await expect(cp.getByText('You are in the waiting room. The panel will start your interview.')).toBeVisible()
  await page.bringToFront()
  await sendInvite()
  await cp.reload()
  await expect(cp.getByText('You are in the waiting room. The panel will start your interview.')).toBeVisible()
  checks.push('resending signed invitations preserves existing candidate link and consent')
  await page.getByRole('button', { name: 'Start interview', exact: true }).click({ timeout: 12000 })
  const answer = async (question, text) => {
    await cp.bringToFront()
    await expect(cp.getByText(question, { exact: true })).toBeVisible({ timeout: 15000 })
    const input = cp.getByPlaceholder('Type your answer or use the configured voice interview.')
    await expect(input).toBeEnabled({ timeout: 10000 }); await input.fill(text)
    const savedAnswer = cp.waitForResponse(r => r.url().endsWith('/events') && r.request().method() === 'POST' && r.request().postDataJSON()?.kind === 'answer')
    await cp.locator('button').filter({ hasText: /^Submit answer$/ }).click()
    assert((await savedAnswer).ok())
    await expect(page.getByText(text, { exact: true })).toBeVisible({ timeout: 12000 })
  }
  if (mode !== 'AI') {
    const question = mode === 'Human' ? 'Explain a cross-client access test.' : 'Describe how you prepare test accounts.'
    await page.getByPlaceholder('Ask an instant question or save a private panel note.').fill(question)
    const ask = page.locator('button').filter({ hasText: /^Ask question$/ })
    await expect(ask).toBeEnabled()
    assert.equal(await ask.evaluate(node => !!node.closest('[aria-hidden="true"]')), false)
    // AntD temporarily includes its loading icon in the accessible name after Start.
    // Wait for actual action readiness, not a fixed sleep or force-click.
    await expect(ask).toHaveAccessibleName('Ask question')
    await ask.click()
    await answer(question, 'I verify tenant A cannot read tenant B data.')
    checks.push('human question/answer persists through actual candidate and panel APIs')
  }
  if (mode === 'Hybrid') {
    await page.bringToFront()
    await page.getByRole('combobox', { name: 'AI interview section' }).click()
    await page.locator('.ant-select-item-option').filter({ hasText: 'Integration testing' }).click()
    await page.getByRole('button', { name: 'Hand control to AI', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Resume human control', exact: true })).toBeVisible()
  }
  if (mode !== 'Human') {
    await answer('Explain a cross-client access test.', 'I test cross-client requests and inspect each denied response.')
    await answer('How would you confirm the denied access?', 'I inspect denied read results for tenant B.')
    await expect(page.getByText(/The selected question section is complete/).first()).toBeVisible({ timeout: 15000 })
    checks.push('actual durable AI bank-question and approved-followup orchestration with a mocked model; answers persist')
    if (mode === 'Hybrid') {
      await page.bringToFront()
      await page.getByRole('button', { name: 'Resume human control', exact: true }).click()
      await expect(page.getByRole('button', { name: 'Hand control to AI', exact: true })).toBeVisible()
      checks.push('hybrid section selection and return to human control persist')
    }
  }
  await page.bringToFront()
  await page.getByRole('button', { name: 'Complete interview session', exact: true }).click()
  await page.getByRole('button', { name: 'OK', exact: true }).click()
  await expect(page.getByText('Interview session ended', { exact: true })).toBeVisible()
  await expect(page.locator('.ant-modal-wrap:visible')).toHaveCount(0)
  await expect(page.getByText('AI review did not return an object.', { exact: true }).first()).toBeVisible({ timeout: 15000 })
  await page.getByRole('button', { name: 'Retry failed AI review', exact: true }).click()
  const expectedReviews = mode === 'Human' ? 1 : mode === 'AI' ? 2 : 3
  await expect(page.getByText('Synthetic model draft: describes access-test evidence.', { exact: true })).toHaveCount(expectedReviews, { timeout: 20000 })
  await expect(cp.getByText('Synthetic model draft: describes access-test evidence.', { exact: true })).toHaveCount(0)
  checks.push('malformed model review remains visible; explicit panel retry saves grounded drafts, hidden from candidate')
  await expect(page.getByText('Synthetic combined draft: discusses tenant access testing.', { exact: true })).toBeVisible({ timeout: 20000 })
  await expect(cp.getByText('Synthetic combined draft: discusses tenant access testing.', { exact: true })).toHaveCount(0)
  await expect(page.getByText('Recovered AI errors (1)', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Retry failed AI review', exact: true })).toBeDisabled()
  await expect(page.locator('.ant-alert-warning').filter({ hasText: 'AI review did not return an object.' })).toHaveCount(0)
  const evidence = page.locator('a[href^="#interview-event-"]').first()
  await expect(evidence).toBeVisible()
  const target = await evidence.getAttribute('href')
  await evidence.click()
  await expect(page.locator(target)).toBeVisible()
  checks.push('combined grounded summary links to saved answer; recovered error is historical, no stale warning; summary hidden from candidate')
  await page.screenshot({ path: path.join(output, 'real-api-session-completed.png'), fullPage: true, mask: [page.locator('textarea[readonly]')] })
  await page.getByRole('button', { name: 'Panel feedback / rubric', exact: true }).click()
  await expect(page.getByPlaceholder('Capture evidence, strengths, concerns and hiring rationale.')).toBeVisible({ timeout: 15000 })
  await field('Panel member').locator('.ant-select-selector').click()
  await page.locator('.ant-select-item-option').filter({ hasText: 'Isolated Interview Panel' }).click()
  await field('Overall score').getByRole('spinbutton').fill('80')
  await page.getByPlaceholder('Capture evidence, strengths, concerns and hiring rationale.').fill('Synthetic fixture: described tenant isolation. Final hiring decision remains pending.')
  const saved = page.waitForResponse(response => /\/interviews\/\d+\/feedback$/.test(response.url()) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Save feedback', exact: true }).click()
  assert((await saved).ok())
  checks.push('completed session opens existing panel feedback editor; human feedback persists')
  if (mode === 'Human') {
    await page.goto(`${ui}/recruitment/interview-queue`)
    for (const name of ['Batch One', 'Batch Two'])
      await page.getByRole('row').filter({ hasText: name }).getByRole('checkbox').check()
    await page.getByRole('button', { name: /Schedule selected/ }).click()
    const batch = page.locator('.ant-drawer-content').filter({ has: page.getByText('Schedule 2 candidates', { exact: true }) })
    await field('Panel members', batch).locator('.ant-select-selector').click()
    await page.locator('.ant-select-item-option').filter({ hasText: 'Isolated Interview Panel' }).click()
    await page.keyboard.press('Escape')
    await field('Default meeting link', batch).getByRole('textbox').fill('https://meeting.example.invalid/batch')
    await batch.getByRole('button', { name: 'Apply to all', exact: true }).click()
    await batch.getByRole('button', { name: 'Schedule & send invites', exact: true }).click()
    await expect(batch).toHaveCount(0, { timeout: 30000 })
    await page.goto(`${ui}/recruitment/interviews`)
    for (const name of ['Batch One', 'Batch Two']) await expect(page.getByRole('row').filter({ hasText: name })).toBeVisible()
    await page.screenshot({ path: path.join(output, 'existing-bulk-external-regression.png'), fullPage: true })
    checks.push('existing two-candidate bulk external scheduling and candidate/panel mail still work; no internal session created')
  }
  assert.deepEqual(failedApi, []); assert.deepEqual(errors, [])
  const result = { mode, checks, browserErrors: errors, realApiAndDatabase: true, actualAiOrchestrationWithMockedModel: true, realMediaSpeechOrLlm: false }
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result))
} catch (error) {
  const detail = String(error?.stack || error).replaceAll(process.env.HRMS_TEST_PASSWORD, '[redacted]').replace(/#access=[^\s]+/g, '#access=[redacted]')
  const failure = { mode, detail, checks, failedApi, browserErrors: errors }
  fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify(failure, null, 2))
  console.error(JSON.stringify(failure))
  for (const context of browser.contexts()) for (const page of context.pages()) {
    if (await page.getByPlaceholder('Enter password').count()) { console.error('Login UI: ' + await page.locator('.auth-error').textContent().catch(() => 'No visible error')); continue }
    await page.screenshot({ path: path.join(output, `failure-${Date.now()}.png`), fullPage: true, mask: [page.locator('textarea[readonly]')] }).catch(() => {})
    console.error((await page.locator('body').innerText().catch(() => '')).replace(/#access=[^\s]+/g, '#access=[redacted]').slice(-4000))
  }
  throw error
} finally { await browser.close() }
