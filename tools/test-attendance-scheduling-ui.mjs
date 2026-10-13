// Mocked API fixtures only; no client records are read or changed.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const { chromium, expect } = require(process.argv[2] || '@playwright/test')
const base = process.env.UI_URL || 'http://localhost:5173'
const output = '.codex-validation/attendance-scheduling'
fs.mkdirSync(output, { recursive: true })
const shifts = [1,2].map(id => ({ id,clientId:20,shiftCode:id===1?'FLEX':'DAY',shiftName:id===1?'Flexible hours':'Day shift',shiftType:id===1?'Flexible':'Fixed',startTime:id===1?null:'09:00:00',endTime:id===1?null:'20:00:00',isOvernight:false,graceMinutes:0,breakMinutes:0,minimumFullDayHours:8,minimumHalfDayHours:4,effectiveFrom:'2026-01-01',isActive:true,rules:{hoursMethod:'Pairs',breakMode:'None',maximumHours:24,segments:[]} }))
const cors = {'access-control-allow-origin':base,'access-control-allow-credentials':'true','access-control-allow-headers':'content-type,authorization','access-control-allow-methods':'GET,POST,DELETE,OPTIONS'}
const browser = await chromium.launch({headless:true})
const page = await browser.newPage({viewport:{width:1440,height:1000}})
const errors=[],writes=[]; let fail=false
page.on('pageerror',e=>errors.push(e.message))
await page.route('**/api/**',async route=>{
  const request=route.request(),url=request.url()
  if(request.method()==='OPTIONS') return route.fulfill({status:204,headers:cors})
  if(request.method()==='POST') {
    writes.push(request.postDataJSON())
    return route.fulfill({status:fail?400:200,headers:cors,contentType:'application/json',body:JSON.stringify(fail?{error:'An employee already has an assignment on these dates/weekdays.'}:{assigned:1})})
  }
  const data=url.includes('/shifts?')?shifts:url.includes('/employees?')?[{id:10,code:'TEST010',name:'Test Employee'}]:[]
  return route.fulfill({headers:cors,contentType:'application/json',body:JSON.stringify(data)})
})
await page.route(`${base}/__attendance-smoke**`,route=>route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root" class="settings-master-drawer" style="padding:16px;max-width:1100px;margin:auto"></div><script type="module">
import RefreshRuntime from '/@react-refresh'; RefreshRuntime.injectIntoGlobalHook(window); window.$RefreshReg$=()=>{}; window.$RefreshSig$=()=>type=>type; window.__vite_plugin_react_preamble_installed__=true;
import '/node_modules/antd/dist/reset.css'; import '/src/index.css'; import '/src/OrganizationSetup.css'; import '/src/ModuleDrawer.css'; import '/src/SecurityDrawerCompact.css'; import '/src/AppShell.css';
import theme from '/src/theme/hrmsTheme.ts';
const source=await(await fetch('/src/main.tsx')).text();
const dependency=name=>source.match(new RegExp('from "([^"\\n]*'+name+'[^"\\n]*)"'))[1];
const {default:React}=await import(dependency('react.js'));
const {default:ReactDOM}=await import(dependency('react-dom_client.js'));
const {ConfigProvider,Form,Tabs,Select}=await import(dependency('antd.js'));
const {default:Policy}=await import('/src/components/AttendancePolicyShiftFields.tsx');
const {default:Rules}=await import('/src/components/AttendanceShiftRulesFields.tsx');
const {default:Roster}=await import('/src/components/AttendanceRosterManager.tsx');
const shared=await import('/src/shared/attendanceShift.ts');window.shiftPreview=shared;
const shifts=${JSON.stringify(shifts)};
function Test(){const [policy,setPolicy]=React.useState({clientId:20,attendanceMode:'Punch',allowedShiftIds:[],shiftId:null});const [shift,setShift]=React.useState(shifts[0]);return React.createElement(Tabs,{items:[
{key:'policy',label:'Policy',children:React.createElement(Form,{layout:'vertical',className:'settings-quick-form'},React.createElement(Policy,{value:policy,onChange:p=>setPolicy(v=>({...v,...p})),allowLegacy:false}))},
{key:'shift',label:'Shift rules',children:React.createElement(Form,{layout:'vertical',className:'settings-quick-form'},React.createElement(Select,{'aria-label':'Test shift type',value:shift.id,options:shifts.map(s=>({value:s.id,label:s.shiftType})),onChange:id=>setShift(shifts.find(s=>s.id===id))}),React.createElement(Rules,{value:shift,onChange:rules=>setShift(v=>({...v,rules}))}))},
{key:'roster',label:'Roster',children:React.createElement(Roster,{clientId:20,shifts})}
]})}
ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(ConfigProvider,{theme},React.createElement(Test)));
</script></body></html>`}))
const choose=async(label,text)=>{await page.getByRole('combobox',{name:label,exact:true}).locator('xpath=ancestor::div[contains(@class,"ant-select-selector")]').click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden):visible .ant-select-item-option-content').filter({hasText:text}).last().click()}
try {
  await page.goto(`${base}/__attendance-smoke`)
  await choose('Allowed shifts','Flexible hours')
  await page.keyboard.press('Escape')
  await choose('Default policy shift','Flexible hours')
  await page.getByRole('button',{name:'Help: Allowed shifts',exact:true}).click()
  await expect(page.locator('.ant-popover:visible')).toContainText('does not assign multiple shifts')
  await page.getByRole('button',{name:'Help: Allowed shifts',exact:true}).click()
  assert.equal(writes.length,0)
  await page.screenshot({path:output+'/policy-desktop.png'})
  await choose('Attendance mode','Monthly attendance totals')
  await expect(page.getByRole('combobox',{name:'Allowed shifts',exact:true})).toHaveCount(0)
  await page.getByRole('tab',{name:'Shift rules',exact:true}).click()
  await expect(page.getByLabel('Attendance day starts at',{exact:true})).toBeVisible()
  await page.getByLabel('Attendance day starts at',{exact:true}).fill('04:00')
  await choose('Count worked hours','First IN to last OUT')
  await choose('Break deduction','Deduct fixed break minutes')
  await page.screenshot({path:output+'/flexible-desktop.png'})
  await choose('Test shift type','Fixed')
  await expect(page.getByLabel('Attendance day starts at',{exact:true})).toHaveCount(0)
  await page.getByRole('button',{name:/Add interval/}).click()
  await page.getByLabel('Interval 1 end',{exact:true}).fill('13:00')
  await page.getByRole('button',{name:/Add interval/}).click()
  await page.getByLabel('Interval 2 start',{exact:true}).fill('16:00')
  await page.screenshot({path:output+'/split-desktop.png'})
  await page.setViewportSize({width:390,height:844})
  assert.equal(await page.locator('#root').evaluate(e=>e.scrollWidth<=e.clientWidth),true,'Rules fit mobile')
  await page.screenshot({path:output+'/split-mobile.png',fullPage:true})
  await page.getByRole('tab',{name:'Roster',exact:true}).click()
  await choose('Roster shift','Flexible hours')
  await page.getByRole('button',{name:/Find employees/}).click()
  await expect(page.getByText('TEST010',{exact:true})).toBeVisible()
  await page.getByRole('row').filter({hasText:'TEST010'}).getByRole('checkbox').check()
  await page.getByRole('button',{name:'Assign (1)',exact:true}).click()
  await expect(page.getByText('Select shift, dates, weekdays and employees, and enter a reason.',{exact:true})).toBeVisible()
  assert.equal(writes.length,0)
  await page.getByLabel('Roster reason',{exact:true}).fill('October roster')
  await page.getByRole('button',{name:'Assign (1)',exact:true}).click()
  await expect(page.getByText('1 employee assignments saved.',{exact:true})).toBeVisible()
  assert.deepEqual(writes[0].employeeIds,[10]);assert.equal(writes[0].clientId,20)
  await page.getByRole('row').filter({hasText:'TEST010'}).getByRole('checkbox').check()
  fail=true
  await page.getByRole('button',{name:'Assign (1)',exact:true}).click()
  await expect(page.getByText('An employee already has an assignment on these dates/weekdays.',{exact:true})).toBeVisible()
  await choose('Roster shift','Day shift')
  await expect(page.getByRole('button',{name:/Assign \(0\)/})).toBeDisabled()
  assert.equal(await page.locator('#root').evaluate(e=>e.scrollWidth<=e.clientWidth),true,'Roster fits mobile')
  await page.screenshot({path:output+'/roster-mobile.png',fullPage:true})
  await page.setViewportSize({width:1440,height:1000})
  await page.screenshot({path:output+'/roster-desktop.png'})
  const preview=await page.evaluate(shifts=>{
    const context={settings:{clientId:20,maximumHoursAllowedForFullDay:24},shifts,shiftPolicies:[{clientId:20,employeeId:10,attendanceMode:'Punch',allowedShiftIds:[1],shiftId:1}]};
    const {calculateReviewShift}=window.shiftPreview;
    return [calculateReviewShift(context,10,'2026-10-12','13:00','21:00'),calculateReviewShift(context,10,'2026-10-12',null,null),calculateReviewShift({...context,shifts:[]},10,'2026-10-12','09:00','18:00')]
  },shifts)
  assert.equal(preview[0].hours,8);assert.equal(preview[0].payable,1);assert.equal(preview[1].payable,0);assert.equal(preview[2].payable,0)
  assert.deepEqual(errors,[])
  console.log('Attendance scheduling UI and preview checks passed; '+output)
} catch (error) { console.error(error); await page.screenshot({path:output+'/failure.png',fullPage:true}); console.error(await page.locator('body').innerText()); throw error } finally {await browser.close()}
