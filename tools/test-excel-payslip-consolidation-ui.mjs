import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = path.join(os.tmpdir(), 'frevo-excel-consolidation-ui'); fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-consolidation-${crypto.randomUUID()}.tsx`, fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips" /><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`

const csv = days => Buffer.from('Employee Name,Employee Code,Work location,UAN,Attendance,Basic,PF,Net Pay\nSame Person,NH001,Old location,101123456789,'+days+',1500,180,1320')
const browser=await chromium.launch({headless:process.env.HRMS_TEST_HEADLESS==='1'}),context=await browser.newContext({viewport:{width:1366,height:900}}),page=await context.newPage()
const errors=[],submissions=[];let forbidden=0
page.on('pageerror',e=>errors.push(e.message))
await context.route('**/*',async route=>{
 const q=route.request(),u=new URL(q.url()),p=u.pathname,reply=data=>route.fulfill({contentType:'application/json',body:JSON.stringify(data)})
 if(!['localhost','127.0.0.1'].includes(u.hostname))return route.abort()
 if(p==='/consolidation-fixture')return route.fulfill({contentType:'text/html',body:html})
 if(!p.startsWith('/api/'))return route.continue()
 if(q.method()==='POST'&&p==='/api/excel-payslips/batches'){
  const body=q.postDataJSON();submissions.push(body);assert.equal(body.month,'2026-09');assert.equal(body.rows.length,2)
  assert.ok(body.rows.every(r=>r.information.some(i=>i.label==='Reviewed source split'&&i.value==='Confirmed')))
  assert.deepEqual(body.rows.map(r=>r.information.find(i=>i.label==='Work location').value),['Old location','Old location'])
  assert.equal(new Set(body.rows.map(r=>r.information.find(i=>i.label==='Source workbook').value)).size,2)
  return reply({...body,id:'0123456789abcdef0123456789abcdef',clientId:42,clientName:'Synthetic',createdAtUtc:new Date().toISOString(),createdBy:'test'})
 }
 if(q.method()!=='GET'){forbidden++;return route.fulfill({status:403,body:'{}'})}
 if(p==='/api/auth/me')return reply({id:1,clientId:42,displayName:'Synthetic Admin',roles:['client_admin'],permissions:['payroll.run'],isActive:true})
 if(p.endsWith('/clients'))return reply([{id:42,name:'Synthetic'}])
 if(p.endsWith('/profile'))return reply(null)
 if(p.endsWith('/delivery-status'))return reply({items:[]})
 if(p.endsWith('/mail-jobs'))return reply({items:[]})
 return reply([])
})
try{
 await page.goto(origin+'/consolidation-fixture?clientId=42')
 await page.getByRole('button',{name:'Import spreadsheet',exact:true}).click()
 const drawer=page.locator('.excel-payslip-import')
 await drawer.getByLabel('Payslip salary month',{exact:true}).fill('2026-09')
 await drawer.locator('input[type=file]').setInputFiles([{name:'Region A.csv',mimeType:'text/csv',buffer:csv(11)},{name:'Region B.csv',mimeType:'text/csv',buffer:csv(15)}])
 const allow=drawer.getByRole('checkbox',{name:'Allow verified transfers between source payroll units',exact:true})
 await expect(allow).toBeVisible();await expect(allow).not.toBeChecked()
 const save=drawer.locator('button').filter({hasText:'Save batch (2)'})
 await expect(save).toBeDisabled();await expect(drawer.getByText('2 import issues',{exact:true})).toBeVisible()
 await allow.check();await expect(drawer.getByText('0 import issues',{exact:true})).toBeVisible();await expect(save).toBeEnabled()
 await page.screenshot({path:path.join(output,'desktop.png'),fullPage:true})
 await page.setViewportSize({width:390,height:844});await allow.scrollIntoViewIfNeeded();await expect(save).toBeVisible()
 await page.screenshot({path:path.join(output,'mobile.png'),fullPage:true});await save.click()
 await expect(page.getByText('2 payslips saved. Saved batches retain their imported amounts.',{exact:true})).toBeVisible()
 await page.getByRole('button',{name:'Import spreadsheet',exact:true}).click()
 await expect(drawer.getByRole('checkbox',{name:'Allow verified transfers between source payroll units',exact:true})).toHaveCount(0)
 assert.equal(submissions.length,1);assert.equal(forbidden,0);assert.deepEqual(errors,[])
 fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({checks:['Multiple original files combined with reviewed mapping','Duplicate codes blocked by default','Reviewed units retain work location and source provenance','Desktop/mobile save works'],realDatabaseWrites:0,realEmails:0,forbidden,errors},null,2))
 console.log(JSON.stringify({passed:true,submissions:submissions.length,realDatabaseWrites:0,realEmails:0,output}))
}catch(e){await page.screenshot({path:path.join(output,'failure.png'),fullPage:true});console.log((await page.locator('body').innerText()).slice(-2400));throw e}
finally{await browser.close();fs.rmSync(fixturePath,{force:true})}
