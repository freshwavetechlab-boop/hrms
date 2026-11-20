import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin=process.env.HRMS_TEST_UI_URL||'http://localhost:5173'
if(!['localhost','127.0.0.1'].includes(new URL(origin).hostname))throw Error('Local UI only')
const output=await fs.mkdtemp(path.join(os.tmpdir(),'frevo-register-mail-ui-'))
const fixture=`.codex-register-mail-${crypto.randomUUID()}.tsx`,fixturePath=path.resolve('payroll-ui',fixture)
await fs.writeFile(fixturePath,`import React from'react';import{createRoot}from'react-dom/client';import{BrowserRouter}from'react-router-dom';import{ConfigProvider}from'antd';import theme from'./src/theme/hrmsTheme';import AuthGate from'./src/components/AuthGate';import ToastProvider from'./src/components/ToastProvider';import Register from'./src/components/PayslipRegister';import Header from'./src/components/layout/AppPageHeader';import'./src/index.css';import'./src/OrganizationSetup.css';import'./src/ModuleDrawer.css';import'./src/AppShell.css';createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Payslip Register" description="" icon={null} breadcrumbs={[]} workspace="payslips"/><main><Register/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);`)
const html=`<html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px}</style></head><body><div id="root"></div><script type="module">import R from'/@react-refresh';R.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const run={id:98,clientId:907,clientName:'Synthetic payroll',payPeriod:'2026-10',status:'Approved',employees:Array.from({length:62},(_,i)=>({employeeId:i+1,employeeCode:`TEST${String(i+1).padStart(3,'0')}`,employeeName:`Synthetic Employee ${i+1}`,isSkipped:false,grossPay:1000,oneTimeEarnings:0,statutoryDeductions:100,oneTimeDeductions:0,netPay:900,paymentStatus:'Pending'}))}
let statuses=run.employees.map(row=>{
 const state=({1:'Sent',2:'Email missing',3:'Queued',4:'Deferred',5:'Unknown',6:'Failed',7:'Email missing'})[row.employeeId]||'Not sent'
 return {employeeId:row.employeeId,employeeCode:row.employeeCode,email:[2,7].includes(row.employeeId)?'':`employee${row.employeeId}@example.test`,status:state,canSend:state==='Not sent',canResend:['Sent','Failed'].includes(state),lastRecipient:state==='Sent'?'employee1@example.test':'',sentCopyStatus:state==='Sent'?'Saved':'Unknown',message:'Synthetic delivery status'}
})
let jobs=[],failOnce=true
const submissions=[],errors=[],checks=[],blocked=[]
const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1366,height:768}})
page.on('pageerror',error=>errors.push(error.message))
try{
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url()),p=url.pathname,reply=json=>route.fulfill({json})
  if(p.startsWith('/api/')){
   if(p==='/api/auth/me')return reply({id:7,displayName:'QA',permissions:['payroll.run','payroll.view','security.manage'],roles:['super_admin'],isActive:true})
   if(p==='/api/clients')return reply([{id:907,name:'Synthetic payroll',isActive:true},{id:908,name:'Draft only client',isActive:true},{id:909,name:'Paid payroll client with a longer location name',isActive:true},{id:910,name:'No payroll client',isActive:true}])
   if(p==='/api/pay-runs')return reply([{...run,employees:undefined},{id:99,clientId:908,payPeriod:'2026-10',status:'Draft'},{id:100,clientId:909,payPeriod:'2026-09',status:'Paid'}])
   if(p==='/api/pay-runs/98')return reply(run)
   if(p.endsWith('/mail-status'))return reply({clientId:907,payRunId:98,items:statuses,jobs})
   if(p.endsWith('/send-job')&&request.method()==='POST'){
    const body=request.postDataJSON();submissions.push(body)
    const old=jobs.find(job=>job.id===body.requestId)
    const job=old||{id:body.requestId,clientId:907,payRunId:98,month:'2026-10',resend:body.resend,createdAtUtc:new Date().toISOString(),status:'Processing',message:'Synthetic durable queue',total:body.employeeIds.length,completed:0,queued:body.employeeIds.length,failed:0,missingEmail:0,skipped:0,unknown:0,totalEmails:body.employeeIds.length,acceptedEmails:0,sentCopySavedEmails:0,sentCopyUnverifiedEmails:0,deliveredEmails:0,percentComplete:0,isComplete:false}
    if(!old){jobs=[job,...jobs];statuses=statuses.map(item=>body.employeeIds.includes(item.employeeId)?{...item,status:'Queued',canSend:false,canResend:false}:item)}
    if(failOnce){failOnce=false;return route.fulfill({status:503,json:{error:'Synthetic response lost after commit'}})}
    return reply({job,items:body.employeeIds.map(employeeId=>({employeeId,employeeCode:`TEST${String(employeeId).padStart(3,'0')}`,email:`employee${employeeId}@example.test`,queueId:employeeId,status:'Queued',message:'Individual PDF queued.'}))})
   }
   throw Error(`Unexpected API request ${request.method()} ${p}`)
  }
  if(!['localhost','127.0.0.1'].includes(url.hostname)){blocked.push(url.hostname);return route.abort()}
  if(p==='/register-mail-fixture')return route.fulfill({contentType:'text/html',body:html})
  return route.continue()
 })
 await page.goto(origin+'/register-mail-fixture')
 await expect(page.getByRole('button',{name:'Select with email (55)',exact:true})).toBeEnabled({timeout:20000})
 const clientPicker=page.getByRole('combobox',{name:'Payslip register client',exact:true})
 await clientPicker.press('Enter')
 const clientDropdown=page.locator('.ant-select-dropdown:visible')
 await expect(clientDropdown.locator('.excel-payslip-client-has-batches')).toHaveCount(2)
 await expect(clientDropdown.locator('.excel-payslip-client-no-batches')).toHaveCount(2)
 await expect(clientDropdown.getByText('Approved payroll',{exact:true})).toHaveCount(2)
 await expect(clientDropdown.locator('.excel-payslip-client-has-batches').filter({hasText:'Synthetic payroll'})).toBeVisible()
 await expect(clientDropdown.locator('.excel-payslip-client-has-batches').filter({hasText:'Paid payroll client'})).toBeVisible()
 for(const size of [{width:1366,height:768},{width:768,height:1024},{width:390,height:844}]){
  await page.setViewportSize(size)
  await expect.poll(async()=>{const rect=await clientDropdown.boundingBox();return !!rect&&rect.x>=-.5&&rect.x+rect.width<=size.width+.5}).toBe(true)
  await page.screenshot({path:path.join(output,`client-indicators-${size.width}.png`),fullPage:false})
 }
 await clientPicker.fill('Draft only')
 await expect(clientDropdown.locator('.ant-select-item-option')).toHaveCount(1)
 await clientDropdown.getByText('Draft only client',{exact:true}).click()
 await expect(page.getByText('No approved pay run is available for this client.',{exact:true})).toBeVisible()
 await clientPicker.fill('Synthetic')
 await clientDropdown.getByText('Synthetic payroll',{exact:true}).click()
 await expect(page.getByRole('button',{name:'Select with email (55)',exact:true})).toBeEnabled()
 await page.setViewportSize({width:1366,height:768})
 checks.push('client-selector-reuses-green-approved-and-paid-indicators-with-search-safe-switching-and-responsive-popup')
 await expect(page.getByRole('button',{name:'Send selected (0)',exact:true})).toBeDisabled()
 await expect(page.getByText('Email status',{exact:true})).toBeVisible()
 checks.push('all62-employees-load-with-sent-missing-queued-delayed-and-unknown-statuses')
 await page.getByRole('button',{name:'Select with email (55)',exact:true}).click()
 await page.getByRole('button',{name:'Send selected (55)',exact:true}).click()
 const delivery=page.getByRole('dialog').filter({has:page.getByText('Payslip delivery',{exact:true})})
 await expect(delivery.getByText('Synthetic response lost after commit',{exact:true})).toBeVisible()
 await delivery.getByRole('button',{name:'Retry same email request',exact:true}).click()
 assert.equal(submissions.length,2);assert.deepEqual(submissions[0],submissions[1])
 assert.deepEqual(submissions[0].employeeIds,Array.from({length:55},(_,i)=>i+8));assert.equal(submissions[0].resend,false)
 await expect(delivery.getByText('Emails SMTP accepted: 0 / 55',{exact:true})).toBeVisible()
 checks.push('one-bulk-request-selects-valid-unsent-employees-across-all-pages-and-lost-response-retries-the-same-id')
 statuses=statuses.map(item=>submissions[0].employeeIds.includes(item.employeeId)?{...item,status:'Sent',canSend:false,canResend:true,sentCopyStatus:'Saved',lastRecipient:item.email}:item)
 jobs=jobs.map(job=>({...job,status:'Completed',queued:0,completed:job.total,acceptedEmails:job.total,sentCopySavedEmails:job.total,percentComplete:100,isComplete:true}))
 await delivery.getByRole('button',{name:'Refresh email progress',exact:true}).click()
 await expect(delivery.getByText('Emails SMTP accepted: 55 / 55',{exact:true})).toBeVisible()
 await expect(delivery.getByText('Saved in Sent folder: 55',{exact:true})).toBeVisible()
 checks.push('background-progress-refreshes-smtp-acceptance-and-sent-copy-counts-separately')
 await page.screenshot({path:path.join(output,'desktop-progress.png'),fullPage:false})
 await delivery.getByRole('button',{name:'Close',exact:true}).click()
 await page.getByRole('combobox',{name:'Payslip email status',exact:true}).press('Enter')
 await page.getByText('Sent (56)',{exact:true}).last().click()
 await expect(page.getByRole('button',{name:'Select with email (56)',exact:true})).toBeEnabled()
 await page.getByRole('button',{name:'Select with email (56)',exact:true}).click()
 await expect(page.locator('button').filter({hasText:/^Send selected \(0\)$/})).toBeDisabled()
 await page.getByRole('button',{name:'Resend selected (56)',exact:true}).click()
 const resend=page.getByRole('dialog').filter({has:page.getByText('Resend payslips',{exact:true})})
 await expect(resend.getByRole('button',{name:'Resend PDF (56)',exact:true})).toBeDisabled()
 await resend.getByRole('checkbox').check()
 for(const size of [{width:1366,height:768},{width:768,height:1024},{width:390,height:844}]){
  await page.setViewportSize(size)
  await expect.poll(async()=>{const rect=await resend.boundingBox();return !!rect&&rect.x>=-.5&&rect.x+rect.width<=size.width+.5}).toBe(true)
  await expect(resend.getByRole('button',{name:'Resend PDF (56)',exact:true})).toBeInViewport({ratio:1})
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1))
  await page.screenshot({path:path.join(output,`resend-${size.width}.png`),fullPage:false})
 }
 checks.push('acknowledged-bulk-resend-and-footer-remain-accessible-on-desktop-tablet-and-mobile')
 await resend.getByRole('button',{name:'Resend PDF (56)',exact:true}).click()
 await expect(delivery).toBeVisible()
 assert.equal(submissions.length,3);assert.equal(submissions[2].resend,true);assert.equal(submissions[2].acknowledgeResend,true)
 assert.notEqual(submissions[2].requestId,submissions[0].requestId);assert.equal(submissions[2].employeeIds.length,56)
 assert.ok(!submissions[2].employeeIds.some(id=>[2,3,4,5,7].includes(id)))
 checks.push('resend-retains-a-new-explicit-request-and-excludes-missing-pending-delayed-unknown-recipients')
 await page.reload()
 await expect(page.getByRole('button',{name:'Send selected (0)',exact:true})).toBeDisabled()
 await page.getByRole('button',{name:'Email progress',exact:true}).click()
 await expect(page.getByRole('dialog').getByText('Emails SMTP accepted: 0 / 56',{exact:true})).toBeVisible()
 checks.push('queue-progress-is-restored-after-refresh-without-recreating-employees-payroll-or-excel-batches')
 assert.deepEqual(errors,[]);assert.ok(blocked.every(host=>['fonts.googleapis.com','fonts.gstatic.com'].includes(host)))
 await fs.writeFile(path.join(output,'result.json'),JSON.stringify({checks,errors,submissions:submissions.length,realEmails:0,productionWrites:0},null,2))
 console.log(JSON.stringify({checks,output,realEmails:0,productionWrites:0}))
}catch(error){await page.screenshot({path:path.join(output,'failure.png'),fullPage:false});await fs.writeFile(path.join(output,'failure.json'),JSON.stringify({error:String(error),checks,errors,body:await page.locator('body').innerText()},null,2));console.error(JSON.stringify({error:String(error),checks,output}));process.exitCode=1}
finally{await browser.close();await fs.unlink(fixturePath)}
