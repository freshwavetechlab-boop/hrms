import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import {fileURLToPath,pathToFileURL} from 'node:url'
const repo=fileURLToPath(new URL('../',import.meta.url)),origin='http://localhost:5173'
const {chromium,expect}=await import(pathToFileURL(path.join(repo,'tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const output=fs.mkdtempSync(path.join(os.tmpdir(),'frevo-identity-preview-')),fixture='.codex-identity-preview-'+crypto.randomUUID()+'.tsx',fixturePath=path.join(repo,'payroll-ui',fixture)
fs.writeFileSync(fixturePath,`import React,{useState} from 'react';import ReactDOM from 'react-dom/client';import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';import Provider from '/src/components/ExcelPayslipCodePreview.tsx';import Grid from '/src/components/ExcelPayslipMappingPreview.tsx';import Employees from '/src/components/ExcelPayslipEmployeePreview.tsx';import Details from '/src/components/ExcelPayslipRowDetails.tsx';import {payslipPreviewCodes,payslipPreviewIdentities} from '/src/utils/excelPayslipCodePreview.ts';import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/AppShell.css';import '/src/components/ExcelPayslips.css';
const headers=['Employee Name','Employee Code','Phone Number','Bank A/C No','UAN Number','Net pay'],kinds=['employeeName','employeeCode','info','info','info','netPay'],columns=headers.map((sourceHeader,columnIndex)=>({sourceHeader,columnIndex,label:sourceHeader,kind:kinds[columnIndex]}));const sheet={name:'Salary',columnCount:6,rows:[{sourceRow:1,cells:headers.map(value=>({value}))},...Array.from({length:120},(_,i)=>({sourceRow:i+2,cells:[{value:'Synthetic employee '+(i+1)},{value:'SOURCE'+(i+1)},{value:i===119?'9876543210':''},{value:i===42?'001234567890':''},{value:i===2?'123456789012':''},{value:'10000'}]}))]};const original=JSON.stringify(sheet);window.__unchanged=()=>original===JSON.stringify(sheet);
function App(){const[excluded,setExcluded]=useState([]),[review,setReview]=useState(null);window.__review=()=>review;const rows=sheet.rows.slice(1).filter(r=>!excluded.includes(r.sourceRow)).map(r=>({id:'row-'+r.sourceRow,sourceRow:r.sourceRow,employeeName:r.cells[0].value,employeeCode:r.cells[1].value,email:'',information:[{label:'Bank A/C No',value:r.cells[3].value}],earnings:[{label:'Basic',amount:10000}],deductions:[],employerContributions:[],netPay:10000,warnings:[]}));return <ConfigProvider theme={theme}><Provider clientId={33} codes={payslipPreviewCodes(sheet,1,columns)} identityRows={payslipPreviewIdentities(sheet,1,columns)}><main className="excel-payslip-import" style={{padding:12}}><Grid sheet={sheet} headerRow={1} columns={columns} fileName="Synthetic.xlsx" excludedRows={excluded} disabled={false} onPatch={()=>{}} onSuggest={()=>{}} onExclude={n=>setExcluded(v=>[...v,n])} onInclude={n=>setExcluded(v=>v.filter(i=>i!==n))}/><Employees rows={rows} simpleLayout={false} generationEnabled disabled={false} canManage={false} amount={v=>String(v)} onDetails={setReview} onCorrect={()=>{throw Error('Unexpected correction')}} onExclude={n=>setExcluded(v=>[...v,n])}/>{review&&<Details clientId={33} row={review} compareMaster importOpen deliveryDetails="Not yet checked"/>}</main></Provider></ConfigProvider>}ReactDOM.createRoot(document.getElementById('root')).render(<App/>);`)
const html=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const browser=await chromium.launch({headless:true}),context=await browser.newContext({viewport:{width:1366,height:768},serviceWorkers:'block'}),page=await context.newPage(),errors=[],unexpected=[],checks=[]
let mode='ready',identityReads=0,detailReads=0
page.on('pageerror',e=>errors.push(e.message));page.setDefaultTimeout(10000)
await context.routeWebSocket('**/*',socket=>socket.close())
await context.route('**/*',route=>{
 const request=route.request(),url=new URL(request.url()),p=url.pathname
 const reply=(data,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)})
 if(p.startsWith('/api/')){
  if(p==='/api/excel-payslips/employee-identity-preview'&&request.method()==='POST'){
   identityReads++;const {rows}=request.postDataJSON();assert.equal(rows.length,120);assert.equal(rows.find(r=>r.sourceRow===44).bankAccount,'001234567890');assert.equal(rows.find(r=>r.sourceRow===121).mobile,'9876543210');assert.equal(rows.find(r=>r.sourceRow===4).uan,'123456789012');assert.ok(rows.every(row=>!Object.hasOwn(row,'netPay')))
   if(mode==='error')return reply({error:'Synthetic unavailable API'},404)
   return reply({clientId:mode==='wrong-client'?11:33,checkedRows:rows.length,rows:rows.map(row=>({sourceRow:row.sourceRow,status:[44,121].includes(row.sourceRow)?'Existing':row.sourceRow===4?'Review':'No match',masterCode:row.sourceRow===44?'NHDC043':row.sourceRow===121?'NHDC120':'',active:true,basis:row.sourceRow===44?['Bank Account']:row.sourceRow===121?['Phone']:[],reasons:row.sourceRow===4?['Phone and UAN point to different employees; choose the verified Master record.']:[]}))})
  }
  if(p==='/api/excel-payslips/employee-master-comparison'&&request.method()==='GET'){
   detailReads++;assert.equal(url.searchParams.get('employeeCode'),'NHDC043');return reply({clientId:33,employeeCode:'NHDC043',status:'Matched',fields:{EmployeeCode:'NHDC043',EmployeeName:'Synthetic employee 43',Email:'',BankAccountNo:'001234567890'}})
  }
  unexpected.push(request.method()+' '+p);return route.abort('blockedbyclient')
 }
 if(url.origin!==origin)return route.abort()
 if(p==='/identity-preview-fixture')return route.fulfill({contentType:'text/html',body:html})
 if(p==='/favicon.ico')return route.fulfill({status:204,body:''})
 return route.continue()
})
try{
 await page.goto(origin+'/identity-preview-fixture')
 const employees=page.getByRole('region',{name:'Employee import preview'}),rows=employees.locator('tbody tr[data-row-key]')
 await expect(rows.first()).toContainText('SOURCE43');await expect(rows.nth(1)).toContainText('SOURCE120');await expect(rows.first()).toHaveClass(/excel-code-existing-row/)
 await rows.first().getByText('Existing employee',{exact:true}).hover();await expect(page.getByRole('tooltip').last()).toContainText('Matched by Bank Account. Master code: NHDC043')
 await rows.nth(2).getByText('Review',{exact:true}).hover();await expect(page.getByRole('tooltip').last()).toContainText('Phone and UAN point to different employees')
 await rows.first().getByRole('button',{name:'Details',exact:true}).click();await expect(page.getByRole('region',{name:'Master versus Excel comparison'})).toContainText('NHDC043');assert.equal(detailReads,1)
 await rows.nth(2).getByRole('button',{name:'Details',exact:true}).click();await expect(page.getByText('Employee identity needs review',{exact:true})).toBeVisible();assert.equal(detailReads,1)
 checks.push('Different source codes matched by bank/phone appear red at the top; exact Review reason is in the dark tooltip and Details, and no ambiguous Master is fetched')
 await employees.getByRole('checkbox',{name:'Existing codes first',exact:true}).uncheck();await expect(rows.first()).toContainText('SOURCE1')
 await employees.getByRole('checkbox',{name:'Existing codes first',exact:true}).check();await expect(rows.first()).toContainText('SOURCE43')
 await page.getByRole('button',{name:'Excel preview & map',exact:true}).click()
 const modal=page.locator('.smart-bulk-source-preview-modal:visible').last(),grid=modal.getByTestId('smart-bulk-source-preview-table')
 await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toHaveClass(/excel-code-existing-cell/)
 await grid.locator('tbody tr').nth(2).getByText('Review',{exact:true}).hover();await expect(page.getByRole('tooltip').last()).toContainText('Phone and UAN point to different employees')
 await modal.getByRole('button',{name:'Exclude source row 44',exact:true}).click();await expect(grid.locator('tbody tr').first().locator('td').nth(2)).toContainText('SOURCE120')
 assert.equal(identityReads,1);assert.equal(await page.evaluate(()=>window.__unchanged()),true)
 await expect(modal.getByRole('button',{name:'Use mapped preview',exact:true})).toBeInViewport()
 await page.screenshot({path:path.join(output,'identity-grid.png')})
 checks.push('Both previews share global sorting and field-specific review tooltips; exclusion and old/new inspection preserve original Excel cells and codes')
 mode='error';await page.goto(origin+'/identity-preview-fixture');await expect(employees.getByText('Employee identity check unavailable',{exact:true})).toBeVisible();await expect(rows.first()).toHaveClass(/excel-code-pending-row/)
 mode='ready';await employees.getByRole('button',{name:'Retry Master code check',exact:true}).click();await expect(rows.first()).toContainText('SOURCE43')
 mode='wrong-client';await page.goto(origin+'/identity-preview-fixture');await expect(employees.getByText('Employee identity check unavailable',{exact:true})).toBeVisible()
 checks.push('Old API failures and wrong-client/partial identity replies stay unverified instead of colouring unmatched employees white; explicit retry restores valid results')
 assert.deepEqual(errors,[]);assert.deepEqual(unexpected,[])
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:true,checks,actualBusinessWrites:0,identityReads,detailReads,errors,unexpected},null,2));console.log(JSON.stringify({passed:true,checks,output}))
}catch(error){await page.screenshot({path:path.join(output,'failure.png')});fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({error:String(error),errors,unexpected,tooltipDom:await page.locator('.ant-tooltip').evaluateAll(nodes=>nodes.map(n=>({text:n.textContent,html:n.outerHTML.slice(0,1200),bounds:n.getBoundingClientRect().toJSON()}))),hovered:await page.locator('.smart-bulk-source-preview-table .ant-tag-orange').evaluateAll(nodes=>nodes.map(n=>{const r=n.getBoundingClientRect();return {text:n.textContent,hover:n.matches(':hover'),atPoint:document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.outerHTML.slice(0,400)}}))},null,2));console.error('Diagnostics: '+output);throw error}
finally{await context.close();await browser.close();fs.rmSync(fixturePath,{force:true})}
