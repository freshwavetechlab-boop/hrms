import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
import {quickPreparationFixture,quickPreview} from '../../tools/fixtures/excel-payroll-quick-prepare.mjs'

const modules=new Map()
function load(name){
 if(modules.has(name))return modules.get(name)
 const exports={};modules.set(name,exports)
 new Function('exports','require',ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,path=>load(path.replace('./','')))
 return exports
}
const {proposeExcelPayrollSetup}=load('excelPayrollSetup')
const {attendanceProofBinding}=load('excelPayrollPayableOnlyAttendance')
const {masterWorkspaceDefaults,masterWorkspaceTemplateDecisions,reviewedMasterWorkspace,masterWorkspaceIssues,sourceMonthEnd}=load('excelPayrollMasterWorkspace')

function fixture(count=3){
 const data=quickPreparationFixture(count)
 data.request=proposeExcelPayrollSetup(data.batch,data.context,data.batch.rows.map(row=>row.id)).request
 return data
}
function freeze(value){if(value&&typeof value==='object'){Object.values(value).forEach(freeze);Object.freeze(value)}return value}
function candidate(id,overrides={}){return{id,name:'Synthetic salary '+id,exact:true,configurationExact:true,differences:[],details:[],...overrides}}

test('visible default proposals preserve all source amounts and do not pre-approve Master writes',()=>{
 const data=fixture(),snapshot=structuredClone(data)
 freeze(data)
 const request=masterWorkspaceDefaults(data.batch,data.context,data.request)
 assert.equal(request.importAttendance,false)
 assert.equal(request.templateScope.reviewed,false)
 assert.equal(request.reviewedFixedInputs,undefined)
 assert.equal(request.reviewedPolicyScope,undefined)
 assert.equal(request.attendanceReview,undefined)
 assert.deepEqual(request.templateDecisions,undefined)
 assert.deepEqual(request.components,data.request.components)
 assert.deepEqual(data,snapshot)
})

test('existing and applied source identities are reused while new and uncertain employees remain distinguishable',()=>{
 const {batch,context,request}=fixture(4)
 context.roster.rows[0]={...context.roster.rows[0],status:'Existing',employeeId:11}
 context.roster.rows[1]={...context.roster.rows[1],status:'Applied',employeeId:12}
 context.roster.rows[2]={...context.roster.rows[2],status:'New',employeeId:null}
 context.roster.rows[3]={...context.roster.rows[3],status:'Review',employeeId:99}
 request.employeeDecisions[0]={rowId:'row-1',allowCreate:false}
 request.employeeDecisions[1]={rowId:'row-2',allowCreate:false}
 request.employeeDecisions[2]={rowId:'row-3',allowCreate:false}
 request.employeeDecisions[3]={rowId:'row-4',matchedEmployeeId:99,allowCreate:true}
 const next=masterWorkspaceDefaults(batch,context,request)
 assert.deepEqual(next.employeeDecisions.slice(0,2),[{rowId:'row-1',matchedEmployeeId:11,allowCreate:false},{rowId:'row-2',matchedEmployeeId:12,allowCreate:false}])
 assert.equal(next.employeeDecisions[2].allowCreate,true)
 assert.equal(next.employeeDecisions[3].allowCreate,false)
 assert.equal(next.employeeDecisions[3].matchedEmployeeId,undefined)
})

test('a valid operator-selected Master match survives an unresolved roster hint without granting creation',()=>{
 const {batch,context,request}=fixture()
 context.roster.rows[0]={...context.roster.rows[0],status:'Review',employeeId:null}
 request.employeeDecisions[0]={rowId:'row-1',matchedEmployeeId:2,allowCreate:false}
 const before=structuredClone({batch,context,request})
 const next=masterWorkspaceDefaults(batch,context,request)
 assert.deepEqual(next.employeeDecisions[0],{rowId:'row-1',matchedEmployeeId:2,allowCreate:false})
 assert.deepEqual({batch,context,request},before)
 request.employeeDecisions[0]={rowId:'row-1',matchedEmployeeId:99,allowCreate:false}
 const invalid=masterWorkspaceDefaults(batch,context,request).employeeDecisions[0]
 assert.equal(invalid.matchedEmployeeId,undefined);assert.equal(invalid.allowCreate,false)
 request.employeeDecisions[0]={rowId:'row-1',matchedEmployeeId:2,allowCreate:true}
 assert.equal(masterWorkspaceDefaults(batch,context,request).employeeDecisions[0].matchedEmployeeId,undefined)
})

