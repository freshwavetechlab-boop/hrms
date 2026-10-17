import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/utils/recruitmentJourney.ts', import.meta.url), 'utf8')
const { recruitmentJourney } = await import(`data:text/javascript;base64,${Buffer.from(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText).toString('base64')}`)
const visit = (id, stage, order, status = 'Completed') => ({ id, pipelineStageId: order, stageCode: stage, stageName: stage, displayOrder: order, enteredAtUtc: `2026-10-17T10:${String(id).padStart(2, '0')}:00Z`, status })

test('Repeated backward and forward moves preserve every visit in chronological order', () => {
  const input = [visit(6, 'INTERVIEW', 3, 'Active'), visit(2, 'INTERVIEW', 3), visit(4, 'INTERVIEW', 3), visit(1, 'SOURCE', 1), visit(5, 'SOURCE', 1), visit(3, 'SOURCE', 1)]
  const rows = recruitmentJourney(input)
  assert.deepEqual(rows.map(item => item.row.id), [1, 2, 3, 4, 5, 6])
  assert.deepEqual(rows.map(item => item.direction), ['start', 'forward', 'back', 'forward', 'back', 'forward'])
  assert.deepEqual(rows.map(item => item.visit), [1, 1, 2, 2, 3, 3])
  assert.equal(input[0].id, 6)
})

test('Unvisited placeholders are excluded; reopened and superseded visits retain their history', () => {
  const rows = recruitmentJourney([visit(1, 'SOURCE', 1), visit(2, 'SOURCE', 1, 'Paused'), visit(3, 'FUTURE', 4, 'Pending'), { ...visit(4, 'FUTURE', 4), enteredAtUtc: null }, visit(5, 'OLD', 5, 'Superseded')])
  assert.equal(rows.length, 3)
  assert.equal(rows[1].direction, 'repeat')
  assert.equal(rows[1].visit, 2)
  assert.equal(rows[2].row.status, 'Superseded')
})

test('Same timestamp uses visit identity to retain the recorded sequence', () => {
  const first = visit(1, 'SOURCE', 1), next = { ...visit(2, 'INTERVIEW', 2), enteredAtUtc: first.enteredAtUtc }
  assert.deepEqual(recruitmentJourney([next, first]).map(item => item.row.id), [1, 2])
})
