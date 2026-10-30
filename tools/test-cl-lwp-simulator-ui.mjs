import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import {createRequire} from 'node:module'
import {fileURLToPath,pathToFileURL} from 'node:url'

// Reuses tools/test-excel-payroll-bootstrap-ui.mjs's module discovery, Vite fixture and strict request mocking pattern.
const root=fileURLToPath(new URL('../',import.meta.url)),require=createRequire(new URL('../playwright-e2e/package.json',import.meta.url))
const {chromium,expect}=process.env.HRMS_PLAYWRIGHT_MODULE?await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href):require('@playwright/test')
const origin=process.env.HRMS_TEST_UI_URL||'http://localhost:5173'
if(!['localhost','127.0.0.1'].includes(new URL(origin).hostname))throw Error('Isolated local Vite fixture only.')
const fixture=`.codex-cl-lwp-${crypto.randomUUID()}.tsx`,fixturePath=path.join(root,'payroll-ui',fixture)
const output=path.join(os.tmpdir(),'hrms-cl-lwp-ui-'+Date.now())
fs.mkdirSync(output,{recursive:true})
fs.writeFileSync(fixturePath,`
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import {ConfigProvider} from 'antd';import theme from '/src/theme/hrmsTheme.ts';import AuthGate from '/src/components/AuthGate.tsx';
import ToastProvider from '/src/components/ToastProvider.tsx';import Header from '/src/components/layout/AppPageHeader.tsx';
import Simulator from '/src/components/ClLwpPolicySimulator.tsx';import '/src/index.css';import '/src/AppShell.css';
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><Header title="Synthetic CL/LWP policy" description="" icon={null} breadcrumbs={[]} workspace="attendance-settings"/><Simulator clientId={42}/></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const salarySnapshot={id:'17',snapshotHash:'a'.repeat(64),snapshot:{mode:'Template',clientId:42,focusCode:'',annualCtc:0,components:[{id:'1',code:'WAGES',category:'Earning',calculationType:'Fixed Amount',value:'30000',formula:'',active:true}],lines:[{componentId:'1'}]}}
let browser,testPage,apiWrites=0,simulationCalls=0,templateChanged=false
const errors=[],checks=[],consoleErrors=[]
try{
 browser=await chromium.launch({headless:process.env.HRMS_TEST_HEADLESS!=='0'})
 const context=await browser.newContext({viewport:{width:1366,height:900},serviceWorkers:'block'}),page=await context.newPage()
 testPage=page
 page.on('pageerror',e=>errors.push(e.message))
 page.on('console',m=>{if(m.type()==='error')consoleErrors.push(m.text().replace(/([?&](?:token|access_token)=)[^\\s'\"&]+/gi,'$1[redacted]'))})
 await context.routeWebSocket('**/*',ws=>ws.close())
 await context.route('**/*',async route=>{
  const req=route.request(),url=new URL(req.url())
  if(!['localhost','127.0.0.1'].includes(url.hostname))return route.abort()
  if(url.pathname==='/cl-lwp-fixture')return route.fulfill({contentType:'text/html',body:html})
  if(url.pathname==='/@vite/client')return route.fulfill({contentType:'application/javascript',body:'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'});
  if(url.pathname.startsWith('/api/')){
   if(url.pathname==='/api/payroll-calculation/template-snapshots'&&req.method()==='GET'){
    assert.equal(url.searchParams.get('clientId'),'42')
    return route.fulfill({contentType:'application/json',body:JSON.stringify([{...salarySnapshot,snapshotHash:templateChanged?'b'.repeat(64):salarySnapshot.snapshotHash}])})
   }
   if(url.pathname==='/api/payroll-calculation/simulate'&&req.method()==='POST'){
    const body=req.postDataJSON();assert.equal(body.mode,'Template');assert.equal(body.clientId,42);assert.equal(body.templateId,'17');assert.equal(body.snapshot.clientId,42)
    assert.ok([26,28].includes(body.payableDays));assert.equal(body.presentDays,20);assert.equal(body.payrollDays,30);simulationCalls++
    const gross=body.payableDays===26?26000:28000,deductions=body.payableDays===26?3120:3360,benefits=body.payableDays===26?260:280
    return route.fulfill({contentType:'application/json',body:JSON.stringify({complete:true,coverage:'Synthetic mocked Template response; not a live payroll calculation',issues:[],gross,deductions,benefits,net:gross-deductions,lines:[{code:'WAGES',category:'Earning',formula:'WAGES_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS',earned:gross,proRata:true,inputs:{PAYABLE_DAYS:body.payableDays}},{code:'PF',category:'Deduction',formula:'WAGES_EARNED*12%',earned:deductions,proRata:false,inputs:{}},{code:'ER',category:'Benefit',formula:'WAGES_EARNED*1%',earned:benefits,proRata:false,inputs:{}}]})})
   }
   if(req.method()!=='GET'){apiWrites++;return route.fulfill({status:403,body:'{}'})}
   if(url.pathname==='/api/auth/me')return route.fulfill({contentType:'application/json',body:JSON.stringify({id:1,clientId:42,displayName:'Synthetic reviewer',roles:['client_admin'],permissions:['settings.manage'],isActive:true})})
   return route.fulfill({status:403,body:'{}'})
  }
  const staticAsset=req.method()==='GET'&&url.origin===new URL(origin).origin&&(url.pathname==='/'+fixture||['/src/','/node_modules/','/@','/assets/'].some(p=>url.pathname.startsWith(p))||url.pathname==='/favicon.ico')
  if(staticAsset)return route.continue()
  errors.push('Blocked unexpected non-API resource '+req.method()+' '+url.pathname);return route.abort()
 })
 const choose=async(label,value)=>{await page.locator('.ant-select').filter({has:page.getByRole('combobox',{name:label,exact:true})}).click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({hasText:value}).click()}
 const number=async(label,value)=>page.getByRole('spinbutton',{name:label,exact:true}).fill(String(value))
 await page.goto(origin+'/cl-lwp-fixture');await page.getByRole('button',{name:'CL / LWP simulator',exact:true}).click()
 await expect(page.getByRole('button',{name:'Activate policy — approval required'})).toBeDisabled()
 await choose('Attendance meaning','Worked');await expect(page.getByRole('spinbutton',{name:'Proposed CL limit',exact:true})).toHaveValue('')
 for(const [label,value]of[['Proposed effective date','2026-11-01'],['Cycle start','2026-11-01'],['Cycle end','2026-11-30']])await page.getByLabel(label,{exact:true}).fill(value)
 await choose('Join/exit eligibility','Full cycle');await choose('Input evidence state','Assumed')
 for(const [label,value]of[['Actual worked days',20],['Salary divisor',30],['Eligible paid weekly-off days (non-overlapping)',4],['Eligible paid holiday days (non-overlapping)',0],['Proposed CL limit',4],['Already used CL in limit period',0],['Available CL balance for scenario',4],['Requested paid CL allocation',4]])await number(label,value)
 await choose('CL limit period','Annual');await choose('CL approval assumption','Assume approved');await choose('Remaining absence treatment','LWP')
 const result=page.locator('section[aria-label="CL LWP comparison result"]')
 await expect(result).toContainText('Comparison only — policy approval pending');await expect(result).toContainText('Unknown → 28');checks.push('No default allowance; explicit provisional CL/LWP scenario reaches 28 payable days')
 await page.getByText('Source Excel comparison (original formulas/values remain unchanged)',{exact:true}).click()
 await number('Source payable days',26);await number('Source LWP days',4);await number('Source divisor',30)
 const impact=page.locator('section[aria-label="CL LWP salary amount impact"]')
 await impact.getByRole('button',{name:'Load saved client salary snapshots',exact:true}).click()
 await choose('CL LWP salary template snapshot','Template 17 · '+salarySnapshot.snapshotHash.slice(0,12))
 await number('Salary impact source actual present days',20);await number('Salary impact annual CTC',0)
 await impact.getByRole('checkbox',{name:/I reviewed this template snapshot/}).check()
 const compare=impact.getByRole('button',{name:'Compare salary amounts using main engine',exact:true})
 await compare.dblclick();await expect(impact).toContainText('1760.00');checks.push('Synthetic Template source/scenario requests show component, employer and net deltas without payroll writes')
 await number('Requested paid CL allocation',2);await expect(impact).toContainText('Previous results are stale')
 await number('Requested paid CL allocation',4);templateChanged=true
 await compare.click();await expect(impact).toContainText('salary template changed');templateChanged=false
 checks.push('Changed scenario and saved-template hash force re-review/re-simulation')
 await page.getByRole('button',{name:'Save browser draft',exact:true}).click();await expect(page.getByText(/Browser draft v1 saved/)).toBeVisible()
 await page.getByRole('button',{name:'Save browser draft',exact:true}).click();await expect(page.getByText(/Browser draft v1 saved/)).toBeVisible();checks.push('Repeated browser draft save is idempotent')
 await page.getByRole('button',{name:'Close',exact:true}).last().click();await page.getByRole('button',{name:'CL / LWP simulator',exact:true}).click();await expect(page.getByRole('spinbutton',{name:'Actual worked days',exact:true})).toHaveValue(/^20(?:\.0+)?$/);checks.push('Cancel/reopen restores only the explicitly saved browser draft')
 await number('Available CL balance for scenario',2);await expect(result).toContainText('26 → 26');await expect(result).toContainText('limited by available balance');checks.push('Insufficient balance changes scenario with an explicit reason')
 await choose('Attendance meaning','Already payable');await number('Entered already-payable days',30);await number('Included CL annotation (never added again)',4)
 await page.getByRole('checkbox',{name:/No additional CL is added/}).check();await expect(result).toContainText('26 → 30');await expect(result).toContainText('not added again');checks.push('CL embedded in payable attendance is not credited twice')
 await page.evaluate(()=>{const k='hrms:cl-lwp:proposal:v1:1:42';const draft=JSON.parse(localStorage.getItem(k));draft.revision=9;localStorage.setItem(k,JSON.stringify(draft))})
 await page.getByRole('button',{name:'Save browser draft',exact:true}).click();await expect(page.getByText(/draft changed in another view/)).toBeVisible();checks.push('Stale revision refuses overwrite')
 for(const [name,viewport]of[['desktop',{width:1366,height:900}],['mobile',{width:390,height:844}]]){
  await page.setViewportSize(viewport);await expect(page.getByRole('button',{name:'Save browser draft',exact:true})).toBeVisible()
  await expect.poll(async()=>{const box=await page.locator('.cl-lwp-policy-simulator').boundingBox();return box?.width??Infinity},{timeout:5000}).toBeLessThanOrEqual(viewport.width+1)
  await page.screenshot({path:path.join(output,name+'.png'),fullPage:true});checks.push(name+' layout screenshot captured')
 }
 assert.deepEqual(errors,[]);assert.equal(apiWrites,0)
 console.log(JSON.stringify({passed:checks.length,checks,screenshots:output,consoleErrors,apiWrites,simulationCalls,mockedSimulationOnly:true,realPolicyWrites:0},null,2))
}catch(error){if(testPage){await testPage.screenshot({path:path.join(output,'failure.png'),fullPage:true});console.error(JSON.stringify(await testPage.locator('.cl-lwp-policy-simulator').evaluateAll(nodes=>nodes.map(el=>{const items=[];let n=el;for(let i=0;n&&i<4;i++,n=n.parentElement){const r=n.getBoundingClientRect(),s=getComputedStyle(n);items.push({className:n.className,width:r.width,left:r.left,cssWidth:s.width,min:s.minWidth,max:s.maxWidth,style:n.getAttribute('style')})}return {viewport:innerWidth,documentWidth:document.documentElement.scrollWidth,items}}))))}fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({errors,consoleErrors,error:error.message},null,2));console.error('Synthetic CL/LWP failure artifacts: '+output);console.error(error.message);throw error}finally{if(browser)await browser.close();if(fs.existsSync(fixturePath))fs.unlinkSync(fixturePath)}
