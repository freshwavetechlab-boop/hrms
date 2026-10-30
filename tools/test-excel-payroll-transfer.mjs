import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const root=fileURLToPath(new URL('../',import.meta.url)), require=createRequire(import.meta.url)
const ts=require(path.join(root,'payroll-ui/node_modules/typescript/lib/typescript.js'))
function load(relative, dependency) {
  const source=fs.readFileSync(path.join(root,'payroll-ui/src',relative),'utf8'), module={exports:{}}
  const code=ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText
  new Function('require','module','exports',code)(dependency||((name)=>assert.fail('Unexpected runtime dependency '+name)),module,module.exports)
  return module.exports
}
const {compareTransferPayroll}=load('utils/excelPayrollTransfer.ts')
const components=[{code:'BASIC',category:'Earning'},{code:'PF',category:'Deduction'},{code:'PF_ER',category:'Benefit'}]
const source={rowId:'source-one',employeeId:10,payrollDays:30,presentDays:30,payableDays:30,sourceNet:88,lines:components.map((c,i)=>({code:c.code,sourceAmount:i?12:100,calculatedAmount:i?12:100,variance:0}))}
const receipt={importId:'synthetic-receipt',transfer:{version:1,tolerance:.000001,components,parityStatus:'Not verified'},transferRows:[source]}
const observation={employeeId:10,ok:true,error:'',data:{mode:'Employee',complete:true,issues:[],inputs:['PAYROLL_DAYS','PRESENT_DAYS','PAYABLE_DAYS'].map(name=>({name,value:30})),lines:components.map((c,i)=>({...c,earned:i?12:100})),gross:100,deductions:12,benefits:12,net:88}}
const copy=value=>structuredClone(value)
const check=(r,o)=>compareTransferPayroll(r,o)[0]
assert.equal(check(receipt,[observation]).reasons.length,0)
let changed=copy(observation); changed.data.lines[0].earned=101;changed.data.lines[1].earned=13;changed.data.gross=101;changed.data.deductions=13
assert.match(check(receipt,[changed]).status,/Not reconciled/,'A matching net cannot hide component differences')
changed=copy(observation);changed.data.lines.pop();changed.data.benefits=0
assert.ok(check(receipt,[changed]).reasons.some(v=>v.includes('PF_ER')))
changed=copy(observation);changed.data.inputs[2].value=29
assert.ok(check(receipt,[changed]).reasons.some(v=>v.includes('PAYABLE_DAYS')))
changed=copy(observation);changed.data.mode='Template'
assert.ok(check(receipt,[changed]).reasons.some(v=>v.includes('Employee simulation')))
changed=copy(observation);changed.data.lines.push({code:'TDS',category:'Deduction',earned:0})
assert.ok(check(receipt,[changed]).reasons.some(v=>v.includes('no source comparison')))
changed=copy(observation);changed.data.lines[2].category='Deduction'
assert.ok(check(receipt,[changed]).reasons.some(v=>v.includes('classification')))
assert.ok(check(receipt,[observation,observation]).reasons.length)
const ambiguous=copy(receipt);ambiguous.transferRows.push(copy(source))
assert.ok(check(ambiguous,[observation]).reasons.some(v=>v.includes('provenance')))
changed=copy(observation);changed.ok=false;changed.error='Simulation unavailable'
assert.ok(check(receipt,[changed]).reasons.includes('Simulation unavailable'))
const calls=[],response={ok:true,data:{importId:'receipt'},error:'',status:200}
const service=load('services/excelPayrollSetupService.ts',name=>{
  assert.equal(name,'./apiClient')
  return {getJsonResult:async(...args)=>{calls.push(['GET',...args]);return response},postJson:async(...args)=>{calls.push(['POST',...args]);return response}}
})
const request=Object.freeze({batchId:'a'.repeat(32),rowIds:['source-one'],transferReviewVersion:1,reviewedPolicyScope:true,templateDecisions:[{variantKey:'1000',existingTemplateId:'500',createVariant:false}]})
await service.savePayrollSetupDraft(42,request,'stable-request')
await service.previewPayrollSetup(42,request)
await service.applyPayrollSetup(42,request,'reviewed-hash','stable-request')
assert.deepEqual(calls.map(c=>c[1]),['/api/excel-payroll-setup/draft?clientId=42','/api/excel-payroll-setup/preview?clientId=42','/api/excel-payroll-setup/apply?clientId=42'])
assert.equal(calls[0][2].requestId,calls[2][2].requestId)
assert.equal(calls[2][2].request,request)
assert.equal(calls[2][2].planHash,'reviewed-hash')
assert.equal(receipt.transfer.parityStatus,'Not verified','Observations must not mutate the durable receipt into a parity certificate')
console.log('PASS: 11 synthetic Transfer Batch checks — per-component/employer/net/attendance reconciliation, complete Employee mode, unexpected and ambiguous rows, safe failures, existing receipt/preview/apply route reuse and immutable provenance.')
