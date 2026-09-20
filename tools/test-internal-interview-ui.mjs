import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
import { installInterviewDeviceFixture, verifyInterviewDeviceChecks } from './test-interview-device-checks.mjs'

// Actual portal UI with fully intercepted API. Does NOT claim real media/STT/LLM integration coverage.
const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = require('@playwright/test')
const root = fileURLToPath(new URL('../', import.meta.url))
const output = path.join(root, '.codex-validation/internal-interviews/ui')
fs.mkdirSync(output, { recursive: true })
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('UI smoke test is restricted to localhost.')
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1', slowMo: 40,
  args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows'] })
const checks = []
const errors = []
let configured = false
let disconnectEvents = false
let voiceRequests = 0, speechRequests = 0
let replayBytes = null, replayRanges = [], resetCalls = 0
const bank = []
const silentWav = Buffer.alloc(44 + 16000)
silentWav.write('RIFF', 0); silentWav.writeUInt32LE(silentWav.length - 8, 4); silentWav.write('WAVEfmt ', 8)
silentWav.writeUInt32LE(16, 16); silentWav.writeUInt16LE(1, 20); silentWav.writeUInt16LE(1, 22)
silentWav.writeUInt32LE(16000, 24); silentWav.writeUInt32LE(32000, 28); silentWav.writeUInt16LE(2, 32); silentWav.writeUInt16LE(16, 34)
silentWav.write('data', 36); silentWav.writeUInt32LE(16000, 40)
let nextId = 1
const timeline = []
const configuration = { mode: 'Hybrid', language: 'en', difficulty: 'Intermediate', maxQuestions: 8, answerSeconds: 120, followUpLimit: 1, recordingEnabled: false, transcriptionEnabled: true, questionIds: [1] }
const context = { interviewId: 7, applicationId: 100, clientId: 20, positionId: 10, candidateName: 'Synthetic Candidate', positionTitle: 'Integration Test Engineer', roundCode: 'Technical', interviewStatus: 'Scheduled', result: 'Pending', panelUserIds: [4], timeZoneId: 'UTC', scheduledStart: new Date(Date.now() - 300000).toISOString(), scheduledEnd: new Date(Date.now() + 3300000).toISOString() }
const session = { ...context, status: 'Scheduled', control: 'Human', revision: 1, consentAtUtc: null, startedAtUtc: null, endedAtUtc: null, recordingConsent: false, transcriptionConsent: false, noticeVersion: 'internal-interview-v1', configuration, questions: [{ id: 1, question: 'Describe integration testing.', evaluationCriteria: 'PRIVATE RUBRIC' }] }
const user = { id: 4, displayName: 'Synthetic Interview Panel', email: 'panel@example.invalid', roles: ['super_admin'], permissions: ['recruitment.interview.schedule', 'recruitment.interview.panel', 'recruitment.manage'], dashboardAccess: [], defaultDashboardCode: '', isActive: true, clientId: null, employeeId: null }
const append = (kind, text, actor, source = 'server', questionEventId = null, eventKey = crypto.randomUUID()) => {
  const row = { id: nextId++, interviewId: 7, eventKey, kind, text, actor, source, questionEventId, createdAtUtc: new Date().toISOString() }; timeline.push(row); return row
}
async function mock(page, panel) {
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()), p = url.pathname
    const fulfill = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (p === '/api/auth/me') return fulfill(user)
    if (p.endsWith('/capabilities')) return fulfill({ enabled: true, canManage: true, acceptingNewSessions: !session.draining })
    if (p.endsWith('/7/context')) return fulfill(context)
    if (!p.includes('/internal-interviews/')) return fulfill({ error: 'Unexpected API request in isolated smoke test.' }, 404)
    const publicApi = p.startsWith('/api/public/')
    if (publicApi && request.headers()['x-interview-token'] !== 'synthetic-link') return fulfill({ error: 'The interview link is invalid or expired.' }, 401)
    if (p.includes('/speech/question/')) { speechRequests++; return route.fulfill({ status: 200, contentType: 'audio/wav', body: silentWav }) }
    if (p.includes('/speech/transcribe/')) {
      assert((request.postDataBuffer()?.length || 0) > 0); voiceRequests++
      return fulfill({ text: 'I verify denied cross-client reads and updates.', draft: true })
    }
    const view = () => ({ ...session, isCandidate: publicApi, canManage: !publicApi, canResetSchedule: !publicApi && session.status === 'Cancelled', questions: publicApi ? null : session.questions })
    if (p.endsWith('/questions')) {
      if (request.method() === 'POST') { const q = request.postDataJSON(); q.id ||= bank.length + 1; const i = bank.findIndex(x => x.id === q.id); if (i < 0) bank.push(q); else bank[i] = q; return fulfill(q) }
      return fulfill(bank)
    }
    if (p.endsWith('/configuration')) { configured = true; session.configuration = request.postDataJSON(); session.questions = bank.filter(q => session.configuration.questionIds.includes(q.id)); return fulfill(view()) }
    if (p.endsWith('/7') && request.method() === 'GET') return configured ? fulfill(view()) : fulfill({ error: 'Internal interview has not been configured.' }, 404)
    if (p.endsWith('/link')) { checks.push('candidate link generated'); return fulfill({ url: `${origin}/interview/7#access=synthetic-link`, expiresAtUtc: context.scheduledEnd }) }
    if (p.endsWith('/consent')) { session.consentAtUtc = new Date().toISOString(); session.transcriptionConsent = true; session.status = 'Waiting'; session.revision++; return fulfill(view()) }
    if (p.endsWith('/join')) return fulfill({ error: 'Video service unavailable in this mock; use the configured real LiveKit integration for media validation.' }, 503)
    if (p.endsWith('/schedule-reset')) { assert.equal(request.postDataJSON().revision, session.revision); resetCalls++; session.status = 'Scheduled'; session.consentAtUtc = null; session.revision++; return fulfill(view()) }
    if (p.endsWith('/recordings')) return fulfill(replayBytes ? [{ id: 'synthetic-replay', interviewId: 7, status: 'Ready', contentType: 'video/webm', sizeBytes: replayBytes.length, startedAtUtc: session.startedAtUtc }] : [])
    if (p.endsWith('/recordings/synthetic-replay')) {
      assert(!publicApi); const range = request.headers().range; replayRanges.push(range || 'none')
      const parts = /^bytes=(\d+)-(\d*)$/.exec(range || '')
      if (!parts) return fulfill({ error: 'Range required by this fixture.' }, 416)
      const start = Number(parts[1]), end = Math.min(parts[2] ? Number(parts[2]) : replayBytes.length - 1, replayBytes.length - 1)
      return route.fulfill({ status: 206, contentType: 'video/webm', headers: { 'accept-ranges': 'bytes', 'content-range': `bytes ${start}-${end}/${replayBytes.length}`, 'cache-control': 'private, no-store' }, body: replayBytes.subarray(start, end + 1) })
    }
    if (p.endsWith('/ai-retry')) return fulfill({ message: 'Synthetic retry acknowledged.' })
    if (p.endsWith('/command')) {
      const { action, revision } = request.postDataJSON(); assert.equal(revision, session.revision)
      if (action === 'start') { assert.equal(session.status, 'Waiting'); session.status = 'Live'; session.startedAtUtc = new Date().toISOString() }
      if (action === 'complete') { session.status = 'Completed'; session.endedAtUtc = new Date().toISOString() }
      if (action === 'ai') session.control = 'AI'
      if (action === 'human') session.control = 'Human'
      append(`session-${action}`, 'Human controlled session action; hiring result unchanged.', 'panel:4'); session.revision++; return fulfill(view())
    }
    if (p.endsWith('/events')) {
      if (request.method() === 'POST') {
        if (publicApi && disconnectEvents) return route.abort('internetdisconnected')
        const e = request.postDataJSON(); const existing = timeline.find(row => row.eventKey === e.eventKey)
        return fulfill(existing || append(e.kind, e.text, publicApi ? 'candidate' : 'panel:4', publicApi ? 'candidate-reported' : 'panel', e.questionEventId, e.eventKey))
      }
      const after = Number(url.searchParams.get('after') || 0)
      return fulfill(timeline.filter(e => e.id > after && (!publicApi || ['question', 'answer', 'transcript', 'session-start', 'session-complete', 'session-ai', 'session-human'].includes(e.kind))))
    }
    return fulfill({ error: 'Unmocked interview operation.' }, 404)
  })
}
try {
  const hr = await browser.newContext({ viewport: { width: 1440, height: 960 } })
  const candidateBrowser = await browser.newContext({ viewport: { width: 1366, height: 900 }, permissions: ['microphone', 'camera'] })
  await installInterviewDeviceFixture(candidateBrowser)
  const hrPage = await hr.newPage(), candidatePage = await candidateBrowser.newPage()
  await mock(hrPage, true); await mock(candidatePage, false)
  await hrPage.goto(`${origin}/recruitment/interview-session/7`)
  await expect(hrPage.getByRole('heading', { name: 'Synthetic Candidate' })).toBeVisible()
  checks.push('authenticated panel session UI')
  const field = label => hrPage.locator('.ant-form-item').filter({ has: hrPage.locator('label').filter({ hasText: new RegExp(`^${label}$`) }) })
  await field('Skill').getByRole('textbox').fill('Testing')
  await field('Question').getByRole('textbox').fill('Describe integration testing.')
  await field('Expected evaluation criteria').getByRole('textbox').fill('PRIVATE RUBRIC')
  await hrPage.getByRole('button', { name: 'Add question', exact: true }).click()
  await expect.poll(() => bank.length).toBe(1)
  await field('Interview mode').locator('.ant-select-selector').click()
  await hrPage.getByText('Human + AI Hybrid', { exact: true }).click()
  await field('Question set').locator('.ant-select-selector').click()
  await hrPage.getByText('Testing · Intermediate · Describe integration testing.', { exact: true }).click()
  await hrPage.getByRole('button', { name: 'Save internal interview', exact: true }).click()
  await expect(hrPage.getByRole('button', { name: 'Generate candidate link', exact: true })).toBeVisible()
  assert.equal(session.configuration.mode, 'Hybrid'); assert.equal(session.configuration.transcriptionEnabled, true)
  checks.push('job question bank and internal configuration UI (mock persistence)')
  await hrPage.getByRole('button', { name: 'Edit internal settings', exact: true }).click()
  await expect(field('Interview mode')).toContainText('Human + AI Hybrid')
  await hrPage.getByRole('button', { name: 'Edit internal settings', exact: true }).click()
  session.status = 'Cancelled'; session.revision++
  await hrPage.reload()
  await hrPage.getByRole('button', { name: 'Use updated schedule', exact: true }).click()
  await hrPage.getByRole('button', { name: 'OK', exact: true }).click()
  await expect.poll(() => resetCalls).toBe(1)
  await expect(hrPage.getByRole('button', { name: 'Generate candidate link', exact: true })).toBeVisible()
  checks.push('saved settings reopen; changed unstarted schedule reset uses revision and confirmation (mock persistence)')
  await hrPage.getByRole('button', { name: 'Generate candidate link', exact: true }).click()
  await hrPage.getByRole('button', { name: 'OK', exact: true }).click()
  await expect(hrPage.getByText('Candidate-only interview link', { exact: true })).toBeVisible()
  await candidatePage.goto(`${origin}/interview/7#access=synthetic-link`)
  await expect(candidatePage.getByRole('button', { name: 'Agree and enter waiting room' })).toBeDisabled()
  await candidatePage.getByRole('checkbox', { name: /privacy and browser-event notice/ }).check()
  await candidatePage.getByRole('checkbox', { name: /local transcription/ }).check()
  session.draining = true
  await expect(candidatePage.getByText('Interview maintenance: new sessions are paused', { exact: true })).toBeVisible({ timeout: 10000 })
  await expect(candidatePage.getByRole('button', { name: 'Agree and enter waiting room' })).toBeDisabled()
  await expect(hrPage.getByRole('button', { name: 'Generate candidate link', exact: true })).toBeDisabled({ timeout: 10000 })
  session.draining = false
  await expect(candidatePage.getByRole('button', { name: 'Agree and enter waiting room' })).toBeEnabled({ timeout: 10000 })
  checks.push('maintenance admission pause is visible and disables new consent/link actions')
  await candidatePage.getByRole('button', { name: 'Agree and enter waiting room' }).click()
  await verifyInterviewDeviceChecks(candidatePage, expect, checks, output)
  await expect(candidatePage.getByText('You are in the waiting room. The panel will start your interview.')).toBeVisible()
  checks.push('separate consent and candidate waiting room')
  await expect(hrPage.getByRole('button', { name: 'Start interview', exact: true })).toBeVisible({ timeout: 10000 })
  await hrPage.getByRole('button', { name: 'Start interview', exact: true }).click()
  await expect(candidatePage.getByRole('button', { name: 'Join audio/video' })).toBeVisible({ timeout: 10000 })
  checks.push('panel-controlled start, candidate status refresh')
  await candidatePage.getByRole('button', { name: 'Join audio/video' }).click()
  await expect(candidatePage.getByText(/Video service unavailable in this mock/)).toBeVisible()
  checks.push('media failure is visible rather than fake success')
  await hrPage.getByPlaceholder('Ask an instant question or save a private panel note.').fill('How do you test tenant isolation?')
  await hrPage.getByRole('button', { name: 'Ask question', exact: true }).click()
  await candidatePage.bringToFront()
  await expect(candidatePage.getByText('How do you test tenant isolation?', { exact: true })).toBeVisible({ timeout: 10000 })
  await candidatePage.getByRole('button', { name: 'Hear question', exact: true }).click()
  await expect.poll(() => speechRequests).toBe(1)
  await expect(candidatePage.getByText(/Local playback only/)).toBeVisible()
  await candidatePage.getByRole('button', { name: 'Record answer', exact: true }).click()
  await expect(candidatePage.getByText(/Recording \d+s/)).toBeVisible()
  await candidatePage.waitForTimeout(1200) // collect a real MediaRecorder chunk from Chromium's synthetic device
  await candidatePage.getByRole('button', { name: 'Stop and parse voice', exact: true }).click()
  await expect.poll(() => voiceRequests).toBe(1)
  await expect(candidatePage.getByPlaceholder('Type your answer or use the configured voice interview.')).toHaveValue('I verify denied cross-client reads and updates.')
  assert(!timeline.some(e => e.kind === 'answer'))
  checks.push('synthetic microphone -> mocked STT editable draft; mocked WAV question playback, no premature answer save')
  await candidatePage.getByPlaceholder('Type your answer or use the configured voice interview.').fill('I verify denied cross-client reads and updates.')
  const submitAnswer = candidatePage.locator('button').filter({ hasText: /^Submit answer$/ })
  await expect(submitAnswer).toBeEnabled()
  assert((await submitAnswer.ariaSnapshot()).includes('Submit answer'))
  await submitAnswer.click()
  await expect(hrPage.getByText('I verify denied cross-client reads and updates.', { exact: true })).toBeVisible({ timeout: 10000 })
  checks.push('human question and candidate answer timeline')
  await hrPage.bringToFront()
  await hrPage.getByPlaceholder('Ask an instant question or save a private panel note.').fill('PRIVATE PANEL NOTE')
  await hrPage.getByRole('button', { name: 'Save panel note' }).click()
  await expect(hrPage.getByText('PRIVATE PANEL NOTE', { exact: true })).toBeVisible()
  await expect(candidatePage.getByText('PRIVATE PANEL NOTE', { exact: true })).toHaveCount(0)
  await expect(candidatePage.getByText('PRIVATE RUBRIC', { exact: true })).toHaveCount(0)
  checks.push('candidate UI excludes private rubric and panel notes')
  append('media-presence', JSON.stringify({ eventType: 'participant_left', identity: 'panel-4', participantSessionId: 'PA_test', role: 'Panel user', occurredAtUtc: '2026-09-20T01:02:00Z' }), 'media-server', 'livekit-webhook')
  append('media-presence', JSON.stringify({ eventType: 'participant_joined', identity: 'panel-4', participantSessionId: 'PA_test', role: 'Panel user', occurredAtUtc: '2026-09-20T01:01:00Z' }), 'media-server', 'livekit-webhook')
  await expect(hrPage.locator('.interview-presence-table tbody tr')).toHaveCount(2, { timeout: 10000 })
  await expect(hrPage.locator('.interview-presence-table tbody tr').first()).toContainText('Joined')
  await expect(hrPage.locator('.interview-presence-table tbody tr').last()).toContainText('Left')
  await expect(candidatePage.getByText('Participant connection history', { exact: true })).toHaveCount(0)
  checks.push('verified participant history distinguishes event/receipt times, sorts delayed events and remains panel-only (synthetic callbacks)')
  await candidatePage.evaluate(() => { window.dispatchEvent(new Event('blur')); window.dispatchEvent(new Event('focus')); document.dispatchEvent(new Event('paste')); window.dispatchEvent(new Event('online')) })
  await expect.poll(() => timeline.filter(e => ['blur', 'focus', 'paste', 'reconnected'].includes(e.kind)).length, { timeout: 15000 }).toBeGreaterThanOrEqual(4)
  assert(timeline.filter(e => ['blur', 'focus', 'paste', 'reconnected'].includes(e.kind)).every(e => e.text === ''))
  checks.push('objective browser events contain no clipboard payload')
  disconnectEvents = true
  await candidatePage.evaluate(() => window.dispatchEvent(new Event('offline')))
  await expect(candidatePage.getByText(/browser event\(s\) pending sync/)).toBeVisible()
  const queued = await candidatePage.evaluate(() => Object.keys(sessionStorage).filter(k => k.startsWith('hrms-interview-events:')).map(k => sessionStorage.getItem(k)).join(''))
  assert(queued.includes('connection-lost')); assert(!queued.includes('synthetic-link'))
  await candidatePage.reload()
  await expect(candidatePage.getByRole('button', { name: 'Submit answer', exact: true })).toBeVisible()
  disconnectEvents = false
  await candidatePage.evaluate(() => window.dispatchEvent(new Event('online')))
  await expect.poll(() => timeline.some(e => e.kind === 'connection-lost'), { timeout: 20000 }).toBe(true)
  await expect.poll(() => timeline.some(e => e.kind === 'page-reload'), { timeout: 20000 }).toBe(true)
  checks.push('failed event HTTP retry survives page reload, without storing bearer credentials (video reconnect not claimed)')
  await hrPage.bringToFront()
  await hrPage.getByRole('button', { name: 'Hand control to AI' }).click()
  await expect(hrPage.getByRole('button', { name: 'Resume human control' })).toBeVisible()
  await hrPage.getByRole('button', { name: 'Resume human control' }).click()
  checks.push('hybrid control handoff (no inference claimed)')
  append('ai-error', 'Synthetic live AI error: local model unavailable.', 'ai-assistant', 'local-ai', null, 'turn:synthetic:failed:0')
  await expect(hrPage.getByText('Synthetic live AI error: local model unavailable.', { exact: true })).toBeVisible({ timeout: 10000 })
  await expect(candidatePage.getByText('Synthetic live AI error: local model unavailable.', { exact: true })).toHaveCount(0)
  checks.push('live model failure stays visible to panel, not silently hidden by completed-review audit cleanup')
  session.draining = true
  await expect(hrPage.getByText('Interview maintenance: new sessions are paused', { exact: true })).toBeVisible({ timeout: 10000 })
  await expect(hrPage.getByRole('button', { name: 'Complete interview session', exact: true })).toBeEnabled()
  checks.push('draining a live interview retains panel completion and saved evidence')
  await hrPage.getByRole('button', { name: 'Complete interview session', exact: true }).click()
  const confirmation = hrPage.getByRole('dialog', { name: 'End this session?' })
  await expect(confirmation).toBeVisible()
  await hrPage.waitForTimeout(400) // allow the normal opening animation, not ongoing movement
  const bounds = []
  for (let i = 0; i < 10; i++) { bounds.push(await confirmation.boundingBox()); await hrPage.waitForTimeout(400) }
  for (const box of bounds) for (const key of ['x', 'y', 'width', 'height']) assert(Math.abs(box[key] - bounds[0][key]) < 1, `Confirmation moved while polling: ${key}`)
  await hrPage.screenshot({ path: path.join(output, 'stable-end-confirmation.png') })
  checks.push('end confirmation stays stationary across timer/status updates; no force-click or disabled animations')
  await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click()
  await hrPage.getByRole('button', { name: 'Fullscreen', exact: true }).click()
  await expect.poll(() => hrPage.evaluate(() => document.fullscreenElement?.classList.contains('internal-interview-page'))).toBe(true)
  await hrPage.getByRole('button', { name: 'Complete interview session', exact: true }).click()
  await expect(confirmation).toBeVisible()
  assert(await confirmation.evaluate(node => document.fullscreenElement.contains(node)), 'Fullscreen must include the confirmation portal')
  await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click()
  await hrPage.evaluate(() => document.exitFullscreen())
  checks.push('fullscreen confirmation remains visible and cancellable inside the session')
  await hrPage.getByRole('button', { name: 'Complete interview session', exact: true }).click()
  await hrPage.getByRole('button', { name: 'OK', exact: true }).click()
  await expect(hrPage.getByText('Interview session ended', { exact: true })).toBeVisible()
  await expect(candidatePage.getByText('Interview session ended', { exact: true })).toBeVisible({ timeout: 10000 })
  assert.equal(context.result, 'Pending'); checks.push('completed session retains human-only hiring outcome')
  const submitted = timeline.find(e => e.kind === 'answer')
  assert(submitted)
  append('ai-error', 'Synthetic summary attempt failed.', 'ai-assistant', 'local-ai', null, 'review:summary:failed:0')
  append('ai-summary', JSON.stringify({ questionCount: 1, answerCount: 1, unansweredQuestionEventIds: [], observations: [{
    kind: 'interviewSummary', note: 'Synthetic grounded combined review.', evidence: [{
      questionEventId: submitted.questionEventId, answerEventId: submitted.id, answeredAtUtc: submitted.createdAtUtc, evidenceQuote: submitted.text,
    }],
  }] }), 'ai-assistant', 'local-ai', null, 'review:summary')
  await expect(hrPage.getByText('Synthetic grounded combined review.', { exact: true })).toBeVisible({ timeout: 10000 })
  await expect(candidatePage.getByText('Synthetic grounded combined review.', { exact: true })).toHaveCount(0)
  await expect(hrPage.getByText('Recovered AI errors (1)', { exact: true })).toBeVisible()
  const noRecordings = hrPage.locator('.interview-no-recordings')
  await expect(noRecordings).toBeVisible()
  assert(await noRecordings.evaluate(node => [...node.children].every(card => card.getBoundingClientRect().width > node.getBoundingClientRect().width * 0.9)), 'No-recording layout must reclaim the empty column')
  await hrPage.screenshot({ path: path.join(output, 'combined-review-no-recording.png'), fullPage: true })
  checks.push('combined evidence display/private projection and recovered-error history; absent recording does not waste half the review width')
  await hrPage.getByRole('button', { name: 'Manage job question bank', exact: true }).click()
  await expect(hrPage.getByRole('button', { name: 'Add question', exact: true })).toBeVisible()
  await expect(field('Interview mode')).toHaveCount(0)
  await expect(hrPage.getByRole('button', { name: 'Save internal interview', exact: true })).toHaveCount(0)
  await hrPage.getByRole('button', { name: 'Close question bank', exact: true }).click()
  checks.push('question bank remains manageable after completion without editing the session snapshot')
  // A real playable synthetic clip, not a claimed LiveKit recording.
  replayBytes = Buffer.from(await hrPage.evaluate(async () => {
    const canvas = document.createElement('canvas'); canvas.width = 320; canvas.height = 180
    const draw = canvas.getContext('2d'); draw.fillStyle = '#145678'; draw.fillRect(0, 0, 320, 180)
    const stream = canvas.captureStream(10), recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp8' }), chunks = []
    const done = new Promise(resolve => { recorder.ondataavailable = e => chunks.push(e.data); recorder.onstop = resolve })
    recorder.start(100); await new Promise(resolve => setTimeout(resolve, 250))
    draw.fillStyle = '#267890'; draw.fillRect(10, 10, 100, 100)
    await new Promise(resolve => setTimeout(resolve, 350)); recorder.stop(); await done
    stream.getTracks().forEach(track => track.stop())
    return Array.from(new Uint8Array(await new Blob(chunks, { type: 'video/webm' }).arrayBuffer()))
  }))
  await hrPage.reload()
  await hrPage.getByRole('button', { name: 'Load protected replay', exact: true }).click()
  await expect.poll(() => hrPage.locator('video').evaluate(video => video.readyState), { timeout: 15000 }).toBeGreaterThanOrEqual(1)
  assert(replayRanges.includes('bytes=0-0')); assert(replayRanges.length >= 2)
  assert(!(await hrPage.locator('video').getAttribute('src')).includes('?'))
  checks.push('native video metadata and protected byte-range replay; synthetic WebM, no full-file blob or URL token')
  await hrPage.screenshot({ path: path.join(output, 'panel-completed.png'), fullPage: true })
  await candidatePage.setViewportSize({ width: 390, height: 844 })
  await candidatePage.screenshot({ path: path.join(output, 'candidate-mobile-completed.png'), fullPage: true })
  assert.equal(await candidatePage.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  checks.push('mobile session layout does not overflow')
  await candidatePage.goto(`${origin}/interview/7#access=invalid`)
  await expect(candidatePage.getByText('The interview link is invalid or expired.', { exact: true })).toBeVisible()
  await expect(candidatePage.getByRole('button', { name: 'Join audio/video' })).toHaveCount(0)
  checks.push('invalid-link UI denies join')
  assert.deepEqual(errors, [])
  const result = { completedAtUtc: new Date().toISOString(), mode: 'actual-ui-mocked-api', checks, screenshots: output, browserErrors: errors, syntheticCameraMicrophone: true, realMediaSpeechOrModelTested: false }
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result, null, 2))
} catch (error) {
  console.error(JSON.stringify({ completedChecks: checks, browserErrors: errors }))
  for (const context of browser.contexts()) for (const page of context.pages()) {
    console.error((await page.locator('body').innerText().catch(() => '')).slice(0, 1500))
    console.error(await page.locator('button').evaluateAll(nodes => nodes.map(n => ({ text: n.textContent, role: n.getAttribute('role'), label: n.getAttribute('aria-label'), hidden: n.closest('[aria-hidden=true]')?.tagName }))))
    await page.screenshot({ path: path.join(output, `failure-${Date.now()}.png`), fullPage: true }).catch(() => {})
  }
  throw error
} finally { await browser.close() }
