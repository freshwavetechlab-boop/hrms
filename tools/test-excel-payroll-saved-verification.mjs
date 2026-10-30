import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = process.env.HRMS_REPO_ROOT || fileURLToPath(new URL('../', import.meta.url))
const require = createRequire(import.meta.url), ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const file = process.env.HRMS_TARGET_HELPER || path.join(root, 'payroll-ui/src/utils/excelPayrollSavedVerification.ts'), module = { exports: {} }
new Function('module', 'exports', ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(module, module.exports)
const { savedPayrollVerificationTargets: targets } = module.exports
const batch = { id: 'saved-september', clientId: 33, month: '2026-09', rows: [1, 2, 3, 4, 5].map(n => ({ id: 'r'+n, sourceRow: n+2, employeeCode: 'SOURCE'+n, employment: { employeeId: n < 3 ? n+10 : null, employeeCode: 'MASTER'+n, identityReviewRequired: n===5, blockedForMonth: false } })) }
const result = { importId: 'receipt', employeeIds: [15, 13, 11, 14, 12] }
const rowIds = batch.rows.map(row => row.id)
const context = { calculationOnlySimulationSupported: true, employees: [1,2,3,4,5].map(n => ({ id: n+10, employeeCode: 'MASTER'+n })), roster: { rows: batch.rows.map((row,n) => ({rowId: row.id, employeeId: n+11, status: 'Applied'})) }, lastImport: { importId: 'receipt', status: 'Applied', result, savedRequest: { batchId: batch.id, rowIds, employeeDecisions: rowIds.map((rowId,n) => ({rowId, matchedEmployeeId: n+11, allowCreate: false})) } } }
const run = (b=batch,c=context,r=result,ids=rowIds,clientId=33) => targets(clientId,b,ids,c,r)
const before=JSON.stringify({batch,context,result}); let groups=0
assert.deepEqual(run().filter(t=>t.eligible).map(t=>[t.rowId,t.employeeId]),[['r1',11],['r2',12]]); groups++
const reversed=structuredClone(context); reversed.roster.rows.reverse(); reversed.employees.reverse(); assert.deepEqual(run(batch,reversed).filter(t=>t.eligible).map(t=>t.employeeId),[11,12]); groups++
assert.equal(run(batch,context,result,rowIds,44).filter(t=>t.eligible).length,0); groups++
for(const edit of [c=>c.calculationOnlySimulationSupported=false,c=>c.lastImport.status='Draft',c=>c.lastImport.importId='other',c=>c.lastImport.savedRequest.batchId='other',c=>c.lastImport.savedRequest.rowIds=['r1'],c=>c.lastImport.result={importId:'other'}]){const c=structuredClone(context);edit(c);assert.equal(run(batch,c).some(t=>t.eligible),false)} groups++
const stale=structuredClone(context); stale.lastImport.savedRequest.employeeDecisions[0].matchedEmployeeId=99; assert.equal(run(batch,stale)[0].eligible,false); groups++
for(const edit of [b=>b.rows[0].employment.identityReviewRequired=true,b=>b.rows[0].employment.blockedForMonth=true,b=>b.rows[0].employment.employeeId=99,b=>b.rows[0].employment.employeeCode='changed',b=>b.rows[0].employeeCode='']){const b=structuredClone(batch);edit(b);assert.equal(run(b)[0].eligible,false)} groups++
const duplicate=structuredClone(batch); duplicate.rows[1].employeeCode=duplicate.rows[0].employeeCode; assert.equal(run(duplicate).filter(t=>t.eligible).length,0); groups++
const repeatedSource=structuredClone(batch); repeatedSource.rows[1].sourceRow=repeatedSource.rows[0].sourceRow; assert.equal(run(repeatedSource).filter(t=>t.eligible).length,0); groups++
const repeatId=structuredClone(batch); repeatId.rows[2].employment.employeeId=11; assert.equal(run(repeatId)[0].eligible,false); groups++
const dupMaster=structuredClone(context); dupMaster.employees.push({...dupMaster.employees[0]}); assert.equal(run(batch,dupMaster)[0].eligible,false); groups++
const review=structuredClone(context); review.roster.rows[0].status='Review'; assert.equal(run(batch,review)[0].eligible,false); groups++
const duplicateReceipt={...result,employeeIds:[11,11,12]};assert.equal(run(batch,context,duplicateReceipt)[0].eligible,false); groups++
assert.equal(run(batch,context,result,['r1','r1']).some(t=>t.eligible),false); assert.equal(run(batch,context,result,[]).length,0); groups++
const transfer=structuredClone(result); transfer.transferRows=[{rowId:'r1',employeeId:11}]; const created=structuredClone(context); created.lastImport.savedRequest.employeeDecisions[0].matchedEmployeeId=undefined; assert.equal(run(batch,created,transfer)[0].eligible,true); transfer.transferRows[0].employeeId=99; assert.equal(run(batch,created,transfer)[0].eligible,false); groups++
assert.equal(JSON.stringify({batch,context,result}),before); groups++
console.log(`PASS: ${groups} saved-receipt identity groups; only 2/5 synthetic rows selected, order-independent binding, stale/client/runtime/identity/receipt guards, no network or writes.`)
