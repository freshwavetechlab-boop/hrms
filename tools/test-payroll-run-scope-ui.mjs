// Mounted payroll scope regression. Every API is synthetic and intercepted;
// neither the configured database nor delivery services receive any request.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import {createRequire} from 'node:module'
import {pathToFileURL} from 'node:url'

const playwright=process.env.HRMS_PLAYWRIGHT_MODULE
  ?await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
  :createRequire(new URL('../playwright-e2e/package.json',import.meta.url))('@playwright/test')
const base=process.env.HRMS_UI_TEST_URL||'http://127.0.0.1:5173'
const output=process.env.HRMS_UI_TEST_OUTPUT||path.join(os.tmpdir(),'frevo-payroll-run-scope-ui')
fs.mkdirSync(output,{recursive:true})
const clients=[{id:99001,name:'Synthetic first client',isActive:true},{id:99002,name:'Synthetic second client',isActive:true}]
const employees=clients.map((client,index)=>({id:99101+index,clientId:client.id,employeeCode:`SYNTH-${index+1}`,firstName:'Synthetic',lastName:index?'Second':'First',employeeName:index?'Synthetic Second':'Synthetic First',isActive:true,annualCtc:120000,department:'Synthetic department',designation:'Synthetic role'}))
const groups=clients.map((client,index)=>({id:99501+index,clientId:client.id,policyBatchId:index?'synthetic-second':'synthetic-first',name:index?'Synthetic second policy':'Synthetic first policy',workLocationName:'Synthetic location',department:'',designation:'',employeeIds:[99101+index],isActive:true}))
const browser=await playwright.chromium.launch({headless:true,channel:'chrome'})
const results=[]
try{
  for(const mode of ['All','LegacySingle','Selected']){
    const context=await browser.newContext({viewport:{width:1400,height:1000}})
    const page=await context.newPage(),calls=[],blocked=[]
    let clientReads=0,oldClientResponseFinished=false
    const user={id:99000,email:'synthetic-reviewer@example.test',displayName:'Synthetic reviewer',isActive:true,mustChangePassword:false,roles:['Admin'],
      permissions:mode==='All'?['security.manage','settings.manage','payroll.run']:['client.settings.manage','payroll.run'],
      clientId:mode==='LegacySingle'?99002:null,clientAccessMode:mode==='Selected'?'Selected':'Legacy',assignedClientIds:mode==='Selected'?[99001,99002]:[]}
    await context.addInitScript(()=>sessionStorage.setItem('payroll.auth.token','synthetic-fixture-token'))
    await page.route('**/api/**',async route=>{
      const request=route.request(),url=new URL(request.url()),p=url.pathname
      calls.push({method:request.method(),path:p})
      const headers={'access-control-allow-origin':new URL(base).origin,'access-control-allow-credentials':'true','access-control-allow-methods':'GET,HEAD,OPTIONS',
        'access-control-allow-headers':request.headers()['access-control-request-headers']||'authorization,content-type,x-client-id'}
      if(request.method()==='OPTIONS')return route.fulfill({status:204,headers})
      if(request.method()!=='GET'){blocked.push({method:request.method(),path:p});return route.fulfill({status:405,headers,contentType:'application/json',body:'{"error":"Synthetic UI fixture rejects writes"}'})}
      let data=[]
      if(p==='/api/auth/me')data=user
      else if(p==='/api/clients'){
        const ordinal=++clientReads
        // The first mount response must finish AFTER the user chooses client2.
        await new Promise(resolve=>setTimeout(resolve,ordinal===1?3500:20))
        if(ordinal===1)oldClientResponseFinished=true
        data=clients
      }else if(p==='/api/payroll/employee-options')data=employees
      else if(p==='/api/leave-attendance/groups')data=groups
      else if(p==='/api/setup')data={salaryComponents:[],salaryTemplates:[]}
      else if(p==='/api/leave-attendance/attendance/monthly'){
        const clientId=Number(url.searchParams.get('clientId'))
        data=employees.filter(e=>e.clientId===clientId).map(e=>({employeeId:e.id,clientId,month:url.searchParams.get('month'),workingDays:30,presentDays:30,payableDays:30}))
      }else if(p==='/api/leave-attendance/attendance/payroll-locations')data={clientId:Number(url.searchParams.get('clientId')),month:url.searchParams.get('month'),policyBatchId:url.searchParams.get('policyBatchId'),groupIds:[],version:'synthetic',storageAvailable:true,rows:[]}
      return route.fulfill({status:200,headers,contentType:'application/json',body:JSON.stringify(data)})
    })
    try{
      await page.goto(base+'/payroll/regular'+(mode==='Selected'?'?clientId=99002':''),{waitUntil:'domcontentloaded'})
      await page.getByRole('heading',{name:'Regular Run',exact:true}).waitFor({timeout:30000})
      if(mode==='All'){
        const label=page.locator('label').filter({hasText:/^Client/})
        await label.locator('.ant-select-selection-item').waitFor({timeout:30000})
        assert.ok(clientReads>=2,'The StrictMode mounted test must exercise two concurrent metadata requests')
        assert.equal(oldClientResponseFinished,false,'The stale response must still be outstanding at user selection')
        await label.locator('.ant-select-selector').click()
        await label.getByRole('combobox').fill('Synthetic second client')
        await page.locator('.ant-select-item-option[title="Synthetic second client"]').click()
        assert.equal(await label.locator('.ant-select-selection-item').innerText(),'Synthetic second client')
      }
      await page.waitForFunction(()=>document.body.innerText.includes('Synthetic Second'),{},{timeout:30000})
      await page.waitForTimeout(4000)
      assert.equal(oldClientResponseFinished,true)
      const policy=page.locator('label').filter({hasText:/^Attendance Policy/}).locator('.ant-select-selection-item')
      assert.match(await policy.innerText(),/Synthetic second policy/)
      assert.ok((await page.locator('body').innerText()).includes('Synthetic Second'))
      assert.ok(!(await page.locator('body').innerText()).includes('Synthetic First'))
      if(mode==='All')assert.equal(await page.locator('label').filter({hasText:/^Client/}).locator('.ant-select-selection-item').innerText(),'Synthetic second client')
      else assert.equal(await page.locator('label').filter({hasText:/^Client/}).count(),0)
      assert.equal(blocked.length,0)
      await page.screenshot({path:path.join(output,mode+'.png'),fullPage:true})
      results.push({mode,passed:true,concurrentInitialClientReads:clientReads,lateResponseFinished:oldClientResponseFinished,selectedPolicyRetained:true,correctRosterRetained:true,blockedWrites:blocked.length,realDatabaseRequests:0})
    }catch(error){
      await page.screenshot({path:path.join(output,mode+'-failure.png'),fullPage:true}).catch(()=>{})
      fs.writeFileSync(path.join(output,mode+'-failure.private.json'),JSON.stringify({message:error.message,calls,blocked},null,2))
      throw error
    }finally{await context.close()}
  }
  fs.writeFileSync(path.join(output,'results.json'),JSON.stringify(results,null,2))
  console.log(JSON.stringify({passed:results.length,total:3,realDatabaseRequests:0,realBusinessWrites:0,results}))
}finally{await browser.close()}
