import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = process.env.HRMS_SOURCE_ROOT || fileURLToPath(new URL('../', import.meta.url))
const require = createRequire(import.meta.url), ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js')), modules = new Map()
function load(relative) {
  const file = path.join(root, relative)
  if (modules.has(file)) return modules.get(file).exports
  const mod = { exports: {} }; modules.set(file, mod)
  const code = ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('require', 'module', 'exports', code)(name => load(path.relative(root, path.resolve(path.dirname(file), name + '.ts'))), mod, mod.exports)
  return mod.exports
}
const api = load('payroll-ui/src/utils/workbookDependencyCapture.ts')
const hash = 'a'.repeat(64), hash2 = 'b'.repeat(64), key = 'wb_' + hash, key2 = 'wb_' + hash2
const cell = (value, formulaText) => ({ value, dataType: 'Number', ...(formulaText ? { formulaText, formula: true } : {}) })
const rootSheet = (formula = 'IFERROR(VLOOKUP(A2,Attendance!A2:D3,4,FALSE),0)') => ({ name: 'Salary', columnCount: 3, rows: [{ sourceRow: 1, cells: [{ value: 'Code', dataType: 'Text' }, cell('Days'), cell('Pay')] }, { sourceRow: 2, cells: [{ value: 'Synthetic-1', dataType: 'Text' }, cell('30'), cell('28', formula)] }] })
const attendance = { name: 'Attendance', columnCount: 4, rows: [{ sourceRow: 2, cells: [{ value: 'Synthetic-1', dataType: 'Text' }, cell(''), cell(''), cell('28')] }] }
const book = (sheet = rootSheet()) => ({ key, sha256: hash, fileName: 'synthetic.xlsx', sheets: [sheet, attendance] })
const columns = [{ columnIndex: 2, kind: 'earning', sourceHeader: 'Pay', label: 'Pay' }]
const collect = (sheet, books = [book(sheet)], maps = []) => api.collectWorkbookDependencies(sheet, key, books, maps, columns, 1, [])
const checks = []
{
  const source = rootSheet(), before = JSON.stringify(source), result = collect(source)
  assert.deepEqual(result.issues, []); assert.equal(result.fields.dependencySheets[0].sheetName, 'Attendance'); assert.equal(result.needsReview, true)
  assert.equal(api.hasCompleteInternalWorkbookSources(result), true)
  assert.equal(api.workbookSourcesConfirmed(result, ''), true)
  assert.equal(JSON.stringify(source), before); assert.equal(result.fields.workbookFiles[0].sha256, hash)
  checks.push('Same-workbook graph snapshots preserve original formulas and version fingerprint')
}
{
  const source = rootSheet("VLOOKUP(A2,'[1]Attendance'!A2:D3,4,0)"), extra = { key: key2, sha256: hash2, fileName: 'synthetic.xlsx', sheets: [attendance] }
  const unresolved = collect(source, [book(source), extra]); assert.match(unresolved.issues[0], /reviewed/)
  const map = { fromWorkbookKey: key, reference: '[1]', toWorkbookKey: key2, reviewed: true }
  const resolved = collect(source, [book(source), extra], [map]); assert.equal(resolved.issues.length, 0); assert.equal(resolved.fields.workbookFiles.length, 2)
  assert.equal(api.hasCompleteInternalWorkbookSources(resolved), false)
  assert.equal(api.workbookSourcesConfirmed(resolved, ''), false)
  assert.equal(api.workbookSourcesConfirmed(resolved, resolved.reviewKey), true)
  assert.notEqual(resolved.reviewKey, unresolved.reviewKey)
  assert.equal(collect(source, [book(source), extra], [map, map]).issues.length, 1)
  const missing = collect(source, [book(source)], [map]); assert.match(missing.issues[0], /not supplied/)
  assert.equal(api.workbookSourcesConfirmed(missing, missing.reviewKey), false)
  checks.push('External aliases require one explicit reviewed exact version; missing/ambiguous mappings remain actionable')
}
{
  const source = rootSheet("VLOOKUP(A2,'[1]Attendance'!A2:D3,4,0)+VLOOKUP(A2,'[2]Attendance'!A2:D3,4,0)")
  const extra = { key: key2, sha256: hash2, fileName: 'dependency.xlsx', sheets: [attendance] }, maps = ['[1]', '[2]'].map(reference => ({ fromWorkbookKey: key, reference, toWorkbookKey: key2, reviewed: true }))
  const result = collect(source, [book(source), extra], maps)
  assert.equal(result.issues.length, 0); assert.equal(result.fields.workbookFiles.length, 2); assert.equal(result.fields.externalMappings.length, 2)
  checks.push('Multiple aliases share a retained dependency without duplicating original file bindings')
}
{
  const source = rootSheet('IF(1=1,28,"[1]NotASheet!A2")')
  source.rows[1].cells.push(cell('0', "VLOOKUP(A2,'[99]BankSupport'!A2:D3,4,0)")); source.columnCount++
  const result = collect(source); assert.equal(result.references.length, 0)
  checks.push('Quoted strings and unmapped supporting fields do not become financial dependencies')
}
{
  const source = rootSheet(), first = collect(source), edited = structuredClone(source)
  edited.rows[1].cells[2].value = '29'
  assert.equal(collect(edited).reviewKey, first.reviewKey)
  edited.rows[1].cells[2].formulaText += '+1'; assert.notEqual(collect(edited).reviewKey, first.reviewKey)
  const restored = api.restoreCapturedWorkbooks({ ...first.fields, sheetName: source.name, columnCount: 3, rows: source.rows }, source)
  assert.equal(collect(source, restored).issues.length, 0)
  checks.push('Formula changes invalidate UI review; cache corrections do not; saved dependency snapshots reopen')
}
{
  const source = rootSheet('SUM(Attendance!A1:D9999)')
  assert.throws(() => collect(source), /10,000/)
  assert.throws(() => collect(rootSheet(), [{ ...book(), sha256: 'x' + hash }]), /fingerprint/)
  assert.throws(() => collect(rootSheet(), [book(), book()]), /Duplicate/)
  assert.equal(collect(rootSheet(), []).fields, null)
  checks.push('Bounded ranges, malformed hashes, duplicate versions and absent original bindings fail closed')
}
{
  const bytes = 'Code,Attendance\n0007,28\n', file = new File([bytes], 'synthetic.csv', { type: 'text/csv' })
  const captured = await api.captureWorkbook(file)
  assert.equal(await file.text(), bytes); assert.equal(captured.key, 'wb_' + captured.sha256)
  assert.equal(captured.sheets[0].rows[1].cells[0].value, '0007'); assert.equal(captured.sheets[0].rows[1].cells[0].dataType, 'Text')
  assert.equal(captured.file, file)
  checks.push('Explicitly supplied synthetic bytes remain unchanged; CSV key types and leading zeroes are preserved')
}
console.log(JSON.stringify({ passed: checks.length, checks }, null, 2))
