import test from 'node:test'
import assert from 'node:assert/strict'
import { savedPayrollDays } from '../payroll-ui/src/utils/savedPayrollAttendance.ts'

function fixture(paid=26) {
  const proof={Version:2,ClientId:17,EmployeeId:21,PayPeriod:'2026-10',BatchId:'a'.repeat(32),RowId:'row-7',ImportId:'b'.repeat(32),SourceHash:'c'.repeat(64),PayrollDays:26,PayableDays:paid,ActualPresentDays:null,PresentMeaning:'NotMapped',PayableMeaning:'AlreadyPayable',Mode:'SourceSeed',ReviewedExtraDutyUnits:true}
  const line={Id:'ATTENDANCE_INPUT',Category:'Summary',payrollDays:26,payableDays:paid,actualPresentDays:null,sourceType:'ExcelPayableOnly',sourceEvidence:proof}
  return {employee:{employeeId:21,presentDays:null,payableDays:paid,detailsJson:JSON.stringify([line])},line,proof}
}
test('saved source days preserve paid units and remove calendar-only unpaid units',()=>{
  for(const paid of [25,26,40]){const {employee}=fixture(paid),before=JSON.stringify(employee);assert.equal(savedPayrollDays(employee,31,17,'2026-10'),26);assert.equal(JSON.stringify(employee),before)}
})
test('ordinary known attendance and legacy details keep the existing run divisor',()=>{
  const {employee}=fixture();employee.presentDays=26;assert.equal(savedPayrollDays(employee,31,17,'2026-10'),31)
  employee.presentDays=null;employee.detailsJson='[]';assert.equal(savedPayrollDays(employee,31,17,'2026-10'),31)
})
test('a different employee, client, month, value or ambiguous proof cannot change displayed basis',()=>{
  for(const change of ['employee','client','month','paid','divisor','duplicate','proof','version','hash']){
    const {employee,line,proof}=fixture()
    if(change==='employee')proof.EmployeeId++
    if(change==='client')proof.ClientId++
    if(change==='month')proof.PayPeriod='2026-09'
    if(change==='paid')proof.PayableDays--
    if(change==='divisor')line.payrollDays=31
    if(change==='proof')line.sourceEvidence=null
    if(change==='version')proof.Version=99
    if(change==='hash')proof.SourceHash='bad'
    employee.detailsJson=JSON.stringify(change==='duplicate'?[line,line]:[line])
    assert.equal(savedPayrollDays(employee,31,17,'2026-10'),31,change)
  }
})
