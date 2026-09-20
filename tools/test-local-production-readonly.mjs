// Visible smoke only. Credentials are stdin-only; no storage state or mutation fixtures.
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import assert from 'node:assert/strict'
const require = createRequire(path.resolve('playwright-e2e/package.json'))
const { chromium, expect } = require('@playwright/test')
const auth = JSON.parse(fs.readFileSync(0, 'utf8'))
const output = '.codex-validation/local-production'
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: false, slowMo: 140 })
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
const errors = [], checks = []
page.on('pageerror', e => errors.push(e.message))
page.on('response', r => { if (r.status() >= 400 && r.url().includes('/api/') && !r.url().includes('/auth/me') && !r.url().includes('/internal-interviews/capabilities')) errors.push(`${r.status()} ${new URL(r.url()).pathname}`) })
try {
  await page.goto('http://localhost:5173/recruitment/job-postings')
  await page.getByPlaceholder('Enter email or login ID').fill(auth.email)
  await page.getByPlaceholder('Enter password').fill(auth.password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByPlaceholder('Enter password')).toHaveCount(0, { timeout: 45000 })
  for (const [route, title] of [['job-postings', 'Jobs'], ['work-orders-and-sla', 'Work Orders'], ['applications', 'Applications'], ['interviews', 'Interview Tracker'], ['offers-and-pre-onboarding', 'Offers & Pre-boarding']]) {
    await page.goto(`http://localhost:5173/recruitment/${route}`)
    // This portal polls/SSEs continuously; wait on business content, not network idle.
    await page.waitForLoadState('domcontentloaded')
    const close = page.getByRole('button', { name: 'Close app modules', exact: true })
    if (await close.isVisible()) await close.click()
    await expect(page.getByRole('heading', { name: title, exact: true }).first()).toBeVisible({ timeout: 30000 })
    await page.evaluate(route => {
      const bar = document.createElement('div'); bar.textContent = `TEST MODE · PRODUCTION DATA · READ ONLY · ${route}`
      Object.assign(bar.style, { position: 'fixed', bottom: 0, left: 0, right: 0, background: '#113e3e', color: 'white', padding: '14px', zIndex: 2147483647, pointerEvents: 'none' }); document.body.append(bar)
    }, route)
    if (route === 'job-postings') {
      await expect(page.getByRole('heading', { name: 'Software Architect', exact: true })).toHaveCount(1)
      await expect(page.getByRole('heading', { name: 'Chief Architect', exact: true })).toHaveCount(1)
    }
    if (route === 'offers-and-pre-onboarding' && process.env.HRMS_VERIFY_SIGNING_SETTINGS === '1') {
      await page.getByRole('button', { name: /Final offer signatory$/ }).click()
      const drawer = page.locator('.ant-drawer-content:visible')
      await drawer.getByRole('combobox').first().click()
      await page.getByText('Unique Identification Authority of India', { exact: true }).last().click()
      await expect(drawer.getByRole('combobox').nth(1)).toBeVisible()
      await page.screenshot({ path: `${output}/final-signatory-settings.png`, fullPage: true })
      await drawer.getByRole('button', { name: 'Cancel', exact: true }).click()
      checks.push('final-signatory-settings-read-only')
    }
    await page.screenshot({ path: `${output}/${route}.png`, fullPage: true })
    checks.push(route); console.log(`Read-only production data: ${route} passed`)
  }
  assert.deepEqual(errors, [])
  fs.writeFileSync(`${output}/result.json`, JSON.stringify({ status: 'Passed', at: new Date().toISOString(), checks, errors }, null, 2))
} catch (e) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true })
  fs.writeFileSync(`${output}/result.json`, JSON.stringify({ status: 'Failed', checks, errors, error: String(e) }, null, 2)); throw e
} finally { await browser.close() }
