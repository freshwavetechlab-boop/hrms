import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const cache = new Map()
function compile(name) {
  if (cache.has(name)) return cache.get(name)
  let code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
  code = code.replace(/from ['"]\.\/([^'"]+)['"]/g, (_, dependency) => `from '${compile(dependency)}'`)
  const url = 'data:text/javascript;base64,' + Buffer.from(code).toString('base64'); cache.set(name, url); return url
}
const { buildSalaryTemplateWorkbook: build } = await import(compile('salaryTemplateWorkbook'))
const { buildXlsxBlob } = await import(compile('xlsx'))
const { evaluatePayrollFormula: evaluate } = await import(compile('payrollFormula'))
const component = (id, code, category, calculationType, value = '', formula = '', extra = {}) => ({ id, code, name: code, category, calculationType, value, formula, active: true, componentRole: 'Regular Earning', proRata: category === 'Earning', payType: 'Fixed Pay', priority: String(id), baseComponent: '', ...extra })
const components = [component(1, 'BASIC', 'Earning', 'Fixed Amount'), component(2, 'HRA', 'Earning', 'Percentage of Component', '40', '', { baseComponent: 'BASIC' }), component(3, 'PF', 'Deduction', 'Formula', '', 'MIN(BASIC_EARNED+HRA_EARNED,15000)*12%'), component(4, 'PT', 'Deduction', 'Fixed Amount', '0'), component(5, 'ER_PF', 'Benefit', 'Formula', '', 'BASIC_EARNED*13%')]
const template = { id: 17, name: 'Saved site template', clientId: '10', annualCtc: '', active: true, lines: components.map(c => ({ componentId: String(c.id), value: '' })), excelSource: { workLocationLabel: 'Site A', payrollDays: 26 } }
const context = { clientId: 10, clientName: 'Synthetic client', month: '2026-10', payrollDays: 26, locationLabel: 'Site A', rowCount: 2 }
const value = cell => typeof cell === 'object' ? cell.value : cell
const makeValues = (sheet, rowIndex, edits) => {
  const cells = new Map(sheet.rows[rowIndex].map((cell, i) => { let col = ''; for (let n = i + 1; n; n = Math.floor((n - 1) / 26)) col = String.fromCharCode(65 + (n - 1) % 26) + col; return [`${col}${rowIndex + 1}`, cell] }))
  const calculate = address => {
    if (address in edits) return edits[address]
    const cell = cells.get(address)
    if (typeof cell === 'object' && cell.formula) {
      const tokens = Object.fromEntries([...new Set(cell.formula.match(/\b[A-Z]{1,3}\d+\b/g) || [])].map(ref => [ref, calculate(ref)]))
      return evaluate(cell.formula, tokens)
    }
    const raw = value(cell)
    if (raw === '' || raw == null) throw Error(`Missing input ${address}`)
    return Number(raw)
  }
  return calculate
}

test('client/location workbook contains live row-local formulas and blank attendance/employee inputs without mutating saved rules', async () => {
  const before = JSON.stringify({ components, template })
  const sheets = build(template, components, context), salary = sheets[0]
  assert.equal(salary.rows.length, 7)
  assert.equal(value(salary.rows[5][0]), '')
  assert.equal(value(salary.rows[5][1]), '')
  assert.equal(value(salary.rows[5][2]), 26)
  assert.equal(value(salary.rows[5][3]), '')
  assert.equal(value(salary.rows[5][4]), '')
  assert.equal(value(salary.rows[5][8]), '')
  assert.equal(value(salary.rows[5][14]), 0, 'an explicit saved zero deduction stays zero')
  const first = makeValues(salary, 5, { C6: 26, D6: 11, E6: 13, I6: 10000 })
  assert.equal(first('J6'), 5000)
  assert.equal(first('L6'), 2000)
  assert.equal(first('N6'), 840)
  assert.equal(first('R6'), 650)
  const second = makeValues(salary, 6, { C7: 26, D7: 26, E7: 26, I7: 20000 })
  assert.equal(second('J7'), 20000)
  assert.equal(second('L7'), 8000)
  assert.equal(second('N7'), 1800)
  assert.ok(salary.rows[5].filter(c => typeof c === 'object' && c.formula).every(c => c.missingCachedValue === true), 'no sample salary is misrepresented as a calculated payroll snapshot')
  assert.equal(JSON.stringify({ components, template }), before)
  const bytes = new Uint8Array(await buildXlsxBlob(sheets).arrayBuffer()), xml = new TextDecoder().decode(bytes)
  assert.match(xml, /<f>/); assert.match(xml, /state="hidden"/)
  assert.match(xml, /calcMode="auto" fullCalcOnLoad="1"/)
})

test('scope, month, missing components, duplicate lines and unsupported native inputs fail before exporting', () => {
  assert.throws(() => build(template, components, { ...context, clientId: 20 }), /selected client/)
  assert.throws(() => build(template, components, { ...context, locationLabel: 'Site B' }), /another location/)
  assert.throws(() => build(template, components, { ...context, month: '2026-13' }), /valid month/)
  assert.throws(() => build(template, components.slice(1), context), /missing components/)
  assert.throws(() => build({ ...template, lines: [...template.lines, template.lines[0]] }, components, context), /duplicate/)
  const unknown = components.map(c => c.id === 3 ? { ...c, formula: 'UNDECLARED_TARGET*12%' } : c)
  assert.throws(() => build(template, unknown, context), /PF.*UNDECLARED_TARGET/)
})

test('download keeps location-specific rules independent and reuses shared/client templates without reassigning them', () => {
  const siteB = { ...template, id: 18, name: 'Site B template', lines: template.lines.map(line => line.componentId === '2' ? { ...line, formula: 'BASIC*30%', calculationType: 'Formula' } : line), excelSource: { workLocationLabel: 'Site B', payrollDays: 30 } }
  const salary = build(siteB, components, { ...context, locationLabel: 'Site B', payrollDays: 30 })[0]
  const calculate = makeValues(salary, 5, { C6: 30, D6: 10, E6: 10, I6: 30000 })
  assert.equal(calculate('L6'), 3000)
  assert.equal(template.lines[1].formula, undefined)
  assert.doesNotThrow(() => build({ ...template, clientId: '', excelSource: undefined }, components, { ...context, locationLabel: '' }))
})

test('daily-rate dependencies use monthly wages while earned wages use independent payable days', () => {
  const daily = [component(11, 'WAGE', 'Earning', 'Daily Rate', '700'), component(12, 'ALLOW', 'Earning', 'Formula', '', 'WAGE_MONTHLY*10%', { proRata: false })]
  const salary = build({ ...template, lines: daily.map(c => ({ componentId: String(c.id), value: '' })) }, daily, context)[0]
  const calculate = makeValues(salary, 5, { C6: 26, D6: 11, E6: 13 })
  assert.equal(calculate('J6'), 9100)
  assert.equal(calculate('L6'), 1820)
})
