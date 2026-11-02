import type { ExcelPayslipBatch, ExcelPayslipRow } from '../services/excelPayslipService'

const designationKey = (label: string) => label.trim().replace(/\s*\([a-z]{1,3}\)\s*$/i, '').replace(/[^a-z0-9]/gi, '').toLowerCase()
const isDesignation = (label: string) => ['designation', 'designationmanpowercategory', 'manpowercategory'].includes(designationKey(label))
const aliases:Record<string,string>={designationmanpowercategory:'designation',manpowercategory:'designation',mobileno:'phone',mobilenumber:'phone',mobile:'phone',phoneno:'phone',phonenumber:'phone',contactno:'phone',emailid:'email',workemail:'email',emailaddress:'email',uanno:'uan',uannumber:'uan',panno:'pan',pannumber:'pan',aadhar:'aadhaar',aadhaarno:'aadhaar',aadhaarnumber:'aadhaar',esicno:'esic',esicnumber:'esic',bank:'bankname',accountnumber:'accountno',bankaccountno:'accountno',bankaccountnumber:'accountno',acno:'accountno',ifsccode:'ifsc',location:'worklocation',locationnametehsilsubtehsil:'worklocation',placeofposting:'worklocation',posting:'worklocation',doj:'joiningdate',dateofjoining:'joiningdate',dob:'birthdate',dateofbirth:'birthdate',ctc:'annualctc',annualsalary:'annualctc',employeeno:'employeecode',idno:'employeecode',name:'employeename',conductorname:'employeename'}
const canonical=(label:string)=>aliases[designationKey(label)]||designationKey(label)
export const payslipMasterLinked=(row:ExcelPayslipRow)=>!!row.masterDetails?.linked && row.employment?.employeeId===row.masterDetails.employeeId&&!row.employment.identityReviewRequired
export function payslipDisplayInformation(row: ExcelPayslipRow) {
  const detail = row.masterDetails
  if(payslipMasterLinked(row)&&detail){
    const fields={...detail.fields,employeecode:detail.employeeCode||'',employeename:detail.employeeName||''} as Record<string,string>
    const information=row.information.map(field=>({...field,value:fields[`source:${field.label.replace(/[^a-z0-9]/gi,'').toLowerCase()}`]??fields[canonical(field.label)]??field.value}))
    for(const [label,key] of [['Designation','designation'],['Department','department'],['Grade','grade'],['Work Location','worklocation'],['Email','email'],['Phone','phone'],['Annual CTC','annualctc']])
      if(!information.some(field=>canonical(field.label)===key)&&fields[key])information.push({label,value:fields[key]})
    return information
  }
  const designation = detail && row.employment?.employeeId === detail.employeeId && !row.employment.identityReviewRequired ? detail.designation.trim() : ''
  if (!designation || designation.toLowerCase() === 'unassigned') return row.information
  const information = row.information.map(field => ({ ...field, value: isDesignation(field.label) ? designation : field.value }))
  if (!information.some(field => isDesignation(field.label))) information.push({label:'Designation',value:designation})
  return information
}
export const payslipDesignation = (row: ExcelPayslipRow) => payslipDisplayInformation(row).find(field => isDesignation(field.label))?.value || ''
export function payslipCurrentMasterFields(row:ExcelPayslipRow){
  if(!payslipMasterLinked(row))return []
  const fields=row.masterDetails?.fields||{}
  return [['designation','Designation'],['department','Department'],['grade','Grade'],['worklocation','Work location'],['email','Email'],['phone','Phone'],['annualctc','Annual CTC'],['monthlyctc','Monthly CTC'],
    ['uan','UAN'],['pan','PAN'],['aadhaar','Aadhaar'],['esic','ESIC'],['bankname','Bank name'],['accountno','Account number'],['ifsc','IFSC'],['paymentmode','Payment mode'],['gender','Gender'],
    ['joiningdate','Joining date'],['birthdate','Date of birth'],['employmenttype','Employment type'],['skillcategory','Skill category'],['address','Address'],['correspondenceaddress','Correspondence address'],['permanentaddress','Permanent address'],
    ['city','City'],['district','District'],['state','State'],['employmentstatus','Employment status'],['lastworkingdate','Last working date']]
    .filter(([key])=>Object.hasOwn(fields,key)).map(([key,label])=>({label,value:fields[key]||'Not set'}))
}
export function currentPayslip(row:ExcelPayslipRow):ExcelPayslipRow {
  if(!payslipMasterLinked(row))return row
  const d=row.masterDetails!,salary=d.salary?.status==='Updated'&&d.salary.netPay!=null?d.salary:null
  return {...row,employeeCode:d.employeeCode??row.employeeCode,employeeName:d.employeeName??row.employeeName,email:d.email??row.email,information:payslipDisplayInformation(row),
    earnings:salary?.earnings??row.earnings,deductions:salary?.deductions??row.deductions,employerContributions:salary?.employerContributions??row.employerContributions,
    netPay:salary?.netPay??row.netPay,declaredGross:salary?.gross??row.declaredGross,declaredDeductions:salary?.totalDeductions??row.declaredDeductions}
}

export function mergePayslipMasterDetails(current: ExcelPayslipBatch, fresh: ExcelPayslipBatch): ExcelPayslipBatch {
  if (current.id !== fresh.id || current.clientId !== fresh.clientId) return current
  const rows = new Map(fresh.rows.map(row => [row.id, row]))
  return { ...current, rows: current.rows.map(row => {
    const latest = rows.get(row.id)
    return latest && latest.employeeCode === row.employeeCode && latest.employeeName === row.employeeName
      ? {...row, employment:latest.employment, masterDetails:latest.masterDetails} : row
  }) }
}
