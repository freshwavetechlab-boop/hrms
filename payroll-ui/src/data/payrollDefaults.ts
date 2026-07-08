import type { Client, Component, Drop, Employee, Org, PayslipTemplate, Setup, Structure, TaxDeclarationSection, TaxFinalAdjustment, TaxSlab, TaxSurcharge, WorkLocation } from '../types/payroll'
import workWeekPatterns from './workWeekPatterns.json'

export type WorkWeekPatternConfig = { workingDays: number[]; offSaturdays: number[] }
export type WorkWeekPattern = { value: string; config: WorkWeekPatternConfig }
export const workWeekPatternConfigs = workWeekPatterns as WorkWeekPattern[]
const cleanNumbers = (items: number[], min: number, max: number) => Array.from(new Set(items.map(Number).filter(item => Number.isFinite(item) && item >= min && item <= max))).sort((a, b) => a - b)
export const normalizeWorkWeekPatternConfig = (config: WorkWeekPatternConfig): WorkWeekPatternConfig => ({
  workingDays: cleanNumbers(config.workingDays ?? [], 0, 6),
  offSaturdays: cleanNumbers(config.offSaturdays ?? [], 1, 5)
})
const workWeekConfigKey = (config: WorkWeekPatternConfig) => {
  const normalized = normalizeWorkWeekPatternConfig(config)
  return `${normalized.workingDays.join(',')}|${normalized.offSaturdays.join(',')}`
}
const workWeekPatternByConfig = new Map(workWeekPatternConfigs.map(item => [workWeekConfigKey(item.config), item.value]))
const legacyWorkWeekLabels: Record<string, string> = {
  'sunday + 2nd saturday off': 'Second Saturday + Sunday off',
  'only 2nd saturday off': 'Second Saturday + Sunday off',
  'sunday + 2nd/4th saturday off': 'Second & Fourth Saturday + Sunday off',
  'sun, sat off': 'Saturday-Sunday off',
  'sat, sun off': 'Saturday-Sunday off'
}
export const canonicalWorkWeekValue = (value: string, configJson = '') => {
  if (configJson.trim()) {
    try {
      const parsed = JSON.parse(configJson) as WorkWeekPatternConfig
      return workWeekPatternByConfig.get(workWeekConfigKey(parsed)) ?? value
    } catch {
      // Keep the saved label if the stored JSON is invalid.
    }
  }
  return legacyWorkWeekLabels[value.trim().toLowerCase()] ?? value
}
export const workWeekOptionsFromDrops = (drops: Pick<Drop, 'type' | 'value' | 'configJson' | 'isActive'>[], current = '') => {
  const values = new Map<string, string>()
  drops.filter(item => item.type === 'Work Week' && item.isActive).forEach(item => {
    const configJson = item.configJson ?? ''
    const label = canonicalWorkWeekValue(item.value, configJson)
    const key = configJson.trim()
      ? (() => { try { return workWeekConfigKey(JSON.parse(configJson) as WorkWeekPatternConfig) } catch { return label.toLowerCase() } })()
      : label.toLowerCase()
    if (!values.has(key)) values.set(key, label)
  })
  if (current.trim() && !Array.from(values.values()).some(item => item.toLowerCase() === current.trim().toLowerCase())) values.set(`current:${current.toLowerCase()}`, current)
  return Array.from(values.values()).sort((a, b) => a.localeCompare(b))
}

