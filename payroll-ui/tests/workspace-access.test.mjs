import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import ts from 'typescript'
const exports={}
new Function('exports',ts.transpileModule(readFileSync(new URL('../src/utils/workspaceAccess.ts',import.meta.url),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(exports)
const {canOpenReport,billingOnlySettings,hasReportWorkspace}=exports
test('scoped report menus cannot reveal unrelated HR, attendance, billing or unimplemented pages',()=>{
 const permissions=['reports.payroll.view','reports.compliance.view']
 assert.ok(hasReportWorkspace(permissions));assert.equal(canOpenReport(permissions,'Payroll Reports','excel-payslips'),false);assert.ok(canOpenReport(permissions,'Compliance Reports','pf-register'))
 for(const [menu,code]of [['Employee Reports','employee-master'],['Attendance Reports','daily-attendance'],['Department Reports','headcount'],['Client Billing Report','client-billing-report'],['Executive Dashboards',undefined]])assert.equal(canOpenReport(permissions,menu,code),false)
 assert.equal(canOpenReport(permissions,'Tax Reports',undefined),false)
})
test('Excel Payslips requires unrestricted admin scope and the existing report grant',()=>{
 assert.equal(canOpenReport(['reports.view'],'Payroll Reports','excel-payslips'),false)
 assert.equal(canOpenReport(['reports.payroll.view'],'Payroll Reports','excel-payslips',false),false)
 assert.ok(canOpenReport(['reports.payroll.view'],'Payroll Reports','excel-payslips',true))
 assert.equal(canOpenReport(['settings.manage'],'Payroll Reports','excel-payslips',true),false)
})
test('billing-only settings and legacy full access remain distinct and additive',()=>{
 assert.ok(billingOnlySettings(['billing.manage']));assert.equal(billingOnlySettings(['billing.manage','settings.manage']),false)
 assert.ok(canOpenReport(['reports.billing.view'],'Client Billing Report','payroll-cost-report'))
 assert.ok(canOpenReport(['reports.view'],'Employee Reports','employee-master'));assert.ok(canOpenReport(['reports.view'],'Executive Dashboards'))
 assert.equal(hasReportWorkspace(['billing.manage']),false)
})
