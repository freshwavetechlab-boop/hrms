import type { ExcelPayslipRow, PayslipMasterComparison } from '../services/excelPayslipService'
import { normalizePayslipCode } from './excelPayslipCodePreview'

const labelKey = (label: string) => label.normalize('NFKC').toUpperCase().replace(/[^A-Z0-9]/g, '')
const aliases: Record<string, string> = {
  EMPLOYEECODE:'EmployeeCode',EMPLOYEEID:'EmployeeCode',EMPID:'EmployeeCode',EMPLOYEENUMBER:'EmployeeCode',
  EMPLOYEENAME:'EmployeeName',NAME:'EmployeeName',FIRSTNAME:'FirstName',LASTNAME:'LastName',
  EMAIL:'Email',EMAILID:'Email',WORKEMAIL:'Email',MOBILE:'Mobile',MOBILENO:'Mobile',MOBILENUMBER:'Mobile',PHONE:'Mobile',PHONENUMBER:'Mobile',CONTACTNUMBER:'Mobile',
  DEPARTMENT:'Department',DESIGNATION:'Designation',GRADE:'Grade',WORKLOCATION:'WorkLocation',LOCATION:'WorkLocation',PLACEOFPOSTING:'WorkLocation',
  WORKLOCATIONNAME:'WorkLocation',LOCATIONNAME:'WorkLocation',LOCATIONNAMETEHSILSUBTEHSIL:'WorkLocation',
  WORKLOCATIONNAMETEHSILSUBTEHSIL:'WorkLocation',
  STATE:'State',CITY:'City',DISTRICT:'District',ADDRESS:'Address',GENDER:'Gender',DOB:'DateOfBirth',DATEOFBIRTH:'DateOfBirth',DOJ:'DateOfJoining',DATEOFJOINING:'DateOfJoining',
  EMPLOYMENTTYPE:'EmploymentType',EMPLOYEETYPE:'EmploymentType',SKILLCATEGORY:'SkillCategory',EMPLOYEECATEGORY:'SkillCategory',
  PAN:'PAN',PANNO:'PAN',PANNUMBER:'PAN',UAN:'UAN',UANNO:'UAN',UANNUMBER:'UAN',AADHAAR:'Aadhaar',AADHAARNUMBER:'Aadhaar',AADHAR:'Aadhaar',AADHARNUMBER:'Aadhaar',
  AADHAARNO:'Aadhaar',AADHARNO:'Aadhaar',
  ESIC:'ESIC',ESICNO:'ESIC',ESICNUMBER:'ESIC',BANK:'BankName',BANKNAME:'BankName',ACCOUNT:'BankAccountNo',ACCOUNTNO:'BankAccountNo',ACCOUNTNUMBER:'BankAccountNo',BANKACCOUNTNO:'BankAccountNo',BANKACCOUNTNUMBER:'BankAccountNo',BANKACNO:'BankAccountNo',ACNO:'BankAccountNo',
  IFSC:'IFSC',IFSCCODE:'IFSC',PAYMENTMODE:'PaymentMode',ANNUALCTC:'AnnualCTC',YEARLYCTC:'AnnualCTC',
}
const comparable = (key: string, value: string) => {
  if (key === 'AnnualCTC' && /^\d[\d,.]*$/.test(value.trim())) { const amount=Number(value.replace(/,/g,'')); if (Number.isFinite(amount)) return String(amount) }
  return value.normalize('NFKC').trim().replace(/\s+/g, ' ').toUpperCase()
}
export type MasterComparisonLine = { id: string; field: string; oldValue: string | null; newValue: string; status: 'Same' | 'Different' | 'New value' | 'Not supplied' | 'Source only' | 'Not checked' }
export const payslipMasterFieldKeys = (row: ExcelPayslipRow) => [...new Set(['EmployeeCode','EmployeeName','Email',...row.information.map(item=>aliases[labelKey(item.label)]).filter(Boolean)])]
export function payslipMasterComparisonLines(row: ExcelPayslipRow, snapshot: PayslipMasterComparison | null): MasterComparisonLine[] {
  const source = [{ label:'Employee code',value:row.employeeCode,key:'EmployeeCode' }, {label:'Employee name',value:row.employeeName,key:'EmployeeName'}, {label:'Email',value:row.email,key:'Email'}, ...row.information.map(item => ({ ...item, key: aliases[labelKey(item.label)] }))]
  return source.map((item, index) => {
    const oldValue = snapshot?.status === 'Matched' && item.key && Object.hasOwn(snapshot.fields,item.key) ? snapshot.fields[item.key] : null
    const status: MasterComparisonLine['status'] = !snapshot || snapshot.status === 'Ambiguous' ? 'Not checked'
      : !item.key ? 'Source only' : snapshot.status === 'Not found' ? item.value.trim() ? 'New value' : 'Not supplied'
        : oldValue == null ? 'Source only' : !item.value.trim() ? 'Not supplied'
          : !oldValue.trim() ? 'New value' : comparable(item.key,oldValue) === comparable(item.key,item.value) ? 'Same' : 'Different'
    return { id:String(index),field:item.label,oldValue,newValue:item.value,status }
  })
}
export function validPayslipMasterComparison(data: PayslipMasterComparison | null, clientId: number, code: string) {
  return !!data && data.clientId === clientId && typeof data.employeeCode === 'string' && normalizePayslipCode(data.employeeCode) === normalizePayslipCode(code)
    && ['Matched','Not found','Ambiguous'].includes(data.status) && !!data.fields && typeof data.fields === 'object' && !Array.isArray(data.fields) && Object.values(data.fields).every(value => typeof value === 'string')
    && (data.status === 'Matched' ? typeof data.fields.EmployeeCode === 'string' && normalizePayslipCode(data.fields.EmployeeCode) === normalizePayslipCode(code) : Object.keys(data.fields).length === 0)
}
