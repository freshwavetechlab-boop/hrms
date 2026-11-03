import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
import {quickPreparationFixture,reviewedQuickBasis,quickPreview} from '../../tools/fixtures/excel-payroll-quick-prepare.mjs'
const modules=new Map()
function load(name){if(modules.has(name))return modules.get(name);const exports={};modules.set(name,exports);new Function('exports','require',ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,p=>load(p.replace('./','')));return exports}
const {proposeExcelPayrollSetup}=load('excelPayrollSetup')
const {quickPayrollGroups,quickPayrollRequest,selectQuickPayrollTemplates,quickAttendanceConflict,sameQuickPayrollPreview,completedQuickPayroll}=load('excelPayrollQuickPrepare')
function fixture(count=2){const f=quickPreparationFixture(count);f.context.clientConfiguration=reviewedQuickBasis(proposeExcelPayrollSetup(f.batch,f.context,f.batch.rows.map(r=>r.id)).request,f.columns);return f}

test('compatible client setup generates current-month candidates without copying old approvals or salary amounts',()=>{
 const {batch,context}=fixture(),before=structuredClone({batch,context}),ids=batch.rows.map(r=>r.id)
 const q=quickPayrollRequest(batch,context,ids,false)
 assert.equal(q.effectiveFrom,'2026-10-01');assert.equal(q.importAttendance,true);assert.equal(q.presentDaysColumnIndex,4)
 assert.equal(q.attendanceReview.sourceHash,'a'.repeat(64));assert.equal(q.attendanceReview.cycleEnd,'2026-10-31')
 assert.deepEqual(q.roundingReviews,{});assert.deepEqual(q.templateDecisions,[]);assert.equal(q.employeeDecisions[0].matchedEmployeeId,1)
 assert.deepEqual({batch,context},before)
})
test('first setup, different clients/layouts, unknown source meanings and special duty units stay in explicit review',()=>{
 for(const change of [f=>f.context.clientConfiguration=null,f=>f.context.clientConfiguration.clientId=99,f=>f.context.source.columns[3].sourceHeader='Different basis',f=>f.context.clientConfiguration.savedRequest.attendanceReview.reviewed=false,f=>f.context.clientConfiguration.savedRequest.reviewedExtraDutyUnits=true,f=>delete f.context.quickPreparation]){
  const f=fixture();change(f);assert.throws(()=>quickPayrollRequest(f.batch,f.context,['row-1'],false))
 }
})
test('new employees need one explicit include choice; ambiguous identities and payable-only presence remain blocked',()=>{
 const f=fixture();f.context.roster.rows[1].status='New';f.context.roster.rows[1].employeeId=null
 assert.throws(()=>quickPayrollRequest(f.batch,f.context,['row-2'],false),/new employees/)
 assert.equal(quickPayrollRequest(f.batch,f.context,['row-2'],true).employeeDecisions[0].allowCreate,true)
 f.context.roster.rows[1].status='Review';assert.throws(()=>quickPayrollRequest(f.batch,f.context,['row-2'],true),/identities/)
 f.context.clientConfiguration.savedRequest.presentDaysColumnIndex=undefined;f.context.clientConfiguration.savedRequest.attendanceReview.presentMeaning='NotMapped'
 assert.throws(()=>quickPayrollRequest(f.batch,f.context,['row-1'],false),/actual Present/)
})
test('saved extra-duty salary meaning can be reviewed again only while retaining native attendance',()=>{
 const f=fixture();f.context.clientConfiguration.savedRequest.reviewedExtraDutyUnits=true
 const q=quickPayrollRequest(f.batch,f.context,['row-1'],false,false)
 assert.equal(q.reviewedExtraDutyUnits,true);assert.equal(q.importAttendance,false)
 assert.match(q.attendanceReview.boundSourceInputs,/extraDuty:1$/)
 assert.throws(()=>quickPayrollRequest(f.batch,f.context,['row-1'],false,true),/special payable units/)
})
test('saved cohort scopes are preserved while remaining employees are grouped automatically',()=>{
 const f=fixture(205);f.context.appliedSetups=[['row-1','row-2']]
 assert.deepEqual(quickPayrollGroups(f.context,f.batch.rows.map(r=>r.id)).map(ids=>ids.length),[2,100,100,3])
 assert.throws(()=>quickPayrollGroups(f.context,['row-1']),/part of a saved configuration/)
 assert.throws(()=>quickPayrollGroups(f.context,['row-3','row-3']),/Refresh/)
 assert.throws(()=>quickPayrollGroups(f.context,[]),/Refresh/)
})
test('quick prepare reuses an exact template and never auto-creates or silently updates differing rules',()=>{
 const f=fixture(),q=quickPayrollRequest(f.batch,f.context,['row-1'],false),p=quickPreview(q)
 assert.equal(selectQuickPayrollTemplates(q,p).templateDecisions[0].existingTemplateId,'template-1')
 p.transfer.templates[0].candidates[0].exact=false;assert.throws(()=>selectQuickPayrollTemplates(q,p),/rules or template applicability changed/)
 p.transfer.sourceHash='d'.repeat(64);assert.throws(()=>selectQuickPayrollTemplates(q,p),/source changed/)
})
test('existing monthly differences and dated attendance cannot be overwritten by the quick route',()=>{
 const f=fixture(),q=quickPayrollRequest(f.batch,f.context,['row-1'],false),p=quickPreview(q)
 f.context.quickPreparation.monthlyAttendance=[{employeeId:1,workingDays:30,presentDays:24,payableDays:24,sourceType:'Monthly'}]
 assert.equal(quickAttendanceConflict(f.context,q,p),'')
 f.context.quickPreparation.monthlyAttendance[0].presentDays=null;assert.match(quickAttendanceConflict(f.context,q,p),/differs/)
 f.context.quickPreparation.dailyEmployeeIds=[1];assert.match(quickAttendanceConflict(f.context,q,p),/Dated attendance/)
 q.importAttendance=false;assert.equal(quickAttendanceConflict(f.context,q,p),'')
})
test('concurrency hashes may refresh but changed amounts, source proof or employee plans pause preparation',()=>{
 const f=fixture(),q=quickPayrollRequest(f.batch,f.context,['row-1'],false),p=quickPreview(q),next=structuredClone(p)
 next.planHash='d'.repeat(64);next.transfer.templates[0].candidates[0].recordedScopes=[{sourceMonth:'2026-10'}]
 assert.equal(sameQuickPayrollPreview(p,next),true)
 next.rows[0].net++;assert.equal(sameQuickPayrollPreview(p,next),false)
 next.rows[0].net--;next.transfer.sourceHash='d'.repeat(64);assert.equal(sameQuickPayrollPreview(p,next),false)
})
test('completed work is recovered only from the same selection, unchanged source and matching request receipt',()=>{
 const f=fixture(),q=quickPayrollRequest(f.batch,f.context,['row-1'],false)
 f.context.lastImport={status:'Applied',sourceChanged:false,requestId:'attempt-1',savedRequest:q,result:{importId:'saved',employeeIds:[1]}}
 assert.equal(completedQuickPayroll(f.context,['row-1'],'attempt-1').importId,'saved')
 assert.equal(completedQuickPayroll(f.context,['row-1'],'other'),null)
 assert.equal(completedQuickPayroll(f.context,['row-2']),null)
 f.context.lastImport.sourceChanged=true;assert.equal(completedQuickPayroll(f.context,['row-1']),null)
})
