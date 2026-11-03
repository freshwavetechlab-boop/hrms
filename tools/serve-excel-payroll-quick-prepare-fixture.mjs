// Synthetic HTTP fixture for CUA. Every API route is mocked; no real DB/payroll/mail calls.
import http from 'node:http'
import fs from 'node:fs'
import {createRequire} from 'node:module'
import assert from 'node:assert/strict'
import {quickPreparationFixture,reviewedQuickBasis,quickPreview} from './fixtures/excel-payroll-quick-prepare.mjs'
const require=createRequire(new URL('../payroll-ui/package.json',import.meta.url)),ts=require('typescript'),modules=new Map()
function load(name){if(modules.has(name))return modules.get(name);const exports={};modules.set(name,exports);new Function('exports','require',ts.transpileModule(fs.readFileSync(new URL(`../payroll-ui/src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,p=>load(p.replace('./','')));return exports}
const {batch,context}=quickPreparationFixture(105)
context.clientConfiguration=reviewedQuickBasis(load('excelPayrollSetup').proposeExcelPayrollSetup(batch,context,batch.rows.slice(0,100).map(r=>r.id)).request,context.source.columns)
const sourceBefore=JSON.stringify({rows:batch.rows,source:context.source}),receipts=new Map(),calls=[]
let mode='normal',forbidden=0
const fixture=new URL('../payroll-ui/.codex-quick-prepare-fixture.tsx',import.meta.url)
fs.writeFileSync(fixture,`
import React,{useEffect,useState} from 'react';import ReactDOM from 'react-dom/client';import {ConfigProvider,Button,Space} from 'antd';
import {BrowserRouter} from 'react-router-dom';import theme from '/src/theme/hrmsTheme.ts';import AuthGate from '/src/components/AuthGate.tsx';
import ToastProvider from '/src/components/ToastProvider.tsx';import Workspace from '/src/components/ExcelPayrollSetupWorkspace.tsx';
import '/src/index.css';import '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';import '/src/AppShell.css';
function App(){const [data,setData]=useState(null),[open,setOpen]=useState(true),[saved,setSaved]=useState(0);useEffect(()=>{fetch('/fixture-data').then(r=>r.json()).then(setData)},[]);return <main style={{padding:16}}><h1>Synthetic payroll preparation</h1><p>Test data only — no real records or emails</p><Space wrap><Button onClick={()=>setOpen(true)}>Open preparation</Button><Button onClick={()=>fetch('/fixture-mode/uncertain',{method:'POST'})}>Fail next save before commit</Button><Button onClick={()=>fetch('/fixture-mode/lost-response',{method:'POST'})}>Lose next save response after commit</Button></Space><Button onClick={async()=>{const r=await fetch("/fixture-reset",{method:"POST"});setData(await r.json());setSaved(0);setOpen(true)}}>Start fresh synthetic import</Button><p>Confirmed groups: {saved}</p>{open&&data&&<Workspace clientId={42} batch={data.batch} rowIds={data.batch.rows.map(r=>r.id)} onClose={()=>setOpen(false)} onApplied={()=>setSaved(n=>n+1)}/>}</main>}
const fixtureWindow=window as any;fixtureWindow.quickFixtureRoot??=ReactDOM.createRoot(document.getElementById('root'));fixtureWindow.quickFixtureRoot.render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App/></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);
`)
const html=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/.codex-quick-prepare-fixture.tsx');</script></body></html>`
const server=http.createServer(async(req,res)=>{
 const u=new URL(req.url,'http://127.0.0.1:5190'),p=u.pathname,reply=(data,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(data))}
 try {
  if(p==='/fixture-data')return reply({batch})
  if(p==='/fixture-status')return reply({calls,receipts:receipts.size,sourceUnchanged:JSON.stringify({rows:batch.rows,source:context.source})===sourceBefore,forbidden,realDatabaseWrites:0,realEmails:0})
  if(p==='/fixture-reset'){batch.id=crypto.randomUUID().replaceAll('-','');receipts.clear();calls.push({kind:'reset'});return reply({batch})}
  if(p.startsWith('/fixture-mode/')){mode=p.split('/').at(-1);return reply({mode})}
  if(p==='/api/auth/me')return reply({id:1,email:'reviewer@example.test',displayName:'Synthetic reviewer',isActive:true,mustChangePassword:false,roles:['Admin'],permissions:['settings.manage','employees.manage','payroll.run']})
  if(p==='/api/excel-payroll-setup/context'){
   assert.equal(u.searchParams.get('clientId'),'42');assert.equal(u.searchParams.get('batchId'),batch.id)
   const ids=u.searchParams.get('rowIds')?.split(',')||batch.rows.map(r=>r.id),ctx=structuredClone(context)
   const saved=[...receipts.values()].find(r=>r.request.rowIds.slice().sort().join('|')===ids.slice().sort().join('|'))
   ctx.appliedSetups=[...receipts.values()].map(r=>r.request.rowIds)
   ctx.lastImport=saved?{importId:saved.result.importId,requestId:saved.requestId,status:'Applied',sourceChanged:false,savedRequest:saved.request,result:saved.result}:null
   ctx.roster.rows.forEach(row=>{if(ctx.appliedSetups.some(ids=>ids.includes(row.rowId)))row.status='Applied'})
   return reply(ctx)
  }
  if(p==='/api/excel-payroll-setup/preview'||p==='/api/excel-payroll-setup/apply'){
   assert.equal(u.searchParams.get('clientId'),'42');let raw='';for await(const c of req)raw+=c;const input=JSON.parse(raw),q=input.request||input
   assert.ok(q.rowIds.length<=100);assert.equal(q.quickPreparation.sourceHash,'a'.repeat(64));assert.equal(q.importAttendance,true);assert.equal(q.presentDaysColumnIndex,4)
   if(p.endsWith('/preview')){calls.push({kind:'preview',count:q.rowIds.length});return reply(quickPreview(q))}
   assert.equal(input.planHash,'c'.repeat(64));assert.ok(q.templateDecisions.every(d=>!d.createVariant&&!d.updateExisting&&d.existingTemplateId==='template-1'))
   const existing=receipts.get(input.requestId);if(existing)return reply(existing.result)
   calls.push({kind:'apply',count:q.rowIds.length,requestId:input.requestId,mode})
   if(mode==='uncertain'){mode='normal';return reply({error:'Synthetic unconfirmed save. Check the receipt.'},503)}
   const result={importId:crypto.randomUUID().replaceAll('-',''),templateId:'template-1',attendanceGroupId:1,employeeIds:q.rowIds.map(id=>Number(id.split('-')[1])),createdCount:0,updatedCount:q.rowIds.length,replayed:false,transferRows:q.rowIds.map(id=>({rowId:id,employeeId:Number(id.split('-')[1]),payrollDays:30,presentDays:24,payableDays:24,sourceNet:704,lines:[]}))}
   receipts.set(input.requestId,{request:q,requestId:input.requestId,result})
   if(mode==='lost-response'){mode='normal';return reply({error:'Synthetic response lost after commit.'},503)}
   return reply(result)
  }
  if(p.startsWith('/api/')){forbidden++;return reply({error:'Real API access blocked by fixture'},403)}
  if(p==='/quick-fixture'){res.writeHead(200,{'Content-Type':'text/html'});return res.end(html)}
  if(p==='/src/services/apiBaseUrl.ts'){res.writeHead(200,{'Content-Type':'application/javascript'});return res.end('export function resolveRuntimeApiBase(){return window.location.origin}')}
  const upstream=await fetch('http://localhost:5173'+req.url);res.writeHead(upstream.status,{'Content-Type':upstream.headers.get('content-type')||'text/plain'});res.end(Buffer.from(await upstream.arrayBuffer()))
 } catch(e){reply({error:e.message},500)}
})
server.listen(5190,'127.0.0.1',()=>console.log('Synthetic quick preparation: http://127.0.0.1:5190/quick-fixture'))
const close=()=>{server.close();fs.rmSync(fixture,{force:true});process.exit()};process.on('SIGINT',close);process.on('SIGTERM',close)
