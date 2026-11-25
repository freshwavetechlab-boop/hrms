import type { Component, Structure } from '../types/payroll'
import type { SimulationLine, SimulationMode, SimulationResult } from '../services/payrollCalculationService'

export type SimulationPayslipContext = {
  mode: SimulationMode
  result: SimulationResult | null | undefined
  template: Structure
  components: Component[]
  clientId?: number
  month: string
  employeeId?: number
  employeeCode?: string
  employeeName?: string
  current: boolean
  loading?: boolean
}
export type SimulationPayslipLine = { code: string; name: string; category: string; amount: number }
export type SimulationPayslip = {
  employeeCode: string; employeeName: string; month: string; location: string; templateName: string
  payrollDays: number | null; presentDays: number | null; payableDays: number | null
  earnings: SimulationPayslipLine[]; deductions: SimulationPayslipLine[]; benefits: SimulationPayslipLine[]
  otherLines: SimulationPayslipLine[]; gross: number; totalDeductions: number; employerContributions: number; net: number
}
const displayLine = (line: SimulationLine): SimulationPayslipLine => ({ code: line.code, name: line.name || line.code, category: line.category, amount: line.earned })
const monetaryLine = (line: SimulationLine) => ['Earning', 'Reimbursement', 'Deduction', 'Benefit'].includes(line.category)
const matches = (line: SimulationLine, component: Component) => line.componentId
  ? String(line.componentId) === String(component.id)
  : line.code.toUpperCase() === component.code.toUpperCase()

/** Display native amounts only. This never creates a pay run or recalculates salary. */
export function simulationPayslipPreview(context: SimulationPayslipContext): { model: SimulationPayslip | null; reason: string } {
  const { result, template, components, clientId, employeeId, month } = context
  const blocked = (reason: string) => ({ model: null, reason })
  if (context.mode !== 'Template' && !employeeId) return blocked('Select an employee to calculate and preview a payslip.')
  if (context.loading) return blocked('Wait for the current simulation to finish.')
  if (!context.current) return blocked('Complete the current salary and attendance inputs before previewing.')
  if (!result?.complete || result.issues.some(issue => issue.severity === 'Error')) return blocked('Complete the simulation inputs before previewing a payslip.')
  if (result.mode !== context.mode) return blocked('Recalculate the selected simulation mode before previewing.')
  if (!clientId || result.clientId !== clientId || result.payPeriod !== month) return blocked('Recalculate for the selected client and month.')
  const clients = template.clientId.split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  if (clients.length && !clients.includes(clientId)) return blocked('The template belongs to a different client.')
  if (template.id && !result.templateId.split(';').map(value => value.trim()).includes(String(template.id))) return blocked('Recalculate the selected salary template.')
  if (result.mode !== 'Template' && (!employeeId || result.employeeId !== employeeId)) return blocked('Select the employee whose current calculation should be previewed.')
  if ([result.gross, result.deductions, result.benefits, result.net].some(value => value == null || !Number.isFinite(value))) return blocked('The payroll engine has not returned all payslip totals.')
  const masters = new Map(components.map(component => [String(component.id), component]))
  const configured: SimulationPayslipLine[] = [], used = new Set<number>(), seen = new Set<string>()
  for (const line of template.lines) {
    const id = String(line.componentId), component = masters.get(id)
    if (!component?.active || seen.has(id)) return blocked('Review missing, inactive or duplicate template components first.')
    seen.add(id)
    const index = result.lines.findIndex((native, index) => !used.has(index) && native.source !== 'Approved period adjustment' && matches(native, component))
    if (index < 0) return blocked(`The engine has not returned ${component.name || component.code}. Recalculate before previewing.`)
    const native = result.lines[index]
    if (!Number.isFinite(native.earned)) return blocked(`The engine has not returned a valid amount for ${component.name || component.code}.`)
    if (monetaryLine(native)) { configured.push(displayLine(native)); used.add(index) }
  }
  const otherLines = result.lines.filter((line, index) => !used.has(index) && monetaryLine(line) && line.earned !== 0)
  if (otherLines.some(line => !Number.isFinite(line.earned))) return blocked('A native payroll amount is not available.')
  const input = (name: string) => result.inputs.find(value => value.name === name)?.value ?? null
  return { reason: '', model: {
    employeeCode: result.mode === 'Template' ? context.employeeCode || result.employeeCode || 'EMPLOYEE-001' : result.employeeCode || context.employeeCode || '',
    employeeName: result.mode === 'Template' ? context.employeeName || result.employeeName || result.employee || 'Template scenario' : result.employeeName || context.employeeName || result.employee || 'Template scenario',
    month, templateName: template.name,
    location: result.locations?.length ? [...new Set(result.locations.map(item => item.workLocationName || `Location #${item.workLocationId}`))].join(' / ') : result.workLocationName || (result.mode === 'Template' ? 'Template scenario' : 'Not supplied'),
    payrollDays: input('PAYROLL_DAYS'), presentDays: input('PRESENT_DAYS'), payableDays: input('PAYABLE_DAYS'),
    earnings: configured.filter(line => ['Earning', 'Reimbursement'].includes(line.category)),
    deductions: configured.filter(line => line.category === 'Deduction'), benefits: configured.filter(line => line.category === 'Benefit'),
    otherLines: otherLines.map(displayLine), gross: result.gross!, totalDeductions: result.deductions!, employerContributions: result.benefits!, net: result.net!,
  } }
}
