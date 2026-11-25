import type { PayslipDesignCode, PayslipDesignField, PayslipDesignTemplate } from '../services/payslipService'
import type { SimulationPayslip, SimulationPayslipContext, SimulationPayslipLine } from './salarySimulationPayslip'
import { nativePayslipKind } from './payslipDesign'

export type SimulationPayslipDesign = {
  template: PayslipDesignTemplate
  fields: PayslipDesignField[]
  amountDecimalPlaces: 0 | 2
  simpleLayout: boolean
  keepMappedZeroRows: boolean
  nativeCodes?: PayslipDesignCode[]
  organizationName?: string
  logoDataUrl?: string
}
export type ConfiguredSimulationPayslip = {
  information: { label: string; value: string }[]
  earnings: SimulationPayslipLine[]
  deductions: SimulationPayslipLine[]
  benefits: SimulationPayslipLine[]
  showYtd: boolean
}

/** Presentation projection only; all money comes from the guarded native result. */
export function configuredSimulationPayslip(context: SimulationPayslipContext, baseline: SimulationPayslip, design: SimulationPayslipDesign): { model: ConfiguredSimulationPayslip | null; reason: string } {
  const blocked = (reason: string) => ({ model: null, reason })
  const result = context.result
  if (!result?.complete) return blocked('Complete the native salary simulation before previewing.')
  if (design.template.clientId !== context.clientId || design.template.theme !== 'Excel Payslip') return blocked('Select a payslip design for this simulation client.')
  if (![0, 2].includes(design.amountDecimalPlaces) || !design.fields.length || design.fields.length > 100 || new Set(design.fields.map(field => field.sourceColumn)).size !== design.fields.length) return blocked('Review the configured payslip fields before previewing.')
  const mapped = new Set<string>()
  for (const field of design.fields) {
    if (!field.label.trim() || !['info', 'earning', 'deduction', 'employer'].includes(field.kind)) return blocked('Review the payslip field labels and classifications.')
    if (field.kind === 'info') { if (field.nativeCode) return blocked('Review the payslip information mappings.'); continue }
    const key = `${field.kind}:${field.nativeCode}`
    const nativeOutput = result.lines.some(line => line.code === field.nativeCode && nativePayslipKind(line.category) === field.kind)
    const catalogCode = design.nativeCodes?.some(code => code.code === field.nativeCode && nativePayslipKind(code.category) === field.kind)
    if (field.infoToken || !field.nativeCode || mapped.has(key) || !nativeOutput && !catalogCode && !context.components.some(component => component.active && component.code === field.nativeCode && nativePayslipKind(component.category) === field.kind)) return blocked(`Review the configured payslip mapping for ${field.label}.`)
    mapped.add(key)
  }
  const financial = result.lines.filter(line => nativePayslipKind(line.category))
  if (financial.some(line => !Number.isFinite(line.earned) || !Number.isFinite(line.monthly))) return blocked('A native payroll amount is unavailable.')
  const missing = financial.filter(line => line.earned !== 0 && !mapped.has(`${nativePayslipKind(line.category)}:${line.code}`))
  if (missing.length) return blocked(`The payslip design does not map these native payroll components: ${[...new Set(missing.map(line => line.code))].join(', ')}.`)
  const input = (name: string) => {
    const value = result.inputs.find(item => item.name === name)?.value
    return value != null && Number.isFinite(value) ? String(value) : 'Not supplied'
  }
  const info = (token: string) => {
    if (token.startsWith('COMPONENT_MONTHLY:')) {
      const code = token.slice('COMPONENT_MONTHLY:'.length)
      if (!context.components.some(component => component.active && component.code === code)) return 'Not supplied'
      const matches = financial.filter(line => line.code === code)
      const bases = matches.filter(line => line.monthly !== 0)
      return !matches.length || bases.length > 1 || new Set(matches.map(line => nativePayslipKind(line.category))).size !== 1 ? 'Not supplied' : String(bases[0]?.monthly ?? 0)
    }
    switch (token) {
      case 'EMPLOYEE_CODE': return baseline.employeeCode || 'Not supplied'
      case 'EMPLOYEE_NAME': return baseline.employeeName || 'Not supplied'
      case 'WORK_LOCATION': return result.workLocationName || (result.locations?.length ? baseline.location : 'Not supplied')
      case 'PAYROLL_DAYS': return input('PAYROLL_DAYS')
      case 'PRESENT_DAYS': return input('PRESENT_DAYS')
      case 'PAYABLE_DAYS': return input('PAYABLE_DAYS')
      case 'SAVED_MONTHLY_GROSS': return input('SAVED_MONTHLY_GROSS')
      case 'CURRENT_ANNUAL_CTC': return input('ANNUAL_CTC')
      default: return 'Not supplied'
    }
  }
  const information = design.fields.filter(field => field.visible && field.kind === 'info' && (design.template.showBank || !['BANK_ACCOUNT', 'BANK_NAME', 'IFSC'].includes(field.infoToken))).map(field => ({ label: field.label, value: info(field.infoToken) }))
  const lines = design.fields.filter(field => field.visible && field.kind !== 'info').map(field => ({
    code: field.nativeCode, name: field.label, category: field.kind,
    amount: financial.filter(line => line.code === field.nativeCode && nativePayslipKind(line.category) === field.kind).reduce((total, line) => total + line.earned, 0),
  })).filter(line => design.keepMappedZeroRows || line.amount !== 0)
  return { reason: '', model: { information, earnings: lines.filter(line => line.category === 'earning'), deductions: lines.filter(line => line.category === 'deduction'), benefits: lines.filter(line => line.category === 'employer'), showYtd: design.template.showYtd } }
}

export const configuredSimulationAmount = (value: number, decimals: 0 | 2) => `Rs ${value.toLocaleString('en-IN', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })}`
