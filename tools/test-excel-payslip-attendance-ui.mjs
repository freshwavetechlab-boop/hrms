import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = process.env.HRMS_PLAYWRIGHT_MODULE ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href) : require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only.')
const output = path.join(os.tmpdir(), 'frevo-excel-attendance-ui-20261017')
fs.mkdirSync(output, { recursive: true })
let batch, source, attendanceFile
if (process.env.HRMS_ATTENDANCE_EVIDENCE) {
  const evidence = JSON.parse(fs.readFileSync(process.env.HRMS_ATTENDANCE_EVIDENCE, 'utf8').replace(/^\uFEFF/, ''))
  batch = JSON.parse(evidence.uploadedSource[0].batchJson); source = JSON.parse(evidence.uploadedSource[0].sourceJson)
  attendanceFile = process.env.HRMS_ATTENDANCE_FILE
  assert.ok(attendanceFile && fs.existsSync(attendanceFile))
} else {
  batch = { id: 'synthetic-batch', clientId: 16, month: '2026-09', sourceFileName: 'Synthetic.xlsx', headerRow: 1, rows: Array.from({ length: 24 }, (_, i) => ({ id: `row-${i + 2}`, sourceRow: i + 2, employeeCode: `TEST${i + 1}`, employeeName: `Employee ${i + 1}`, information: [], earnings: [], deductions: [], employerContributions: [], netPay: 0, warnings: [] })) }
  source = { headerRow: 1, columns: [{ columnIndex: 12, sourceHeader: 'Attendance', label: 'Attendance', kind: 'info' }], rows: [{ sourceRow: 2, cells: [{ value: '', formulaText: 'H2/26*M2' }] }] }
  const csv = ['Batch ID,Source row,Employee code,Employee,Location,Days', ...batch.rows.map((r, i) => `${batch.id},${r.sourceRow},${r.employeeCode},${r.employeeName},,${i >= 14 && i <= 16 ? '' : i === 0 ? 25 : 26}`)].join('\n')
  attendanceFile = { name: 'Attendance.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) }
}
const fixture = `.codex-attendance-${crypto.randomUUID()}.tsx`
const fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React,{useEffect,useState} from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import MonthTools from '/src/components/ExcelPayslipMonthTools.tsx';import '/src/AppShell.css';import '/src/components/ExcelPayslips.css';
function Test(){const [data,setData]=useState(null);useEffect(()=>{fetch('/attendance-data').then(r=>r.json()).then(setData)},[]);return data?<MonthTools clientId={data.clientId} batch={data} history={[]} canManage={true} open="calculate" onClose={()=>{}} onBusy={()=>{}} onSaved={()=>{}}/>:null}
ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><Test/></AuthGate></ToastProvider></BrowserRouter>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const page = await browser.newPage({ viewport: { width: 1366, height: 768 } })
const errors = [], calculations = [], checks = []
page.on('pageerror', error => errors.push(error.message))
await page.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  const reply = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/attendance-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if (p === '/attendance-data') return reply(batch)
  if (!p.startsWith('/api/')) return route.continue()
  if (p === '/api/auth/me') return reply({ id: 1, displayName: 'Synthetic admin', roles: ['client_admin'], permissions: ['payroll.run'], isActive: true })
  if (p.endsWith('/calculation')) return reply(source)
  if (p.endsWith('/calculate') && request.method() === 'POST') {
    const body = request.postDataJSON(); calculations.push(body)
    return reply({ ...batch, id: '', month: body.month })
  }
  if (request.method() !== 'GET') { errors.push(`Unexpected write ${p}`); return route.fulfill({ status: 403, body: '{}' }) }
  return reply([])
})
const upload = file => page.locator('input[type="file"]').setInputFiles(file)
try {
  await page.goto(origin + '/attendance-fixture')
  await expect.poll(async () => Number(await page.getByRole('spinbutton', { name: 'Days for empty attendance' }).inputValue())).toBe(26)
  await page.getByRole('textbox', { name: 'Calculation salary month' }).fill('2026-08')
  await upload(attendanceFile)
  const actualSheets = await page.evaluate(async ({ bytes, name }) => {
    const { parseExcelPayslipFile } = await import('/src/utils/excelPayslipImport.ts')
    return parseExcelPayslipFile(new File([new Uint8Array(bytes)], name))
  }, typeof attendanceFile === 'string' ? { bytes: [...fs.readFileSync(attendanceFile)], name: path.basename(attendanceFile) } : { bytes: [...attendanceFile.buffer], name: attendanceFile.name })
  const attendanceRows = actualSheets[0].rows
  const blankCount = attendanceRows.slice(1).filter(row => !row.cells[5]?.value.trim()).length
  await expect(page.getByText(`${24 - blankCount}/24 attendance entered`, { exact: true })).toBeVisible()
  checks.push(`Current input file read with ${24 - blankCount}/24 filled days; original workbook unchanged`)
  const csv = attendanceRows.map((row, i) => Array.from({ length: attendanceRows[0].cells.length }, (_, j) => {
    const value = j === 5 && [16, 17, 18].includes(row.sourceRow) ? '' : row.cells[j]?.value || ''
    return `"${value.replaceAll('"', '""')}"`
  }).join(',')).join('\n')
  const blankCase = { name: 'Attendance-three-missing.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) }
  await upload(blankCase)
  await expect(page.getByText('21/24 attendance entered', { exact: true })).toBeVisible()
  await expect(page.getByText(/Excel rows 16, 17, 18 have blank Days/)).toBeVisible()
  await page.getByRole('button', { name: /Calculate preview/ }).click()
  await expect(page.getByText(/Attendance is missing for 3 of 24/)).toBeVisible()
  assert.equal(calculations.length, 0)
  for (const [row, days] of [[16, '26'], [17, '0'], [18, '12.5']]) {
    const input = page.getByRole('spinbutton', { name: `Attendance days row ${row}`, exact: true })
    await input.fill(days)
    await input.blur()
    await expect.poll(async () => Number(await input.inputValue())).toBe(Number(days))
  }
  await expect(page.getByText('24/24 attendance entered', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: /Calculate preview/ }).click()
  await expect.poll(() => calculations.length).toBe(1)
  assert.equal(calculations[0].attendance.length, 24)
  assert.equal(calculations[0].attendance.find(row => row.rowId === batch.rows.find(row => row.sourceRow === 17).id).days, 0)
  assert.ok(!calculations[0].monthDaysCell)
  await expect(page.getByRole('button', { name: /Save new batch/ })).toBeDisabled()
  checks.push('Actual/representative 24-employee workbook imports 21 values, retains 3 blanks, blocks preview until completed; zero and half days survive')
  await upload(blankCase)
  await expect(page.getByText('21/24 attendance entered', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Fill empty days', exact: true }).click()
  await expect(page.getByText('24/24 attendance entered', { exact: true })).toBeVisible()
  await expect.poll(async () => Number(await page.getByRole('spinbutton', { name: 'Attendance days row 2', exact: true }).inputValue())).toBe(25)
  await page.getByRole('button', { name: /Calculate preview/ }).click()
  await expect.poll(() => calculations.length).toBe(2)
  const priorDays = new Map(calculations[0].attendance.map(row => [row.rowId, row.days]))
  for (const row of batch.rows.filter(row => ![16, 17, 18].includes(row.sourceRow)))
    assert.equal(calculations[1].attendance.find(item => item.rowId === row.id).days, priorDays.get(row.id))
  checks.push('Optional fill uses detected 26-day suggestion only on blanks, preserving all uploaded employee attendance')
  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download attendance template', exact: true }).click()
  const download = await downloadPromise, downloadPath = path.join(output, download.suggestedFilename())
  await download.saveAs(downloadPath)
  const bytes = [...fs.readFileSync(downloadPath)]
  const exported = await page.evaluate(async ({ bytes }) => {
    const { parseExcelPayslipFile } = await import('/src/utils/excelPayslipImport.ts')
    return parseExcelPayslipFile(new File([new Uint8Array(bytes)], 'attendance.xlsx'))
  }, { bytes })
  assert.equal(exported[0].rows.length, 25)
  assert.ok(exported[0].rows.slice(1).every(row => row.cells[6].value === '2026-08'))
  checks.push('Downloaded XLSX contains every saved employee plus salary month; no pagination-based truncation')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    const body = page.locator('.excel-payslip-month-tools .ant-drawer-body')
    await expect.poll(async () => body.evaluate(element => { const rect = element.closest('.ant-drawer-content-wrapper').getBoundingClientRect(); return rect.x >= -1 && rect.right <= innerWidth + 1 })).toBe(true)
    await body.evaluate(element => { element.scrollTop = element.scrollHeight })
    await expect(page.getByRole('button', { name: /Calculate preview/ })).toBeInViewport()
    assert.equal(await body.evaluate(element => element.scrollWidth > element.clientWidth + 1), false)
    await page.screenshot({ path: path.join(output, `attendance-${width}.png`), fullPage: true })
  }
  checks.push('Desktop, tablet and mobile drawer scroll with persistent actions and no body horizontal overflow')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.getByRole('textbox', { name: 'Calculation salary month' }).fill('2026-07')
  await expect(page.getByText('0/24 attendance entered', { exact: true })).toBeVisible()
  await upload(downloadPath)
  await expect(page.getByText(/this attendance file is not for 2026-07/)).toBeVisible()
  await expect(page.getByText('0/24 attendance entered', { exact: true })).toBeVisible()
  checks.push('Changing salary month clears old attendance and rejects a new-format file for another month')
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, previewRequests: calculations.length, actualDatabaseWrites: 0, emails: 0 }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  console.error(errors)
  throw error
} finally { fs.rmSync(fixturePath, { force: true }); await browser.close() }
