import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/excelPayslipImport.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const { parsePayslipCsv, defaultPayslipMapping, buildExcelPayslipRows, payslipHeaders, payslipHeaderSignature, parsePayslipAmount, inferPayslipMonth, formatExcelPayslipAmount } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
const parse = text => { const sheet = parsePayslipCsv(text); const columns = defaultPayslipMapping(payslipHeaders(sheet, 1)); return { sheet, columns, result: buildExcelPayslipRows(sheet, 1, columns) } }

test('CSV preserves text identifiers, duplicate employee names, signed/zero amounts and quoted multiline information', () => {
  const { result } = parse('Employee Name,Employee Code,Basic,Net Pay,Email,Address\r\nSame name,000017,0,0,,"Line one\nLine two"\r\nSame name,000018,-12.5,-12.5,,"Street, town"')
  assert.equal(result.rows.length, 2)
  assert.equal(result.rows[0].employeeCode, '000017')
  assert.equal(result.rows[1].employeeCode, '000018')
  assert.equal(result.rows[0].sourceRow, 2)
  assert.equal(result.rows[1].sourceRow, 4, 'physical CSV record start line is retained')
  assert.equal(result.rows[0].earnings[0].amount, 0)
  assert.equal(result.rows[1].netPay, -12.5)
  assert.deepEqual(result.issues, [])
  assert.notEqual(result.rows[0].id, result.rows[1].id)
})

test('blank amount is an issue, explicit zero is valid, and spreadsheet errors are not evaluated', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay\nA,,0\nB,0,0\nC,20,19')
  sheet.rows[3].cells[2] = { value: '#REF!', formula: true, error: '#REF!' }
  const result = buildExcelPayslipRows(sheet, 1, columns)
  assert.deepEqual(result.issues.map(issue => [issue.sourceRow, issue.column]), [[2, 'B'], [4, 'C']])
  assert.equal(result.rows[1].netPay, 0)
  sheet.rows[3].cells[2] = { value: '', formula: true, missingCachedValue: true }
  assert.match(buildExcelPayslipRows(sheet, 1, columns).issues.at(-1).message, /no saved result/)
})

test('net pay remains the source amount and email is optional for valid import', () => {
  const { result } = parse('Employee Name,Basic,Net Pay\nA,100,74.321')
  assert.equal(result.rows[0].netPay, 74.321)
  assert.equal(result.rows[0].email, '')
  assert.deepEqual(result.issues, [])
})

test('blank-name, total and explicitly excluded records remain visible in exclusion review', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay\nA,100,90\n,100,90\nGrand Total,200,180\nB,5,5')
  const result = buildExcelPayslipRows(sheet, 1, columns, [5])
  assert.equal(result.rows.length, 1)
  assert.deepEqual(result.excludedRows, [3, 4, 5])
  assert.match(result.excludedDetails[0].amounts, /100/)
  assert.match(result.excludedDetails[0].reason, /No employee name/)
  assert.equal(result.excludedDetails[2].reason, 'Manually excluded')
})

test('PLRS preset retains physical blank and duplicate headings; S.no and invoice are not payroll identity/amounts', () => {
  const headers = ['S.no.', 'District', 'Location Name (Tehsil/Sub Tehsil)', 'Name of the person', "Father's/Husbands' name", 'District', 'Qualification', 'Designation_Manpower Category', 'Rate', 'Attendance', 'Wages', 'EPF@12%', 'ESIC@0.75%', 'Net Pay', 'Bonus@8.33%', 'EPF@13%', 'ESIC@3.25%', 'CTC', 'SC@1.48%', 'IGST@18%', 'Invoice', 'Bank Account Number', 'IFSC Code', 'Mobile', '', 'UAN', 'ESIC No', 'Adhaar Number', 'PAN No', 'Address']
  const mapping = defaultPayslipMapping(headers)
  assert.equal(mapping.length, 30)
  assert.equal(mapping[0].kind, 'ignore')
  assert.equal(mapping[24].kind, 'ignore')
  assert.equal(mapping[10].kind, 'earning')
  assert.equal(mapping[11].kind, 'deduction')
  assert.equal(mapping[13].kind, 'netPay')
  assert.equal(mapping[14].kind, 'info', 'bonus is excluded from workbook net pay')
  assert.equal(mapping[20].kind, 'ignore')
  assert.notEqual(mapping[1].label, mapping[5].label)
  assert.equal(mapping[28].kind, 'ignore')
})

