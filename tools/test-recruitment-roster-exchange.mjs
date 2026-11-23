import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = fileURLToPath(new URL('../payroll-ui/', import.meta.url))
const require = createRequire(path.join(root, 'package.json')), ts = require('typescript'), cache = new Map()
function load(name) {
  const file = path.resolve(root, 'src', name + '.ts'); if (cache.has(file)) return cache.get(file)
  const exports = {}; cache.set(file, exports)
  new Function('exports', 'require', ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports, p => load(path.relative(path.join(root, 'src'), path.resolve(path.dirname(file), p)).replaceAll('\\', '/')))
  return exports
}
const { standardRosterSheets, standardRosterCell } = load('utils/recruitmentRosterExchangeTemplate'), { buildXlsxBlob } = load('utils/xlsx')
const columns = [
  { key: 'profileCode', header: 'Profile Code [profileCode]', infotype: 'Identity', type: 'text', readOnly: true },
  { key: 'employeeCode', header: 'Employee Code [employeeCode]', infotype: '0001', type: 'text' },
  { key: 'bankAccountNo', header: 'Bank Account Number [bankAccountNo]', infotype: '0009', type: 'text' },
  { key: 'annualCtc', header: 'Annual CTC INR/year [annualCtc]', infotype: '0008', type: 'money' },
  { key: 'SALARY:BASIC', header: 'Basic [SALARY:BASIC]', infotype: '0008', type: 'amount' },
  { key: 'revision', header: 'Record Version [revision]', infotype: 'Identity', type: 'text', readOnly: true },
]
const template = { version: 'FREVO-EMPLOYEE-1', clientId: 4, templateId: 6, sourceGroup: 'Synthetic', columns, rows: [{ profileCode: 'CAN-SYN-001', employeeCode: '00001', bankAccountNo: '000123456789012345', annualCtc: '0', 'SALARY:BASIC': '12345.6789', revision: 'a'.repeat(64) }] }
const blank = standardRosterSheets(template, 'Insert'), update = standardRosterSheets(template, 'Update')
assert.equal(blank.length, 1); assert.equal(update.length, 1); assert.equal(update[0].name, 'Employees')
assert.deepEqual(blank[0].rows[0], update[0].rows[0]); assert.equal(blank[0].rows.length, 2)
assert.equal(update[0].rows[1][0].value, 'CAN-SYN-001'); assert.equal(blank[0].rows[1][0].value, '')
assert.equal(update[0].rows[1][2].value, '000123456789012345'); assert.equal(update[0].rows[1][2].type, 'text')
assert.equal(update[0].rows[1][3].value, 0); assert.equal(update[0].rows[1][4].value, 12345.6789)
assert.equal(standardRosterCell(columns[4], '123456789012.1234').type, 'text')
assert.deepEqual(update[0].hiddenColumns, [5, 6]); assert.equal(update[0].freezeRows, 1)
const xml = new TextDecoder().decode(await buildXlsxBlob(update).arrayBuffer())
assert.match(xml, /<sheets><sheet name="Employees" sheetId="1" r:id="rId1"\/><\/sheets>/)
assert.match(xml, /min="7" max="7"[^>]*hidden="1"/); assert.match(xml, /__FREVO_TEMPLATE__/)
assert.doesNotMatch(xml, /sheet2\.xml|<f>|Instructions|name="Fields"|name="Template"/)
globalThis.sessionStorage = { getItem: () => null, setItem: () => {} }
const scope = load('services/clientRequestScope')
scope.configureClientRequestScope({ id: 7, clientAccessMode: 'Selected', assignedClientIds: [4, 8] }, 4)
for (const route of ['preview', 'import']) {
  assert.equal(scope.activeClientHeader(`/api/recruitment/document-roster/standard/${route}?clientId=8`, 'POST'), undefined)
  assert.equal(scope.activeClientHeader(`/api/recruitment/document-roster/standard/${route}`, 'POST'), '4')
}
console.log('PASS single-sheet insert/update schema, prefilled identity, precise values, hidden system fields and selected-client scope')
