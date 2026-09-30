import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const module = { exports: {} }
new Function('exports', ts.transpileModule(read('../src/utils/employeeWorkforce.ts'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(module.exports)
const { workforcePath, matchesWorkforce } = module.exports
const employees = [
  { id: 1, clientId: 20, isActive: true, personalDetails: { employmentType: 'Contract', skillCategory: 'Skilled' } },
  { id: 2, clientId: 20, isActive: true, personalDetails: { employmentType: ' contract ', skillCategory: 'Skilled' } },
  { id: 3, clientId: 20, isActive: false, personalDetails: { employmentType: 'Contract' } },
  { id: 4, clientId: 30, isActive: true, personalDetails: { employmentType: 'Contract' } },
  { id: 5, clientId: 20, isActive: true, personalDetails: {} },
  { id: 6, clientId: 20, isActive: true, personalDetails: { employmentType: 'Part time', skillCategory: 'Unskilled' } },
]

test('dashboard drilldown preserves client, matches exact classification and excludes inactive employees', () => {
  const url = new URL(workforcePath(20, { employmentType: 'Contract' }), 'http://hrms.test')
  assert.equal(url.pathname, '/employees/master')
  assert.equal(url.searchParams.get('source'), 'workforce')
  assert.deepEqual(employees.filter(row => matchesWorkforce(row, url.searchParams, [])).map(row => row.id), [1, 2])
  url.searchParams.set('employmentType', 'Not specified')
  assert.deepEqual(employees.filter(row => matchesWorkforce(row, url.searchParams, [])).map(row => row.id), [5])
  url.searchParams.delete('employmentType')
  url.searchParams.set('skillCategory', 'Skilled')
  assert.equal(employees.filter(row => matchesWorkforce(row, url.searchParams, [])).length, 2)
})

test('campus and ESS drilldowns combine filters without leaking another client', () => {
  const params = new URL(workforcePath(20, { location: 'Delhi', campusGender: 'other', portalAccess: 'Not enabled' }), 'http://hrms.test').searchParams
  const locations = [{ id: 9, name: 'Delhi' }, { id: 10, name: 'Mumbai' }]
  const row = { ...employees[0], workLocationId: 9, gender: '', portalAccess: false }
  assert.equal(matchesWorkforce(row, params, locations), true)
  for (const change of [{ clientId: 30 }, { gender: 'Male' }, { workLocationId: 10 }, { portalAccess: true }]) assert.equal(matchesWorkforce({ ...row, ...change }, params, locations), false)
})

test('shared SQL classification expressions agree with dashboard drilldown including missing JSON', () => {
  const source = read('../../Payroll.API/Repositories/EmployeeClassification.cs')
  const sql = name => source.match(new RegExp(`string ${name} = "([^"]+)"`))[1]
  const db = new DatabaseSync(':memory:')
  try {
    // SQLite JSON extraction already unquotes strings; the remaining expression is shared SQL.
    db.function('JSON_UNQUOTE', value => value)
    db.exec('CREATE TABLE employees (Id INTEGER, ClientId INTEGER, IsActive INTEGER, PersonalJson TEXT)')
    const insert = db.prepare('INSERT INTO employees VALUES (?,?,?,?)')
    for (const row of employees) insert.run(row.id, row.clientId, +row.isActive, JSON.stringify(row.personalDetails))
    insert.run(7, 20, 1, '{broken')
    insert.run(8, 20, 1, '{"employmentType":null,"skillCategory":null}')
    for (const [column, field] of [['EmploymentType', 'employmentType'], ['Category', 'skillCategory']]) {
      const groups = db.prepare(`SELECT ${sql(column)} AS label, COUNT(*) AS count FROM employees e WHERE e.IsActive=1 AND e.ClientId=20 GROUP BY label COLLATE NOCASE`).all()
      for (const group of groups) {
        const params = new URL(workforcePath(20, { [field]: group.label }), 'http://hrms.test').searchParams
        const rows = [...employees, { ...employees[4], id: 7 }, { ...employees[4], id: 8 }]
        assert.equal(rows.filter(row => matchesWorkforce(row, params, [])).length, group.count)
      }
    }
  } finally { db.close() }
})
