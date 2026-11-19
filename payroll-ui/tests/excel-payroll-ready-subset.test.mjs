import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
import {quickPreparationFixture} from '../../tools/fixtures/excel-payroll-quick-prepare.mjs'

const modules=new Map()
function load(name){
 if(modules.has(name))return modules.get(name)
 const exports={};modules.set(name,exports)
 new Function('exports','require',ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`,import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports,path=>load(path.replace('./','')))
 return exports
}
const {masterReadySubset}=load('excelPayrollReadySubset')
const {quickPayrollGroups}=load('excelPayrollQuickPrepare')
function freeze(value){if(value&&typeof value==='object'){Object.values(value).forEach(freeze);Object.freeze(value)}return value}

test('35 source employees offer an explicit33-ready selection while preserving both pending rows and source data',()=>{
 const {batch,context}=quickPreparationFixture(35),ids=batch.rows.map(row=>row.id)
 context.roster.rows.slice(-2).forEach(row=>{row.status='Review';row.employeeId=null;row.message='Source contact identifier requires review'})
 context.roster.rows.slice(0,33).forEach(row=>{row.status='New';row.employeeId=null})
 const snapshot=structuredClone({batch,context});freeze(batch);freeze(context)
 const result=masterReadySubset(context,ids)
 assert.equal(result.canContinue,true)
 assert.deepEqual(result.readyRowIds,ids.slice(0,33));assert.deepEqual(result.reviewRowIds,ids.slice(33))
 assert.deepEqual(result.appliedRowIds,[])
 assert.equal(ids.length,35);assert.equal(batch.rows.length,35)
 assert.deepEqual({batch,context},snapshot)
})

test('pending rows are never silently selected as ready and unselected reviews do not constrain a clean selection',()=>{
 const {context}=quickPreparationFixture(3)
 context.roster.rows[2].status='Review'
 assert.deepEqual(masterReadySubset(context,['row-1','row-2']).readyRowIds,['row-1','row-2'])
 assert.equal(masterReadySubset(context,['row-1','row-2']).canContinue,false)
 const mixed=masterReadySubset(context,['row-3','row-2'])
 assert.deepEqual(mixed.readyRowIds,['row-2']);assert.deepEqual(mixed.reviewRowIds,['row-3'])
 assert.equal(mixed.canContinue,true)
})

test('there is no continue action when every selected row needs review or when no review is pending',()=>{
 const {context}=quickPreparationFixture(2)
 assert.equal(masterReadySubset(context,['row-1','row-2']).canContinue,false)
 context.roster.rows.forEach(row=>{row.status='Review'})
 const blocked=masterReadySubset(context,['row-1','row-2'])
 assert.equal(blocked.canContinue,false);assert.deepEqual(blocked.readyRowIds,[])
})

test('already applied rows are not reintroduced as ready and a complete applied cohort can remain untouched',()=>{
 const {context}=quickPreparationFixture(4)
 context.roster.rows[1].status='Review';context.roster.rows[2].status='Applied';context.roster.rows[3].status='Applied'
 context.appliedSetups=[['row-3','row-4']]
 const result=masterReadySubset(context,['row-1','row-2','row-3','row-4'])
 assert.equal(result.canContinue,true)
 assert.deepEqual(result.readyRowIds,['row-1']);assert.deepEqual(result.appliedRowIds,['row-3','row-4'])
 assert.deepEqual(context.appliedSetups,[['row-3','row-4']])
})

test('a selection containing only part of an applied cohort cannot advertise a ready-subset action',()=>{
 const {context}=quickPreparationFixture(4)
 context.roster.rows[1].status='Review';context.roster.rows[2].status='Applied';context.roster.rows[3].status='Applied'
 context.appliedSetups=[['row-3','row-4']]
 const result=masterReadySubset(context,['row-1','row-2','row-3'])
 assert.equal(result.canContinue,false)
 assert.match(result.reason,/part.*saved setup|complete saved configuration/i)
 assert.throws(()=>quickPayrollGroups(context,['row-1','row-2','row-3']),/part of a saved configuration/)
})

test('receipt evidence protects applied rows even if a stale roster calls them Existing or New',()=>{
 const {context}=quickPreparationFixture(4)
 context.roster.rows[0].status='New';context.roster.rows[1].status='Review'
 context.appliedSetups=[['row-3','row-4']]
 const result=masterReadySubset(context,['row-1','row-2','row-3','row-4'])
 assert.equal(result.canContinue,true)
 assert.deepEqual(result.readyRowIds,['row-1'])
 assert.deepEqual(result.appliedRowIds,['row-3','row-4'])
})

test('unrelated applied configurations do not block a selected clean/pending cohort',()=>{
 const {context}=quickPreparationFixture(4)
 context.roster.rows[1].status='Review';context.appliedSetups=[['row-3','row-4']]
 assert.equal(masterReadySubset(context,['row-1','row-2']).canContinue,true)
})

test('larger queue grouping still preserves its100-row limits and saved cohort boundaries',()=>{
 const {batch,context}=quickPreparationFixture(205),ids=batch.rows.map(row=>row.id)
 context.roster.rows[204].status='Review';context.appliedSetups=[['row-1','row-2']]
 context.roster.rows[0].status=context.roster.rows[1].status='Applied'
 const result=masterReadySubset(context,ids)
 assert.equal(result.canContinue,true);assert.equal(result.readyRowIds.length,202)
 assert.deepEqual(quickPayrollGroups(context,result.readyRowIds).map(rows=>rows.length),[100,100,2])
 assert.deepEqual(context.appliedSetups,[['row-1','row-2']])
})

test('empty, duplicate, missing or ambiguous readiness snapshots cannot change the review selection',()=>{
 const {context}=quickPreparationFixture(3)
 context.roster.rows[2].status='Review'
 for(const ids of [[],['row-1','row-1','row-3'],['row-1','missing','row-3']]){
  const result=masterReadySubset(context,ids)
  assert.equal(result.canContinue,false);assert.ok(result.reason)
 }
 const duplicate=structuredClone(context);duplicate.roster.rows.push({...duplicate.roster.rows[0]})
 assert.equal(masterReadySubset(duplicate,['row-1','row-3']).canContinue,false)
 const missing=structuredClone(context);delete missing.roster
 assert.equal(masterReadySubset(missing,['row-1','row-3']).canContinue,false)
})
