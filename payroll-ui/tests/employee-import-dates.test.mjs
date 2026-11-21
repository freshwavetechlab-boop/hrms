import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const load = file => { const exports = {}; new Function('exports', ts.transpileModule(readFileSync(new URL(file, import.meta.url),'utf8'), {compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText)(exports); return exports }
const { normalizeEmployeeImportDate } = load('../src/utils/employeeImportDate.ts')
const { validateImportPreview } = load('../src/utils/importPreview.ts')
const cases = JSON.parse(readFileSync(new URL('../../Payroll.API.Tests/Fixtures/employee-import-dates.json',import.meta.url),'utf8'))
for (const {input,expected} of cases) test(`Employee bulk date: ${input || '(blank)'}`,()=>assert.equal(normalizeEmployeeImportDate(input),expected))
test('mixed-format employee rows pass the actual preview validator without changing identity or pay',()=>{
  const rows = cases.filter(row=>row.expected).map((row,index)=>[`SYN-${index+1}`,row.input,'12345.67','001234'])
  const source = structuredClone(rows)
  const normalized = rows.map(row=>row.map((cell,index)=>index===1?normalizeEmployeeImportDate(cell):cell))
  const rules={dates:['Date Of Joining'],normalizeDate:normalizeEmployeeImportDate,unique:[['Employee Code']]}
  assert.deepEqual(validateImportPreview({headers:['Employee Code','Date Of Joining','Amount','Account'],rows},rules),[])
  assert.deepEqual(validateImportPreview({headers:['Employee Code','Date Of Joining','Amount','Account'],rows:normalized},rules),[])
  assert.deepEqual(rows,source)
  assert.equal(normalized.every((row,index)=>row[0]===source[index][0]&&row[2]==='12345.67'&&row[3]==='001234'),true)
  assert.equal(validateImportPreview({headers:['Date Of Joining'],rows:[['31/02/2026']]},{dates:['Date Of Joining'],normalizeDate:normalizeEmployeeImportDate}).length,1)
})
