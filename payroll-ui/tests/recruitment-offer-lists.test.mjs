import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentOfferLists.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { allRecruitmentOffers } = exports
const native = (id, extra = {}) => ({ id, clientId: 55, offerNumber: `PIPE-${id}`, status: 'Draft', candidateName: 'Synthetic candidate', ...extra })
const roster = (id, extra = {}) => ({ sourceType: 'Roster', sourceId: id, rosterRowId: id, clientId: 55, offerNumber: `BULK-${id}`, offerStatus: 'Released', candidateName: 'Synthetic roster candidate', offeredCtc: 120000, currency: 'INR', acceptanceRecorded: false, ...extra })

test('All offers contains both bulk released and accepted letters when no pipeline offers exist', () => {
  const source = [roster(7), roster(8, { offerStatus: 'Accepted', acceptanceRecorded: true })]
  const before = structuredClone(source)
  const rows = allRecruitmentOffers([], source, 55)
  assert.deepEqual(rows.map(row => [row.id, row.offerNumber, row.status]), [['roster:7', 'BULK-7', 'Released'], ['roster:8', 'BULK-8', 'Accepted']])
  assert.equal(rows[0].offeredCtc, 120000)
  assert.equal(rows.every(row => row.nativeOffer == null), true)
  assert.deepEqual(source, before)
})

test('native and roster IDs cannot collide, joining-native rows are not duplicated, and client scope is retained', () => {
  const offers = [native(7), native(9, { clientId: 91 })]
  const rows = allRecruitmentOffers(offers, [roster(7), roster(7), roster(12, { clientId: 91 }), { ...roster(7), sourceType: 'Offer', offerId: 7 }, roster(0)], 55)
  assert.deepEqual(rows.map(row => row.id), ['offer:7', 'roster:7'])
  assert.equal(rows[0].nativeOffer, offers[0])
  assert.equal(rows[0].nativeOffer.id, 7)
  assert.equal(rows[1].nativeOffer, undefined)
  assert.equal(rows[1].status, 'Released')
})
