import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}, code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  modules.set(name, exports)
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { buildRosterImportTemplate, rosterImportTemplateHeaders, templateRosterMapping, isRosterImportTemplate, downloadRosterImportTemplate } = load('recruitmentRosterTemplate')
const xlsx = load('xlsx')
const field = (key, label, sourceHeaders = [], type = 'text', required = false, defaultValue = '') => ({ key, label, sourceHeaders, type, required, defaultValue, blankText: '' })
const fixture = () => ({ id: 7, clientId: 30, templateName: 'Synthetic offer', importPresets: [], fields: [
  field('candidateName', 'Employee name', ['Name', 'Employee Name'], 'text', true),
  field('candidateEmail', 'Email', ['Email']), field('candidatePhone', 'Phone', ['Mobile']),
  field('positionTitle', 'Designation', ['Designation'], 'text', true),
  field('joiningDate', 'Joining date', ['DOJ'], 'date', true),
  field('monthlyGross', 'Monthly gross', ['Gross'], 'money', true),
  field('serviceCharge', 'Service charge', ['Service Charge'], 'percent', false, '0.10'),
  field('tenureMonths', 'Tenure', [], 'number'),
] })
const value = cell => typeof cell === 'object' ? cell.value : cell

test('runtime document inputs export exactly once and roundtrip with unambiguous configured headers', () => {
  const template = fixture(), before = JSON.stringify(template)
  const sheets = buildRosterImportTemplate(template)
  const headers = sheets[0].rows[0].map(value)
  assert.equal(sheets[0].name, 'Employees')
  assert.equal(sheets[0].freezeRows, 1)
  assert.deepEqual(headers, ['Name', 'Email', 'Mobile', 'Designation', 'DOJ', 'Gross', 'Service Charge', 'Tenure [tenureMonths]', 'Location'])
  assert.deepEqual(templateRosterMapping(template.fields, headers), Object.fromEntries(template.fields.map((field, index) => [field.key, index])))
  assert.equal(isRosterImportTemplate(template, headers), true)
  assert.equal(isRosterImportTemplate(template, [...headers].reverse()), false)
  assert.equal(isRosterImportTemplate(template, headers.slice(1)), false)
  assert.equal(isRosterImportTemplate(template, [...headers, 'Unexpected column']), false)
  assert.equal(JSON.stringify(template), before, 'export must not mutate selected configuration')
})

test('shared aliases and duplicate labels use separate canonical headers without assigning one column twice', () => {
  const template = fixture()
  template.fields.push(field('contractEnd', 'Date', ['Date'], 'date'), field('extensionEnd', 'Date', ['Date'], 'date'))
  template.fields.push(field('alternateName', 'Alternate name', ['Name', 'Other name']))
  const headers = rosterImportTemplateHeaders(template.fields)
  assert.equal(headers[0], 'Employee Name', 'use an alternative alias unique to the original name field')
  assert.equal(headers[8], 'Date [contractEnd]')
  assert.equal(headers[9], 'Date [extensionEnd]')
  assert.equal(headers[10], 'Other name')
  assert.equal(new Set(headers).size, template.fields.length + 1)
  const reversed = [...headers].reverse()
  assert.deepEqual(templateRosterMapping(template.fields, reversed), Object.fromEntries(template.fields.map((field, index) => [field.key, headers.length - index - 1])))
  assert.equal(templateRosterMapping([template.fields[7]], ['Tenure [tenureMonths]', 'Tenure [tenureMonths]']).tenureMonths, -1, 'duplicate columns still require explicit review')
})

test('employee area is blank while instructions explain readiness, formats, context and import-batch updates', () => {
  const template = fixture(), sheets = buildRosterImportTemplate(template, { sourceGroup: 'Synthetic institute list', workLocationName: 'Synthetic master location', defaults: { tenureMonths: '12' } })
  assert.equal(sheets[0].rows.length, 26)
  for (const row of sheets[0].rows.slice(1)) {
    assert.equal(row.length, template.fields.length + 1)
    for (const cell of row) assert.deepEqual(cell, { value: '', type: 'text', style: 'text' })
  }
  assert.deepEqual(sheets.map(sheet => sheet.name), ['Employees', 'Instructions'], 'no employee example sheet may accidentally be imported')
  const notes = sheets[1].rows.flat().map(value).join('\n')
  for (const expected of ['Synthetic institute list', 'Synthetic master location', 'header row 1', 'valid email or phone', 'Names alone never merge', 'Import batch', 'November joiners', 'separate from the client, JD and work location', 'same client, document template and import batch', 'YYYY-MM-DD', '30% or 0.30', 'Monthly wages remain monthly', 'before release', 'Required for import', 'Required for letter', '12']) assert.ok(notes.includes(expected), expected)
  assert.doesNotMatch(notes, /source group/i)
  assert.match(notes, /reuse a matching master in the same client/)
  assert.match(notes, /new master is created when your access allows it/)
  assert.match(notes, /applies it to all imported rows and overrides their source location names/)
  assert.match(notes, /Preview checks location names without saving or creating masters/)
  assert.doesNotMatch(notes, /does not link a work-location master/)
  assert.ok(sheets[1].rows.some(row => value(row[0]) === 'Tenure [tenureMonths]' && value(row[4]) === '12'))
  assert.equal(isRosterImportTemplate({ ...template, fields: [] }, []), false)
  assert.throws(() => buildRosterImportTemplate({ ...template, fields: [] }), /no employee input fields/)
})