test('an empty Master can propose new source employees without synthetic matching or hidden exclusions',()=>{
 const {batch,context,request}=fixture(33)
 context.employees=[]
 context.roster.rows.forEach(row=>{row.status='New';row.employeeId=null})
 request.employeeDecisions=request.employeeDecisions.map(decision=>({rowId:decision.rowId,allowCreate:false}))
 const next=masterWorkspaceDefaults(batch,context,request)
 assert.equal(next.employeeDecisions.length,33)
 assert.ok(next.employeeDecisions.every(decision=>decision.allowCreate&&!decision.matchedEmployeeId))
 assert.deepEqual(next.rowIds,batch.rows.map(row=>row.id))
})

test('missing policy stays blank instead of accepting the legacy Sunday-off fallback',()=>{
 const {batch,context,request}=fixture()
 context.defaults.workWeek='';context.workWeeks=[];request.workWeek='Sunday off'
 assert.equal(masterWorkspaceDefaults(batch,context,request).workWeek,'')
 context.workWeeks=[{value:'WEEK-6',label:'Saturday off'}];request.workWeek='WEEK-6'
 assert.equal(masterWorkspaceDefaults(batch,context,request).workWeek,'WEEK-6')
 context.defaults.workWeek='WEEK-5'
 assert.equal(masterWorkspaceDefaults(batch,context,request).workWeek,'WEEK-6','a known selected policy must not be replaced by the client default')
 request.workWeek='Unknown policy';context.defaults.workWeek=''
 assert.equal(masterWorkspaceDefaults(batch,context,request).workWeek,'','an unknown fresh policy remains unresolved')
})

test('the same saved selection retains its Monday-off policy even when the client default is Sunday off',()=>{
 const {batch,context,request}=fixture()
 context.defaults.workWeek='Sunday off';context.workWeeks=[{value:'Sunday off',label:'Sunday off'}]
 request.workWeek='Monday off'
 context.lastImport={importId:'saved-draft',requestId:'same-source',status:'Draft',savedRequest:structuredClone(request)}
 const before=structuredClone({batch,context,request})
 freeze(batch);freeze(context);freeze(request)
 const next=masterWorkspaceDefaults(batch,context,request)
 assert.equal(next.workWeek,'Monday off')
 assert.deepEqual(next.components,request.components)
 assert.deepEqual(next.editableDefaults,request.editableDefaults)
 assert.equal(next.reviewedPolicyScope,undefined)
 assert.equal(next.templateScope.reviewed,false)
 assert.deepEqual({batch,context,request},before)
})

test('source locations are listed for the source month with a finite applicability period',()=>{
 const {batch,context,request}=fixture()
 context.source.rows.find(row=>row.sourceRow===2).cells[2].value=' Test location '
 context.source.rows.find(row=>row.sourceRow===3).cells[2].value='New site'
 context.source.rows.find(row=>row.sourceRow===4).cells[2].value='N/A'
 const scope=masterWorkspaceDefaults(batch,context,request).templateScope
 assert.equal(scope.locationMode,'Listed')
 assert.deepEqual(scope.workLocationIds,[1])
 assert.deepEqual(scope.newLocationNames,['New site'])
 assert.equal(scope.effectiveTo,'2026-10-31');assert.equal(scope.openEnded,false)
 assert.equal(scope.reviewed,false)
 assert.equal(sourceMonthEnd('2024-02'),'2024-02-29')
 assert.equal(sourceMonthEnd('2026-02'),'2026-02-28')
})

test('a reused finite client scope proposes the new month and current source locations without approving policy',()=>{
 const {batch,context,request}=fixture()
 const oldScope={reviewed:true,effectiveTo:'2026-09-30',openEnded:false,locationMode:'Listed',workLocationIds:[99],newLocationNames:[],categoryKind:'SkillCategory',categories:['Research'],divisorMeaning:'ContractFixedDays',payableMeaning:'AlreadyPayable',policyBasis:'DatedPolicy',policyVersion:'policy-v1'}
 request.templateScope=structuredClone(oldScope)
 context.clientConfiguration={clientId:42,month:'2026-09',sourceColumns:structuredClone(context.source.columns),savedRequest:structuredClone(request)}
 context.source.rows.find(row=>row.sourceRow===3).cells[2].value='New research site'
 const before=structuredClone({batch,context,request})
 const next=masterWorkspaceDefaults(batch,context,request),scope=next.templateScope
 assert.equal(scope.effectiveTo,'2026-10-31');assert.equal(scope.openEnded,false);assert.equal(scope.reviewed,false)
 assert.equal(scope.locationMode,'Listed');assert.deepEqual(scope.workLocationIds,[1]);assert.deepEqual(scope.newLocationNames,['New research site'])
 for(const key of ['policyBasis','policyVersion','divisorMeaning','payableMeaning','categoryKind','categories'])assert.deepEqual(scope[key],oldScope[key],key)
 assert.deepEqual({batch,context,request},before)
 context.lastImport={importId:'draft',requestId:'saved-draft',status:'Draft',savedRequest:structuredClone(request)}
 assert.deepEqual(masterWorkspaceDefaults(batch,context,request).templateScope,oldScope,'continuing the exact saved selection preserves its explicitly edited scope')
})

