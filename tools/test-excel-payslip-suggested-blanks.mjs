import assert from 'node:assert/strict'
import fs from 'node:fs'
import { stripTypeScriptTypes } from 'node:module'
const source = fs.readFileSync(new URL('../payroll-ui/src/utils/excelPayslipImport.ts', import.meta.url), 'utf8')
const { applySuggestedPayslipBlankAmounts: suggest, buildExcelPayslipRows: build, defaultPayslipMapping: defaults, resolvePayslipSharedFormulas } = await import('data:text/javascript;base64,' + Buffer.from(stripTypeScriptTypes(source)).toString('base64'))
const labels = ['Employee name', 'Employee code', 'Basic', 'Expense allowance', 'Bonus', 'TA/DA', 'Recovery', 'PF Admin', 'EDLI', 'Net Pay', 'Payable days']
const kinds = ['employeeName', 'employeeCode', 'earning', 'earning', 'earning', 'earning', 'deduction', 'employer', 'employer', 'netPay', 'info']
const columns = labels.map((sourceHeader, columnIndex) => ({ columnIndex, sourceHeader, label: sourceHeader, kind: kinds[columnIndex] }))
const c = (value, formulaText) => ({ value, ...(formulaText ? { formula: true, formulaText } : {}) })
const sheet = { name: 'Synthetic salary', columnCount: labels.length, rows: [{ sourceRow: 1, cells: labels.map(v => c(v)) }, ...Array.from({ length: 109 }, (_, i) => ({ sourceRow: i + 2, cells: [
  c('Synthetic ' + i), c('SYN-' + i), c('1000'), i < 43 ? c('') : c('50', '=I2/L2*M2'), i < 3 ? c('') : c('0', '=IF(F2<=21000,ROUNDUP(O2*8.33%,0),0)'),
  c(i < 107 ? '' : '25'), c(''), i < 3 ? c('') : c('5', '=O2*0.5%'), i < 3 ? c('') : c('5', '=IF(O2>25000,25000*0.5%,O2*0.5%)'), c('1000'), c('')
] }))] }
const before = JSON.stringify({ sheet, columns })
assert.equal(build(sheet, 1, columns).issues.length, 268)
const suggested = suggest(sheet, 1, columns)
assert.equal(suggested.blankCellCount, 268)
assert.deepEqual(suggested.zeroedColumns.map(item => item.cellCount), [43, 3, 107, 109, 3, 3])
const after = build(sheet, 1, suggested.columns)
assert.equal(after.issues.length, 0); assert.equal(after.rows.length, 109)
assert.equal(after.rows[0].earnings.find(e => e.label === 'Bonus').amount, 0)
assert.equal(after.rows[3].earnings.find(e => e.label === 'Bonus').amount, 0)
assert.equal(after.rows[3].employerContributions.find(e => e.label === 'EDLI').amount, 5)
assert.equal(suggested.columns[9].blankAsZero, undefined); assert.equal(suggested.columns[10].blankAsZero, undefined)
assert.equal(JSON.stringify({ sheet, columns }), before)
const checks = ['109-row mixed formula/literal fixture: 268 blank-cell issues become zero; valid zero/formula results preserved', 'Only component blanks are approved; net, days and original source remain unchanged']
const mixed = structuredClone(sheet)
mixed.rows[4].cells[3] = { value: '', formula: true, formulaText: '=Other!A4', missingCachedValue: true }
mixed.rows[5].cells[3] = { value: '', formula: true, calculationError: 'Shared formula is missing' }
mixed.rows[6].cells[3] = { value: '', error: '#N/A' }
mixed.rows[7].cells[3] = { value: 'bad-value' }
mixed.rows[8].cells[9] = { value: '' }
const failures = build(mixed, 1, suggest(mixed, 1, columns).columns).issues
assert.equal(failures.length, 5)
assert.ok(failures.some(i => /no saved result/.test(i.message)))
assert.ok(failures.some(i => /Saved Excel error/.test(i.message)))
assert.ok(failures.some(i => i.column === 'J'))
checks.push('Missing formula caches, unresolved shared formulas, Excel errors, invalid text and blank net remain blockers')
const onlyExcluded = { ...sheet, rows: [sheet.rows[0], { sourceRow: 2, cells: [c('Total'), c(''), c('')] }, { sourceRow: 3, cells: [c('Pending'), c(''), c('')] }] }
assert.equal(suggest(onlyExcluded, 1, columns, [3]).blankCellCount, 0)
checks.push('Totals and manually excluded rows do not trigger zero policies')
const duplicateEsicHeaders = ['Employee name', 'Basic', 'Employee ESIC@0.75%', 'Net Pay', 'UAN', 'ESIC', 'Aadhaar']
const idMapping = defaults(duplicateEsicHeaders)
assert.equal(idMapping[2].kind, 'deduction'); assert.equal(idMapping[5].kind, 'info')
checks.push('Separate monetary ESIC and identity-block ESIC retain distinct roles')
if (process.argv[2]) {
  const actual = JSON.parse(fs.readFileSync(process.argv[2], 'utf8').replace(/^\uFEFF/, ''))
  const byRow = new Map(actual.sheet.rows.map(row => [row.sourceRow, row]))
  resolvePayslipSharedFormulas(actual.formulas.map(formula => ({ ...formula, cell: byRow.get(formula.sourceRow).cells[formula.columnIndex] })))
  const mapping = defaults(actual.headers, actual.sheet, 2)
  const original = JSON.stringify(actual.sheet)
  const initial = build(actual.sheet, 2, mapping)
  const result = suggest(actual.sheet, 2, mapping, initial.excludedRows)
  const final = build(actual.sheet, 2, result.columns)
  assert.equal(initial.rows.length, 109); assert.equal(initial.issues.length, 268, JSON.stringify(Object.fromEntries([...new Set(initial.issues.map(i => i.column))].map(column => [column, initial.issues.filter(i => i.column === column).length]))))
  assert.equal(result.blankCellCount, 268); assert.equal(final.issues.length, 0)
  assert.equal(final.rows.length, 109); assert.equal(JSON.stringify(actual.sheet), original)
  checks.push('Read-only anonymized actual NHDC source: 109 rows / 268 issues -> 109 rows / 0 issues; source cells unchanged')
}
console.log(JSON.stringify({ checks, groups: checks.length, businessWrites: 0 }, null, 2))
