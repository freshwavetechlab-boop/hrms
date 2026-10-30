import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const require = createRequire(new URL('../playwright-e2e/package.json', import.meta.url))
const { chromium, expect } = process.env.HRMS_PLAYWRIGHT_MODULE ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href) : require('@playwright/test')
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local UI only.')
const output = path.join(os.tmpdir(), 'frevo-excel-bootstrap-ui-'+Date.now())
fs.mkdirSync(output, { recursive: true })
const fixture = `.codex-bootstrap-${crypto.randomUUID()}.tsx`
const fixturePath = fileURLToPath(new URL(`../payroll-ui/${fixture}`, import.meta.url))
fs.writeFileSync(fixturePath, `
import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';
import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';
import Excel from '/src/components/ExcelPayslips.tsx';import Employee from '/src/pages/EmployeePage.tsx';
import Header from '/src/components/layout/AppPageHeader.tsx';
import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';
import TransferReview from '/src/components/ExcelPayrollTransferReview.tsx';
import Applicability from '/src/components/ExcelPayrollTemplateApplicability.tsx';
import Setup from '/src/components/ExcelPayrollSetup.tsx';
import {Form} from 'antd';
import {previewPayrollSetup} from '/src/services/excelPayrollSetupService.ts';
function NativeFixture(){
 const [request,setRequest]=React.useState({batchId:'a'.repeat(32),rowIds:new URLSearchParams(location.search).has('multi')?['row-1','row-2']:['row-1'],templateName:'Synthetic transfer',effectiveFrom:'2026-09-01',transferReviewVersion:1,...(new URLSearchParams(location.search).has('scope')?{templateScopeReviewVersion:1}:{}),payrollDaysColumnIndex:1,payableDaysColumnIndex:2,presentDaysColumnIndex:3,attendanceCycleStartDay:1,attendanceCycleEndDay:30,importAttendance:true,workWeek:'Saturday off',payrollReportGenerationDay:3,components:[{key:'basic',code:'BASIC',name:'Basic',category:'Earning',componentRole:'Regular Earning',statutoryType:'None',calculationType:'Fixed Amount',value:'0',formula:'',baseComponent:'',sourceColumnIndex:5,expectedColumnIndex:6,proRata:true,taxable:true,priority:1,isPeriodAdjustment:false,exemptWhenSourceZero:false}],employeeDecisions:[],editableDefaults:{}});
 const [transfer,setTransfer]=React.useState({version:1,sourceBatchId:'a'.repeat(32),sourceMonth:'2026-09',sourceHash:'c'.repeat(64),effectiveFrom:'2026-09-01',preparationStatus:'Blocked',parityStatus:'Not verified - Employee payroll parity remains pending',tolerance:0.000001,components:[],formulas:[],templates:[],attendanceProofs:[]});
 const [busy,setBusy]=React.useState(false);
 const preview=async()=>{if(busy)return;setBusy(true);try{const r=await previewPayrollSetup(33,request);if(r.ok&&r.data?.transfer)setTransfer(r.data.transfer);}finally{setBusy(false)}};
 return <div style={{flex:1,minHeight:0,overflow:'auto',padding:12,minWidth:0}}><h2>Synthetic saved-batch Transfer review</h2><label><input aria-label="Fixture import source attendance" type="checkbox" checked={request.importAttendance} onChange={e=>setRequest(s=>({...s,importAttendance:e.target.checked}))}/>Import source attendance (synthetic)</label>{new URLSearchParams(location.search).has('scope')&&<Form component="div" layout="vertical"><Applicability request={request} disabled={busy} locations={[{id:1,name:'Synthetic office'},{id:2,name:'Synthetic second location'}]} onChange={change=>setRequest(s=>({...s,...change}))}/></Form>}<TransferReview request={request} transfer={transfer} disabled={busy} onChange={change=>setRequest(s=>({...s,...change}))}/><button disabled={busy} onClick={preview}>Refresh mocked saved-batch preview</button><button onClick={()=>setTransfer(s=>({...s,sourceHash:'b'.repeat(64)}))}>Simulate changed saved batch version</button><p>No Apply endpoint is invoked in this fixture.</p></div>
}

import Overview from '/src/components/ExcelPayslipOverview.tsx';
import Lifecycle from '/src/components/ExcelPayslipBatchLifecycle.tsx';
function WorkspaceFixture(){
 const [overview,setOverview]=React.useState(false),[notice,setNotice]=React.useState('');
 return <div style={{flex:1,minHeight:0,overflow:'auto',padding:12,minWidth:0}}><button onClick={()=>setOverview(true)}>Open selected-scope overview</button><p role="status">{notice}</p><Overview open={overview} canConfigure={true} onClose={()=>setOverview(false)} onOpen={(client,batch,setup)=>{setOverview(false);setNotice((setup?'Master setup':'Batch')+' handoff '+client+' '+batch)}}/><Lifecycle clientId={33} batchId={'a'.repeat(32)} canDelete={true} disabled={false} onDeleted={()=>setNotice('Synthetic deletion confirmed; originals/audit retained')} onInspectBatch={id=>setNotice('Inspect retained batch '+id)}/></div>
}

function RoundingFixture(){return <Setup clientId={33} batch={{id:'a'.repeat(32),clientId:33,month:'2026-09',clientName:'Synthetic',sourceFileName:'Synthetic numeric fixture',sheetName:'Salary',headerRow:1,rows:[{id:'row-1',sourceRow:2,employeeName:'Synthetic person',employeeCode:'TEST001',email:'',information:[],earnings:[{label:'Basic',amount:133.99999999999997}],deductions:[{label:'ESIC',amount:1.0049999999999997}],employerContributions:[],netPay:116.91499999999999,warnings:[]}]}} rowIds={['row-1']} onClose={()=>{}} onApplied={()=>{throw Error('Unexpected fixture Apply')}}/>}
function LegacyFixture(){const rows=Array.from({length:5},(_,i)=>({id:'row-'+(i+1),sourceRow:i+2,employeeName:'Synthetic person '+(i+1),employeeCode:'TEST00'+(i+1),email:'',information:[],earnings:[{label:'Basic',amount:10000}],deductions:[{label:'PF',amount:1200}],employerContributions:[{label:'Employer PF',amount:1300}],netPay:8800,warnings:[],employment:{employeeId:i<2?i+1:null,employeeCode:'TEST00'+(i+1),identityReviewRequired:i===4,blockedForMonth:false}}));return <Setup clientId={33} batch={{id:'a'.repeat(32),clientId:33,month:'2026-09',clientName:'Synthetic legacy',sourceFileName:'Synthetic.xlsx',sheetName:'Salary',headerRow:1,rows}} rowIds={rows.map(r=>r.id)} onClose={()=>{}} onApplied={()=>{throw Error('Unexpected Apply')}}/>}
function ReviewScaleFixture(){const rows=Array.from({length:100},(_,i)=>({id:'review-row-'+(i+1),sourceRow:i+2,employeeName:'Synthetic scale '+(i+1),employeeCode:'SCALE'+String(i+1).padStart(3,'0'),email:'',information:[],earnings:[{label:'Basic',amount:10000}],deductions:[{label:'PF',amount:1200}],employerContributions:[],netPay:8800,warnings:[],employment:{employeeId:i+1,employeeCode:'SCALE'+String(i+1).padStart(3,'0'),identityReviewRequired:false,blockedForMonth:false}}));return <Setup clientId={33} batch={{id:'a'.repeat(32),clientId:33,month:'2026-09',clientName:'Synthetic scale',sourceFileName:'Synthetic.xlsx',sheetName:'Salary',headerRow:1,rows}} rowIds={rows.map(r=>r.id)} onClose={()=>{}} onApplied={()=>{throw Error('Unexpected scale fixture Apply')}}/>}
const reviewScale=new URLSearchParams(location.search).has('reviewScale');
const legacy=new URLSearchParams(location.search).has('legacy');
const rounding=new URLSearchParams(location.search).has('rounding');
const master=new URLSearchParams(location.search).has('master');
const workspace=new URLSearchParams(location.search).has('workspace');
const native=new URLSearchParams(location.search).has('native');
ReactDOM.createRoot(document.getElementById('root')).render(<BrowserRouter><ToastProvider><AuthGate><div className="fixture-root"><Header title={master?'Employees':'Excel Payslips'} description="" icon={null} breadcrumbs={[]} workspace={master?'employees':'excel-payslips'} /><main>{reviewScale?<ReviewScaleFixture/>:legacy?<LegacyFixture/>:rounding?<RoundingFixture/>:workspace?<WorkspaceFixture/>:native?<NativeFixture/>:master?<Employee/>:<Excel/>}</main></div></AuthGate></ToastProvider></BrowserRouter>);
`)
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;font-family:Arial;background:#f6f8fc}#root,.fixture-root{height:100dvh;min-height:0;display:flex;flex-direction:column}.fixture-root>main{flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:8px;box-sizing:border-box}.fixture-root>main>section{flex:1;min-height:0}</style></head><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const user = { id: 1, clientId: 33, displayName: 'Synthetic admin', roles: ['client_admin'], permissions: ['employees.manage', 'payroll.run', 'settings.manage'], isActive: true }
const employees = Array.from({ length: 26 }, (_, i) => ({ id: i + 1, clientId: 33, employeeCode: `TEST${String(i + 1).padStart(3, '0')}`, firstName: 'Employee', lastName: String(i + 1).padStart(3, '0'), isActive: i !== 1, employmentStatus: i === 1 ? 'Resigned' : 'Active', lastWorkingDate: i === 1 ? '2026-08-31' : null, department: 'Operations', designation: 'Executive', workLocationId: 1, grade: 'G1', workEmail: '', personalDetails: { skillCategory: 'Skilled' }, paymentDetails: {}, salaryComponents: {} }))
const batch = { id: 'a'.repeat(32), clientId: 33, clientName: 'Synthetic Client', month: '2026-09', createdAtUtc: '2026-10-17T10:00:00Z', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1, rows: employees.map((e, i) => ({ id: `row-${e.id}`, sourceRow: i + 2, employeeName: `${e.firstName} ${e.lastName}`, employeeCode: e.employeeCode, email: '', information: [], earnings: [{ label: 'Basic', amount: 10000 }], deductions: [{ label: 'PF', amount: 1200 }], employerContributions: [], netPay: 8800, warnings: [], employment: { employeeId: e.id, employeeCode: e.employeeCode, status: e.employmentStatus, lastWorkingDate: e.lastWorkingDate, blockedForMonth: i === 1, identityReviewRequired: false } })) }
Object.assign(batch.rows[2].employment, { employeeId: null, identityReviewRequired: true })
const source = { sheetName: 'Salary', headerRow: 1, columnCount: 6, columns: ['Name', 'Basic', 'Month days', 'Working Day', 'PF', 'Net'].map((name, i) => ({ columnIndex: i, sourceHeader: name, label: name, kind: ['employeeName', 'earning', 'info', 'info', 'deduction', 'netPay'][i] })), rows: batch.rows.map(r => ({ sourceRow: r.sourceRow, cells: [{ value: r.employeeName }, { value: '10000' }, { value: '30' }, { value: '30' }, { value: '1200', formulaText: `B${r.sourceRow}*12%` }, { value: '8800' }] })), excludedSourceRows: [] }
const sharedHeaderSource=structuredClone(source);sharedHeaderSource.columnCount=7;sharedHeaderSource.columns[1].sourceHeader='Wages';sharedHeaderSource.columns[1].label='Wages';sharedHeaderSource.columns[2].sourceHeader='Header reference';sharedHeaderSource.columns[2].label='Header reference';sharedHeaderSource.columns.push({columnIndex:6,sourceHeader:'Rate',label:'Rate',kind:'info'});for(const r of sharedHeaderSource.rows){r.cells[1].formulaText='G'+r.sourceRow+'/$C$1*D'+r.sourceRow;r.cells.push({value:'10000'})}sharedHeaderSource.rows.unshift({sourceRow:1,cells:[{value:''},{value:''},{value:'30'}]});
const workspaceCatalog=[{clientId:33,batchId:'a'.repeat(32),clientName:'Synthetic Alpha',month:'2026-09',fileName:'Alpha-September.xlsx',sheetName:'Salary',rowCount:28,createdAtUtc:'2026-10-01T00:00:00Z',hasCalculationSource:true},{clientId:44,batchId:'b'.repeat(32),clientName:'Synthetic Beta',month:'2026-09',fileName:'Beta-September.xlsx',sheetName:'Salary',rowCount:2,createdAtUtc:'2026-10-01T00:00:00Z',hasCalculationSource:true},{clientId:33,batchId:'c'.repeat(32),clientName:'Synthetic Alpha',month:'2026-08',fileName:'Alpha-August-old.xlsx',sheetName:'Salary',rowCount:1,createdAtUtc:'2026-09-01T00:00:00Z',hasCalculationSource:false}]
const workspaceRows=workspaceCatalog.flatMap(b=>Array.from({length:b.rowCount},(_,i)=>{const n=i+1,m=b.clientId===44?'Matched':['Missing code','Matched','Unmatched','Conflict'][n%4],code=m==='Missing code'?'':n===1?'SHARED':'SYN-'+b.clientId+'-'+n;return {...b,key:b.clientId+':'+b.batchId+':'+n,sourceRow:n+1,employeeCode:code,employeeName:'Synthetic '+b.clientId+' employee '+n,masterEmployeeId:m==='Matched'?b.clientId*1000+n:null,masterStatus:m,masterReason:m==='Matched'?'One scoped employee match':m==='Conflict'?'Ambiguous employee code needs review':m==='Missing code'?'Code not supplied':'New reviewed employee candidate',readiness:m==='Matched'?'Ready':m==='Conflict'?'Blocked':'Review',reasons:m==='Matched'?[]:[m==='Conflict'?'Ambiguous identity':'Source review required'],scopeOverlap:false,setupDecision:'Review existing Configure workflow'}}))
const overviewRequests=[],cleanupRequests=[]
let overviewFailNext=false,superseded=false,jobDiscarded=false,discardFailed=false,deleteFailed=false,activeCleanupJob=false,cleanupVersion=0
let originalMetadataAttempts=0
const originalBytes=Buffer.from('Synthetic,Value\nExample,1\n'),originalSha=createHash('sha256').update(originalBytes).digest('hex')
const syntheticOriginal={id:'1'.repeat(32),fileName:'Synthetic.csv',sizeBytes:originalBytes.length,sha256:originalSha,uploadedAtUtc:'2026-10-01T00:00:00Z'}
const cleanupPreview=()=>({batchId:batch.id,token:'cleanup-preview-'+(++cleanupVersion),payslipRows:28,calculationSources:1,deliveryRecords:0,mailJobs:jobDiscarded?0:1,requestReceipts:0,notifications:0,notificationLogs:0,masterSetupReceipts:0,derivedBatches:0,originalLinks:1,otherBatchOriginalLinks:2,legacyStorageRecords:0,cleanupAuditRecords:superseded?1:0,isLegacy:true,legacyReviewValid:superseded,legacyReviewStatus:superseded?'Reviewed supersession recorded':'Legacy ancestry review required',recordedParentId:'',candidateCount:1,candidates:[{id:'d'.repeat(32),month:'2026-09',fileName:'Synthetic-final.xlsx',sheetName:'Salary',rowCount:28,createdAtUtc:'2026-10-02T00:00:00Z'}],blockers:[...(!superseded?['Review legacy supersession first']:[]),...(!jobDiscarded?['Never-queued attempt remains']:[]),...(activeCleanupJob?['Active queued delivery job must finish before deletion']:[])],canDelete:superseded&&jobDiscarded&&!activeCleanupJob,objects:[{kind:'OriginalAttachment',id:syntheticOriginal.id,status:'Retained',reason:'Original bytes and shared references are retained',action:'',count:3,label:'Synthetic.csv'},...(!jobDiscarded?[{kind:'MailJob',id:'job-1',status:'NeverQueued',reason:'Never queued or dispatched; separately confirmed discard permitted',action:'discard-unqueued-job',count:1,label:'Synthetic never-queued attempt'}]:[]),...(activeCleanupJob?[{kind:'MailJob',id:'active-job',status:'Queued',reason:'Active delivery evidence is protected; finish it in the delivery workflow',action:'',count:1,label:'Synthetic active job'}]:[])]})