test('only mapped errors block import; serial and external lookup errors can remain ignored', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay,S.no.,\nA,100,90,#REF!,#N/A')
  sheet.rows[1].cells[3] = { value: '#REF!', error: '#REF!', formula: true }
  sheet.rows[1].cells[4] = { value: '#N/A', error: '#N/A', formula: true }
  assert.deepEqual(buildExcelPayslipRows(sheet, 1, columns).issues, [])
  columns[4].kind = 'info'
  assert.equal(buildExcelPayslipRows(sheet, 1, columns).issues[0].column, 'E')
})

test('source month is derived from labeled cached Excel date, never current month', () => {
  const serial = (Date.UTC(2026, 8, 1) / 86400000) + 25569
  const sheet = parsePayslipCsv(`Department,,,PLRS\nCompany,,,Company\nMonth,,,${serial}\n\nEmployee Name,Basic,Net Pay`)
  assert.equal(inferPayslipMonth(sheet), '2026-09')
  assert.equal(inferPayslipMonth(parsePayslipCsv('Employee Name,Basic,Net Pay\nA,100,90')), '')
  assert.equal(inferPayslipMonth({ ...sheet, dateSystem: '1904' }), '', '1904 date-system workbooks require explicit month selection')
  assert.equal(inferPayslipMonth(parsePayslipCsv('Month,Salary,45000')), '', 'unrelated later numeric cells are not treated as the month')
})

test('header signatures include column positions, duplicates and blank headings', async () => {
  const first = await payslipHeaderSignature(['Name', '', 'District', 'District'])
  assert.match(first, /^[0-9a-f]{64}$/)
  assert.notEqual(first, await payslipHeaderSignature(['Name', 'District', '', 'District']))
  assert.equal(first, await payslipHeaderSignature([' Name ', '', 'District', 'District']))
})

test('invalid numeric and malformed CSV data do not silently become money', () => {
  for (const value of ['', ' ', '#VALUE!', 'NaN', 'Infinity', '=1+1', '1,23,abc', '1 000']) assert.equal(parsePayslipAmount(value), null)
  assert.equal(parsePayslipAmount('1,23,456.125'), 123456.125)
  assert.equal(parsePayslipAmount('1e3'), 1000)
  assert.throws(() => parsePayslipCsv('Name,Basic\n"unclosed,100'), /unclosed/)
})

test('required singleton mappings prevent ambiguous name/net selection', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay,Name\nA,100,90,A')
  assert.match(buildExcelPayslipRows(sheet, 1, columns).issues[0].message, /only once/)
  columns[3].kind = 'ignore'; columns[2].kind = 'ignore'
  assert.match(buildExcelPayslipRows(sheet, 1, columns).issues[0].message, /explicit net pay/)
})

test('oversized display labels require an explicit shorter mapping', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay\nA,100,90')
  columns[1].label = 'x'.repeat(81)
  assert.match(buildExcelPayslipRows(sheet, 1, columns).issues[0].message, /80 characters/)
})

test('display matches Excel15-digit precision without changing cached or imported amounts', () => {
  const cached = 14129.499999999998
  assert.equal(formatExcelPayslipAmount(cached), '14,130')
  assert.equal(formatExcelPayslipAmount(cached, 2), '14,129.50')
  assert.equal(formatExcelPayslipAmount(-cached), '-14,130')
  const { result } = parse(`Employee Name,Basic,Net Pay\nA,${cached},${cached}`)
  assert.equal(result.rows[0].netPay, cached)
  assert.equal(result.rows[0].earnings[0].amount, cached)
})
