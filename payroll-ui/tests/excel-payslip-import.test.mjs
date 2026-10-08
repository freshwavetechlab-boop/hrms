import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/excelPayslipImport.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const { parsePayslipCsv, defaultPayslipMapping, buildExcelPayslipRows, buildPayslipCalculationSource, translatePayslipSharedFormula, resolvePayslipSharedFormulas, payslipHeaders, payslipHeaderSignature, parsePayslipAmount, inferPayslipMonth, formatExcelPayslipAmount, payslipPhoneValue } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
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
  assert.equal(mapping[23].kind, 'info', 'the original PLRS Mobile column is retained')
  assert.notEqual(mapping[1].label, mapping[5].label)
  assert.equal(mapping[28].kind, 'ignore')
  const expanded = defaultPayslipMapping([...headers, 'Basic', 'HRA', 'Email', 'Employee Code'])
  assert.equal(expanded[10].kind, 'grossTotal', 'added salary breakup must not double-count wages')
  assert.equal(expanded[30].kind, 'earning')
  assert.equal(expanded[31].kind, 'earning')
  assert.equal(expanded[32].kind, 'email')
  assert.equal(expanded[33].kind, 'employeeCode')
})

test('natural-language identity and take-home headings map by meaning rather than column position', () => {
  const aliases = {
    employeeName: ['Name of the person', 'Name of Employee', 'Employee Full Name', 'Staff Name', 'Full Name'],
    employeeCode: ['Emp Code', 'Employee ID', 'Staff Number', 'Worker No.', 'Personnel Code'],
    email: ['Email ID', 'Official Email', 'Employee Email Address', 'Work Email'],
    netPay: ['Take Home Salary', 'In Hand Salary', 'Net Amount', 'Net Payable', 'Amount Payable'],
  }
  for (const [expected, labels] of Object.entries(aliases)) for (const label of labels)
    assert.equal(defaultPayslipMapping([label])[0].kind, expected, label)
})

test('phone aliases are information and keep country prefixes, leading zeros and blank values', () => {
  for (const label of ['Phone', 'Phone No.', 'Phone Number', 'Mobile', 'Mobile No.', 'Mobile Number', 'Contact No.', 'Contact Number', 'Employee Mobile Number', 'Personal Phone', 'Telephone Number']) {
    const { result, columns } = parse(`Employee Name,Basic,Net Pay,${label}\nA,100,100,+919876543210\nB,100,100,09876543210\nC,100,100,`)
    assert.deepEqual(result.issues, [], label)
    assert.equal(columns[3].kind, 'info', label)
    assert.deepEqual(result.rows.map(payslipPhoneValue), ['+919876543210', '09876543210', ''], label)
    assert.deepEqual(result.rows.map(row => row.information[0].value), ['+919876543210', '09876543210', ''], label)
  }
  assert.equal(defaultPayslipMapping(['Emergency Contact Number'])[0].kind, 'ignore', 'an emergency contact is not the employee phone')
})

test('phone table value uses the first nonempty matching information field, including duplicate column labels', () => {
  const { result } = parse('Employee Name,Basic,Net Pay,Mobile,Mobile,Contact Number\nA,100,100,,+91 98765 43210,09876543210\nB,100,100,,,')
  assert.deepEqual(result.issues, [])
  assert.deepEqual(result.rows[0].information.map(item => item.label), ['Mobile (D)', 'Mobile (E)', 'Contact Number'])
  assert.deepEqual(result.rows.map(payslipPhoneValue), ['+91 98765 43210', ''])
  assert.equal(payslipPhoneValue({ information: [{ label: 'Bank Account Number', value: '1234567890' }] }), '')
})

