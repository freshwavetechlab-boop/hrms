import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
import {quickPreparationFixture,quickPreview} from '../../tools/fixtures/excel-payroll-quick-prepare.mjs'

const modules=new Map()
function load(name){if(modules.has(name))return modules.get(name);const exports={};modules.set(name,exports);new Function('exports','require',ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,path=>load(path.replace('./','')));return exports}
const {proposeExcelPayrollSetup}=load('excelPayrollSetup')
const {masterWorkspaceDefaults}=load('excelPayrollMasterWorkspace')
const {diagnoseSetup}=load('excelPayrollSetupDiagnostics')
function fixture(count=3){const f=quickPreparationFixture(count);f.request=masterWorkspaceDefaults(f.batch,f.context,proposeExcelPayrollSetup(f.batch,f.context,f.batch.rows.map(row=>row.id)).request);f.request.templateScope.divisorMeaning='ContractFixedDays';return f}
function freeze(value){if(value&&typeof value==='object'){Object.values(value).forEach(freeze);Object.freeze(value)}return value}
const joined=items=>items.flatMap(item=>item.details).join('\n')

test('a valid new-component proposal is not a missing-Master error for any client name',()=>{
 for(const clientName of ['Example research office','Another manufacturing client']){
  const {batch,context,request}=fixture();batch.clientName=clientName;context.components=[]
  request.components=request.components.map(component=>({...component,existingComponentId:undefined}))
  assert.deepEqual(diagnoseSetup(batch,context,request),[])
 }
})

test('33 unsupported row formulas produce one translation cause with all original source references',()=>{
 const {batch,context,request}=fixture(33),component=request.components[0]
 component.formula='REVIEW_REQUIRED';component.rowFormulas=Object.fromEntries(request.rowIds.map(id=>[id,'REVIEW_REQUIRED']))
 const before=structuredClone({batch,context,request});freeze(batch);freeze(context);freeze(request)
 const diagnostics=diagnoseSetup(batch,context,request),translation=diagnostics.filter(item=>item.category==='translation')
 assert.equal(translation.length,1)
 assert.deepEqual(translation[0].sourceRows,Array.from({length:33},(_,i)=>i+2))
 assert.ok(translation[0].columns.includes('H · Basic'))
 assert.match(joined(translation),/G2\/D2\*F2/)
 assert.match(translation[0].explanation,/does not.*mean.*Excel.*wrong/i)
 assert.equal(translation[0].tab,'salary');assert.equal(translation[0].blocking,true)
 assert.deepEqual({batch,context,request},before)
})

test('a valid row override is not diagnosed from another row placeholder',()=>{
 const {batch,context,request}=fixture(),component=request.components[0]
 component.formula='REVIEW_REQUIRED';component.rowFormulas={'row-1':'BASIC_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS','row-2':'REVIEW_REQUIRED','row-3':'0'}
 const result=diagnoseSetup(batch,context,request).find(item=>item.category==='translation')
 assert.deepEqual(result.sourceRows,[3])
})

test('dependent PF/ESI missing-component cascades stay with the known untranslated cause, not20 fake Master failures',()=>{
 const {batch,context,request}=fixture(),basic=request.components[0],pf=request.components[1]
 basic.code='BASIC_REVISED';basic.formula='REVIEW_REQUIRED';basic.rowFormulas=Object.fromEntries(request.rowIds.map(id=>[id,'REVIEW_REQUIRED']))
 pf.formula='BASIC_REVISED_EARNED*12%';pf.rowFormulas={}
 request.components.push({...structuredClone(pf),key:'esi',code:'ESI',formula:'PF_EARNED+1',rowFormulas:{},existingComponentId:undefined})
 const preview=quickPreview(request)
 preview.issues=request.rowIds.flatMap(rowId=>[{severity:'Error',rowId,code:'PF',message:'Unknown component BASIC_REVISED_EARNED'},{severity:'Error',rowId,code:'ESI',message:'Missing dependency PF_EARNED'}])
 preview.issues.push({severity:'Error',rowId:'row-1',message:'A positive full-month salary input is required.'})
 const result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.filter(item=>item.category==='translation').length,1)
 assert.equal(result.filter(item=>item.category==='master').length,0)
 assert.match(joined(result),/Unknown component BASIC_REVISED_EARNED/)
 assert.match(joined(result),/Missing dependency PF_EARNED/)
 assert.match(joined(result),/positive full-month salary input/)
 assert.deepEqual(result.find(item=>item.category==='translation').sourceRows,[2,3,4])
})

