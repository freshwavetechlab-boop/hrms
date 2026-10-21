import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { pathToFileURL } from 'node:url'
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only')
const output = await fs.mkdtemp(path.join(os.tmpdir(), 'frevo-excel-recipients-ui-'))
const fixture = `.codex-excel-recipients-${crypto.randomUUID()}.tsx`, fixturePath = path.resolve('payroll-ui', fixture)
await fs.writeFile(fixturePath, `import React from'react';import{createRoot}from'react-dom/client';import{BrowserRouter}from'react-router-dom';import{ConfigProvider}from'antd';import theme from'./src/theme/hrmsTheme';import AuthGate from'./src/components/AuthGate';import ToastProvider from'./src/components/ToastProvider';import Excel from'./src/components/ExcelPayslips';import Header from'./src/components/layout/AppPageHeader';import'./src/index.css';import'./src/OrganizationSetup.css';import'./src/ModuleDrawer.css';import'./src/AppShell.css';createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title="Excel Payslips" description="" icon={null} breadcrumbs={[]} workspace="excel-payslips"/><main><Excel/></main></div></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);`)
const html = `<html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px}</style></head><body><div id="root"></div><script type="module">import R from'/@react-refresh';R.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const id = '0123456789abcdef0123456789abcdef'
const batch = { id, clientId: 42, clientName: 'Synthetic', month: '2026-09', sourceFileName: 'Example.xlsx', sheetName: 'Salary', headerRow: 1, createdAtUtc: '2026-10-21T12:00:00Z', createdBy: 'QA', rows: ['a','b','c'].map((key,i) => ({ id:key, sourceRow:i+2, employeeName:`Employee ${key.toUpperCase()}`, employeeCode:`TEST00${i+1}`, email:key==='b'?'':`${key}@example.test`, information:[], earnings:[{label:'Basic',amount:1000}], deductions:[], employerContributions:[], netPay:1000, declaredGross:null, declaredDeductions:null, warnings:[] })) }
const oldJob = { id:'old-job',clientId:42,clientName:'Synthetic',batchId:id,month:'2026-09',createdAtUtc:'2026-10-21T12:00:00Z',status:'Needs attention',total:3,sent:3,queued:0,failed:0,missingEmail:0,skipped:0,unknown:0,completed:3,percentComplete:100,isComplete:true,message:'SMTP accepted; mailbox Sent copy is unverified. Do not resend.',dismissedAtUtc:null,mode:'Combined',recipient:'coordinator@example.test',totalEmails:1,acceptedEmails:1,sentCopySavedEmails:0,sentCopyUnverifiedEmails:1,deliveredEmails:0 }
let jobs = [oldJob], failOnce = true
const statuses = batch.rows.map(row => ({ rowId:row.id,email:row.email,status:'Pending',message:'No individual employee email has been queued.',canSend:true,sentCopyStatus:'Unknown',recipientDeliveries:[{mode:'Combined',email:oldJob.recipient,status:'Sent',sentCopyStatus:'Unknown',message:'SMTP accepted; personal delivery remains unsent.'}] }))
const submissions=[],errors=[],checks=[]
const browser=await chromium.launch({headless:true}), page=await browser.newPage({viewport:{width:1366,height:900}})
page.on('pageerror',error=>errors.push(error.message))
try {
  await page.route('**/api/**',async route=>{
    const request=route.request(),p=new URL(request.url()).pathname,reply=json=>route.fulfill({json})
    if(p.endsWith('/send-job')&&request.method()==='POST'){
      const body=request.postDataJSON();submissions.push(body);assert.equal(body.mode,'Individual');assert.deepEqual(body.rowIds,['a','c'])
      if(failOnce){failOnce=false;return route.fulfill({status:503,json:{error:'Synthetic queue response lost'}})}
      assert.deepEqual(body,submissions[0]);const job={...oldJob,id:'personal-job',status:'Queued',mode:'Individual',recipient:'',total:2,sent:0,queued:2,completed:0,percentComplete:0,isComplete:false,totalEmails:2,acceptedEmails:0,sentCopyUnverifiedEmails:0,message:'Queued for employee recipients.'};jobs=[job,oldJob]
      return reply({job,items:body.rowIds.map(key=>({rowId:key,employeeName:key,email:`${key}@example.test`,status:'Queued',message:'Individual PDF queued.'}))})
    }
    if(request.method()!=='GET') throw Error(`Unexpected mutation ${p}`)
    if(p==='/api/auth/me')return reply({id:1,clientId:42,displayName:'QA',permissions:['payroll.run'],roles:['client_admin'],isActive:true})
    if(p.endsWith('/clients'))return reply([{id:42,name:'Synthetic'}])
    if(p.endsWith('/batches'))return reply([{...batch,rows:undefined,rowCount:3}])
    if(p.endsWith('/batches/'+id))return reply(batch)
    if(p.endsWith('/delivery-status'))return reply({items:statuses})
    if(p.endsWith('/mail-jobs'))return reply({items:jobs})
    return reply([])
  })
  await page.route('**/__excel-recipient-fixture*',route=>route.fulfill({contentType:'text/html',body:html}))
  await page.goto(`${origin}/__excel-recipient-fixture?clientId=42&batchId=${id}`)
  await page.getByRole('columnheader',{name:'Employee email status',exact:true}).waitFor()
  await expect(page.getByText('No individual employee email has been queued.',{exact:true})).toHaveCount(3)
  await expect(page.getByRole('columnheader',{name:'Coordinator copy',exact:true})).toBeVisible()
  await expect(page.getByText('3 sent / 0 failed / 0 queued',{exact:true})).toHaveCount(0)
  await page.getByRole('button',{name:'Expand email progress',exact:true}).click()
  await expect(page.getByText('Emails SMTP accepted: 1 / 1',{exact:true})).toBeVisible()
  await expect(page.getByText('Saved in Sent folder: 0',{exact:true})).toBeVisible()
  await expect(page.getByText('Sent copy not verified: 1',{exact:true})).toBeVisible();checks.push('coordinator-email-count-and-unverified-sent-copy-are-distinct-from-employee-status')
  await page.getByRole('button',{name:'Email batch (3)',exact:true}).click()
  const drawer=page.locator('.excel-payslip-action'),send=drawer.locator('.ant-drawer-footer button').filter({hasText:/^Send email$/})
  await drawer.getByRole('textbox',{name:'Combined payslip recipient',exact:true}).fill('coordinator@example.test')
  await expect(send).toBeDisabled();assert.equal(submissions.length,0);checks.push('same-coordinator-cannot-be-sent-the-same-slips-again')
  const mode=drawer.getByRole('combobox',{name:'Payslip email delivery',exact:true});await mode.press('Enter');await page.getByTitle('Individual PDF to each employee',{exact:true}).click()
  await expect(drawer.getByRole('checkbox',{name:'Exclude missing / invalid email',exact:true})).toBeChecked()
  await expect(drawer.getByText('2 selected; 1 email missing / invalid',{exact:true})).toBeVisible()
  await drawer.getByRole('button',{name:'Clear recipient selection',exact:true}).click();await expect(send).toBeDisabled();await drawer.getByRole('button',{name:'Select all with email',exact:true}).click();await expect(send).toBeEnabled();checks.push('missing-email-exclusion-and-explicit-recipient-selection')
  await page.screenshot({path:path.join(output,'desktop-recipients.png'),fullPage:false})
  await page.setViewportSize({width:768,height:1024});await page.screenshot({path:path.join(output,'tablet-recipients.png'),fullPage:false})
  await page.setViewportSize({width:390,height:844});await expect(send).toBeVisible()
  await expect.poll(async()=>{const rect=await drawer.boundingBox();return !!rect && rect.x>=0 && rect.x+rect.width<=390.5}).toBe(true)
  const mobileBounds=await drawer.evaluate(element=>{
    const rect=element.getBoundingClientRect()
    return {left:rect.left,right:rect.right,width:rect.width,viewport:innerWidth,
      parents:[element,...document.querySelectorAll('.ant-drawer-content-wrapper')].map(el=>{
        const computed=getComputedStyle(el)
        return {class:el.className,rect:el.getBoundingClientRect().toJSON(),style:el.getAttribute('style'),computed:{width:computed.width,minWidth:computed.minWidth,maxWidth:computed.maxWidth,boxSizing:computed.boxSizing,padding:computed.padding,transform:computed.transform}}
      })}
  })
  await fs.writeFile(path.join(output,'mobile-bounds.json'),JSON.stringify(mobileBounds,null,2))
  assert.ok(mobileBounds.left>=0 && mobileBounds.right<=390.5,JSON.stringify(mobileBounds))
  await page.screenshot({path:path.join(output,'mobile-recipients.png'),fullPage:false});checks.push('recipient-controls-and-send-footer-on-desktop-tablet-mobile')
  await send.click();await expect(drawer.getByText(/Synthetic queue response lost/)).toBeVisible();await drawer.locator('.ant-drawer-footer button').filter({hasText:'Retry / check email request'}).click()
  assert.equal(submissions.length,2);assert.deepEqual(submissions[0],submissions[1]);await expect(drawer.getByText('Emails SMTP accepted: 0 / 2',{exact:true})).toBeVisible();checks.push('individual-send-excludes-missing-recipient-and-retry-reuses-request-id-without-false-success')
  await page.setViewportSize({width:1366,height:900})
  await drawer.locator('.ant-drawer-footer').getByRole('button',{name:'Close',exact:true}).click()
  await page.getByRole('button',{name:'Select with email',exact:true}).click()
  await page.getByRole('button',{name:'Email selected (2)',exact:true}).click()
  await expect(drawer.getByText('Individual PDF to each employee',{exact:true}).first()).toBeVisible()
  await expect(drawer.getByText('2 selected; 0 email missing / invalid',{exact:true})).toBeVisible()
  checks.push('toolbar-select-with-email-opens-individual-mode-for-the-selected-employees')
  assert.deepEqual(errors,[])
  await fs.writeFile(path.join(output,'result.json'),JSON.stringify({checks,errors,mockedRequests:submissions.length,realEmails:0,realDatabaseWrites:0},null,2));console.log(JSON.stringify({checks,output,realEmails:0,realDatabaseWrites:0}))
}catch(error){await page.screenshot({path:path.join(output,'failure.png'),fullPage:false});await fs.writeFile(path.join(output,'failure.json'),JSON.stringify({error:String(error),checks,errors,submissions,body:await page.locator('body').innerText()},null,2));console.error(JSON.stringify({error:String(error),checks,output}));process.exitCode=1}
finally{await browser.close();await fs.unlink(fixturePath)}
