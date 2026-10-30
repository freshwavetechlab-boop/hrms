import assert from 'node:assert/strict'
import fs from 'node:fs'
import { stripTypeScriptTypes } from 'node:module'

// Pure metadata tests: no browser session, HTTP, source workbook or database access.
const source = fs.readFileSync(new URL('../payroll-ui/src/utils/excelPayslipMapping.ts', import.meta.url), 'utf8')
const { buildPayslipMappingEvidence } = await import('data:text/javascript;base64,' + Buffer.from(stripTypeScriptTypes(source)).toString('base64'))
const headers = ['Employee name', 'Employee code', 'Monthly Basic', 'Payable days', 'Earned Basic', 'PF', 'Net Pay', 'Fixed allowance']
const kinds = ['employeeName', 'employeeCode', 'info', 'info', 'earning', 'deduction', 'netPay', 'earning']
const columns = headers.map((sourceHeader, columnIndex) => ({ columnIndex, sourceHeader, label: sourceHeader, kind: kinds[columnIndex] }))
const cell = (value, formulaText) => ({ value, ...(formulaText ? { formula: true, formulaText } : {}) })
const sheet = { name: 'Salary Private Person', columnCount: 8, rows: [
  { sourceRow: 1, cells: headers.map(v => cell(v)) },
  { sourceRow: 2, cells: [cell('Private Person'), cell('SECRET-00017'), cell('31001'), cell('26', '=IFERROR(VLOOKUP(B2,\'Attendance Sept\'!$A:$D,4,FALSE),0)'), cell('26867.53', '=C2/30*D2'), cell('3224.10', '=E2*12%'), cell('23643.43', '=SUM(E2,H2)-F2'), cell('1503')] }
] }
const attendance = { name: 'Attendance Sept', columnCount: 4, rows: [{ sourceRow: 1, cells: ['Employee code', 'Name', 'LWP', 'Payable days'].map(v => cell(v)) }, { sourceRow: 2, cells: ['SECRET-00017', 'Private Person', '4', '26'].map(v => cell(v)) }] }
const before = JSON.stringify([sheet, attendance, columns])
const result = buildPayslipMappingEvidence(sheet, 1, columns, '', [], [sheet, attendance])
assert.deepEqual(result.columns[3].formulaFunctions, ['IFERROR', 'VLOOKUP'])
assert.deepEqual(result.columns[3].formulaDependencies, [1])
assert.deepEqual(result.columns[3].crossSheetDependencies, [{ sheetIndex: 1, sourceType: 'attendance', status: 'supplied', columnIndexes: [0, 1, 2, 3] }])
assert.equal(result.columns[3].kind, 'info')
assert.deepEqual(result.columns[4].formulaDependencies, [2, 3])
assert.deepEqual(result.columns[6].formulaDependencies, [4, 5, 7])
assert.equal(result.columns[7].hasFormula, false)
assert.equal(result.columns[7].kind, 'earning')
assert.equal(JSON.stringify([sheet, attendance, columns]), before)
const raw = JSON.stringify(result)
for (const secret of ['Private Person', 'SECRET-00017', '31001', '26867.53', '23643.43', 'Attendance Sept', 'Salary Private Person', 'formulaText', 'blankAsZero']) assert.ok(!raw.includes(secret), secret + ' leaked')
const checks = ['Linked Attendance VLOOKUP retains anonymous source columns; source days stay Information', 'Monthly inputs, earned amounts, controls and literal earnings retain distinct roles', 'No employee data, amounts, sheet names, raw formulas or blank-as-zero approval transmitted; input unchanged']

function evidence(formula, supplied = [sheet, attendance]) {
  const copy = structuredClone(sheet); copy.rows[1].cells[3] = cell('26', formula)
  return buildPayslipMappingEvidence(copy, 1, columns, '', [], supplied).columns[3]
}
assert.equal(evidence('=Missing!D2').crossSheetDependencies[0].status, 'missing')
assert.deepEqual(evidence('=\'[Private.xlsx]Attendance Sept\'!D2').crossSheetDependencies, [{ sheetIndex: -1, sourceType: 'unknown', status: 'external-review', columnIndexes: [3] }])
assert.equal(evidence('=\'Attendance Sept\'!D2', [sheet, attendance, structuredClone(attendance)]).crossSheetDependencies[0].status, 'missing')
assert.deepEqual(evidence('=IF(B2="Attendance!D2",\'Attendance Sept\'!D2,0)').crossSheetDependencies.map(d => d.columnIndexes), [[3]])
assert.deepEqual(evidence('=\'Salary Private Person\'!D2').formulaDependencies, [3])
assert.deepEqual(evidence('=\'Attendance Sept\'!$A$2:$D$50').crossSheetDependencies[0].columnIndexes, [0, 1, 2, 3])
assert.deepEqual(evidence('=INDEX(\'Attendance Sept\'!D:D,MATCH(B2,\'Attendance Sept\'!A:A,0))').formulaFunctions, ['INDEX', 'MATCH'])
assert.equal(evidence("='Attendance Sept'!A:XFD").dependencyEvidenceIncomplete, true)
assert.equal(evidence("='Attendance Sept'!A:XFD").crossSheetDependencies[0].columnIndexes.length, 128)
assert.equal(evidence('=' + Array.from({ length: 33 }, (_, i) => `'Missing${i}'!D2`).join('+')).dependencyEvidenceIncomplete, true)
checks.push('Missing, ambiguous and external sources remain unresolved; literals are not mistaken for references', 'Quoted/ranged/whole-column references and INDEX/MATCH are supported; bounded evidence reports truncation')
const excluded = buildPayslipMappingEvidence(sheet, 1, columns, '', [2], [sheet, attendance])
assert.deepEqual(excluded.columns[3].crossSheetDependencies, [])
assert.equal(excluded.columns[3].hasFormula, false)
checks.push('Excluded source rows cannot introduce mapping dependencies')
const late = structuredClone(sheet)
late.rows.push({ sourceRow: 501, cells: [cell('Private Person 501'), cell('SECRET-LATE'), cell('31001'), cell('26', '=XLOOKUP(B501,\'Attendance Sept\'!A:A,\'Attendance Sept\'!D:D)'), cell('1000'), cell('120'), cell('880'), cell('0')] })
assert.ok(buildPayslipMappingEvidence(late, 1, columns, '', [], [late, attendance]).columns[3].formulaFunctions.includes('XLOOKUP'))
assert.equal(evidence('=UNSUPPORTED_FUNCTION(B2)').dependencyEvidenceIncomplete, true)
assert.deepEqual(evidence('=_xlfn.XLOOKUP(B2,\'Attendance Sept\'!A:A,\'Attendance Sept\'!D:D)').formulaFunctions, ['XLOOKUP'])
assert.deepEqual(evidence("='Salary (ROUND private)'!A2").formulaFunctions, [])
checks.push('Formula variants after employee 500 are inspected; unsupported functions are flagged rather than certified')
console.log(JSON.stringify({ checks, count: checks.length, mutations: 0 }, null, 2))
