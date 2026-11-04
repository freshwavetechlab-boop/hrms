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
const { buildPayrollFormulaWorkbook, translatePayrollExcelFormula, excelCurrencyRound, preparePayrollFormulaWorkbookRequest } = load('payrollFormulaWorkbook')
const { buildXlsxBlob } = load('xlsx')
const fixture = JSON.parse(readFileSync(new URL('./fixtures/payroll-formula-workbook.json', import.meta.url), 'utf8'))

test('one employee keeps two formula rows and linked attendance identities for different location wages', () => {
  const rows = [0, 1].map(index => {
    const row = structuredClone(fixture.employees?.[0] || fixture[0])
    row.employeeId = 1; row.employeeCode = '0001'
    row.location = { key: `native:${index}`, assignmentId: `assignment-${index}`, workLocationId: index + 10,
      workLocationName: `Location ${index}`, rulesHash: `rules-${index}`, contextVersion: 'scope-version', inputVersion: `input-${index}` }
    return row
  })
  const sheets = buildPayrollFormulaWorkbook('Synthetic location client', '2026-10', rows)
  const payroll = sheets.find(sheet => sheet.name === 'Payroll'), attendance = sheets.find(sheet => sheet.name === 'Attendance')
  assert.equal(payroll.rows.length, 3); assert.equal(attendance.rows.length, 3)
  assert.equal(payroll.rows[1][0].value, '0001'); assert.equal(payroll.rows[2][0].value, '0001')
  for (const [index, row] of attendance.rows.slice(1).entries()) {
    assert.equal(row[0], '0001'); assert.equal(row[8], `native:${index}`); assert.equal(row[10], index + 10)
    assert.equal(row[12], `rules-${index}`); assert.equal(row[13], 'scope-version'); assert.equal(row[14], `input-${index}`)
    assert.equal(row[3].formula, `'Payroll'!F${index + 2}`); assert.equal(row[4].formula, `'Payroll'!G${index + 2}`); assert.equal(row[5].formula, `'Payroll'!H${index + 2}`)
  }
  rows[1].location.key = rows[0].location.key
  assert.throws(() => buildPayrollFormulaWorkbook('Synthetic', '2026-10', rows), /distinct employee\/location/)
})

test('formula download has no all-client fallback and requires an explicit policy employee selection',()=>{
  assert.throws(()=>preparePayrollFormulaWorkbookRequest(9,'2026-09',null,[11,12]),/attendance policy/)
  const policy={id:'policy-A',groupIds:[101,102],employeeIds:[11,12,13]}
  assert.throws(()=>preparePayrollFormulaWorkbookRequest(9,'2026-09',policy,[]),/Select employees/)
  assert.throws(()=>preparePayrollFormulaWorkbookRequest(9,'2026-09',policy,[11,99]),/chosen attendance policy/)
  assert.throws(()=>preparePayrollFormulaWorkbookRequest(9,'2026-09',policy,[11,11]),/Select employees/)
  assert.deepEqual(preparePayrollFormulaWorkbookRequest(9,'2026-09',policy,[12]),{clientId:9,payPeriod:'2026-09',employeeIds:[12],attendancePolicyBatchId:'policy-A',attendanceGroupIds:[101,102]})
})