test('ambiguous and invalid location source values never become inferred Head-office scope',()=>{
 const {batch,context,request}=fixture()
 context.workLocations.push({id:9,name:'test location'})
 context.source.rows.find(row=>row.sourceRow===3).cells[2].error='#VALUE!'
 context.source.rows.find(row=>row.sourceRow===4).cells[2].value='-'
 request.defaultWorkLocationName='Head office'
 const scope=masterWorkspaceDefaults(batch,context,request).templateScope
 assert.equal(scope.locationMode,'')
 assert.deepEqual(scope.workLocationIds,[]);assert.deepEqual(scope.newLocationNames,[])
})

test('CalendarDays is proposed only from a complete valid source-month divisor',()=>{
 const {batch,context,request}=fixture()
 assert.equal(masterWorkspaceDefaults(batch,context,request).templateScope.divisorMeaning,'')
 context.source.rows.filter(row=>row.sourceRow>1).forEach(row=>{row.cells[3].value='31'})
 assert.equal(masterWorkspaceDefaults(batch,context,request).templateScope.divisorMeaning,'CalendarDays')
 for(const flag of ['error','calculationError','missingCachedValue']){
  const changed=structuredClone(context);changed.source.rows.find(row=>row.sourceRow===2).cells[3][flag]=true
  assert.notEqual(masterWorkspaceDefaults(batch,changed,request).templateScope.divisorMeaning,'CalendarDays',flag+' must not certify a numeric cache')
 }
 const fixed={...request,payrollDaysColumnIndex:-1,fixedPayrollDays:30}
 assert.equal(masterWorkspaceDefaults(batch,context,fixed).templateScope.divisorMeaning,'ContractFixedDays')
})

test('explicit prior scope and attendance choices remain unchanged; payable days never become present days',()=>{
 const {batch,context,request}=fixture()
 request.templateScope={reviewed:false,effectiveTo:'',openEnded:true,locationMode:'Listed',workLocationIds:[1],newLocationNames:[],categoryKind:'SkillCategory',categories:['Research'],divisorMeaning:'ContractFixedDays',payableMeaning:'AlreadyPayable',policyBasis:'DatedPolicy',policyVersion:'reviewed-policy'}
 request.importAttendance=true;request.importPayableOnlyAttendance=true;request.presentDaysColumnIndex=undefined
 const next=masterWorkspaceDefaults(batch,context,request)
 assert.deepEqual(next.templateScope,request.templateScope)
 assert.equal(next.importAttendance,true);assert.equal(next.importPayableOnlyAttendance,true)
 assert.equal(next.payableDaysColumnIndex,5);assert.equal(next.presentDaysColumnIndex,undefined)
 const reviewed=reviewedMasterWorkspace(next,context,batch,quickPreview(next))
 assert.equal(reviewed.attendanceReview.presentMeaning,'NotMapped')
 assert.equal(reviewed.presentDaysColumnIndex,undefined)
 assert.equal(reviewed.importPayableOnlyAttendance,true)
})

test('special location-allocation and salary-correction workflows retain their original request',()=>{
 const {batch,context,request}=fixture()
 for(const extra of [{locationAllocations:{version:1,groups:[]}},{correctionOfImportId:'previous-import'}]){
  const original=freeze({...structuredClone(request),...extra})
  assert.equal(masterWorkspaceDefaults(batch,context,original),original)
 }
})

test('one explicit review sets source-bound flags and template reuse without changing inputs or salary amounts',()=>{
 const {batch,context,request}=fixture(),proposed=masterWorkspaceDefaults(batch,context,request),preview=quickPreview(proposed)
 const before=structuredClone({batch,context,request:proposed,preview})
 freeze(batch);freeze(context);freeze(proposed);freeze(preview)
 const reviewed=reviewedMasterWorkspace(proposed,context,batch,preview)
 assert.equal(reviewed.reviewedFixedInputs,true);assert.equal(reviewed.reviewedPolicyScope,true)
 assert.equal(reviewed.templateScope.reviewed,true)
 assert.equal(reviewed.attendanceReview.reviewed,true)
 assert.equal(reviewed.attendanceReview.sourceHash,'a'.repeat(64))
 assert.equal(reviewed.attendanceReview.sourceMonth,'2026-10')
 assert.equal(reviewed.attendanceReview.boundSourceInputs,attendanceProofBinding(reviewed))
 assert.deepEqual(reviewed.templateDecisions,[{variantKey:'rules',existingTemplateId:'template-1',createVariant:false}])
 assert.deepEqual(reviewed.components,proposed.components)
 assert.deepEqual(reviewed.employeeDecisions,proposed.employeeDecisions)
 assert.deepEqual({batch,context,request:proposed,preview},before)
})

