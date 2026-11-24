import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentRosterDraftJobs.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports)
const request={id:7,clientId:17,sourceType:'Employee List JD',latestJobDescriptionVersionId:71,positionTitle:'Research Assistant',rfrNumber:'RFR-SAMPLE-7',status:'Draft'}
test('an employee-list JD appears as an unpublished preparation row, with no public URL or portal',()=>{
 const rows=exports.withRosterDraftJobs([], [request],17)
 assert.equal(rows.length,1); assert.equal(rows[0].id,-7); assert.equal(rows[0].status,'Draft')
 assert.equal(rows[0].publicUrl,''); assert.equal(rows[0].publicSlug,''); assert.equal(rows[0].candidatePortalReady,false)
 assert.equal(exports.rosterDraftRequest(rows[0]),request)
})
test('a real job posting replaces the preparation row and client boundaries and closed requests remain enforced',()=>{
 const native={id:7,clientId:17,positionId:88,status:'Published'}
 const rows=exports.withRosterDraftJobs([native],[{...request,openPositionId:88},{...request,id:8,clientId:208},{...request,id:9,status:'Withdrawn'},{...request,id:10,sourceType:'Manual'}],17)
 assert.deepEqual(rows,[native]); assert.equal(exports.rosterDraftRequest(native),undefined)
})
