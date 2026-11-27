import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exported = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeeEmailEditLink.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)(exported)
const { employeeEmailEditTarget, consumeEmployeeEmailEditLink } = exported
const rows = [{ id: 12, clientId: 5, name: 'Synthetic employee' }, { id: 21, clientId: 7, name: 'Another employee' }]

test('email reminder opens only a loaded employee in the authorized current client and editable infotype', () => {
  const query = new URLSearchParams('clientId=5&employeeId=12&edit=0001')
  assert.equal(employeeEmailEditTarget(query, true, 5, rows), rows[0])
  assert.equal(employeeEmailEditTarget(query, false, 5, rows), undefined)
  assert.equal(employeeEmailEditTarget(query, true, 7, rows), undefined)
  assert.equal(employeeEmailEditTarget(query, true, 5, []), undefined)
  assert.equal(employeeEmailEditTarget(new URLSearchParams('clientId=7&employeeId=12&edit=0001'), true, 7, rows), undefined)
  assert.equal(employeeEmailEditTarget(new URLSearchParams('clientId=5&employeeId=12&edit=0009'), true, 5, rows), undefined)
})

test('malformed, missing or oversized employee and client identities cannot trigger the editor', () => {
  for (const id of ['', '0', '-1', '1.2', '12abc', '9007199254740993']) {
    assert.equal(employeeEmailEditTarget(new URLSearchParams(`clientId=5&employeeId=${id}&edit=0001`), true, 5, rows), undefined)
    assert.equal(employeeEmailEditTarget(new URLSearchParams(`clientId=${id}&employeeId=12&edit=0001`), true, Number(id), rows), undefined)
  }
  assert.equal(employeeEmailEditTarget(new URLSearchParams('clientId=5&employeeId=12'), true, 5, rows), undefined)
})

test('consuming the one-time editor action retains client and employee context and other navigation options', () => {
  const query = new URLSearchParams('clientId=5&employeeId=12&edit=0001&month=2026-10')
  const next = consumeEmployeeEmailEditLink(query)
  assert.equal(next.toString(), 'clientId=5&employeeId=12&month=2026-10')
  assert.equal(query.get('edit'), '0001')
  assert.equal(employeeEmailEditTarget(next, true, 5, rows), undefined)
})
