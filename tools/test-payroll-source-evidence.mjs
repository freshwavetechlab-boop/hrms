import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import {createRequire} from 'node:module'
import {fileURLToPath} from 'node:url'
const root=fileURLToPath(new URL('../',import.meta.url)), require=createRequire(import.meta.url), ts=require(path.join(root,'payroll-ui/node_modules/typescript/lib/typescript.js'))
function load(relative){const filename=path.join(root,'payroll-ui/src',relative),module={exports:{}};const code=ts.transpileModule(fs.readFileSync(filename,'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText;new Function('require','module','exports',code)(name=>{if(name==='./excelPayslipImport')return load('utils/excelPayslipImport.ts');if(name==='./excelPayslipAttendance')return load('utils/excelPayslipAttendance.ts');if(name==='./payrollSourceEvidence')return load('utils/payrollSourceEvidence.ts');if(name==='./excelPayrollColumns')return load('utils/excelPayrollColumns.ts');throw Error('Unexpected dependency '+name)},module,module.exports);return module.exports}
const {payrollSourceEvidence,redactSourceFormula,sharedPayrollDivisor}=load('utils/payrollSourceEvidence.ts')
const batch={id:'saved-batch',month:'2026-09',rows:[{id:'one',sourceRow:4},{id:'other',sourceRow:5}]}
const source={columns:[{columnIndex:0,sourceHeader:'Employee name',label:'Name',kind:'employeeName'},{columnIndex:1,sourceHeader:'Rate',label:'Rate',kind:'info'},{columnIndex:2,sourceHeader:'Attendance',label:'Attendance',kind:'info'},{columnIndex:3,sourceHeader:'Wages',label:'Wages',kind:'earning'},{columnIndex:4,sourceHeader:'UAN',label:'UAN',kind:'info'},{columnIndex:5,sourceHeader:'PF',label:'PF',kind:'deduction'}],rows:[{sourceRow:4,cells:[{value:'Private name'},{value:'15000'},{value:'28'},{value:'14000',formulaText:'B4/30*C4'},{value:'111122223333'},{value:'1680',formulaText:'D4*12%'}]},{sourceRow:5,cells:[]}]}
const request={batchId:batch.id,rowIds:['one'],payrollDaysColumnIndex:-1,payableDaysColumnIndex:2,sourceColumnRoles:{},components:[{sourceColumnIndex:1,expectedColumnIndex:3}]}
const before=JSON.stringify({batch,source,request});let result=payrollSourceEvidence(batch,source,request)
assert.deepEqual(result.map(r=>r.cell),['B4','C4','D4','F4']);assert.equal(result.find(r=>r.cell==='D4').formula,'B4/30*C4');assert.equal(result.find(r=>r.cell==='D4').cached,'14000');assert.equal(before,JSON.stringify({batch,source,request}))
assert.ok(result.every(r=>r.sourceRow===4));assert.equal(payrollSourceEvidence(batch,source,{...request,batchId:'another'}).length,0)
const misclassified=structuredClone(request);misclassified.components.push({sourceColumnIndex:4});assert.ok(payrollSourceEvidence(batch,source,misclassified).every(r=>r.cell!=='E4'))
source.rows[0].cells[5].missingCachedValue=true;assert.equal(payrollSourceEvidence(batch,source,request).find(r=>r.cell==='F4').state,'Formula cache missing')
source.rows[0].cells[1].value='00123456789';assert.match(payrollSourceEvidence(batch,source,request).find(r=>r.cell==='B4').cached,/withheld/)
assert.equal(redactSourceFormula('IF(A4="ABCDE1234F",111122223333,30)'), 'IF(A4="[text]",[identifier],30)')
source.rows[0].cells[1].value='';assert.equal(payrollSourceEvidence(batch,source,request).find(r=>r.cell==='B4').cached,'Blank')
assert.equal(payrollSourceEvidence(batch,source,{...request,rowIds:[]}).length,0)
console.log('PASS: 8 saved-batch source evidence groups: selected scope, original formula/cache, no mutation, private fields/long identifiers withheld, missing cache and blank inputs; no requests or writes.')

const shared=structuredClone(source);shared.headerRow=3;shared.rows.unshift({sourceRow:2,cells:[{value:''},{value:''},{value:'30'}]});shared.rows.find(r=>r.sourceRow===4).cells[3].formulaText='B4/$C$2*C4';assert.equal(sharedPayrollDivisor(shared,2).days,30);assert.equal(sharedPayrollDivisor(shared,2).recordedCell,'');assert.equal(payrollSourceEvidence(batch,shared,request)[0].cell,'C2');shared.rows[0].cells[2].formulaText='15*2';assert.equal(sharedPayrollDivisor(shared,2),null);console.log('PASS: shared header divisor evidence is source-backed, read-only, bounded and refuses a formula cache as a fixed input.')

const {proposeExcelPayrollSetup}=load('utils/excelPayrollSetup.ts');const proposalSource=structuredClone(shared);proposalSource.rows[0].cells[2].formulaText=undefined;proposalSource.rows.find(r=>r.sourceRow===4).cells[1].value='15000';const proposalBatch={...batch,clientId:42,rows:batch.rows.map(r=>({...r,employeeName:'Synthetic person',employeeCode:'SYN',information:[],earnings:[],deductions:[],employerContributions:[],netPay:0}))};const proposed=proposeExcelPayrollSetup(proposalBatch,{source:proposalSource,employees:[],components:[],workWeeks:[{value:'Sunday off',label:'Sunday off'}],workLocations:[]},['one']);assert.equal(proposed.request.fixedPayrollDays,30);assert.equal(proposed.request.payrollDaysCell,'C2');assert.equal(proposed.request.payrollDaysColumnIndex,-1);console.log('PASS: actual setup proposer binds a captured shared header cell and literal days without inventing a divisor or marking policy review complete.')

proposalSource.monthDaysCell='Z1';assert.equal(sharedPayrollDivisor(proposalSource,2).cell,'C2');console.log('PASS: stale divisor metadata cannot override the actual captured earning formula reference.');


// Synthetic MSETCL-shaped source: appended attendance is not an automatic alias
// for original K-row input. Referenced K6 may even lie beyond a combined header.
const refBatch={id:'reference-batch',month:'2026-09',rows:[{id:'anonymous',sourceRow:8}]};
const refColumns=Array.from({length:59},(_,columnIndex)=>({columnIndex,sourceHeader:'Unclassified',label:'Unclassified',kind:'info'}));
for(const [i,label,kind] of [[4,'Monthly Basic','info'],[10,'Original days','info'],[58,'Appended Attendance','info'],[59,'Earned Basic','earning']])refColumns[i]={columnIndex:i,sourceHeader:label,label,kind};
const refSource={headerRow:1,columns:refColumns,rows:[{sourceRow:6,cells:Array.from({length:60},(_,i)=>({value:i===10?'31':''}))},{sourceRow:8,cells:Array.from({length:60},(_,i)=>i===4?{value:'12424'}:i===10||i===58?{value:'19'}:i===59?{value:'7614.709677419354',formulaText:'E8/$K$6*K8'}:{value:''})}]};
const refRequest={batchId:refBatch.id,rowIds:['anonymous'],payrollDaysColumnIndex:-1,payableDaysColumnIndex:58,components:[{expectedColumnIndex:59}],sourceColumnRoles:{}};
const untouched=JSON.stringify({refBatch,refSource,refRequest}),ref=payrollSourceEvidence(refBatch,refSource,refRequest);
assert.equal(ref.find(r=>r.cell==='K6').cached,'31');assert.equal(ref.find(r=>r.cell==='E8').cached,'12424');assert.equal(ref.find(r=>r.cell==='K8').cached,'19');assert.match(ref.find(r=>r.cell==='K6').state,/NOT certified/);assert.equal(ref.find(r=>r.cell==='BH8').cached,'7614.709677419354');assert.equal(JSON.stringify({refBatch,refSource,refRequest}),untouched);assert.equal(sharedPayrollDivisor(refSource,58),null);
const missingRef=structuredClone(refSource);missingRef.rows=missingRef.rows.filter(r=>r.sourceRow!==6);const missing=payrollSourceEvidence(refBatch,missingRef,refRequest).find(r=>r.cell==='K6');assert.equal(missing.cached,'Unavailable');assert.match(missing.state,/missing/);
const duplicateRef=structuredClone(refSource);duplicateRef.rows.push(structuredClone(duplicateRef.rows[0]));const ambiguous=payrollSourceEvidence(refBatch,duplicateRef,refRequest).find(r=>r.cell==='K6');assert.equal(ambiguous.cached,'Unavailable');assert.match(ambiguous.state,/ambiguous/);
const protectedRef=structuredClone(refSource);protectedRef.columns[10].sourceHeader='UANNumber';protectedRef.columns[10].label='UANNumber';assert(!payrollSourceEvidence(refBatch,protectedRef,refRequest).some(r=>r.cell==='K6'||r.cell==='K8'));
const unknownRef=structuredClone(refSource);unknownRef.columns[4].sourceHeader='Unclassified';unknownRef.columns[4].label='Unclassified';assert.match(payrollSourceEvidence(refBatch,unknownRef,refRequest).find(r=>r.cell==='E8').cached,/withheld/);
const externalRef=structuredClone(refSource);externalRef.rows[1].cells[59].formulaText="'[external.xlsx]Days'!K6+E8";assert(!payrollSourceEvidence(refBatch,externalRef,refRequest).some(r=>r.key.startsWith('reference:')));
const cyclicRef=structuredClone(refSource);cyclicRef.rows[1].cells[4].formulaText='BH8';assert(payrollSourceEvidence(refBatch,cyclicRef,refRequest).length<20);
const errorRef=structuredClone(refSource);errorRef.rows[0].cells[10].missingCachedValue=true;assert.equal(payrollSourceEvidence(refBatch,errorRef,refRequest).find(r=>r.cell==='K6').cached,'Unavailable');
console.log('PASS: 8 local reference groups: appended/original day distinction, unchanged caches, missing and duplicate rows, private/unknown inputs, external references, cycles and error caches; no requests, source edits or financial inference.');