test('common earning and deduction labels are recognized without assigning unknown fields', () => {
  const aliases = {
    earning: ['Basic Salary', 'House Rent Allowance', 'Conveyance Allowance', 'Special Allowance', 'Dearness Allowance', 'Incentive', 'Overtime Pay', 'Salary Arrears', 'Leave Encashment'],
    deduction: ['Salary Advance', 'Loan Recovery', 'Other Deduction', 'Recovery (If Any)', 'Medical Deduction', 'Professional Tax', 'Income Tax', 'LOP Amount'],
    ignore: ['Unexpected Field', 'District', 'Qualification', 'Custom Adjustment', 'S.no.', 'Invoice Amount', 'IGST@18%', 'CTC'],
  }
  for (const [expected, labels] of Object.entries(aliases)) for (const label of labels)
    assert.equal(defaultPayslipMapping([label])[0].kind, expected, label)
})

test('statutory employee deductions and employer contributions stay separate from IDs and percentage inputs', () => {
  const aliases = {
    deduction: ['EPF@12%', 'PF @ 12%', 'ESIC@0.75%', 'ESI @ 0.75%', 'Employee PF', 'Employee ESIC', 'PF Amount'],
    employer: ['EPF@13%', 'PF @ 13%', 'ESIC@3.25%', 'ESI @ 3.25%', 'Employer PF', 'PF Employer', 'Employer ESIC'],
    info: ['PF No.', 'EPF Account Number', 'ESIC No', 'UAN', 'UAN (Reference)', 'PF Rate', 'ESIC Percentage', 'PF %', 'ESIC %', 'EPF @ 10%'],
  }
  for (const [expected, labels] of Object.entries(aliases)) for (const label of labels)
    assert.equal(defaultPayslipMapping([label])[0].kind, expected, label)
})

test('payslip details and attendance rates are information, never deductions', () => {
  const headings = ['Department', 'Designation', 'Designation_Manpower Category', 'Work Location', 'Location Name (Tehsil/Sub Tehsil)', 'Bank Account Number', 'IFSC Code', 'Attendance', 'Paid Days', 'Working Days', 'Month Days', 'Rate', 'Daily Rate', 'Rate Per Day', 'OT', 'Overtime', 'LOP', 'Loss of Pay']
  assert.deepEqual(defaultPayslipMapping(headings).map(column => column.kind), headings.map(() => 'info'))
  assert.deepEqual(defaultPayslipMapping(['OT Pay', 'OT Amount', 'Overtime Pay', 'Overtime Amount', 'LOP Amount', 'LOP Deduction']).map(column => column.kind), ['earning', 'earning', 'earning', 'earning', 'deduction', 'deduction'])
})

test('itemized salary uses wages and deduction totals as controls without double-counting amounts', () => {
  const { columns, result } = parse('Name of the person,Basic,HRA,Special Allowance,Wages,EPF@12%,Other Deduction,Total Deductions,Net Pay\nPerson,10000,4000,2000,16000,1200,100,1300,14700')
  assert.deepEqual(result.issues, [])
  assert.equal(columns[4].kind, 'grossTotal'); assert.equal(columns[7].kind, 'deductionTotal')
  const row = result.rows[0]
  assert.equal(row.earnings.reduce((total, item) => total + item.amount, 0), 16000)
  assert.equal(row.deductions.reduce((total, item) => total + item.amount, 0), 1300)
  assert.equal(row.declaredGross, 16000); assert.equal(row.declaredDeductions, 1300); assert.equal(row.netPay, 14700)
  assert.deepEqual(defaultPayslipMapping(['Basic', 'Gross Salary', 'Wages']).map(column => column.kind), ['earning', 'grossTotal', 'info'])
})

test('a single total salary is usable when itemized earning fields are absent', () => {
  for (const heading of ['Wages', 'Salary', 'Earned Salary', 'Gross Pay', 'Total Earnings']) {
    const { result } = parse(`Employee Name,${heading},Net Pay\nPerson,15000,15000`)
    assert.deepEqual(result.issues, [], heading)
    assert.equal(result.rows[0].earnings.length, 1, heading)
    assert.equal(result.rows[0].earnings[0].amount, 15000, heading)
  }
})

