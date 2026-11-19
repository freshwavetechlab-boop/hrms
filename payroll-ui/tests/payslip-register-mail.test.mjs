import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'
const source=fs.readFileSync(new URL('../src/utils/payslipRegisterMail.ts',import.meta.url),'utf8')
const compiled=ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}).outputText
const {validRegisterMail,validRegisterMailSubmission,registerMailFilter}=await import('data:text/javascript;base64,'+Buffer.from(compiled).toString('base64'))
const status={employeeId:1,email:'employee@example.test',status:'Not sent',canSend:true,canResend:false}
const overview={clientId:34,payRunId:98,items:[status],jobs:[]}
test('mail status fails closed across clients, runs and malformed responses',()=>{
  assert.equal(validRegisterMail(overview,34,98),true)
  assert.equal(validRegisterMail(overview,35,98),false)
  assert.equal(validRegisterMail(overview,34,99),false)
  assert.equal(validRegisterMail({...overview,items:[status,status]},34,98),false)
  assert.equal(validRegisterMail({...overview,items:[{...status,canSend:undefined}]},34,98),false)
})
test('a lost-response retry must match the same request and exact employee selection',()=>{
  const request={requestId:'same-request',employeeIds:[1,2],resend:true,acknowledgeResend:true}
  const result={job:{id:'same-request',clientId:34,payRunId:98},items:[{employeeId:1,status:'Queued'},{employeeId:2,status:'Already queued'}]}
  assert.equal(validRegisterMailSubmission(result,34,98,request),true)
  assert.equal(validRegisterMailSubmission({...result,job:{...result.job,id:'different'}},34,98,request),false)
  assert.equal(validRegisterMailSubmission({...result,items:[result.items[0],result.items[0]]},34,98,request),false)
  assert.equal(validRegisterMailSubmission(result,35,98,request),false)
})
test('sent and attention filters retain the actual delivery meaning',()=>{
  assert.equal(registerMailFilter('Delivered','Sent'),true)
  assert.equal(registerMailFilter('Queued','Sent'),false)
  assert.equal(registerMailFilter('Deferred','Needs attention'),true)
  assert.equal(registerMailFilter('Not sent','Not sent'),true)
})
