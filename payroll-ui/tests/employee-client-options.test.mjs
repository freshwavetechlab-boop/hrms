import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'

const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/employeeClientOptions.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports)
const {employeeClientOptions}=exports
const clients=[{id:7,name:'Research Office',isActive:true},{id:8,name:'Empty client',isActive:true},{id:9,name:'Another location',isActive:true}]

test('employee clients retain full-roster counts for main and Manage selectors, with empty clients remaining zero',()=>{
 const rows=[{id:1,clientId:7,isActive:true},{id:2,clientId:7,isActive:true},{id:3,clientId:9,isActive:true}]
 const options=employeeClientOptions(clients,rows)
 assert.deepEqual(options.map(row=>[row.value,row.count]),[[0,3],[7,2],[8,0],[9,1]])
 assert.equal(options.find(row=>row.value===9).label,'Another location')
 assert.deepEqual(options.filter(row=>row.value!==0).map(row=>row.count),[2,0,1])
})

test('duplicate, inactive and out-of-scope records never inflate or leak client counts',()=>{
 const rows=[{id:1,clientId:7,isActive:true},{id:1,clientId:7,isActive:true},{id:2,clientId:7,isActive:false},{id:3,clientId:99,isActive:true},{id:0,clientId:8,isActive:true}]
 assert.deepEqual(employeeClientOptions([...clients,{id:10,name:'Inactive',isActive:false}],rows).map(row=>[row.value,row.count]),[[0,1],[7,1],[8,0],[9,0]])
})

test('client names appear before roster data arrives and later counts do not change the selected client value',()=>{
 const early=employeeClientOptions(clients,[]),late=employeeClientOptions(clients,[{id:1,clientId:7,isActive:true}])
 assert.deepEqual(early.map(row=>row.value),late.map(row=>row.value))
 assert.equal(early.find(row=>row.value===7).label,'Research Office')
 assert.equal(late.find(row=>row.value===7).count,1)
})
