// Synthetic CUA harness for the actual Master setup workspace. No real API,
// database, employee, payroll, file-mail or delivery endpoint can be reached.
import http from 'node:http'
import fs from 'node:fs'
import {createRequire} from 'node:module'
import assert from 'node:assert/strict'
import {masterWorkspaceFixture,reviewedQuickBasis,quickPreview} from './fixtures/excel-payroll-quick-prepare.mjs'

const require=createRequire(new URL('../payroll-ui/package.json',import.meta.url)),ts=require('typescript'),modules=new Map()
function load(name){
 if(modules.has(name))return modules.get(name)
 const exports={};modules.set(name,exports)
 new Function('exports','require',ts.transpileModule(fs.readFileSync(new URL(`../payroll-ui/src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,p=>load(p.replace('./','')))
 return exports
}
const scenarios=new Map(['fresh','saved','conflict','partial','applied'].map(name=>{
 const data=masterWorkspaceFixture(name),{batch,context}=data
 const proposal=load('excelPayrollSetup').proposeExcelPayrollSetup(batch,context,batch.rows.map(row=>row.id)).request
 if(!['fresh','partial'].includes(name))context.clientConfiguration=reviewedQuickBasis(proposal,context.source.columns)
 return [name,{...data,original:JSON.stringify({rows:batch.rows,source:context.source}),receipts:new Map(),calls:[],forbidden:0}]
}))
const port=Number(process.env.MASTER_FIXTURE_PORT||5191),uiOrigin=process.env.MASTER_FIXTURE_UI_ORIGIN||'http://localhost:5173'
const fixture=new URL('../payroll-ui/.codex-master-workspace-fixture.tsx',import.meta.url)

function previewFor(data,request){
 const {batch,context}=data,preview=quickPreview(request),issues=[]
 assert.equal(request.batchId,batch.id);assert.ok(request.rowIds.length>0&&request.rowIds.length<=100)
 assert.equal(new Set(request.rowIds).size,request.rowIds.length)
 assert.ok(request.rowIds.every(id=>batch.rows.some(row=>row.id===id)))
 for(const id of request.rowIds){
  const decision=request.employeeDecisions.find(item=>item.rowId===id),roster=context.roster.rows.find(row=>row.rowId===id)
  if(!decision||roster.status==='Review'&&!decision.matchedEmployeeId)issues.push({severity:'Error',code:'IdentityReview',rowId:id,message:'Choose the correct Employee Master record for this source employee.'})
  else if(!decision.matchedEmployeeId&&!decision.allowCreate)issues.push({severity:'Error',code:'CreationReview',rowId:id,message:'Review this new employee before creating an Employee Master record.'})
 }
 if(!request.workWeek)issues.push({severity:'Error',code:'WorkWeek',message:'Select a saved weekly-off rule; no weekly-off policy was inferred.'})
 if(!request.components.length)issues.push({severity:'Error',code:'Components',message:'Map at least one source salary component.'})
 const scope=request.templateScope
 if(!scope||!['CalendarDays','ContractFixedDays','WorkingDays'].includes(scope.divisorMeaning))
  issues.push({severity:'Error',code:'DivisorMeaning',message:'Choose what the source salary days mean: calendar days, fixed contract days or working days.'})
 if(!scope||scope.payableMeaning!=='AlreadyPayable'||!['SavedSourceRules','DatedPolicy'].includes(scope.policyBasis)||scope.policyBasis==='DatedPolicy'&&!scope.policyVersion?.trim())
  issues.push({severity:'Error',code:'SalaryBasis',message:'Review the saved source salary and payable-day basis.'})
 if(!scope||!['Listed','AllClientLocations'].includes(scope.locationMode)||scope.locationMode==='Listed'&&!scope.workLocationIds?.length&&!scope.newLocationNames?.length)
  issues.push({severity:'Error',code:'Locations',message:'Select the actual source work locations for this salary template.'})
 if(!scope||!scope.openEnded&&!/^\d{4}-\d{2}-\d{2}$/.test(scope.effectiveTo||''))
  issues.push({severity:'Error',code:'EffectivePeriod',message:'Review a finite effective period or explicitly keep a saved open-ended scope.'})
 if(!scope||scope.categoryKind!=='AllClientCategories'&&!scope.categories?.length)
  issues.push({severity:'Error',code:'SalaryCategory',message:'Choose which employee categories use this source salary template.'})
 if(request.attendanceReview&&(!['AlreadyPayable','WorkedOnly'].includes(request.attendanceReview.payableMeaning)||!['ActualWorked','NotMapped'].includes(request.attendanceReview.presentMeaning)))
  issues.push({severity:'Error',code:'AttendanceMeaning',message:'Choose the actual meanings of the source payable and present days.'})
 if(!request.reviewedFixedInputs||!request.reviewedPolicyScope||!request.templateScope?.reviewed||!request.attendanceReview?.reviewed||!request.templateDecisions?.length)
  issues.push({severity:'Error',code:'SetupReview',message:'Review this source-bound configuration before saving its Master setup.'})
 preview.canApply=!issues.length;preview.issues=issues
 preview.coverage='Synthetic source comparison only; not a payroll approval'
 preview.transfer.sourceMonth=batch.month;preview.transfer.sourceBatchId=batch.id
 preview.selectedSourceRows=request.rowIds.map(rowId=>({rowId,sourceRow:batch.rows.find(row=>row.id===rowId).sourceRow,sourceFound:true}))
 preview.transfer.preparationStatus=issues.length?'Blocked':'Ready'
 preview.transfer.parityStatus='All synthetic source amounts match; no production payroll was run'
 preview.transfer.components=request.components.map(component=>({code:component.code,category:component.category,action:component.existingComponentId?'Reuse':'Create',masterId:component.existingComponentId||''}))
 preview.transfer.formulas=request.rowIds.flatMap(rowId=>request.components.map(component=>({rowId,code:component.code,sourceRow:batch.rows.find(row=>row.id===rowId).sourceRow,sourceColumn:component.expectedColumnIndex??component.sourceColumnIndex??null,sourceHash:'a'.repeat(64),status:'Verified',reason:'Synthetic captured formula and source amount agree.'})))
 preview.transfer.attendanceProofs=request.rowIds.map(rowId=>({rowId,sourceRow:batch.rows.find(row=>row.id===rowId).sourceRow,mode:request.importAttendance===false?'VerifyExistingNative':'SourceSeed',status:'Verified',reason:'Synthetic actual-worked and payable columns are separate.',sourceHash:'a'.repeat(64),inputHash:'d'.repeat(64),payrollDays:30,presentDays:24,payableDays:24,sourceLwp:null,nativeHash:null,nativeInput:null}))
 preview.transfer.amountComparisons=request.rowIds.map(rowId=>({version:'1',rowId,sourceRow:batch.rows.find(row=>row.id===rowId).sourceRow,status:'Exact at raw precision',reason:'Source and calculated amounts match exactly in this synthetic fixture.',proofHash:'e'.repeat(64),acknowledged:false,sourceNetRaw:704,sourceNetDisplay:704,coreNet:704,rawNetDelta:0,displayNetDelta:0,lines:[]}))
 const reusable=!['fresh','partial'].includes(data.scenario)
 preview.transfer.templates[0]={...preview.transfer.templates[0],templateId:reusable?'template-1':'',name:'Research salary',action:reusable?'Reuse':'Create',candidates:reusable?[{id:'template-1',name:'Research salary',exact:true,configurationExact:true,differences:[],details:[],scopeStatus:'Exact reviewed scope'}]:[]}
 return preview
}
function resultFor(data,request,importId){
 const preview=previewFor(data,request),fresh=request.employeeDecisions.filter(item=>!item.matchedEmployeeId&&item.allowCreate).length
 return {importId,templateId:'template-1',attendanceGroupId:1,employeeIds:request.rowIds.map(id=>Number(id.split('-')[1])),createdCount:fresh,updatedCount:request.rowIds.length-fresh,replayed:false,transfer:preview.transfer,transferRows:request.rowIds.map(rowId=>({rowId,employeeId:Number(rowId.split('-')[1]),payrollDays:30,presentDays:24,payableDays:24,sourceNet:704,lines:[]}))}
}

{
 const data=scenarios.get('applied'),request=structuredClone(data.context.clientConfiguration.savedRequest)
 request.batchId=data.batch.id;request.rowIds=data.batch.rows.map(row=>row.id)
 request.attendanceReview={...request.attendanceReview,sourceMonth:data.batch.month,sourceHash:'a'.repeat(64),cycleStart:'2026-10-01',cycleEnd:'2026-10-31'}
 request.templateDecisions=[{variantKey:'rules',existingTemplateId:'template-1',createVariant:false}]
 const requestId='synthetic-already-applied',result=resultFor(data,request,'f'.repeat(32))
 data.receipts.set(requestId,{request,requestId,result})
}

fs.writeFileSync(fixture,`
import React,{useEffect,useState}from'react';import ReactDOM from'react-dom/client';import{ConfigProvider,Button,Space}from'antd';
import{BrowserRouter}from'react-router-dom';import theme from'/src/theme/hrmsTheme.ts';import AuthGate from'/src/components/AuthGate.tsx';import ToastProvider from'/src/components/ToastProvider.tsx';import Workspace from'/src/components/ExcelPayrollSetupWorkspace.tsx';
import'antd/dist/reset.css';import'/src/index.css';import'/src/OrganizationSetup.css';import'/src/ModuleDrawer.css';import'/src/AppShell.css';
function App(){const[data,setData]=useState(null),[open,setOpen]=useState(true),[saved,setSaved]=useState(0);const scenario=new URLSearchParams(location.search).get('scenario')||'fresh';useEffect(()=>{fetch('/fixture-data?scenario='+encodeURIComponent(scenario)).then(r=>r.json()).then(setData)},[scenario]);return<main style={{padding:16}}><h1>Master setup · synthetic review</h1><p>No production records or emails. Scenario: {scenario}.</p><Space wrap>{['fresh','saved','conflict','partial','applied'].map(name=><a key={name} href={'/master-fixture?scenario='+name}>{name}</a>)}<Button onClick={()=>setOpen(true)}>Open Master setup</Button></Space><p role="status">Confirmed synthetic saves: {saved}</p>{open&&data&&<Workspace key={data.batch.id} clientId={42} batch={data.batch} rowIds={data.batch.rows.map(row=>row.id)} onClose={()=>setOpen(false)} onApplied={()=>setSaved(n=>n+1)}/>}</main>}
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App/></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html=`<!doctype html><html><head><title>Master setup verification</title><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/.codex-master-workspace-fixture.tsx');</script></body></html>`
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,`http://127.0.0.1:${port}`),path=url.pathname
 const reply=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value))}
 try{
  if(path==='/fixture-data'){const data=scenarios.get(url.searchParams.get('scenario')||'fresh');return data?reply({batch:data.batch}):reply({error:'Unknown scenario'},400)}
  if(path==='/fixture-status')return reply([...scenarios.values()].map(data=>({scenario:data.scenario,calls:data.calls,receipts:data.receipts.size,sourceUnchanged:JSON.stringify({rows:data.batch.rows,source:data.context.source})===data.original,forbidden:data.forbidden,realDatabaseWrites:0,realEmails:0})))
  if(path==='/api/auth/me')return reply({id:1,email:'reviewer@example.test',displayName:'Synthetic reviewer',isActive:true,mustChangePassword:false,roles:['Admin'],permissions:['settings.manage','employees.manage','payroll.run']})
  if(path==='/api/excel-payroll-setup/context'){
   assert.equal(url.searchParams.get('clientId'),'42');const data=[...scenarios.values()].find(item=>item.batch.id===url.searchParams.get('batchId'));assert.ok(data,'Unknown synthetic batch')
   const context=structuredClone(data.context),ids=url.searchParams.get('rowIds')?.split(',')||data.batch.rows.map(row=>row.id)
   const saved=[...data.receipts.values()].find(item=>item.request.rowIds.slice().sort().join('|')===ids.slice().sort().join('|'))
   context.lastImport=saved?{importId:saved.result.importId,requestId:saved.requestId,status:'Applied',sourceChanged:false,savedRequest:saved.request,result:saved.result}:null
   context.appliedSetups=[...data.receipts.values()].map(item=>item.request.rowIds)
   context.roster.rows.forEach(row=>{if(context.appliedSetups.some(group=>group.includes(row.rowId)))row.status='Applied'})
   return reply(context)
  }
  if(path.startsWith('/api/excel-payroll-setup/')&&req.method==='POST'){
   assert.equal(url.searchParams.get('clientId'),'42');let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw),request=body.request||body,data=[...scenarios.values()].find(item=>item.batch.id===request.batchId)
   assert.ok(data,'Unknown synthetic batch');assert.equal(JSON.stringify({rows:data.batch.rows,source:data.context.source}),data.original,'Source values changed')
   const preview=previewFor(data,request)
   if(path.endsWith('/preview')){data.calls.push({kind:'preview',count:request.rowIds.length});return reply(preview)}
   if(path.endsWith('/draft')){data.calls.push({kind:'draft',count:request.rowIds.length});return reply({importId:'synthetic-draft',requestId:body.requestId,status:'Draft'})}
   if(path.endsWith('/apply')){
    assert.equal(preview.canApply,true,'Blocked synthetic review cannot be saved');assert.equal(body.planHash,'c'.repeat(64));assert.ok(body.requestId)
    const existing=data.receipts.get(body.requestId);if(existing)return reply({...existing.result,replayed:true})
    const result=resultFor(data,request,crypto.randomUUID().replaceAll('-',''));data.receipts.set(body.requestId,{request,requestId:body.requestId,result});data.calls.push({kind:'apply',count:request.rowIds.length,createdCount:result.createdCount,updatedCount:result.updatedCount});return reply(result)
   }
   data.forbidden++;return reply({error:'Unimplemented fixture write blocked'},403)
  }
  if(path.startsWith('/api/')){for(const data of scenarios.values())data.forbidden++;return reply({error:'Real API access blocked by synthetic Master setup fixture'},403)}
  if(path==='/master-responsive'){
   const width=Number(url.searchParams.get('width')),height=Number(url.searchParams.get('height'))
   assert.ok([390,768].includes(width));assert.ok([844,1024].includes(height))
   res.writeHead(200,{'Content-Type':'text/html'});return res.end(`<!doctype html><html><head><title>Responsive Master setup</title><style>body{margin:0;background:#f2f3f8}iframe{display:block;border:0;background:white}</style></head><body><iframe title="Master setup responsive preview" width="${width}" height="${height}" src="/master-fixture?scenario=conflict"></iframe></body></html>`)
  }
  if(path==='/master-fixture'){res.writeHead(200,{'Content-Type':'text/html'});return res.end(html)}
  if(path==='/src/services/apiBaseUrl.ts'){res.writeHead(200,{'Content-Type':'application/javascript'});return res.end('export function resolveRuntimeApiBase(){return window.location.origin}')}
  const upstream=await fetch(uiOrigin+req.url);res.writeHead(upstream.status,{'Content-Type':upstream.headers.get('content-type')||'text/plain'});res.end(Buffer.from(await upstream.arrayBuffer()))
 }catch(error){reply({error:error.message},500)}
})
server.listen(port,'127.0.0.1',()=>console.log(`Synthetic Master setup: http://127.0.0.1:${port}/master-fixture?scenario=fresh`))
const close=()=>{server.close();fs.rmSync(fixture,{force:true});process.exit()};process.on('SIGINT',close);process.on('SIGTERM',close)
