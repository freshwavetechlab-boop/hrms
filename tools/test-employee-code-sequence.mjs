import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root=fileURLToPath(new URL('../',import.meta.url)),require=createRequire(import.meta.url),ts=require(path.join(root,'payroll-ui/node_modules/typescript/lib/typescript.js')),modules=new Map()
function load(relative){const file=path.join(root,relative);if(modules.has(file))return modules.get(file).exports;const mod={exports:{}};modules.set(file,mod);const code=ts.transpileModule(fs.readFileSync(file,'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText;new Function('require','module','exports',code)(name=>load(path.relative(root,path.resolve(path.dirname(file),name+'.ts'))),mod,mod.exports);return mod.exports}
const {nextEmployeeCodePreview:next,inferEmployeeCodeDigits:digits}=load('payroll-ui/src/utils/smartBulkImport.ts')
assert.equal(next('nhdc',5,[]),'NHDC00001')
assert.equal(next('NHDC',3,['NHDC00009','other999']),'NHDC010')
assert.equal(digits('NHDC',['NHDC00009','OTHER000001'] ),5)
assert.equal(next('NHDC',6,['NHDC00009'],'42'),'NHDC000042')
assert.equal(next('NHDC',3,['NHDC999'],'42'),'NHDC1000')
assert.equal(next('NHDC',1,['NHDC9007199254740993']),'NHDC9007199254740994')
assert.equal(next('NHDC',12,[],'9007199254740994'),'NHDC9007199254740994')
assert.equal(next('N.H',3,['N.H09','NXH999']),'N.H010')
assert.equal(next('',3,['007']),'008')
console.log(JSON.stringify({passed:true,checks:9,description:'Shared Employee/Payslip preview preserves padding, reserved counter, prefix escaping and exact long integer identifiers; no data writes'}))
