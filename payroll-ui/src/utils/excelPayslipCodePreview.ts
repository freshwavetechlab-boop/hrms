import type { PayslipColumn, PayslipSheet } from './excelPayslipImport'
import type { PayslipCodeMatch, PayslipCodePreview, PayslipIdentityInput, PayslipIdentityPreview } from '../services/excelPayslipService'
import type { CodePreviewContext } from '../hooks/usePayslipCodePreview'

export const normalizePayslipCode = (code: string) => code.normalize('NFKC').trim().replace(/\s+/g, ' ').toUpperCase()
export function payslipPreviewCodes(sheet: PayslipSheet | undefined, headerRow: number, columns: PayslipColumn[]) {
  const codes = columns.filter(column => column.kind === 'employeeCode'), names = columns.filter(column => column.kind === 'employeeName')
  if (!sheet || codes.length !== 1 || names.length !== 1) return []
  return sheet.rows.filter(row => row.sourceRow > headerRow).flatMap(row => {
    const name = row.cells[names[0].columnIndex]?.value?.trim(), cell = row.cells[codes[0].columnIndex]
    return name && !/^(grand\s*)?total(?:s|\s|:|$)/i.test(name) && cell?.value?.trim() && !cell.error && !cell.missingCachedValue && !cell.calculationError ? [cell.value] : []
  })
}
export function validPayslipCodePreview(data: PayslipCodePreview | null, codes: string[]) {
  const requested = new Set(codes.map(normalizePayslipCode))
  return !!data && data.checkedCodes === requested.size && Array.isArray(data.matches)
    && data.matches.every(match => typeof match.code === 'string' && requested.has(normalizePayslipCode(match.code)) && Number.isInteger(match.records) && match.records > 0 && Number.isInteger(match.activeRecords) && match.activeRecords >= 0 && match.activeRecords <= match.records)
    && new Set(data.matches.map(match => normalizePayslipCode(match.code))).size === data.matches.length
}
export function orderPayslipCodeRows<T>(rows: T[], code: (row: T) => string, matches: Map<string, PayslipCodeMatch>, existingFirst: boolean, excluded: (row: T) => boolean = () => false) {
  if (!existingFirst) return rows
  const rank = (row: T) => excluded(row) ? 0 : matches.has(normalizePayslipCode(code(row))) ? 2 : 1
  return [...rows].sort((a, b) => rank(b) - rank(a))
}

export function payslipPreviewIdentities(sheet:PayslipSheet|undefined,headerRow:number,columns:PayslipColumn[]):PayslipIdentityInput[] {
  if(!sheet||columns.filter(c=>c.kind==='employeeName').length!==1)return []
  const aliases:Record<string,keyof Omit<PayslipIdentityInput,'sourceRow'|'conflictingFields'>>={MOBILE:'mobile',MOBILENO:'mobile',MOBILENUMBER:'mobile',PHONE:'mobile',PHONENO:'mobile',PHONENUMBER:'mobile',CONTACTNO:'mobile',CONTACTNUMBER:'mobile',UAN:'uan',UANNO:'uan',UANNUMBER:'uan',EMAIL:'email',EMAILID:'email',WORKEMAIL:'email',ACCOUNT:'bankAccount',ACCOUNTNO:'bankAccount',ACCOUNTNUMBER:'bankAccount',BANKACCOUNTNO:'bankAccount',BANKACCOUNTNUMBER:'bankAccount',ACNO:'bankAccount',BANKACNO:'bankAccount',LOCATION:'workLocation',WORKLOCATION:'workLocation',WORKLOCATIONNAME:'workLocation',LOCATIONNAME:'workLocation',PLACEOFPOSTING:'workLocation',DEPARTMENT:'department',DEPT:'department'}
  return sheet.rows.filter(row=>row.sourceRow>headerRow).flatMap(row=>{
    const values:PayslipIdentityInput={sourceRow:row.sourceRow,employeeCode:'',employeeName:'',email:'',mobile:'',uan:'',bankAccount:'',workLocation:'',department:''}
    const conflicts=new Set<string>()
    for(const column of columns){
      const cell=row.cells[column.columnIndex];if(!cell||cell.error||cell.missingCachedValue||cell.calculationError)continue
      const key=column.kind==='employeeName'?'employeeName':column.kind==='employeeCode'?'employeeCode':column.kind==='email'?'email':column.kind==='info'?aliases[(column.sourceHeader||column.label).normalize('NFKC').replace(/[^a-z0-9]/gi,'').toUpperCase()]||aliases[column.label.replace(/[^a-z0-9]/gi,'').toUpperCase()]:undefined
      if(key&&cell.value.trim()){
        const text=cell.value.trim(),old=values[key]
        if(typeof old==='string'&&old&&normalizePayslipCode(old)!==normalizePayslipCode(text))conflicts.add(key)
        values[key]=text
      }
    }
    if(conflicts.size)values.conflictingFields=[...conflicts]
    return values.employeeName&&!/^(grand\s*)?total(?:s|\s|:|$)/i.test(values.employeeName)?[values]:[]
  })
}
export function validPayslipIdentityPreview(data:PayslipIdentityPreview|null,clientId:number,rows:PayslipIdentityInput[]) {
  const requested=new Set(rows.map(row=>row.sourceRow))
  return !!data&&data.clientId===clientId&&data.checkedRows===requested.size&&Array.isArray(data.rows)&&data.rows.length===requested.size
    &&new Set(data.rows.map(row=>row.sourceRow)).size===requested.size&&data.rows.every(row=>requested.has(row.sourceRow)&&['Existing','Review','No match'].includes(row.status)&&typeof row.masterCode==='string'&&typeof row.active==='boolean'&&Array.isArray(row.basis)&&row.basis.every(value=>typeof value==='string')&&Array.isArray(row.reasons)&&row.reasons.every(value=>typeof value==='string')&&(row.status!=='Existing'||!!row.masterCode.trim()))
}
export function payslipPreviewRowStatus(preview:CodePreviewContext,sourceRow:number,code:string) {
  return preview.identityMode?preview.identities.get(sourceRow)?.status||'No match':preview.matches.has(normalizePayslipCode(code))?'Existing':'No match'
}
export function orderPayslipPreviewRows<T>(rows:T[],sourceRow:(row:T)=>number,code:(row:T)=>string,preview:CodePreviewContext,excluded:(row:T)=>boolean=()=>false) {
  if(!preview.existingFirst||preview.status!=='Ready')return rows
  const rank=(row:T)=>excluded(row)?0:payslipPreviewRowStatus(preview,sourceRow(row),code(row))==='Existing'?3:payslipPreviewRowStatus(preview,sourceRow(row),code(row))==='Review'?2:1
  return [...rows].sort((a,b)=>rank(b)-rank(a))
}
