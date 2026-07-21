import { useEffect, useState } from 'react'
import type { Dispatch, FormEvent, ReactNode, SetStateAction } from 'react'
import { createPortal } from 'react-dom'
import { DownloadOutlined, PlusOutlined, UploadOutlined } from '@ant-design/icons'
import { Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, Modal, Row, Select as AntSelect, Space } from 'antd'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewState } from '../components/BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from '../components/BulkUploadProgressModal'
import DataTable from '../components/DataTable'
import FileDropZone from '../components/FileDropZone'
import { Card, Chk, F, Sel } from '../components/FormPrimitives'
import PageTabs from '../components/PageTabs'
import SalaryTemplateDesigner from '../components/SalaryTemplateDesigner'
import TaxEngineManager from '../components/TaxEngineManager'
import NotificationSettings from '../components/NotificationSettings'
import RecruitmentAdminSettings, { type RecruitmentAdminSection } from '../components/RecruitmentAdminSettings'
import ScheduledJobsManager from '../components/ScheduledJobsManager'
import TravelExpensePolicySettings from '../components/TravelExpensePolicySettings'
import AttachmentSettings from '../components/AttachmentSettings'
import { useToast, type ToastType } from '../components/ToastProvider'
import { client0, component0, drop0, dropTypes, location0, org0, payslip0, settingsMenus, setup0, structure0, workWeekOptions, workWeekPatternConfigs } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { getAttendanceGroups } from '../services/leaveAttendanceService'
import { getClientBillingAdvanced, getClientBillingConfigurations, getClientBillingImportJob, getClientBillingModule, getClientImportJob, getDropdownImportJob, getDropdowns, getEssClientSettings, getOrganization, getSalaryComponentImportJob, getSalaryTemplateImportJob, getSetup, getWorkLocationImportJob, getWorkLocations, saveClient as persistClient, saveClientBillingAdvancedLine, saveClientBillingConfiguration, saveClientBillingModule, saveDropdown, saveEssClientSetting, saveOrganization, saveSetup, saveWorkLocation, startClientBillingImport, startClientImport, startDropdownImport, startSalaryComponentImport, startSalaryTemplateImport, startWorkLocationImport, type BulkImportStatus } from '../services/settingsService'
import type { AttendanceGroup, Client, ClientBillingConfiguration, ClientBillingCostRuleHeader, ClientBillingCostRuleLine, ClientBillingRateCardType, ClientBillingRateType, Component, Drop, Employee, EssClientSetting, Org, ProfessionalTaxSlab, Setup, Structure, WorkLocation } from '../types/payroll'
import { money } from '../utils/salary'
import { parseImportPreviewFile, parseImportPreviewSheets, validateImportPreview, type ImportPreviewData, type ImportPreviewIssue, type ImportPreviewRules } from '../utils/importPreview'
import { downloadXlsx } from '../utils/xlsx'
import { previewToXlsxFile } from '../utils/previewFile'

type SettingsTab = (typeof settingsMenus)[number]
type OrganizationTab = 'Organization' | 'Tax' | 'EPF' | 'ESI' | 'Professional Tax' | 'Labour Welfare Fund'
const organizationTabs = ['Organization', 'Tax', 'EPF', 'ESI', 'Professional Tax', 'Labour Welfare Fund'] as const
const statutoryTabs = ['Income Tax Rules', 'Professional Tax'] as const
type StatutoryTab = (typeof statutoryTabs)[number]
const componentTabs = ['Earning', 'Deduction', 'Reimbursement', 'Benefit', 'Correction'] as const
type ComponentCategory = (typeof componentTabs)[number]
const componentRoleOptions = ['Regular Earning', 'Regular Deduction', 'Statutory Deduction', 'Employer Contribution', 'Reimbursement', 'Variable Pay', 'Arrear / Correction', 'Recovery'] as const
const statutoryTypeOptions = ['None', 'TDS', 'Professional Tax', 'PF Employee', 'PF Employer', 'VPF', 'EPS', 'ESI Employee', 'ESI Employer', 'LWF Employee', 'LWF Employer', 'NPS Employee', 'NPS Employer', 'Workmen Compensation'] as const
const defaultComponentRole = (category: string) => category === 'Deduction' ? 'Regular Deduction' : category === 'Reimbursement' ? 'Reimbursement' : category === 'Benefit' ? 'Employer Contribution' : category === 'Correction' ? 'Arrear / Correction' : 'Regular Earning'
const componentRolesForCategory = (category: string) => category === 'Deduction'
  ? ['Regular Deduction', 'Statutory Deduction', 'Recovery', 'Arrear / Correction']
  : category === 'Reimbursement'
    ? ['Reimbursement']
    : category === 'Benefit'
      ? ['Employer Contribution']
      : category === 'Correction'
        ? ['Arrear / Correction']
        : ['Regular Earning', 'Variable Pay']
const statutoryTypesForRole = (role: string) => role === 'Statutory Deduction'
  ? ['TDS', 'Professional Tax', 'PF Employee', 'VPF', 'ESI Employee', 'LWF Employee', 'NPS Employee']
  : role === 'Employer Contribution'
    ? ['PF Employer', 'EPS', 'ESI Employer', 'LWF Employer', 'NPS Employer', 'Workmen Compensation']
    : ['None']
const ptSlab0: ProfessionalTaxSlab = { id: 0, state: '', salaryFrom: '0', salaryTo: '', deductionAmount: '', effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: '', gender: 'All', notes: '', active: true }
const calculationOptions = ['Fixed Amount', 'Formula', 'Residual / Balancing', 'Manual / Variable', 'Slab Based']
const initialPasswordModeOptions = ['App Default', 'Random', 'Aadhaar', 'EmployeeCode', 'Fixed']
const formulaChips = ['GROSS', 'CTC', 'MONTHLY_CTC', 'PAYROLL_DAYS', 'PAYABLE_DAYS', 'MIN()', 'MAX()', 'ROUND()', 'ROUNDDOWN()', 'ROUNDUP()']
const formulaReservedWords = new Set(['GROSS', 'CTC', 'MONTHLY_CTC', 'ANNUAL_CTC', 'PAYROLL_DAYS', 'TOTAL_DAYS', 'WORKING_DAYS', 'PAYABLE_DAYS', 'PRESENT_DAYS', 'LOP_DAYS', 'GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST', 'MIN', 'MAX', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'SUM', 'FIXED', 'EARNINGS', 'EARNINGS_BEFORE_THIS', 'OF'])
const settingsSetup0: Setup = setup0
const billingRateCardTypes: ClientBillingRateCardType[] = ['All', 'Service Charge', 'Reimbursement', 'Bonus', 'Statutory Compliance Charges']
const billingRateTypes: ClientBillingRateType[] = ['Percentage', 'Fixed']
const billing0: ClientBillingConfiguration = { id: 0, clientId: 0, clientName: '', workLocationId: null, workLocationName: '', rateCardType: 'All', rateType: 'Percentage', value: 0, taxInclusive: false, gstRatePercent: 18, effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: null, isActive: true }
const billingAdvancedLine0: ClientBillingCostRuleLine = { id: 0, headerId: 0, ruleName: '', lineType: 'Component Category', matchValue: 'Earning', billingTreatment: 'Bill Actual', baseType: 'Processed Amount', rateType: 'Actual', rateValue: 0, taxApplicable: true, commissionApplicable: true, displayGroup: 'Salary', sortOrder: 100, isActive: true }
const billingImportHeaders = ['Client Id', 'Work Location Id', 'Rate Card Type', 'Rate Type', 'Value', 'Tax Basis', 'GST Rate %', 'Effective From', 'Effective To', 'Active']
const clientImportHeaders = ['Client Name', 'Code', 'Contact Person', 'Email', 'Phone', 'Address', 'Active']
const workLocationImportHeaders = ['Client Id', 'Location Name', 'Address', 'State', 'City', 'PIN', 'GST Number', 'Primary', 'Active']
const dropdownImportHeaders = ['Master Type', 'Value', 'Client Id', 'State', 'Active', 'Config Json']
const salaryComponentImportHeaders = ['Code', 'Category', 'Name', 'Component Type', 'Component Role', 'Statutory Type', 'Pay Type', 'Calculation Type', 'Value', 'Formula', 'Base Component', 'Taxable', 'Part Of CTC', 'Pro Rata', 'FBP', 'Restrict FBP', 'EPF', 'ESI', 'Recurring', 'Scheduled', 'Investment Type', 'Correction Of', 'Priority', 'Active']
const salaryTemplateImportHeaders = ['Client Ids', 'Template Name', 'Annual CTC', 'Active', 'Component Code', 'Value']
const clientPreviewRules: ImportPreviewRules = {
  required: ['Client Name'],
  unique: [['Client Name'], ['Code']],
  booleans: ['Active'],
  custom: (row, rowNumber) => row.Email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(row.Email) ? [{ rowNumber, column: 'Email', message: 'Email must be valid.' }] : []
}
const workLocationPreviewRules: ImportPreviewRules = { required: ['Client Id', 'Location Name'], unique: [['Client Id', 'Location Name']], booleans: ['Primary', 'Active'] }
const dropdownPreviewRules: ImportPreviewRules = { required: ['Master Type', 'Value'], unique: [['Master Type', 'Client Id', 'State', 'Value']], booleans: ['Active'], enums: { 'Master Type': [...dropTypes] } }
const salaryComponentPreviewRules: ImportPreviewRules = { required: ['Code', 'Category', 'Name', 'Calculation Type'], unique: [['Code']], booleans: ['Taxable', 'Part Of CTC', 'Pro Rata', 'FBP', 'Restrict FBP', 'ESI', 'Recurring', 'Scheduled', 'Active'], numbers: ['Priority'], enums: { Category: [...componentTabs], 'Component Role': [...componentRoleOptions], 'Statutory Type': [...statutoryTypeOptions], 'Calculation Type': [...calculationOptions], 'Pay Type': ['Fixed Pay', 'Variable Pay'], EPF: ['Never', 'Always', 'Only if employee is PF eligible'] } }
const salaryTemplatePreviewRules: ImportPreviewRules = { required: ['Client Ids', 'Template Name', 'Component Code'], unique: [['Client Ids', 'Template Name', 'Component Code']], booleans: ['Active'], numbers: ['Annual CTC'] }
const billingPreviewRules: ImportPreviewRules = { required: ['Client Id', 'Rate Card Type', 'Rate Type', 'Value', 'Effective From'], unique: [['Client Id', 'Work Location Id', 'Rate Card Type', 'Rate Type', 'Effective From']], booleans: ['Active'], numbers: ['Value', 'GST Rate %'], dates: ['Effective From', 'Effective To'], enums: { 'Rate Card Type': [...billingRateCardTypes], 'Rate Type': [...billingRateTypes], 'Tax Basis': ['Excluding', 'Inclusive'] } }
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))
const dropdownSheetHeaders = (type: string) => type === 'Employee Grade' ? ['Client Id', 'Value', 'Active'] : type === 'City' ? ['State', 'Value', 'Active'] : type === 'Work Week' ? ['Value', 'Active', 'Working Days', 'Off Saturdays'] : ['Value', 'Active']
const dropdownSheetExample = (type: string, clientId = '1') => type === 'Employee Grade' ? [clientId, 'G1', 'TRUE'] : type === 'City' ? ['Delhi', 'New Delhi', 'TRUE'] : type === 'Work Week' ? ['Monday - Saturday with 1st-4th Saturdays off', 'TRUE', 'Mon, Tue, Wed, Thu, Fri, Sat', '1st, 2nd, 3rd, 4th'] : [type === 'State' ? 'Delhi' : type === 'Business Unit' ? 'Corporate' : type === 'Department' ? 'Finance' : type === 'Designation' ? 'Manager' : type === 'Employment Type' ? 'Full Time' : type === 'Cost Center' ? 'CC-001' : 'Head Office', 'TRUE']
const dropdownWorkWeekReferenceRows = [
  ['', '', '', ''],
  ['Work Week Examples', '', '', ''],
  ['Pattern', 'Working Days', 'Off Saturdays', 'Result'],
  ['Monday - Friday', 'Mon, Tue, Wed, Thu, Fri', '', 'Saturday and Sunday off'],
  ['Monday - Saturday', 'Mon, Tue, Wed, Thu, Fri, Sat', '', 'Only Sunday off'],
  ['Second Saturday + Sunday off', 'Mon, Tue, Wed, Thu, Fri, Sat', '2nd', 'Sunday and 2nd Saturday off'],
  ['Second & Fourth Saturday + Sunday off', 'Mon, Tue, Wed, Thu, Fri, Sat', '2nd, 4th', 'Sunday, 2nd Saturday, and 4th Saturday off'],
  ['Friday off', 'Sun, Mon, Tue, Wed, Thu, Sat', '', 'Only Friday off'],
  ['Friday-Saturday off', 'Sun, Mon, Tue, Wed, Thu', '', 'Friday and Saturday off']
]
type SettingsBulkUpload = { open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }
type ImportStart = (file: File) => Promise<{ ok: boolean; data: BulkImportStatus; error: string; status: number }>
type ImportStatus = (jobId: string) => Promise<BulkImportStatus>
const normalizeCalculationType = (value: string) =>
  value === 'Percentage of CTC' || value === 'Percentage of Component' || value === 'Formula' ? 'Formula' :
  value === 'Balancing Amount' || value === 'Residual / Balancing' ? 'Residual / Balancing' :
  value === 'Manual Entry' || value === 'Manual Override' || value === 'Manual / Variable' ? 'Manual / Variable' :
  value === 'Slab Based' ? 'Slab Based' : 'Fixed Amount'
