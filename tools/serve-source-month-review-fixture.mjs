// Isolated actual-component UI verification. All /api traffic terminates here;
// there is no database connection, real payroll write, mail or production call.
import http from 'node:http'
import fs from 'node:fs'
import assert from 'node:assert/strict'
import { quickPreparationFixture } from './fixtures/excel-payroll-quick-prepare.mjs'

const port=5192,upstream='http://localhost:5173',entry=new URL('../payroll-ui/.codex-source-month-fixture.tsx',import.meta.url)
const fixture=quickPreparationFixture(101),{batch,context}=fixture
batch.rows.forEach((row,index)=>{row.employment={employeeId:index+1};if(index<2)context.source.rows[index+1].cells[8].value='0'})
context.source.rows[1].cells[3].value='26';context.source.rows[1].cells[5].value='40'
const setup={rowIds:batch.rows.map(row=>row.id),components:[{existingComponentId:'102',code:'PF',expectedColumnIndex:8}],employeeDecisions:[],payrollDaysColumnIndex:3,payableDaysColumnIndex:5,attendanceCycleStartDay:1,attendanceCycleEndDay:31}
const receipts=new Map(),cleanupReceipts=new Map(),calls=[]
const attendanceReceipts=new Map(),attendanceSaved=new Set()
let cleanupPresent=true,failSecondGroup=true,failSecondAttendance=true,forbidden=0
const original=JSON.stringify({batch,context})
fs.writeFileSync(entry,`
import React,{useState}from'react';import ReactDOM from'react-dom/client';import{ConfigProvider}from'antd';
import theme from'/src/theme/hrmsTheme.ts';import Cleanup from'/src/components/EmptySalarySetupCleanup.tsx';import Statutory from'/src/components/SourcePeriodStatutoryReview.tsx';import SourceAttendance from'/src/components/SourceMonthAttendanceReview.tsx';
import'antd/dist/reset.css';import'/src/index.css';import'/src/OrganizationSetup.css';import'/src/ModuleDrawer.css';import'/src/AppShell.css';
const data=${JSON.stringify({batch,context,setup})};
function App(){const[cleaned,setCleaned]=useState(false);return<main style={{padding:24,maxWidth:1200,margin:'auto'}}><h1>Source-month review · synthetic test</h1><p>Disposable synthetic records only. No production API, database or mail.</p><p role="status">Empty setup entry: {cleaned?'deleted; configured October salary retained':'awaiting review'}</p><Cleanup employeeId={71} clientId={42} employeeCode="SYNTH-71" onApplied={()=>setCleaned(true)}/><hr/><SourceAttendance clientId={42} batch={data.batch} setup={data.setup} disabled={false}/><Statutory clientId={42} batch={data.batch} context={data.context} setup={data.setup} disabled={false}/><p>101 synthetic source employees. The second save response fails once to test safe retry.</p></main>}
ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><App/></ConfigProvider>);
`)
const html=`<!doctype html><html><head><title>Source-month review verification</title><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/.codex-source-month-fixture.tsx');</script></body></html>`
const server=http.createServer(async(req,res)=>{
  const url=new URL(req.url,`http://127.0.0.1:${port}`),path=url.pathname
  const reply=(data,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(data))}
  try {
    if(path==='/fixture-status')return reply({calls,cleanupDeletes:cleanupPresent?0:1,configuredSalaryRetained:true,sourceUnchanged:original===JSON.stringify({batch,context}),savedEmployees:[...receipts.values()].reduce((n,r)=>n+r.savedEmployees,0),attendanceSaved:attendanceSaved.size,realDatabaseWrites:0,realEmails:0,forbidden})
    if(path==='/api/employees/71/salary-setup-cleanup'){
      if(req.method==='GET'){
        assert.equal(url.searchParams.get('clientId'),'42')
        const month=url.searchParams.get('payPeriod'),blockers=month==='2026-10'?[]:['No verified October setup cleanup is available for this month.']
        return reply({employeeId:71,clientId:42,employeeCode:'SYNTH-71',payPeriod:month,planHash:'d'.repeat(64),templateId:'501',annualCtc:0,emptyEffectiveDates:cleanupPresent?['2026-10-06']:[],blockers,canApply:cleanupPresent&&!blockers.length})
      }
      let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw),id=body.requestId.replaceAll('-','')
      assert.equal(body.clientId,42);assert.equal(body.payPeriod,'2026-10');assert.equal(body.expectedPlanHash,'d'.repeat(64));assert.equal(body.acknowledged,true)
      if(cleanupReceipts.has(id))return reply({...cleanupReceipts.get(id),replayed:true})
      assert.ok(cleanupPresent);cleanupPresent=false;calls.push({kind:'cleanup',emptyEntries:1})
      const result={employeeId:71,clientId:42,payPeriod:'2026-10',requestId:id,repairedMarkers:1,replayed:false};cleanupReceipts.set(id,result);return reply(result)
    }
    if(path.endsWith('/source-period-capabilities'))return reply({version:2,sourceZeroStatutorySupported:true,sourceAttendanceOnlySupported:true,payableOnlyAttendanceEnabled:true})
    if(path.endsWith('/source-attendance/preview')||path.endsWith('/source-attendance/save')){
      let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw),request=body.request||body
      assert.equal(request.version,1);assert.equal(request.clientId,42);assert.equal(request.sourceBatchId,batch.id);assert.equal(request.payPeriod,'2026-10')
      assert.equal(request.divisorColumn,3);assert.equal(request.payableColumn,5);assert.ok(request.rows.length>0&&request.rows.length<=100)
      if(path.endsWith('/preview')){
        const rows=request.rows.map(row=>{const cells=context.source.rows[row.employeeId].cells,divisor=Number(cells[3].value),payableUnits=Number(cells[5].value);return {...row,employeeCode:'TEST-'+row.employeeId,divisor,payableUnits,blockers:payableUnits>divisor&&!request.reviewedExtraPaidUnits?['Extra source units need explicit review']:[]}})
        calls.push({kind:'attendance-preview',count:rows.length});return reply({request:{...request,sourceHash:'a'.repeat(64)},planHash:'c'.repeat(64),rows,blockers:[],canSave:rows.every(row=>!row.blockers.length)})
      }
      assert.equal(body.expectedPlanHash,'c'.repeat(64));assert.equal(body.acknowledged,true)
      if(attendanceReceipts.has(body.requestId))return reply({...attendanceReceipts.get(body.requestId),replayed:true})
      if(request.rows[0].employeeId===101&&failSecondAttendance){failSecondAttendance=false;return reply({error:'Synthetic attendance second-group response failed once. Retry the same review.'},503)}
      for(const row of request.rows){assert.ok(!attendanceSaved.has(row.employeeId));attendanceSaved.add(row.employeeId)}
      const result={requestId:body.requestId,savedEmployees:request.rows.length,replayed:false};attendanceReceipts.set(body.requestId,result);calls.push({kind:'attendance-save',count:request.rows.length});return reply(result)
    }
    if(path.endsWith('/source-period/preview')||path.endsWith('/source-period/save')){
      let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw),request=body.request||body
      assert.equal(request.sourcePeriodReviewVersion,2);assert.equal(request.clientId,42);assert.equal(request.payPeriod,'2026-10');assert.equal(request.sourceBatchId,batch.id)
      assert.ok(request.rows.length>0&&request.rows.length<=100)
      for(const row of request.rows){assert.equal(row.amount,0);if(row.sourceZeroStatutoryColumns){assert.ok(row.employeeId<=2);assert.deepEqual(row.sourceZeroStatutoryColumns,{PF:8})}}
      if(path.endsWith('/preview')){
        calls.push({kind:'preview',count:request.rows.length})
        const blockers=request.rows.some(row=>!attendanceSaved.has(row.employeeId))?['Save reviewed native source attendance first']:[]
        return reply({request:{...request,sourceHash:'a'.repeat(64)},sourceHash:'a'.repeat(64),planHash:'b'.repeat(64),canSave:!blockers.length,blockers,rows:request.rows.map(row=>({employeeId:row.employeeId,employeeCode:'TEST-'+row.employeeId,employeeName:'Synthetic employee '+row.employeeId,sourceRowId:row.sourceRowId,blockers:[]}))})
      }
      assert.equal(body.expectedPlanHash,'b'.repeat(64));assert.equal(body.acknowledged,true)
      if(receipts.has(body.requestId))return reply({...receipts.get(body.requestId),replayed:true})
      if(request.rows[0].employeeId===101&&failSecondGroup){failSecondGroup=false;calls.push({kind:'synthetic-save-failure',count:1});return reply({error:'Synthetic second-group response failed once. Retry this reviewed input.'},503)}
      const result={requestId:body.requestId,savedEmployees:request.rows.length,replayed:false};receipts.set(body.requestId,result);calls.push({kind:'save',count:request.rows.length});return reply(result)
    }
    if(path.startsWith('/api/')){forbidden++;return reply({error:'Real API blocked by isolated fixture'},403)}
    if(path==='/source-month-responsive'){
      const width=Number(url.searchParams.get('width')),height=Number(url.searchParams.get('height'))
      assert.ok([390,768,1366].includes(width));assert.ok([768,844,1024].includes(height))
      res.writeHead(200,{'Content-Type':'text/html'});return res.end(`<!doctype html><html><head><title>Responsive source-month review</title><style>body{margin:0;background:#eee}iframe{display:block;border:0;background:white}</style></head><body><iframe title="Source-month responsive preview" width="${width}" height="${height}" src="/source-month-fixture"></iframe></body></html>`)
    }
    if(path==='/source-month-fixture'){res.writeHead(200,{'Content-Type':'text/html'});return res.end(html)}
    if(path==='/src/services/apiBaseUrl.ts'){res.writeHead(200,{'Content-Type':'application/javascript'});return res.end('export function resolveRuntimeApiBase(){return window.location.origin}')}
    const response=await fetch(upstream+req.url);res.writeHead(response.status,{'Content-Type':response.headers.get('content-type')||'text/plain'});res.end(Buffer.from(await response.arrayBuffer()))
  }catch(error){reply({error:error.message},500)}
})
server.listen(port,'127.0.0.1',()=>console.log(`Isolated review fixture: http://127.0.0.1:${port}/source-month-fixture`))
function close(){server.close();fs.rmSync(entry,{force:true});process.exit()}
process.on('SIGINT',close);process.on('SIGTERM',close)