test('native-engine fixtures export real formulas, exact cached amounts, editable inputs and stable identities', async () => {
  const before = JSON.stringify(fixture)
  const sheets = buildPayrollFormulaWorkbook('Synthetic client', '2026-09', fixture)
  const payroll = sheets[0]
  assert.equal(payroll.rows.length, fixture.length + 1)
  assert.equal(payroll.freezeColumns, 2)
  for (const [index, employee] of fixture.entries()) {
    const row = payroll.rows[index + 1]
    assert.equal(row[0].value, employee.employeeCode)
    assert.equal(row[5].value, 26)
    assert.equal(row[7].value, employee.calculation.inputs.find(input => input.name === 'PAYABLE_DAYS').value)
    assert.equal(row[12].value, employee.calculation.net)
    assert.match(row[12].formula, /MAX\(0,J\d+-K\d+\)/)
    assert.equal(row[14].value, 0)
    assert.ok(row.some(cell => typeof cell === 'object' && cell.formula?.includes(`H${index+2}`)))
  }
  assert.equal(JSON.stringify(fixture), before)
  const bytes = new Uint8Array(await buildXlsxBlob(sheets).arrayBuffer())
  const xml = new TextDecoder().decode(bytes)
  assert.match(xml, /<f>/)
  assert.match(xml, /calcMode="auto" fullCalcOnLoad="1"/)
  assert.match(xml, /state="frozen"/)
  assert.match(xml, /s="2"/)
  assert.ok(!xml.includes('[1]Sheet'))
  const header=payroll.rows[0].map(cell=>cell.value)
  const address=index=>{let name='';for(let n=index+1;n>0;n=Math.floor((n-1)/26))name=String.fromCharCode(65+(n-1)%26)+name;return name+'2'}
  for(const code of ['PF','ESIC','EPF_ER','ESIC_ER']){
    const raw=header.indexOf(`${code} input / raw`),earned=header.indexOf(`${code} earned`)
    assert.ok(raw>=15&&earned>raw,`${code} must retain its own calculated columns`)
    assert.ok(payroll.rows[1][raw].formula,`${code} raw cell must contain the configured calculation`)
    assert.ok(payroll.rows[1][earned].formula,`${code} earned cell must contain an Excel formula`)
    assert.match(xml,new RegExp(`<c r="${address(earned)}"[^>]*><f>`),`${code} must be stored as a real XLSX formula cell`)
  }
  if (process.env.FREVO_FORMULA_XLSX) writeFileSync(process.env.FREVO_FORMULA_XLSX, bytes)
})

test('formula translation preserves dependencies, conditional rules and explicit formula rounding', () => {
  assert.equal(translatePayrollExcelFormula('IF(PAYABLE_DAYS>0,ROUND(BASIC_MONTHLY*12%,0),0)', { PAYABLE_DAYS: 'H2', BASIC_MONTHLY: 'Q2' }), 'IF((H2)>0,ROUND((Q2)*12%,0),0)')
  assert.throws(() => translatePayrollExcelFormula('[1]Rates!A3', {}))
  assert.throws(() => translatePayrollExcelFormula('UNKNOWN_INPUT*2', {}))
  assert.match(excelCurrencyRound('P2'), /ROUND\(\(P2\)\*100\/2,0\)\*2/)
})

test('missing native attendance and policy-only results cannot become fake formula exports', () => {
  const employee = structuredClone(fixture[0])
  employee.calculation.inputs.find(input => input.name === 'PAYABLE_DAYS').value = null
  assert.throws(() => buildPayrollFormulaWorkbook('Synthetic', '2026-09', [employee]), /attendance/)
  const policy = structuredClone(fixture[0])
  policy.calculation.lines[0].source = 'Effective-dated PF policy (overrides formula)'
  assert.throws(() => buildPayrollFormulaWorkbook('Synthetic', '2026-09', [policy]), /dated policy/)
})

test('approved adjustments with the same code remain separate and employer amounts stay outside net', () => {
  const employee = structuredClone(fixture[2])
  employee.calculation.lines.push({ code: 'BASIC', name: 'Adjustment', category: 'Earning', formula: '', raw: 300, monthly: 300, earned: 300, proRata: false, source: 'Approved period adjustment', inputs: {} })
  employee.calculation.gross += 300; employee.calculation.net += 300
  const sheet = buildPayrollFormulaWorkbook('Synthetic', '2026-09', [employee])[0]
  const headers = sheet.rows[0].map(cell => cell.value)
  assert.equal(headers.filter(value => value === 'BASIC earned').length, 1)
  assert.equal(headers.filter(value => value === 'BASIC adjustment earned').length, 1)
  assert.match(sheet.rows[1][12].formula, /J2-K2/)
  assert.ok(!sheet.rows[1][12].formula.includes('L2'))
})
