import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentPipelineJourney.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const lanes = ['Work order', 'Request approval', 'JD publishing', 'Screening', 'PI review', 'Interview', 'Offer', 'Joining', 'Hired', 'Not selected'].map((stageName, index) => ({ stageId: 112 + index, pipelineVersionId: 13, stageName, displayOrder: index + 1, cardScope: index < 3 ? 'Position' : 'Application' }))
test('the complete hiring map retains all ten stages across the position/candidate hand-off', () => {
  const before = JSON.stringify(lanes)
  assert.equal(exports.recruitmentJourneyLanes(lanes, 'hiring').length, 10)
  assert.deepEqual(exports.recruitmentJourneyLanes(lanes, 'hiring').map(l => l.stageId), lanes.map(l => l.stageId))
  assert.equal(JSON.stringify(lanes), before)
})
test('candidate working view keeps seven candidate stages without duplicating hiring-demand cards', () => {
  const candidate = exports.recruitmentJourneyLanes(lanes, 'candidates')
  assert.equal(candidate.length, 7)
  assert.ok(candidate.every(l => l.cardScope === 'Application'))
  assert.equal(exports.recruitmentJourneyLanes([], 'hiring').length, 0)
})