test('generated XLSX contains text input cells and no formulas or prefilled employee identities', async () => {
  const blob = xlsx.buildXlsxBlob(buildRosterImportTemplate(fixture()))
  assert.equal(blob.type, 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')
  const data = new Uint8Array(await blob.arrayBuffer()), view = new DataView(data.buffer), entries = new Map()
  let offset = 0
  while (view.getUint32(offset, true) === 0x04034b50) {
    const length = view.getUint32(offset + 18, true), nameLength = view.getUint16(offset + 26, true), extra = view.getUint16(offset + 28, true), start = offset + 30 + nameLength + extra
    entries.set(new TextDecoder().decode(data.slice(offset + 30, offset + 30 + nameLength)), new TextDecoder().decode(data.slice(start, start + length)))
    offset = start + length
  }
  const employeeXml = entries.get('xl/worksheets/sheet1.xml')
  assert.match(employeeXml, /topLeftCell="A2"/)
  assert.match(employeeXml, /<c r="C2" s="4" t="inlineStr"><is><t xml:space="preserve"><\/t>/)
  assert.equal((employeeXml.match(/<row /g) || []).length, 26)
  assert.doesNotMatch(employeeXml, /<f>|example@|25000\.50|2026-12-01/)
  assert.match(entries.get('xl/workbook.xml'), /name="Employees".*name="Instructions"/)
})

test('download uses selected template and retains an xlsx extension with a safe bounded name', () => {
  let downloaded
  const original = xlsx.downloadXlsx
  try {
    xlsx.downloadXlsx = (fileName, sheets) => { downloaded = { fileName, sheets } }
    const template = fixture()
    template.templateName = 'Unsafe:/\\*?' + 'x'.repeat(200)
    downloadRosterImportTemplate(template)
    assert.match(downloaded.fileName, /-import-template\.xlsx$/)
    assert.doesNotMatch(downloaded.fileName, /[<>:"/\\|?*]/)
    assert.ok(downloaded.fileName.length < 180)
    assert.equal(downloaded.sheets[0].rows[0].length, template.fields.length + 1)
  } finally { xlsx.downloadXlsx = original }
})

test('templates without a location input add import-only Location metadata without an unknown document mapping', () => {
  const template = fixture(), sheets = buildRosterImportTemplate(template), headers = sheets[0].rows[0].map(value)
  assert.equal(headers.at(-1), 'Location')
  assert.equal(isRosterImportTemplate(template, headers), true)
  assert.equal(isRosterImportTemplate(template, headers.slice(0, -1)), false)
  const mapping = templateRosterMapping(template.fields, headers)
  assert.deepEqual(Object.keys(mapping), template.fields.map(field => field.key))
  assert.equal(Object.hasOwn(mapping, 'Location'), false)
  assert.equal(Object.hasOwn(mapping, 'jobLocation'), false)
  assert.ok(sheets[1].rows.some(row => value(row[0]) === 'Location' && value(row[1]) === 'Import metadata'))
  assert.equal(isRosterImportTemplate({ ...template, fields: [] }, ['Location']), false)
  for (const location of [
    field('jobLocation', 'Posting', ['Workplace']),
    field('workLocationName', 'Posting', ['Workplace']),
    field('customInstitute', 'Institute name'),
    field('customPosting', 'Posting', ['Office location']),
    field('customInstituteName', 'Posting', ['Name of the institute']),
    field('customInstitutionName', 'Name of the institution'),
    field('customPostingLocation', 'Posting location'),
  ]) {
    const configured = { ...template, fields: [...template.fields, location] }
    const configuredHeaders = rosterImportTemplateHeaders(configured.fields)
    assert.equal(configuredHeaders.length, configured.fields.length, `reuse configured location input ${location.key}`)
    assert.equal(templateRosterMapping(configured.fields, configuredHeaders)[location.key], configured.fields.length - 1)
  }
  const unrelated = { ...template, fields: [...template.fields, field('currentLocation', 'Current location'), field('locationRate', 'Location rate')] }
  assert.equal(rosterImportTemplateHeaders(unrelated.fields).length, unrelated.fields.length + 1, 'personal current location or location rate must not suppress work-location metadata')
})
