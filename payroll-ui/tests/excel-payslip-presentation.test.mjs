import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/excelPayslipPresentation.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(exports)
const {payslipDisplayInformation,payslipDesignation,mergePayslipMasterDetails}=exports
const {currentPayslip,payslipCurrentMasterFields}=exports
const row=()=>({id:'row',employeeCode:'SYN-1',employeeName:'Synthetic employee',information:[{label:'Designation_Manpower Category',value:'Operator'}],netPay:1200.123,
  earnings:[{label:'Basic',amount:1200.123}],employment:{employeeId:7,identityReviewRequired:false},masterDetails:{employeeId:7,designation:'Supervisor'}})

test('current verified Master designation is displayed while captured source and salary stay unchanged',()=>{
 const item=row(),before=JSON.stringify(item)
 assert.equal(payslipDesignation(item),'Supervisor')
 assert.equal(payslipDisplayInformation(item)[0].label,'Designation_Manpower Category')
 assert.equal(JSON.stringify(item),before)
 for(const change of [{masterDetails:{employeeId:8,designation:'Other employee'}},{employment:{employeeId:null,identityReviewRequired:true}},{masterDetails:{employeeId:7,designation:'Unassigned'}}])assert.equal(payslipDesignation({...item,...change}),'Operator')
 assert.deepEqual(payslipDisplayInformation({...item,information:[]}),[{label:'Designation',value:'Supervisor'}])
})
test('durably linked profiles and current salary project without changing source or freezing a cleared Master email',()=>{
 const item=row();item.email='old@example.test';item.information.push({label:'Mobile No',value:'old'},{label:'Account No',value:'old-bank'},{label:'CTC',value:'12'})
 item.masterDetails={employeeId:7,designation:'Manager',linked:true,employeeCode:'RENAMED',employeeName:'Renamed employee',email:'current@example.test',annualCtc:420000,
  fields:{designation:'Manager',phone:'9000000001',accountno:'new-bank',annualctc:'420000'},salary:{status:'Updated',netPay:18000,gross:20000,totalDeductions:2000,earnings:[{label:'Basic',amount:20000}],deductions:[{label:'PF',amount:2000}],employerContributions:[]}}
 const original=JSON.stringify(item),current=currentPayslip(item)
 assert.equal(current.employeeCode,'RENAMED');assert.equal(current.email,'current@example.test');assert.equal(current.netPay,18000)
 assert.equal(current.information.find(f=>f.label==='CTC').value,'420000');assert.equal(current.information.find(f=>f.label==='Mobile No').value,'9000000001')
 assert.equal(current.information.find(f=>f.label==='Account No').value,'new-bank');assert.equal(JSON.stringify(item),original)
 assert.equal(payslipCurrentMasterFields({...item,information:[]}).find(f=>f.label==='Annual CTC').value,'420000')
 assert.equal(payslipCurrentMasterFields({...item,information:[]}).find(f=>f.label==='Phone').value,'9000000001')
 assert.deepEqual(payslipCurrentMasterFields({...item,employment:{employeeId:99,identityReviewRequired:false}}),[])
 item.masterDetails.email='';assert.equal(currentPayslip(item).email,'')
 const fresh={id:'saved',clientId:9,rows:[structuredClone(item)]},open={...fresh,rows:[{...item,masterDetails:null}]}
 assert.equal(currentPayslip(mergePayslipMasterDetails(open,fresh).rows[0]).netPay,18000)
})
test('Master refresh never copies new financials, another client or changed source identity into an open preview',()=>{
 const current={id:'saved',clientId:9,rows:[row()]},fresh=structuredClone(current)
 fresh.rows[0].masterDetails.designation='Senior Supervisor';fresh.rows[0].netPay=9999
 const result=mergePayslipMasterDetails(current,fresh)
 assert.equal(payslipDesignation(result.rows[0]),'Senior Supervisor');assert.equal(result.rows[0].netPay,1200.123)
 assert.equal(payslipDesignation(current.rows[0]),'Supervisor')
 assert.equal(mergePayslipMasterDetails(current,{...fresh,clientId:10}),current)
 fresh.rows[0].employeeCode='SYN-2';assert.equal(payslipDesignation(mergePayslipMasterDetails(current,fresh).rows[0]),'Supervisor')
})
