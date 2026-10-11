import assert from 'node:assert/strict'
import { buildEmployeeCoverage } from '../payroll-ui/src/utils/clientConfigCoverage.ts'

const employee = id => ({ id, clientId: 1, isActive: true, employeeCode: `EMP${id}`, firstName: 'Test', lastName: `${id}`, annualCtc: 240000, salaryStructureId: '', salaryComponents: {}, workLocationId: 7, department: 'Engineering', designation: 'Developer', gender: 'Female', grade: 'G1', portalAccess: false, workEmail: '', reportingManagerId: 0, paymentDetails: {} })
const employees = Array.from({ length: 100 }, (_, index) => employee(index + 1))
const policy = { id: 1, clientId: 1, isActive: true, name: 'Standard policy', employeeIds: employees.slice(0, 80).map(row => row.id) }
const input = {
  clientId: 1, asOf: '2026-10-11', failures: {}, employees: [...employees, { ...employee(101), isActive: false }, { ...employee(102), clientId: 2 }],
  attendanceGroups: [policy, { ...policy, id: 2, isActive: false, employeeIds: [81] }, { ...policy, id: 3, clientId: 2, employeeIds: [82] }],
  setup: { salaryStructures: [], salaryComponents: [{ id: 1, active: true }] },
  shifts: [], attendanceSettings: null, leaveTypes: [], holidays: [], locations: [{ id: 7, clientId: 1, name: 'Office', isActive: true }],
  travel: { clientSettings: [], policies: [], assignments: [] }
}
const get = (key, data = input) => buildEmployeeCoverage(data).find(row => row.key === key)
const count = (key, status, data) => get(key, data).results.filter(row => row.status === status).length
assert.equal(get('attendance-coverage').results.length, 100)
assert.equal(count('attendance-coverage', 'Covered'), 80)
assert.equal(count('attendance-coverage', 'Uncovered'), 20)
assert.equal(count('payroll-coverage', 'Covered'), 80)
assert.equal(count('salary-coverage', 'Covered'), 100, 'CTC alternative does not need explicit template')

const overlap = { ...input, attendanceGroups: [policy, { ...policy, id: 2, employeeIds: [1, 1] }] }
assert.equal(count('attendance-coverage', 'Covered', overlap), 79)
assert.equal(count('attendance-coverage', 'Review', overlap), 1)
assert.equal(count('attendance-coverage', 'Uncovered', overlap), 20)
assert.equal(get('attendance-coverage', { ...input, attendanceGroups: [{ ...policy, employeeIds: [1, 1] }] }).results.filter(row => row.status === 'Covered').length, 1)

const salaryInput = { ...input, employees: [{ ...employee(1), salaryStructureId: '10', annualCtc: 0 }], setup: { salaryComponents: [{ id: 1, active: true }], salaryStructures: [{ id: 10, active: true, clientId: '1:Alpha;2:Beta', name: 'Shared', lines: [{ componentId: '1' }] }] } }
assert.equal(count('salary-coverage', 'Covered', salaryInput), 1)
assert.equal(count('salary-coverage', 'Review', { ...salaryInput, setup: { ...salaryInput.setup, salaryStructures: [{ ...salaryInput.setup.salaryStructures[0], active: false }] } }), 1)
assert.equal(count('salary-coverage', 'Review', { ...salaryInput, setup: { ...salaryInput.setup, salaryComponents: [{ id: 1, active: false }] } }), 1)
assert.equal(count('salary-coverage', 'Uncovered', { ...input, employees: [{ ...employee(1), annualCtc: 0 }] }), 1)
assert.equal(count('salary-coverage', 'Covered', { ...input, employees: [{ ...employee(1), annualCtc: 0, salaryComponents: { BASIC: 10000 } }] }), 1)

const shift = { id: 9, clientId: 1, isActive: true, shiftName: 'Day', effectiveFrom: '2026-01-01', effectiveTo: null }
assert.equal(count('shift-coverage', 'Covered', { ...input, shifts: [shift], attendanceSettings: { id: 1, shiftId: 9 } }), 100)
assert.equal(count('shift-coverage', 'Review', { ...input, shifts: [{ ...shift, effectiveTo: '2026-10-10' }], attendanceSettings: { id: 1, shiftId: 9 } }), 100)
assert.equal(count('shift-coverage', 'Uncovered'), 100)

const leave = { id: 1, clientId: 1, name: 'Paid leave', isActive: true, effectiveFrom: '2026-01-01', applicabilityMode: 'Criteria based employees', workLocation: 'Office', department: 'Engineering', designation: '', gender: 'Female', attendanceAction: 'Mark as leave' }
assert.equal(count('leave-coverage', 'Covered', { ...input, leaveTypes: [leave] }), 100)
assert.equal(count('leave-coverage', 'Uncovered', { ...input, leaveTypes: [{ ...leave, gender: 'Male' }] }), 100)
assert.equal(count('leave-coverage', 'Uncovered', { ...input, leaveTypes: [{ ...leave, expiresOn: '2026-09-30' }] }), 100)
assert.equal(count('leave-coverage', 'Uncovered', { ...input, leaveTypes: [{ ...leave, attendanceAction: 'Mark as present' }] }), 100)

assert.equal(get('travel-coverage').disabled, true)
const travel = { clientSettings: [{ clientId: 1, isEnabled: true }], policies: [{ id: 1, companyId: 1, policyName: 'Standard', isActive: true, status: 'Active', effectiveFrom: '2026-01-01' }], assignments: [{ id: 1, companyId: 1, policyId: 1, isActive: true, effectiveFrom: '2026-01-01', branchId: null, department: '', designation: '', grade: '', employeeId: 1, priority: 1 }] }
assert.equal(count('travel-coverage', 'Covered', { ...input, travel }), 1)
assert.equal(count('travel-coverage', 'Uncovered', { ...input, travel }), 99)
assert.equal(count('travel-coverage', 'Uncovered', { ...input, travel: { ...travel, policies: [{ ...travel.policies[0], effectiveTo: '2026-10-01' }] } }), 100)
assert.equal(get('travel-coverage', { ...input, travel: { ...travel, clientSettings: [{ clientId: 1, isEnabled: true, effectiveFrom: '2027-01-01' }] } }).disabled, true)
assert.equal(count('holiday-coverage', 'Covered', { ...input, holidays: [{ clientId: 1, startDate: '2026-01-01', endDate: '2026-01-01', allLocations: false, workLocationIds: [7] }] }), 100)
assert.equal(count('location-coverage', 'Uncovered', { ...input, locations: [{ id: 7, clientId: 2, isActive: true }] }), 100)

const failed = get('attendance-coverage', { ...input, failures: { groups: 'Forbidden' } })
assert.equal(failed.unavailable, true)
assert.equal(failed.results.length, 0)
assert.ok(buildEmployeeCoverage({ ...input, failures: { employees: 'Timeout' } }).every(row => row.unavailable && row.results.length === 0))
assert.ok(buildEmployeeCoverage({ ...input, employees: [] }).every(row => row.results.length === 0))
console.log('PASS: 80/20 coverage, unique/conflicting membership, inactive/client isolation, salary alternatives, effective shifts/leave/travel, holiday/location scope, unavailable and empty data')
