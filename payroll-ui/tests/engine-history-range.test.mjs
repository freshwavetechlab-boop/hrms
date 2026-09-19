import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context=vm.createContext({exports:{},Date,Error,Number,String})
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/components/engineHistoryRange.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText,context)
const {engineHistoryRange:range}=context.exports
test('yesterday and week follow local calendar boundaries',()=>{
 const now=new Date(2026,8,20,13)
 const yesterday=range('yesterday','','',now)
 assert.equal(new Date(yesterday.from).getDate(),19)
 assert.equal(new Date(yesterday.until).getDate(),20)
 assert.equal(new Date(yesterday.from).getHours(),0)
 assert.equal(new Date(range('week','','',now).from).getDate(),14)
})
test('custom includes selected final day and rejects invalid/reversed/future dates',()=>{
 const now=new Date(2026,8,20,13)
 assert.equal(new Date(range('custom','2026-09-18','2026-09-19',now).until).getDate(),20)
 for(const [from,to] of [['2026-09-20','2026-09-18'],['2026-09-31','2026-10-01'],['2026-10-01','2026-10-02'],['bad','2026-09-20']]) assert.throws(()=>range('custom',from,to,now))
})
