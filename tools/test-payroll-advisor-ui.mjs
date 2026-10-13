// Browser fixtures only: no real AI calls, employee records or database writes.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { createRequire } from 'node:module'
const require=createRequire(import.meta.url)
const {chromium,expect}=require(process.argv[2]||process.env.PLAYWRIGHT_MODULE||'@playwright/test')
const base=process.env.UI_URL||'http://localhost:5173',output='.codex-validation/payroll-advisor'
fs.mkdirSync(output,{recursive:true})
const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1440,height:1000}})
const errors=[],requests=[]
let queued=null,failed=false
page.on('pageerror',e=>errors.push(e.message))
const finding={id:'ALLOWANCE:double-proration',source:'Engine',severity:'Warning',code:'ALLOWANCE',field:'proRata',title:'Attendance may be applied twice',explanation:'This formula uses earned wages and applies attendance again.',example:'900 becomes 450 at 15/30 days.',suggestion:'Review the second adjustment.',canDisableProration:true}
await page.route('**/api/**',async route=>{
  const request=route.request(),path=new URL(request.url()).pathname
  const headers={'access-control-allow-origin':base,'access-control-allow-credentials':'true','access-control-allow-methods':'GET, POST, OPTIONS','access-control-allow-headers':'Content-Type, Authorization'}
  if(request.method()==='OPTIONS')return route.fulfill({status:204,headers})
  requests.push({path,method:request.method(),body:request.postDataJSON()})
  if(path.endsWith('/reviews')&&request.method()==='POST'){
    queued={id:'11111111-1111-1111-1111-111111111111',clientId:0,snapshotHash:'fixture',snapshot:request.postDataJSON().snapshot,status:'Queued',createdAt:new Date().toISOString(),report:{status:'NeedsReview',coverage:'No verified statutory rule pack is attached. Qualified review required.',findings:[finding],scenarios:[]}}
    return route.fulfill({json:queued,headers})
  }
  if(path.endsWith('/decisions'))return route.fulfill({json:{recorded:true,liveConfigurationChanged:false},headers})
  if(path.includes('/reviews/'))return route.fulfill({json:{...queued,status:failed?'Failed':'NeedsReview',error:failed?'AI provider unavailable. No configuration was changed.':null},headers})
  if(path.endsWith('/reviews'))return route.fulfill({json:queued?[queued]:[],headers})
  return route.fulfill({status:500,json:{error:'Unexpected API call'},headers})
})
await page.route(`${base}/__advisor-smoke`,route=>route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">
import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;
import '/node_modules/antd/dist/reset.css';import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';import theme from '/src/theme/hrmsTheme.ts';
const source=await(await fetch('/src/main.tsx')).text(),dep=n=>source.match(new RegExp('from "([^"\\n]*'+n+'[^"\\n]*)"'))[1];
const {default:React}=await import(dep('react.js')),{default:ReactDOM}=await import(dep('react-dom_client.js')),{ConfigProvider}=await import(dep('antd.js')),{default:Editor}=await import('/src/components/SalaryComponentEditor.tsx');
const original={id:2,code:'ALLOWANCE',name:'Allowance',category:'Earning',componentType:'Allowance',componentRole:'Regular Earning',statutoryType:'None',calculationType:'Formula',value:'',formula:'WAGES_EARNED*5%',baseComponent:'',payType:'Fixed Pay',taxable:true,ctc:true,proRata:true,fbp:false,restrictFbp:false,epf:'Never',esi:false,recurring:true,scheduled:false,investmentType:'',correctionOf:'',active:true,priority:'200'};
const wage={...original,id:1,code:'WAGES',name:'Wages',calculationType:'Fixed Amount',value:'18000',formula:'',priority:'100'};
window.saved=[];function Test(){const[value,setValue]=React.useState(original);window.draft=value;return React.createElement(Editor,{value,original,components:[wage,original],usedBy:['Test template'],saving:false,onChange:setValue,onSave:async()=>window.saved.push(value),onClose:()=>{}})}
ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(ConfigProvider,{theme},React.createElement(Test)));
</script></body></html>`}))
try{
  await page.goto(`${base}/__advisor-smoke`)
  await page.getByRole('button',{name:'AI review',exact:true}).click()
  const modal=page.getByRole('dialog').filter({hasText:'Payroll configuration review'})
  await expect(modal.getByRole('button',{name:'Run review',exact:true})).toBeDisabled()
  assert.equal(requests.length,0,'Opening advisor never starts an AI request')
  await modal.getByRole('checkbox').check()
  await modal.getByRole('button',{name:'Run review',exact:true}).click()
  await expect(modal.getByText('Review required',{exact:true})).toBeVisible({timeout:10000})
  assert.equal(await page.evaluate(()=>window.saved.length),0)
  assert.equal(await page.evaluate(()=>window.draft.proRata),true)
  assert.deepEqual(Object.keys(queued.snapshot.components[0]).sort(),['id','code','category','componentRole','statutoryType','calculationType','value','formula','baseComponent','proRata','taxable','active','priority','payType'].sort())
  await page.screenshot({path:output+'/desktop.png'})
  await modal.getByRole('button',{name:'Apply to draft',exact:true}).click()
  await page.getByRole('dialog',{name:'Remove the second attendance adjustment?'}).getByRole('button',{name:'Apply to draft',exact:true}).click()
  await expect(modal.getByText('Draft changed. This review is out of date.',{exact:true})).toBeVisible()
  assert.equal(await page.evaluate(()=>window.draft.proRata),false)
  assert.equal(await page.evaluate(()=>window.saved.length),0,'Applying suggestion affects draft only')
  await expect(modal.getByRole('button',{name:'Apply to draft',exact:true})).toBeDisabled()
  assert.equal(requests.filter(r=>r.path.endsWith('/decisions')).length,1)
  failed=true
  await modal.getByRole('button',{name:'Run new review',exact:true}).click()
  await expect(modal.getByText('AI provider unavailable. No configuration was changed.',{exact:true})).toBeVisible({timeout:10000})
  await page.setViewportSize({width:390,height:844})
  await expect.poll(async()=>{const b=await modal.boundingBox();return b&&b.x>=0&&b.x+b.width<=391}).toBeTruthy()
  await page.screenshot({path:output+'/mobile.png'})
  assert.equal(requests.some(r=>r.path==='/api/setup'),false,'No live configuration endpoint is called')
  assert.deepEqual(errors,[])
  console.log('PASS: opt-in AI review, queued polling, whitelisted payload, draft-only change, audit decision, stale review guard, provider failure, mobile layout')
}catch(e){await page.screenshot({path:output+'/failure.png'});console.error(errors,requests);throw e}finally{await browser.close()}
