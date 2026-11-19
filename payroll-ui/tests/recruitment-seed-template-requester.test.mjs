import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

test('downloaded hiring seed workbook requests employer-user identity without shifting salary/JD columns', () => {
  const source = fs.readFileSync(new URL('../src/services/recruitmentSeedPackService.ts', import.meta.url), 'utf8')
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  let workbook
  const exports = {}
  new Function('require', 'exports', compiled)(name => {
    if (name === './apiClient') return { postForm() { throw Error('Template download must not send a request.') } }
    if (name === '../utils/xlsx') return { downloadXlsx(fileName, sheets) { workbook = { fileName, sheets } } }
    throw Error(`Unexpected dependency: ${name}`)
  }, exports)
  exports.downloadHiringSeedTemplate()
  assert.match(workbook.fileName, /\.xlsx$/)
  const hiring = workbook.sheets.find(sheet => sheet.name === 'Hiring Requests')
  const [headers, example] = hiring.rows
  assert.equal(headers[2], 'Requester User')
  assert.equal(headers.includes('Requester Employee Code'), false)
  assert.match(example[2], /@/)
  assert.equal(headers.length, example.length)
  assert.equal(example[headers.indexOf('Budget Amount')], '1500000')
  assert.equal(example[headers.indexOf('Request Action')], 'Draft')
  const instructions = workbook.sheets.find(sheet => sheet.name === 'Instructions').rows.find(row => row[0] === 'Requester User')
  assert.match(instructions[1], /login email or numeric user ID/)
  assert.match(instructions[1], /Existing imported requests keep their saved requester/)
})
