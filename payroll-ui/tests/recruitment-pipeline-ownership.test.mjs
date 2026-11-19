import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentPipelineOwnership.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)

test('legacy stages remain unassigned instead of guessing responsibility from the stage name or type', () => {
  assert.equal(exports.pipelineStageOwnership().ownerType, 'Unassigned')
  assert.equal(exports.pipelineStageOwnership({stageType:'Approval',stageName:'Client approval'}).ownerType, 'Unassigned')
})
test('responsibility has one consistent color in the designer, hiring flow and candidate board', () => {
  for (const owner of exports.pipelineOwnershipLegend) {
    const policy={ownerType:owner.ownerType,departmentLabel:'Project Cell',restricted:true,allowedRoleCodes:['project_cell'],allowedUserIds:[]}
    const before=JSON.stringify(policy)
    const resolved=exports.pipelineStageOwnership({accessPolicy:policy})
    assert.equal(resolved.color,owner.color)
    assert.match(resolved.label,/Project Cell/)
    assert.equal(JSON.stringify(policy),before)
  }
  assert.equal(new Set(exports.pipelineOwnershipLegend.map(row=>row.color)).size,4)
})
