import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
const code = ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentDocumentLayout.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', code)(exports)
const { readDocumentLayout, suggestRosterMapping, documentLayoutMarker } = exports
test('layout upload keeps configured pages, input definitions and literal placeholders', () => {
  const source = { version: 1, inputs: [{ key: 'candidateName', label: 'Candidate' }], sections: [{ pages: 4, blocks: [{ text: '{{candidateName}}' }] }, { pages: 1 }, { pages: 1 }, { pages: 1 }, { pages: 1 }] }
  const result = readDocumentLayout(JSON.stringify(source))
  assert.equal(result.pages, 8); assert.equal(result.fields[0].key, 'candidateName'); assert.ok(result.body.startsWith(documentLayoutMarker)); assert.match(result.body, /\{\{candidateName\}\}/)
  assert.equal(readDocumentLayout(result.body).body, result.body)
})
test('ambiguous, blank and inserted headers require an explicit mapping rather than a guessed identity', () => {
  const fields = [{ key: 'candidateName', sourceHeaders: ['Name of Candidate'] }, { key: 'monthlyBasic', sourceHeaders: ['Basic / Consolidated (₹)'] }, { key: 'accountNo', sourceHeaders: ['Account No.'] }]
  assert.deepEqual(suggestRosterMapping(fields, ['', 'Name of\nCandidate', 'Account No.', 'Basic / Consolidated (₹)']), { candidateName: 1, monthlyBasic: 3, accountNo: 2 })
  assert.equal(suggestRosterMapping(fields, ['Name of Candidate', 'Name of Candidate']).candidateName, -1)
})
test('invalid and oversized layout JSON is not accepted as a configured document', () => {
  assert.throws(() => readDocumentLayout('not json'))
  assert.throws(() => readDocumentLayout('{"version":2,"inputs":[],"sections":[{}]}'), /version 1/)
  assert.throws(() => readDocumentLayout(JSON.stringify({ version: 1, inputs: [], sections: [{ text: 'x'.repeat(65000) }] })), /64 KB/)
})
