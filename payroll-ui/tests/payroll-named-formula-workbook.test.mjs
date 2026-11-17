import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, writeFileSync } from 'node:fs'
import ts from 'typescript'

function load(name) {
  const exports = {}
  const code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, () => { throw Error('Unexpected runtime dependency') })
  return exports
}
const { buildPayrollFormulaWorkbook } = load('payrollFormulaWorkbook')
const { buildXlsxBlob } = load('xlsx')
const fixture = JSON.parse(readFileSync(new URL('./fixtures/payroll-named-formula-workbook.json', import.meta.url), 'utf8'))
const legacy = JSON.parse(readFileSync(new URL('./fixtures/payroll-formula-workbook.json', import.meta.url), 'utf8'))
const letter = index => { let result = ''; for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) result = String.fromCharCode(65 + (n - 1) % 26) + result; return result }

test('twenty-six native lines export row-local editable operand cells and real percent/attendance formulas', async () => {
  const before = JSON.stringify(fixture)
  const sheets = buildPayrollFormulaWorkbook('Synthetic named inputs', '2026-10', fixture)
  const payroll = sheets[0], headers = payroll.rows[0].map(cell => cell.value)
  const base = headers.findIndex(header => header.includes('[HARDSHIP_BASE_INPUT]'))
  const rate = headers.findIndex(header => header.includes('[HARDSHIP_RATE_PCT]'))
  const hardship = headers.indexOf('HARDSHIP input / raw')
  assert.equal(base, 15 + 26 * 3); assert.equal(rate, base + 1)
  assert.match(headers[rate], /percent points/)
  for (const [index, employee] of fixture.entries()) {
    const row = payroll.rows[index + 1], n = index + 2
    assert.equal(employee.calculation.lines.length, 26)
    assert.equal(row[base].value, employee.namedInputs.values.HARDSHIP_BASE_INPUT)
    assert.equal(row[rate].value, employee.namedInputs.values.HARDSHIP_RATE_PCT)
    assert.equal(row[base].style, 'operand'); assert.equal(row[rate].style, 'operand')
    assert.match(row[hardship].formula, new RegExp(`${letter(base)}${n}`))
    assert.match(row[hardship].formula, new RegExp(`${letter(rate)}${n}\\)%`))
    assert.match(row[hardship].formula, new RegExp(`F${n}.*H${n}`))
    assert.ok(!row[hardship].formula.includes('54000'))
    assert.match(row[8].formula, new RegExp(`COUNT\\(${letter(rate)}${n}\\)=1`))
    assert.equal(row[12].value, employee.calculation.net)
    assert.equal(row[3].value, employee.calculation.inputs.find(input => input.name === 'ANNUAL_CTC').value)
  }
  assert.equal(JSON.stringify(fixture), before)
  const bytes = new Uint8Array(await buildXlsxBlob(sheets).arrayBuffer()), xml = new TextDecoder().decode(bytes)
  assert.match(xml, /numFmtId="0" fillId="3" applyNumberFormat="1" applyFill="1"/)
  assert.match(xml, /ht="72" customHeight="1"/)
  assert.match(xml, new RegExp(`<c r="${letter(rate)}2" s="5"><v>0</v>`))
  assert.match(xml, new RegExp(`<c r="${letter(hardship)}2"[^>]*><f>`))
  assert.match(xml, /calcMode="auto" fullCalcOnLoad="1"/)
  if (process.env.FREVO_NAMED_FORMULA_XLSX) writeFileSync(process.env.FREVO_NAMED_FORMULA_XLSX, bytes)
})

test('same named template keeps independent zero/twenty/twenty-five values without adding money lines', () => {
  const sheets = buildPayrollFormulaWorkbook('Synthetic', '2026-10', fixture)
  const headers = sheets[0].rows[0].map(cell => cell.value)
  assert.equal(headers.filter(header => header.includes('[HARDSHIP_RATE_PCT]')).length, 1)
  assert.ok(!headers.includes('HARDSHIP_BASE_INPUT earned'))
  assert.ok(!headers.includes('HARDSHIP_RATE_PCT earned'))
  assert.equal(new Set(fixture.map(employee => employee.namedInputs.declarationHash)).size, 1)
  assert.deepEqual([...new Set(fixture.map(employee => employee.namedInputs.values.HARDSHIP_RATE_PCT))], [0, 20, 25])
})

test('missing, undeclared, colliding, wrong-unit or imprecise input evidence fails before export', () => {
  for (const mutation of ['missing', 'undeclared', 'alias', 'unit', 'precision', 'range', 'declaration', 'manifest']) {
    const employee = structuredClone(fixture.at(-1)), context = employee.namedInputs
    if (mutation === 'missing') delete context.values.HARDSHIP_RATE_PCT
    if (mutation === 'undeclared') context.values.UNREVIEWED = 1
    if (mutation === 'alias') { context.definitions[1].key = 'ACTUAL_BASIC'; delete context.values.HARDSHIP_RATE_PCT; context.values.ACTUAL_BASIC = 25 }
    if (mutation === 'unit') context.definitions[1].unit = 'Days'
    if (mutation === 'precision') context.values.HARDSHIP_RATE_PCT = 25.00001
    if (mutation === 'range') context.values.HARDSHIP_RATE_PCT = 101
    if (mutation === 'declaration') context.declarationHash = ''
    if (mutation === 'manifest') context.manifestHash = ''
    assert.throws(() => buildPayrollFormulaWorkbook('Synthetic', '2026-10', [employee]), /reviewed named|declared|evidence|precision/)
  }
  const missing = structuredClone(fixture.at(-1)); delete missing.namedInputs
  assert.throws(() => buildPayrollFormulaWorkbook('Synthetic', '2026-10', [missing]), /unavailable/)
})

test('operand range/precision checks are live worksheet formulas and source zero remains an explicit number', () => {
  const sheets = buildPayrollFormulaWorkbook('Synthetic', '2026-10', fixture)
  const headers = sheets[0].rows[0].map(cell => cell.value), rate = headers.findIndex(header => header.includes('[HARDSHIP_RATE_PCT]'))
  const address = `${letter(rate)}2`, row = sheets[0].rows[1]
  assert.equal(row[rate].value, 0)
  assert.ok(!row[rate].formula)
  assert.ok(row[8].formula.includes(`${address}>=0`))
  assert.ok(row[8].formula.includes(`${address}<=100`))
  assert.ok(row[8].formula.includes(`${address}=ROUND(${address},4)`))
  assert.match(row[12].formula, /IF\(I2="Ready"/)
  assert.match(row[12].formula, /NA\(\)/)
})

test('absence or null named inputs preserves the existing workbook layout and bytes', async () => {
  const nulls = structuredClone(legacy).map(employee => ({ ...employee, namedInputs: null }))
  const oldSheets = buildPayrollFormulaWorkbook('Synthetic', '2026-09', legacy)
  const newSheets = buildPayrollFormulaWorkbook('Synthetic', '2026-09', nulls)
  assert.deepEqual(newSheets, oldSheets)
  assert.deepEqual(new Uint8Array(await buildXlsxBlob(oldSheets).arrayBuffer()), new Uint8Array(await buildXlsxBlob(newSheets).arrayBuffer()))
  assert.ok(!new TextDecoder().decode(await buildXlsxBlob(newSheets).arrayBuffer()).includes('numFmtId="164"'))
})