export const org0: Org = { name: '', legalName: '', businessType: '', businessLocation: 'India', industry: '', hasRunPayrollThisYear: false, setupCompleted: false, logoDataUrl: '', pan: '', gstin: '', tanNumber: '', addressLine1: '', addressLine2: '', registeredOfficeAddress: '', corporateOfficeAddress: '', city: '', state: '', postalCode: '', country: 'India', professionalTaxNumber: '' }
export const defaultTaxSlabs: TaxSlab[] = []
export const defaultTaxSurcharges: TaxSurcharge[] = []
export const defaultTaxFinalAdjustments: TaxFinalAdjustment[] = []
export const defaultTaxSections: TaxDeclarationSection[] = []
export const setup0: Setup = { tax: { pan: '', tan: '', aoCode: '', frequency: '', clientSettings: [], slabs: defaultTaxSlabs, surcharges: defaultTaxSurcharges, finalAdjustments: defaultTaxFinalAdjustments, declarationSections: defaultTaxSections }, schedule: { workWeek: '', salaryDays: '', fixedDays: '', payDay: '', firstPayPeriod: '' }, statutory: { epf: false, epfNumber: '', epfCtc: false, abry: false, epfContribution: '', restrictPf: false, esi: false, esiNumber: '', pt: false, ptNumber: '', ptState: '', ptCycle: '', ptSlabs: '', ptStateSlabs: [], lwf: false, lwfState: '', lwfCycle: '', lwfEligibilityLimit: '', lwfEmployeeContribution: '', lwfEmployerContribution: '' }, salaryComponents: [], salaryStructures: [], payslipTemplates: [] }
export const component0: Component = { id: 0, code: '', componentType: 'Custom Allowance', componentRole: 'Regular Earning', statutoryType: 'None', category: 'Earning', name: '', payType: 'Fixed Pay', calculationType: 'Fixed Amount', value: '', formula: '', baseComponent: '', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '100' }
export const structure0: Structure = { id: 0, clientId: '', name: '', annualCtc: '', lines: [], active: true }
export const payslip0: PayslipTemplate = { id: 0, clientId: '', name: 'Standard Payslip', theme: 'Classic', showLogo: true, showClient: true, showYtd: true, showBank: true, note: 'This is a system generated payslip.', active: true }
export const employee0: Employee = { id: 0, clientId: 0, employeeCode: '', firstName: '', lastName: '', gender: '', dateOfJoining: '', workEmail: '', department: '', designation: '', grade: '', workLocationId: 0, reportingManagerId: 0, portalAccess: false, salaryStructureId: '', annualCtc: 0, salaryComponents: {}, personalDetails: { dateOfBirth: '', mobile: '', panNumber: '', aadhaarNumber: '', uanNumber: '', esicNumber: '', address: '', correspondenceAddress: '', permanentAddress: '', source: '', sourceLocation: '', city: '', district: '', state: '', rawDesignation: '', originalEmployeeCode: '', duplicateResolution: '', excelRow: 0, esicEmployee: 0, ptLwfWorkmenComp: 0, tds: 0, recovery: 0 }, paymentDetails: { bankName: '', bankAccountNo: '', ifscCode: '', paymentMode: '' }, salaryJson: '{}', personalJson: '{}', paymentJson: '{}', isActive: true }
export const client0: Client = { id: 0, name: '', code: '', contactPerson: '', email: '', phone: '', address: '', payScheduleJson: '', isActive: true }
export const location0: WorkLocation = { id: 0, clientId: 0, clientName: '', name: '', address: '', city: '', state: '', postalCode: '', gstin: '', isPrimary: false, isActive: true }
export const drop0: Drop = { id: 0, clientId: 0, type: 'Department', value: '', configJson: '', isActive: true }
export const settingsMenus = ['Organization', 'Clients', 'Work Locations', 'Dropdown Masters', 'Tax Engine', 'Statutory Setup', 'Client Billing Configuration', 'Salary Components', 'Salary Templates', 'Payslip Templates'] as const
export const securityMenus = ['Users', 'Roles', 'Audit'] as const
export const leaveAttendanceMenus = ['Attendance Policies', 'Leave Types', 'Holiday', 'Attendance', 'Geo-Fencing', 'Import Balance'] as const
export const reportingMenus = [
  'Payroll Reports',
  'Compliance Reports',
  'Client Billing Report',
  'Employee Reports',
  'Attendance Reports',
  'Leave Reports',
  // Hidden for now; enable later when these modules/reports are production-ready:
  // 'Recruitment Reports',
  // 'Onboarding Reports',
  // 'Separation Reports',
  'Tax Reports',
  // 'Loan & Advance Reports',
  // 'Cost Center Reports',
  // 'Department Reports',
  // 'Location Reports',
  // 'Contractor Reports',
  // 'Audit Reports',
  // 'MIS Reports',
  // 'Executive Dashboards',
  // 'Scheduled Reports',
  // 'Report Builder'
] as const
export const workflowMenus = ['Workflow Setup', 'API Catalog', 'Department Head Assignments', 'My Tasks', 'Workflow History'] as const
export const workWeekOptions = workWeekPatternConfigs.map(item => item.value)
export const dropTypes = ['Department', 'Designation', 'Work Week', 'Employment Type', 'Employee Grade', 'Cost Center', 'Location Tag', 'State', 'City']
