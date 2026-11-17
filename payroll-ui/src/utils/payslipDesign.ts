import type { PayslipDesignCatalog, PayslipDesignCode, PayslipDesignField, PayslipDesignKind, SavePayslipDesignRequest } from '../services/payslipService'

export const excelPayslipTheme = 'Excel Payslip'
function existingMonthlyInformationCode(token: string, codes: readonly PayslipDesignCode[]): string | null {
  const match = /^COMPONENT_MONTHLY:([A-Za-z][A-Za-z0-9_]{0,79})$/.exec(token)
  const matches = match ? codes.filter(component => component.code === match[1]) : []
  return match && matches.length === 1 && nativePayslipKind(matches[0].category) ? match[1] : null
}
export function payslipDesignInformationLabel(token: string, codes: readonly PayslipDesignCode[]): string {
  const match = /^COMPONENT_MONTHLY:([A-Za-z][A-Za-z0-9_]{0,79})$/.exec(token)
  if (!match) return token.replaceAll('_', ' ')
  const matches = codes.filter(component => component.code === match[1])
  return `${matches.length === 1 ? matches[0].name : match[1]} monthly amount · ${match[1]}`
}
export function payslipDesignInformationOptions(catalog: Pick<PayslipDesignCatalog, 'infoTokens' | 'nativeCodes'>, currentToken = ''): { value: string; label: string }[] {
  const tokens = [...catalog.infoTokens]
  // An already saved monthly selection stays readable after its writer is
  // disabled. Save validation still requires the server's writable catalogue.
  if (!tokens.includes(currentToken) && existingMonthlyInformationCode(currentToken, catalog.nativeCodes)) tokens.push(currentToken)
  return tokens.map(token => ({ value: token, label: payslipDesignInformationLabel(token, catalog.nativeCodes) }))
}
export function payslipDesignScopeCapabilityIssues(catalog: Pick<PayslipDesignCatalog, 'supportsPayrollMonthScope'> | null, payPeriods: readonly string[] | null | undefined): string[] {
  return payPeriods != null && catalog?.supportsPayrollMonthScope !== true
    ? ['Selected payroll months need the updated server. Update it before saving this scope.'] : []
}
export function payslipDesignPeriodIssues(payPeriods: readonly string[] | null | undefined): string[] {
  if (payPeriods == null) return []
  if (!Array.isArray(payPeriods) || !payPeriods.length) return ['Choose at least one payroll month.']
  if (payPeriods.length > 120) return ['Choose no more than 120 payroll months.']
  if (payPeriods.some(month => typeof month !== 'string' || month.length !== 7 || !/^(?!0000)\d{4}-(0[1-9]|1[0-2])$/.test(month))) return ['Use a valid payroll month in YYYY-MM format.']
  if (new Set(payPeriods).size !== payPeriods.length) return ['Choose each payroll month only once.']
  return []
}
// Omitted scope retains the server's saved selection. Widening a scoped design
// requires the user's explicit All choice, never an absent/null default.
export function payslipDesignPeriodRequest(payPeriods: readonly string[] | null | undefined, changed = false): Pick<SavePayslipDesignRequest, 'payPeriods' | 'allPayPeriods'> {
  const issues = payslipDesignPeriodIssues(payPeriods)
  if (issues.length) throw new Error(issues[0])
  if (!changed) return {}
  return payPeriods == null ? { allPayPeriods: true } : { payPeriods: [...payPeriods].sort() }
}
export function payslipDesignPeriodLabel(month: string): string {
  if (payslipDesignPeriodIssues([month]).length) return month
  const names = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
  return names[Number(month.slice(5)) - 1] + ' ' + month.slice(0, 4)
}
export function newestClientExcelPayslip<T extends { id: number; clientId: string | number; theme: string; active: boolean }>(templates: readonly T[], clientId: number): T | undefined {
  return templates.filter(template => template.active && template.theme === excelPayslipTheme && Number(String(template.clientId).split(':')[0]) === clientId && clientId > 0).sort((a, b) => b.id - a.id)[0]
}
export const payslipKindLabel = (kind: PayslipDesignKind) => ({ info: 'Information', earning: 'Earning', deduction: 'Deduction', employer: 'Employer contribution' })[kind]
export const nativePayslipKind = (category: string): PayslipDesignKind | '' => {
  switch (category.trim().toLowerCase()) {
    case 'earning': case 'reimbursement': return 'earning'
    case 'deduction': return 'deduction'
    case 'benefit': case 'employer': case 'employer contribution': case 'employercontribution': return 'employer'
    default: return ''
  }
}
export function payslipDesignIssues(catalog: PayslipDesignCatalog | null, fields: PayslipDesignField[], clientId: number): string[] {
  if (!catalog || catalog.clientId !== clientId || clientId <= 0) return ['Choose a saved source for this client.']
  const issues: string[] = []
  if (!catalog.sourceBatchId || !/^[a-f0-9]{64}$/i.test(catalog.sourceHash)) issues.push('Reload the saved source before reviewing the design.')
  if (!fields.length || fields.length > 100 || fields.length !== catalog.fields.length || new Set(fields.map(field => field.sourceColumn)).size !== fields.length) issues.push('Keep each source field exactly once in the design.')
  const mapped = new Set<string>()
  for (const field of fields) {
    const original = catalog.fields.find(value => value.sourceColumn === field.sourceColumn)
    if (!original || original.label !== field.label || original.kind !== field.kind) { issues.push('Source labels and classifications changed. Reload the saved source.'); continue }
    if (field.sourceColumn >= 1500 && (field.infoToken !== original.infoToken || field.kind !== 'info')) { issues.push(field.label + ': retain the verified Master information field.'); continue }
    if (field.kind === 'info') {
      if (field.nativeCode || (field.visible || field.infoToken) && !catalog.infoTokens.includes(field.infoToken)) issues.push(`${field.label}: select a supported information field or hide it.`)
    } else {
      if (field.infoToken || !catalog.nativeCodes.some(value => value.code === field.nativeCode && nativePayslipKind(value.category) === field.kind)) issues.push(`${field.label}: map the matching saved payroll component.`)
      const key = `${field.kind}:${field.nativeCode}`
      if (field.nativeCode && mapped.has(key)) issues.push(`${field.label}: this payroll component is already mapped.`)
      mapped.add(key)
    }
  }
  return [...new Set(issues)]
}
// A synthetic presentation sample can render before all money/info bindings are
// selected. Provided bindings and immutable source metadata are still checked.
export function payslipDesignPreviewIssues(catalog: PayslipDesignCatalog | null, fields: PayslipDesignField[], clientId: number): string[] {
  if (!catalog || catalog.clientId !== clientId || clientId <= 0) return ['Choose a saved source for this client.']
  const issues: string[] = []
  if (!catalog.sourceBatchId || !/^[a-f0-9]{64}$/i.test(catalog.sourceHash)) issues.push('Reload the saved source before previewing.')
  if (!fields.length || fields.length > 100 || fields.length !== catalog.fields.length || new Set(fields.map(field => field.sourceColumn)).size !== fields.length) issues.push('Keep each source field exactly once in the design.')
  for (const field of fields) {
    const original = catalog.fields.find(value => value.sourceColumn === field.sourceColumn)
    if (!original || original.label !== field.label || original.kind !== field.kind) { issues.push('Source labels changed. Reload the saved source.'); continue }
    if (field.sourceColumn >= 1500 && (field.infoToken !== original.infoToken || field.kind !== 'info')) issues.push('Retain the verified Master information field.')
    if (field.kind === 'info') {
      if (field.nativeCode || field.infoToken && !catalog.infoTokens.includes(field.infoToken) && !existingMonthlyInformationCode(field.infoToken, catalog.nativeCodes)) issues.push('Choose a supported information field.')
    } else if (field.infoToken || field.nativeCode && !catalog.nativeCodes.some(code => code.code === field.nativeCode && nativePayslipKind(code.category) === field.kind)) issues.push('Choose a supported payroll component.')
  }
  return [...new Set(issues)]
}
export function movePayslipDesignField(fields: PayslipDesignField[], sourceColumn: number, direction: -1 | 1): PayslipDesignField[] {
  const index = fields.findIndex(field => field.sourceColumn === sourceColumn), next = index + direction
  if (index < 0 || next < 0 || next >= fields.length) return fields
  const result = [...fields]; [result[index], result[next]] = [result[next], result[index]]; return result
}

// A preview owns one request scope and one Blob URL. A late response cannot
// allocate or expose a PDF after the employee, client or run has changed.
export class PayslipPreviewLease {
  private sequence = 0
  private scope = ''
  private url: string | null = null
  private create: (blob: Blob) => string
  private revoke: (url: string) => void
  constructor(create = (blob: Blob) => URL.createObjectURL(blob), revoke = (url: string) => URL.revokeObjectURL(url)) { this.create = create; this.revoke = revoke }
  begin(scope: string) { this.clear(); this.scope = scope; return this.sequence }
  current(scope: string, sequence: number) { return this.scope === scope && this.sequence === sequence }
  accept(scope: string, sequence: number, blob: Blob) {
    if (!this.current(scope, sequence)) return null
    if (this.url) this.revoke(this.url)
    this.url = this.create(blob); return this.url
  }
  clear() { this.sequence += 1; this.scope = ''; if (this.url) this.revoke(this.url); this.url = null }
}
