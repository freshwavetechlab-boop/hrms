import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('../', import.meta.url))
const { chromium, expect } = await import(pathToFileURL(path.join(repo, 'tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin = 'http://localhost:5173'
const output = fs.mkdtempSync(path.join(os.tmpdir(), 'frevo-mapping-layout-'))
const fixture = `.codex-mapping-layout-${crypto.randomUUID()}.tsx`
const fixturePath = path.join(repo, 'payroll-ui', fixture)
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import 'antd/dist/reset.css';import theme from '/src/theme/hrmsTheme.ts';import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const headers = ['Employee Name', 'Employee Code', 'Basic', 'PF', 'Net Pay', 'Present days', ...Array.from({ length: 46 }, (_, i) => `Source reference ${i + 1}`)]
const csv = Buffer.from(headers.join(',') + '\n' + Array.from({ length: 126 }, (_, i) => [`Synthetic employee ${i + 1}`, `SYN-${i + 1}`, 10000, 1200, 8800, 26, ...Array.from({ length: 46 }, (_, n) => `${i + 1}:${n + 1}`)].join(',')).join('\n'))
const browser = await chromium.launch({ headless: true })
const context = await browser.newContext({ viewport: { width: 1393, height: 835 }, serviceWorkers: 'block' })
const page = await context.newPage()
const errors = [], unexpected = [], checks = [], layouts = []
let lookupMode = 'ready', writeAttempts = 0
page.setDefaultTimeout(10000)
page.on('pageerror', e => errors.push(e.message))
await context.routeWebSocket('**/*', socket => socket.close())
await context.route('**/*', route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (url.origin !== origin && !p.startsWith('/api/')) return route.abort()
  if (p === '/mapping-layout-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (!p.startsWith('/api/')) return route.continue()
  if (p === '/api/auth/me') return reply({ id: 1, displayName: 'Synthetic admin', roles: ['client_admin'], isActive: true, permissions: ['payroll.run', 'settings.manage', 'employees.manage'] })
  if (p.endsWith('/clients')) return reply([{ id: 42, name: 'Synthetic client' }])
  if (p.endsWith('/profile')) return reply(null)
  if (p === '/api/excel-payslips/employee-code-options') return reply({ clientCode: 'SYN', digits: 5, nextSequence: '127' })
  if (p === '/api/excel-payslips/employee-identity-preview' && request.method() === 'POST') {
    if(lookupMode==='error')return reply({error:'Synthetic older API unavailable'},404)
    const {rows}=request.postDataJSON();return reply({clientId:42,checkedRows:rows.length,rows:rows.map(row=>{const matched=['SYN-43','SYN-120'].includes(row.employeeCode);return {sourceRow:row.sourceRow,status:matched?'Existing':'No match',masterCode:matched?row.employeeCode:'',active:matched,basis:matched?['Employee Code']:[],reasons:[]}})})
  }
  if (p === '/api/excel-payslips/employee-code-preview' && request.method() === 'POST') {
    const input = request.postDataJSON()
    if (lookupMode === 'error') return reply({ error: 'Synthetic older API unavailable' }, 404)
    return reply({ checkedCodes: input.codes.length, matches: ['SYN-43', 'SYN-120'].map(code => ({ code, records: 1, activeRecords: 1 })) })
  }
  if (request.method() !== 'GET') { writeAttempts++; unexpected.push(request.method() + ' ' + p); return reply({}, 403) }
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  return reply([])
})

async function layout(modal, label) {
  return modal.evaluate((el, label) => {
    const keys = ['.ant-modal-body', '.smart-bulk-source-preview', '.smart-bulk-source-preview-help', '.excel-payslip-grid-tools', '.excel-payslip-code-preview-tools', '.smart-bulk-source-preview-table', '.smart-bulk-source-preview-pagination', '.ant-modal-footer']
    return { label, viewport: { width: innerWidth, height: innerHeight }, nodes: keys.map(selector => {
      const n = el.querySelector(selector), r = n.getBoundingClientRect(), c = getComputedStyle(n)
      return { selector, x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom, display: c.display, rows: c.gridTemplateRows, minHeight: c.minHeight, minWidth: c.minWidth, scrollWidth: n.scrollWidth, clientWidth: n.clientWidth, children: [...n.children].map(x => ({ class: x.className, y: x.getBoundingClientRect().y, height: x.getBoundingClientRect().height })) }
    }) }
  }, label)
}

async function openImport() {
  await page.goto(origin + '/mapping-layout-fixture')
  await page.getByRole('combobox', { name: 'Excel payslips client', exact: true }).click()
  await page.locator('.ant-select-dropdown').getByText('Synthetic client', { exact: true }).click()
  await page.getByRole('button', { name: 'Import spreadsheet', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({ name: 'Synthetic 126 rows 52 columns.csv', mimeType: 'text/csv', buffer: csv })
  await page.getByRole('textbox', { name: 'Payslip salary month', exact: true }).fill('2026-09')
  await page.getByRole('button', { name: 'Excel preview & map', exact: true }).click()
}

async function usableGrid(modal, label) {
  const grid = modal.getByTestId('smart-bulk-source-preview-table')
  layouts.push(await layout(modal, label))
  await expect(modal.getByRole('button', { name: 'Use mapped preview', exact: true })).toBeInViewport()
  await expect(grid.locator('thead')).toBeInViewport()
  await expect(grid.locator('tbody tr').first().locator('td').first()).toBeInViewport()
  await expect(modal.locator('.smart-bulk-source-preview-pagination')).toBeInViewport()
  assert.ok(await grid.evaluate(el => el.getBoundingClientRect().bottom - el.querySelector('thead').getBoundingClientRect().bottom >= 40), 'At least one real data row must fit below the fixed column mappings')
  assert.ok(await modal.evaluate(el => [...el.querySelectorAll('.ant-modal-body,.smart-bulk-source-preview,.excel-payslip-grid-tools,.smart-bulk-source-preview-table')].every(n => { const r = n.getBoundingClientRect(); return r.left >= -1 && r.right <= innerWidth + 1 })), 'Grid and tools must stay inside the modal viewport')
  assert.ok(await modal.locator('.excel-payslip-grid-tools').evaluate(el => el.getBoundingClientRect().height < innerHeight / 2), 'Mapping tools must not expand into the reserved worksheet area')
}

try {
  await openImport()
  const modal = page.locator('.smart-bulk-source-preview-modal:visible').last(), grid = modal.getByTestId('smart-bulk-source-preview-table')
  await expect(modal.getByText(/126 data rows \/ 52 columns/)).toBeVisible()
  await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN-43')
  await usableGrid(modal, 'initial-wide-52-columns')
  await page.screenshot({ path: path.join(output, 'initial-wide.png') })
  checks.push('Full import page with all shell/reset styles renders a 126-row, 52-column worksheet below compact mapping tools')
  await modal.getByRole('checkbox', { name: 'Existing codes first', exact: true }).uncheck()
  await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN-1')
  await modal.getByRole('checkbox', { name: 'Existing codes first', exact: true }).check()
  await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN-43')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await usableGrid(modal, 'collapsed-' + width)
    await grid.locator('tbody tr').first().locator('td').nth(2).click()
    await expect(modal.getByRole('textbox', { name: 'Payslip saved cell result', exact: true })).toHaveValue('SYN-43')
    await modal.locator('.excel-payslip-grid-column-options summary').click()
    await usableGrid(modal, 'expanded-' + width)
    await expect(modal.getByRole('textbox', { name: 'Preview payslip label', exact: true })).toBeInViewport()
    await modal.locator('.excel-payslip-grid-column-options summary').click()
    await grid.evaluate(el => { el.scrollLeft = el.scrollWidth; el.scrollTop = el.scrollHeight })
    await expect(grid.locator('tbody tr').last().locator('td').last()).toBeInViewport()
    await expect(modal.getByRole('button', { name: 'Use mapped preview', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, 'scrolled-' + width + '.png') })
    await grid.evaluate(el => { el.scrollLeft = 0; el.scrollTop = 0 })
  }
  checks.push('Desktop, tablet and mobile retain visible worksheet rows, pagination and footer with options collapsed or expanded, and scroll to the last column and row')
  await page.setViewportSize({ width: 1366, height: 768 })
  await modal.locator('.ant-pagination-item-3').click()
  await expect(grid.locator('tbody tr').last().locator('td').first()).toHaveText('127')
  await modal.getByRole('button', { name: 'Use mapped preview', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Employee import preview' })).toContainText('SYN-43')
  await page.getByRole('button', { name: 'Excel preview & map', exact: true }).click()
  await usableGrid(modal, 'reopened')
  checks.push('Sorting, source cell inspection, last-page access and reopen preserve the unsaved import without saving or updating Master')
  await page.goto(origin + '/mapping-layout-fixture')
  await page.evaluate(() => sessionStorage.clear())
  lookupMode = 'error'
  await openImport()
  await expect(modal.getByText('Employee identity check unavailable', { exact: true })).toBeVisible()
  await usableGrid(modal, 'lookup-error')
  checks.push('An unavailable Master-code lookup does not hide or stretch the mapping worksheet')
  assert.deepEqual(errors, []); assert.deepEqual(unexpected, []); assert.equal(writeAttempts, 0)
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: true, checks, layouts, actualWrites: 0, errors, unexpected }, null, 2))
  console.log(JSON.stringify({ passed: true, checks, output }))
} catch (error) {
  const modal = page.locator('.smart-bulk-source-preview-modal:visible').last()
  if (await modal.count()) layouts.push(await layout(modal, 'failure').catch(() => null))
  await page.screenshot({ path: path.join(output, 'failure.png') })
  fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), layouts, errors, unexpected, writeAttempts }, null, 2))
  console.error('Diagnostics: ' + output)
  throw error
} finally {
  await context.close(); await browser.close(); fs.rmSync(fixturePath, { force: true })
}
