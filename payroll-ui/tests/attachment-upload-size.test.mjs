import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/attachmentUploadSize.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { attachmentUploadSizeError: validate } = exports
const MB = 1024 * 1024
const field = { id: 89, fieldLabel: 'Aadhaar Card', maximumFileSizeBytes: 10 * MB, maximumTotalSizeBytes: null }

test('Employee 1 MB and Joining 10 MB rules permit exact size and reject one extra byte with the document and limit', () => {
  for (const maximumFileSizeBytes of [MB, 10 * MB]) {
    const rule = { ...field, maximumFileSizeBytes }
    assert.equal(validate(rule, { size: maximumFileSizeBytes }), '')
    assert.match(validate(rule, { size: maximumFileSizeBytes + 1 }), new RegExp(`Aadhaar Card:.*${maximumFileSizeBytes / MB} MB`))
  }
})

test('combined limit counts existing files only for the same configuration and permits exact total', () => {
  const rule = { ...field, maximumTotalSizeBytes: 15 * MB }
  const files = [{ fieldConfigurationId: 89, fileSizeBytes: 5 * MB }, { fieldConfigurationId: 1, fileSizeBytes: 100 * MB }]
  assert.equal(validate(rule, { size: 10 * MB }, files), '')
  assert.match(validate(rule, { size: 10 * MB }, [{ fieldConfigurationId: 89, fileSizeBytes: 5 * MB + 1 }]), /combined.*15 MB/)
})

test('empty files get an actionable message and disabled combined limits do not invent a cap', () => {
  assert.match(validate(field, { size: 0 }), /non-empty/)
  assert.equal(validate(field, { size: MB }, [{ fieldConfigurationId: 89, fileSizeBytes: 100 * MB }]), '')
})
