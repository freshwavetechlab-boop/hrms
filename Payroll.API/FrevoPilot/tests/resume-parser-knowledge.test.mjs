import {test} from 'node:test'
import assert from 'node:assert/strict'
import {relevantKnowledge} from '../analytics/knowledge.mjs'
test('resume identity/location and bulk zero lessons are retrieved as anonymized support knowledge',async()=>{
 for(const question of ['Resume parser Technology Leader Contact Information name PDF columns location company','Bulk resume draft remove missing current CTC expected CTC notice period zero','Resume Skills sidebar Artificial intelligence design company Current Present employment timeline LLM SLM degree','Published job link resume parsing same shared admin bulk parser','Qwen Quinn supervised feedback verified recruiter corrections API memory self learning encrypted resume','Resume photograph qualification MS Teams ABOUT ME PhD completed degree long CPU model excerpts','Bulk preview job AI parsing enabled before final upload company designation qualification skills']){
  const rows=await relevantKnowledge(question)
  assert.ok(rows.some(row=>row.source.endsWith('resume-parsing-patterns.md')),question)
 }
})
