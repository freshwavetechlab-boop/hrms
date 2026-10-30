import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repo = fileURLToPath(new URL('..', import.meta.url))
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE || path.join(repo, 'tools/dev-launcher/node_modules/playwright/test.mjs')).href)
const origin = 'http://localhost:5173', output = fs.mkdtempSync(path.join(os.tmpdir(), 'frevo-excel-what-if-'))
const fixture = `.codex-excel-what-if-${crypto.randomUUID()}.tsx`, fixturePath = path.join(repo, 'payroll-ui', fixture)
const component = (id, code, category, formula = '') => ({ id, code, category, componentRole: category === 'Deduction' ? 'Regular Deduction' : 'Regular Earning', statutoryType: 'None', calculationType: formula ? 'Formula' : 'Fixed Amount', value: '0', formula, baseComponent: '', proRata: !formula && code !== 'TA_DA', taxable: true, active: true, priority: Number(id) * 10, payType: 'Fixed Pay' })
const components = [component('1','BASIC','Earning'),component('2','HRA','Earning'),component('3','PF','Deduction','BASIC_EARNED*12%'),component('4','TA_DA','Reimbursement')]
const snapshot = { mode: 'Template', clientId: 33, focusCode: '', annualCtc: 300000, components, lines: components.map(c => ({ componentId: c.id, value: c.value, formula: c.formula, calculationType: c.calculationType, baseComponent: '', proRataOverride: String(c.proRata) })) }
const scenario = { mode: 'Template', clientId: 33, templateId: 'excel-what-if', snapshot, payrollDays: 30, presentDays: 30, payableDays: 30, annualCtc: 300000, amountOverrides: { '1': 20000, '2': 5000, '4': 1000 } }
const row = { rowId:'synthetic',employeeCode:'SYN001',employeeName:'Synthetic employee',action:'Review',payrollDays:30,presentDays:30,payableDays:30,annualCtc:300000,lines:[],whatIfSimulation:scenario }
fs.writeFileSync(fixturePath, `import React from 'react';import ReactDOM from 'react-dom/client';import WhatIf from '/src/components/ExcelPayrollWhatIfSimulation.tsx';import PayrollSimulation from '/src/components/PayrollSimulation.tsx';
const row=${JSON.stringify(row)};function freeze(o){if(o&&typeof o==='object'){Object.freeze(o);Object.values(o).forEach(freeze)}return o};freeze(row);const before=JSON.stringify(row);window.__unchanged=()=>JSON.stringify(row)===before;
const mode=new URLSearchParams(location.search).get('mode');ReactDOM.createRoot(document.getElementById('root')).render(mode==='default'?<PayrollSimulation template={{id:1,clientId:'33',name:'Existing Settings template',annualCtc:'300000',active:true,lines:[{componentId:'1',value:'20000'}]}} components={[{...row.whatIfSimulation.snapshot.components[0],id:1,name:'Basic',priority:'10'}]} clients={[]} fixedClientId={33} onClose={()=>{}}/>:<WhatIf clientId={mode==='wrong-client'?34:33} row={mode==='old-api'?{...row,whatIfSimulation:undefined}:row}/>);`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial}#root{height:100dvh}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const viteClient = 'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'
const browser = await chromium.launch({ headless:true }), checks=[], requests=[], unexpected=[], errors=[]
let fail=false
let activePage
try {
  const context=await browser.newContext({viewport:{width:1366,height:768},serviceWorkers:'block'}),page=await context.newPage()
  activePage=page
  page.on('pageerror',e=>errors.push(e.message))
  await context.routeWebSocket('**/*',socket=>socket.close())
  await context.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url()),p=url.pathname
    if(p.startsWith('/api/')) {
      if(p==='/api/payroll-calculation/simulate' && req.method()==='POST') {
        const body=req.postDataJSON();requests.push(body)
        assert.equal(body.mode,'Template');assert.equal(body.clientId,33);assert.equal(body.employeeId,undefined)
        assert.deepEqual(body.snapshot,snapshot);assert.equal(body.payPeriod,'')
        if(fail)return route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({error:'Synthetic core unavailable; no payroll saved'})})
        return route.fulfill({contentType:'application/json',body:JSON.stringify({mode:'Template',templateId:'',employee:'',complete:true,coverage:'Synthetic API response, not live payroll parity',issues:[],inputs:[],lines:[],gross:100,net:90,deductions:10,benefits:0})})
      }
      unexpected.push(req.method()+' '+p);return route.abort('blockedbyclient')
    }
    if(url.origin!==origin){unexpected.push(url.origin+p);return route.abort('blockedbyclient')}
    if(p==='/what-if-fixture')return route.fulfill({contentType:'text/html',body:html})
    if(p==='/@vite/client')return route.fulfill({contentType:'application/javascript',body:viteClient})
    if(p==='/favicon.ico')return route.fulfill({status:204,body:''})
    if(req.method()==='GET'&&(p==='/'+fixture||['/src/','/node_modules/','/@','/assets/'].some(prefix=>p.startsWith(prefix))))return route.continue()
    unexpected.push(req.method()+' '+p);return route.abort('blockedbyclient')
  })
  await page.goto(origin+'/what-if-fixture')
  await page.getByRole('button',{name:'Try CTC & attendance',exact:true}).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(page.getByLabel('Simulation Annual CTC',{exact:true})).toHaveValue('300000')
  await expect(page.getByLabel('Source scenario amount BASIC',{exact:true})).toHaveValue('20000')
  await expect(page.getByLabel('Source scenario amount TA_DA',{exact:true})).toHaveValue('1000')
  const run=page.getByRole('button',{name:'Run source salary simulation',exact:true})
  await page.getByLabel('Simulation Annual CTC',{exact:true}).fill('600000')
  await page.getByLabel('Simulation Payable days',{exact:true}).fill('15')
  await page.getByLabel('Simulation Present days',{exact:true}).fill('14')
  await run.click();await expect.poll(()=>requests.length).toBe(1)
  assert.equal(requests[0].annualCtc,600000);assert.equal(requests[0].payableDays,15);assert.equal(requests[0].presentDays,14)
  assert.deepEqual(requests[0].amountOverrides,scenario.amountOverrides)
  await expect(page.getByText('Calculated by payroll engine',{exact:true})).toBeVisible()
  checks.push('Seeded exact server snapshot; CTC/days editable; CTC does not silently rescale fixed monthly inputs')
  await page.getByLabel('Source scenario amount BASIC',{exact:true}).fill('25000')
  await expect(page.getByText('Inputs changed. Run simulation again; the result below is from previous inputs.',{exact:true})).toBeVisible()
  await run.click();await expect.poll(()=>requests.length).toBe(2);assert.equal(requests[1].amountOverrides['1'],25000)
  await page.getByLabel('Simulation Payable days',{exact:true}).fill('31');await expect(run).toBeDisabled()
  await page.getByLabel('Simulation Payable days',{exact:true}).fill('0');await page.getByLabel('Simulation Present days',{exact:true}).fill('0')
  await page.getByLabel('Source scenario amount TA_DA',{exact:true}).fill('0');await run.click();await expect.poll(()=>requests.length).toBe(3)
  assert.equal(requests[2].payableDays,0);assert.equal(requests[2].amountOverrides['4'],0)
  assert.equal(await page.evaluate(()=>window.__unchanged()),true)
  checks.push('Monthly inputs and explicit zero are retained; invalid attendance blocked; prior results become stale; source immutable')
  for(const [width,height] of [[1366,768],[768,1024],[390,844]]) {
    await page.setViewportSize({width,height})
    await expect.poll(async()=>page.locator('.payroll-simulation .ant-modal-footer').evaluate(el=>{const r=el.getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight})).toBe(true)
    assert.ok(await page.locator('.payroll-simulation .ant-modal-body').evaluate(el=>el.clientHeight>0&&el.scrollHeight>=el.clientHeight))
    await page.screenshot({path:path.join(output,`scenario-${width}.png`)})
  }
  fail=true;await run.click();await expect.poll(()=>requests.length).toBe(4)
  await expect(page.getByText('Synthetic core unavailable; no payroll saved',{exact:true})).toBeVisible()
  await expect(page.getByText('Calculated by payroll engine',{exact:true})).toHaveCount(0)
  checks.push('Desktop/tablet/mobile contained; API failure cannot retain an apparent verified result')
  for(const mode of ['old-api','wrong-client']){
    await page.goto(origin+'/what-if-fixture?mode='+mode)
    await expect(page.getByRole('button',{name:'Try CTC & attendance',exact:true})).toBeDisabled()
  }
  await page.goto(origin+'/what-if-fixture?mode=default')
  await expect(page.getByRole('dialog')).toContainText('Existing Settings template')
  await expect(page.getByRole('region',{name:'Source scenario amounts'})).toHaveCount(0)
  await page.getByText('Additional inputs and payroll verification',{exact:true}).click()
  await expect(page.getByLabel('Simulation Annual CTC',{exact:true})).toHaveValue('300000')
  await expect(page.getByLabel('Simulation Present days',{exact:true})).toHaveValue('')
  checks.push('Old API and wrong-client scenario blocked; existing Settings simulator defaults preserved')
  assert.deepEqual(unexpected,[]);assert.deepEqual(errors,[])
  await context.close()
  fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:true,checks,simulationRequests:requests.length,actualBusinessWrites:0,unexpected,errors},null,2))
  console.log(JSON.stringify({passed:true,groups:checks.length,actualBusinessWrites:0,output}))
} catch(error) {
  if(activePage && !activePage.isClosed()) {
    await activePage.screenshot({path:path.join(output,'failure.png')}).catch(()=>{})
    fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({error:String(error),errors,unexpected,requests,text:await activePage.locator('body').innerText().catch(()=>''),buttons:await activePage.getByRole('button').allTextContents().catch(()=>[])},null,2))
  }
  console.error('Fixture diagnostics: '+output)
  throw error
} finally {await browser.close();fs.rmSync(fixturePath,{force:true})}
