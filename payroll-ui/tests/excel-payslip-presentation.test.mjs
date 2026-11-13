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

test('profile-only refresh updates UAN/contact/bank while preserving every source financial even if a salary overlay is supplied',()=>{
 const item=row();item.email='old@example.test';item.declaredGross=1500.1234;item.declaredDeductions=300.0004
 item.deductions=[{label:'PF',amount:300.0004}];item.employerContributions=[{label:'Employer PF',amount:200.1234}]
 item.information.push({label:'UAN',value:''},{label:'Contact Number',value:'old'},{label:'ESI Number',value:''},{label:'Account No',value:'old-bank'},{label:'CTC',value:'original monthly base'},{label:'Basic (E)',value:'original base'})
 item.masterDetails={employeeId:7,designation:'Analyst',linked:true,profileOnly:true,employeeCode:'SYN-1',employeeName:'Current name',email:'current@example.test',
  fields:{uan:'001234567890',phone:'9000000001',esic:'0012345678',accountno:'0012345678901',annualctc:'900000',monthlyctc:'75000','source:basice':'99999'},
  salary:{status:'Updated',netPay:99999,gross:99999,totalDeductions:0,earnings:[],deductions:[],employerContributions:[]}}
 const before=JSON.stringify(item),current=currentPayslip(item)
 assert.equal(current.employeeName,'Current name');assert.equal(current.email,'current@example.test')
 for(const [label,value]of [['UAN','001234567890'],['Contact Number','9000000001'],['ESI Number','0012345678'],['Account No','0012345678901'],['CTC','original monthly base'],['Basic (E)','original base']])assert.equal(current.information.find(f=>f.label===label).value,value)
 for(const key of ['earnings','deductions','employerContributions','netPay','declaredGross','declaredDeductions'])assert.deepEqual(current[key],item[key])
 assert.equal(JSON.stringify(item),before)
})

test('legacy profile sync retains supplied email and identity data when Master is blank, plus the salary-period location',()=>{
 const item=row();item.email='source@example.test';item.information.push({label:'UAN',value:'001234567890'},{label:'Email',value:'source@example.test'},{label:'ESIC',value:'0012345678'},{label:'Work Location',value:'Salary-period site'})
 item.masterDetails={employeeId:7,designation:'Analyst',linked:true,profileOnly:true,employeeCode:'',employeeName:'',email:'',fields:{uan:'',email:'',esic:'',worklocation:'Current Master site'}}
 const current=currentPayslip(item)
 assert.equal(current.email,item.email);assert.equal(current.employeeName,item.employeeName);assert.equal(current.employeeCode,item.employeeCode)
 for(const [label,value]of [['UAN','001234567890'],['Email','source@example.test'],['ESIC','0012345678'],['Work Location','Salary-period site']])assert.equal(current.information.find(f=>f.label===label).value,value)
 assert.equal(payslipCurrentMasterFields(item).find(f=>f.label==='Work location').value,'Current Master site')
})
