import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentJoiningSelection.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { joiningRecords, releasedJoiningRecords, joiningAcceptanceSelection } = exports
const row = (sourceType, sourceId, extra = {}) => ({ sourceType, sourceId, offerId: sourceType === 'Offer' ? sourceId : 0, rosterRowId: sourceType === 'Roster' ? sourceId : null, clientId: 42, offerStatus: 'Released', acceptanceRecorded: false, ...extra })

test('offer and roster IDs remain distinct and only the selected client and valid source coordinates survive', () => {
  const source = [row('Offer', 7), row('Roster', 7), row('Offer', 8, { clientId: 99 }), row('Roster', 0)]
  const before = JSON.stringify(source), records = joiningRecords(source, 42)
  assert.deepEqual(records.map(row => row.id), ['offer:7', 'roster:7'])
  assert.equal(JSON.stringify(source), before)
  assert.deepEqual(joiningAcceptanceSelection(records, ['roster:7'], records.map(row => row.id), 42), { clientId: 42, offerIds: [], rosterRowIds: [7] })
})

test('a filtered submit contains only visible selected released letters, preserving user deselection', () => {
  const rows = joiningRecords([row('Offer', 1), row('Offer', 2), row('Roster', 3), row('Offer', 4, { acceptanceRecorded: true, offerStatus: 'Accepted' }), row('Offer', 5, { offerStatus: 'Draft' }), row('Offer', 6, { offerStatus: 'Rejected' })], 42)
  const selected = ['offer:1', 'offer:2', 'roster:3', 'offer:4', 'offer:5', 'offer:6']
  assert.deepEqual(releasedJoiningRecords(rows).map(row => row.id), ['offer:1', 'offer:2', 'roster:3'])
  assert.deepEqual(joiningAcceptanceSelection(rows, selected, ['offer:2', 'roster:3'], 42), { clientId: 42, offerIds: [2], rosterRowIds: [3] })
  assert.deepEqual(joiningAcceptanceSelection(rows, ['offer:2'], ['offer:2', 'roster:3'], 42), { clientId: 42, offerIds: [2], rosterRowIds: [] })
})
