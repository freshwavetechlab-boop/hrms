// Isolated fixtures: this test never changes client or payroll data.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import { cors, catalogueFixture } from './payroll-calculation-fixtures.mjs'
const require = createRequire(import.meta.url)
const { chromium, expect } = require(process.argv[2] || process.env.PLAYWRIGHT_MODULE || '@playwright/test')
const base = process.env.UI_URL || 'http://localhost:5173'
const output = '.codex-validation/payroll-simulator'
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
const errors = []
page.on('pageerror', error => errors.push(error.message))
const calls=[]; let fail=false
await page.route('**/api/**', async route => {
  const req=route.request(),url=req.url()
  if(req.method()==='OPTIONS') return route.fulfill({status:204,headers:cors})
  calls.push({url,body:req.method()==='POST'?req.postDataJSON():null})
  if(url.endsWith('/catalogue')) return route.fulfill({headers:cors,contentType:'application/json',body:JSON.stringify(catalogueFixture(req.postDataJSON()))})
  if(url.includes('/employees?')) return route.fulfill({headers:cors,contentType:'application/json',body:JSON.stringify([{id:7,code:'EMP007',name:'Test employee'}])})
  if(url.endsWith('/simulate')) {
    if(fail) return route.fulfill({status:503,headers:cors,contentType:'application/json',body:JSON.stringify({error:'Simulation data is unavailable.'})})
    const b=req.postDataJSON(),days=b.payrollDays??30,payable=b.payableDays??14,present=b.presentDays??14
    const invalid=days<=0||payable>days||present>days||b.mode==='Template'&&(b.payableDays===null||b.presentDays===null)
    const gross=7193.2,deduction=863.18
    const body={mode:b.mode,templateId:'1',employee:b.mode==='Employee'?'EMP007 - Test employee':'',complete:!invalid,coverage:'Engine fixture: no records saved.',issues:invalid?[{severity:'Error',component:'Attendance',message:'Days are invalid or missing.',action:'Supply valid days.'}]:[],
      inputs:[{name:'PAYABLE_DAYS',value:b.payableDays,source:b.mode==='Employee'?'Recorded attendance':'What-if',unit:'Days'}],gross:invalid?null:gross,deductions:invalid?null:deduction,benefits:invalid?null:0,net:invalid?null:gross-deduction,
      lines:invalid?[]:[{code:'WAGE',name:'Wages',category:'Earning',formula:'15414',substituted:'15414',raw:15414,monthly:15414,earned:gross,proRata:true,source:'Component / template default',rounding:'Engine rounding',inputs:{}},{code:'PF',name:'PF',category:'Deduction',formula:'MIN(WAGE_EARNED,15000)*12%',substituted:'MIN(7193.2,15000)*12%',raw:863.184,monthly:863.18,earned:863.18,proRata:false,source:'Component / template default',rounding:'2 decimals',inputs:{WAGE_EARNED:7193.2}}]}
    return route.fulfill({headers:cors,contentType:'application/json',body:JSON.stringify(body)})
  }
  return route.fulfill({status:400,headers:cors,contentType:'application/json',body:'{}'})
})
await page.route(`${base}/__payroll-smoke**`, route => route.fulfill({ contentType: 'text/html', body: `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root" style="padding:16px"></div><script type="module">
import RefreshRuntime from '/@react-refresh'; RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => type => type; window.__vite_plugin_react_preamble_installed__ = true;
import '/node_modules/antd/dist/reset.css'; import '/src/index.css'; import '/src/OrganizationSetup.css'; import '/src/ModuleDrawer.css'; import '/src/SecurityDrawerCompact.css'; import '/src/AppShell.css';
import theme from '/src/theme/hrmsTheme.ts';
const source = await (await fetch('/src/main.tsx')).text();
const dependency = name => source.match(new RegExp('from "([^"\\n]*' + name + '[^"\\n]*)"'))[1];
const { default:React } = await import(dependency('react.js'));
const { default:ReactDOM } = await import(dependency('react-dom_client.js'));
const { ConfigProvider } = await import(dependency('antd.js'));
const { default: Designer } = await import('/src/components/SalaryTemplateDesigner.tsx');
const components = [
{ id:'1', code:'WAGE', name:'Monthly wages', category:'Earning', calculationType:'Fixed Amount', formula:'', value:'15414', active:true, priority:10, proRata:true, payType:'Fixed Pay' },
{ id:'2', code:'PF', name:'Employee PF', category:'Deduction', calculationType:'Formula', formula:'MIN(WAGE_EARNED,15000)*12%', value:'', active:true, priority:20, proRata:false, payType:'Fixed Pay' }
];
const template = { id:'1', name:'Regression template', clientId:'1', annualCtc:'184968', active:true, lines:[{componentId:'1'},{componentId:'2'}] };
if (new URLSearchParams(location.search).has('multi')) template.clientId='1,2';
if (new URLSearchParams(location.search).has('benefits')) { components.push({ ...components[1],id:'3',code:'ER_PF',name:'Employer PF',category:'Benefit',formula:'WAGE_EARNED*13%' },{ ...components[0],id:'4',code:'EXPENSE',name:'Expense reimbursement',category:'Reimbursement',value:'1000',proRata:false }); template.lines.push({componentId:'3'},{componentId:'4'}); }
if (new URLSearchParams(location.search).has('daily')) { components[0]={...components[0],name:'Daily wages',calculationType:'Daily Rate',value:'700',proRata:false};template.annualCtc='0'; }
function Test() { const [structure,setStructure] = React.useState(template); return React.createElement(Designer, { clients:[{id:1,name:'Test client'}], components, structure, setStructure, templates:[structure], saveTemplate:()=>{} }); }
ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(ConfigProvider,{theme},React.createElement(Test)));
</script></body></html>` }))
try {
  await page.goto(`${base}/__payroll-smoke`)
  await expect(page.getByRole('button',{name:'Save Template',exact:true})).toBeEnabled()
  await page.getByRole('button',{name:'Simulate',exact:true}).click()
  const dialog=page.getByRole('dialog')
  assert.equal(calls.filter(c=>c.url.endsWith('/simulate')).length,0,'Opening does not run a calculation')
  const preview=dialog.locator('.simulation-preview')
  await expect(preview.locator('tbody tr')).toHaveCount(2)
  await expect(preview).toContainText('Monthly wages')
  await expect(preview.locator('.simulation-totals')).toContainText('15,414.00')
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('14')
  await expect(preview.locator('tbody tr').first()).toContainText('7,193.20')
  await expect(preview.locator('tbody tr').last()).toContainText('863.18')
  assert.equal(calls.filter(c=>c.url.endsWith('/simulate')).length,0,'Days update immediately without an API request')
  const inputStyle=await dialog.getByLabel('Simulation Payable days',{exact:true}).evaluate(el=>({height:getComputedStyle(el).height,radius:getComputedStyle(el).borderRadius}))
  assert.deepEqual(inputStyle,{height:'36px',radius:'8px'})
  await page.screenshot({path:output+'/restored-desktop.png',animations:'disabled'})
  await page.setViewportSize({width:390,height:844})
  await expect(preview.locator('tbody tr')).toHaveCount(2)
  assert.equal(await dialog.evaluate(el=>el.scrollWidth<=el.clientWidth),true)
  await page.screenshot({path:output+'/restored-mobile.png',animations:'disabled'})
  await page.setViewportSize({width:1440,height:1000})
  await dialog.getByText('Additional inputs and payroll verification',{exact:true}).click()
  await dialog.getByLabel('Simulation Present days',{exact:true}).fill('12')
  await dialog.getByRole('button',{name:'Run salary simulation',exact:true}).click()
  await expect(dialog.getByText('Calculated by payroll engine',{exact:true})).toBeVisible()
  await expect(dialog.locator('.simulation-totals').last()).toContainText('7,193.20')
  await expect(dialog.locator('.simulation-totals').last()).toContainText('863.18')
  assert.equal(calls.at(-1).body.presentDays,12)
  await dialog.locator('.simulation-line-title').filter({hasText:'PF'}).click()
  await expect(dialog.locator('.simulation-trace')).toContainText('MIN(7193.2,15000)*12%')
  await page.screenshot({path:output+'/server-desktop.png'})
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('')
  await expect(dialog.getByText('Inputs changed. Run simulation again; the result below is from previous inputs.',{exact:true})).toBeVisible()
  await expect(dialog.getByRole('button',{name:'Run salary simulation',exact:true})).toBeDisabled()
  await expect(preview.getByText('Check simulation inputs',{exact:true}).first()).toBeVisible()
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('0')
  await dialog.getByRole('button',{name:'Run salary simulation',exact:true}).click()
  await expect(dialog.getByText('Calculated by payroll engine',{exact:true})).toBeVisible()
  assert.equal(calls.at(-1).body.payableDays,0,'Explicit zero survives transport')
  await expect(dialog.getByText('Employee / period',{exact:true})).toHaveCount(0)
  await expect(dialog.getByRole('combobox',{name:'Simulation employee',exact:true})).toHaveCount(0)
  assert.equal(calls.at(-1).body.mode,'Template')
  assert.equal(calls.at(-1).body.employeeId,undefined)
  assert.ok(calls.every(c=>!c.url.includes('/employees?')),'Template simulation does not load employees')
  await page.setViewportSize({width:390,height:844})
  const bounds=await dialog.boundingBox()
  assert.ok(bounds && bounds.x>=0 && bounds.x+bounds.width<=391,'Mobile dialog fits')
  assert.equal(await dialog.evaluate(el=>el.scrollWidth<=el.clientWidth),true,'No horizontal clipping')
  await page.screenshot({path:output+'/server-mobile.png'})
  fail=true
  await dialog.getByRole('button',{name:'Run salary simulation',exact:true}).click()
  await expect(dialog.getByText('Simulation data is unavailable.',{exact:true})).toBeVisible()
  await expect(dialog.locator('.simulation-totals')).toHaveCount(1)
  await expect(preview.locator('tbody tr')).toHaveCount(2)
  await page.setViewportSize({width:1440,height:1000})
  await dialog.locator('.ant-modal-footer').getByRole('button',{name:'Close',exact:true}).click()
  await page.locator('.salary-line input').last().fill('WAGE / 0')
  await expect(page.getByRole('button',{name:'Save Template',exact:true})).toBeDisabled()
  await page.goto(`${base}/__payroll-smoke?multi&benefits`)
  await page.getByRole('button',{name:'Simulate',exact:true}).click()
  await expect(preview.locator('tbody tr')).toHaveCount(4)
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('14')
  await expect(preview.locator('tbody tr').filter({hasText:'ER_PF'})).toContainText('935.12')
  await expect(preview.locator('.simulation-totals')).toContainText('8,193.20')
  await expect(preview.locator('.simulation-totals')).toContainText('7,330.02')
  await page.goto(`${base}/__payroll-smoke?daily`)
  await page.getByRole('button',{name:'Simulate',exact:true}).click()
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('22.5')
  await expect(preview.locator('tbody tr').first()).toContainText('700 x 22.5 payable days')
  await expect(preview.locator('tbody tr').first()).toContainText('15,750.00')
  await dialog.getByLabel('Simulation Payable days',{exact:true}).fill('0')
  await expect(preview.locator('.simulation-totals')).toContainText('Rs 0.00')
  assert.ok(calls.every(c=>c.url.includes('/payroll-calculation/')),'No save or payroll execution requests')
  assert.deepEqual(errors,[])
  console.log('PASS: template-only simulation, instant component table, day recalculation, multi-client, benefits, reimbursements, daily rates, zero/missing days, optional engine verification, theme controls and mobile layout')
} catch(error) {
  console.error('Browser errors:',errors)
  await page.screenshot({path:output+'/failure.png'})
  throw error
} finally { await browser.close() }
