import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath,pathToFileURL } from 'node:url'
const repo=fileURLToPath(new URL('..',import.meta.url))
const {chromium,expect}=await import(pathToFileURL(path.join(repo,'tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin='http://localhost:5173',output=fs.mkdtempSync(path.join(os.tmpdir(),'frevo-workbook-sources-'))
const fixture='.codex-workbook-sources-'+crypto.randomUUID()+'.tsx',fixturePath=path.join(repo,'payroll-ui',fixture)
fs.writeFileSync(fixturePath,`import React,{useState} from 'react';import ReactDOM from 'react-dom/client';import Sources from '/src/components/ExcelWorkbookDependencies.tsx';import {collectWorkbookDependencies} from '/src/utils/workbookDependencyCapture.ts';import '/src/components/ExcelPayslips.css';
const key='wb_'+'a'.repeat(64),second='wb_'+'b'.repeat(64),external=new URLSearchParams(location.search).has('external');
const root={name:'Salary',columnCount:2,rows:[{sourceRow:1,cells:[{value:'Employee'},{value:'Pay'}]},...Array.from({length:109},(_,i)=>({sourceRow:i+2,cells:[{value:'Synthetic '+i},{value:'26000',formula:true,formulaText:external?'VLOOKUP(A'+(i+2)+",'[1]Attendance'!A2:B110,2,0)":'VLOOKUP(A'+(i+2)+',Attendance!A2:B110,2,0)'}]}))]};
const attendance={name:'Attendance',columnCount:2,rows:[{sourceRow:2,cells:[{value:'Synthetic'},{value:'26'}]}]};
const book={key,sha256:'a'.repeat(64),fileName:'Synthetic payroll.xlsx',sheets:external?[root]:[root,attendance]};const extra={key:second,sha256:'b'.repeat(64),fileName:'Synthetic attendance.xlsx',sheets:[attendance]};const columns=[{columnIndex:1,sourceHeader:'Pay',label:'Pay',kind:'earning'}];
function App(){const[books,setBooks]=useState([book]),[mappings,setMappings]=useState([]),[reviewedKey,setReviewedKey]=useState(''),[referenceOnly,setReferenceOnly]=useState(false),[revision,setRevision]=useState(0);const sheet=revision?{...root,rows:root.rows.map((r,i)=>i===1?{...r,cells:r.cells.map((c,j)=>j===1?{...c,formulaText:c.formulaText+'+1'}:c)}:r)}:root;const report=collectWorkbookDependencies(sheet,key,books,mappings,columns,1,[]);window.__sourceState=()=>({reviewedKey,report,referenceOnly,mappings,books:books.map(b=>b.key)});return <main><Sources books={books} mappings={mappings} report={report} disabled={false} reviewedKey={reviewedKey} referenceOnly={referenceOnly} onFiles={()=>setBooks([book,extra])} onMappings={v=>{setMappings(v);setReviewedKey('')}} onReview={setReviewedKey} onReferenceOnly={setReferenceOnly}/><button onClick={()=>setRevision(v=>v+1)}>Change test formula</button></main>};ReactDOM.createRoot(document.getElementById('root')).render(<App/>);`)
const html=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:12px;font-family:Arial}main{max-width:100%;min-width:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const viteClient='export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=document.querySelector("style[data-fixture-id="+JSON.stringify(id)+"]");if(!e){e=document.createElement("style");e.dataset.fixtureId=id;document.head.appendChild(e)}e.textContent=css}export function removeStyle(){}export function injectQuery(url){return url}'
const browser=await chromium.launch({headless:true}),checks=[],unexpected=[],errors=[]
try{
 const context=await browser.newContext({viewport:{width:1366,height:768},serviceWorkers:'block'}),page=await context.newPage()
 page.on('pageerror',e=>errors.push(e.message));await context.routeWebSocket('**/*',s=>s.close())
 await context.route('**/*',route=>{const r=route.request(),u=new URL(r.url()),p=u.pathname;if(p.startsWith('/api/')||u.origin!==origin){unexpected.push(r.method()+' '+u.href);return route.abort('blockedbyclient')}if(p==='/sources-fixture')return route.fulfill({contentType:'text/html',body:html});if(p==='/@vite/client')return route.fulfill({contentType:'application/javascript',body:viteClient});if(p==='/favicon.ico')return route.fulfill({status:204,body:''});if(r.method()==='GET'&&(p==='/'+fixture||['/src/','/node_modules/','/@','/assets/'].some(x=>p.startsWith(x))))return route.continue();unexpected.push(p);return route.abort('blockedbyclient')})
 await page.goto(origin+'/sources-fixture')
 const details=page.locator('.excel-workbook-source-details'),ack=page.getByRole('checkbox',{name:'Use the linked sheets in this uploaded Excel for formula inputs',exact:true})
 await expect(ack).toHaveCount(0);await expect(details).not.toHaveAttribute('open','')
 await expect(page.getByText('Linked sheets included automatically',{exact:true})).toBeVisible()
 await expect(page.getByText(/These sheets are already included; no extra Excel file is needed/)).toBeVisible()
 await expect(page.getByLabel('Supply dependency workbook')).toBeHidden()
 assert.equal((await page.evaluate(()=>window.__sourceState())).reviewedKey,'')
 let state=await page.evaluate(()=>window.__sourceState());assert.equal(state.reviewedKey,'');assert.equal(state.referenceOnly,false)
 const previousKey=state.report.reviewKey
 await page.getByRole('button',{name:'Change test formula',exact:true}).click();await expect(ack).toHaveCount(0)
 state=await page.evaluate(()=>window.__sourceState());assert.notEqual(state.report.reviewKey,previousKey)
 await expect(page.getByText('Linked sheets included automatically',{exact:true})).toBeVisible()
 checks.push('Complete same-file linked sheets are included automatically without an unnecessary acknowledgement; edited formulas are captured afresh, optional upload remains collapsed')
 await details.locator('summary').first().click();await expect(page.getByLabel('Linked worksheet summary')).toHaveText('Salary → Attendance (same Excel file)')
 await expect(page.getByText('Show formula cells (109)',{exact:true})).toBeVisible()
 await expect(page.getByText('Salary!B2 → Attendance / Attendance',{exact:true})).toBeHidden()
 await page.getByText('Show formula cells (109)',{exact:true}).click();await expect(page.getByText('Salary!B2 → Attendance / Attendance',{exact:true})).toBeVisible()
 await expect(page.getByRole('checkbox',{name:'Save payslip amounts only; keep payroll setup blocked until formula inputs are resolved',exact:true})).toHaveCount(0)
 checks.push('Advanced trace preserves formula cells; a complete internal source does not offer blocked amounts-only saving unnecessarily; no API is called')
 for(const[width,height]of[[1366,768],[768,1024],[390,844]]){await page.setViewportSize({width,height});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);await page.screenshot({path:path.join(output,'sources-'+width+'.png'),fullPage:true})}
 await page.goto(origin+'/sources-fixture?external=1')
 await expect(page.getByText('Some formula inputs need attention',{exact:true})).toBeVisible();await expect(details).toHaveAttribute('open','')
 await page.getByRole('checkbox',{name:'Save payslip amounts only; keep payroll setup blocked until formula inputs are resolved',exact:true}).check()
 assert.equal((await page.evaluate(()=>window.__sourceState())).referenceOnly,true)
 await expect(page.getByRole('checkbox',{name:'I reviewed this external alias and workbook version',exact:true})).toBeDisabled()
 await page.getByLabel('Supply dependency workbook').setInputFiles({name:'Synthetic attendance.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:Buffer.from('Synthetic fixture only')})
 const select=page.locator('.ant-select').filter({has:page.getByRole('combobox',{name:'Map [1] from wb_'+'a'.repeat(64),exact:true})})
 await select.click();await page.locator('.ant-select-dropdown').getByText('Synthetic attendance.xlsx · '+'b'.repeat(12),{exact:true}).click()
 await page.getByRole('checkbox',{name:'I reviewed this external alias and workbook version',exact:true}).check()
 await expect(page.getByRole('checkbox',{name:'Use these reviewed Excel files for formula inputs',exact:true})).toBeVisible()
 state=await page.evaluate(()=>window.__sourceState());assert.equal(state.report.issues.length,0);assert.equal(state.mappings[0].toWorkbookKey,'wb_'+'b'.repeat(64));assert.equal(state.reviewedKey,'')
 checks.push('Missing external workbook remains actionable; exact uploaded version and explicit alias review required; final source review remains separate')
 assert.deepEqual(unexpected,[]);assert.deepEqual(errors,[]);fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:true,checks,apiRequests:0,actualBusinessWrites:0},null,2));console.log(JSON.stringify({passed:true,groups:checks.length,output}));await context.close()
}finally{await browser.close();fs.rmSync(fixturePath,{force:true})}
