import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root=fileURLToPath(new URL('../',import.meta.url)),require=createRequire(import.meta.url)
const ts=require(path.join(root,'payroll-ui/node_modules/typescript/lib/typescript.js'))
const text=fs.readFileSync(path.join(root,'payroll-ui/src/utils/clLwpSimulation.ts'),'utf8'),module={exports:{}}
new Function('require','module','exports',ts.transpileModule(text,{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(name=>assert.fail('No server/ledger/storage dependency may run in simulation: '+name),module,module.exports)
const {emptyClLwpScenario,simulateClLwp,loadClLwpDraft,saveClLwpDraft}=module.exports
const initial=emptyClLwpScenario(42)
assert.equal(initial.proposedLimit,null);assert.equal(initial.attendanceDays,null);assert.equal(simulateClLwp(initial).payable,null)
const s={...initial,effectiveFrom:'2026-11-01',cycleStart:'2026-11-01',cycleEnd:'2026-11-30',basis:'Worked',eligibility:'Full cycle',attendanceDays:20,paidWeeklyOff:4,paidHolidays:0,requestedCl:4,availableCl:4,proposedLimit:4,limitPeriod:'Annual',usedInLimitPeriod:0,approval:'Assume approved',excessAsLwp:true,divisor:30,evidence:'Assumed',sourcePayable:28,sourceLwp:2,sourceDivisor:30}
const before=JSON.stringify(s),result=simulateClLwp(s)
assert.deepEqual([result.absenceDays,result.paidCl,result.lwp,result.payable],[6,4,2,28]);assert.equal(result.payableVariance,0)
assert.equal(JSON.stringify(s),before);assert.equal(result.canActivate,false);assert.equal(result.salaryImpact,null)
assert.match(result.status,/policy approval pending/);assert.match(result.salaryImpactReason,/No verified salary/)
let r=simulateClLwp({...s,availableCl:2});assert.deepEqual([r.paidCl,r.lwp,r.payable],[2,4,26]);assert.ok(r.reasons.some(v=>v.includes('limited')))
r=simulateClLwp({...s,usedInLimitPeriod:3});assert.deepEqual([r.paidCl,r.lwp,r.payable],[1,5,25])
r=simulateClLwp({...s,attendanceDays:23.5,requestedCl:.5});assert.deepEqual([r.absenceDays,r.paidCl,r.lwp,r.payable],[2.5,.5,2,28])
r=simulateClLwp({...s,eligibility:'Reviewed partial cycle',eligibleDays:12,attendanceDays:10,paidWeeklyOff:0,requestedCl:1,divisor:12});assert.deepEqual([r.paidCl,r.lwp,r.payable],[1,1,11])
for(const patch of [{paidWeeklyOff:null},{attendanceDays:null},{availableCl:null},{usedInLimitPeriod:null},{eligibility:'Unknown'},{approval:'Unknown'},{excessAsLwp:null},{effectiveFrom:'2026-11-16'},{divisor:26.5}]) assert.equal(simulateClLwp({...s,...patch}).payable,null,JSON.stringify(patch))
r=simulateClLwp({...s,basis:'Already payable',attendanceDays:30,requestedCl:null,includedCl:4});assert.deepEqual([r.payable,r.lwp,r.paidCl],[30,0,4]);assert.ok(r.reasons.some(v=>v.includes('not added again')))
assert.equal(simulateClLwp({...s,basis:'Already payable',attendanceDays:30}).payable,null,'Double credit must be rejected')
assert.equal(simulateClLwp({...s,requestedCl:7}).payable,null,'CL cannot exceed comparable absence')
assert.equal(simulateClLwp({...s,cycleStart:'2026-11-31'}).payable,null)
r=simulateClLwp({...s,cycleEnd:'2026-12-01',basis:'Already payable',attendanceDays:31,requestedCl:null});assert.equal(r.payable,null,'No guessed calendar/divisor conversion')
const map=new Map();let writes=0
const storage={getItem:key=>map.get(key)||null,setItem:(key,value)=>{writes++;map.set(key,value)}}
assert.equal(loadClLwpDraft(storage,1,42),null)
const draft=saveClLwpDraft(storage,1,s,0);assert.equal(draft.revision,1)
assert.equal(saveClLwpDraft(storage,1,s,1).revision,1);assert.equal(writes,1,'Repeated save is a no-op')
assert.throws(()=>saveClLwpDraft(storage,1,{...s,requestedCl:2},0),/changed in another view/)
assert.equal(loadClLwpDraft(storage,2,42),null);assert.equal(loadClLwpDraft(storage,1,43),null)
assert.deepEqual(loadClLwpDraft(storage,1,42).scenario,s)
const key=[...map.keys()][0],tampered={...draft,clientId:43};map.set(key,JSON.stringify(tampered));assert.throws(()=>loadClLwpDraft(storage,1,42),/scope\/version/)
assert.deepEqual(Object.keys(module.exports).sort(),['emptyClLwpScenario','loadClLwpDraft','saveClLwpDraft','simulateClLwp'])
console.log('PASS: 14 CL/LWP scenario groups — unknown inputs, provisional 4 CL example, balance/limit shortfalls, half-day/partial cycle, no double credit, effective dates/divisor, source immutability, user/client draft isolation, replay/stale-version guards and no activation/ledger API.')
