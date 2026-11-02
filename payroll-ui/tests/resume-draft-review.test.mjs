import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/resumeDraftReview.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(exports)
test('removing a parsed file retains the other edited drafts in upload order, even with repeated filenames',()=>{
 const first=new File(['first'],'resume.pdf'),second=new File(['second'],'resume.pdf'),third=new File(['third'],'third.docx')
 const rows=[{file:first,firstName:'Edited first'},{file:second,firstName:'Edited second'},{file:third,firstName:'Third'}]
 assert.deepEqual(exports.retainResumeDrafts(rows,[third,second]),[rows[2],rows[1]])
 assert.deepEqual(exports.retainResumeDrafts(rows,[]),[])
})
test('bulk zero fills only absent CTC and notice fields, keeps salaries and explicit zero, and is repeatable',()=>{
 const original=[{currentCtc:1200000,expectedCtc:null,noticePeriodDays:0,firstName:'A'},{currentCtc:null,noticePeriodDays:30,firstName:'B'}]
 const result=exports.fillMissingResumeNumbers(original)
 assert.equal(result.changed,3)
 assert.deepEqual(result.rows,[{currentCtc:1200000,expectedCtc:0,noticePeriodDays:0,firstName:'A'},{currentCtc:0,expectedCtc:0,noticePeriodDays:30,firstName:'B'}])
 assert.equal(original[0].expectedCtc,null)
 assert.equal(exports.fillMissingResumeNumbers(result.rows).changed,0)
})
