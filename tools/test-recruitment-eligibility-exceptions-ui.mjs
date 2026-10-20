import fs from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import assert from 'node:assert/strict'
import { pathToFileURL } from 'node:url'

const { chromium } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const fixtureName = '.codex-eligibility-exceptions-fixture.tsx'
const fixturePath = path.resolve('payroll-ui', fixtureName)
const output = await fs.mkdtemp(path.join(os.tmpdir(), 'frevo-eligibility-exceptions-ui-'))
const fixture = `import{createRoot}from'react-dom/client';import{ConfigProvider}from'antd';import{BrowserRouter}from'react-router-dom';import AuthGate from'./src/components/AuthGate';import ToastProvider from'./src/components/ToastProvider';import ReviewButton from'./src/components/RecruitmentEligibilityReviewButton';import theme from'./src/theme/hrmsTheme';import'antd/dist/reset.css';import'./src/index.css';import'./src/App.css';createRoot(document.getElementById('root')!).render(<BrowserRouter><ConfigProvider theme={theme}><ToastProvider><AuthGate><ReviewButton applicationId={8}/></AuthGate></ToastProvider></ConfigProvider></BrowserRouter>);`
await fs.writeFile(fixturePath, fixture)
const user = { id: 9001, email: 'qa@example.test', displayName: 'QA recruitment admin', isActive: true, clientId: null, roles: [], permissions: ['recruitment.manage', 'recruitment.candidate.view'] }
let context = {
  applicationId: 8, applicationCode: 'APP-EXCEPTION-8', candidateName: 'Exception test candidate', resumeId: 108,
  scoreId: 48, requirementsHash: 'qa-exception-context', eligibilityStatus: 'Ineligible', atsScore: 99,
  parsedExperienceMonths: 48, verifiedExperienceMonths: null, experienceEvidence: '', requiredExperienceRange: '5 years', scoreIsStale: false,
  reasons: ['Minimum total experience is 5 years; resume shows 4 years.'], items: [
    { key: 'experience:minimum', requirement: 'Minimum total experience: 5 years', status: 'NotMet', evidence: 'Actual experience: 4 years.', canReview: false, canWaive: true },
    { key: 'skill:soc', requirement: 'Mandatory skill: SOC — minimum 3 years', status: 'NotMet', evidence: 'Resume documents 2 years of SOC work.', canReview: false, canWaive: true },
    { key: 'requirement:cert', requirement: 'Mandatory certification: CISSP', status: 'NeedsReview', evidence: 'No completed credential established.', canReview: true, canWaive: true },
  ],
}
const posts = [], checks = [], errors = []
const browser = await chromium.launch({ headless: true })
const page = await browser.newPage({ viewport: { width: 1366, height: 768 } })
page.on('pageerror', error => errors.push(error.message))
const choose = async (requirement, label) => {
  const input = page.getByRole('combobox', { name: `Review ${requirement}`, exact: true })
  await input.press('Enter')
  const controls = await input.getAttribute('aria-controls')
  await page.locator('.ant-select-dropdown').filter({ has: page.locator(`[id=${JSON.stringify(controls)}]`) }).getByTitle(label, { exact: true }).click()
}
const save = () => page.locator('.ant-drawer-footer button').filter({ hasText: 'Save review & Run ATS' })
try {
  await page.route('**/api/**', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname
    if (pathname === '/api/auth/me') return route.fulfill({ json: user })
    if (!pathname.endsWith('/applications/8/eligibility-review')) return route.fulfill({ status: 404, json: { error: 'Unexpected mocked request' } })
    if (request.method() === 'GET') return route.fulfill({ json: context })
    const body = request.postDataJSON(); posts.push(body)
    assert.equal(body.experienceChanged, false)
    context = { ...context, scoreId: context.scoreId + 1, items: context.items.map(item => {
      const review = body.decisions.find(decision => decision.key === item.key)
      return review ? { ...item, review: { ...review, reviewerName: user.displayName, reviewedAtUtc: '2026-10-20T12:00:00Z' } } : item
    }) }
    const unresolved = context.items.filter(item => !['Waived', 'Verified'].includes(item.review?.decision))
    context.eligibilityStatus = !unresolved.length ? 'Eligible' : unresolved.some(item => item.key === 'experience:minimum') ? 'Ineligible' : 'NeedsReview'
    context.reasons = [...unresolved.map(item => `Unresolved: ${item.requirement}`), ...context.items.filter(item => item.review?.decision === 'Waived').map(item => `Approved eligibility exception: ${item.requirement} — ${item.review.note} (approved by ${user.displayName}).`)]
    return route.fulfill({ json: { review: context, scoringError: '' } })
  })
  await page.route('**/__eligibility-exceptions-fixture', route => route.fulfill({ contentType: 'text/html', body: `<html><body><div id="root"></div><script type="module">import R from '/@react-refresh';R.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;</script><script type="module" src="/${fixtureName}"></script></body></html>` }))
  await page.goto(`${process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'}/__eligibility-exceptions-fixture`)
  await page.getByRole('button', { name: /Review eligibility/ }).click()
  await page.getByText('Exception test candidate', { exact: true }).waitFor()
  await choose(context.items[0].requirement, 'Approve eligibility exception — reason required')
  await save().click()
  await page.getByText('Add supporting evidence of at least 8 characters for every reviewed item.', { exact: true }).waitFor()
  assert.equal(posts.length, 0); checks.push('exception-needs-a-business-reason')
  await page.getByRole('textbox', { name: `Evidence for ${context.items[0].requirement}`, exact: true }).fill('Client approval permits this candidate with four actual years of experience.')
  await save().click()
  await page.getByText('Exception approved', { exact: true }).first().waitFor()
  assert.equal(context.eligibilityStatus, 'NeedsReview')
  assert.equal(posts[0].decisions.length, 1)
  assert.equal(posts[0].decisions[0].decision, 'Waived'); checks.push('one-exception-does-not-approve-other-requirements')
  for (const item of context.items.slice(1)) {
    await choose(item.requirement, 'Approve eligibility exception — reason required')
    await page.getByRole('textbox', { name: `Evidence for ${item.requirement}`, exact: true }).fill('Recruitment administrator recorded client authorization for this specific exception.')
  }
  await page.screenshot({ path: path.join(output, 'desktop-exceptions.png'), fullPage: false })
  await page.setViewportSize({ width: 768, height: 1024 })
  await page.screenshot({ path: path.join(output, 'tablet-exceptions.png'), fullPage: false })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('textbox', { name: `Evidence for ${context.items[2].requirement}`, exact: true }).scrollIntoViewIfNeeded()
  const footer = await save().boundingBox()
  assert.ok(footer && footer.y >= 0 && footer.y + footer.height <= 844)
  await page.screenshot({ path: path.join(output, 'mobile-exceptions.png'), fullPage: false }); checks.push('exception-controls-and-save-reachable-on-desktop-tablet-mobile')
  await save().click()
  await page.getByText('Eligible', { exact: true }).waitFor()
  assert.equal(context.parsedExperienceMonths, 48)
  assert.equal(context.verifiedExperienceMonths, null)
  assert.equal(posts[1].decisions.length, 2)
  assert.equal(await page.getByText('Exception approved', { exact: true }).count(), 3)
  assert.ok(await page.getByText('Eligibility exception', { exact: true }).count())
  checks.push('all-explicit-exceptions-clear-eligibility-without-changing-candidate-facts')
  await choose(context.items[0].requirement, 'Needs review')
  await save().click()
  await page.getByText('Not eligible', { exact: true }).waitFor()
  assert.equal(posts[2].decisions[0].decision, 'Pending')
  assert.equal(context.parsedExperienceMonths, 48)
  assert.equal(context.items.filter(item => item.review?.decision === 'Waived').length, 2)
  checks.push('revoking-one-exception-restores-the-original-eligibility-failure')
  assert.deepEqual(errors, [])
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify({ status: 'Passed', checks, mockedWrites: posts.length, liveWrites: false, errors }, null, 2))
  console.log(JSON.stringify({ status: 'Passed', checks, output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: false })
  await fs.writeFile(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), checks, errors, posts }, null, 2))
  console.error(JSON.stringify({ error: String(error), checks, output })); process.exitCode = 1
} finally {
  await browser.close()
  await fs.unlink(fixturePath)
}