test('mapped source employee codes stay unchanged and absent codes remain blank for backend assignment', () => {
  const { sheet, columns, result } = parse('Name of the person,Emp Code,Basic,Net Pay\nSame Name,TEST-EMP-001,100,100\nSame Name,000017,100,100\nSame Name,,100,100')
  assert.deepEqual(result.issues, [])
  assert.deepEqual(result.rows.map(row => row.employeeCode), ['TEST-EMP-001', '000017', ''])
  const source = buildPayslipCalculationSource(sheet, 1, columns)
  assert.equal(source.rows[3].cells[1].value, '', 'the browser does not invent employee identities before saving')
})

test('the 43-column dummy salary layout maps correctly after columns are inserted before PLRS wages', () => {
  const headers = ['S.no.', 'District', 'Location Name (Tehsil/Sub Tehsil)', 'Name of the person', 'UAN', 'Email', "Father's/Husbands' name", 'Home District', 'Qualification', 'Designation_Manpower Category', 'Rate', 'Attendance', 'Wages', 'EPF@12%', 'ESIC@0.75%', 'Net Pay', 'Bonus@8.33%', 'EPF@13%', 'ESIC@3.25%', 'CTC', 'SC@1.48%', 'IGST@18%', 'Invoice', 'Bank Account Number', 'IFSC Code', 'Mobile', 'Client Name', 'UAN (Reference)', 'ESIC No', 'Adhaar Number', 'PAN No', 'Address', 'Employee Code', 'Payroll Month', 'Month Days', 'Basic', 'HRA', 'Conveyance Allowance', 'Special Allowance', 'Incentive', 'Salary Advance', 'Other Deduction', 'Total Deductions']
  assert.equal(headers.length, 43)
  const mapping = defaultPayslipMapping(headers), byHeader = Object.fromEntries(mapping.map(column => [column.sourceHeader, column.kind]))
  assert.equal(byHeader['Name of the person'], 'employeeName'); assert.equal(byHeader['Employee Code'], 'employeeCode'); assert.equal(byHeader.Email, 'email')
  assert.equal(byHeader.Mobile, 'info', 'inserted UAN/Email columns do not hide the phone')
  assert.equal(byHeader.Wages, 'grossTotal'); assert.equal(byHeader['Total Deductions'], 'deductionTotal'); assert.equal(byHeader['Net Pay'], 'netPay')
  assert.deepEqual(mapping.filter(column => column.kind === 'earning').map(column => column.sourceHeader), ['Basic', 'HRA', 'Conveyance Allowance', 'Special Allowance', 'Incentive'])
  assert.deepEqual(mapping.filter(column => column.kind === 'deduction').map(column => column.sourceHeader), ['EPF@12%', 'ESIC@0.75%', 'Salary Advance', 'Other Deduction'])
  assert.deepEqual(mapping.filter(column => column.kind === 'employer').map(column => column.sourceHeader), ['EPF@13%', 'ESIC@3.25%'])
  assert.equal(byHeader['Bonus@8.33%'], 'info'); assert.equal(byHeader.Invoice, 'ignore'); assert.equal(byHeader['SC@1.48%'], 'ignore')
  const reversed = Object.fromEntries(defaultPayslipMapping([...headers].reverse()).map(column => [column.sourceHeader, column.kind]))
  for (const header of ['Name of the person', 'Employee Code', 'Wages', 'EPF@12%', 'ESIC@3.25%', 'Basic', 'Total Deductions'])
    assert.equal(reversed[header], byHeader[header], header)
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

test('shared formula translation adjusts relative and mixed references but preserves absolute references', () => {
  assert.equal(translatePayslipSharedFormula('I5/$J$4*J5+SUM($A5:B$5)', 'K5', 'L7'), 'J7/$J$4*K7+SUM($A7:C$5)')
  assert.equal(translatePayslipSharedFormula('SUM(A:A,$B:C,1:2,$3:4)', 'D5', 'F8'), 'SUM(C:C,$B:E,4:5,$3:7)')
  assert.equal(translatePayslipSharedFormula('A1+$A$1+XFD1', 'B2', 'A1'), '#REF!+$A$1+#REF!')
  assert.equal(translatePayslipSharedFormula('XFD1', 'A1', 'B1'), '#REF!')
})

test('shared formulas do not rewrite quoted strings, sheet names, workbook names, structured references or functions', () => {
  const formula = `IF(A1="A1 ""B2""",'A1 and O''Brien'!B2,Sheet1!C3)+[A1.xlsx]Sheet2!D4+'[A1 book.xlsx]A1'!E5+Table1[A1]+LOG10(A1)+A1:Sheet2!B3`
  assert.equal(translatePayslipSharedFormula(formula, 'A1', 'B2'), `IF(B2="A1 ""B2""",'A1 and O''Brien'!C3,Sheet1!D4)+[A1.xlsx]Sheet2!E5+'[A1 book.xlsx]A1'!F6+Table1[A1]+LOG10(B2)+A1:Sheet2!C4`)
})

test('shared formula dependents can precede anchors and keep their original cached amounts', () => {
  const laterCell = { value: '14129.499999999998', formula: true }, anchorCell = { value: '100.125', formula: true }
  resolvePayslipSharedFormulas([
    { reference: 'K6', cell: laterCell, expression: '', type: 'shared', sharedIndex: '0' },
    { reference: 'K5', cell: anchorCell, expression: 'I5/$J$4*J5', type: 'shared', sharedIndex: '0' },
  ])
  assert.equal(laterCell.formulaText, 'I6/$J$4*J6')
  assert.equal(anchorCell.formulaText, 'I5/$J$4*J5')
  assert.equal(laterCell.value, '14129.499999999998')
  assert.equal(anchorCell.value, '100.125')
  assert.equal(laterCell.calculationError, undefined)
})

test('unsupported or unresolved formulas keep cached import working but explicitly block recalculation', () => {
  const { sheet, columns } = parse('Employee Name,Basic,Net Pay\nA,100,90')
  sheet.rows[1].cells[1].formula = true
  for (const type of ['array', 'dataTable', 'shared', 'normal']) {
    const cell = { value: '100', formula: true }
    resolvePayslipSharedFormulas([{ reference: 'B2', cell, expression: type === 'array' ? 'SUM(A1:A2)' : '', type, sharedIndex: '9' }])
    assert.match(cell.calculationError, /cannot be recalculated/)
    sheet.rows[1].cells[1] = cell
    assert.deepEqual(buildExcelPayslipRows(sheet, 1, columns).issues, [], type)
    const source = buildPayslipCalculationSource(sheet, 1, columns)
    assert.equal(source.rows[1].cells[1].calculationError, cell.calculationError)
    assert.equal(source.rows[1].cells[1].value, '100')
  }
})

test('ambiguous shared formula groups never silently use one anchor', () => {
  const cells = [1, 2, 3].map(() => ({ value: '1', formula: true }))
  resolvePayslipSharedFormulas(cells.map((cell, index) => ({ reference: `C${index + 1}`, cell, type: 'shared', sharedIndex: '4', expression: index === 2 ? '' : 'A1' })))
  assert.ok(cells.every(cell => /ambiguous/.test(cell.calculationError)))
})

test('calculation source preserves unmapped errors, excluded rows and header constants without retaining mutable references', () => {
  const sheet = parsePayslipCsv('Rate,30\nEmployee Name,Basic,Net Pay,Unused\nA,100,90,#REF!\nGrand Total,100,90')
  const columns = defaultPayslipMapping(payslipHeaders(sheet, 2)), excluded = [4]
  sheet.rows[2].cells[1] = { value: '100', formula: true, formulaText: 'B1*10/3' }
  sheet.rows[2].cells[3] = { value: '#REF!', formula: true, formulaText: '#REF!+1', error: '#REF!' }
  sheet.rows[2].cells[5] = { value: '000123' }
  sheet.columnCount = 6
  const source = buildPayslipCalculationSource(sheet, 2, columns, excluded)
  assert.equal(source.rows.length, 4)
  assert.equal(source.headerRow, 2)
  assert.equal(source.rows[0].cells[1].value, '30')
  assert.deepEqual(source.rows[2].cells[1], { value: '100', formulaText: 'B1*10/3' })
  assert.deepEqual(source.rows[2].cells[3], { value: '#REF!', formulaText: '#REF!+1', error: '#REF!' })
  assert.deepEqual(source.rows[2].cells[4], { value: '' }, 'sparse cells become explicit blanks')
  assert.equal(source.rows[2].cells[5].value, '000123')
  source.rows[0].cells[1].value = '31'; source.columns[0].label = 'Changed'; source.excludedSourceRows.push(3)
  assert.equal(sheet.rows[0].cells[1].value, '30')
  assert.notEqual(columns[0].label, 'Changed')
  assert.deepEqual(excluded, [4])
  sheet.rows[2].cells[1] = { value: '100', formula: true }
  assert.match(buildPayslipCalculationSource(sheet, 2, columns).rows[2].cells[1].calculationError, /missing/)
})

// Run with FREVO_PLAYWRIGHT_MODULE pointing to an installed Playwright entry point.
// This exercises the browser's real namespace-aware DOMParser without adding a runtime dependency.
test('XLSX imports preserve cached values with default or prefixed XML namespaces', { skip: !process.env.FREVO_PLAYWRIGHT_MODULE }, async t => {
  const { chromium } = await import(pathToFileURL(process.env.FREVO_PLAYWRIGHT_MODULE).href)
  const browser = await chromium.launch({ headless: true, ...(process.env.FREVO_CHROMIUM_PATH ? { executablePath: process.env.FREVO_CHROMIUM_PATH } : {}) })
  try {
    const page = await browser.newPage()
    await page.route('**/*', route => route.abort()) // Pure file parsing: never call an application/API.
    const parseFixture = async files => page.evaluate(async ({ moduleUrl, bytes }) => {
      const parser = await import(moduleUrl)
      return parser.parseExcelPayslipFile(new File([new Uint8Array(bytes)], 'fixture.xlsx'))
    }, { moduleUrl: 'data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'), bytes: [...xmlFixtureZip(files)] })
    for (const prefixed of [false, true]) await t.test(prefixed ? 'prefixes on workbook, rows, values, styles, shared text and relationship ID' : 'existing default-namespace workbook remains compatible', async () => {
      const [sheet] = await parseFixture(namespaceFixture(prefixed))
      assert.equal(sheet.name, 'Salary'); assert.equal(sheet.dateSystem, '1904')
      assert.equal(sheet.rows.length, 2); assert.equal(sheet.columnCount, 6)
      assert.equal(sheet.rows[1].cells[0].value, 'Example Person')
      assert.equal(sheet.rows[1].cells[1].value, '000017', 'zero-format identifier remains intact')
      assert.equal(sheet.rows[1].cells[2].value, '123.456')
      assert.deepEqual(sheet.rows[1].cells[3], { value: '90.25', formula: true, formulaText: 'C2-33.206' })
      assert.equal(sheet.rows[1].cells[4].value, 'Two parts')
      assert.equal(sheet.rows[1].cells[5].error, '#REF!')
      const imported = buildExcelPayslipRows(sheet, 1, defaultPayslipMapping(payslipHeaders(sheet, 1)))
      assert.deepEqual(imported.issues, []); assert.equal(imported.rows[0].netPay, 90.25)
    })
    await t.test('external worksheet relationships remain blocked when package XML is prefixed', async () => {
      const files = namespaceFixture(true)
      files['xl/_rels/workbook.xml.rels'] = files['xl/_rels/workbook.xml.rels'].replace('Target="worksheets/sheet1.xml"', 'Target="https://example.invalid/salary.xml" TargetMode="External"')
      await assert.rejects(() => parseFixture(files), /Worksheet relationship is missing/)
    })
    await t.test('prefixed formulas without cached results still require source recalculation', async () => {
      const files = namespaceFixture(true)
      files['xl/worksheets/sheet1.xml'] = files['xl/worksheets/sheet1.xml'].replace('<x:v>90.25</x:v>', '')
      const [sheet] = await parseFixture(files)
      assert.equal(sheet.rows[1].cells[3].missingCachedValue, true)
      assert.match(buildExcelPayslipRows(sheet, 1, defaultPayslipMapping(payslipHeaders(sheet, 1))).issues[0].message, /no saved result/)
    })
  } finally { await browser.close() }
})

function namespaceFixture(prefixed) {
  const main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
  const relation = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
  const prefix = prefixed ? 'x:' : '', ns = prefixed ? `xmlns:x="${main}"` : `xmlns="${main}"`
  const xml = (root, body) => `<${prefix}${root} ${ns}>${body}</${prefix}${root}>`
  const tag = (name, text, attributes = '') => `<${prefix}${name}${attributes}>${text}</${prefix}${name}>`
  const cell = (reference, body, attributes = '') => tag('c', body, ` r="${reference}"${attributes}`)
  const inline = text => tag('is', tag('t', text))
  const headers = ['Employee Name', 'Employee Code', 'Basic', 'Net Pay', 'Notes', 'Ignored error']
  const first = tag('row', headers.map((text, index) => cell(String.fromCharCode(65 + index) + '1', inline(text), ' t="inlineStr"')).join(''), ' r="1"')
  const second = tag('row', cell('A2', tag('v', '0'), ' t="s"') + cell('B2', tag('v', '17'), ' s="1"')
    + cell('C2', tag('v', '123.456')) + cell('D2', tag('f', 'C2-33.206') + tag('v', '90.25'))
    + cell('E2', tag('is', tag('r', tag('t', 'Two ')) + tag('r', tag('t', 'parts'))), ' t="inlineStr"')
    + cell('F2', tag('v', '#REF!'), ' t="e"'), ' r="2"')
  const relPrefix = prefixed ? 'link' : 'r', packagePrefix = prefixed ? 'p:' : ''
  return {
    'xl/workbook.xml': xml('workbook', tag('workbookPr', '', ' date1904="1"') + tag('sheets', tag('sheet', '', ` name="Salary" sheetId="1" xmlns:${relPrefix}="${relation}" ${relPrefix}:id="sheet-1"`))),
    'xl/_rels/workbook.xml.rels': `<${packagePrefix}Relationships xmlns${prefixed ? ':p' : ''}="http://schemas.openxmlformats.org/package/2006/relationships"><${packagePrefix}Relationship Id="sheet-1" Target="worksheets/sheet1.xml"/></${packagePrefix}Relationships>`,
    'xl/sharedStrings.xml': xml('sst', tag('si', tag('r', tag('t', 'Example ')) + tag('r', tag('t', 'Person')))),
    'xl/styles.xml': xml('styleSheet', tag('numFmts', tag('numFmt', '', ' numFmtId="164" formatCode="000000"')) + tag('cellXfs', tag('xf', '', ' numFmtId="0"') + tag('xf', '', ' numFmtId="164"'))),
    'xl/worksheets/sheet1.xml': xml('worksheet', tag('sheetData', first + second)),
  }
}

function xmlFixtureZip(files) {
  const local = [], central = []; let offset = 0
  for (const [path, xml] of Object.entries(files)) {
    const name = Buffer.from(path), data = Buffer.from(xml)
    let crc = 0xffffffff
    for (const byte of data) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0) }
    crc = (crc ^ 0xffffffff) >>> 0
    const header = Buffer.alloc(30); header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4)
    header.writeUInt32LE(crc, 14); header.writeUInt32LE(data.length, 18); header.writeUInt32LE(data.length, 22); header.writeUInt16LE(name.length, 26)
    const entry = Buffer.alloc(46); entry.writeUInt32LE(0x02014b50); entry.writeUInt16LE(20, 4); entry.writeUInt16LE(20, 6)
    entry.writeUInt32LE(crc, 16); entry.writeUInt32LE(data.length, 20); entry.writeUInt32LE(data.length, 24); entry.writeUInt16LE(name.length, 28); entry.writeUInt32LE(offset, 42)
    local.push(header, name, data); central.push(entry, name); offset += header.length + name.length + data.length
  }
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10)
  end.writeUInt32LE(central.reduce((sum, part) => sum + part.length, 0), 12); end.writeUInt32LE(offset, 16)
  return Buffer.concat([...local, ...central, end])
}
