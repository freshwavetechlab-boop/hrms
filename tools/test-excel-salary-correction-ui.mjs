// Synthetic browser regression: all API requests are intercepted; no production writes.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

if (!process.env.HRMS_PLAYWRIGHT_MODULE) throw Error('Use the existing project Playwright runtime.')
const { chromium, expect } = await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
const origin = 'http://localhost:5173'
const output = path.join(os.tmpdir(), 'frevo-salary-correction-ui-' + Date.now())
fs.mkdirSync(output, { recursive: true })
const fixture = '.codex-salary-correction-' + crypto.randomUUID() + '.tsx'
const fixturePath = fileURLToPath(new URL('../payroll-ui/' + fixture, import.meta.url))
const batch = { id: 'a'.repeat(32), clientId: 33, month: '2026-09', clientName: 'Synthetic', sourceFileName: 'Synthetic.xlsx', sheetName: 'Salary', headerRow: 1,
  rows: [{ id: 'source-row', sourceRow: 19, employeeCode: 'SYN-1', employeeName: 'Synthetic employee', email: '', information: [], earnings: [{label:'Basic',amount:10000}], deductions: [], employerContributions: [], netPay:10000, warnings: [], employment:{employeeId:1,employeeCode:'SYN-1',identityReviewRequired:false,blockedForMonth:false} }] }
