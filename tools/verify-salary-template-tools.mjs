import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

// Synthetic, database-free round trip through the actual UI workbook utilities,
// existing Payslip capture/mapping and the native template extraction service.
const root = fileURLToPath(new URL('..', import.meta.url)), require = createRequire(root + '/payroll-ui/package.json'), ts = require('typescript'), cache = new Map()
function compile(name) {
  if (cache.has(name)) return cache.get(name)
  let code = ts.transpileModule(readFileSync(root + '/payroll-ui/src/utils/' + name + '.ts', 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
  code = code.replace(/from ['"]\.\/([^'"]+)['"]/g, (_, dependency) => 'from ' + JSON.stringify(compile(dependency)))
  const url = 'data:text/javascript;base64,' + Buffer.from(code).toString('base64'); cache.set(name, url); return url
}
const { buildSalaryTemplateWorkbook } = await import(compile('salaryTemplateWorkbook'))
const { restorePayslipTemplateMetadata, excelColumnLetter } = await import(compile('excelPayslipImport'))
const { analyzeSalaryTemplateSheet, buildSalaryTemplateExcelSource } = await import(compile('salaryTemplateExcel'))
const { collectWorkbookDependencies } = await import(compile('workbookDependencyCapture'))
const { evaluatePayrollFormula } = await import(compile('payrollFormula'))
const component = (id, code, category, calculationType, value = '', formula = '', extra = {}) => ({ id, code, name: code, category, calculationType, value, formula, active: true, componentRole: category === 'Deduction' ? 'Other Deduction' : category === 'Benefit' ? 'Employer Contribution' : 'Regular Earning', proRata: category === 'Earning', payType: 'Fixed Pay', priority: String(id), baseComponent: '', ...extra })
const components = [component(1, 'BASIC', 'Earning', 'Fixed Amount'), component(2, 'HRA', 'Earning', 'Percentage of Component', '40', '', { baseComponent: 'BASIC' }), component(3, 'PF', 'Deduction', 'Formula', '', 'MIN(BASIC_EARNED+HRA_EARNED,15000)*12%'), component(4, 'PT', 'Deduction', 'Fixed Amount', '0'), component(5, 'ER_PF', 'Benefit', 'Formula', '', 'BASIC_EARNED*13%')]
const template = { id: 17, name: 'Synthetic saved location rules', clientId: '42', annualCtc: '', active: true, lines: components.map(c => ({ componentId: String(c.id), value: '' })), excelSource: { workLocationLabel: 'Site A', payrollDays: 26 } }
const context = { clientId: 42, clientName: 'Synthetic client', month: '2026-10', payrollDays: 26, locationLabel: 'Site A', rowCount: 1 }
const original = JSON.stringify({ components, template })
const build = spawnSync('dotnet', ['build', 'tools/fixtures/SalaryTemplateTools.Native/Native.csproj', '-p:NuGetAudit=false', '--verbosity', 'quiet'], { cwd: root, encoding: 'utf8' })
assert.equal(build.status, 0, build.stdout + build.stderr)
const native = payload => {
  const process = spawnSync('dotnet', [root + '/tools/fixtures/SalaryTemplateTools.Native/bin/Debug/net8.0/Native.dll'], { input: JSON.stringify(payload), encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 })
  assert.equal(process.status, 0, process.stdout + process.stderr); return JSON.parse(process.stdout)
}
function captureWorkbook(edits, addedColumn = false) {
  const workbook = buildSalaryTemplateWorkbook(template, components, context)
  if (addedColumn) {
    workbook[0].rows[4].push({ value: 'Transport deduction' }); workbook[0].rows[5].push({ value: '', formula: 'J6*2%', missingCachedValue: true, type: 'number' })
    workbook[0].rows[5][19].formula = workbook[0].rows[5][19].formula.replace(/,0\/0\)$/, '+W6,0/0)')
  }
  // Deterministic test recalculation. No Excel process or saved user file is edited.
  // COUNT and the text status are exported validation controls, not salary rules.
  const row = workbook[0].rows[5], raw = cell => typeof cell === 'object' ? cell.value : cell
  const cells = new Map(row.map((cell, i) => [excelColumnLetter(i) + '6', cell]))
  const calculate = address => {
    if (address in edits) return edits[address]
    const cell = cells.get(address)
    if (typeof cell === 'object' && cell.formula) {
      if (address === 'H6') return 'Ready'
      const expression = cell.formula.replace(/COUNT\(([^)]+)\)/g, (_, refs) => String(refs.split(',').filter(ref => calculate(ref) !== '').length)).replace(/([A-Z]+6)=""/g, (_, ref) => calculate(ref) === '' ? '1' : '0')
      const tokens = Object.fromEntries([...new Set(expression.match(/\b[A-Z]{1,3}\d+\b/g) || [])].map(ref => [ref, calculate(ref)]))
      return evaluatePayrollFormula(expression, tokens)
    }
    return raw(cell) === '' ? '' : Number(raw(cell))
  }
  return restorePayslipTemplateMetadata(workbook.map(sheet => ({ name: sheet.name, columnCount: Math.max(...sheet.rows.map(row => row.length)), rows: sheet.rows.map((row, r) => ({ sourceRow: r + 1, cells: row.map((cell, c) => {
    const value = r === 5 && sheet.name === 'Salary' && c > 1 ? calculate(excelColumnLetter(c) + '6') : r === 5 && sheet.name === 'Salary' && c < 2 ? c ? 'Synthetic employee' : 'EX-001' : raw(cell)
    return { value: String(value ?? ''), dataType: typeof value === 'number' ? 'Number' : 'Text', ...(typeof cell === 'object' && cell.formula ? { formula: true, formulaText: cell.formula } : {}) }
  }) })) })))
}
const results = []
for (const [label, payable, basic, added] of [['partial', 13, 10000, false], ['full', 26, 20000, false], ['added-column', 13, 10000, true]]) {
  const sheets = captureWorkbook({ D6: payable, E6: payable, I6: basic }, added), sheet = sheets[0]
  assert.equal(sheet.template.clientId, 42); assert.equal(sheet.template.headerRow, 5)
  const analysis = analyzeSalaryTemplateSheet(sheet, 5, 6, components, sheets, sheet.template.columns)
  // A newly added heading is reviewed in the same column-role selector; the old
  // metadata never silently approves a new payroll component.
  if (added) Object.assign(analysis.columns.at(-1), { suggestedRole: 'deduction', suggestedCode: 'TRANSPORT_DEDUCTION', label: 'Transport deduction', matchedComponentId: undefined })
  const selectedColumns = analysis.columns.filter(c => c.suggestedRole !== 'ignore').map(c => ({ columnIndex: c.columnIndex, code: c.suggestedCode, name: c.label, kind: c.suggestedRole, existingComponentId: c.matchedComponentId, inputMode: 'monthly', reviewedFixedValue: c.header === 'Monthly PT' }))
  const source = buildSalaryTemplateExcelSource(sheet, 5)
  const sha256 = 'b'.repeat(64), book = { key: 'wb_' + sha256, sha256, fileName: 'synthetic-roundtrip.xlsx', sheets }
  const dependency = collectWorkbookDependencies(sheet, book.key, [book], [], sheet.template.columns, 5)
  assert.deepEqual(dependency.issues, [])
  Object.assign(source, dependency.fields, { dependencyReviewStatus: 'Reviewed' })
  const result = native({ operation: 'extract', request: { clientId: 42, templateName: 'Round trip ' + label, month: context.month, source, sampleRow: 6, selectedColumns }, components })
  assert.equal(result.canCreateDraft, true, JSON.stringify({ label, selectedColumns, issues: result.issues }))
  assert.ok(result.comparisons.every(c => c.status === 'Matched'), JSON.stringify(result.comparisons))
  assert.equal(result.components.filter(c => c.category === 'Earning').length, 2, 'totals do not become duplicate earnings')
  assert.equal(result.simulation.benefits, basic * payable / 26 * 0.13)
  if (added) assert.ok(result.components.some(c => c.name === 'Transport deduction'))
  const scenario = structuredClone(result.scenario); scenario.payableDays = 13; scenario.presentDays = 13
  const changed = native({ operation: 'simulate', request: scenario })
  assert.equal(changed.complete, true, JSON.stringify(changed.issues))
  results.push({ case: label, components: result.components.length, net: result.simulation.net, changedDaysNet: changed.net, comparisonsMatched: result.comparisons.length })
}
assert.equal(JSON.stringify({ components, template }), original)
console.log(JSON.stringify({ passed: results, originalRulesUnchanged: true, databaseWrites: 0, emailsSent: 0 }, null, 2))