test('review rejects other batches, months, malformed source hashes and stale source snapshots',()=>{
 const {batch,context,request}=fixture(),q=masterWorkspaceDefaults(batch,context,request),preview=quickPreview(q)
 for(const change of [p=>{p.transfer.sourceBatchId='foreign'},p=>{p.transfer.sourceMonth='2026-09'},p=>{p.transfer.sourceHash='short'},p=>{delete p.transfer}]){
  const changed=structuredClone(preview);change(changed)
  assert.throws(()=>reviewedMasterWorkspace(q,context,batch,changed),/saved Excel|source|Refresh/i)
 }
 const foreign={...q,batchId:'foreign'},foreignPreview=structuredClone(preview);foreignPreview.transfer.sourceBatchId='foreign'
 assert.throws(()=>reviewedMasterWorkspace(foreign,context,batch,foreignPreview),/saved Excel|source|Refresh/i)
 const stale=structuredClone(preview);stale.transfer.sourceHash='b'.repeat(64)
 assert.throws(()=>reviewedMasterWorkspace(q,context,batch,stale),/saved Excel|source|Refresh/i)
})

test('custom cycles without exact saved dates and unclear prior day meanings are not invented',()=>{
 const {batch,context,request}=fixture()
 delete context.quickPreparation
 const q={...request,attendanceCycleStartDay:26,attendanceCycleEndDay:25}
 let reviewed=reviewedMasterWorkspace(q,context,batch,quickPreview(q))
 assert.equal(reviewed.attendanceReview.cycleStart,'');assert.equal(reviewed.attendanceReview.cycleEnd,'')
 q.attendanceReview={mode:'VerifyExistingNative',sourceHash:'a'.repeat(64),sourceMonth:batch.month,cycleStart:'2026-09-26',cycleEnd:'2026-10-25',payableMeaning:'',presentMeaning:'',lwpBasis:'',reviewed:false}
 reviewed=reviewedMasterWorkspace(q,context,batch,quickPreview(q))
 assert.equal(reviewed.attendanceReview.presentMeaning,'');assert.equal(reviewed.attendanceReview.payableMeaning,'')
 assert.equal(reviewed.attendanceReview.mode,'VerifyExistingNative')
 assert.equal(reviewed.attendanceReview.cycleStart,'2026-09-26')
})

test('template choice reuses a unique exact match and does not choose among conflicting existing templates',()=>{
 const {request}=fixture(),preview=quickPreview(request),template=preview.transfer.templates[0]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[{variantKey:'rules',existingTemplateId:'template-1',createVariant:false}])
 template.candidates=[candidate('one'),candidate('two')]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[])
 template.candidates=[];template.existingResults=[{rowId:'row-1',templateId:'existing'}]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[])
 template.existingResults=[]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[{variantKey:'rules',existingTemplateId:'',createVariant:true}])
})

test('same-rule template applicability and explicit operator decisions retain their review boundary',()=>{
 const {request}=fixture(),preview=quickPreview(request),template=preview.transfer.templates[0]
 template.candidates=[candidate('one',{exact:false,scopeStatus:'Legacy',recordedScopes:[]})]
 assert.equal(masterWorkspaceTemplateDecisions(request,preview)[0].scopeAction,'AttestLegacyScope')
 template.candidates[0].recordedScopes=[{sourceMonth:'2026-09'}]
 assert.equal(masterWorkspaceTemplateDecisions(request,preview)[0].scopeAction,'ExtendReviewedScope')
 template.candidates[0].scopeStatus='Different policy/basis';template.existingResults=[{templateId:'one'}]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[])
 const chosen={variantKey:'rules',existingTemplateId:'one',createVariant:false,keepExisting:true}
 request.templateDecisions=[chosen]
 assert.deepEqual(masterWorkspaceTemplateDecisions(request,preview),[chosen])
})

test('duplicate employee issues are grouped without hiding distinct blockers or affected row IDs',()=>{
 const {request}=fixture(33),preview=quickPreview(request)
 preview.issues=request.rowIds.map(rowId=>({severity:'Error',code:'MissingInput',message:'Select the monthly source column.',rowId}))
 preview.issues.push({severity:'Warning',code:'MissingInput',message:'Select the monthly source column.',rowId:'row-1'})
 preview.issues.push({severity:'Error',code:'OtherPolicy',message:'Select the monthly source column.',rowId:'row-1'})
 const groups=masterWorkspaceIssues(preview)
 assert.equal(groups.length,3)
 assert.equal(groups[0].count,33);assert.deepEqual(groups[0].rowIds,request.rowIds)
 assert.equal(groups[1].severity,'Warning');assert.equal(groups[2].count,1)
})