const normalizeComponentForUi = (row: Component): Component => {
  const calculationType = normalizeCalculationType(row.calculationType)
  const currentFormula = row.formula || ''
  const formula = calculationType === 'Formula' && !currentFormula.trim()
    ? row.calculationType === 'Percentage of CTC' ? `CTC * ${row.value || 0}%`
      : row.calculationType === 'Percentage of Component' ? `${row.baseComponent || 'BASIC'} * ${row.value || 0}%`
        : currentFormula
    : currentFormula
  return { ...row, calculationType, formula, componentRole: row.componentRole || defaultComponentRole(row.category), statutoryType: row.statutoryType || 'None', payType: calculationType === 'Manual / Variable' ? 'Variable Pay' : row.payType }
}
const prepareComponentForSave = (row: Component): Component => {
  const category = row.category
  const roleOptions = componentRolesForCategory(category)
  const role = roleOptions.includes(row.componentRole) ? row.componentRole : defaultComponentRole(category)
  const isEarning = category === 'Earning'
  const isBenefit = category === 'Benefit'
  const isReimbursement = category === 'Reimbursement'
  const isCorrection = category === 'Correction'
  const isStatutory = role === 'Statutory Deduction' || role === 'Employer Contribution'
  const statutoryOptions = statutoryTypesForRole(role)
  const statutoryType = isStatutory && statutoryOptions.includes(row.statutoryType) ? row.statutoryType : 'None'
  return {
    ...row,
    componentRole: role,
    statutoryType,
    taxable: isEarning || isBenefit || isReimbursement ? row.taxable : false,
    ctc: isEarning || isBenefit || role === 'Employer Contribution' ? row.ctc : false,
    fbp: isEarning || isBenefit || isReimbursement ? row.fbp : false,
    restrictFbp: (isEarning || isBenefit || isReimbursement) && row.fbp ? row.restrictFbp : false,
    epf: isEarning ? row.epf : 'Never',
    esi: isEarning ? row.esi : false,
    scheduled: isEarning || role === 'Variable Pay' ? row.scheduled : false,
    investmentType: isBenefit || isStatutory || statutoryType.includes('NPS') || statutoryType === 'TDS' ? row.investmentType : '',
    correctionOf: isCorrection ? row.correctionOf : ''
  }
}
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const plural = (count: number, label: string) => `${count} ${label}${count === 1 ? '' : 's'}`
const countLink = (count: number, label: string) => count ? plural(count, label) : ''
const namedLink = (label: string, names: string[]) => {
  const clean = unique(names)
  if (!clean.length) return ''
  return `${label}: ${clean.slice(0, 3).join(', ')}${clean.length > 3 ? ` +${clean.length - 3} more` : ''}`
}
const cityType = (state: string) => `City:${state}`
const isCityType = (type: string) => type.startsWith('City:')
const cityState = (type: string) => isCityType(type) ? type.slice(5) : ''
const refId = (value: string | number | null | undefined) => String(value ?? '').split(':')[0]
const refIds = (value: string | number | null | undefined) => unique(String(value ?? '').split(/[;,|]/).map(item => refId(item)).filter(Boolean))
const importNorm = (value: string) => value.replace(/[\s_-]/g, '').toLowerCase()
const dropdownTypeFromSheet = (name: string) => dropTypes.find(type => importNorm(type) === importNorm(name) || importNorm(`${type}s`) === importNorm(name)) ?? ''
const previewCell = (headers: string[], row: string[], name: string) => {
  const index = headers.findIndex(header => importNorm(header) === importNorm(name))
  return index >= 0 && index < row.length ? row[index].trim() : ''
}
const parseWorkWeekNumberList = (text: string, aliases: Record<string, number>) => unique(text.split(/[,;|/]+/).map(item => item.trim()).filter(Boolean).map(item => {
  const key = importNorm(item.replace(/\d+(st|nd|rd|th)/i, match => match.replace(/\D/g, '')))
  return String(aliases[key] ?? aliases[importNorm(item)] ?? '')
})).map(Number).filter(Number.isFinite)
const buildWorkWeekConfigJson = (workingDaysText: string, offSaturdaysText: string) => {
  const dayAliases: Record<string, number> = { sun: 0, sunday: 0, '0': 0, mon: 1, monday: 1, '1': 1, tue: 2, tuesday: 2, '2': 2, wed: 3, wednesday: 3, '3': 3, thu: 4, thursday: 4, '4': 4, fri: 5, friday: 5, '5': 5, sat: 6, saturday: 6, '6': 6 }
  const satAliases: Record<string, number> = { first: 1, '1st': 1, '1': 1, second: 2, '2nd': 2, '2': 2, third: 3, '3rd': 3, '3': 3, fourth: 4, '4th': 4, '4': 4, fifth: 5, '5th': 5, '5': 5 }
  const workingDays = parseWorkWeekNumberList(workingDaysText, dayAliases).filter(day => day >= 0 && day <= 6)
  const offSaturdays = parseWorkWeekNumberList(offSaturdaysText, satAliases).filter(item => item >= 1 && item <= 5)
  return workingDays.length ? JSON.stringify({ workingDays, offSaturdays }) : ''
}
const validateWorkWeekJson = (text: string) => {
  if (!text.trim()) return 'Work Week requires Working Days or Config Json.'
  try {
    const json = JSON.parse(text) as { workingDays?: unknown; offSaturdays?: unknown }
    const validNumbers = (items: unknown[], min: number, max: number) => items.every(item => Number.isInteger(Number(item)) && Number(item) >= min && Number(item) <= max)
    if (!Array.isArray(json.workingDays) || !json.workingDays.length || !validNumbers(json.workingDays, 0, 6)) return 'Config Json workingDays must be an array with values 0-6.'
    if (!Array.isArray(json.offSaturdays) || !validNumbers(json.offSaturdays, 1, 5)) return 'Config Json offSaturdays must be an array with values 1-5.'
    return ''
  } catch {
    return 'Config Json must be valid JSON.'
  }
}
type WorkWeekConfig = { workingDays: number[]; offSaturdays: number[] }
const weekDayOptions = [{ value: 0, label: 'Sun' }, { value: 1, label: 'Mon' }, { value: 2, label: 'Tue' }, { value: 3, label: 'Wed' }, { value: 4, label: 'Thu' }, { value: 5, label: 'Fri' }, { value: 6, label: 'Sat' }]
const saturdayOptions = [{ value: 1, label: '1st' }, { value: 2, label: '2nd' }, { value: 3, label: '3rd' }, { value: 4, label: '4th' }, { value: 5, label: '5th' }]
const defaultWorkWeekConfig: WorkWeekConfig = { workingDays: [1, 2, 3, 4, 5], offSaturdays: [] }
const workWeekPresetConfigs: Record<string, WorkWeekConfig> = {
  ...Object.fromEntries(workWeekPatternConfigs.map(item => [item.value, item.config])),
  'Monday - Friday': { workingDays: [1, 2, 3, 4, 5], offSaturdays: [] },
  'Monday - Saturday': { workingDays: [1, 2, 3, 4, 5, 6], offSaturdays: [] },
  'All days': { workingDays: [0, 1, 2, 3, 4, 5, 6], offSaturdays: [] },
  'Only 2nd Saturday off': { workingDays: [1, 2, 3, 4, 5, 6], offSaturdays: [2] },
  'Sunday + 2nd Saturday off': { workingDays: [1, 2, 3, 4, 5, 6], offSaturdays: [2] },
  'Sunday + 2nd/4th Saturday off': { workingDays: [1, 2, 3, 4, 5, 6], offSaturdays: [2, 4] }
}
const normalizeWorkWeekConfig = (config: WorkWeekConfig): WorkWeekConfig => ({ workingDays: unique(config.workingDays.map(String)).map(Number).filter(day => day >= 0 && day <= 6), offSaturdays: unique(config.offSaturdays.map(String)).map(Number).filter(day => day >= 1 && day <= 5) })
const parseWorkWeekConfig = (drop: Drop): WorkWeekConfig => {
  if (drop.configJson) {
    try { return normalizeWorkWeekConfig(JSON.parse(drop.configJson) as WorkWeekConfig) } catch { /* ignore invalid legacy config */ }
  }
  return workWeekPresetConfigs[drop.value] ?? defaultWorkWeekConfig
}
const workWeekLabel = (config: WorkWeekConfig) => {
  const normalized = normalizeWorkWeekConfig(config)
  const canonical = workWeekPatternConfigs.find(item => {
    const itemConfig = normalizeWorkWeekConfig(item.config)
    return itemConfig.workingDays.join(',') === normalized.workingDays.join(',') && itemConfig.offSaturdays.join(',') === normalized.offSaturdays.join(',')
  })
  if (canonical) return canonical.value
  const weeklyOffDays = weekDayOptions.filter(day => !normalized.workingDays.includes(day.value)).map(day => day.label)
  const saturdayOffText = normalized.workingDays.includes(6) && normalized.offSaturdays.length
    ? `${normalized.offSaturdays.map(item => saturdayOptions.find(option => option.value === item)?.label).filter(Boolean).join('/')} Saturday off`
    : ''
  return [weeklyOffDays.length ? `${weeklyOffDays.join(', ')} off` : '', saturdayOffText].filter(Boolean).join('; ') || 'No weekly off'
}
const workWeekPayload = (config: WorkWeekConfig) => {
  const normalized = normalizeWorkWeekConfig(config)
  return { value: workWeekLabel(normalized), configJson: JSON.stringify(normalized) }
}

