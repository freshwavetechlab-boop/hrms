import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(path.resolve('tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin = 'http://localhost:5173', output = path.join(os.tmpdir(), 'frevo-shared-preview-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-shared-preview-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React,{useState} from 'react';import ReactDOM from 'react-dom/client';import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import PayslipPreview from '/src/components/ExcelPayslipMappingPreview.tsx';import EmployeePreview from '/src/components/SmartBulkSourcePreview.tsx';import '/src/index.css';
const headers=['Employee Name','Monthly Basic','Payable days','ER BASIC','PF','Net Pay','TA/DA','Reference'];
const sheet={name:'Source salary',columnCount:8,rows:[{sourceRow:3,cells:headers.map(value=>({value}))},...Array.from({length:120},(_,i)=>{const n=i+4;return{sourceRow:n,cells:[{value:'Synthetic '+n},{value:'30000'},{value:'26'},{value:'26000',formula:true,formulaText:'B'+n+'/30*C'+n},{value:'3120',formula:true,formulaText:'D'+n+'*12%'},{value:'22880'},{value:''},{value:'',formula:true,formulaText:'Missing!A'+n,missingCachedValue:true}]}})]};
const original=JSON.stringify(sheet);const kinds=['employeeName','info','info','earning','deduction','netPay','earning','info'];
function Test(){const[columns,setColumns]=useState(headers.map((sourceHeader,columnIndex)=>({columnIndex,sourceHeader,label:sourceHeader,kind:kinds[columnIndex],...(columnIndex===6?{blankAsZero:true}:{})})));const[count,setCount]=useState(0);const[record,setRecord]=useState('');const employee=new URL(location.href).searchParams.has('employee');
return <ConfigProvider theme={theme}><div style={{padding:12}}>{employee?<EmployeePreview open fileName="Synthetic employees.xlsx" sheet={{name:'Employees',headers:['Name','Code'],rows:[['Synthetic Worker','SYN1']]}} definition={{moduleCode:'employees',moduleLabel:'Employees',targetSheetName:'Employees',fields:[{code:'FirstName',header:'FirstName',label:'First Name',group:'Identity',type:'text'},{code:'LastName',header:'LastName',label:'Last Name',group:'Identity',type:'text'}]}} mappings={{FirstName:0}} onMap={(i,c)=>setRecord(i+':'+c)} onUnmapSource={i=>setRecord('unmap:'+i)} onSplitColumn={(i,s)=>setRecord('split:'+i+':'+s)} onSplitStrategyChange={()=>{}} onUndoSplit={()=>{}} onClose={()=>{}}/>:<PayslipPreview sheet={sheet} headerRow={3} columns={columns} fileName="Synthetic salary.xlsx" excludedRows={[123]} disabled={false} template={{id:'t',name:'Synthetic template',components:[{id:'tax',code:'TAX',name:'Tax',category:'Deduction'}]}} onPatch={(index,patch)=>setColumns(current=>current.map(c=>c.columnIndex===index?{...c,...patch}:c))} onSuggest={()=>setCount(c=>c+1)}/>}<output data-testid="source-unchanged">{JSON.stringify(sheet)===original?'yes':'no'}</output><output data-testid="mapping-state">{JSON.stringify(columns)}</output><output data-testid="suggestions">{count}</output><output data-testid="legacy-record">{record}</output></div></ConfigProvider>}
ReactDOM.createRoot(document.getElementById('root')).render(<Test/>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const browser = await chromium.launch({ headless: true }), context = await browser.newContext({ viewport: { width: 1366, height: 768 } }), page = await context.newPage()
page.setDefaultTimeout(10000)
const errors = [], checks = []; let apiCalls = 0
page.on('pageerror', e => errors.push(e.message))
await context.route('**/*', route => { const url = new URL(route.request().url()); if (url.origin !== origin) return route.abort(); if (url.pathname.startsWith('/api/')) { apiCalls++; return route.abort() } if (url.pathname === '/shared-preview-fixture') return route.fulfill({ contentType: 'text/html', body: html }); return route.continue() })
try {
  await page.goto(origin + '/shared-preview-fixture')
  await page.getByRole('button', { name: 'Excel preview & map', exact: true }).click()
  const modal = page.locator('.smart-bulk-source-preview-modal'), grid = page.getByTestId('smart-bulk-source-preview-table')
  await expect(page.getByText('PAYSLIP WORKING PREVIEW', { exact: true })).toBeVisible()
  await grid.locator('tbody tr').first().locator('td').nth(4).click()
  await expect(page.getByRole('textbox', { name: 'Payslip source formula', exact: true })).toHaveValue('=B4/30*C4')
  await expect(page.getByRole('textbox', { name: 'Payslip saved cell result', exact: true })).toHaveValue('26000')
  await expect(grid.locator('tbody tr').first().locator('td').first()).toHaveText('4')
  const basic = grid.getByTestId('smart-bulk-preview-column').nth(3)
  const oldColor = await basic.evaluate(el => getComputedStyle(el).getPropertyValue('--map-color'))
  await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Preview column D use as', exact: true }) }).click()
  await page.locator('.ant-select-dropdown:visible').getByText('Deduction', { exact: true }).click()
  assert.notEqual(await basic.evaluate(el => getComputedStyle(el).getPropertyValue('--map-color')), oldColor)
  assert.equal(JSON.parse(await page.getByTestId('mapping-state').textContent())[3].kind, 'deduction')
  await page.getByText('Column D options — ER BASIC', { exact: true }).click()
  await page.getByRole('textbox', { name: 'Preview payslip label', exact: true }).fill('Reviewed Basic')
  await expect(page.getByTestId('mapping-state')).toContainText('Reviewed Basic')
  await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Preview template component', exact: true }) }).click()
  await page.locator('.ant-select-dropdown:visible').getByText('TAX — Tax', { exact: true }).click()
  await expect.poll(async () => JSON.parse(await page.getByTestId('mapping-state').textContent())[3].componentId).toBe('tax')
  await grid.getByTestId('smart-bulk-preview-column').nth(6).locator('.smart-bulk-preview-column-head > span').click()
  await expect(page.locator('.excel-payslip-grid-column-options summary')).toHaveText('Column G options — TA/DA')
  await page.getByRole('checkbox', { name: 'Preview blank amounts as zero', exact: true }).uncheck()
  await expect.poll(async () => JSON.parse(await page.getByTestId('mapping-state').textContent())[6].blankAsZero).toBe(false)
  await grid.locator('tbody tr').first().locator('td').nth(8).click()
  await expect(page.getByRole('textbox', { name: 'Payslip source formula', exact: true })).toHaveValue('=Missing!A4')
  await expect(grid.locator('tbody tr').first().locator('td').nth(8)).toHaveText('Missing saved result')
  await expect(page.getByTestId('source-unchanged')).toHaveText('yes')
  checks.push('Shared colored grid maps payslip roles/labels/blank policy; original formulas, row numbers, source cells and missing-cache errors remain intact')
  await page.getByRole('button', { name: 'Use suggested mapping', exact: true }).click()
  await expect(page.getByTestId('suggestions')).toHaveText('1')
  await page.locator('.excel-payslip-grid-column-options summary').click()
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await expect(page.getByRole('button', { name: 'Use mapped preview', exact: true })).toBeInViewport()
    await grid.evaluate(el => { el.scrollLeft = el.scrollWidth; el.scrollTop = el.scrollHeight })
    await expect(page.getByRole('button', { name: 'Use mapped preview', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `payslip-${width}.png`), fullPage: true })
  }
  checks.push('Desktop/tablet/mobile grid scrolls within the preview and retains formula/mapping and footer controls')
  await page.getByRole('button', { name: 'Use mapped preview', exact: true }).click()
  await expect(page.getByTestId('source-unchanged')).toHaveText('yes')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(origin + '/shared-preview-fixture?employee=1')
  await expect(page.getByText('UPLOAD WORKING PREVIEW', { exact: true })).toBeVisible()
  await page.locator('.ant-select').filter({ has: page.getByRole('combobox', { name: 'Map Name to HRMS field', exact: true }) }).click()
  await page.locator('.ant-select-dropdown:visible').getByText('Last Name', { exact: true }).click()
  await expect(page.getByTestId('legacy-record')).toHaveText('0:LastName')
  await page.getByTestId('smart-bulk-preview-split').first().click()
  await page.getByText('Last word = Last Name', { exact: true }).click()
  await expect(page.getByTestId('legacy-record')).toHaveText('split:0:last-token')
  for (const [width, height] of [[768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await expect(page.getByRole('button', { name: 'Use mapped preview', exact: true })).toBeInViewport()
  }
  checks.push('Employee Master default preview retains its existing mapping selector and split-name callback')
  assert.equal(apiCalls, 0); assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, apiCalls, actualWrites: 0, errors }, null, 2))
  console.log(JSON.stringify({ checks, apiCalls, output }, null, 2))
} catch (error) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: error.message, errors }, null, 2)); throw error }
finally { await context.close(); await browser.close(); fs.rmSync(fixturePath, { force: true }) }
