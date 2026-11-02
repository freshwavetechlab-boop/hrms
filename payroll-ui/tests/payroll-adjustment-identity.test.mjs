import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/payrollAdjustmentIdentity.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(exports)
const {payrollAdjustmentPayload:payload}=exports
test('BIGINT component codes retain legacy transaction identity without truncation or amount changes',()=>{
 const row={id:0,componentId:1700000000001,componentCode:'EXTRA_DUTY',componentName:'Extra duty',amount:123.45,employeeId:11,clientId:9,payPeriod:'2026-09',adjustmentType:'Earning'}
 assert.deepEqual(payload(row),{...row,componentId:0});assert.equal(row.componentId,1700000000001)
 for(const id of [0,109,2147483647]){const native={...row,componentId:id};assert.equal(payload(native),native)}
 assert.throws(()=>payload({...row,componentCode:''}),/component code/)
 assert.throws(()=>payload({...row,componentId:Number.MAX_SAFE_INTEGER+1}),/component code/)
})
