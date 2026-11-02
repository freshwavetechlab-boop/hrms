import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/draftEmployerCosts.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(exports)
const {prepareDraftEmployerCosts:parse}=exports
const head='employeeCode,componentCode,amount,reference,notes\r\n'
const roster=[{employeeId:11,employeeCode:'001',isSkipped:false}]
test('employer costs retain text identity, paise, source references and quoted reasons',()=>{
 assert.deepEqual(parse(head+'001,EPF_ER,123.45,source-row-1,"Extra duty, employer share"',roster),[{employeeId:11,componentCode:'EPF_ER',amount:123.45,reference:'source-row-1',notes:'Extra duty, employer share'}])
})
test('missing, ambiguous, skipped, duplicate and invalid cost rows fail before a write',()=>{
 for(const line of ['001,EPF_ER,,source-row-1,reason','001,EPF_ER,1.005,source-row-1,reason','002,EPF_ER,10,source-row-1,reason','001,EPF_ER,-1,source-row-1,reason','001,EPF_ER,10,,reason'])assert.throws(()=>parse(head+line,roster))
 const line='001,EPF_ER,10,source-row-1,reason';assert.throws(()=>parse(head+line+'\n'+line,roster));assert.throws(()=>parse(head+line,[...roster,...roster]));assert.throws(()=>parse(head+line,[{...roster[0],isSkipped:true}]))
})