test('unrelated unknown dependency and distinct unknown messages remain visible alongside translation failures',()=>{
 const {batch,context,request}=fixture(),component=request.components[0]
 component.formula='REVIEW_REQUIRED';component.rowFormulas={}
 const preview=quickPreview(request)
 preview.issues=[{severity:'Error',rowId:'row-1',code:'OTHER',message:'Unknown variable UNRELATED_RATE'},{severity:'Error',message:'Unexpected server check A'},{severity:'Error',message:'Unexpected server check B'}]
 const result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.filter(item=>item.category==='translation').length,2)
 assert.equal(result.filter(item=>item.category==='unknown').length,2)
 for(const issue of preview.issues)assert.ok(joined(result).includes(issue.message))
})

test('actual captured cell errors and missing caches remain separate source evidence, not a translation claim',()=>{
 const {batch,context,request}=fixture()
 context.source.rows.find(row=>row.sourceRow===2).cells[7].error='#VALUE!'
 context.source.rows.find(row=>row.sourceRow===3).cells[6].missingCachedValue=true
 const result=diagnoseSetup(batch,context,request),source=result.find(item=>item.category==='source')
 assert.ok(source);assert.deepEqual(source.sourceRows,[2,3])
 assert.match(joined([source]),/#VALUE!/);assert.match(joined([source]),/not captured/)
 assert.equal(result.some(item=>item.category==='translation'),false)
})

test('missing location and new-location state route to Location & month without inventing an office',()=>{
 const {batch,context,request}=fixture()
 context.employees=[];context.roster.rows.forEach(row=>{row.status='New';row.employeeId=null})
 request.employeeDecisions=request.rowIds.map(rowId=>({rowId,allowCreate:true}));request.sourceWorkLocationColumnIndex=undefined;request.defaultWorkLocationId=undefined;request.defaultWorkLocationName='Head office'
 context.workLocations=[]
 let result=diagnoseSetup(batch,context,request)
 assert.ok(result.some(item=>item.key==='work-location'&&item.tab==='location'))
 request.sourceWorkLocationColumnIndex=2;request.createMissingWorkLocations=true;request.defaultWorkLocationState=''
 result=diagnoseSetup(batch,context,request)
 assert.ok(result.some(item=>item.key==='location-state'&&item.tab==='location'))
 request.defaultWorkLocationState='Maharashtra'
 assert.equal(diagnoseSetup(batch,context,request).some(item=>item.key==='location-state'),false)
})

test('known existing organisation locations do not become missing-new-location proposals',()=>{
 const {batch,context,request}=fixture()
 request.sourceWorkLocationColumnIndex=undefined;request.defaultWorkLocationId=undefined;request.defaultWorkLocationName=''
 const result=diagnoseSetup(batch,context,request)
 assert.equal(result.some(item=>item.category==='location'),false)
})

test('actual roster identity review is shown before preview and keeps its raw reason',()=>{
 const {batch,context,request}=fixture()
 context.roster.rows[0].status='Review';context.roster.rows[0].message='Work email is repeated across this source selection.'
 request.employeeDecisions[0]={rowId:'row-1',allowCreate:false}
 const result=diagnoseSetup(batch,context,request),identity=result.find(item=>item.category==='identity')
 assert.equal(identity.tab,'employees');assert.deepEqual(identity.sourceRows,[2])
 assert.match(joined([identity]),/Work email is repeated/)
 const preview=quickPreview(request);preview.canApply=true;preview.issues=[]
 assert.equal(diagnoseSetup(batch,context,request,preview).some(item=>item.category==='identity'),false,'a new authoritative clean preview is not contradicted by an old roster hint')
})

test('day, attendance and missing policy choices are explained without fabricating actual presence',()=>{
 const {batch,context,request}=fixture()
 request.workWeek='';request.payrollDaysColumnIndex=-1;request.fixedPayrollDays=undefined;request.payableDaysColumnIndex=-1;request.presentDaysColumnIndex=undefined;request.importAttendance=true
 const before=structuredClone(request),result=diagnoseSetup(batch,context,request)
 assert.ok(result.some(item=>item.key==='attendance-policy'&&item.tab==='policies'))
 const day=result.find(item=>item.key==='attendance-day-inputs')
 assert.equal(day.tab,'location');assert.match(joined([day]),/not actual presence/)
 assert.deepEqual(request,before)
})

test('after-preview identity, linked sources, unsupported formulas, salary differences and precision retain separate causes',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 preview.issues=[
  {severity:'Error',rowId:'row-1',message:'Full employee identity preflight: Work email is repeated across the reviewed source selection.'},
  {severity:'Error',rowId:'row-1',message:'This source is reference only. Supply its original workbook dependencies.'},
  {severity:'Error',rowId:'row-2',code:'BASIC',message:'This source function is not supported by exact transfer proof. It cannot be replaced with a cached constant.'},
  {severity:'Review',rowId:'row-2',code:'NET_PAY',message:'Net pay differs from saved Excel by 4.25. Review formula, classification and rounding.'},
  {severity:'Error',rowId:'row-3',message:'Monthly input exceeds the employee salary store four-decimal precision.'},
 ]
 const result=diagnoseSetup(batch,context,request,preview)
 for(const category of ['identity','source','translation','salary','rounding'])assert.ok(result.some(item=>item.category===category),category)
 assert.equal(result.find(item=>item.category==='salary').blocking,false)
 for(const issue of preview.issues)assert.ok(joined(result).includes(issue.message))
})