export default function SettingsPage({ tab, onMessage, recruitmentSection = 'settings' }: { tab: SettingsTab; onMessage: (message: string) => void; recruitmentSection?: RecruitmentAdminSection }) {
  const toast = useToast()
  const [org, setOrg] = useState(org0), [setup, setSetup] = useState(settingsSetup0), [clients, setClients] = useState<Client[]>([]), [client, setClient] = useState(client0)
  const [locations, setLocations] = useState<WorkLocation[]>([]), [location, setLocation] = useState(location0), [drops, setDrops] = useState<Drop[]>([]), [drop, setDrop] = useState(drop0)
  const [employees, setEmployees] = useState<Employee[]>([]), [attendanceGroups, setAttendanceGroups] = useState<AttendanceGroup[]>([])
  const [essSettings, setEssSettings] = useState<EssClientSetting[]>([])
  const [essDraft, setEssDraft] = useState<EssClientSetting | null>(null)
  const [essDrawerOpen, setEssDrawerOpen] = useState(false)
  const [essSaving, setEssSaving] = useState(false)
  const [component, setComponent] = useState(component0), [structure, setStructure] = useState(structure0), [payslip, setPayslip] = useState(payslip0), [componentTab, setComponentTab] = useState<ComponentCategory>('Earning')
  const [billingEnabled, setBillingEnabled] = useState(false), [billingRows, setBillingRows] = useState<ClientBillingConfiguration[]>([]), [billingRow, setBillingRow] = useState<ClientBillingConfiguration>(billing0), [billingDrawerOpen, setBillingDrawerOpen] = useState(false)
  const [billingAdvancedEnabled, setBillingAdvancedEnabled] = useState(false), [billingAdvancedHeaders, setBillingAdvancedHeaders] = useState<ClientBillingCostRuleHeader[]>([]), [billingAdvancedLines, setBillingAdvancedLines] = useState<ClientBillingCostRuleLine[]>([]), [billingAdvancedLine, setBillingAdvancedLine] = useState<ClientBillingCostRuleLine>(billingAdvancedLine0), [billingAdvancedLineDrawerOpen, setBillingAdvancedLineDrawerOpen] = useState(false)
  const [billingGuideOpen, setBillingGuideOpen] = useState(false)
  const [billingTemplateDownloaded, setBillingTemplateDownloaded] = useState(false)
  const [billingUpload, setBillingUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [organizationTab, setOrganizationTab] = useState<OrganizationTab>('Organization')
  const [statutoryTab, setStatutoryTab] = useState<StatutoryTab>('Income Tax Rules')
  const [ptSlab, setPtSlab] = useState<ProfessionalTaxSlab>(ptSlab0)
  const [componentDrawerOpen, setComponentDrawerOpen] = useState(false)
  const [componentTemplateDownloaded, setComponentTemplateDownloaded] = useState(false)
  const [componentUpload, setComponentUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [salaryTemplateDownloaded, setSalaryTemplateDownloaded] = useState(false)
  const [salaryTemplateUpload, setSalaryTemplateUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [clientDrawerOpen, setClientDrawerOpen] = useState(false)
  const [clientTemplateDownloaded, setClientTemplateDownloaded] = useState(false)
  const [clientUpload, setClientUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [locationDrawerOpen, setLocationDrawerOpen] = useState(false)
  const [locationTemplateDownloaded, setLocationTemplateDownloaded] = useState(false)
  const [locationUpload, setLocationUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [dropDrawerOpen, setDropDrawerOpen] = useState(false)
  const [dropTemplateDownloaded, setDropTemplateDownloaded] = useState(false)
  const [dropUpload, setDropUpload] = useState<SettingsBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [componentSaving, setComponentSaving] = useState(false)
  const [templateSaving, setTemplateSaving] = useState(false)
  const [payslipSaving, setPayslipSaving] = useState(false)
  const [saving, setSaving] = useState(false)
  const [dropState, setDropState] = useState('')
  const [bulkPreview, setBulkPreview] = useState<BulkUploadPreviewState>(emptyBulkUploadPreview)
  const [bulkPreviewImporting, setBulkPreviewImporting] = useState(false)
  const [bulkPreviewConfirm, setBulkPreviewConfirm] = useState<((preview: BulkUploadPreviewState) => Promise<void>) | null>(null)
  const isErrorMessage = (message: string) => /error|unable|failed|required|resolve|select|invalid|must|cannot|some/i.test(message)

  const load = async () => {
    const [organization, rawSetup, clientRows, locationRows, dropdownRows, employeeRows, groupRows, billingModule, billingConfigs, billingAdvanced, essClientSettings] = await Promise.all([getOrganization(org0), getSetup(settingsSetup0), getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getAttendanceGroups(), getClientBillingModule(), getClientBillingConfigurations(), getClientBillingAdvanced(), getEssClientSettings()])
    setOrg({ ...org0, ...organization, pan: organization.pan || rawSetup.tax?.pan || '', tanNumber: organization.tanNumber || rawSetup.tax?.tan || '', professionalTaxNumber: organization.professionalTaxNumber || rawSetup.statutory?.ptNumber || '' })
    setSetup({ ...settingsSetup0, ...rawSetup, tax: { ...setup0.tax, ...rawSetup.tax, clientSettings: rawSetup.tax?.clientSettings ?? setup0.tax.clientSettings, slabs: rawSetup.tax?.slabs ?? setup0.tax.slabs, surcharges: rawSetup.tax?.surcharges ?? setup0.tax.surcharges, finalAdjustments: rawSetup.tax?.finalAdjustments ?? setup0.tax.finalAdjustments, declarationSections: rawSetup.tax?.declarationSections ?? setup0.tax.declarationSections }, schedule: { ...setup0.schedule, ...rawSetup.schedule }, statutory: { ...setup0.statutory, ...rawSetup.statutory }, salaryComponents: (rawSetup.salaryComponents ?? []).map(normalizeComponentForUi), salaryStructures: rawSetup.salaryStructures ?? [], payslipTemplates: rawSetup.payslipTemplates ?? [] })
    setClients(clientRows)
    setLocations(locationRows.filter(location => location.isActive && clientRows.some(client => client.id === location.clientId)).map(location => ({ ...location0, ...location })))
    setDrops(dropdownRows.map(item => ({ ...drop0, ...item })))
    setEmployees(employeeRows)
    setAttendanceGroups(groupRows)
    setBillingEnabled(billingModule.isEnabled)
    setBillingAdvancedEnabled(Boolean(billingModule.advancedCostingEnabled))
    setBillingRows(billingConfigs.map(row => ({ ...billing0, ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), effectiveTo: row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : null })))
    setBillingAdvancedHeaders(billingAdvanced.headers.map(row => ({ ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), effectiveTo: row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : null })))
    setBillingAdvancedLines(billingAdvanced.lines)
    setEssSettings(essClientSettings)
  }

  useEffect(() => { void load() }, [])
  const editEssSetting = (row: EssClientSetting) => {
    setEssDraft({ ...row, initialPasswordMode: row.initialPasswordMode || 'App Default', fixedPassword: row.fixedPassword || '' })
    setEssDrawerOpen(true)
  }
  const patchEssDraft = (patch: Partial<EssClientSetting>) => setEssDraft(current => current ? { ...current, ...patch } : current)
  const saveEssDraft = async () => {
    if (!essDraft) return
    if ((essDraft.initialPasswordMode || 'App Default') === 'Fixed' && !essDraft.fixedPassword.trim()) {
      notify('Enter fixed password or select another initial password mode.', 'warning')
      return
    }
    setEssSaving(true)
    const payload = { ...essDraft, fixedPassword: essDraft.initialPasswordMode === 'Fixed' ? essDraft.fixedPassword : '' }
    const response = await saveEssClientSetting(payload)
    setEssSaving(false)
    if (!response.ok) {
      notify(response.error || 'Unable to save ESS settings.', 'error')
      return
    }
    setEssSettings(current => current.map(item => item.id === response.data.id ? response.data : item))
    setEssDrawerOpen(false)
    setEssDraft(null)
  }
  const o = <K extends keyof Org>(key: K, value: Org[K]) => setOrg(current => ({ ...current, [key]: value }))
  const u = <S extends keyof Setup, K extends keyof Setup[S]>(section: S, key: K, value: Setup[S][K]) => setSetup(current => ({ ...current, [section]: { ...current[section], [key]: value } }))
  const setPtSlabField = <K extends keyof ProfessionalTaxSlab>(key: K, value: ProfessionalTaxSlab[K]) => setPtSlab(current => ({ ...current, [key]: value }))
  const savePtSlab = () => {
    if (!ptSlab.state || !ptSlab.deductionAmount) return
    const row = { ...ptSlab, id: ptSlab.id || Date.now() }
    setSetup(current => ({ ...current, statutory: { ...current.statutory, ptStateSlabs: [...(current.statutory.ptStateSlabs ?? []).filter(item => item.id !== row.id), row] } }))
    setPtSlab(ptSlab0)
    notify('Click Save settings to persist Professional Tax slabs.', 'info')
  }
  const removePtSlab = (row: ProfessionalTaxSlab) => {
    if (!window.confirm(`Delete Professional Tax slab for ${row.state || 'this state'}?`)) return
    setSetup(current => ({ ...current, statutory: { ...current.statutory, ptStateSlabs: (current.statutory.ptStateSlabs ?? []).filter(item => item.id !== row.id) } }))
  }
  const uploadLogo = (file: File) => {
    const reader = new FileReader()
    reader.onload = () => o('logoDataUrl', String(reader.result || ''))
    reader.readAsDataURL(file)
  }
  const notify = (message: string, type: ToastType = 'success') => { onMessage(message); toast(message, type) }
  const notifyFromChild = (message: string) => notify(message, isErrorMessage(message) ? 'error' : 'success')
  const activeEmployees = employees.filter(item => item.isActive)
  const activeGroups = attendanceGroups.filter(item => item.isActive)
  const blockDeleteIfLinked = (item: string, links: string[]) => {
    const linked = links.filter(Boolean)
    if (!linked.length) return false
    notify(`Cannot delete ${item}. Linked records: ${linked.join(' | ')}`, 'warning')
    return true
  }
  const clientDeleteLinks = (row: Client) => {
    const id = String(row.id)
    return [
      countLink(locations.filter(item => item.isActive && String(item.clientId) === id).length, 'work location'),
      countLink(activeEmployees.filter(item => String(item.clientId) === id).length, 'employee'),
      namedLink('Salary templates', setup.salaryStructures.filter(item => refId(item.clientId) === id).map(item => item.name)),
      namedLink('Payslip templates', setup.payslipTemplates.filter(item => refId(item.clientId) === id).map(item => item.name)),
      countLink(setup.tax.clientSettings.filter(item => refId(item.clientId) === id && item.active).length, 'tax rule'),
      namedLink('Attendance policies', activeGroups.filter(item => String(item.clientId) === id).map(item => item.name))
    ]
  }
  const locationDeleteLinks = (row: WorkLocation) => [
    countLink(activeEmployees.filter(item => item.workLocationId === row.id).length, 'employee'),
    namedLink('Attendance policies', activeGroups.filter(item => item.workLocationId === row.id).map(item => item.name))
  ]
  const dropdownDeleteLinks = (row: Drop) => {
    const type = row.type, value = row.value.trim()
    if (type === 'Department') return [
      countLink(activeEmployees.filter(item => item.department === value).length, 'employee'),
      namedLink('Attendance policies', activeGroups.filter(item => item.department === value).map(item => item.name))
    ]
    if (type === 'Designation') return [
      countLink(activeEmployees.filter(item => item.designation === value).length, 'employee'),
      namedLink('Attendance policies', activeGroups.filter(item => item.designation === value).map(item => item.name))
    ]
    if (type === 'Work Week') return [
      setup.schedule.workWeek === value ? 'Payroll setup pay schedule' : '',
      namedLink('Attendance policies', activeGroups.filter(item => item.workWeek === value).map(item => item.name))
    ]
    if (type === 'State') return [
      org.state === value ? 'Organization address' : '',
      setup.statutory.ptState === value ? 'Professional Tax setup' : '',
      setup.statutory.lwfState === value ? 'LWF setup' : '',
      countLink(locations.filter(item => item.isActive && item.state === value).length, 'work location'),
      countLink((setup.statutory.ptStateSlabs ?? []).filter(item => item.state === value).length, 'PT slab')
    ]
    if (isCityType(type)) {
      const state = cityState(type)
      return [
        org.state === state && org.city === value ? 'Organization address' : '',
        countLink(locations.filter(item => item.isActive && item.state === state && item.city === value).length, 'work location')
      ]
    }
    return []
  }
  const componentDeleteLinks = (row: Component) => [
    namedLink('Salary templates', setup.salaryStructures.filter(structure => structure.lines.some(line => Number(line.componentId) === row.id)).map(item => item.name)),
    countLink(setup.tax.clientSettings.filter(item => item.active && item.taxDeductionComponentCode === row.code).length, 'client tax setting')
  ]
  const saveAll = async (event: FormEvent) => { event.preventDefault(); setSaving(true); await saveOrganization(org); await saveSetup({ ...setup, tax: { ...setup.tax, pan: org.pan || setup.tax.pan, tan: org.tanNumber || setup.tax.tan }, statutory: { ...setup.statutory, ptNumber: org.professionalTaxNumber || setup.statutory.ptNumber } }); window.dispatchEvent(new CustomEvent('organization-updated', { detail: org })); notify('Settings saved.'); setSaving(false) }
  const saveClient = async () => { if (!client.name.trim()) return notify('Client name is required.', 'error'); const response = await persistClient(client, { toast: false }); notify(response.ok ? client.id ? 'Client updated.' : 'Client saved.' : response.error || 'Unable to save client.', response.ok ? 'success' : 'error'); if (response.ok) { setClient(client0); setClientDrawerOpen(false); await load() } }
  const deleteClient = async (row: Client) => {
    if (blockDeleteIfLinked(row.name, clientDeleteLinks(row))) return
    if (!window.confirm(`Delete ${row.name}?`)) return
    const response = await persistClient({ ...row, isActive: false }, { toast: false })
    notify(response.ok ? 'Client deleted.' : response.error || 'Unable to delete client.', response.ok ? 'success' : 'error')
    if (response.ok) { if (client.id === row.id) setClient(client0); await load() }
  }
  const runBulkUploadJob = async (file: File, setUpload: Dispatch<SetStateAction<SettingsBulkUpload>>, startImport: ImportStart, getImportJob: ImportStatus, failureText: string) => {
    setUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: 0 } })
    const start = await startImport(file)
    if (!start.ok || !start.data.jobId) {
      setUpload({ open: true, state: 'error', percent: 100, summary: { ...start.data, errors: start.data.errors?.length ? start.data.errors : [start.error || 'Upload failed.'] } })
      return
    }
    let job = start.data
    while (job.state === 'Queued' || job.state === 'Processing') {
      const percent = job.totalRows ? Math.min(99, Math.round((job.completedRows / job.totalRows) * 100)) : 5
      setUpload({ open: true, state: 'uploading', percent, summary: job })
      await wait(700)
      job = await getImportJob(job.jobId)
    }
    const percent = job.totalRows ? Math.round((job.completedRows / job.totalRows) * 100) : 100
    if (job.state === 'Completed') {
      setUpload({ open: true, state: 'success', percent: 100, summary: job })
      await load()
      return
    }
    setUpload({ open: true, state: 'error', percent, summary: { ...job, errors: job.errors?.length ? job.errors : [failureText] } })
  }
  const previewBulkUploadData = (file: File, title: string, data: ImportPreviewData, rules: ImportPreviewRules, onConfirm: (file: File) => Promise<void>) => {
    const issues = validateImportPreview(data, rules)
    setBulkPreview({ open: true, title, fileName: file.name, headers: data.headers, rows: data.rows, issues })
    setBulkPreviewConfirm(() => async (preview: BulkUploadPreviewState) => onConfirm(previewToXlsxFile(preview, file.name)))
  }
  const previewBulkUpload = async (file: File, title: string, rules: ImportPreviewRules, onConfirm: (file: File) => Promise<void>) => {
    try { previewBulkUploadData(file, title, await parseImportPreviewFile(file), rules, onConfirm) }
    catch (error) { notify(error instanceof Error ? error.message : 'Unable to preview import file.', 'error') }
  }
  const parseDropdownPreviewData = async (file: File): Promise<ImportPreviewData> => {
    const sheets = await parseImportPreviewSheets(file)
    const first = sheets[0]
    if (!first) return { headers: [], rows: [] }
    if (first.headers.some(header => importNorm(header) === 'mastertype')) return first
    const rows: string[][] = []
    for (const sheet of sheets) {
      const type = dropdownTypeFromSheet(sheet.name)
      if (!type) continue
      for (const row of sheet.rows) {
        const value = previewCell(sheet.headers, row, 'Value')
        const active = previewCell(sheet.headers, row, 'Active')
        const clientId = type === 'Employee Grade' ? previewCell(sheet.headers, row, 'Client Id') : ''
        const state = type === 'City' ? previewCell(sheet.headers, row, 'State') : ''
        const configJson = type === 'Work Week' ? previewCell(sheet.headers, row, 'Config Json') || buildWorkWeekConfigJson(previewCell(sheet.headers, row, 'Working Days'), previewCell(sheet.headers, row, 'Off Saturdays')) : ''
        if ([value, active, clientId, state, configJson].some(Boolean)) rows.push([sheet.name, type, value, clientId, state, active, configJson])
      }
    }
    return { headers: ['Sheet', 'Master Type', 'Value', 'Client Id', 'State', 'Active', 'Config Json'], rows }
  }
  const confirmBulkPreview = async (preview: BulkUploadPreviewState) => {
    if (!bulkPreviewConfirm) return
    const action = bulkPreviewConfirm
    setBulkPreviewImporting(true)
    setBulkPreview(emptyBulkUploadPreview)
    setBulkPreviewConfirm(null)
    try {
      await action(preview)
    } finally {
      setBulkPreviewImporting(false)
    }
  }
  const downloadClientTemplate = () => {
    downloadXlsx('client-import-template.xlsx', [
      { name: 'Clients', rows: [clientImportHeaders, ['Acme Services Pvt Ltd', 'ACME', 'Priya Sharma', 'priya@example.com', '9876543210', 'Registered office address', 'TRUE']] },
      { name: 'Instructions', rows: [['Field', 'Required', 'Notes'], ['Client Name', 'Yes', 'Unique client display name. Existing names will be updated.'], ['Code', 'No', 'Used to update an existing client when matched.'], ['Contact Person', 'No', 'Primary contact name.'], ['Email', 'No', 'Must be valid when filled.'], ['Phone', 'No', 'Primary phone number.'], ['Address', 'No', 'Client address.'], ['Active', 'No', 'TRUE/FALSE. Blank means TRUE.']] }
    ])
    setClientTemplateDownloaded(true)
    notify('Client import template downloaded.', 'info')
  }
  const uploadClients = async (file: File | null) => {
    if (!file) return
    if (!clientTemplateDownloaded) {
      const errors = ['Download the client template before uploading.']
      setClientUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    await previewBulkUpload(file, 'Client bulk upload preview', clientPreviewRules, selected => runBulkUploadJob(selected, setClientUpload, startClientImport, getClientImportJob, 'Client import failed. No rows were saved.'))
  }
  const downloadLocationTemplate = () => {
    const firstClient = clients[0]
    downloadXlsx('work-location-import-template.xlsx', [
      { name: 'Work Locations', rows: [workLocationImportHeaders, [firstClient ? String(firstClient.id) : '', 'Head Office', 'Registered office address', 'Delhi', 'New Delhi', '110001', '', 'TRUE', 'TRUE']] },
      { name: 'Clients', rows: [['Client Id', 'Client Name', 'Client Code'], ...clients.map(item => [String(item.id), item.name, item.code || ''])] }
    ])
    setLocationTemplateDownloaded(true)
    notify('Work-location import template downloaded.', 'info')
  }
  const uploadWorkLocations = async (file: File | null) => {
    if (!file) return
    if (!locationTemplateDownloaded) {
      const errors = ['Download the work-location template before uploading.']
      setLocationUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    const clientIds = new Set(clients.map(item => String(item.id)))
    await previewBulkUpload(file, 'Work-location bulk upload preview', {
      ...workLocationPreviewRules,
      custom: (row, rowNumber) => {
        const issues: ImportPreviewIssue[] = []
        const clientId = row['Client Id']
        if (clientId && !clientIds.has(clientId)) issues.push({ rowNumber, column: 'Client Id', message: `Client Id ${clientId} was not found.` })
        if (row.PIN && !/^[1-9][0-9]{5}$/.test(row.PIN)) issues.push({ rowNumber, column: 'PIN', message: 'PIN must be a valid 6-digit PIN code.' })
        return issues
      }
    }, selected => runBulkUploadJob(selected, setLocationUpload, startWorkLocationImport, getWorkLocationImportJob, 'Work-location import failed. No rows were saved.'))
  }
  const downloadDropTemplate = () => {
    const firstClientId = clients[0] ? String(clients[0].id) : '1'
    const referenceRows = [['Clients', '', '', ''], ['Client Id', 'Client Name', 'Client Code', ''], ...clients.map(item => [String(item.id), item.name, item.code || '', '']), ['', '', '', ''], ['Sheet', 'Required columns', 'Example row', 'Notes'], ...dropTypes.map(type => [type, dropdownSheetHeaders(type).join(', '), dropdownSheetExample(type, firstClientId).join(' | '), type === 'Employee Grade' ? 'Client Id must match Clients sheet.' : type === 'City' ? 'State must exist or will be created as a state master.' : 'Value is the dropdown text.']), ...dropdownWorkWeekReferenceRows]
    downloadXlsx('dropdown-master-import-template.xlsx', [
      ...dropTypes.map(type => ({ name: type, rows: [dropdownSheetHeaders(type), dropdownSheetExample(type, firstClientId)] })),
      { name: 'Reference', rows: referenceRows }
    ])
    setDropTemplateDownloaded(true)
    notify('Dropdown master import template downloaded.', 'info')
  }
  const uploadDrops = async (file: File | null) => {
    if (!file) return
    if (!dropTemplateDownloaded) {
      const errors = ['Download the dropdown master template before uploading.']
      setDropUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    const clientIds = new Set(clients.map(item => String(item.id)))
    try {
      previewBulkUploadData(file, 'Dropdown master bulk upload preview', await parseDropdownPreviewData(file), {
        ...dropdownPreviewRules,
        custom: (row, rowNumber) => {
          const issues: ImportPreviewIssue[] = []
          const type = row['Master Type']?.toLowerCase()
          const clientId = row['Client Id']
          if (type === 'employee grade') {
            if (!clientId) issues.push({ rowNumber, column: 'Client Id', message: 'Client Id is required for Employee Grade.' })
            else if (!clientIds.has(clientId)) issues.push({ rowNumber, column: 'Client Id', message: `Client Id ${clientId} was not found.` })
          }
          if (type === 'city' && !row.State) issues.push({ rowNumber, column: 'State', message: 'State is required for City.' })
          if (type === 'work week' || row['Config Json']) {
            const jsonIssue = type === 'work week' ? validateWorkWeekJson(row['Config Json']) : (() => { try { JSON.parse(row['Config Json']); return '' } catch { return 'Config Json must be valid JSON.' } })()
            if (jsonIssue) issues.push({ rowNumber, column: 'Config Json', message: jsonIssue })
          }
          return issues
        }
      }, selected => runBulkUploadJob(selected, setDropUpload, startDropdownImport, getDropdownImportJob, 'Dropdown master import failed. No rows were saved.'))
    } catch (error) {
      notify(error instanceof Error ? error.message : 'Unable to preview dropdown import file.', 'error')
    }
  }
  const downloadSalaryComponentTemplate = () => {
    const flag = (value: boolean) => value ? 'TRUE' : 'FALSE'
    const rows = setup.salaryComponents.length
      ? setup.salaryComponents.map(item => [item.code, item.category, item.name, item.componentType, item.componentRole, item.statutoryType, item.payType, item.calculationType, item.value, item.formula, item.baseComponent, flag(item.taxable), flag(item.ctc), flag(item.proRata), flag(item.fbp), flag(item.restrictFbp), item.epf, flag(item.esi), flag(item.recurring), flag(item.scheduled), item.investmentType, item.correctionOf, item.priority, flag(item.active)])
      : [['BASIC', 'Earning', 'Basic Salary', 'Basic', 'Regular Earning', 'None', 'Fixed Pay', 'Fixed Amount', '0', '', '', 'TRUE', 'TRUE', 'TRUE', 'FALSE', 'FALSE', 'Never', 'FALSE', 'TRUE', 'FALSE', '', '', '100', 'TRUE']]
    downloadXlsx('salary-component-import-template.xlsx', [
      { name: 'Salary Components', rows: [salaryComponentImportHeaders, ...rows] },
      { name: 'Reference', rows: [['Categories', componentTabs.join(', '), ''], ['Component Roles', componentRoleOptions.join(', '), ''], ['Statutory Types', statutoryTypeOptions.join(', '), ''], ['Calculation Types', calculationOptions.join(', '), ''], ['Pay Types', 'Fixed Pay, Variable Pay', ''], ['EPF Options', 'Never, Always, Only if employee is PF eligible', ''], ['', '', ''], ['Existing Code', 'Name', 'Category'], ...setup.salaryComponents.map(item => [item.code, item.name, item.category])] }
    ])
    setComponentTemplateDownloaded(true)
    notify('Salary component import template downloaded.', 'info')
  }
  const uploadSalaryComponents = async (file: File | null) => {
    if (!file) return
    if (!componentTemplateDownloaded) {
      const errors = ['Download the salary component template before uploading.']
      setComponentUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    await previewBulkUpload(file, 'Salary component bulk upload preview', salaryComponentPreviewRules, selected => runBulkUploadJob(selected, setComponentUpload, startSalaryComponentImport, getSalaryComponentImportJob, 'Salary component import failed. No rows were saved.'))
  }
  const downloadSalaryTemplateTemplate = () => {
    const flag = (value: boolean) => value ? 'TRUE' : 'FALSE'
    const componentsById = new Map(setup.salaryComponents.map(item => [String(item.id), item]))
    const rows = setup.salaryStructures.flatMap(template => template.lines.map(line => {
      const component = componentsById.get(String(line.componentId))
      return [refId(template.clientId), template.name, template.annualCtc, flag(template.active), component?.code || String(line.componentId), line.value]
    }))
    const sampleClient = clients[0]
    const sampleComponent = setup.salaryComponents.find(item => item.code === 'BASIC') ?? setup.salaryComponents[0]
    downloadXlsx('salary-template-import-template.xlsx', [
      { name: 'Salary Templates', rows: [salaryTemplateImportHeaders, ...(rows.length ? rows : [[sampleClient ? String(sampleClient.id) : 'ALL', 'Standard Salary', '600000', 'TRUE', sampleComponent?.code || 'BASIC', sampleComponent?.formula || sampleComponent?.value || 'CTC * 40%']])] },
      { name: 'Reference', rows: [['Clients', '', ''], ['Client Id', 'Client Name', 'Client Code'], ...clients.map(item => [String(item.id), item.name, item.code || '']), ['', '', ''], ['Components', '', ''], ['Component Code', 'Name', 'Category'], ...setup.salaryComponents.map(item => [item.code, item.name, item.category])] }
    ])
    setSalaryTemplateDownloaded(true)
    notify('Salary template import template downloaded.', 'info')
  }
  const uploadSalaryTemplates = async (file: File | null) => {
    if (!file) return
    if (!salaryTemplateDownloaded) {
      const errors = ['Download the salary template before uploading.']
      setSalaryTemplateUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    const clientIds = new Set(clients.map(item => String(item.id)))
    const componentCodes = new Set(setup.salaryComponents.map(item => item.code.toUpperCase()))
    await previewBulkUpload(file, 'Salary template bulk upload preview', {
      ...salaryTemplatePreviewRules,
      custom: (row, rowNumber) => {
        const issues: ImportPreviewIssue[] = []
        const clientText = row['Client Ids']
        const clientValues = clientText.toUpperCase() === 'ALL' ? [] : clientText.split(/[;,|]/).map(item => refId(item.trim())).filter(Boolean)
        for (const clientId of clientValues) if (!clientIds.has(clientId)) issues.push({ rowNumber, column: 'Client Ids', message: `Client Id ${clientId} was not found.` })
        const code = row['Component Code']?.toUpperCase()
        if (code && !componentCodes.has(code)) issues.push({ rowNumber, column: 'Component Code', message: `Component Code ${code} was not found.` })
        return issues
      }
    }, selected => runBulkUploadJob(selected, setSalaryTemplateUpload, startSalaryTemplateImport, getSalaryTemplateImportJob, 'Salary template import failed. No rows were saved.'))
  }
  const downloadBillingTemplate = () => {
    const flag = (value: boolean) => value ? 'TRUE' : 'FALSE'
    const firstClient = clients[0]
    const rows = billingRows.length
      ? billingRows.map(row => [String(row.clientId || ''), row.workLocationId ? String(row.workLocationId) : '', row.rateCardType, row.rateType, String(row.value ?? 0), row.taxInclusive ? 'Inclusive' : 'Excluding', String(row.gstRatePercent ?? 18), String(row.effectiveFrom).slice(0, 10), row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : '', flag(row.isActive)])
      : [[firstClient ? String(firstClient.id) : '', '', 'All', 'Percentage', '0', 'Excluding', '18', new Date().toISOString().slice(0, 10), '', 'TRUE']]
    downloadXlsx('client-billing-import-template.xlsx', [
      { name: 'Client Billing', rows: [billingImportHeaders, ...rows] },
      { name: 'Reference', rows: [['Clients', '', ''], ['Client Id', 'Client Name', 'Client Code'], ...clients.map(item => [String(item.id), item.name, item.code || '']), ['', '', ''], ['Work Locations', '', ''], ['Work Location Id', 'Client Id', 'Work Location Name'], ...locations.filter(item => item.isActive).map(item => [String(item.id), String(item.clientId), item.name]), ['', '', ''], ['Options', 'Values', 'Notes'], ['Rate Card Type', billingRateCardTypes.join(', '), ''], ['Rate Type', billingRateTypes.join(', '), ''], ['Tax Basis', 'Excluding, Inclusive', ''], ['Work Location Id', 'Blank or 0', 'Applies to all locations for that client'], ['Active', 'TRUE, FALSE', '']] }
    ])
    setBillingTemplateDownloaded(true)
    notify('Client billing import template downloaded.', 'info')
  }
  const uploadBillingConfigurations = async (file: File | null) => {
    if (!file) return
    if (!billingTemplateDownloaded) {
      const errors = ['Download the client billing template before uploading.']
      setBillingUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors } })
      notify(errors[0], 'error')
      return
    }
    const clientIds = new Set(clients.map(item => String(item.id)))
    const locationClientById = new Map(locations.filter(item => item.isActive).map(item => [String(item.id), String(item.clientId)]))
    await previewBulkUpload(file, 'Client billing bulk upload preview', {
      ...billingPreviewRules,
      custom: (row, rowNumber) => {
        const issues: ImportPreviewIssue[] = []
        const clientId = row['Client Id']
        const locationId = row['Work Location Id']
        if (clientId && !clientIds.has(clientId)) issues.push({ rowNumber, column: 'Client Id', message: `Client Id ${clientId} was not found.` })
        if (locationId && locationId !== '0' && locationClientById.get(locationId) !== clientId) issues.push({ rowNumber, column: 'Work Location Id', message: `Work Location Id ${locationId} was not found for Client Id ${clientId}.` })
        const value = Number(row.Value)
        if (row.Value && Number.isFinite(value) && value < 0) issues.push({ rowNumber, column: 'Value', message: 'Value cannot be negative.' })
        const gst = Number(row['GST Rate %'])
        if (row['GST Rate %'] && Number.isFinite(gst) && (gst < 0 || gst > 100)) issues.push({ rowNumber, column: 'GST Rate %', message: 'GST Rate % must be between 0 and 100.' })
        const from = row['Effective From'] ? Date.parse(row['Effective From']) : Number.NaN
        const to = row['Effective To'] ? Date.parse(row['Effective To']) : Number.NaN
        if (!Number.isNaN(from) && !Number.isNaN(to) && to < from) issues.push({ rowNumber, column: 'Effective To', message: 'Effective To cannot be before Effective From.' })
        return issues
      }
    }, selected => runBulkUploadJob(selected, setBillingUpload, startClientBillingImport, getClientBillingImportJob, 'Client billing import failed. No rows were saved.'))
  }
  const applyLocationClient = (value: string) => {
    const clientId = Number(refId(value) || 0)
    const selectedClient = clients.find(item => item.id === clientId)
    setLocation(current => ({ ...current, clientId, clientName: selectedClient?.name || '' }))
  }
  const saveLocation = async () => {
    if (!location.clientId) return notify('Select a client.', 'error')
    if (!location.name.trim()) return notify('Location name is required.', 'error')
    const response = await saveWorkLocation(location)
    notify(response.ok ? 'Work location saved.' : response.error || 'Review the work location fields.', response.ok ? 'success' : 'error')
    if (response.ok) { setLocation(location0); setLocationDrawerOpen(false); await load() }
  }
  const deleteLocation = async (row: WorkLocation) => {
    if (blockDeleteIfLinked(row.name, locationDeleteLinks(row))) return
    if (!window.confirm(`Delete ${row.name}?`)) return
    const response = await saveWorkLocation({ ...row, isActive: false, isPrimary: false })
    notify(response.ok ? 'Work location deleted.' : response.error || 'Unable to delete work location.', response.ok ? 'success' : 'error')
    if (response.ok) { if (location.id === row.id) setLocation(location0); await load() }
  }
  const activeDrops = drops.filter(item => item.isActive)
  const activeClientIds = new Set(clients.map(item => String(item.id)))
  const stateOptions = unique([...activeDrops.filter(item => item.type === 'State').map(item => item.value), ...locations.map(item => item.state), org.state, location.state, dropState, setup.statutory.ptState, setup.statutory.lwfState, ptSlab.state, ...(setup.statutory.ptStateSlabs ?? []).map(item => item.state)])
  const cityOptions = (state: string) => unique([...activeDrops.filter(item => isCityType(item.type) && (!state || item.type === cityType(state))).map(item => item.value), ...locations.filter(item => !state || item.state === state).map(item => item.city), !state || org.state === state ? org.city : '', !state || location.state === state ? location.city : ''])
  const clientName = (id: string | number) => clients.find(item => String(item.id) === refId(id))?.name || (id ? String(id).split(':')[1] || `Client #${refId(id)}` : 'Default')
  const addCityForSelectedState = async (city: string) => {
    const state = location.state.trim(), value = city.trim()
    if (!state) { notify('Select a state before adding a city.', 'error'); return false }
    if (!value) { notify('City name is required.', 'error'); return false }
    const actualType = cityType(state)
    const duplicate = drops.find(item => Number(item.clientId || 0) === 0 && item.type.toLowerCase() === actualType.toLowerCase() && item.value.trim().toLowerCase() === value.toLowerCase())
    if (duplicate?.isActive) { setLocation(current => ({ ...current, city: duplicate.value })); notify('City already exists and has been selected.', 'info'); return true }
    const response = await saveDropdown(duplicate ? { ...duplicate, value, type: actualType, isActive: true } : { ...drop0, value, type: actualType, isActive: true }, { toast: false })
    notify(response.ok ? 'City added.' : response.error || 'Unable to add city.', response.ok ? 'success' : 'error')
    if (!response.ok) return false
    setLocation(current => ({ ...current, city: value }))
    await load()
    return true
  }
  const selectedDropType = drop.type === 'City' ? 'City' : drop.type
  const visibleDrops = drops.filter(item => item.isActive && (selectedDropType === 'City' ? isCityType(item.type) && (!dropState || item.type === cityType(dropState)) : item.type === selectedDropType))
  const changeDropType = (type: string) => { const workWeek = workWeekPayload(defaultWorkWeekConfig); setDrop(type === 'Work Week' ? { ...drop0, type, ...workWeek } : { ...drop0, type, clientId: type === 'Employee Grade' ? clients[0]?.id || 0 : 0 }); setDropState('') }
  const editDrop = (row: Drop) => { setDropDrawerOpen(true); if (isCityType(row.type)) { setDropState(cityState(row.type)); setDrop({ ...row, type: 'City' }); return } setDropState(''); setDrop(row.type === 'Work Week' ? { ...row, ...workWeekPayload(parseWorkWeekConfig(row)) } : row) }
  const seedWorkWeekPatterns = async () => {
    let inserted = 0, updated = 0
    for (const pattern of workWeekPatternConfigs) {
      const config = normalizeWorkWeekConfig(pattern.config)
      const existing = drops.find(item => item.type === 'Work Week' && item.value.trim().toLowerCase() === pattern.value.toLowerCase())
      const payload = { ...(existing ?? drop0), clientId: 0, type: 'Work Week', value: pattern.value, configJson: JSON.stringify(config), isActive: true }
      const response = await saveDropdown(payload, { toast: false })
      if (!response.ok) return notify(response.error || `Unable to save ${pattern.value}.`, 'error')
      if (existing) updated += 1
      else inserted += 1
    }
    notify(`Work week patterns ready. ${inserted} added, ${updated} refreshed.`, 'success')
    await load()
  }
  const saveDrop = async () => {
    const actualType = drop.type === 'City' ? dropState ? cityType(dropState) : '' : drop.type
    if (!actualType || !drop.value.trim()) return notify(drop.type === 'City' ? 'Select a state and city for the city master.' : 'Dropdown value is required.', 'error')
    const value = drop.value.trim()
    if (actualType === 'Work Week' && !parseWorkWeekConfig(drop).workingDays.length) return notify('Select at least one working day.', 'error')
    const clientId = actualType === 'Employee Grade' ? Number(drop.clientId || 0) : drop.id ? Number(drop.clientId || 0) : 0
    if (actualType === 'Employee Grade' && !clientId) return notify('Select a client for Employee Grade.', 'error')
    const duplicate = drops.find(item => item.id !== drop.id && Number(item.clientId || 0) === clientId && item.type.toLowerCase() === actualType.toLowerCase() && item.value.trim().toLowerCase() === value.toLowerCase())
    if (duplicate?.isActive) return notify(`${value} already exists in ${drop.type === 'City' ? dropState : actualType}.`, 'error')
    const payload = duplicate && !drop.id ? { ...duplicate, clientId, value, type: actualType, isActive: true } : { ...drop, clientId, type: actualType, value }
    const response = await saveDropdown(payload, { toast: false })
    notify(response.ok ? drop.id ? 'Dropdown value updated.' : 'Dropdown value added.' : response.error || 'Dropdown save failed.', response.ok ? 'success' : 'error')
    if (response.ok) { setDrop({ ...drop0, type: drop.type, clientId: drop.type === 'Employee Grade' ? clientId : 0 }); setDropDrawerOpen(false); await load() }
  }
  const deleteDrop = async (row: Drop) => {
    if (blockDeleteIfLinked(row.value, dropdownDeleteLinks(row))) return
    if (!window.confirm(`Delete ${row.value}?`)) return
    const response = await saveDropdown({ ...row, isActive: false }, { toast: false })
    notify(response.ok ? 'Dropdown value deleted.' : response.error || 'Dropdown delete failed.', response.ok ? 'success' : 'error')
    if (response.ok) await load()
  }
  const openNewComponent = () => { setComponent(normalizeComponentForUi({ ...component0, category: componentTab })); setComponentDrawerOpen(true) }
  const persistComponentSetup = async (nextSetup: Setup, success: string) => { setComponentSaving(true); const response = await saveSetup(nextSetup, { toast: false }); setComponentSaving(false); if (!response.ok) { notify(response.error || 'Unable to save salary components.', 'error'); return false } setSetup(nextSetup); notify(success); return true }
  const saveComponent = async () => { const rowForUi = prepareComponentForSave(normalizeComponentForUi({ ...component, category: componentTab })); const errors = validateComponent(rowForUi, componentTab, setup); if (errors.length) return notify(errors[0], 'error'); const isUpdate = Boolean(rowForUi.id), locked = rowForUi.id && componentUsed(rowForUi.id, setup); const row = { ...rowForUi, category: locked ? rowForUi.category : componentTab, id: rowForUi.id || Date.now(), code: locked ? rowForUi.code : rowForUi.code.trim().toUpperCase() }; const nextSetup = { ...setup, salaryComponents: [...setup.salaryComponents.filter(item => item.id !== row.id), row] }; if (await persistComponentSetup(nextSetup, isUpdate ? 'Salary component updated successfully.' : 'Salary component added successfully.')) { setComponent(normalizeComponentForUi({ ...component0, category: componentTab })); setComponentDrawerOpen(false) } }
  const editComponent = (row: Component) => { if (componentTabs.includes(row.category as ComponentCategory)) setComponentTab(row.category as ComponentCategory); setComponent(normalizeComponentForUi(row)); setComponentDrawerOpen(true) }
  const deleteComponent = async (row: Component) => { if (blockDeleteIfLinked(row.name || row.code, componentDeleteLinks(row))) return; if (!window.confirm(`Delete ${row.name || row.code}?`)) return; await persistComponentSetup({ ...setup, salaryComponents: setup.salaryComponents.filter(item => item.id !== row.id) }, 'Salary component deleted successfully.'); if (component.id === row.id) { setComponent({ ...component0, category: componentTab }); setComponentDrawerOpen(false) } }
  const toggleBillingModule = async (enabled: boolean) => {
    const response = await saveClientBillingModule({ isEnabled: enabled, advancedCostingEnabled: billingAdvancedEnabled })
    notify(response.ok ? enabled ? 'Client billing configuration enabled.' : 'Client billing configuration disabled.' : response.error || 'Unable to update billing module.', response.ok ? 'success' : 'error')
    if (response.ok) setBillingEnabled(enabled)
  }
  const toggleAdvancedBilling = async (enabled: boolean) => {
    const response = await saveClientBillingModule({ isEnabled: billingEnabled, advancedCostingEnabled: enabled })
    notify(response.ok ? enabled ? 'Advanced costing enabled.' : 'Advanced costing disabled.' : response.error || 'Unable to update advanced costing.', response.ok ? 'success' : 'error')
    if (response.ok) setBillingAdvancedEnabled(enabled)
  }
  const openBillingDrawer = () => { setBillingRow({ ...billing0, clientId: clients[0]?.id || 0 }); setBillingDrawerOpen(true) }
  const editBilling = (row: ClientBillingConfiguration) => { setBillingRow({ ...billing0, ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), effectiveTo: row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : null }); setBillingDrawerOpen(true) }
  const saveBilling = async () => {
    if (!billingRow.clientId) return notify('Select a client.', 'error')
    if (billingRow.value < 0) return notify('Value cannot be negative.', 'error')
    if (billingRow.gstRatePercent < 0 || billingRow.gstRatePercent > 100) return notify('GST rate must be between 0 and 100.', 'error')
    if (!billingRow.effectiveFrom) return notify('Effective from date is required.', 'error')
    const response = await saveClientBillingConfiguration(billingRow)
    notify(response.ok ? billingRow.id ? 'Billing configuration updated.' : 'Billing configuration saved.' : response.error || 'Unable to save billing configuration.', response.ok ? 'success' : 'error')
    if (response.ok) { setBillingDrawerOpen(false); setBillingRow(billing0); setBillingRows(await getClientBillingConfigurations()) }
  }
  const deleteBilling = async (row: ClientBillingConfiguration) => {
    if (!window.confirm(`Delete billing configuration for ${row.clientName || 'this client'}?`)) return
    const response = await saveClientBillingConfiguration({ ...row, isActive: false })
    notify(response.ok ? 'Billing configuration deleted.' : response.error || 'Unable to delete billing configuration.', response.ok ? 'success' : 'error')
    if (response.ok) setBillingRows(await getClientBillingConfigurations())
  }
  const saveAdvancedLine = async () => {
    if (!billingAdvancedLine.headerId) return notify('Select a rule header.', 'error')
    const response = await saveClientBillingAdvancedLine(billingAdvancedLine)
    notify(response.ok ? billingAdvancedLine.id ? 'Advanced rule line updated.' : 'Advanced rule line saved.' : response.error || 'Unable to save advanced rule line.', response.ok ? 'success' : 'error')
    if (response.ok) { setBillingAdvancedLineDrawerOpen(false); setBillingAdvancedLine(billingAdvancedLine0); const setup = await getClientBillingAdvanced(); setBillingAdvancedHeaders(setup.headers); setBillingAdvancedLines(setup.lines) }
  }
  const saveStructure = async () => {
    if (!structure.name.trim()) return notify('Template name is required.', 'error')
    const templateName = structure.name.trim()
    const selectedClientIds = refIds(structure.clientId)
    const targetClientIds = selectedClientIds.length ? selectedClientIds : ['']
    const templateKey = (row: Structure) => `${refId(row.clientId)}:${row.name.trim().toLowerCase()}`
    const rows = targetClientIds.map((clientId, index) => {
      const existing = setup.salaryStructures.find(item => item.id !== structure.id && refId(item.clientId) === clientId && item.name.trim().toLowerCase() === templateName.toLowerCase())
      return { ...structure, clientId, name: templateName, id: existing?.id || (index === 0 && structure.id ? structure.id : Date.now() + index) }
    })
    const replacementIds = new Set([structure.id, ...rows.map(item => item.id)].filter(Boolean))
    const replacementKeys = new Set(rows.map(templateKey))
    const nextSetup = { ...setup, salaryStructures: [...setup.salaryStructures.filter(item => !replacementIds.has(item.id) && !replacementKeys.has(templateKey(item))), ...rows] }
    setTemplateSaving(true)
    const response = await saveSetup(nextSetup, { toast: false })
    setTemplateSaving(false)
    if (!response.ok) return notify(response.error || 'Unable to save salary template.', 'error')
    setSetup(nextSetup)
    setStructure(structure0)
    notify(rows.length > 1 ? `Salary template saved for ${rows.length} clients.` : rows[0]?.id === structure.id ? 'Salary template updated.' : 'Salary template saved.')
  }
  const savePayslip = async () => {
    if (!payslip.name.trim()) return notify('Payslip template name is required.', 'error')
    const row = { ...payslip, clientId: refId(payslip.clientId), id: payslip.id || Date.now() }
    const nextSetup = { ...setup, payslipTemplates: [...setup.payslipTemplates.filter(item => item.id !== row.id), row] }
    setPayslipSaving(true)
    const response = await saveSetup(nextSetup, { toast: false })
    setPayslipSaving(false)
    if (!response.ok) return notify(response.error || 'Unable to save payslip template.', 'error')
    setSetup(nextSetup)
    setPayslip(payslip0)
    notify(row.id === payslip.id ? 'Payslip template updated.' : 'Payslip template saved.')
  }
  const previewStructure = setup.salaryStructures.find(item => refId(item.clientId) === refId(payslip.clientId)) ?? setup.salaryStructures[0]
  const monthly = Number(previewStructure?.annualCtc || 600000) / 12
  const previewLines = setup.salaryComponents.filter(item => item.active).slice(0, 6).map((componentRow, index) => ({ componentRow, amount: componentRow.category === 'Deduction' ? monthly * 0.048 : index === 0 ? monthly * 0.4 : index === 1 ? monthly * 0.2 : monthly * 0.08 }))
  const renderOrganizationBody = () => {
    if (organizationTab === 'Organization') return <Row gutter={[16, 16]}><Col xs={24} lg={6}><AntCard title="Logo" size="small" className="settings-compact-panel organization-logo-panel"><FileDropZone accept="image/png,image/jpeg,image/svg+xml,image/webp" title="Drop logo here or browse" hint="PNG, JPG, SVG or WebP for payslips and documents." onFile={uploadLogo} preview={org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo preview" /> : <b>No logo</b>} />{org.logoDataUrl && <Button block onClick={() => o('logoDataUrl', '')}>Remove logo</Button>}</AntCard></Col><Col xs={24} lg={6}><AntCard title="Basic details" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Form.Item label="Name" required><Input value={org.name} onChange={event => o('name', event.target.value)} /></Form.Item><Form.Item label="Legal name"><Input value={org.legalName} onChange={event => o('legalName', event.target.value)} /></Form.Item><Form.Item label="Industry"><Input value={org.industry} onChange={event => o('industry', event.target.value)} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={6}><AntCard title="Tax identity" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Form.Item label="GST Number"><Input value={org.gstin} onChange={event => o('gstin', event.target.value.toUpperCase())} /></Form.Item><Form.Item label="TAN Number"><Input value={org.tanNumber} onChange={event => o('tanNumber', event.target.value.toUpperCase())} /></Form.Item><Form.Item label="PAN"><Input value={org.pan} onChange={event => o('pan', event.target.value.toUpperCase())} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={6}><AntCard title="Address" size="small" className="settings-compact-panel organization-address-panel"><Form component={false} layout="vertical"><Form.Item label="Address" required><Input value={org.addressLine1} onChange={event => o('addressLine1', event.target.value)} /></Form.Item><Form.Item label="State"><Sel v={org.state} set={value => o('state', value)} a={stateOptions} /></Form.Item><Form.Item label="City" required><Input value={org.city} onChange={event => o('city', event.target.value)} /></Form.Item><Form.Item label="PIN" required><Input value={org.postalCode} onChange={event => o('postalCode', event.target.value.replace(/\D/g, '').slice(0, 6))} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={12}><AntCard title="Registered Office Address" size="small" className="settings-compact-panel"><Input.TextArea rows={3} value={org.registeredOfficeAddress} onChange={event => o('registeredOfficeAddress', event.target.value)} /></AntCard></Col><Col xs={24} lg={12}><AntCard title="Corporate Office Address" size="small" className="settings-compact-panel"><Input.TextArea rows={3} value={org.corporateOfficeAddress} onChange={event => o('corporateOfficeAddress', event.target.value)} /></AntCard></Col></Row>
    if (organizationTab === 'Tax') return <AntCard title="Tax details" size="small" className="settings-compact-panel"><Row gutter={16}><Col xs={24} md={6}><Form.Item label="PAN"><Input value={setup.tax.pan} onChange={event => u('tax', 'pan', event.target.value.toUpperCase())} /></Form.Item></Col><Col xs={24} md={6}><Form.Item label="TAN"><Input value={setup.tax.tan} onChange={event => u('tax', 'tan', event.target.value.toUpperCase())} /></Form.Item></Col><Col xs={24} md={6}><Form.Item label="AO Code"><Input value={setup.tax.aoCode} onChange={event => u('tax', 'aoCode', event.target.value)} /></Form.Item></Col><Col xs={24} md={6}><Form.Item label="Frequency"><Sel v={setup.tax.frequency} set={value => u('tax', 'frequency', value)} a={['Monthly', 'Quarterly']} /></Form.Item></Col></Row></AntCard>
    if (organizationTab === 'EPF') return <AntCard title="EPF setup" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Row gutter={16}><Col xs={24} md={8}><Form.Item><AntCheckbox checked={setup.statutory.epf} onChange={event => u('statutory', 'epf', event.target.checked)}>Enable EPF</AntCheckbox></Form.Item></Col><Col xs={24} md={8}><Form.Item label="EPF registration no"><Input value={setup.statutory.epfNumber} onChange={event => u('statutory', 'epfNumber', event.target.value)} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Contribution"><Sel v={setup.statutory.epfContribution} set={value => u('statutory', 'epfContribution', value)} a={['Both Employee and Employer', 'Employee only', 'Employer only']} /></Form.Item></Col></Row><Space wrap><AntCheckbox checked={setup.statutory.epfCtc} onChange={event => u('statutory', 'epfCtc', event.target.checked)}>Employer PF in CTC</AntCheckbox><AntCheckbox checked={setup.statutory.restrictPf} onChange={event => u('statutory', 'restrictPf', event.target.checked)}>Restrict PF to statutory wage ceiling</AntCheckbox><AntCheckbox checked={setup.statutory.abry} onChange={event => u('statutory', 'abry', event.target.checked)}>ABRY applicable</AntCheckbox></Space></Form></AntCard>
    if (organizationTab === 'ESI') return <AntCard title="ESI setup" size="small" className="settings-compact-panel"><Row gutter={16}><Col xs={24} md={8}><AntCheckbox checked={setup.statutory.esi} onChange={event => u('statutory', 'esi', event.target.checked)}>Enable ESI</AntCheckbox></Col><Col xs={24} md={8}><Form.Item label="ESI registration no"><Input value={setup.statutory.esiNumber} onChange={event => u('statutory', 'esiNumber', event.target.value)} /></Form.Item></Col></Row></AntCard>
    if (organizationTab === 'Professional Tax') return <AntCard title="Professional Tax" size="small" className="settings-compact-panel"><Form.Item label="PT registration no"><Input value={org.professionalTaxNumber} onChange={event => o('professionalTaxNumber', event.target.value)} /></Form.Item></AntCard>
    return <AntCard title="Labour Welfare Fund" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Row gutter={16}><Col xs={24} md={8}><Form.Item><AntCheckbox checked={setup.statutory.lwf} onChange={event => u('statutory', 'lwf', event.target.checked)}>Enable LWF</AntCheckbox></Form.Item></Col><Col xs={24} md={8}><Form.Item label="LWF state"><Sel v={setup.statutory.lwfState} set={value => u('statutory', 'lwfState', value)} a={stateOptions} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Deduction cycle"><Sel v={setup.statutory.lwfCycle} set={value => u('statutory', 'lwfCycle', value)} a={['Monthly', 'Half-yearly', 'Yearly']} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Eligibility wage limit"><Input value={setup.statutory.lwfEligibilityLimit} onChange={event => u('statutory', 'lwfEligibilityLimit', event.target.value.replace(/\D/g, ''))} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Employee contribution"><Input value={setup.statutory.lwfEmployeeContribution} onChange={event => u('statutory', 'lwfEmployeeContribution', event.target.value)} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Employer contribution"><Input value={setup.statutory.lwfEmployerContribution} onChange={event => u('statutory', 'lwfEmployerContribution', event.target.value)} /></Form.Item></Col></Row></Form></AntCard>
  }
  const renderProfessionalTaxSetup = () => <Card t="Professional Tax">
    <div className="grid"><Chk l="Enable PT" v={setup.statutory.pt} set={value => u('statutory', 'pt', value)} /><F l="Default PT state"><Sel v={setup.statutory.ptState} set={value => u('statutory', 'ptState', value)} a={stateOptions} /></F><F l="Deduction cycle"><Sel v={setup.statutory.ptCycle} set={value => u('statutory', 'ptCycle', value)} a={['Monthly', 'Half-yearly', 'Yearly']} /></F></div>
    <section className="pt-slab-manager"><h3>State-wise tax slab management</h3><div className="grid"><F l="State"><Sel v={ptSlab.state} set={value => setPtSlabField('state', value)} a={stateOptions} /></F><F l="Salary from"><input value={ptSlab.salaryFrom} onChange={event => setPtSlabField('salaryFrom', event.target.value.replace(/\D/g, ''))} /></F><F l="Salary to"><input value={ptSlab.salaryTo} onChange={event => setPtSlabField('salaryTo', event.target.value.replace(/\D/g, ''))} placeholder="No upper limit" /></F><F l="Deduction amount"><input value={ptSlab.deductionAmount} onChange={event => setPtSlabField('deductionAmount', event.target.value.replace(/\D/g, ''))} /></F><F l="Effective from"><input type="date" value={ptSlab.effectiveFrom} onChange={event => setPtSlabField('effectiveFrom', event.target.value)} /></F><F l="Effective to"><input type="date" value={ptSlab.effectiveTo} onChange={event => setPtSlabField('effectiveTo', event.target.value)} /></F><F l="Gender"><Sel v={ptSlab.gender} set={value => setPtSlabField('gender', value)} a={['All', 'Male', 'Female', 'Other']} /></F><Chk l="Active slab" v={ptSlab.active} set={value => setPtSlabField('active', value)} /><F l="Notes" w><input value={ptSlab.notes} onChange={event => setPtSlabField('notes', event.target.value)} placeholder="e.g. February special deduction" /></F><button type="button" onClick={savePtSlab}>{ptSlab.id ? 'Update slab' : 'Add slab'}</button></div><DataTable rows={setup.statutory.ptStateSlabs ?? []} columns={[{ key: 'state', label: 'State' }, { key: 'salaryRange', label: 'Salary Range', value: row => `${row.salaryFrom || '0'} - ${row.salaryTo || 'No limit'}` }, { key: 'deductionAmount', label: 'Deduction' }, { key: 'cycle', label: 'Cycle', value: () => setup.statutory.ptCycle }, { key: 'effective', label: 'Effective', value: row => `${row.effectiveFrom || '-'} to ${row.effectiveTo || 'Open'}` }, { key: 'gender', label: 'Gender' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <span className="row-actions"><button type="button" onClick={() => setPtSlab(row)}>Edit</button><button type="button" className="danger" onClick={() => removePtSlab(row)}>Delete</button></span>} /></section>
  </Card>
  const renderBillingGuide = () => <Modal open={billingGuideOpen} onCancel={() => setBillingGuideOpen(false)} footer={<Button type="primary" onClick={() => setBillingGuideOpen(false)}>Got it</Button>} width={1080} title="Advanced Costing Configuration Guide" className="billing-guide-modal">
    <div className="billing-guide">
      <section className="billing-guide-hero">
        <b>Simple rule</b>
        <p><strong>Line type</strong> decides what the row will read or create. <strong>Match value</strong> must match the selected source. <strong>Base type</strong> decides which amount the calculation uses.</p>
      </section>
      <section>
        <h3>How statutory linking works</h3>
        <p>Salary Components have a field called <strong>Statutory type</strong>. If a billing line uses <strong>Line type = Statutory Type</strong> and <strong>Match value = PF Employer</strong>, the report picks payroll result lines whose salary component is tagged as <strong>PF Employer</strong>.</p>
        <div className="billing-guide-chain"><span>Salary Component EPF_ER</span><span>Statutory type = PF Employer</span><span>Payroll calculates EPF_ER</span><span>Billing line matches PF Employer</span><span>Report uses processed PF amount</span></div>
      </section>
      <section>
        <h3>Field guide</h3>
        <div className="billing-guide-grid">
          <article><b>Rule header</b><p>Contract header. It controls client, work location, effective dates, GST rate, and active status.</p><em>Example: RECL rule from 01-04-2026, all locations, GST 18%.</em></article>
          <article><b>Line type</b><p>What the line reads or creates: total, component, category, statutory type, fixed charge, or commission.</p><em>Use Statutory Type for PF/ESI/TDS/PT.</em></article>
          <article><b>Match value</b><p>The exact source to match. For Statutory Type, it must match the statutory type in Salary Components.</p><em>PF Employer, ESI Employer, EPF_ER, Reimbursement, Insurance.</em></article>
          <article><b>Base type</b><p>The amount used for calculation: matched amount, net pay, gross pay, employer cost, or accumulated billable base.</p><em>Use Processed Amount for PF Employer. Use Billable Salary for service charge.</em></article>
          <article><b>Rate type</b><p>Actual uses the source amount. Percent applies Rate value %. Fixed uses Rate value as amount.</p><em>3.26 means 3.26%. 4.1666667 means 4.1666667%.</em></article>
          <article><b>Flags</b><p>GST applicable controls tax. Include in service-charge base controls whether this line is part of commission base.</p><em>Tick base lines, do not tick the service charge line itself.</em></article>
        </div>
      </section>
      <section>
        <h3>Line type decision table</h3>
        <table><thead><tr><th>Requirement</th><th>Line type</th><th>Match value</th><th>Base type</th></tr></thead><tbody>
          <tr><td>Recover employee payout</td><td>Base Amount</td><td>Total Paid To Employee</td><td>Net Pay</td></tr>
          <tr><td>Recover employer PF</td><td>Statutory Type</td><td>PF Employer</td><td>Processed Amount</td></tr>
          <tr><td>Recover employer ESIC</td><td>Statutory Type</td><td>ESI Employer</td><td>Processed Amount</td></tr>
          <tr><td>Recover exact component</td><td>Payroll Component</td><td>EPF_ER / BASIC / HRA</td><td>Processed Amount</td></tr>
          <tr><td>Bill all reimbursements</td><td>Component Category</td><td>Reimbursement</td><td>Processed Amount</td></tr>
          <tr><td>Add insurance/fixed charge</td><td>Fixed Charge</td><td>Insurance</td><td>Billable Salary</td></tr>
          <tr><td>Add service charge</td><td>Commission</td><td>Service Charges</td><td>Billable Salary</td></tr>
        </tbody></table>
      </section>
      <section>
        <h3>RECL configuration example</h3>
        <table><thead><tr><th>Purpose</th><th>Line type</th><th>Match value</th><th>Base type</th><th>Rate</th><th>Service base</th></tr></thead><tbody>
          <tr><td>Total paid to employee</td><td>Base Amount</td><td>Total Paid To Employee</td><td>Net Pay</td><td>Actual</td><td>Yes</td></tr>
          <tr><td>Employer PF</td><td>Statutory Type</td><td>PF Employer</td><td>Processed Amount</td><td>Actual</td><td>Yes</td></tr>
          <tr><td>PF Admin</td><td>Statutory Type</td><td>PF Employer</td><td>Processed Amount</td><td>4.1666667%</td><td>Yes</td></tr>
          <tr><td>EDLI</td><td>Statutory Type</td><td>PF Employer</td><td>Processed Amount</td><td>4.1666667%</td><td>Yes</td></tr>
          <tr><td>Employer ESIC</td><td>Statutory Type</td><td>ESI Employer</td><td>Processed Amount</td><td>Actual</td><td>Yes</td></tr>
          <tr><td>Insurance</td><td>Fixed Charge</td><td>Insurance</td><td>Billable Salary</td><td>Fixed 0</td><td>Yes</td></tr>
          <tr><td>Service charge</td><td>Commission</td><td>Service Charges</td><td>Billable Salary</td><td>3.26%</td><td>No</td></tr>
        </tbody></table>
        <p><strong>PF Admin / EDLI logic:</strong> Employer PF / 12% * 0.5% = Employer PF * 4.1666667%.</p>
      </section>
      <section>
        <h3>Before saving, check this</h3>
        <ul>
          <li>For PF/ESI/TDS/PT, prefer <strong>Statutory Type</strong>, not component code.</li>
          <li>Match value for Statutory Type must match Salary Component statutory type.</li>
          <li>Use <strong>Processed Amount</strong> for component/statutory/category lines.</li>
          <li>Use <strong>Billable Salary</strong> only for commission/service charge lines.</li>
          <li>Tick <strong>Include in service-charge base</strong> only for lines that should be part of commission base.</li>
          <li>If a value is zero, check whether payroll result contains that component and whether statutory type is maintained correctly.</li>
        </ul>
      </section>
    </div>
  </Modal>
  const renderClientBilling = () => {
    return <AntCard title="Client Billing Configuration" size="small" className="settings-panel settings-table-panel">
      <div className="component-table-head"><div><b>Advanced costing rules</b><span>Payroll-output based billing and payroll-cost interpretation layer. Salary components are not changed.</span></div><Space className="settings-master-actions" size={8} wrap><Button className="settings-toolbar-secondary" onClick={() => setBillingGuideOpen(true)}>Configuration guide</Button><AntCheckbox checked={billingEnabled} onChange={event => void toggleBillingModule(event.target.checked)}>Enable module</AntCheckbox><AntCheckbox checked={billingAdvancedEnabled} disabled={!billingEnabled} onChange={event => void toggleAdvancedBilling(event.target.checked)}>Enable advanced costing</AntCheckbox><Button type="primary" disabled={!billingEnabled || !billingAdvancedEnabled || billingAdvancedHeaders.length === 0} onClick={() => { setBillingAdvancedLine({ ...billingAdvancedLine0, headerId: billingAdvancedHeaders[0]?.id || 0 }); setBillingAdvancedLineDrawerOpen(true) }}>Add rule line</Button></Space></div>
      {!billingEnabled && <p className="component-guide"><b>Module disabled</b><span>Enable the module to maintain advanced billing and payroll cost rules.</span></p>}
      {billingEnabled && !billingAdvancedEnabled && <p className="component-guide"><b>Advanced costing disabled</b><span>Enable advanced costing when billing should be calculated from processed payroll output such as net pay, employer PF, ESIC, insurance, service charge, and GST.</span></p>}
      {billingEnabled && billingAdvancedEnabled && <><DataTable rows={billingAdvancedHeaders.filter(row => row.isActive)} emptyText="No advanced costing rule header. Create a standard template to start." exportFileName="client-billing-advanced-headers" columns={[{ key: 'ruleName', label: 'Rule' }, { key: 'clientName', label: 'Client' }, { key: 'workLocationName', label: 'Work location', value: row => row.workLocationName || 'All locations' }, { key: 'gstRatePercent', label: 'GST %', value: row => `${row.gstRatePercent}%` }, { key: 'effectiveFrom', label: 'From', value: row => String(row.effectiveFrom).slice(0, 10) }, { key: 'effectiveTo', label: 'To', value: row => row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : 'Open' }]} />
        <DataTable rows={billingAdvancedLines.filter(row => row.isActive)} emptyText="No advanced rule lines added." exportFileName="client-billing-advanced-lines" actions={row => <Button size="small" type="primary" onClick={() => { setBillingAdvancedLine(row); setBillingAdvancedLineDrawerOpen(true) }}>Edit</Button>} columns={[{ key: 'ruleName', label: 'Rule' }, { key: 'displayGroup', label: 'Group' }, { key: 'lineType', label: 'Line type' }, { key: 'matchValue', label: 'Match' }, { key: 'baseType', label: 'Base type' }, { key: 'rateType', label: 'Rate type' }, { key: 'rateValue', label: 'Rate' }, { key: 'taxApplicable', label: 'GST', value: row => row.taxApplicable ? 'Yes' : 'No' }, { key: 'commissionApplicable', label: 'Service base', value: row => row.commissionApplicable ? 'Yes' : 'No' }]} /></>}
      {renderBillingGuide()}
      {billingAdvancedLineDrawerOpen && <div className="component-drawer-backdrop" onClick={() => setBillingAdvancedLineDrawerOpen(false)}><aside className="component-drawer" role="dialog" aria-modal="true" aria-label="Advanced costing rule line" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">Advanced costing</span><h3>{billingAdvancedLine.id ? 'Edit rule line' : 'Add rule line'}</h3><p>Rules are applied on processed payrun lines only. Payroll calculation remains untouched.</p></div><button type="button" onClick={() => setBillingAdvancedLineDrawerOpen(false)}>x</button></header><div className="component-drawer-form"><div className="component-drawer-note">Line type decides what the rule reads or creates. Match value must match that source. For Statutory Type, match value must match the Statutory type maintained in Salary Components.</div><InfoField label="Rule header" help="Contract header. It decides client, work location, effective dates, and GST rate."><Sel v={billingAdvancedLine.headerId || ''} set={value => setBillingAdvancedLine(current => ({ ...current, headerId: Number(refId(value) || 0) }))} a={billingAdvancedHeaders.map(item => `${item.id}:${item.ruleName} - ${item.clientName}`)} /></InfoField><InfoField label="Line type" help="What this line reads or creates: payroll totals, exact component, component category, statutory identity, fixed charge, or commission/service charge."><Sel v={billingAdvancedLine.lineType} set={value => setBillingAdvancedLine(current => ({ ...current, lineType: value }))} a={['Base Amount', 'Payroll Component', 'Component Category', 'Statutory Type', 'Fixed Charge', 'Commission']} /></InfoField><InfoField label="Match value" help="Important: for Statutory Type, this must match the Statutory type in Salary Components, such as PF Employer or ESI Employer."><Input value={billingAdvancedLine.matchValue} onChange={event => setBillingAdvancedLine(current => ({ ...current, matchValue: event.target.value }))} placeholder="PF Employer / ESI Employer / EPF_ER" /></InfoField><InfoField label="Base type" help="Processed Amount uses matched payroll line. Net Pay/Gross Pay use payrun totals. Billable Salary uses prior lines marked as service-charge base."><Sel v={billingAdvancedLine.baseType} set={value => setBillingAdvancedLine(current => ({ ...current, baseType: value }))} a={['Processed Amount', 'Net Pay', 'Gross Pay', 'Employer Cost', 'Billable Salary']} /></InfoField><InfoField label="Rate type" help="Actual takes the source amount as-is. Percent applies Rate value %. Fixed uses Rate value as absolute amount."><Sel v={billingAdvancedLine.rateType} set={value => setBillingAdvancedLine(current => ({ ...current, rateType: value }))} a={['Actual', 'Percent', 'Fixed']} /></InfoField><InfoField label="Rate value" help="Only used for Percent or Fixed. Example: 3.26 for service charge, 4.1666667 for PF admin based on employer PF."><Input type="number" min={0} step="0.01" value={billingAdvancedLine.rateValue} onChange={event => setBillingAdvancedLine(current => ({ ...current, rateValue: Number(event.target.value || 0) }))} /></InfoField><InfoField label="Display group" help="Report grouping label, for example Employer statutory, PF Admin, EDLI, Insurance, Service charges."><Input value={billingAdvancedLine.displayGroup} onChange={event => setBillingAdvancedLine(current => ({ ...current, displayGroup: event.target.value }))} /></InfoField><InfoField label="Sort order" help="Controls calculation and display order. Commission should usually come after all base lines."><Input type="number" value={billingAdvancedLine.sortOrder} onChange={event => setBillingAdvancedLine(current => ({ ...current, sortOrder: Number(event.target.value || 100) }))} /></InfoField><InfoField label="Flags" help="GST applies tax on this line. Service-charge base means this line amount will be included before calculating commission."><Space direction="vertical"><AntCheckbox checked={billingAdvancedLine.taxApplicable} onChange={event => setBillingAdvancedLine(current => ({ ...current, taxApplicable: event.target.checked }))}>GST applicable</AntCheckbox><AntCheckbox checked={billingAdvancedLine.commissionApplicable} onChange={event => setBillingAdvancedLine(current => ({ ...current, commissionApplicable: event.target.checked }))}>Include in service-charge base</AntCheckbox><AntCheckbox checked={billingAdvancedLine.isActive} onChange={event => setBillingAdvancedLine(current => ({ ...current, isActive: event.target.checked }))}>Active</AntCheckbox></Space></InfoField></div><footer><button type="button" className="secondary" onClick={() => setBillingAdvancedLineDrawerOpen(false)}>Cancel</button><button type="button" onClick={() => void saveAdvancedLine()}>Save rule line</button></footer></aside></div>}
    </AntCard>
  }
  const componentTypeOptions = componentTab === 'Earning' ? ['Basic', 'House Rent Allowance', 'Custom Allowance', 'Bonus', 'Commission'] : componentTab === 'Deduction' ? ['NPS', 'VPF', 'Non-Taxable Deduction', 'One-time Deduction', 'Recurring Deduction'] : componentTab === 'Benefit' ? ['Employer NPS', 'Insurance Benefit', 'Meal Benefit', 'Car Benefit', 'Custom Benefit'] : componentTab === 'Correction' ? ['Earning Correction', 'Deduction Correction', 'Reversal', 'Arrear Correction', 'Custom Correction'] : ['Fuel', 'Telephone', 'Internet', 'Books', 'Custom Reimbursement']
  const componentRows = setup.salaryComponents.filter(item => item.category === componentTab)
  const renderComponentDrawer = () => {
    if (!componentDrawerOpen) return null
    const calcType = normalizeCalculationType(component.calculationType)
    const roleOptions = componentRolesForCategory(componentTab)
    const role = roleOptions.includes(component.componentRole) ? component.componentRole : defaultComponentRole(componentTab)
    const statutoryOptions = statutoryTypesForRole(role)
    const statutoryType = statutoryOptions.includes(component.statutoryType) ? component.statutoryType : 'None'
    const isEarning = componentTab === 'Earning'
    const isDeduction = componentTab === 'Deduction'
    const isReimbursement = componentTab === 'Reimbursement'
    const isBenefit = componentTab === 'Benefit'
    const isCorrection = componentTab === 'Correction'
    const isStatutory = role === 'Statutory Deduction' || role === 'Employer Contribution'
    const showStatutoryType = isStatutory
    const showTaxable = isEarning || isBenefit || isReimbursement
    const showCtc = isEarning || isBenefit || role === 'Employer Contribution'
    const showProRata = isEarning || isDeduction || isBenefit
    const showFbp = isEarning || isReimbursement || isBenefit
    const showPfEsiBase = isEarning
    const showInvestment = isBenefit || isStatutory || statutoryType.includes('NPS') || statutoryType === 'TDS'
    const showScheduled = isEarning || role === 'Variable Pay'
    const showCorrectionOf = isCorrection
    const setCalcType = (value: string) => setComponent({ ...component, calculationType: value, payType: value === 'Manual / Variable' ? 'Variable Pay' : component.payType })
    const setRole = (value: string) => {
      const nextStatutoryOptions = statutoryTypesForRole(value)
      setComponent({ ...component, componentRole: value, statutoryType: nextStatutoryOptions.includes(component.statutoryType) ? component.statutoryType : nextStatutoryOptions[0] })
    }
    const addFormulaToken = (token: string) => setComponent(current => ({ ...current, formula: `${current.formula}${current.formula ? ' ' : ''}${token}` }))
    const removeFormulaToken = (token: string) => setComponent(current => ({ ...current, formula: current.formula.replace(new RegExp(`\\b${token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'gi'), '').replace(/\s+/g, ' ').trim() }))
    const componentFormulaChips = setup.salaryComponents.filter(item => item.active && item.code && item.code.toUpperCase() !== component.code.trim().toUpperCase()).sort((a, b) => Number(a.priority || 999) - Number(b.priority || 999))
    const formulaComponentCodes = new Set(componentFormulaChips.map(item => item.code.toUpperCase()))
    const selectedFormulaCodes = Array.from(new Set((component.formula.toUpperCase().match(/\b[A-Z_][A-Z0-9_]*\b/g) ?? []).filter(token => formulaComponentCodes.has(token))))
    const formulaComponentOptions = componentFormulaChips.map(item => ({ value: item.code.toUpperCase(), label: `${item.code} — ${item.name || 'Component'} / Priority ${item.priority}` }))
    const formulaError = calcType === 'Formula' && component.formula.trim() ? validateFormula(component.formula, normalizeComponentForUi(component), setup) : ''
    return <div className="component-drawer-backdrop">
    <aside className="component-drawer" role="dialog" aria-modal="true" aria-label={`${component.id ? 'Edit' : 'Add'} salary component`}>
      <header><div><span className="eyebrow purple">{componentTab}</span><h3>{component.id ? 'Edit salary component' : 'Add salary component'}</h3><p>Define calculation once. Payroll uses config, not hardcoded component names.</p></div><button type="button" aria-label="Close salary component drawer" onClick={() => setComponentDrawerOpen(false)}>×</button></header>
      <div className="component-drawer-form">
        <InfoField label="Code" help="Unique short code used in formulas and imports. Changing it can affect formula references and payroll mapping."><input value={component.code} onChange={event => setComponent({ ...component, code: event.target.value.toUpperCase() })} placeholder="BASIC" /></InfoField>
        <InfoField label="Name in payslip" help="Employee-facing label printed in payroll outputs and payslips. Keep it clear and recognizable."><input value={component.name} onChange={event => setComponent({ ...component, name: event.target.value })} /></InfoField>
        <InfoField label="Component type" help="Groups the component by business purpose. This controls the setup choices shown to payroll admins."><Sel v={component.componentType} set={value => setComponent({ ...component, componentType: value })} a={componentTypeOptions} /></InfoField>
        <InfoField label="Component role" help="Defines payroll behavior for this category only."><Sel v={role} set={setRole} a={roleOptions} /></InfoField>
        {showStatutoryType && <InfoField label="Statutory type" help="Canonical identity used by payroll, reports, statutory registers, and later rule mapping."><Sel v={statutoryType} set={value => setComponent({ ...component, statutoryType: value })} a={statutoryOptions} /></InfoField>}
        <InfoField label="Pay type" help="Fixed pay is part of regular monthly salary. Variable pay is usually event-based or manually adjusted."><Sel v={component.payType} set={value => setComponent({ ...component, payType: value })} a={['Fixed Pay', 'Variable Pay']} /></InfoField>
        <InfoField label="Calculation" help="Choose the natural behavior. Formula covers percentage of CTC/component, so those shortcuts are no longer separate options."><Sel v={calcType} set={setCalcType} a={calculationOptions} /></InfoField>
        {calcType === 'Fixed Amount' && <InfoField label="Monthly amount" help="Fixed monthly value before attendance pro-rata. Example: 2000."><input value={component.value} onChange={event => setComponent({ ...component, value: event.target.value.replace(/[^\d.-]/g, '') })} placeholder="2000" /></InfoField>}
        {calcType === 'Formula' && <InfoField label="Formula" wide help="Use component codes and generic payroll tokens. Example: GROSS * 50%, BASIC * 40%, ROUNDDOWN(BASIC * 8.33%)."><div className="formula-builder"><div className="formula-chip-group"><span>Dependent components</span><AntSelect className="formula-component-select" popupClassName="formula-component-dropdown" mode="multiple" showSearch allowClear placeholder="Search & select components" value={selectedFormulaCodes} options={formulaComponentOptions} filterOption={(input, option) => String(option?.label ?? option?.value ?? '').toLowerCase().includes(input.toLowerCase())} onSelect={value => addFormulaToken(String(value))} onDeselect={value => removeFormulaToken(String(value))} onClear={() => selectedFormulaCodes.forEach(removeFormulaToken)} /></div><textarea value={component.formula} onChange={event => setComponent({ ...component, formula: event.target.value })} rows={3} placeholder="GROSS * 50%" /><div className="formula-chip-group"><span>Tokens</span><div className="formula-chips">{formulaChips.map(token => <button type="button" key={token} onClick={() => addFormulaToken(token)}>{token}</button>)}</div></div>{formulaError && <p className="inline-error">{formulaError}</p>}</div></InfoField>}
        {calcType === 'Residual / Balancing' && <InfoField label="Balance target" help="Usually GROSS or CTC. Payroll subtracts already calculated earnings before this component."><input value={component.baseComponent || 'GROSS'} onChange={event => setComponent({ ...component, baseComponent: event.target.value.toUpperCase() })} placeholder="GROSS" /></InfoField>}
        {calcType === 'Slab Based' && <InfoField label="Slab rules" wide help="Use semicolon slabs like 0-15000:0;15001+:200."><textarea value={component.formula || component.value} onChange={event => setComponent({ ...component, formula: event.target.value })} rows={3} placeholder="0-15000:0;15001+:200" /></InfoField>}
        {calcType === 'Manual / Variable' && <div className="component-drawer-note">Value will come from payroll adjustment/import/manual entry. No formula or fixed amount is required.</div>}
        {showPfEsiBase && <InfoField label="EPF wage base" help="Controls whether this earning contributes to PF wage calculations."><Sel v={component.epf} set={value => setComponent({ ...component, epf: value })} a={['Never', 'Always', 'Only if employee is PF eligible']} /></InfoField>}
        {showInvestment && <InfoField label="Tax / investment class" help="Optional classification such as 80C, 80CCD, perquisite, reimbursement, or statutory register group."><input value={component.investmentType} onChange={event => setComponent({ ...component, investmentType: event.target.value })} placeholder="80C / 80CCD / Perquisite / Other" /></InfoField>}
        {showCorrectionOf && <InfoField label="Correction of" help="Original component code being corrected or reversed."><input value={component.correctionOf} onChange={event => setComponent({ ...component, correctionOf: event.target.value.toUpperCase() })} placeholder="BASIC / HRA / TDS" /></InfoField>}
        <InfoField label="Priority" help="Controls calculation and display order. Lower numbers calculate earlier. Put residual/balancing after normal earnings."><input value={component.priority} onChange={event => setComponent({ ...component, priority: event.target.value.replace(/\D/g, '') })} /></InfoField>
        <div className="component-drawer-checks">
          {showTaxable && <><Chk l="Taxable" v={component.taxable} set={value => setComponent({ ...component, taxable: value })} /><small>Includes this component in taxable salary and tax reports.</small></>}
          {showCtc && <><Chk l="Part of CTC" v={component.ctc} set={value => setComponent({ ...component, ctc: value })} /><small>Counts this amount in annual CTC totals.</small></>}
          {showProRata && <><Chk l="Pro-rata" v={component.proRata} set={value => setComponent({ ...component, proRata: value })} /><small>Adjusts the amount for payable days.</small></>}
          {showFbp && <><Chk l="FBP" v={component.fbp} set={value => setComponent({ ...component, fbp: value })} /><small>Marks this component as flexible benefit plan eligible.</small></>}
          {component.fbp && showFbp && <><Chk l="Restrict FBP override" v={component.restrictFbp} set={value => setComponent({ ...component, restrictFbp: value })} /><small>Prevents ad hoc changes after FBP selection is locked.</small></>}
          {showPfEsiBase && <><Chk l="ESI wage base" v={component.esi} set={value => setComponent({ ...component, esi: value })} /><small>Includes this earning in ESI wage eligibility and contribution calculation.</small></>}
          <Chk l="Recurring" v={component.recurring} set={value => setComponent({ ...component, recurring: value })} /><small>Runs every payroll cycle unless changed in employee salary.</small>
          {showScheduled && <><Chk l="Scheduled" v={component.scheduled} set={value => setComponent({ ...component, scheduled: value })} /><small>Used for planned future earnings or variable pay.</small></>}
          <Chk l="Active" v={component.active} set={value => setComponent({ ...component, active: value })} /><small>Inactive components stay saved but are hidden from new salary templates.</small>
        </div>
      </div>
      <footer><button type="button" disabled={componentSaving} onClick={() => void saveComponent()}>{componentSaving ? 'Saving...' : component.id ? 'Update component' : `Add ${componentTab}`}</button></footer>
    </aside>
  </div>
  }

  return <form onSubmit={saveAll}>
    {tab === 'Organization' && <AntCard title="Organization" size="small" className="settings-panel organization-settings-page"><PageTabs items={organizationTabs} value={organizationTab} onChange={setOrganizationTab} label="Organization sections" />{renderOrganizationBody()}</AntCard>}
    {tab === 'Clients' && <>
      <AntCard title="Clients" size="small" className="settings-panel settings-table-panel">
        <div className="component-table-head">
          <div><b>Client master</b><span>Maintain client accounts used across payroll, attendance, billing, and reports.</span></div>
          <Space className="settings-master-actions" size={8} wrap>
            <Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} onClick={downloadClientTemplate}>Template</Button>
            <label className={`settings-upload-action ${!clientTemplateDownloaded ? 'disabled' : ''}`} title={clientTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}>
              <input type="file" disabled={!clientTemplateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadClients(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} />
              <UploadOutlined />
              Bulk upload
            </label>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => { setClient(client0); setClientDrawerOpen(true) }}>Add client</Button>
          </Space>
        </div>
        <DataTable rows={clients.filter(item => item.isActive)} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => { setClient(row); setClientDrawerOpen(true) }}>Edit</Button><Button size="small" danger onClick={() => void deleteClient(row)}>Delete</Button></Space>} columns={[{ key: 'name', label: 'Client' }, { key: 'code', label: 'Code' }, { key: 'contactPerson', label: 'Contact' }, { key: 'email', label: 'Email' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} />
      </AntCard>
      <Drawer className="settings-master-drawer client-master-drawer" title={<div className="settings-drawer-title"><span>Client</span><h3>{client.id ? 'Edit client' : 'Add client'}</h3><p>Maintain client account details used across payroll, attendance, billing, and reports.</p></div>} open={clientDrawerOpen} width={760} onClose={() => { setClientDrawerOpen(false); setClient(client0) }} destroyOnClose>
        <Form component="div" layout="vertical" className="settings-quick-form"><Form.Item label="Client name" required><Input value={client.name} onChange={event => setClient({ ...client, name: event.target.value })} /></Form.Item><Form.Item label="Code"><Input value={client.code} onChange={event => setClient({ ...client, code: event.target.value })} /></Form.Item><Form.Item label="Contact"><Input value={client.contactPerson} onChange={event => setClient({ ...client, contactPerson: event.target.value })} /></Form.Item><Form.Item label="Email"><Input value={client.email} onChange={event => setClient({ ...client, email: event.target.value })} /></Form.Item><Form.Item label="Phone"><Input value={client.phone} onChange={event => setClient({ ...client, phone: event.target.value })} /></Form.Item><Form.Item label="Address"><Input value={client.address} onChange={event => setClient({ ...client, address: event.target.value })} /></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => setClient(client0)}>Reset</Button><Button type="primary" onClick={saveClient}>{client.id ? 'Update client' : 'Add client'}</Button></Space></Row></Form>
      </Drawer>
    </>}
    {tab === 'Work Locations' && <><AntCard title="Work locations" size="small" className="settings-panel settings-table-panel"><div className="component-table-head"><div><b>Work-location master</b><span>Maintain operating locations, state/city, GST, and primary-location flags.</span></div><Space className="settings-master-actions" size={8} wrap><Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} onClick={downloadLocationTemplate}>Template</Button><label className={`settings-upload-action ${!locationTemplateDownloaded ? 'disabled' : ''}`} title={locationTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}><input type="file" disabled={!locationTemplateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadWorkLocations(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} /><UploadOutlined />Bulk upload</label><Button type="primary" icon={<PlusOutlined />} onClick={() => { setLocation(location0); setLocationDrawerOpen(true) }}>Add work location</Button></Space></div><DataTable rows={locations.filter(item => item.isActive)} columns={[{ key: 'clientName', label: 'Client', value: row => row.clientName || clients.find(item => item.id === row.clientId)?.name || '-' }, { key: 'name', label: 'Location' }, { key: 'city', label: 'City' }, { key: 'state', label: 'State' }, { key: 'postalCode', label: 'PIN' }, { key: 'gstin', label: 'GST Number' }, { key: 'isPrimary', label: 'Primary', render: item => item.isPrimary ? 'Yes' : 'No' }]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => { setLocation(row); setLocationDrawerOpen(true) }}>Edit</Button><Button size="small" danger onClick={() => void deleteLocation(row)}>Delete</Button></Space>} /></AntCard><Drawer className="settings-master-drawer work-location-master-drawer" title={<div className="settings-drawer-title"><span>Work location</span><h3>{location.id ? 'Edit work location' : 'Add work location'}</h3><p>Maintain client location, address, GST, and primary-location details.</p></div>} open={locationDrawerOpen} width={760} onClose={() => { setLocationDrawerOpen(false); setLocation(location0) }} destroyOnClose><Form component="div" layout="vertical" className="settings-quick-form"><Form.Item label="Client" required><Sel v={location.clientId || ''} set={applyLocationClient} a={clients.map(item => `${item.id}:${item.name}`)} /></Form.Item><Form.Item label="Location name" required><Input value={location.name} onChange={event => setLocation({ ...location, name: event.target.value })} placeholder="Head Office / WFH - Employee Name" /></Form.Item><Row gutter={12}><Col xs={24} md={12}><Form.Item label="State"><Sel v={location.state} set={value => setLocation({ ...location, state: value, city: '' })} a={stateOptions} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="City"><CitySelectWithAdd value={location.city} stateName={location.state} options={cityOptions(location.state)} onChange={value => setLocation({ ...location, city: value })} onAddCity={addCityForSelectedState} /></Form.Item></Col></Row><Row gutter={12}><Col xs={24} md={12}><Form.Item label="PIN code"><Input value={location.postalCode} onChange={event => setLocation({ ...location, postalCode: event.target.value.replace(/\D/g, '').slice(0, 6) })} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="GST Number"><Input value={location.gstin} onChange={event => setLocation({ ...location, gstin: event.target.value.toUpperCase() })} /></Form.Item></Col></Row><Form.Item label="Address"><Input value={location.address} onChange={event => setLocation({ ...location, address: event.target.value })} /></Form.Item><Form.Item><Space direction="vertical"><AntCheckbox checked={location.isPrimary} onChange={event => setLocation({ ...location, isPrimary: event.target.checked })}>Primary work location</AntCheckbox><AntCheckbox checked={location.isActive} onChange={event => setLocation({ ...location, isActive: event.target.checked })}>Active</AntCheckbox></Space></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => setLocation(location0)}>Reset</Button><Button type="primary" onClick={saveLocation}>{location.id ? 'Update location' : 'Add location'}</Button></Space></Row></Form></Drawer></>}
    {tab === 'Dropdown Masters' && <>
      <AntCard title="Dropdown values" size="small" className="settings-panel settings-table-panel">
        <div className="component-table-head">
          <div><b>Dropdown master</b><span>Maintain reusable departments, designations, states, cities, grades, and work-week patterns.</span></div>
          <Space className="settings-master-actions" size={8} wrap>
            <Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} onClick={downloadDropTemplate}>Template</Button>
            <label className={`settings-upload-action ${!dropTemplateDownloaded ? 'disabled' : ''}`} title={dropTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}>
              <input type="file" disabled={!dropTemplateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadDrops(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} />
              <UploadOutlined />Bulk upload
            </label>
            {selectedDropType === 'Work Week' && <Button className="settings-toolbar-secondary" onClick={() => void seedWorkWeekPatterns()}>Load common patterns</Button>}
            <Button type="primary" icon={<PlusOutlined />} onClick={() => { setDrop({ ...drop0, type: selectedDropType, clientId: selectedDropType === 'Employee Grade' ? clients[0]?.id || 0 : 0 }); setDropState(''); setDropDrawerOpen(true) }}>Add value</Button>
          </Space>
        </div>
        <Form component="div" layout="vertical" className="dropdown-master-filter">
          <Form.Item label="Master type"><Sel v={selectedDropType} set={changeDropType} a={dropTypes} /></Form.Item>
          {selectedDropType === 'City' && <Form.Item label="State"><Sel v={dropState} set={value => { setDropState(value); setDrop({ ...drop, type: 'City' }) }} a={stateOptions} /></Form.Item>}
          <span>{visibleDrops.length} active value{visibleDrops.length === 1 ? '' : 's'}</span>
        </Form>
        <DataTable rows={visibleDrops} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => editDrop(row)}>Edit</Button><Button size="small" danger onClick={() => void deleteDrop(row)}>Delete</Button></Space>} columns={[{ key: 'master', label: 'Master', value: row => isCityType(row.type) ? 'City' : row.type }, { key: 'clientId', label: 'Scope', value: row => Number(row.clientId || 0) ? clientName(row.clientId) : 'Global' }, { key: 'state', label: 'State', value: row => cityState(row.type) || '-' }, { key: 'value', label: 'Value' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} />
      </AntCard>
      <Drawer className="settings-master-drawer dropdown-master-drawer" title={<div className="settings-drawer-title"><span>{selectedDropType === 'Work Week' ? 'Work week pattern' : 'Dropdown master'}</span><h3>{drop.id ? 'Edit dropdown value' : 'Add dropdown value'}</h3><p>{selectedDropType === 'Work Week' ? 'Maintain weekly off rules used by attendance policy, review, payroll attendance, and reports.' : 'Maintain reusable values for departments, designations, states, cities, grades, and work weeks.'}</p></div>} open={dropDrawerOpen} width={760} onClose={() => { setDropDrawerOpen(false); setDrop({ ...drop0, type: drop.type, clientId: drop.type === 'Employee Grade' ? drop.clientId : 0 }); setDropState('') }} destroyOnClose><Form component="div" layout="vertical" className="settings-quick-form"><Form.Item label="Master type" required><Sel v={selectedDropType} set={changeDropType} a={dropTypes} /></Form.Item>{selectedDropType === 'Employee Grade' && <Form.Item label="Client" required><Sel v={drop.clientId || ''} set={value => setDrop({ ...drop, clientId: Number(refId(value) || 0) })} a={clients.map(item => `${item.id}:${item.name}`)} /></Form.Item>}{selectedDropType === 'City' && <Form.Item label="State" required><Sel v={dropState} set={value => { setDropState(value); setDrop({ ...drop, type: 'City' }) }} a={stateOptions} /></Form.Item>}{selectedDropType === 'Work Week' ? <WorkWeekMasterFields drop={drop} setDrop={setDrop} /> : <Form.Item label={selectedDropType === 'City' ? 'City' : 'Value'} required><Input value={drop.value} onChange={event => setDrop({ ...drop, value: event.target.value })} placeholder={selectedDropType === 'City' ? 'e.g. Bengaluru / Pune' : selectedDropType === 'Employee Grade' ? 'e.g. G1 / Supervisor' : 'e.g. Finance / Manager'} /></Form.Item>}<Form.Item><AntCheckbox checked={drop.isActive} onChange={event => setDrop({ ...drop, isActive: event.target.checked })}>Active</AntCheckbox></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => { setDrop({ ...drop0, type: drop.type, clientId: drop.type === 'Employee Grade' ? drop.clientId : 0 }); setDropState('') }}>Reset</Button><Button type="primary" style={drop.id ? { background: '#f59e0b', borderColor: '#f59e0b' } : undefined} onClick={saveDrop}>{drop.id ? 'Update value' : 'Add value'}</Button></Space></Row></Form></Drawer>
    </>}
    {tab === 'ESS Settings' && <AntCard title="ESS settings" size="small" className="settings-panel settings-table-panel ess-settings-panel">
      <div className="component-table-head">
        <div><b>Client self-service policy</b><span>Configure ESS behavior client-wise. Open a client, change controls, then save once.</span></div>
      </div>
      <div className="ess-settings-summary">
        <article><b>{essSettings.length}</b><span>Configured clients</span></article>
        <article><b>{essSettings.filter(row => row.allowProfileEdit).length}</b><span>Profile update enabled</span></article>
        <article><b>{essSettings.filter(row => (row.initialPasswordMode || 'App Default') !== 'App Default').length}</b><span>Custom password policies</span></article>
      </div>
      <DataTable rows={essSettings} columns={[
        { key: 'clientName', label: 'Client' },
        { key: 'allowProfileEdit', label: 'Profile update', render: row => row.allowProfileEdit ? 'Allowed' : 'Blocked' },
        { key: 'initialPasswordMode', label: 'Initial password', render: row => row.initialPasswordMode || 'App Default' },
        { key: 'isActive', label: 'Status', render: row => row.isActive ? 'Active' : 'Inactive' }
      ]} actions={row => <Button size="small" type="primary" onClick={() => editEssSetting(row)}>Configure</Button>} />
      <Drawer className="settings-master-drawer ess-settings-drawer" title={<div className="settings-drawer-title"><span>ESS Client Policy</span><h3>{essDraft?.clientName || 'Client settings'}</h3><p>Changes are saved only when you click Save policy.</p></div>} open={essDrawerOpen} width={720} onClose={() => { setEssDrawerOpen(false); setEssDraft(null) }} destroyOnClose>
        {essDraft && <Form component="div" layout="vertical" className="settings-quick-form ess-settings-form">
          <Form.Item label="Client"><Input value={essDraft.clientName} disabled /></Form.Item>
          <Form.Item label="ESS setting status"><AntCheckbox checked={essDraft.isActive} onChange={event => patchEssDraft({ isActive: event.target.checked })}>Active for this client</AntCheckbox></Form.Item>
          <Form.Item label="Employee profile update"><AntCheckbox checked={essDraft.allowProfileEdit} onChange={event => patchEssDraft({ allowProfileEdit: event.target.checked })}>Allow employees to update basic, contact, address, PAN, Aadhaar and bank information from ESS</AntCheckbox></Form.Item>
          <Form.Item label="Initial password mode" required><AntSelect value={essDraft.initialPasswordMode || 'App Default'} options={initialPasswordModeOptions.map(value => ({ value, label: value === 'EmployeeCode' ? 'Employee code' : value }))} onChange={value => patchEssDraft({ initialPasswordMode: value, fixedPassword: value === 'Fixed' ? essDraft.fixedPassword : '' })} /></Form.Item>
          {(essDraft.initialPasswordMode || 'App Default') === 'Fixed' && <Form.Item label="Fixed initial password" required><Input.Password value={essDraft.fixedPassword || ''} onChange={event => patchEssDraft({ fixedPassword: event.target.value })} placeholder="Enter fixed initial password" /></Form.Item>}
          <div className="ess-settings-note"><b>Login rule</b><span>Username remains employee code. Welcome email is queued only when a valid work email exists. If Aadhaar mode is selected and Aadhaar is missing, the system falls back to a generated temporary password.</span></div>
          <Divider />
          <Row justify="end"><Space><Button onClick={() => { setEssDrawerOpen(false); setEssDraft(null) }}>Cancel</Button><Button type="primary" loading={essSaving} onClick={() => void saveEssDraft()}>Save policy</Button></Space></Row>
        </Form>}
      </Drawer>
    </AntCard>}
    {tab === 'Tax Engine' && <TaxEngineManager clients={clients} onMessage={notifyFromChild} mode="company" />}
    {tab === 'Statutory Setup' && <><PageTabs items={statutoryTabs} value={statutoryTab} onChange={setStatutoryTab} label="Statutory setup sections" />{statutoryTab === 'Income Tax Rules' ? <TaxEngineManager clients={clients} onMessage={notifyFromChild} mode="statutory" /> : renderProfessionalTaxSetup()}</>}
    {tab === 'Client Billing Configuration' && renderClientBilling()}
    {tab === 'Travel & Expense Policies' && <TravelExpensePolicySettings />}
    {tab === 'Recruitment Administration' && <RecruitmentAdminSettings section={recruitmentSection} />}
    {tab === 'Notifications' && <NotificationSettings />}
    {tab === 'Scheduled Jobs' && <ScheduledJobsManager />}
    {tab === 'Attachments' && <AttachmentSettings />}
    {tab === 'Salary Components' && <Card t="Salary components"><PageTabs items={componentTabs} value={componentTab} onChange={item => { setComponentTab(item); setComponent(normalizeComponentForUi({ ...component0, category: item })); setComponentDrawerOpen(false) }} label="Salary component categories" getLabel={item => `${item}s`} /><div className="component-table-head"><div><b>{componentTab}s</b><span>Maintain salary component formulas, flags, and payroll order.</span></div><Space className="settings-master-actions" size={8} wrap><Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} onClick={downloadSalaryComponentTemplate}>Template</Button><label className={`settings-upload-action ${!componentTemplateDownloaded ? 'disabled' : ''}`} title={componentTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}><input type="file" disabled={!componentTemplateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadSalaryComponents(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} /><UploadOutlined />Bulk upload</label><Button type="primary" icon={<PlusOutlined />} disabled={componentSaving} onClick={openNewComponent}>Add {componentTab}</Button></Space></div><div className="component-guide"><b>Setup guide</b><span>Use Formula for all derived components. Use Residual for balancing amount. Payable values are handled by Pro-rata, separate payable rows are not needed.</span></div><DataTable rows={componentRows} actions={row => <span className="row-actions"><button type="button" onClick={() => editComponent(row)}>Edit</button><button type="button" className="danger" disabled={componentSaving} onClick={() => void deleteComponent(row)}>Delete</button></span>} emptyText={`No ${componentTab.toLowerCase()} components configured yet.`} exportFileName={`salary-${componentTab.toLowerCase()}-components`} columns={[{ key: 'code', label: 'Code' }, { key: 'name', label: 'Name' }, { key: 'componentType', label: 'Type' }, { key: 'componentRole', label: 'Role' }, { key: 'statutoryType', label: 'Statutory', render: item => item.statutoryType && item.statutoryType !== 'None' ? item.statutoryType : '-' }, { key: 'calculationType', label: 'Calculation' }, { key: 'payType', label: 'Pay Type' }, { key: 'priority', label: 'Priority' }, { key: 'locked', label: 'Lock', render: item => componentUsed(item.id, setup) ? 'Locked' : 'Open' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {tab === 'Salary Templates' && <SalaryTemplateDesigner clients={clients} components={setup.salaryComponents} structure={structure} setStructure={setStructure} templates={setup.salaryStructures.filter(item => !item.clientId || activeClientIds.has(refId(item.clientId)))} saveTemplate={saveStructure} saving={templateSaving} templateDownloaded={salaryTemplateDownloaded} onDownloadTemplate={downloadSalaryTemplateTemplate} onUploadTemplate={uploadSalaryTemplates} />}
    {tab === 'Payslip Templates' && <Card t="Payslip templates"><div className="grid"><F l="Client"><Sel v={payslip.clientId} set={value => setPayslip({ ...payslip, clientId: value })} a={clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Template name"><input value={payslip.name} onChange={event => setPayslip({ ...payslip, name: event.target.value })} /></F><F l="Theme"><Sel v={payslip.theme} set={value => setPayslip({ ...payslip, theme: value })} a={['Classic', 'Modern', 'Compact']} /></F><Chk l="Show logo" v={payslip.showLogo} set={value => setPayslip({ ...payslip, showLogo: value })} /><Chk l="Show client" v={payslip.showClient} set={value => setPayslip({ ...payslip, showClient: value })} /><Chk l="Show YTD" v={payslip.showYtd} set={value => setPayslip({ ...payslip, showYtd: value })} /><Chk l="Show bank info" v={payslip.showBank} set={value => setPayslip({ ...payslip, showBank: value })} /><Chk l="Active" v={payslip.active} set={value => setPayslip({ ...payslip, active: value })} /><F l="Footer note" w><input value={payslip.note} onChange={event => setPayslip({ ...payslip, note: event.target.value })} /></F><button type="button" disabled={payslipSaving} onClick={() => void savePayslip()}>{payslipSaving ? 'Saving...' : 'Add / Update template'}</button></div><div className={`payslip-preview ${payslip.theme.toLowerCase()}`}><header>{payslip.showLogo && <b className={org.logoDataUrl ? 'payslip-logo-mark' : ''}>{org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo" /> : 'P'}</b>}<div><h3>{org.name || 'Your Organization'}</h3><p>Payslip for June 2026</p>{payslip.showClient && <small>Client: {clientName(payslip.clientId)}</small>}</div></header><section><div><span>Employee</span><strong>Demo Employee</strong></div><div><span>Designation</span><strong>Software Engineer</strong></div><div><span>Pay Days</span><strong>30</strong></div><div><span>Bank</span><strong>{payslip.showBank ? 'HDFC ****1234' : '-'}</strong></div></section><table><thead><tr><th>Earnings</th><th>Amount</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>{previewLines.map((item, index) => <tr key={item.componentRow.id}><td>{item.componentRow.category !== 'Deduction' ? item.componentRow.name : ''}</td><td>{item.componentRow.category !== 'Deduction' ? money(item.amount) : ''}</td><td>{item.componentRow.category === 'Deduction' ? item.componentRow.name : index === 0 ? 'Professional Tax' : ''}</td><td>{item.componentRow.category === 'Deduction' ? money(item.amount) : index === 0 ? '200' : ''}</td></tr>)}</tbody></table>{payslip.showYtd && <p className="ytd">YTD Gross: Rs {money(monthly * 6)} | YTD Tax: Rs {money(1200)}</p>}<footer>{payslip.note}</footer></div><DataTable rows={setup.payslipTemplates.filter(item => !item.clientId || activeClientIds.has(refId(item.clientId)))} onEdit={setPayslip} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client', value: row => clientName(row.clientId) }, { key: 'theme', label: 'Theme' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {!['Clients', 'Client Billing Configuration', 'Travel & Expense Policies', 'Recruitment Administration', 'Notifications', 'Scheduled Jobs', 'Attachments', 'Salary Components', 'Salary Templates', 'Payslip Templates', 'Work Locations', 'Dropdown Masters', 'ESS Settings'].includes(tab) && <div className="actions"><p>Structures are client-wise. Components are global.</p><button disabled={saving}>{saving ? 'Saving...' : 'Save settings'}</button></div>}
    <BulkUploadProgressModal open={clientUpload.open} title="Client bulk upload" state={clientUpload.state} percent={clientUpload.percent} summary={clientUpload.summary} onClose={() => setClientUpload(current => ({ ...current, open: false }))} />
    <BulkUploadProgressModal open={locationUpload.open} title="Work-location bulk upload" state={locationUpload.state} percent={locationUpload.percent} summary={locationUpload.summary} onClose={() => setLocationUpload(current => ({ ...current, open: false }))} />
    <BulkUploadProgressModal open={dropUpload.open} title="Dropdown master bulk upload" state={dropUpload.state} percent={dropUpload.percent} summary={dropUpload.summary} onClose={() => setDropUpload(current => ({ ...current, open: false }))} />
    <BulkUploadProgressModal open={componentUpload.open} title="Salary component bulk upload" state={componentUpload.state} percent={componentUpload.percent} summary={componentUpload.summary} onClose={() => setComponentUpload(current => ({ ...current, open: false }))} />
    <BulkUploadProgressModal open={salaryTemplateUpload.open} title="Salary template bulk upload" state={salaryTemplateUpload.state} percent={salaryTemplateUpload.percent} summary={salaryTemplateUpload.summary} onClose={() => setSalaryTemplateUpload(current => ({ ...current, open: false }))} />
    <BulkUploadProgressModal open={billingUpload.open} title="Client billing bulk upload" state={billingUpload.state} percent={billingUpload.percent} summary={billingUpload.summary} onClose={() => setBillingUpload(current => ({ ...current, open: false }))} />
    <BulkUploadPreviewModal preview={bulkPreview} importing={bulkPreviewImporting} onCancel={() => { setBulkPreview(emptyBulkUploadPreview); setBulkPreviewConfirm(null) }} onConfirm={preview => void confirmBulkPreview(preview)} />
    {renderComponentDrawer()}
  </form>
}

function InfoField(p: { label: string; help?: string; wide?: boolean; children: ReactNode }) {
  return <div className={`info-field ${p.wide ? 'wide' : ''}`}><span className="field-label">{p.label}{p.help && <HelpTip text={p.help} />}</span>{p.children}</div>
}

function HelpTip({ text }: { text: string }) {
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null)
  const open = (target: HTMLElement) => { const box = target.getBoundingClientRect(); setPos({ top: box.bottom + 8, left: Math.min(Math.max(12, box.left + box.width / 2), window.innerWidth - 12) }) }
  return <>
    <span className="field-help" tabIndex={0} aria-label={text} onMouseEnter={event => open(event.currentTarget)} onMouseLeave={() => setPos(null)} onFocus={event => open(event.currentTarget)} onBlur={() => setPos(null)}>?</span>
    {pos && createPortal(<small className="field-help-popover" style={{ top: pos.top, left: pos.left }}>{text}</small>, document.body)}
  </>
}

function CitySelectWithAdd(p: { value: string; stateName: string; options: string[]; onChange: (value: string) => void; onAddCity: (city: string) => Promise<boolean> }) {
  const [city, setCity] = useState(''), [open, setOpen] = useState(false), [saving, setSaving] = useState(false)
  const addCity = async () => { setSaving(true); const ok = await p.onAddCity(city); setSaving(false); if (ok) { setCity(''); setOpen(false) } }
  return <>
    <AntSelect className="app-search-select" popupClassName="app-search-select-dropdown" showSearch value={p.value || ''} optionFilterProp="label" filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())} onChange={value => p.onChange(String(value))} options={[{ value: '', label: 'Select' }, ...unique([...p.options, p.value]).map(item => ({ value: item, label: item }))]} dropdownRender={menu => <>{menu}<Divider /><Button type="link" disabled={!p.stateName} onClick={() => setOpen(true)}>+ Add city</Button></>} />
    <Modal title="Add city" open={open} okText="Save city" confirmLoading={saving} okButtonProps={{ disabled: !city.trim() }} onOk={() => void addCity()} onCancel={() => { setOpen(false); setCity('') }}><Form component={false} layout="vertical"><Form.Item label="State"><Input value={p.stateName} disabled /></Form.Item><Form.Item label="City name" required><Input autoFocus value={city} onChange={event => setCity(event.target.value)} onPressEnter={() => void addCity()} placeholder="Enter city name" /></Form.Item></Form></Modal>
  </>
}

function WorkWeekMasterFields({ drop, setDrop }: { drop: Drop; setDrop: (drop: Drop) => void }) {
  const config = parseWorkWeekConfig(drop)
  const weeklyOffDays = weekDayOptions.filter(day => !config.workingDays.includes(day.value)).map(day => day.value)
  const update = (patch: Partial<WorkWeekConfig>) => {
    const next = normalizeWorkWeekConfig({ ...config, ...patch })
    setDrop({ ...drop, ...workWeekPayload(next) })
  }
  const toggleWeeklyOff = (day: number) => {
    const workingDays = config.workingDays.includes(day) ? config.workingDays.filter(item => item !== day) : [...config.workingDays, day]
    update({ workingDays, offSaturdays: day === 6 && !workingDays.includes(6) ? [] : config.offSaturdays })
  }
  const toggleSaturday = (occurrence: number) => {
    const offSaturdays = config.offSaturdays.includes(occurrence) ? config.offSaturdays.filter(item => item !== occurrence) : [...config.offSaturdays, occurrence]
    update({ offSaturdays })
  }

  return <>
    <Form.Item label="Pattern name" required><Input value={drop.value} readOnly /></Form.Item>
    <Form.Item label="Weekly off days" required>
      <Space wrap>{weekDayOptions.map(day => <AntCheckbox key={day.value} checked={weeklyOffDays.includes(day.value)} onChange={() => toggleWeeklyOff(day.value)}>{day.label}</AntCheckbox>)}</Space>
    </Form.Item>
    {config.workingDays.includes(6) && <Form.Item label="Extra Saturday off in every month">
      <Space wrap>{saturdayOptions.map(option => <AntCheckbox key={option.value} checked={config.offSaturdays.includes(option.value)} onChange={() => toggleSaturday(option.value)}>{option.label}</AntCheckbox>)}</Space>
    </Form.Item>}
    <Form.Item label="System config JSON" extra="Generated from the checkboxes above and saved in Dropdown Masters. Reports and attendance review use this config when available.">
      <Input.TextArea value={drop.configJson || JSON.stringify(config)} readOnly autoSize={{ minRows: 2, maxRows: 3 }} />
    </Form.Item>
  </>
}

function validateComponent(component: Component, category: string, setup: Setup) {
  const errors: string[] = []
  const calcType = normalizeCalculationType(component.calculationType)
  if (!component.code.trim()) errors.push('Component code is required.')
  if (component.code.trim() && !/^[A-Z0-9_]+$/i.test(component.code.trim())) errors.push('Component code can use only letters, numbers and underscore.')
  if (!component.name.trim()) errors.push('Component name is required.')
  if (calcType === 'Fixed Amount' && !String(component.value).trim()) errors.push('Monthly amount is required for Fixed Amount components.')
  if (calcType === 'Formula' && !component.formula.trim()) errors.push('Formula is required when calculation type is Formula.')
  if (calcType === 'Slab Based' && !component.formula.trim() && !component.value.trim()) errors.push('Slab rules are required for Slab Based components.')
  if (calcType === 'Formula') {
    const formulaError = validateFormula(component.formula, component, setup)
    if (formulaError) errors.push(formulaError)
  }
  if ((component.componentRole === 'Statutory Deduction' || component.componentRole === 'Employer Contribution') && (!component.statutoryType || component.statutoryType === 'None')) errors.push('Select statutory type for statutory or employer contribution components.')
  if (category === 'Benefit' && component.taxable && !component.investmentType.trim()) errors.push('For taxable benefits, add investment/tax classification to guide payroll reports.')
  return errors
}

function validateFormula(formula: string, component: Component, setup: Setup) {
  const text = formula.trim().replace(/×/g, '*').replace(/÷/g, '/')
  if (!text) return ''
  if (/[^A-Z0-9_+\-*/().,%\s×÷]/i.test(text)) return 'Formula has unsupported characters. Use component codes, numbers, + - * /, %, MIN/MAX style text only.'
  let depth = 0
  for (const char of text) {
    if (char === '(') depth += 1
    if (char === ')') depth -= 1
    if (depth < 0) return 'Formula brackets are not balanced.'
  }
  if (depth !== 0) return 'Formula brackets are not balanced.'
  const currentCode = component.code.trim().toUpperCase()
  const currentPriority = Number(component.priority || 999)
  const componentsByCode = new Map(setup.salaryComponents.filter(item => item.code).map(item => [item.code.trim().toUpperCase(), item]))
  const references = Array.from(new Set((text.toUpperCase().match(/\b[A-Z_][A-Z0-9_]*\b/g) ?? []).filter(token => !formulaReservedWords.has(token))))
  const selfReference = references.find(token => token === currentCode)
  if (selfReference) return `Formula cannot reference itself (${selfReference}).`
  const unknown = references.find(token => !componentsByCode.has(token))
  if (unknown) return `${unknown} is not a valid salary component code. Use component chips to avoid spelling mistakes.`
  const inactive = references.map(token => componentsByCode.get(token)).find(item => item && !item.active)
  if (inactive) return `${inactive.code} is inactive. Activate it before using it in a formula.`
  const lateDependency = references.map(token => componentsByCode.get(token)).find(item => item && Number(item.priority || 999) >= currentPriority)
  if (lateDependency) return `${lateDependency.code} must have lower priority than ${currentCode || 'this component'} so payroll can calculate it first.`
  return ''
}

function componentUsed(id: number, setup: Setup) {
  return setup.salaryStructures.some(structure => structure.lines.some(line => Number(line.componentId) === id))
}
