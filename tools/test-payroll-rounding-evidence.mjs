import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import {createRequire} from 'node:module'
import {fileURLToPath} from 'node:url'
const root=fileURLToPath(new URL('../',import.meta.url)), require=createRequire(import.meta.url), ts=require(path.join(root,'payroll-ui/node_modules/typescript/lib/typescript.js'))
function load(relative){const filename=path.join(root,'payroll-ui/src',relative),module={exports:{}};const code=ts.transpileModule(fs.readFileSync(filename,'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText;new Function('require','module','exports',code)(name=>{if(name==='./excelPayslipImport')return load('utils/excelPayslipImport.ts');throw Error('Unexpected dependency '+name)},module,module.exports);return module.exports}
const {payrollComparisonMoney:money,payrollDisplayEvidence:evidence,payrollRawAmount:raw,payrollRawDifference:delta}=load('utils/payrollRoundingEvidence.ts')
const source={net:116.91499999999999,esic:1.0049999999999997,employer:4.355};const before=JSON.stringify(source)
assert.equal(money(source.net),'116.92');assert.equal(money(source.esic),'1.01');assert.equal(money(1),'1.00');assert.equal(money(source.employer),'4.36')
assert.equal(evidence(source.net,116.92).displayStatus,'Same at 2dp; raw parity separate');assert.ok(Math.abs(evidence(source.net,116.92).rawDifference)>0.000001)
assert.equal(evidence(source.esic,1).displayStatus,'Different at 2dp');assert.equal(delta(evidence(source.esic,1).rawDifference),'-0.005')
assert.equal(delta(evidence(source.employer,4.36).rawDifference),'0.005');assert.equal(evidence(source.employer,4.36).displayStatus,'Same at 2dp; raw parity separate')
assert.equal(raw(source.net),'116.91499999999999');assert.equal(delta(-0),'0');assert.equal(delta(2e-12),'2.000e-12')
for(const absent of [undefined,null,NaN,Infinity]){assert.equal(money(absent),'-');assert.equal(evidence(absent,1).rawDifference,null);assert.equal(evidence(absent,1).displayStatus,'Display comparison unavailable')}
assert.equal(money(1.0049),'1.00');assert.equal(money(-1.0049999999999997),'-1.01');assert.equal(JSON.stringify(source),before)
console.log('PASS: 7 rounding evidence groups: shared PDF display semantics, net/display distinction, component mismatch, employer half-paisa, visible raw/tiny delta, missing-not-zero inputs, immutable source. No requests or policy changes.')