const source = { sheetName:'Salary',headerRow:1,columnCount:4,columns:['Name','Month days','Payable days','Basic'].map((label,i)=>({columnIndex:i,sourceHeader:label,label,kind:i===0?'employeeName':i===3?'earning':'info'})),rows:[{sourceRow:19,cells:[{value:'Synthetic employee'},{value:'30'},{value:'30'},{value:'10000'}]}] }
const parent = 'b'.repeat(32), oldRequest = '00000000-0000-4000-8000-000000000001'
const request = { batchId:batch.id,rowIds:['source-row'],templateName:'Synthetic old template',effectiveFrom:'2026-09-01',components:[],employeeDecisions:[{rowId:'source-row',matchedEmployeeId:1,allowCreate:false}],editableDefaults:{},payrollDaysColumnIndex:1,payableDaysColumnIndex:2,presentDaysColumnIndex:2,workWeek:'Sunday off',attendanceCycleStartDay:1,attendanceCycleEndDay:30,payrollReportGenerationDay:3,importAttendance:true }
const result = {importId:parent,employeeIds:[1],createdCount:0,updatedCount:1,templateId:'original',attendanceGroupId:1,replayed:true}
const contextData = supported => ({ salaryCorrectionSupported:supported,sharedHeaderDivisorSupported:true,templateApplicabilitySupported:true,calculationOnlySimulationSupported:true,source,components:[],employees:[{id:1,employeeCode:'SYN-1',employeeName:'Synthetic employee'}],workWeeks:[{value:'Sunday off',label:'Sunday off'}],workLocations:[],roster:{rows:[{rowId:'source-row',sourceRow:19,employeeCode:'SYN-1',employeeName:'Synthetic employee',employeeId:1,status:'Applied',message:'Existing receipt'}],missingEmployees:[]},appliedSetups:[['source-row','other-source-row']],lastImport:{importId:parent,requestId:oldRequest,status:'Applied',result,savedRequest:request} })
fs.writeFileSync(fixturePath, `import React from 'react';import ReactDOM from 'react-dom/client';import Workspace from '/src/components/ExcelPayrollSetupWorkspace.tsx';import '/src/AppShell.css';const batch=${JSON.stringify(batch)};ReactDOM.createRoot(document.getElementById('root')).render(<Workspace clientId={33} batch={batch} rowIds={['source-row']} onClose={()=>{}} onApplied={()=>{throw Error('Unexpected Apply')}}/>);`)
const html = `<!doctype html><html><body><div id="root"></div><script type="module">import RefreshRuntime from '/@react-refresh';RefreshRuntime.injectIntoGlobalHook(window);window.$RefreshReg$=()=>{};window.$RefreshSig$=()=>type=>type;window.__vite_plugin_react_preamble_installed__=true;await import('/${fixture}');</script></body></html>`
const browser = await chromium.launch({headless:true})
const page = await browser.newPage({viewport:{width:1366,height:900},serviceWorkers:'block'})
const previewBodies=[],contextSelections=[],unexpected=[]
await page.routeWebSocket('**/*',socket=>socket.close())
await page.route('**/*',async route=>{
 const req=route.request(),url=new URL(req.url())
 if(!['localhost','127.0.0.1'].includes(url.hostname)){unexpected.push('Nonlocal request');return route.abort()}
 if(req.isNavigationRequest())return route.fulfill({contentType:'text/html',body:html})
 if(url.pathname.startsWith('/api/')){
  const reply=data=>route.fulfill({contentType:'application/json',body:JSON.stringify(data)})
  if(url.pathname.includes('payroll-setup/')&&url.pathname.endsWith('/context')&&req.method()==='GET'){
   contextSelections.push([...url.searchParams.getAll('rowId'),...url.searchParams.getAll('rowIds')]);return reply(contextData(!page.url().includes('unsupported=1')))
  }
  if(url.pathname.includes('payroll-setup/')&&url.pathname.endsWith('/preview')&&req.method()==='POST'){
   previewBodies.push(req.postDataJSON());return reply({canApply:false,planHash:'c'.repeat(64),coverage:'Synthetic preview only',rows:[],issues:[{severity:'Error',message:'Synthetic review incomplete; Apply intentionally blocked.'}]})
  }
  unexpected.push(req.method()+' '+url.pathname);return route.abort()
 }
 if(url.origin!==origin){unexpected.push('Unexpected non-UI local request');return route.abort()}
 return route.continue()
})
try {
 await page.goto(origin+'/?correctionFixture=1&unsupported=1')
 await page.getByRole('button',{name:'Applied (1)',exact:true}).click()
 await expect(page.getByRole('button',{name:'Review salary correction',exact:true})).toHaveCount(0)
 await page.goto(origin+'/?correctionFixture=1')
 await page.getByRole('button',{name:'Applied (1)',exact:true}).click()
 await page.getByRole('button',{name:'Review salary correction',exact:true}).click()
 await expect(page.getByText('Setup saved: 0 employees created, 1 updated',{exact:true})).toBeVisible()
 await page.getByText('Salary month and attendance',{exact:true}).click()
 await page.getByRole('checkbox',{name:'Correct a previously applied salary setup',exact:true}).check()
 await expect(page.getByLabel('Original Applied receipt for salary correction')).toHaveValue(parent)
 await expect(page.getByRole('button',{name:'Preview calculations',exact:true})).toBeEnabled()
 await page.getByRole('button',{name:'Preview calculations',exact:true}).click()
 await expect(page.getByText('Synthetic review incomplete; Apply intentionally blocked.',{exact:true})).toBeVisible()
 assert.equal(previewBodies.length,1)
 const corrected=previewBodies[0]
 assert.equal(corrected.correctionOfImportId,parent);assert.deepEqual(corrected.rowIds,['source-row'])
 for(const flag of ['importAttendance','importPayableOnlyAttendance','overrideSourcePayableAttendance','createMissingWorkLocations','createMissingDropdownValues']) assert.equal(corrected[flag],false)
 assert.equal('presentDaysColumnIndex' in corrected,false)
 assert.equal(corrected.preserveExistingAttendancePolicies,true)
 assert.ok(contextSelections.some(ids=>ids.length===1&&ids[0]==='source-row'))
 await expect(page.getByRole('button',{name:'Apply reviewed Transfer Batch',exact:true})).toBeDisabled()
 await page.screenshot({path:path.join(output,'salary-correction-review.png'),fullPage:true})
 assert.deepEqual(unexpected,[])
 fs.writeFileSync(path.join(output,'outcome.json'),JSON.stringify({passed:true,checks:['Unsupported API hides correction entry','Applied multi-row receipt opens selected row only','Correction leaves receipt view and re-enables fresh preview','Parent receipt retained, attendance/location writes disabled','Incomplete review blocks Apply','No unmocked API or external request'],productionWrites:0,previewRequests:previewBodies.length,oldRequestPreserved:oldRequest},null,2))
 console.log(JSON.stringify({passed:true,output,productionWrites:0}))
} catch(error) {await page.screenshot({path:path.join(output,'failure.png'),fullPage:true}).catch(()=>{});console.error(JSON.stringify({passed:false,output,error:String(error),unexpected}));process.exitCode=1}
finally {await browser.close();fs.unlinkSync(fixturePath)}
