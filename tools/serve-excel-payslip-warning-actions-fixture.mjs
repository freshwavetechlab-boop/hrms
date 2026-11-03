// Isolated synthetic UI fixture. Start with node, interact through CUA on port 5188.
// All API requests are handled here; no production API, database, payroll or mail calls.
import http from 'node:http'
import fs from 'node:fs'
import assert from 'node:assert/strict'
const fixture = new URL('../payroll-ui/.codex-warning-actions-fixture.tsx', import.meta.url)
const grossWarning = 'Mapped earnings do not match the source gross total. Source amounts are preserved.'
const netWarning = 'Mapped earnings less deductions do not match source net pay. Source net pay is preserved.'
const rows = Array.from({length: 16}, (_,i) => ({ id:`row-${i}`, sourceRow:i+2, employeeName:`Synthetic employee ${i+1}`, employeeCode:`TEST-${i+1}`, email:'', information:[],
  earnings:[{label:'Basic',amount:1200},{label:'Additional allowance (C)',amount:60}], deductions:[{label:'Deduction (B)',amount:100}], employerContributions:[],
  declaredGross:1200,declaredDeductions:100,netPay:i===1?1100:1160.000000000019,warnings:i===1?[grossWarning,netWarning]:[grossWarning],warningReviewToken:String(i+1).padStart(64,'0') }))
const batch = {id:'1234567890abcdef1234567890abcdef',clientId:42,clientName:'Synthetic',month:'2026-10',sourceFileName:'Synthetic.xlsx',rows}
const financials = () => JSON.stringify(rows.map(({warningReview,warningReviewToken,warnings,...source})=>source))
const before = financials(); let failNext=false, forbidden=0; const requests=[]
fs.writeFileSync(fixture, `
import React,{useEffect,useState} from 'react';import ReactDOM from 'react-dom/client';
import {Button,ConfigProvider,Space,Alert} from 'antd';import theme from '/src/theme/hrmsTheme.ts';
import {ExcelPayslipWarnings} from '/src/components/ExcelPayslipReview.tsx';import {reviewExcelPayslipWarnings} from '/src/services/excelPayslipService.ts';
import '/src/index.css';import '/src/components/ExcelPayslips.css';
function App(){const [batch,setBatch]=useState(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[open,setOpen]=useState(true),[canManage,setCanManage]=useState(!location.search.includes('viewer'));
useEffect(()=>{fetch('/fixture-snapshot').then(r=>r.json()).then(setBatch)},[]);
const clear=async(rows,action)=>{setBusy(true);setError('');try{const result=await reviewExcelPayslipWarnings(42,batch.id,rows.map(r=>({rowId:r.id,token:r.warningReviewToken})),action);if(!result.ok)throw Error(result.error);setBatch(result.data)}catch(e){setError(e.message)}finally{setBusy(false)}};
return <ConfigProvider theme={theme}><main style={{padding:16}}><h1>Synthetic warning review verification</h1><Alert message="Test data only — no real payroll or emails"/><Space wrap><Button onClick={()=>setOpen(true)}>Open warnings</Button><Button onClick={()=>{fetch('/fixture-fail-next',{method:'POST'});setError('')}}>Simulate failed save</Button><Button onClick={()=>setCanManage(v=>!v)}>Toggle viewer</Button></Space>{batch&&<><p>Active warning rows: {batch.rows.filter(r=>r.warnings.length).length}</p><ExcelPayslipWarnings rows={batch.rows} open={open} busy={busy} canManage={canManage} error={error} onClose={()=>setOpen(false)} onReview={()=>{}} onCorrect={()=>{}} onMapping={()=>{}} onClearWarnings={clear}/></>}</main></ConfigProvider>}
const fixtureWindow=window as any;fixtureWindow.warningFixtureRoot??=ReactDOM.createRoot(document.getElementById('root'));fixtureWindow.warningFixtureRoot.render(<App/>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/.codex-warning-actions-fixture.tsx');</script></body></html>`
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://127.0.0.1:5188'),p=url.pathname
 const reply=(data,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(data))}
 try{
  if(p==='/fixture-snapshot')return reply(batch)
  if(p==='/fixture-status')return reply({requests,forbidden,financialsPreserved:financials()===before,realDatabaseWrites:0,realEmails:0})
  if(p==='/fixture-fail-next'){failNext=true;return reply({ok:true})}
  if(p.startsWith('/api/')){
   if(req.method!=='POST'||p!==`/api/excel-payslips/batches/${batch.id}/warning-review`){forbidden++;return reply({error:'API access outside fixture is blocked'},403)}
   let raw='';for await(const chunk of req)raw+=chunk
   const input=JSON.parse(raw);assert.equal(url.searchParams.get('clientId'),'42');assert.ok(['review','restore'].includes(input.action));assert.ok(input.rows.length>0)
   assert.equal(new Set(input.rows.map(r=>r.rowId)).size,input.rows.length)
   const selected=input.rows.map(s=>{const row=rows.find(r=>r.id===s.rowId);assert.equal(s.token,row?.warningReviewToken);return row})
   requests.push({action:input.action,count:selected.length,failed:failNext})
   if(failNext){failNext=false;return reply({error:'Synthetic save failed; warnings are unchanged. Retry.'},503)}
   for(const row of selected){if(input.action==='review'){row.warningReview??={amountsHash:'synthetic',reviewedBy:'reviewer@example.test',reviewedAtUtc:'2026-11-02T12:00:00Z',warnings:[...row.warnings]};row.warnings=[]}else{row.warnings=row.warningReview?.warnings||row.warnings;delete row.warningReview}}
   assert.equal(financials(),before);return reply(batch)
  }
  if(p==='/warnings-fixture'){res.writeHead(200,{'Content-Type':'text/html'});return res.end(html)}
  // Keep the real service request builder; route it solely to this mock server.
  if(p==='/src/services/apiBaseUrl.ts'){res.writeHead(200,{'Content-Type':'application/javascript'});return res.end('export function resolveRuntimeApiBase(){return window.location.origin}')}
  const upstream=await fetch('http://localhost:5173'+req.url)
  res.writeHead(upstream.status,{'Content-Type':upstream.headers.get('content-type')||'text/plain'});res.end(Buffer.from(await upstream.arrayBuffer()))
 }catch(error){reply({error:error.message},500)}
})
server.listen(5188,'127.0.0.1',()=>console.log('Synthetic warning fixture: http://127.0.0.1:5188/warnings-fixture'))
const close=()=>{server.close();fs.rmSync(fixture,{force:true});process.exit()}
process.on('SIGINT',close);process.on('SIGTERM',close)