test('API compatibility and selected-Master failures are distinct from a component code or source arithmetic issue',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 request.recurringBlankInputReview={version:2,reviewed:false,sourceHash:'',columnIndices:[6],rowIds:['row-1'],reason:'Review'}
 context.rowScopedRecurringBlankInputReviewSupported=false
 request.components[0].existingComponentId='deleted-master'
 preview.issues=[{severity:'Error',code:'BASIC',message:'The selected component master no longer exists.'},{severity:'Error',message:'The additive attendance scheduling schema upgrade is required.'}]
 const result=diagnoseSetup(batch,context,request,preview)
 assert.ok(result.some(item=>item.category==='api'&&item.tab==='review'))
 assert.ok(result.some(item=>item.category==='master'&&item.tab==='salary'))
 assert.equal(result.some(item=>item.category==='translation'),false)
 assert.ok(joined(result).includes(preview.issues[0].message))
})

test('a verified captured/native translation difference is presented as app formula support, not bad source Excel',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 preview.issues=[{severity:'Error',rowId:'row-1',code:'ESIC',message:'Captured formula translation differs from native payroll for ESI.'}]
 const result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.length,1);assert.equal(result[0].category,'translation')
 assert.equal(result[0].tab,'salary');assert.equal(result[0].blocking,true)
 assert.match(result[0].explanation,/does not.*mean.*Excel.*wrong/i)
 assert.ok(joined(result).includes(preview.issues[0].message))
})

test('unresolved raw total coverage is source comparison proof, not an unresolved payroll formula dependency',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 context.source.columns.push({columnIndex:10,sourceHeader:'Total Deductions',label:'Total Deductions',kind:'info'})
 request.sourceColumnRoles[10]='deductionTotal'
 const message='Excel comparison is unresolved. Source deduction coverage does not prove the raw-component aggregate stage. Review the source totals/formulas and coverage; a rounded-looking net or a small unexplained delta cannot be approved automatically.'
 preview.issues=[{severity:'Error',rowId:'row-1',code:'Amount comparison',message},{severity:'Error',rowId:'row-2',code:'Amount comparison',message}]
 const before=structuredClone({batch,context,request,preview}),result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.length,1);assert.equal(result[0].key,'source-total-coverage')
 assert.equal(result[0].category,'source');assert.equal(result[0].tab,'salary');assert.equal(result[0].blocking,true)
 assert.match(result[0].title,/total coverage/i)
 assert.match(result[0].explanation,/which mapped component columns feed.*total/i)
 assert.ok(result[0].columns.includes('K · Total Deductions'))
 assert.ok(result[0].columns.includes('I · PF'))
 assert.deepEqual(result[0].sourceRows,[2,3]);assert.ok(joined(result).includes(message))
 assert.equal(result.some(item=>item.category==='translation'),false)
 assert.deepEqual({batch,context,request,preview},before)
})

test('a genuine missing BASIC_MONTHLY dependency remains a separate formula issue beside source-total coverage',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 preview.issues=[{severity:'Error',rowId:'row-1',code:'Amount comparison',message:'Source deduction coverage does not prove the raw-component aggregate stage.'},{severity:'Error',rowId:'row-1',code:'BASIC',message:'Unknown variable BASIC_MONTHLY is missing from this formula.'}]
 const result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.filter(item=>item.key==='source-total-coverage').length,1)
 assert.equal(result.filter(item=>item.category==='translation').length,1)
 assert.match(joined(result.filter(item=>item.category==='translation')),/Unknown variable BASIC_MONTHLY/)
})

test('nonblocking policy notes remain visible and cannot erase a distinct blocking unknown check',()=>{
 const {batch,context,request}=fixture(),preview=quickPreview(request)
 preview.issues=[{severity:'Review',message:'Source-month attendance and attendance-group membership will not be saved.'},{severity:'Error',message:'Unexpected authorization contract requires attention.'}]
 const result=diagnoseSetup(batch,context,request,preview)
 assert.equal(result.find(item=>item.category==='policy').blocking,false)
 assert.equal(result.find(item=>item.category==='unknown').blocking,true)
 assert.equal(result.length,2)
})
