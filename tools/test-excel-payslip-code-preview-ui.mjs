import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath,pathToFileURL } from 'node:url'
const repo=fileURLToPath(new URL('../',import.meta.url))
const {chromium,expect}=await import(pathToFileURL(path.join(repo,'tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin='http://localhost:5173',output=fs.mkdtempSync(path.join(os.tmpdir(),'frevo-code-preview-'))
const fixture='.codex-code-preview-'+crypto.randomUUID()+'.tsx',fixturePath=path.join(repo,'payroll-ui',fixture)
fs.writeFileSync(fixturePath,`import React,{useState} from 'react';import ReactDOM from 'react-dom/client';import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';import Provider from '/src/components/ExcelPayslipCodePreview.tsx';import Grid from '/src/components/ExcelPayslipMappingPreview.tsx';import Employees from '/src/components/ExcelPayslipEmployeePreview.tsx';import {payslipPreviewCodes} from '/src/utils/excelPayslipCodePreview.ts';import '/src/index.css';import '/src/components/ExcelPayslips.css';
const columns=['Employee name','Employee code','Basic','Net pay'].map((sourceHeader,columnIndex)=>({sourceHeader,columnIndex,label:sourceHeader,kind:['employeeName','employeeCode','earning','netPay'][columnIndex]}));const sheet={name:'Salary',columnCount:4,rows:[{sourceRow:1,cells:columns.map(c=>({value:c.sourceHeader}))},...Array.from({length:120},(_,i)=>({sourceRow:i+2,cells:[{value:'Synthetic employee '+(i+1)},{value:'SYN'+String(i+1).padStart(3,'0')},{value:'10000',formula:true,formulaText:'10000'},{value:'10000',formula:true,formulaText:'C'+(i+2)}]}))]};const before=JSON.stringify(sheet);function freeze(o){if(o&&typeof o==='object'){Object.freeze(o);Object.values(o).forEach(freeze)}return o}freeze(sheet);window.__unchanged=()=>JSON.stringify(sheet)===before;
function App(){const[excluded,setExcluded]=useState([]);window.__excluded=()=>excluded;const rows=sheet.rows.slice(1).filter(r=>!excluded.includes(r.sourceRow)).map(r=>({id:'row-'+r.sourceRow,sourceRow:r.sourceRow,employeeName:r.cells[0].value,employeeCode:r.cells[1].value,email:'',information:[],earnings:[{label:'Basic',amount:10000}],deductions:[],employerContributions:[],netPay:10000,warnings:[]}));return <ConfigProvider theme={theme}><Provider clientId={33} codes={payslipPreviewCodes(sheet,1,columns)}><main className="excel-payslip-import" style={{padding:12}}><Grid sheet={sheet} headerRow={1} columns={columns} fileName="Synthetic.xlsx" excludedRows={excluded} disabled={false} onPatch={()=>{}} onSuggest={()=>{}} onExclude={n=>setExcluded(v=>[...v,n])} onInclude={n=>setExcluded(v=>v.filter(i=>i!==n))}/><Employees rows={rows} simpleLayout={false} generationEnabled={true} disabled={false} canManage={false} amount={v=>String(v)} onDetails={()=>{}} onCorrect={()=>{throw Error('Unexpected correction')}} onExclude={n=>setExcluded(v=>[...v,n])}/></main></Provider></ConfigProvider>}ReactDOM.createRoot(document.getElementById('root')).render(<App/>);`)
const html=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const browser=await chromium.launch({headless:true}),context=await browser.newContext({viewport:{width:1366,height:768},serviceWorkers:'block'}),page=await context.newPage(),checks=[],requests=[],errors=[],unexpected=[]
let mode='ready'
page.setDefaultTimeout(10000);page.on('pageerror',e=>errors.push(e.message))
await context.routeWebSocket('**/*',s=>s.close())
await context.route('**/*',route=>{const req=route.request(),u=new URL(req.url()),p=u.pathname
 if(p.startsWith('/api/')){
  if(p==='/api/excel-payslips/employee-code-preview'&&req.method()==='POST'){
   const body=req.postDataJSON();assert.equal(u.searchParams.get('clientId'),'33');assert.deepEqual(Object.keys(body),['codes']);assert.equal(body.codes.length,120);requests.push(body)
   if(mode==='error')return route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({error:'Synthetic check unavailable'})})
   return route.fulfill({contentType:'application/json',body:JSON.stringify({checkedCodes:mode==='partial'?119:120,matches:[{code:'SYN060',records:1,activeRecords:0},...[116,117,118,119,120].map(n=>({code:'SYN'+n,records:n===119?2:1,activeRecords:1}))]})})
  }unexpected.push(req.method()+' '+p);return route.abort('blockedbyclient')
 }
 if(u.origin!==origin){unexpected.push(u.href);return route.abort('blockedbyclient')}
 if(p==='/code-preview-fixture')return route.fulfill({contentType:'text/html',body:html})
 if(p==='/favicon.ico')return route.fulfill({status:204,body:''})
 return route.continue()
})
try{
 await page.goto(origin+'/code-preview-fixture')
 const employees=page.getByRole('region',{name:'Employee import preview'}),employeeRows=employees.locator('tbody tr[data-row-key]')
 await expect(employeeRows.first()).toContainText('SYN060');await expect(employeeRows.nth(1)).toContainText('SYN116');await expect(employeeRows).toHaveCount(10)
 await expect(employeeRows.first()).toHaveClass(/excel-code-existing-row/);await expect(employeeRows.nth(6)).toHaveClass(/excel-code-new-row/)
 assert.notEqual(await employeeRows.first().locator('td').first().evaluate(el=>getComputedStyle(el).backgroundColor),await employeeRows.nth(6).locator('td').first().evaluate(el=>getComputedStyle(el).backgroundColor))
 await employees.getByRole('checkbox',{name:'Existing codes first',exact:true}).uncheck();await expect(employeeRows.first()).toContainText('SYN001')
 await employees.locator('.ant-pagination-item-2').click();await expect(employeeRows.first()).toContainText('SYN011')
 await employees.getByRole('checkbox',{name:'Existing codes first',exact:true}).check();await expect(employeeRows.first()).toContainText('SYN060')
 checks.push('Employee table sorts all 120 rows before ten-row pagination, existing red and unmatched white, and changing sort returns to page one')
 await page.getByRole('button',{name:'Excel preview & map',exact:true}).click()
 const modal=page.locator('.smart-bulk-source-preview-modal:visible').last(),grid=modal.getByTestId('smart-bulk-source-preview-table')
 await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN060')
 await expect(grid.locator('tbody tr').first().locator('td').first()).toHaveText('61')
 await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toHaveClass(/excel-code-existing-cell/)
 await modal.getByRole('checkbox',{name:'Existing codes first',exact:true}).uncheck();await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN001')
 await modal.locator('.ant-pagination-item-3').click();await expect(grid.locator('tbody tr').first().locator('td').first()).toHaveText('102')
 await modal.getByRole('checkbox',{name:'Existing codes first',exact:true}).check();await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN060')
 checks.push('Excel grid shares the sort option, sorts before 50-row pagination, resets to page one and preserves actual source row numbers')
 await grid.locator('tbody tr').first().locator('td').nth(2).click()
 await modal.getByRole('button',{name:'Exclude source row 61',exact:true}).click();await expect.poll(()=>page.evaluate(()=>window.__excluded())).toEqual([61])
 await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN116')
 assert.equal(requests.length,1,'Exclusion must not refetch or re-identify the remaining source')
 await modal.getByRole('button',{name:'Include source row 61',exact:true}).click();await expect.poll(()=>page.evaluate(()=>window.__excluded())).toEqual([])
 await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SYN060')
 await expect(modal.getByRole('textbox',{name:'Payslip saved cell result',exact:true})).toHaveValue('SYN060')
 assert.equal(await page.evaluate(()=>window.__unchanged()),true)
 for(const[width,height]of[[1366,768],[768,1024],[390,844]]){await page.setViewportSize({width,height});await expect(modal.getByRole('button',{name:'Use mapped preview',exact:true})).toBeInViewport();await expect.poll(()=>grid.evaluate(el=>el.getBoundingClientRect().bottom-el.querySelector('thead').getBoundingClientRect().bottom)).toBeGreaterThan(40);await page.screenshot({path:path.join(output,'preview-'+width+'.png')})}
 await modal.getByRole('button',{name:'Use mapped preview',exact:true}).click();await expect(employeeRows.first()).toContainText('SYN060')
 await employeeRows.first().getByRole('button',{name:'Exclude row',exact:true}).click();await expect.poll(()=>page.evaluate(()=>window.__excluded())).toEqual([61]);await expect(employeeRows.first()).toContainText('SYN116')
 checks.push('Exclude/include work by original source row in both previews, with unchanged cells, code, formula and values; no source/Master/payroll writes')
 await page.setViewportSize({width:1366,height:768});mode='error';await page.goto(origin+'/code-preview-fixture')
 await expect(employees.getByText('Master-code check unavailable',{exact:true})).toBeVisible();await expect(employeeRows.first()).toHaveClass(/excel-code-pending-row/);await expect(employees.getByText('No code match',{exact:true})).toHaveCount(0)
 mode='ready';await employees.getByRole('button',{name:'Retry Master code check',exact:true}).click();await expect(employeeRows.first()).toContainText('SYN060')
 mode='partial';await page.goto(origin+'/code-preview-fixture');await expect(employees.getByText('Master-code check unavailable',{exact:true})).toBeVisible()
 checks.push('Unavailable or partial lookup stays unverified rather than labelling employees new; explicit retry safely restores matches')
 assert.deepEqual(errors,[]);assert.deepEqual(unexpected,[])
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:true,checks,lookupRequests:requests.length,actualBusinessWrites:0,errors,unexpected},null,2));console.log(JSON.stringify({passed:true,groups:checks.length,output}))
}catch(error){await page.screenshot({path:path.join(output,'failure.png')});fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({error:String(error),errors,unexpected,requests},null,2));console.error('Diagnostics: '+output);throw error}
finally{await context.close();await browser.close();fs.rmSync(fixturePath,{force:true})}
