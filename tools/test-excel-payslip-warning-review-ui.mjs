import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(os.tmpdir(), 'frevo-excel-warning-review-ui'); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-warning-review-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`


const originalId='0123456789abcdef0123456789abcdef',newId='1123456789abcdef0123456789abcdef'
const headers=['Employee Name','Employee Code','Account No','Basic','PF','In Hand Salary','Raw error','Gross','Monthly base']
const kinds=['employeeName','employeeCode','info','earning','deduction','netPay','ignore','grossTotal','ignore']
const columns=headers.map((label,columnIndex)=>({columnIndex,sourceHeader:label,label,kind:kinds[columnIndex]}))
const rows=[
 {id:'one',sourceRow:2,employeeName:'Same Name',employeeCode:'TEST001',email:'',information:[{label:'Account No',value:'000012345'}],earnings:[{label:'Basic',amount:1000.25}],deductions:[{label:'PF',amount:120}],employerContributions:[],netPay:880,declaredGross:1000.25,declaredDeductions:null,warnings:['Mapped earnings less deductions do not match source net pay. Source net pay is preserved.']},
 {id:'two',sourceRow:3,employeeName:'Same Name',employeeCode:'TEST002',email:'',information:[{label:'Account No',value:'000098765'}],earnings:[{label:'Basic',amount:500}],deductions:[{label:'PF',amount:50}],employerContributions:[],netPay:450,declaredGross:550,declaredDeductions:null,warnings:['Mapped earnings do not match the source gross total. Source amounts are preserved.']},
 {id:'three',sourceRow:4,employeeName:'Clean Person',employeeCode:'TEST003',email:'',information:[{label:'Account No',value:'000011111'}],earnings:[{label:'Basic',amount:600}],deductions:[{label:'PF',amount:60}],employerContributions:[],netPay:540,declaredGross:600,declaredDeductions:null,warnings:[]}
]
const original={id:originalId,clientId:42,clientName:'Synthetic',month:'2026-09',sourceFileName:'Synthetic.xlsx',sheetName:'Salary',headerRow:1,simpleLayout:true,createdAtUtc:'2026-10-17T12:00:00Z',createdBy:'test',rows}
const source={sheetName:'Salary',headerRow:1,columnCount:9,columns,excludedSourceRows:[5],dateSystem:'1900',rows:[{sourceRow:1,cells:headers.map(value=>({value}))},...rows.map(row=>({sourceRow:row.sourceRow,cells:[{value:row.employeeName},{value:row.employeeCode},{value:row.information[0].value},{value:String(row.earnings[0].amount),formulaText:'I'+row.sourceRow+'*30'},{value:String(row.deductions[0].amount)},{value:String(row.netPay),formulaText:'ROUND(D'+row.sourceRow+'-E'+row.sourceRow+',0)'},{value:'#REF!',error:'#REF!'},{value:String(row.declaredGross)},{value:'30'}]})),{sourceRow:5,cells:[{value:'Historical reference'},{value:'OLD'},{value:''},{value:'100'},{value:'0'},{value:'100'},{value:''},{value:'100'},{value:'30'}]}]}
const before=structuredClone({original,source}),submissions=[],checks=[],errors=[];let forbidden=0,failSave=true,readOnly=false,legacy=false,saved=null,storedSource=null
const browser=await chromium.launch({headless:process.env.HRMS_TEST_HEADLESS==='1'}),context=await browser.newContext({viewport:{width:1366,height:900}}),page=await context.newPage()
page.on('pageerror',e=>errors.push(e.message))
await context.route('**/*',async route=>{
 const q=route.request(),u=new URL(q.url()),p=u.pathname,reply=data=>route.fulfill({contentType:'application/json',body:JSON.stringify(data)})
 if(!['localhost','127.0.0.1'].includes(u.hostname))return route.abort()
 if(p==='/warnings-fixture')return route.fulfill({contentType:'text/html',body:html})
 if(!p.startsWith('/api/'))return route.continue()
 if(q.method()==='POST'&&p==='/api/excel-payslips/batches'){
  const body=q.postDataJSON();submissions.push(body);assert.equal(body.rows.length,3);assert.equal(body.rows[0].netPay,880.25);assert.deepEqual(body.rows.slice(1).map(r=>r.netPay),[450,540])
  assert.equal(body.calculationSource.rows.find(r=>r.sourceRow===2).cells[5].value,'880.25');assert.equal(body.calculationSource.rows.find(r=>r.sourceRow===2).cells[5].formulaText,'ROUND(D2-E2,0)');assert.match(body.calculationSource.rows.find(r=>r.sourceRow===2).cells[5].calculationError,/manually corrected/)
  assert.equal(body.calculationSource.rows.find(r=>r.sourceRow===2).cells[3].formulaText,'I2*30');assert.deepEqual(body.calculationSource.excludedSourceRows,[5]);assert.equal(body.rows[0].information[0].value,'000012345')
  if(failSave){failSave=false;return route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({error:'Synthetic save unavailable; retry safely.'})})}
  saved={...body,id:newId,clientId:42,clientName:'Synthetic',createdAtUtc:'2026-10-17T13:00:00Z',createdBy:'test',rows:body.rows.map((r,i)=>({...r,warnings:i===1?[rows[1].warnings[0]]:[]}))};storedSource=body.calculationSource
  return reply(saved)
 }
 if(q.method()!=='GET'){forbidden++;return route.fulfill({status:403,body:'{}'})}
 if(p==='/api/auth/me')return reply({id:1,clientId:42,displayName:'Synthetic',roles:['client_admin'],permissions:readOnly?['reports.read']:['payroll.run'],isActive:true})
 if(p.endsWith('/clients'))return reply([{id:42,name:'Synthetic'}])
 if(p.endsWith('/batches'))return reply((saved?[saved,original]:[original]).map(b=>({...b,rowCount:b.rows.length,rows:undefined})))
 if(p.endsWith('/batches/'+originalId))return reply(original)
 if(p.endsWith('/batches/'+newId))return reply(saved)
 if(p.endsWith('/calculation'))return legacy?route.fulfill({status:404,contentType:'application/json',body:JSON.stringify({error:'This batch has no saved worksheet. Import the original workbook.'})}):reply(p.includes(newId)?storedSource:source)
 if(p.endsWith('/delivery-status'))return reply({items:[]})
 if(p.endsWith('/mail-jobs'))return reply({items:[]})
 return reply([])
})
const warningDrawer=page.locator('.excel-payslip-warning-review'),detailDrawer=page.locator('.excel-payslip-row-review'),editor=page.locator('.excel-payslip-amount-editor')
async function openOriginal(){await page.goto(origin+'/warnings-fixture?clientId=42&batchId='+originalId);await page.getByRole('button',{name:'2 payslips with warnings',exact:true}).click();await expect(warningDrawer.getByText('Clean Person',{exact:true})).toHaveCount(0)}
try{
 await openOriginal();await expect(warningDrawer.getByText('TEST001',{exact:true})).toBeVisible();await expect(warningDrawer.getByText('TEST002',{exact:true})).toBeVisible()
 await warningDrawer.getByRole('button',{name:'Details',exact:true}).first().click();await expect(detailDrawer.getByText('Whole-rupee net pay matches',{exact:true})).toBeVisible();await expect(detailDrawer.getByText('0.25',{exact:true})).toBeVisible()
 await detailDrawer.getByRole('button',{name:'Correct amounts',exact:true}).click();const save=editor.locator('button').filter({hasText:'Save corrected batch'})
 await expect(save).toBeDisabled();await editor.getByRole('spinbutton',{name:'Correct In Hand Salary (F)',exact:true}).fill('880.25');await expect(save).toBeEnabled()
 await page.screenshot({path:path.join(output,'desktop.png'),fullPage:true});await page.setViewportSize({width:390,height:844});await expect(save).toBeVisible()
 await page.screenshot({path:path.join(output,'mobile.png'),fullPage:true});await save.click();await expect(editor.getByText('Synthetic save unavailable; retry safely.',{exact:true})).toBeVisible();await expect(editor.getByRole('spinbutton',{name:'Correct In Hand Salary (F)',exact:true})).toHaveValue('880.25');await save.click()
 await expect(page.getByRole('button',{name:'1 payslips with warnings',exact:true})).toBeVisible();await expect(editor).toHaveCount(0);assert.equal(submissions.length,2);assert.deepEqual({original,source},before)
 checks.push('Clickable warning count and precise reconciliation; same-name employees remain distinct by source row')
 checks.push('Direct corrected save retains other employees, identifiers, excluded rows and untouched formulas; failed save retains edits')
 await page.reload();await expect(page.getByRole('button',{name:'1 payslips with warnings',exact:true})).toBeVisible();checks.push('Latest corrected version reopens after refresh; previous snapshot remains unchanged')
 readOnly=true;await openOriginal();await expect(warningDrawer.getByRole('button',{name:'Correct amounts',exact:true})).toHaveCount(0);await expect(warningDrawer.getByRole('button',{name:'Edit column mapping',exact:true})).toHaveCount(0);checks.push('Read-only users can inspect warnings without editing controls')
 readOnly=false;legacy=true;await openOriginal();await warningDrawer.getByRole('button',{name:'Correct amounts',exact:true}).first().click();await expect(warningDrawer.getByText('This batch has no saved worksheet. Import the original workbook.',{exact:true})).toBeVisible();await expect(editor).toHaveCount(0);checks.push('Legacy batches without source show an actionable import message')
 assert.equal(forbidden,0);assert.deepEqual(errors,[]);console.log(JSON.stringify({checks,realDatabaseWrites:0,realEmails:0,output},null,2));fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({checks,forbidden,errors,realDatabaseWrites:0,realEmails:0},null,2))
}catch(e){await page.screenshot({path:path.join(output,'failure.png'),fullPage:true});console.log((await page.locator('body').innerText()).slice(-2500));throw e}
finally{await browser.close();fs.rmSync(fixturePath,{force:true})}