const browser = await chromium.launch({ headless: process.env.HRMS_TEST_HEADLESS === '1' })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, serviceWorkers: 'block' })
const page = await context.newPage(), errors = [], checks = [], writes = [], consoleErrors = []
let rejectExit = true
const nativePreviewRequests=[], savedVerificationRequests=[]
page.on('pageerror', e => errors.push(e.message))
page.on('console',m=>{if(m.type()==='error')consoleErrors.push(m.text().replace(/([?&](?:token|access_token)=)[^\\s'\"&]+/gi,'$1[redacted]'))})
await context.routeWebSocket('**/*',ws=>ws.close())
await context.route('**/*', async route => {
  const req = route.request(), url = new URL(req.url()), p = url.pathname
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort()
  if (p === '/bootstrap-fixture') return route.fulfill({ contentType: 'text/html', body: html })
  if(url.pathname==='/@vite/client')return route.fulfill({contentType:'application/javascript',body:'const s=new Map();export function createHotContext(){return {data:{},accept(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){}}}export function updateStyle(id,css){let e=s.get(id);if(!e){e=document.createElement("style");e.dataset.viteDevId=id;document.head.appendChild(e);s.set(id,e)}e.textContent=css}export function removeStyle(id){s.get(id)?.remove();s.delete(id)}export function injectQuery(url){return url}'});
  if (!p.startsWith('/api/')) {
    const staticAsset=req.method()==='GET'&&url.origin===new URL(origin).origin&&(p==='/'+fixture||['/src/','/node_modules/','/@','/assets/'].some(prefix=>p.startsWith(prefix))||p==='/favicon.ico')
    if(staticAsset)return route.continue();errors.push('Blocked unexpected non-API resource '+req.method()+' '+p);return route.abort()
  }
  const reply = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
  if (p === '/api/auth/me') return reply(page.url().includes('workspace=1')?{...user,clientId:null,roles:['super_admin']}:user)
  if (p.startsWith('/api/excel-payslips/batches/') && p.endsWith('/consolidation') && req.method() === 'GET') return reply(null)
  if (p === '/api/employees/resignation-defaults') return reply(req.postDataJSON().employeeIds.map(employeeId => ({ employeeId, lastPayrollMonth: '2026-09', lastWorkingDate: '2026-09-30' })))
  if (p === '/api/employees/mark-resigned' && req.method() === 'POST') {
    const body = req.postDataJSON(); writes.push(body)
    assert.equal(body.clientId, 33); assert.deepEqual(body.employeeIds, [1]); assert.equal(body.lastWorkingDate, '2026-09-30')
    if (rejectExit) return reply({ error: 'Review existing payroll before changing employment dates.' }, 400)
    Object.assign(employees[0], { isActive: false, employmentStatus: 'Resigned', lastWorkingDate: body.lastWorkingDate })
    Object.assign(batch.rows[0].employment, { status: 'Resigned', lastWorkingDate: body.lastWorkingDate })
    return reply({ marked: 1, alreadyMarked: 0, lastWorkingDate: body.lastWorkingDate, employeeIds: [1] })
  }
  if(p==='/api/excel-payroll-setup/preview'&&req.method()==='POST'){
    if(page.url().includes('reviewScale=1')){const body=req.postDataJSON();assert.equal(url.searchParams.get('clientId'),'33');assert.equal(body.batchId,batch.id);assert.deepEqual(body.rowIds,Array.from({length:100},(_,i)=>'review-row-'+(i+1)));assert.ok(!('source'in body)&&!('file'in body));nativePreviewRequests.push(body);const unexplained=page.url().includes('unexplained=1');const proofs=body.rowIds.map((rowId,i)=>{const proofHash=createHash('sha256').update(body.effectiveFrom+':'+rowId).digest('hex');return{rowId,sourceRow:i+2,status:unexplained&&i===99?'Unexplained difference':'Explained rounding',reason:unexplained&&i===99?'Synthetic unexplained difference remains blocked':'Synthetic deterministic source/core proof; UI contract only',proofHash,acknowledged:body.roundingReviews?.[rowId]===proofHash,sourceNetRaw:8800,sourceNetDisplay:8800,coreNet:8800,rawNetDelta:0,displayNetDelta:0,lines:Array.from({length:5},(_,j)=>({code:'TEST_'+j,category:j?'Deduction':'Earning',sourceRaw:100,coreUnrounded:100,sourceDisplay:100,calculated:100,displayDelta:0}))}});const ready=!unexplained&&proofs.every(p=>p.acknowledged)&&body.reviewedPolicyScope===true&&(body.templateDecisions||[]).some(d=>d.existingTemplateId==='500'&&!d.keepExisting);return reply({canApply:ready,migrationRequired:false,planHash:(ready?'8':'7').repeat(64),coverage:'Synthetic100-row review only; no live calculation',issues:unexplained?[{severity:'Error',message:'Synthetic unexplained last-row mismatch'}]:[],rows:body.rowIds.map((rowId,i)=>({rowId,employeeName:'Synthetic scale '+(i+1),action:'Review',payrollDays:30,presentDays:30,payableDays:30,gross:10000,deductions:1200,net:8800,lines:[]})),transfer:{version:1,sourceBatchId:batch.id,sourceMonth:'2026-09',sourceHash:'c'.repeat(64),effectiveFrom:body.effectiveFrom,preparationStatus:ready?'Synthetic ready':'Blocked',parityStatus:'UI contract only; no payroll parity',tolerance:0.000001,components:[],formulas:[],templates:[{variantKey:'1000',rowIds:body.rowIds,action:(body.templateDecisions||[]).some(d=>d.existingTemplateId==='500')?'Reuse unchanged':'Review required',candidates:[{id:'500',name:'Synthetic reviewed scale template',exact:true,configurationExact:true,scopeStatus:'Compatible',scopeReason:'Synthetic source rules and applicability',recordedScopes:[],differences:[],details:[]}],existingResults:[]}],amountComparisons:proofs}})}

    const body=req.postDataJSON();assert.equal(url.searchParams.get('clientId'),'33');assert.equal(body.batchId,batch.id);assert.deepEqual(body.rowIds,page.url().includes('multi=1')?['row-1','row-2']:['row-1']);assert.ok(!('source' in body)&&!('file' in body));nativePreviewRequests.push(body)
    if(page.url().includes('rounding=1')){
      assert.deepEqual(body.rowIds,['row-1']);assert.equal(body.batchId,batch.id);
      return reply({canApply:false,planHash:'f'.repeat(64),coverage:'Mocked arithmetic response; display rendering only, not a real calculation.',issues:[],rows:[{rowId:'row-1',employeeName:'Synthetic person',action:'Review only',payrollDays:30,presentDays:30,payableDays:30,gross:134,deductions:17.08,benefits:21.78,net:116.92,lines:[{code:'BASIC',sourceAmount:133.99999999999997,calculatedAmount:134,variance:0.00000000000003},{code:'ESIC',sourceAmount:1.0049999999999997,calculatedAmount:1,variance:-0.0049999999999997},{code:'ESIC_ER',sourceAmount:4.355,calculatedAmount:4.36,variance:0.005}]}]});
    }
    const a=body.attendanceReview,kept=body.templateDecisions?.some(d=>d.keepExisting),attendanceBlocked=!a?.reviewed||(a.mode==='VerifyExistingNative'&&body.importAttendance!==false),blocked=attendanceBlocked||!body.reviewedPolicyScope||(body.templateDecisions?.length||0)<(page.url().includes('multi=1')?2:1)
    const status=attendanceBlocked?'Blocked':a.mode==='VerifyExistingNative'?'Native attendance inputs matched':'Reviewed source seed — native pending'
    const transfer={version:1,sourceBatchId:batch.id,sourceMonth:'2026-09',sourceHash:'c'.repeat(64),effectiveFrom:'2026-09-01',preparationStatus:kept?'Ready to record keep decisions - no source transfer; parity unresolved':blocked?'Blocked':'Ready for reviewed configuration Apply - payroll parity not verified',parityStatus:'Not verified - Employee payroll parity remains pending',tolerance:0.000001,components:[],formulas:[],templates:[{variantKey:'1000',rowIds:['row-1'],action:kept?'Keep current assignments unchanged - Excel parity unresolved':'Review source-derived variant',candidates:[{id:'500',name:'Synthetic existing template',exact:false,differences:['Formula differs'],details:[{component:'BASIC',field:'formula',existing:'BASIC_MONTHLY*0.9',excelDerived:'BASIC_MONTHLY'}]}],existingResults:[{rowId:'row-1',templateId:'500',complete:true,net:900,sourceNet:1000,variance:-100,coverage:'Synthetic existing draft comparison only',lines:[]}]}],attendanceProofs:kept?[]:[{rowId:'row-1',sourceRow:3,mode:a?.mode||'Unreviewed',status,reason:blocked?'Review source meanings/cycle; turn off attendance import for existing native proof.':'Synthetic matched inputs only; Employee parity not certified.',sourceHash:'c'.repeat(64),inputHash:'d'.repeat(64),payrollDays:30,presentDays:20,payableDays:28,sourceLwp:2,nativeHash:status.startsWith('Native')?'e'.repeat(64):null,nativeInput:status.startsWith('Native')?{month:'2026-09',cycleStart:'2026-09-01',cycleEnd:'2026-09-30',payrollDays:30,presentDays:20,payableDays:28}:null}]}
    if(page.url().includes('multi=1')){
      const reusable=transfer.templates[0];reusable.candidates[0].exact=true;reusable.candidates[0].name='Synthetic exact client rules';reusable.candidates[0].differences=[];reusable.candidates[0].details=[];
      const capped=structuredClone(reusable);capped.variantKey='1001';capped.rowIds=['row-2'];capped.candidates[0].exact=false;capped.candidates[0].differences=['PF cap differs'];capped.candidates[0].details=[{component:'PF',field:'formula',existing:'BASIC_EARNED*12%',excelDerived:'MIN(BASIC_EARNED,150)*12%'}];capped.existingResults[0].rowId='row-2';
      transfer.templates.push(capped);
      for(const variant of transfer.templates){const decision=body.templateDecisions?.find(d=>d.variantKey===variant.variantKey);variant.action=decision?.keepExisting?'Keep current assignments unchanged - Excel parity unresolved':decision?.existingTemplateId?'Reuse unchanged':decision?.createVariant?'Create reviewed variant':'Review source-derived variant'}
      transfer.preparationStatus=blocked?'Blocked':transfer.templates.every(t=>body.templateDecisions?.find(d=>d.variantKey===t.variantKey)?.keepExisting)?'Ready to record keep decisions - no source transfer; parity unresolved':'Ready for reviewed configuration Apply - payroll parity not verified';
      transfer.attendanceProofs=[]; // Multi fixture tests independent decisions only; it supplies no native-input certification.

    }
    if(page.url().includes('scope=1')){
      const t=transfer.templates[0];t.candidates[0]={id:'500',name:'Synthetic legacy exact rules',exact:false,configurationExact:true,scopeStatus:'Unknown legacy scope',scopeReason:'No applicability receipt has been recorded.',recordedScopes:[],differences:[],details:[]};
      t.candidates.push({id:'501',name:'Synthetic prior policy',exact:false,configurationExact:true,scopeStatus:'Different policy/basis',scopeReason:'Recorded dated policy differs; use a reviewed new effective variant.',recordedScopes:[{clientId:33,templateId:'501',rulesSignature:'synthetic-only',effectiveFrom:'2026-08-01',policyKey:'DATED_POLICY:TEST-v1',sourceBatchId:'d'.repeat(32),sourceMonth:'2026-08',scope:{reviewed:true,effectiveTo:'2026-08-31',openEnded:false,locationMode:'Listed',workLocationIds:[2],newLocationNames:[],categoryKind:'SkillCategory',categories:['Skilled'],divisorMeaning:'ContractFixedDays',payableMeaning:'AlreadyPayable',policyBasis:'DatedPolicy',policyVersion:'TEST-v1'}}],differences:[],details:[]});
      const d=body.templateDecisions?.find(d=>d.variantKey==='1000');
      transfer.preparationStatus=body.templateScope?.reviewed&&d?.existingTemplateId==='500'&&d.scopeAction==='AttestLegacyScope'?'Synthetic scope review received - database applicability not tested':'Blocked';
      transfer.attendanceProofs=[];
    }
    return reply({transfer,issues:[],rows:[],canApply:!blocked,planHash:'f'.repeat(64)})
  }

  if(p==='/api/excel-payslips/overview-scopes'&&req.method()==='GET')return reply(page.url().includes('workspace=1')?workspaceCatalog:workspaceCatalog.filter(b=>b.clientId===33))
  if(p==='/api/excel-payslips/overview-preview'&&req.method()==='POST'){
    const body=req.postDataJSON();overviewRequests.push(body);assert.ok(body.scopes.length>0);assert.ok(body.scopes.every(s=>workspaceCatalog.some(b=>b.clientId===s.clientId&&b.batchId===s.batchId)))
    if(overviewFailNext){overviewFailNext=false;return reply({error:'Synthetic overview read failure; retry the same selected scope'},503)}
    const scoped=workspaceRows.filter(r=>body.scopes.some(s=>s.clientId===r.clientId&&s.batchId===r.batchId)),filtered=scoped.filter(r=>(body.readiness==='All'||r.readiness===body.readiness)&&(body.masterStatus==='All'||r.masterStatus===body.masterStatus)&&(!body.search||(r.employeeCode+' '+r.employeeName).toLowerCase().includes(body.search.toLowerCase())))
    const total={sourceRows:filtered.length,readyRows:filtered.filter(r=>r.readiness==='Ready').length,reviewRows:filtered.filter(r=>r.readiness==='Review').length,blockedRows:filtered.filter(r=>r.readiness==='Blocked').length,uniqueScopedCodeGroups:new Set(filtered.filter(r=>['Matched','Unmatched'].includes(r.masterStatus)).map(r=>r.clientId+':'+r.employeeCode)).size,matchedMasterRecords:new Set(filtered.filter(r=>r.masterStatus==='Matched').map(r=>r.masterEmployeeId)).size,matchedRows:filtered.filter(r=>r.masterStatus==='Matched').length,unmatchedRows:filtered.filter(r=>r.masterStatus==='Unmatched').length,conflictRows:filtered.filter(r=>r.masterStatus==='Conflict').length,missingCodeRows:filtered.filter(r=>r.masterStatus==='Missing code').length,overlapRows:0}
    return reply({scopeLabel:'Synthetic explicit selected scope',snapshot:'9'.repeat(64),observedAtUtc:'2026-10-25T00:00:00Z',scopeBatchCount:body.scopes.length,scopeTotalRows:scoped.length,page:body.page,pageSize:body.pageSize,totals:total,rows:filtered.slice((body.page-1)*body.pageSize,body.page*body.pageSize)})
  }
  if(p===`/api/excel-payslips/batches/${batch.id}/originals`&&req.method()==='GET'){assert.equal(url.searchParams.get('clientId'),'33');if(++originalMetadataAttempts===1)return reply({error:'Synthetic original metadata unavailable'},500);return reply([syntheticOriginal])}
  if(p===`/api/excel-payslips/batches/${batch.id}/originals/${syntheticOriginal.id}`&&req.method()==='GET')return route.fulfill({contentType:'text/csv',body:originalBytes})
  if(p===`/api/excel-payslips/batches/${batch.id}/delete-preview`&&req.method()==='GET')return reply(cleanupPreview())
  if(p===`/api/excel-payslips/batches/${batch.id}/cleanup-review`&&req.method()==='POST'){
    const body=req.postDataJSON();assert.equal(body.keeperBatchId,'d'.repeat(32));return reply({batchId:batch.id,keeperBatchId:body.keeperBatchId,token:'comparison-token',evidence:'Synthetic exact captured snapshot evidence',oldRows:28,keeperRows:28,changedRows:0,exactSnapshot:true,sameCalculationSource:true,blockers:[],eligible:true})
  }
  if(p===`/api/excel-payslips/batches/${batch.id}/cleanup-action`&&req.method()==='POST'){
    const body=req.postDataJSON();cleanupRequests.push(body);assert.equal(body.confirmBatchId,batch.id);assert.equal(body.acknowledged,true)
    if(body.action==='supersede-legacy'){assert.equal(body.confirmObjectId,batch.id);assert.equal(body.keeperBatchId,'d'.repeat(32));superseded=true}
    else if(body.action==='discard-unqueued-job'){assert.equal(body.confirmObjectId,'job-1');if(!discardFailed){discardFailed=true;return reply({error:'Synthetic resolution retry; original request receipt retained'},503)}const attempts=cleanupRequests.filter(r=>r.action==='discard-unqueued-job');assert.equal(attempts[0].requestId,body.requestId);jobDiscarded=true}
    else throw Error('Unexpected synthetic cleanup action')
    return reply({applied:true,alreadyApplied:false,preview:cleanupPreview()})
  }
  if(p===`/api/excel-payslips/batches/${batch.id}/delete`&&req.method()==='POST'){
    const body=req.postDataJSON();assert.equal(body.confirmBatchId,batch.id);assert.ok(body.token.startsWith('cleanup-preview-'));assert.ok(superseded&&jobDiscarded)
    if(!deleteFailed){deleteFailed=true;activeCleanupJob=true;return reply({error:'New queued job appeared; refresh dependencies before retrying deletion'},409)}
    assert.equal(activeCleanupJob,false);return reply({deleted:true,alreadyDeleted:false})
  }

  if(p === '/api/payroll-calculation/simulate') {assert.ok(page.url().includes('legacy=1'));const body=req.postDataJSON();savedVerificationRequests.push(body);assert.equal(body.clientId,33);assert.equal(body.payPeriod,'2026-09');assert.equal(body.mode,'Employee');assert.ok([1,2].includes(body.employeeId));assert.equal(body.payrollDays,null);assert.equal(body.payableDays,null);await new Promise(resolve=>setTimeout(resolve,120));return reply({mode:'Employee',complete:!page.url().includes('incomplete=1'),employee:'Synthetic person '+body.employeeId,net:page.url().includes('incomplete=1')?null:8800,gross:10000,deductions:1200,benefits:1300,attendanceEvidence:{sourceKind:'Monthly snapshot',payPeriod:'2026-09',workingDays:30,presentDays:30,payableDays:30,cycleStart:null,cycleEnd:null,temporaryDayOverrides:false,explanation:'Synthetic period totals; dated cycle is not recorded.',policyValidationError:''},issues:[],inputs:['PAYROLL_DAYS','PRESENT_DAYS','PAYABLE_DAYS'].map(name=>({name,value:30,source:'Synthetic saved attendance'})),lines:[{code:'BASIC',category:'Earning',earned:10000,raw:10000,formula:'Monthly basis',rounding:'2dp'},{code:'PF',category:'Deduction',earned:1200,raw:1200,formula:'BASIC*12%',rounding:'2dp'},{code:'EMPLOYER_PF',category:'Benefit',earned:1300,raw:1300,formula:'BASIC*13%',rounding:'2dp'}]})}
  if (req.method() !== 'GET') { errors.push(`Unexpected mutation ${req.method()} ${p}`); return reply({ error: 'Fixture blocks this write' }, 403) }
  if (p.endsWith('/clients') || p === '/api/clients') return reply([{ id: 33, code: 'TEST', name: 'Synthetic Client', isActive: true }])
  if (p === '/api/employees') return reply(employees)
  if (p === '/api/work-locations') return reply([{ id: 1, clientId: 33, name: 'Synthetic office', state: 'Uttar Pradesh', isActive: true }])
  if (p === '/api/setup') return reply({ salaryStructures: [], salaryComponents: [] })
  if (p.includes('field-configuration/catalog')) return reply({ forms: [], fields: [], values: {} })
  if(p.endsWith('/context')&&p.includes('payroll-setup')&&page.url().includes('reviewScale=1')){const rows=Array.from({length:100},(_,i)=>({rowId:'review-row-'+(i+1),sourceRow:i+2,employeeCode:'SCALE'+String(i+1).padStart(3,'0'),employeeName:'Synthetic scale '+(i+1),status:'Existing',employeeId:i+1,message:''}));return reply({sharedHeaderDivisorSupported:true,templateApplicabilitySupported:true,source:{...source,rows:rows.map(r=>({sourceRow:r.sourceRow,cells:[{value:r.employeeName},{value:'10000'},{value:'30'},{value:'30'},{value:'1200'},{value:'8800'}]}))},employees:rows.map(r=>({id:r.employeeId,employeeCode:r.employeeCode,employeeName:r.employeeName})),roster:{rows,missingEmployees:[]},components:[],workWeeks:[],workLocations:[],defaults:{department:'Synthetic',attendanceSource:'Synthetic scale source; no database',notes:[]}})}
  if(p.endsWith('/context') && p.includes('payroll-setup') && page.url().includes('legacy=1')) {const rows=batch.rows.slice(0,5).map((row,i)=>({...row,employment:{...row.employment,employeeId:i<2?i+1:null,employeeCode:row.employeeCode,blockedForMonth:false,identityReviewRequired:i===4}})),rowIds=rows.map(r=>r.id),result={importId:'legacy-synthetic-receipt',employeeIds:[5,3,1,4,2],createdCount:0,updatedCount:5,templateId:'synthetic-template',attendanceGroupId:1,replayed:true};if(page.url().includes('transfer=1'))Object.assign(result,{transfer:{version:1,tolerance:0.000001,components:[{code:'BASIC',category:'Earning'},{code:'PF',category:'Deduction'},{code:'EMPLOYER_PF',category:'Benefit'}],templates:[],formulas:[],preparationStatus:'Synthetic Applied receipt',parityStatus:'Not certified'},transferRows:rows.map((r,i)=>({rowId:r.id,employeeId:i+1,payrollDays:30,presentDays:30,payableDays:30,sourceNet:8800,lines:[{code:'BASIC',sourceAmount:10000,calculatedAmount:10000},{code:'PF',sourceAmount:1200,calculatedAmount:1200},{code:'EMPLOYER_PF',sourceAmount:1300,calculatedAmount:1300}]}))});return reply({calculationOnlySimulationSupported:true,source,components:[],workWeeks:[],workLocations:[],employees:employees.slice(0,5).map(e=>({id:e.id,employeeCode:e.employeeCode,employeeName:e.firstName+' '+e.lastName})),roster:{rows:rows.map((row,i)=>({rowId:row.id,sourceRow:row.sourceRow,employeeCode:row.employeeCode,employeeName:row.employeeName,status:'Applied',employeeId:i+1})),missingEmployees:[]},lastImport:{importId:result.importId,requestId:'synthetic-request',status:'Applied',result,savedRequest:{batchId:batch.id,rowIds,templateName:'Legacy',effectiveFrom:'2026-09-01',components:[],employeeDecisions:rowIds.map((rowId,i)=>({rowId,matchedEmployeeId:i+1,allowCreate:false})),editableDefaults:{},payrollDaysColumnIndex:2,payableDaysColumnIndex:3,workWeek:'Sunday off'}}})}
  if (p.endsWith('/context') && p.includes('payroll-setup')) return reply({ source:page.url().includes('shared=1')?sharedHeaderSource:source,sharedHeaderDivisorSupported:true, templateApplicabilitySupported:true, employees: employees.map(e => ({ id: e.id, employeeCode: e.employeeCode, employeeName: `${e.firstName} ${e.lastName}`, workLocationId: 1 })), roster: { rows: batch.rows.map(row => ({ rowId: row.id, sourceRow: row.sourceRow, employeeName: row.employeeName, employeeCode: row.employeeCode, status: row.employment.identityReviewRequired ? 'Review' : 'Existing', employeeId: row.employment.employeeId, message: '' })), missingEmployees: [] }, components: [], workWeeks: [{ value: 'Friday off', label: 'Friday off' }], workLocations: [{ id: 1, name: 'Synthetic office' }], defaults: { department: 'Operations', designation: 'Executive', grade: 'G1', workWeek: 'Friday off', attendanceCycleStartDay: 1, attendanceCycleEndDay: 30, payrollReportGenerationDay: 3, attendanceSource: 'Existing client policy: Operations', notes: ['Existing settings retained'] } })
  if (p.endsWith('/delivery-status')) return reply({ items: batch.rows.map(row => ({ rowId: row.id, status: 'Pending', email: '', canSend: true, message: '' })) })
  if (p.endsWith('/mail-jobs')) return reply({ items: [] })
  if (p === '/api/excel-payslips/batches') return reply([{ ...batch, rows: undefined, rowCount: batch.rows.length }])
  if (p === `/api/excel-payslips/batches/${batch.id}`) return reply(batch)
  return reply([])
})
async function openBatch(suffix='') {
  const fixtureUrl = new URL('/bootstrap-fixture' + suffix, origin)
  fixtureUrl.searchParams.set('clientId', '33')
  await page.goto(fixtureUrl.href)
  await page.getByRole('button', { name: 'Batch history', exact: true }).click()
  await page.getByRole('button', { name: 'Open batch', exact: true }).click()
  await expect(page.getByText('Employee 001', { exact: true })).toBeVisible()
}
try {
  if(process.env.HRMS_TEST_REVIEW_SCALE_ONLY!=='1'){
  await openBatch()
  await expect(page.locator('tr.employee-exit-record').filter({ hasText: 'Employee 002' })).toBeVisible()
  await expect(page.locator('tr').filter({ hasText: 'Employee 002' }).getByRole('button', { name: 'Preview', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: /Email batch \(25\)/ })).toBeEnabled()
  checks.push('Future exited row stays visible red; preview and batch email eligibility exclude it')
  await page.locator('tr').filter({ hasText: 'Employee 003' }).getByRole('checkbox').check()
  await expect(page.getByRole('button', { name: 'Mark as resigned', exact: true })).toBeDisabled()
  await page.locator('tr').filter({ hasText: 'Employee 003' }).getByRole('checkbox').uncheck()
  await page.locator('tr').filter({ hasText: 'Employee 001' }).getByRole('checkbox').check()
  await page.getByRole('button', { name: 'Configure payroll', exact: true }).click()
  await page.getByRole('button', { name: 'Configure next 1 existing', exact: true }).click()
  await expect(page.getByText('Existing client policy: Operations', { exact: true }).first()).toBeVisible()
  await page.getByText('Saved batch financial source evidence',{exact:true}).click();const evidence=page.getByRole('region',{name:'Saved batch financial source evidence',exact:true});await expect(evidence).toContainText('B2*12%');await expect(evidence).not.toContainText('Employee 001')
  await page.locator('.ant-select').filter({has:page.getByRole('combobox',{name:'Payroll divisor source',exact:true})}).click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({hasText:'Fixed days'}).click();await expect(page.getByRole('spinbutton',{name:'Reviewed fixed payroll days',exact:true})).toHaveValue('');await page.getByRole('spinbutton',{name:'Reviewed fixed payroll days',exact:true}).fill('30')
  await page.getByText('Employee Master matches', { exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Payroll setup Default department' })).toHaveValue('Operations')
  await page.getByText('Client defaults and master creation', { exact: true }).click()
  await expect(page.getByRole('checkbox', { name: /Preserve existing attendance/ })).toBeChecked()
  await page.getByRole('button', { name: 'Close', exact: true }).last().click()
  await page.getByRole('button', { name: 'Close', exact: true }).last().click()
  await expect(page.locator('.excel-payroll-setup')).not.toBeVisible()
  checks.push('Configure payroll preloads this client defaults and preserves existing memberships')
  await page.getByRole('button', { name: 'Mark as resigned (1)', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Mark selected employees as resigned', exact: true })
  await expect(dialog.getByRole('textbox', { name: 'Last working date', exact: true })).toHaveValue('2026-09-30')
  await dialog.getByRole('textbox', { name: 'Last working date' }).fill('2026-09-30')
  await dialog.getByRole('button', { name: /Mark as resigned/ }).click()
  await expect(dialog).toContainText('Review existing payroll')
  rejectExit = false
  await dialog.getByRole('button', { name: /Mark as resigned/ }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.locator('tr.employee-exit-record').filter({ hasText: 'Employee 001' })).toBeVisible()
  await expect(page.locator('tr').filter({ hasText: 'Employee 001' }).getByRole('button', { name: 'Preview', exact: true })).toBeEnabled()
  checks.push('Required date, server validation, success refresh and historical-month preview verified with mocked writes')
  for (const [width, height] of [[1366, 768], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    await page.getByText('Cards', { exact: true }).click()
    await expect(page.locator('.shared-record-cards article.employee-exit-record')).toHaveCount(2)
    await page.getByRole('checkbox', { name: 'Select record row-4' }).check()
    await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeEnabled()
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false)
    await page.screenshot({ path: path.join(output, `excel-cards-${width}.png`), fullPage: true })
    await page.getByText('Table', { exact: true }).click()
    const body = page.locator('.ant-table-body').first()
    await body.evaluate(el => { el.scrollTop = el.scrollHeight; el.scrollLeft = el.scrollWidth })
    await expect(page.getByRole('button', { name: 'Batch history', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `excel-table-${width}.png`), fullPage: true })
  }
  checks.push('Card selection, red exit cards, bounded table scrolling and persistent actions at desktop/tablet/mobile')
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(origin + '/bootstrap-fixture?master=1')
  await expect(page.getByText('TEST001', { exact: true })).toBeVisible()
  await expect(page.locator('tr.employee-exit-record')).toHaveCount(2)
  await page.getByRole('button', { name: 'Select employees', exact: true }).click()
  const departed = page.locator('tr').filter({ hasText: 'TEST001' })
  await expect(departed.getByRole('checkbox')).toBeDisabled()
  await page.locator('tr').filter({ hasText: 'TEST003' }).getByRole('checkbox').check()
  await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeEnabled()
  await page.getByText('Card view', { exact: true }).click()
  await expect(page.locator('.recruitment-record-card.employee-exit-record')).toHaveCount(2)
  await page.screenshot({ path: path.join(output, 'master-cards.png'), fullPage: true })
  for (const [width, height] of [[768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false)
    await expect(page.getByRole('button', { name: 'Mark as resigned (1)', exact: true })).toBeInViewport()
    await page.screenshot({ path: path.join(output, `master-cards-${width}.png`), fullPage: true })
    await page.getByText('Table view', { exact: true }).click()
    await expect(page.locator('tr.employee-exit-record')).toHaveCount(2)
    await page.screenshot({ path: path.join(output, `master-table-${width}.png`), fullPage: true })
    await page.getByText('Card view', { exact: true }).click()
  }
  checks.push('Employee Master preserves exited history and disables selecting already resigned employees in table/card views')
  await page.goto(origin+'/bootstrap-fixture?native=1')
  const transferPanel=page.locator('section[aria-label="Transfer Batch review"]')
  await expect(transferPanel).toContainText('Transfer from this saved batch')
  const chooseNative=async(label,value)=>{await page.locator('.ant-select').filter({has:page.getByRole('combobox',{name:label,exact:true})}).click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({hasText:value}).click()}
  const reviewNative=async()=>{
    await page.getByLabel('Attendance proof source cycle start',{exact:true}).fill('2026-09-01');await page.getByLabel('Attendance proof source cycle end',{exact:true}).fill('2026-09-30')
    await chooseNative('Attendance proof payable meaning','Already payable — includes any approved paid credits; add no CL/off days')
    await chooseNative('Attendance proof present meaning','Actual worked/present units, separate from payable credits')
    await page.getByRole('checkbox',{name:/I reviewed these source meanings/}).check()
  }
  const refreshNative=page.getByRole('button',{name:'Refresh mocked saved-batch preview',exact:true})
  await page.getByRole('checkbox',{name:/I reviewed fixed\/monthly\/daily input semantics/}).check();await page.getByRole('checkbox',{name:/I reviewed this client's effective-period policy assumptions/}).check()
  await chooseNative('Attendance mapping proof mode','Reviewed source seed — native attendance/Employee proof pending');await reviewNative();await refreshNative.click()
  await expect(transferPanel).toContainText('Reviewed source seed — native pending');await expect(transferPanel).toContainText('Employee payroll parity remains pending')
  await page.getByRole('spinbutton',{name:'Attendance proof actual present source column',exact:true}).fill('5')
  await expect(page.getByRole('checkbox',{name:/I reviewed these source meanings/})).not.toBeChecked()
  await page.getByRole('spinbutton',{name:'Attendance proof actual present source column',exact:true}).fill('4');await reviewNative()
  await chooseNative('Attendance mapping proof mode','Compare existing native attendance — keep native ledger unchanged');await reviewNative()
  await expect(transferPanel).toContainText('Turn off Import source-month attendance')
  await page.getByRole('checkbox',{name:'Fixture import source attendance',exact:true}).uncheck();await expect(page.getByRole('checkbox',{name:/I reviewed these source meanings/})).not.toBeChecked()
  await page.getByRole('checkbox',{name:/I reviewed these source meanings/}).check();await refreshNative.dblclick();await expect(transferPanel).toContainText('Native attendance inputs matched')
  await chooseNative('Transfer template 1000','Keep current assignments unchanged')
  await page.getByRole('button',{name:'Use reviewed template decisions, then refresh preview',exact:true}).click();await refreshNative.click();await expect(transferPanel).toContainText('no source transfer; parity unresolved')
  await chooseNative('Transfer template 1000','Create this reviewed variant')
  await page.getByRole('button',{name:'Use reviewed template decisions, then refresh preview',exact:true}).click();await refreshNative.click();await expect(transferPanel).toContainText('Native attendance inputs matched')
  await page.getByRole('button',{name:'Simulate changed saved batch version',exact:true}).click();await expect(page.getByRole('checkbox',{name:/I reviewed these source meanings/})).not.toBeChecked();await expect(transferPanel).toContainText('Saved source changed')
  for(const width of [1366,390]){await page.setViewportSize({width,height:844});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'native-mapping-'+width+'.png'),fullPage:true})}
  assert.ok(nativePreviewRequests.length>=4);assert.ok(nativePreviewRequests.every(body=>body.batchId===batch.id&&!('source' in body)))
  checks.push('Saved-batch native mapping, stale column/import/hash review, keep/new template choices and desktop/mobile checked using mocked preview only')

  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?native=1&multi=1')
  await refreshNative.click();await expect(transferPanel).toContainText('Template variant 1000 (1 source rows)');await expect(transferPanel).toContainText('Template variant 1001 (1 source rows)')
  await chooseNative('Transfer template 1000','Reuse unchanged: Synthetic exact client rules (500)')
  await chooseNative('Transfer template 1001','Create this reviewed variant')
  const useDecisions=page.getByRole('button',{name:'Use reviewed template decisions, then refresh preview',exact:true})
  await useDecisions.click();await refreshNative.click()
  let multiRequest=nativePreviewRequests.at(-1);assert.deepEqual(multiRequest.rowIds,['row-1','row-2']);assert.equal(multiRequest.templateDecisions.length,2)
  assert.equal(multiRequest.templateDecisions.find(d=>d.variantKey==='1000').existingTemplateId,'500');assert.equal(multiRequest.templateDecisions.find(d=>d.variantKey==='1001').createVariant,true)
  const priorMultiDecisions=JSON.stringify(multiRequest.templateDecisions);await refreshNative.dblclick();await expect.poll(()=>JSON.stringify(nativePreviewRequests.at(-1).templateDecisions)).toBe(priorMultiDecisions)
  await chooseNative('Transfer template 1001','Keep current assignments unchanged');await useDecisions.click();await refreshNative.click();multiRequest=nativePreviewRequests.at(-1)
  assert.equal(multiRequest.templateDecisions.find(d=>d.variantKey==='1000').existingTemplateId,'500');assert.equal(multiRequest.templateDecisions.find(d=>d.variantKey==='1001').keepExisting,true)
  await expect(transferPanel).toContainText('Excel parity unresolved');await expect(transferPanel).toContainText('Template variant 1000 (1 source rows): Reuse unchanged');await expect(transferPanel).toContainText('Template variant 1001 (1 source rows): Keep current assignments unchanged')
  for(const width of [1366,390]){await page.setViewportSize({width,height:844});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'multi-template-'+width+'.png'),fullPage:true})}
  await chooseNative('Transfer template 1001','Create this reviewed variant');await useDecisions.click();await refreshNative.click();assert.equal(nativePreviewRequests.at(-1).templateDecisions.find(d=>d.variantKey==='1001').createVariant,true)
  assert.equal(nativePreviewRequests.at(-1).templateDecisions.find(d=>d.variantKey==='1000').existingTemplateId,'500')
  await page.getByText('Template variant 1000 (1 source rows): Reuse unchanged',{exact:true}).scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'multi-template-390-choices.png'),fullPage:true})
  checks.push('Two synthetic saved-batch rule variants keep independent exact-reuse/new/keep choices across repeated preview and mobile changes; UI contract only, no database assignment/apply')
  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?native=1&scope=1');await refreshNative.click();
  const applicability=page.getByRole('region',{name:'Template applicability review',exact:true});
  await expect(applicability).toContainText('Split cohorts when location/category rules differ');
  const scopeAck=applicability.getByRole('checkbox',{name:/I reviewed these exact locations/});await expect(scopeAck).not.toBeChecked();
  assert.equal(nativePreviewRequests.at(-1).templateScope,undefined); // No policy/day/location default was invented.
  await chooseNative('Template location applicability','Only listed locations');await chooseNative('Template location IDs','Synthetic office (1)');await page.keyboard.press('Escape');
  await chooseNative('Template category applicability','All client categories (explicitly reviewed)');await chooseNative('Template divisor meaning','Calendar days in the reviewed cycle');await chooseNative('Template payable meaning','Verified payable units; paid credits already included');await chooseNative('Template policy basis','Reviewed saved-batch rules (interim; no statutory certification)');
  await page.getByRole('textbox',{name:'Template applicability through',exact:true}).fill('2026-09-30');await scopeAck.check();await expect(scopeAck).toBeChecked();
  await page.getByRole('textbox',{name:'Template applicability through',exact:true}).fill('2026-10-31');await expect(scopeAck).not.toBeChecked();await page.getByRole('textbox',{name:'Template applicability through',exact:true}).fill('2026-09-30');await scopeAck.check();
  await chooseNative('Transfer template 1000','Review applicability before reuse: Synthetic legacy exact rules (500)');await chooseNative('Template scope action 1000','I reviewed legacy applicability for this selected cohort');await useDecisions.click();await refreshNative.click();
  const scopeRequest=nativePreviewRequests.at(-1);assert.equal(scopeRequest.templateScopeReviewVersion,1);assert.equal(scopeRequest.batchId,batch.id);assert.deepEqual(scopeRequest.rowIds,['row-1']);assert.equal(scopeRequest.templateScope.reviewed,true);assert.equal(scopeRequest.templateScope.effectiveTo,'2026-09-30');assert.deepEqual(scopeRequest.templateScope.workLocationIds,[1]);assert.equal(scopeRequest.templateScope.policyBasis,'SavedSourceRules');assert.equal(scopeRequest.templateScope.divisorMeaning,'CalendarDays');assert.equal(scopeRequest.templateDecisions[0].scopeAction,'AttestLegacyScope');assert.ok(!('source' in scopeRequest));
  await expect(transferPanel).toContainText('Synthetic scope review received');await expect(transferPanel).toContainText('Employee payroll parity remains pending');
  await page.getByText('Recorded applicability evidence: Synthetic prior policy',{exact:true}).click();await expect(transferPanel).toContainText('DATED_POLICY:TEST-v1');await expect(transferPanel).toContainText('2026-08-31');
  await page.locator('.ant-select').filter({has:page.getByRole('combobox',{name:'Transfer template 1000',exact:true})}).click();await expect(page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).not.toContainText('Synthetic prior policy');await page.keyboard.press('Escape');await expect(page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).toHaveCount(0);
  for(const width of [1366,390]){await page.setViewportSize({width,height:900});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await applicability.scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'applicability-'+width+'.png'),fullPage:true})}
  await refreshNative.dblclick();assert.deepEqual(nativePreviewRequests.at(-1).templateScope,scopeRequest.templateScope);
  checks.push('Explicit saved-batch applicability fields, dirty review reset, unknown legacy attestation payload, recorded scope evidence and incompatible-policy reuse exclusion checked on desktop/mobile; mocked previews only');
  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?workspace=1')
  await page.getByRole('button',{name:'Open selected-scope overview',exact:true}).click()
  const overview=page.locator('.excel-payslip-overview')
  await expect(overview).toContainText(/no active-batch default is assumed/i);await expect(overview.getByRole('button',{name:'Preview selected scope (0)',exact:true})).toBeDisabled()
  const selectedControl=overview.locator('.ant-select').filter({has:page.getByRole('combobox',{name:'Overview selected batches',exact:true})})
  await selectedControl.click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({hasText:'Alpha-September.xlsx'}).click();await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content').filter({hasText:'Beta-September.xlsx'}).click();await page.keyboard.press('Escape')
  const scopePreview=overview.getByRole('button',{name:/Preview selected scope \(2\)/}),sourceTile=overview.locator('.ant-statistic').filter({hasText:'Filtered source rows'}).locator('.ant-statistic-content-value')
  await scopePreview.click();await expect(sourceTile).toHaveText('30');await expect(overview).toContainText('9 / 14 / 7');await expect(overview.locator('.ant-statistic').filter({hasText:'Total employees'})).toContainText('16')
  await expect(overview.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(25);await overview.locator('.ant-pagination-next button').click();await expect(overview.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(5)
  assert.equal(overviewRequests.at(-1).page,2);assert.equal(overviewRequests.at(-1).scopes.length,2)
  await chooseNative('Overview readiness','Ready');await scopePreview.click();await expect(sourceTile).toHaveText('9');await expect(overview.locator('.ant-table-tbody tr[data-row-key]')).toHaveCount(9)
  await overview.locator('input[aria-label="Overview search"]').fill('NO-SUCH-SYNTHETIC');await scopePreview.click();await expect(sourceTile).toHaveText('0');await expect(overview).toContainText('No rows match these filters.')
  await overview.locator('input[aria-label="Overview search"]').fill('');await chooseNative('Overview readiness','All readiness states');overviewFailNext=true;await scopePreview.click();await expect(overview).toContainText('Synthetic overview read failure');await scopePreview.click();await expect(sourceTile).toHaveText('30')
  for(const width of [1366,390]){await page.setViewportSize({width,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false);await expect.poll(async()=>overview.evaluate(el=>{const r=el.getBoundingClientRect();return r.left>=-1&&r.right<=innerWidth+1&&r.width<=innerWidth+1})).toBe(true);await page.screenshot({path:path.join(output,'overview-'+width+'.png'),fullPage:true})}
  await page.setViewportSize({width:1366,height:900});await overview.getByRole('button',{name:'Close',exact:true}).click();await page.getByRole('button',{name:'Open selected-scope overview',exact:true}).click();await expect(overview).toContainText('Current live count not loaded');await scopePreview.click();await expect(sourceTile).toHaveText('30')
  await overview.locator('.ant-table-tbody tr[data-row-key]').first().getByRole('button',{name:'Review Master Setup',exact:true}).click();await expect(page.getByRole('status')).toContainText('Master setup handoff 33 '+batch.id)
  checks.push('Explicit all-client scopes, ready/review/blocked counts, pagination/filter consistency, error retry, reopen and reviewed Master Setup handoff verified with synthetic data')
  await page.getByRole('button',{name:'Original workbooks',exact:true}).click();const originalsDrawer=page.getByRole('dialog').filter({has:page.getByText('Original workbooks',{exact:true})});await expect(originalsDrawer).toContainText('Original-workbook availability is unverified');await expect(originalsDrawer).not.toContainText('No retained original is linked to this batch.');await expect(originalsDrawer.getByRole('button',{name:'Download original workbook',exact:true})).toHaveCount(0);for(const width of [1366,390]){await page.setViewportSize({width,height:844});await expect(originalsDrawer).toContainText('Close and reopen to retry');await expect.poll(async()=>originalsDrawer.evaluate(el=>{const r=el.getBoundingClientRect();return r.width>0&&r.left>=-1&&r.right<=innerWidth+1&&r.width<=innerWidth+1})).toBe(true);await page.screenshot({path:path.join(output,'original-metadata-error-'+width+'.png'),fullPage:true})}await originalsDrawer.getByRole('button',{name:'Close',exact:true}).last().click();await page.getByRole('button',{name:'Original workbooks',exact:true}).click();await expect(originalsDrawer).toContainText(originalSha);await expect(originalsDrawer).not.toContainText('Original-workbook availability is unverified');checks.push('Original metadata HTTP500 stays unknown, never falsely absent; reopening recovers without original/source writes on desktop and mobile')
  const download=page.waitForEvent('download');await originalsDrawer.getByRole('button',{name:'Download original workbook',exact:true}).click();const downloaded=await download;assert.deepEqual(fs.readFileSync(await downloaded.path()),originalBytes)
  await originalsDrawer.getByRole('button',{name:'Close',exact:true}).last().click()
  await page.getByRole('button',{name:'Review / delete batch',exact:true}).click();const cleanup=page.getByRole('dialog').filter({has:page.getByText('Review batch cleanup',{exact:true})});await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toBeDisabled();await expect(cleanup).toContainText('Original attachment bytes and a minimal deletion audit receipt are retained')
  await chooseNative('Retained final version','Synthetic-final.xlsx');await cleanup.getByRole('button',{name:'Preview version evidence',exact:true}).click();await expect(cleanup).toContainText('Synthetic exact captured snapshot evidence')
  await cleanup.getByRole('button',{name:'Review and confirm supersession',exact:true}).click();await cleanup.getByRole('button',{name:'Cancel action',exact:true}).click();await expect(cleanup.getByLabel('Cleanup action confirmation ID',{exact:true})).toHaveCount(0)
  await cleanup.getByRole('button',{name:'Review and confirm supersession',exact:true}).click();await cleanup.getByLabel('Cleanup action confirmation ID',{exact:true}).fill(batch.id);await cleanup.getByRole('checkbox',{name:/I reviewed the original finance source and differences/}).check();await cleanup.getByRole('button',{name:'Confirm / retry this action',exact:true}).click();await expect(cleanup).toContainText('Reviewed supersession recorded')
  await cleanup.getByRole('button',{name:'Discard unqueued job',exact:true}).click();await cleanup.getByLabel('Cleanup action confirmation ID',{exact:true}).fill('job-1');await cleanup.getByRole('checkbox',{name:/I reviewed the exact object and authorize only this resolution/}).check();await cleanup.getByRole('button',{name:'Confirm / retry this action',exact:true}).click();await expect(cleanup).toContainText('Synthetic resolution retry');await cleanup.getByRole('button',{name:'Confirm / retry this action',exact:true}).click()
  await cleanup.getByLabel('Type the batch ID to confirm',{exact:true}).fill(batch.id);await cleanup.getByRole('checkbox',{name:/I reviewed the counts and want to permanently delete this batch/}).check();await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toBeEnabled();await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toHaveAttribute('aria-busy','false');await cleanup.getByRole('button',{name:/Permanently delete batch/}).click();await expect(cleanup).toContainText('New queued job appeared')
  await cleanup.getByRole('button',{name:'Refresh dependency preview',exact:true}).click();await expect(cleanup).toContainText('Active queued delivery job must finish');await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toBeDisabled()
  for(const width of [1366,390]){await page.setViewportSize({width,height:844});await expect.poll(async()=>cleanup.evaluate(el=>{const r=el.getBoundingClientRect();return r.left>=-1&&r.right<=innerWidth+1&&r.width<=innerWidth+1})).toBe(true);await page.screenshot({path:path.join(output,'lifecycle-blocked-'+width+'.png'),fullPage:true})}
  activeCleanupJob=false // Synthetic prerequisite completion only; no actual job/service action.
  await cleanup.getByRole('button',{name:'Refresh dependency preview',exact:true}).click();await expect(cleanup.getByLabel('Type the batch ID to confirm',{exact:true})).toHaveValue('');await cleanup.getByLabel('Type the batch ID to confirm',{exact:true}).fill(batch.id);await cleanup.getByRole('checkbox',{name:/I reviewed the counts and want to permanently delete this batch/}).check();await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toBeEnabled();await expect(cleanup.getByRole('button',{name:/Permanently delete batch/})).toHaveAttribute('aria-busy','false');await cleanup.getByRole('button',{name:/Permanently delete batch/}).click();await expect(page.getByRole('status')).toContainText('Synthetic deletion confirmed; originals/audit retained')
  checks.push('Original bytes, shared retention, legacy dependency review/cancel, separate confirmed resolutions, idempotent retry, stale active-job preview and mock-only deletion verified')

  await page.setViewportSize({width:1366,height:900});await openBatch('?shared=1');await page.locator('tr').filter({hasText:'Employee 001'}).getByRole('checkbox').check();await page.getByRole('button',{name:'Configure payroll',exact:true}).click();await page.getByRole('button',{name:'Configure next 1 existing',exact:true}).click()
  await expect(page.getByRole('spinbutton',{name:'Reviewed fixed payroll days',exact:true})).toHaveValue('30');await expect(page.getByRole('textbox',{name:'Reviewed payroll divisor source cell',exact:true})).toHaveValue('C1')
  await page.getByText('Saved batch financial source evidence',{exact:true}).click();const sharedEvidence=page.getByRole('region',{name:'Saved batch financial source evidence',exact:true});await expect(sharedEvidence).toContainText('Referenced header divisor input');await expect(sharedEvidence).toContainText('G2/$C$1*D2')
  await expect(sharedEvidence).toBeVisible();for(const width of [1366,390]){await page.setViewportSize({width,height:900});await sharedEvidence.getByRole('cell',{name:'C1',exact:true}).scrollIntoViewIfNeeded();await expect(sharedEvidence.getByRole('cell',{name:'C1',exact:true})).toBeVisible();await page.screenshot({path:path.join(output,'shared-divisor-source-'+width+'.png'),fullPage:true})};checks.push('Shared saved-header literal30 maps to C1 with original formula evidence and explicit review; no raw import, source edit, preview or Apply is executed')
  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?rounding=1');
  await page.getByRole('button',{name:'Preview calculations',exact:true}).click();
  const roundingPreview=page.locator('.excel-payroll-setup-preview');await expect(roundingPreview).toContainText('Payslip display and raw calculation are separate');await expect(roundingPreview).toContainText('Raw snapshot: 116.91499999999999');await expect(roundingPreview).toContainText('Same at 2dp; raw parity separate');await page.getByText('Synthetic person · component comparison',{exact:true}).click();
  await expect(roundingPreview).toContainText('Raw snapshot: 1.0049999999999997');await expect(roundingPreview).toContainText('Different at 2dp');await expect(roundingPreview.getByRole('cell',{name:'-0.005',exact:true})).toHaveCount(1);await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();
  for(const width of [1366,390]){await page.setViewportSize({width,height:900});await roundingPreview.getByRole('cell',{name:'ESIC',exact:true}).scrollIntoViewIfNeeded();await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'rounding-evidence-'+width+'.png'),fullPage:true})}
  checks.push('Actual Setup comparison renders existing payslip formatting, independent raw snapshot/delta and display-equality status; unknowns not zero; Apply disabled. One mocked response, no live preview.');
  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?legacy=1');
  const identityPreflight=page.getByRole('region',{name:'Saved payroll identity preflight'});await expect(identityPreflight).toContainText('2 of 5 saved source rows eligible');await expect(identityPreflight).toContainText('Blocked');
  await page.getByRole('button',{name:'Verify saved payroll',exact:true}).dblclick();await expect(page.getByText('Current Employee calculations — legacy source parity remains unverified',{exact:true})).toBeVisible();assert.deepEqual(savedVerificationRequests.map(r=>r.employeeId).sort(),[1,2]);
  const legacyResult=page.getByRole('table').filter({has:page.getByRole('columnheader',{name:'Excel in hand',exact:true})});await expect(legacyResult.getByRole('row')).toHaveCount(3);await expect(legacyResult).toContainText('Synthetic person 1');await expect(legacyResult).toContainText('Synthetic person 2');await expect(legacyResult).toContainText('Calculated; source component/attendance semantics require review');await page.getByText('Source row 2: current Employee component and attendance observations',{exact:true}).click();await expect(page.getByText('Employer PF',{exact:true})).toHaveCount(0);await expect(page.locator('details[open]').getByText(/EMPLOYER_PF \/ Benefit/)).toBeVisible();
  for(const width of [1366,390]){await page.setViewportSize({width,height:900});await expect(identityPreflight).toBeVisible();await expect.poll(async()=>page.getByRole('dialog').filter({has:identityPreflight}).evaluate(el=>{const r=el.getBoundingClientRect();return r.width>0&&r.left>=-1&&r.right<=innerWidth+1&&r.width<=innerWidth+1})).toBe(true);await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'saved-identity-verification-'+width+'.png'),fullPage:true})}
  checks.push('Legacy receipt verifies only 2/5 currently strict identities; reversed receipt order cannot pair rows; duplicate click produces two total Employee calls, blocked employees excluded, all component categories shown and net match never claims parity; desktop/mobile, mocks only');
  for(const incomplete of [false,true]){
    const beforeObservations=savedVerificationRequests.length;await page.goto(origin+'/bootstrap-fixture?legacy=1&transfer=1'+(incomplete?'&incomplete=1':''));
    await expect(page.getByRole('region',{name:'Saved payroll identity preflight'})).toContainText('2 of 5 saved source rows eligible');
    await page.getByRole('button',{name:'Verify saved payroll',exact:true}).dblclick();
    const details=page.locator('details').filter({has:page.locator('summary').filter({hasText:/^Employee 1:/})});await expect(details).toBeVisible();await details.locator('summary').click();
    const actual=page.getByRole('region',{name:'Current saved Employee calculation 1',exact:true});await expect(actual).toContainText('Calculation mode Employee; complete '+(incomplete?'No':'Yes'));
    await expect(actual).toContainText('cycle not recorded to not recorded');await expect(actual).toContainText('temporary day overrides No');await expect(actual).toContainText('PAYABLE_DAYS: 30');await expect(actual).toContainText('EMPLOYER_PF / Benefit');
    if(incomplete){await expect(actual).toContainText('Raw net -');await expect(details).toContainText('Not reconciled');await expect(actual).not.toContainText('Raw net 0.');}else await expect(actual).toContainText('Raw net 8800');
    assert.deepEqual(savedVerificationRequests.slice(beforeObservations).map(r=>r.employeeId).sort(),[1,2]);
    for(const width of [1366,768,390]){await page.setViewportSize({width,height:900});await expect(actual).toBeVisible();await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'saved-transfer-observation-'+(incomplete?'incomplete':'complete')+'-'+width+'.png'),fullPage:true})}
    checks.push('Saved transfer Employee observation '+(incomplete?'keeps unknown net and incomplete status':'shows exact raw net and employer lines')+'; missing dated provenance stays explicit, strict identities only, desktop/tablet/mobile and no Apply');
  }

  }
  await page.setViewportSize({width:1366,height:900});await page.goto(origin+'/bootstrap-fixture?reviewScale=1');await page.locator('button').filter({hasText:/^Preview calculations$/}).click();const scaleRegion=page.getByRole('region',{name:'Saved batch amount and rounding review',exact:true}),scalePager=scaleRegion.getByRole('navigation',{name:'Saved rounding evidence pages',exact:true});await expect(scaleRegion).toBeVisible();await expect(scaleRegion.locator('details')).toHaveCount(10);await expect(scalePager).toContainText('100 source rows');const scaleChoice=page.getByRole('combobox',{name:'Transfer template 1000',exact:true});await scaleChoice.press('ArrowDown');await scaleChoice.press('Enter');await page.getByRole('button',{name:'Use reviewed template decisions, then refresh preview',exact:true}).click();console.log(JSON.stringify({stage:'scale-preview-transition',roleCount:await page.getByRole('button',{name:'Preview calculations',exact:true}).count(),buttons:await page.locator('button').evaluateAll(es=>es.filter(e=>e.textContent?.trim()==='Preview calculations').map(e=>({text:e.textContent,disabled:e.disabled,html:e.outerHTML,hiddenAncestors:(()=>{const a=[];for(let n=e;n;n=n.parentElement)if(n.getAttribute('aria-hidden')==='true'||n.inert)a.push({tag:n.tagName,cls:n.className,ariaHidden:n.getAttribute('aria-hidden'),inert:n.inert});return a})()})))}));await page.locator('button').filter({hasText:/^Preview calculations$/}).click();await expect(scaleRegion).toBeVisible();
  const inputReviewCalls=nativePreviewRequests.length;
  const sourceMode=page.getByRole('combobox',{name:'Attendance mapping proof mode',exact:true});await sourceMode.press('ArrowDown');await sourceMode.press('Enter');
  const cycleStart=page.getByLabel('Attendance proof source cycle start',{exact:true}),cycleEnd=page.getByLabel('Attendance proof source cycle end',{exact:true});
  await expect(cycleStart).toBeVisible();await cycleStart.fill('2026-09-01');await cycleStart.press('Tab');await expect(cycleEnd).toBeVisible();await cycleEnd.fill('2026-09-30');await cycleEnd.press('Tab');
  const payableMeaning=page.getByRole('combobox',{name:'Attendance proof payable meaning',exact:true});await payableMeaning.press('ArrowDown');await payableMeaning.press('Enter');
  const presentMeaning=page.getByRole('combobox',{name:'Attendance proof present meaning',exact:true});if(await presentMeaning.count()){await presentMeaning.press('ArrowDown');await presentMeaning.press('Enter')}
  await page.getByRole('checkbox',{name:/I reviewed these source meanings/}).check();await expect(cycleStart).toHaveValue('2026-09-01');await expect(cycleEnd).toHaveValue('2026-09-30');await expect(scaleRegion).toBeVisible();await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();assert.equal(nativePreviewRequests.length,inputReviewCalls);
  await page.locator('button').filter({hasText:/^Preview calculations$/}).click();await expect.poll(()=>nativePreviewRequests.length).toBe(inputReviewCalls+1);const reviewedAttendance=nativePreviewRequests.at(-1).attendanceReview;
  assert.equal(reviewedAttendance.mode,'SourceSeed');assert.equal(reviewedAttendance.cycleStart,'2026-09-01');assert.equal(reviewedAttendance.cycleEnd,'2026-09-30');assert.equal(reviewedAttendance.payableMeaning,'AlreadyPayable');assert.equal(reviewedAttendance.reviewed,true);assert.equal(reviewedAttendance.sourceMonth,'2026-09');assert.equal(reviewedAttendance.sourceHash,'c'.repeat(64));assert.equal(nativePreviewRequests.at(-1).templateDecisions[0].existingTemplateId,'500');
  checks.push('Actual Setup retains complete multi-field source attendance and reviewed template decisions without intermediate requests; fresh server preview remains required; no Apply');
  const beforeScaleReviews=nativePreviewRequests.length;
  await page.getByRole('checkbox',{name:/I reviewed this client's effective-period policy assumptions/}).check();await expect(page.getByText('Review choices changed - refresh preview before Apply',{exact:true})).toBeVisible();await expect(scaleRegion).toBeVisible();
  for(let index=0;index<10;index++){await expect(scaleRegion.locator('details')).toHaveCount(10);for(const box of await scaleRegion.getByRole('checkbox',{name:/Review rounding source row/}).all())await box.check();if(index<9)await scalePager.locator('.ant-pagination-next').click()}
  console.log('Scale review:100 individual proof checks complete, no per-checkbox API calls');
  assert.equal(nativePreviewRequests.length,beforeScaleReviews);await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();await page.locator('button').filter({hasText:/^Preview calculations$/}).click();await expect(page.getByText('Review choices changed - refresh preview before Apply',{exact:true})).toHaveCount(0);assert.equal(Object.keys(nativePreviewRequests.at(-1).roundingReviews).length,100);await expect(scaleRegion.getByRole('checkbox',{name:'Review rounding source row 2',exact:true})).toBeChecked();await page.getByRole('checkbox',{name:'Review payroll setup',exact:true}).check();await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeEnabled();await scaleRegion.getByRole('checkbox',{name:'Review rounding source row 2',exact:true}).uncheck();await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();await expect(scaleRegion).toBeVisible();
  const scaleDate=page.getByLabel('Payroll setup Effective from',{exact:true});await scaleDate.fill('2026-10-01');await scaleDate.press('Tab');await expect(scaleRegion).toHaveCount(0);await page.locator('button').filter({hasText:/^Preview calculations$/}).click();await expect(scaleRegion.getByRole('checkbox',{name:'Review rounding source row 2',exact:true})).not.toBeChecked();await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();checks.push('100 source-bound reviews remain visible with at most10 evidence tables; no per-checkbox requests; fresh preview required for Apply, uncheck invalidates, changed effective date rejects old fingerprints; no Apply invoked');
  console.log('Scale review: fresh-preview Apply gate, uncheck invalidation and changed-date proof checks complete');
  for(const width of [1366,768,390]){await page.setViewportSize({width,height:900});await expect(scaleRegion.locator('details')).toHaveCount(10);for(let index=0;index<9;index++)await scalePager.locator('.ant-pagination-next').click();const lastProofBox=scaleRegion.getByRole('checkbox',{name:'Review rounding source row 101',exact:true});await lastProofBox.scrollIntoViewIfNeeded();await expect(lastProofBox).toBeInViewport();await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)).toBe(false);await page.screenshot({path:path.join(output,'scale-review-'+width+'.png'),fullPage:true});for(let index=0;index<9;index++)await scalePager.locator('.ant-pagination-prev').click()}
  await page.goto(origin+'/bootstrap-fixture?reviewScale=1&unexplained=1');await page.locator('button').filter({hasText:/^Preview calculations$/}).click();await expect(scaleRegion).toContainText('Unexplained difference: 1');for(let index=0;index<9;index++)await scalePager.locator('.ant-pagination-next').click();await expect(scaleRegion).toContainText('Synthetic unexplained difference remains blocked');await expect(scaleRegion.getByRole('checkbox',{name:'Review rounding source row 101',exact:true})).toHaveCount(0);await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled();checks.push('Desktop/tablet/mobile100-row proof pagination reaches last record with no page overflow; off-page unexplained mismatch stays counted and cannot be acknowledged or Applied; network isolated');
  assert.deepEqual(errors, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, consoleErrors, mockedWrites: writes.length, actualDatabaseWrites: 0, actualEmails: 0 }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, output }))
} catch (error) {
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
  fs.writeFileSync(path.join(output,'failure-debug.json'),JSON.stringify({errors,consoleErrors,overviewRequests,nativePreviewRequests,cleanupRequests},null,2))
  console.error('Synthetic failure artifacts: '+output)
  console.error(errors)
  console.error(JSON.stringify({dialogs:await page.getByRole('dialog').count(),overviewRequests:overviewRequests.length,cleanupRequests:cleanupRequests.length}))
  throw error
} finally { fs.rmSync(fixturePath, { force: true }); await browser.close() }
