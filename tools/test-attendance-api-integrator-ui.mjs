// All API reads and writes use synthetic fixtures; no live attendance is imported.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { pathToFileURL } from 'node:url'
const runtimePath = process.env.HRMS_PLAYWRIGHT_RUNTIME || 'C:/Users/APPC0024/Documents/Codex/2026-10-25/task/live-playwright-local/playwright-runtime-paths.json'
const runtime = JSON.parse(fs.readFileSync(runtimePath, 'utf8'))
process.env.PLAYWRIGHT_BROWSERS_PATH ||= runtime.browserPath
const { chromium, expect } = await import(pathToFileURL(runtime.module).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://127.0.0.1:5187'
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(origin).hostname))
const output = '.codex-validation/attendance-api-integrator'
fs.mkdirSync(output, { recursive: true })
const fixture = (id, clientId = 11) => ({
  id: String(id).padStart(32, 'a'), clientId, revision: 'revision1', name: `Attendance adapter ${String(id).padStart(2, '0')} with a long vendor label`,
  isActive: id % 2 === 1, deviceId: 'GATE-01', sourceDeviceId: 'TW123', url: 'https://attendance.example.invalid/TimeWatchAPI/GetPunchData', method: 'POST',
  authType: 'ApiKey', authHeader: 'X-Api-Key', username: '', hasSecret: true, hasDeviceToken: true, allowPrivateNetwork: false,
  headers: {}, query: {}, bodyTemplate: '{}', requestDateFormat: 'yyyy-MM-dd', recordsPath: 'Data', successPath: 'Success', successValue: 'true',
  employeePath: 'UserID', devicePath: 'DeviceID', timePath: 'PunchTime', actionPath: 'InOutMode', punchIdPath: '', punchTimeFormat: '',
  timeZoneId: 'Asia/Kolkata', inValues: ['IN'], outValues: ['OUT'], employeeMap: {}, pagination: 'None', firstPage: 1, pageSize: 500,
  maxPages: 20, timeoutSeconds: 20, syncIntervalMinutes: 15, lookbackDays: 2,
})
let rows = Array.from({ length: 24 }, (_, i) => fixture(i + 1)), failRun = false
const requests = [], errors = []
const browser = await chromium.launch({ headless: true })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, serviceWorkers: 'block' })
const page = await context.newPage()
page.on('pageerror', e => errors.push(e.message))
await context.route('**/api/**', route => {
  const req = route.request(), url = new URL(req.url()), p = url.pathname
  const headers = { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true', 'access-control-allow-headers': 'content-type,authorization', 'access-control-allow-methods': 'GET,POST,DELETE,OPTIONS' }
  const reply = (data, status = 200) => route.fulfill({ status, headers, contentType: 'application/json', body: JSON.stringify(data) })
  if (req.method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
  requests.push({ path: p, method: req.method(), clientId: url.searchParams.get('clientId'), body: req.method() === 'POST' ? req.postDataJSON() : undefined })
  if (p === '/api/auth/me') return reply({ id: 1, clientId: null, displayName: 'Synthetic administrator', email: 'test@example.invalid', roles: ['admin'], permissions: ['settings.manage', 'security.manage'], isActive: true })
  if (p === '/api/clients') return reply([{ id: 11, name: 'Synthetic Rajiv Gandhi client', isActive: true }, { id: 22, name: 'Other synthetic client', isActive: true }])
  if (p === '/api/organization') return reply({ id: 1, name: 'Synthetic Frevo', logoDataUrl: '' })
  if (p === '/api/integrations/attendance/devices') return reply([{ deviceId: 'GATE-01', name: 'Main gate', clientId: Number(url.searchParams.get('clientId')), workLocationId: 5, isActive: true }])
  if (p === '/api/integrations/attendance/adapters') {
    if (req.method() === 'POST') {
      const saved = { ...req.postDataJSON().configuration, id: 'b'.repeat(32), revision: 'saved', hasSecret: true }
      rows.push(saved); return reply(saved)
    }
    return reply(url.searchParams.get('clientId') === '22' ? [] : rows)
  }
  if (/\/adapters\/[^/]+\/(test|sync)$/.test(p)) return reply({ success: !failRun, preview: p.endsWith('/test'), httpStatus: failRun ? null : 200,
    fetched: failRun ? 0 : 2, mapped: failRun ? 0 : 2, accepted: p.endsWith('/sync') && !failRun ? 1 : 0, duplicates: p.endsWith('/sync') ? 1 : 0, skipped: 0, rejected: 0,
    message: failRun ? 'Cannot connect to the API server.' : p.endsWith('/test') ? 'Test complete. No attendance imported.' : 'Sync complete: 1 accepted, 1 duplicate.', errors: [],
    samples: p.endsWith('/test') && !failRun ? [{ punchId: 'fixture1', employeeCode: 'EMP001', action: 'IN', capturedAt: '2026-11-01T09:00:00+05:30' }] : [], startedAt: '2026-11-02T06:00:00Z', durationMs: 120 })
  if (req.method() === 'DELETE' && p.includes('/adapters/')) return reply({ deleted: true })
  if (req.method() !== 'GET') return reply({ error: 'Unexpected synthetic mutation' }, 400)
  return reply([])
})
const choose = async (name, label) => {
  await page.getByRole('combobox', { name, exact: true }).locator('xpath=ancestor::div[contains(@class,"ant-select-selector")]').click()
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: label }).last().click()
}
const workspace = () => page.getByTestId('attendance-api-integrator')
const noOverflow = async label => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true, label)
try {
  await page.goto(origin + '/security/apps/attendance-api-integrator', { waitUntil: 'domcontentloaded' })
  await expect(workspace()).toBeVisible()
  await expect(workspace().locator('.candidate-application-card')).toHaveCount(10)
  await expect(page.getByRole('heading', { name: 'Attendance API Integrator', exact: true })).toBeVisible()
  const headerButton = page.getByRole('button', { name: 'Add adapter', exact: true })
  const before = await headerButton.boundingBox()
  await workspace().locator('.candidate-application-list').evaluate(e => e.scrollTop = e.scrollHeight)
  const after = await headerButton.boundingBox()
  assert.equal(Math.round(after.y), Math.round(before.y), 'Primary action remains fixed while cards scroll')
  await noOverflow('Desktop fits viewport')
  await page.screenshot({ animations: 'disabled', path: output + '/desktop-cards.png' })

  await page.locator('.ant-segmented-item').filter({ hasText: /^Table$/ }).click()
  await expect(workspace().locator('.candidate-filter-panel')).toHaveCount(0)
  await expect(workspace().locator('.candidate-status-strip')).toHaveCount(0)
  await expect(workspace().locator('.ant-table')).toBeVisible()
  await noOverflow('Table scroll stays internal')
  await page.reload(); await expect(workspace().locator('.ant-table')).toBeVisible()
  const downloadWait = page.waitForEvent('download')
  await workspace().getByRole('button', { name: /Export/ }).first().click()
  const download = await downloadWait
  assert.match(download.suggestedFilename(), /\.xlsx$/)
  await download.saveAs(output + '/adapters-export.xlsx')
  assert.equal(fs.readFileSync(output + '/adapters-export.xlsx').subarray(0, 2).toString(), 'PK')
  await page.screenshot({ animations: 'disabled', path: output + '/desktop-table.png' })

  await page.getByRole('button', { name: 'Use iDMS preset', exact: true }).click()
  const drawer = page.locator('.ant-drawer-content').filter({ has: page.locator('.ant-drawer-title').filter({ hasText: /^Add attendance API adapter$/ }) })
  await expect(drawer.getByLabel('API endpoint URL', { exact: true })).toHaveValue(/GetPunchData$/)
  await choose('Registered HRMS machine', 'Main gate')
  await drawer.getByRole('tab', { name: 'Request & pagination', exact: true }).click()
  await expect(drawer.getByLabel('JSON request body', { exact: true })).toHaveValue(/\{\{fromDate\}\}/)
  await drawer.getByRole('tab', { name: 'Response mapping', exact: true }).click()
  await expect(drawer.getByText(/does not define its meaning/)).toBeVisible()
  await drawer.getByLabel('Vendor user ID → HRMS employee code', { exact: true }).fill('{invalid')
  const savesBefore = requests.filter(r => r.method === 'POST').length
  await drawer.getByRole('button', { name: 'Save adapter', exact: true }).click()
  await expect(drawer.locator('.ant-alert-error')).toBeVisible()
  assert.equal(requests.filter(r => r.method === 'POST').length, savesBefore)
  await drawer.getByLabel('Vendor user ID → HRMS employee code', { exact: true }).fill('{"369839":"EMP001"}')
  await drawer.getByRole('button', { name: 'Save adapter', exact: true }).click()
  await expect(drawer).not.toBeVisible()
  const saved = requests.find(r => r.method === 'POST' && r.path.endsWith('/adapters')).body
  assert.equal(saved.configuration.clientId, 11); assert.equal(saved.configuration.employeeMap['369839'], 'EMP001')
  assert.equal(saved.configuration.isActive, false, 'Preset does not start importing automatically')

  await page.locator('.ant-segmented-item').filter({ hasText: /^Cards$/ }).click()
  await workspace().getByRole('button', { name: 'Test', exact: true }).first().click()
  const modal = page.getByRole('dialog', { name: /^Test attendance API/ })
  await modal.getByRole('button', { name: 'Test & preview', exact: true }).click()
  await expect(modal.getByText('EMP001', { exact: true })).toBeVisible()
  assert.equal(requests.filter(r => r.path.endsWith('/sync')).length, 0, 'Test never calls sync')
  await modal.getByRole('button', { name: 'Close', exact: true }).last().click()
  await workspace().getByRole('button', { name: 'Sync', exact: true }).first().click()
  const sync = page.getByRole('dialog', { name: /^Sync attendance API/ })
  await sync.getByRole('button', { name: 'Sync attendance', exact: true }).click()
  await expect(sync.getByText(/1 accepted, 1 duplicate/)).toBeVisible()
  await sync.getByRole('button', { name: 'Close', exact: true }).last().click()
  failRun = true
  await workspace().getByRole('button', { name: 'Test', exact: true }).first().click()
  await modal.getByRole('button', { name: 'Test & preview', exact: true }).click()
  await expect(modal.getByText('Cannot connect to the API server.', { exact: true })).toBeVisible()
  await modal.getByRole('button', { name: 'Close', exact: true }).last().click()

  for (const viewport of [{ width: 768, height: 1024 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport)
    await noOverflow(`${viewport.width}px layout fits`)
    await expect(headerButton).toBeVisible()
    await expect(workspace().getByRole('button', { name: /Filters \(/ })).toBeVisible()
    await workspace().getByRole('button', { name: /Filters \(/ }).click()
    await expect(page.locator('.ant-drawer:visible .ant-drawer-title').filter({ hasText: /^Filters$/ })).toBeVisible()
    await page.getByRole('button', { name: 'Show results', exact: true }).click()
    await expect(page.locator('.ant-drawer:visible .ant-drawer-title').filter({ hasText: /^Filters$/ })).not.toBeVisible()
    await page.screenshot({ animations: 'disabled', path: `${output}/${viewport.width}-cards.png` })
    await headerButton.click()
    await expect(drawer).toBeVisible()
    await drawer.getByRole('button', { name: 'Save adapter', exact: true }).hover()
    await noOverflow(`${viewport.width}px editor fits`)
    await page.screenshot({ animations: 'disabled', path: `${output}/${viewport.width}-editor.png` })
    await drawer.getByRole('button', { name: 'Cancel', exact: true }).click()
  }
  await page.setViewportSize({ width: 1366, height: 768 })
  await choose('Attendance adapter client', 'Other synthetic client')
  await expect(workspace().getByText(/No attendance adapters/)).toBeVisible()
  await expect(workspace().getByText('Main gate')).toHaveCount(0)
  assert.ok(requests.some(r => r.path.endsWith('/adapters') && r.clientId === '22'))
  assert.deepEqual(errors, [])
  console.log('Attendance API integrator: route/header, 24 records, cards/table, persistence, XLSX export, preset/save validation, preview/sync/failure, client switching and 3 viewport checks passed.')
} catch (e) {
  await page.screenshot({ animations: 'disabled', path: output + '/failure.png', fullPage: true })
  console.error((await page.locator('body').innerText()).slice(-2500)); console.error(errors); console.error(await page.locator('[role=dialog]').evaluateAll(es=>es.map(e=>({role:e.getAttribute('role'),label:e.getAttribute('aria-label'),labelledBy:e.getAttribute('aria-labelledby'),text:e.innerText.slice(0,150),inputs:[...e.querySelectorAll('input')].map(i=>({label:i.getAttribute('aria-label'),value:i.value}))}))))
  throw e
} finally { await browser.close() }
