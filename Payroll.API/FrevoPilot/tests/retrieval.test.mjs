import test from 'node:test'
import assert from 'node:assert/strict'
import { relevantRelationshipHints, scoreCatalogTable, selectRelevantCatalog } from '../analytics/retrieval.mjs'

const question = 'Count RRU candidate applications by CurrentStage. All dates.'
const table = (name, columns) => ({ name, columns: columns.map(name => ({ name, type: 'varchar', sensitive: false })) })
const applications = () => table('recruitment_candidate_applications', ['Id', 'ClientId', 'ApplicationType', 'CurrentStage'])
const clients = () => table('clients', ['Id', 'Code', 'Name'])
const crowd = () => Array.from({ length: 14 }, (_, index) =>
  table(`recruitment_stage_${index}`, ['Id', 'CandidateId', 'ApplicationId', 'CurrentStage']))

test('crowded CurrentStage retrieval retains the authorized client dependency within twelve tables', () => {
  const catalog = [applications(), ...crowd(), clients()]
  const before = structuredClone(catalog)
  const rawRank = catalog.map(item => ({ ...item, score: scoreCatalogTable(question, item) }))
    .sort((left, right) => right.score - left.score)
  assert.ok(rawRank.findIndex(item => item.name === 'clients') >= 12)

  const selected = selectRelevantCatalog(question, catalog)
  assert.equal(selected.length, 12)
  assert.equal(selected[0].name, 'recruitment_candidate_applications')
  assert.ok(selected.some(item => item.name === 'clients'))
  assert.ok(selected[0].columns.some(column => column.name === 'CurrentStage'))
  assert.ok(relevantRelationshipHints(selected).includes('recruitment_candidate_applications.ClientId -> clients.Id'))
  assert.deepEqual(catalog, before)
})

test('selection never synthesizes a missing client table or duplicates an existing one', () => {
  assert.equal(selectRelevantCatalog(question, [applications(), ...crowd()]).some(item => item.name === 'clients'), false)
  const selected = selectRelevantCatalog(question, [applications(), clients()])
  assert.equal(selected.length, 2)
  assert.equal(selected.filter(item => item.name === 'clients').length, 1)
})

test('client retention requires a declared relationship with exposed actual endpoint columns', () => {
  const missingKey = applications()
  missingKey.columns = missingKey.columns.filter(column => column.name !== 'ClientId')
  const protectedKey = applications()
  protectedKey.columns.find(column => column.name === 'ClientId').sensitive = true
  const missingParentKey = clients()
  missingParentKey.columns = missingParentKey.columns.filter(column => column.name !== 'Id')
  const unknownChild = table('recruitment_unknown_applications', ['Id', 'ClientId', 'ApplicationType', 'CurrentStage'])
  for (const catalog of [
    [missingKey, ...crowd(), clients()],
    [protectedKey, ...crowd(), clients()],
    [applications(), ...crowd(), missingParentKey],
    [unknownChild, ...crowd(), clients()],
  ]) {
    const selected = selectRelevantCatalog(question, catalog)
    assert.equal(selected.length, 12)
    assert.equal(selected.some(item => item.name === 'clients'), false)
  }
})

test('unrelated retrieval retains the original ranking and cap', () => {
  const catalog = [table('workflowtasks', ['Id', 'Stage']), ...crowd(), clients()]
  const expected = catalog.map(item => ({ ...item, score: scoreCatalogTable(question, item) }))
    .sort((left, right) => right.score - left.score).slice(0, 12)
  assert.deepEqual(selectRelevantCatalog(question, catalog), expected)
  assert.deepEqual(selectRelevantCatalog(question, []), [])
})
