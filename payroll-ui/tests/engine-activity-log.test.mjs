import {test} from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context=vm.createContext({exports:{},Number,Math})
vm.runInContext(ts.transpileModule(fs.readFileSync(new URL('../src/components/engineActivityLogFormat.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText,context)
const {activityDuration,activityOutcome}=context.exports
test('72 second job is shown as measured duration; missing timing is not zero',()=>{
 assert.equal(activityDuration(72000),'1m 12.0s')
 assert.equal(activityDuration(1250),'1.25 sec')
 assert.equal(activityDuration(0),'0 ms')
 for(const value of [null,undefined,NaN,-1]) assert.equal(activityDuration(value),'Not recorded')
})
test('interrupted and rejected outcomes never read as successful completion',()=>{
 assert.equal(activityOutcome('Interrupted'),'Interrupted / unknown')
 assert.equal(activityOutcome('Rejected'),'Request rejected')
 assert.equal(activityOutcome('Completed'),'Completed')
})
