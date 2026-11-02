const payrollReports=new Set(['salary-register','payslip-register','excel-payslips','payroll-summary','component-ledger','monthly-advice-report','headcount','location-cost','net-pay-estimate','bank-transfer-report'])
const complianceReports=new Set(['pf-register','pf-ecr-report','esi-register','pt-register','statutory-summary','tds-register','form16-register'])
const billingReports=new Set(['client-billing-report','payroll-cost-report'])
export const hasReportWorkspace=(permissions:string[])=>permissions.some(code=>['reports.view','reports.payroll.view','reports.compliance.view','reports.billing.view'].includes(code))
export function canOpenReport(permissions:string[],menu:string,code?:string){
  if(permissions.includes('reports.view'))return true
  if(!code)return false
  return menu==='Payroll Reports'&&permissions.includes('reports.payroll.view')&&payrollReports.has(code)
    ||['Compliance Reports','Tax Reports'].includes(menu)&&permissions.includes('reports.compliance.view')&&complianceReports.has(code)
    ||menu==='Client Billing Report'&&permissions.includes('reports.billing.view')&&billingReports.has(code)
}
export const billingOnlySettings=(permissions:string[])=>permissions.includes('billing.manage')&&!permissions.includes('settings.manage')&&!permissions.includes('client.settings.manage')
