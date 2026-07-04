import { useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, Modal, Row, Select as AntSelect, Space } from 'antd'
import DataTable from '../components/DataTable'
import FileDropZone from '../components/FileDropZone'
import { Card, Chk, F, Sel } from '../components/FormPrimitives'
import PageTabs from '../components/PageTabs'
import SalaryTemplateDesigner from '../components/SalaryTemplateDesigner'
import TaxEngineManager from '../components/TaxEngineManager'
import { useToast, type ToastType } from '../components/ToastProvider'
import { client0, component0, drop0, dropTypes, location0, org0, payslip0, settingsMenus, setup0, structure0, workWeekOptions } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { getAttendanceGroups } from '../services/leaveAttendanceService'
import { getClientBillingConfigurations, getClientBillingModule, getDropdowns, getOrganization, getSetup, getWorkLocations, saveClient as persistClient, saveClientBillingConfiguration, saveClientBillingModule, saveDropdown, saveOrganization, saveSetup, saveWorkLocation } from '../services/settingsService'
import type { AttendanceGroup, Client, ClientBillingConfiguration, ClientBillingRateCardType, ClientBillingRateType, Component, Drop, Employee, Org, ProfessionalTaxSlab, Setup, WorkLocation } from '../types/payroll'
import { money } from '../utils/salary'

type SettingsTab = (typeof settingsMenus)[number]
type OrganizationTab = 'Organization' | 'Tax' | 'EPF' | 'ESI' | 'Professional Tax' | 'Labour Welfare Fund'
const organizationTabs = ['Organization', 'Tax', 'EPF', 'ESI', 'Professional Tax', 'Labour Welfare Fund'] as const
const statutoryTabs = ['Income Tax Rules', 'Professional Tax'] as const
type StatutoryTab = (typeof statutoryTabs)[number]
const componentTabs = ['Earning', 'Deduction', 'Reimbursement', 'Benefit', 'Correction'] as const
type ComponentCategory = (typeof componentTabs)[number]
const ptSlab0: ProfessionalTaxSlab = { id: 0, state: '', salaryFrom: '0', salaryTo: '', deductionAmount: '', effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: '', gender: 'All', notes: '', active: true }
const calculationOptions = ['Fixed Amount', 'Formula', 'Residual / Balancing', 'Manual / Variable', 'Slab Based']
const formulaChips = ['GROSS', 'CTC', 'MONTHLY_CTC', 'PAYROLL_DAYS', 'PAYABLE_DAYS', 'MIN()', 'MAX()', 'ROUND()', 'ROUNDDOWN()', 'ROUNDUP()']
const formulaReservedWords = new Set(['GROSS', 'CTC', 'MONTHLY_CTC', 'ANNUAL_CTC', 'PAYROLL_DAYS', 'TOTAL_DAYS', 'WORKING_DAYS', 'PAYABLE_DAYS', 'PRESENT_DAYS', 'LOP_DAYS', 'GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST', 'MIN', 'MAX', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'SUM', 'FIXED', 'EARNINGS', 'EARNINGS_BEFORE_THIS', 'OF'])
const settingsSetup0: Setup = setup0
const billingRateCardTypes: ClientBillingRateCardType[] = ['All', 'Service Charge', 'Reimbursement', 'Bonus', 'Statutory Compliance Charges']
const billingRateTypes: ClientBillingRateType[] = ['Percentage', 'Fixed']
const billing0: ClientBillingConfiguration = { id: 0, clientId: 0, clientName: '', workLocationId: null, workLocationName: '', rateCardType: 'All', rateType: 'Percentage', value: 0, taxInclusive: false, gstRatePercent: 18, effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: null, isActive: true }
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
  return { ...row, calculationType, formula, payType: calculationType === 'Manual / Variable' ? 'Variable Pay' : row.payType }
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
type WorkWeekConfig = { workingDays: number[]; offSaturdays: number[] }
const weekDayOptions = [{ value: 0, label: 'Sun' }, { value: 1, label: 'Mon' }, { value: 2, label: 'Tue' }, { value: 3, label: 'Wed' }, { value: 4, label: 'Thu' }, { value: 5, label: 'Fri' }, { value: 6, label: 'Sat' }]
const saturdayOptions = [{ value: 1, label: '1st' }, { value: 2, label: '2nd' }, { value: 3, label: '3rd' }, { value: 4, label: '4th' }, { value: 5, label: '5th' }]
const defaultWorkWeekConfig: WorkWeekConfig = { workingDays: [1, 2, 3, 4, 5], offSaturdays: [] }
const workWeekPresetConfigs: Record<string, WorkWeekConfig> = {
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

export default function SettingsPage({ tab, onMessage }: { tab: SettingsTab; onMessage: (message: string) => void }) {
  const toast = useToast()
  const [org, setOrg] = useState(org0), [setup, setSetup] = useState(settingsSetup0), [clients, setClients] = useState<Client[]>([]), [client, setClient] = useState(client0)
  const [locations, setLocations] = useState<WorkLocation[]>([]), [location, setLocation] = useState(location0), [drops, setDrops] = useState<Drop[]>([]), [drop, setDrop] = useState(drop0)
  const [employees, setEmployees] = useState<Employee[]>([]), [attendanceGroups, setAttendanceGroups] = useState<AttendanceGroup[]>([])
  const [component, setComponent] = useState(component0), [structure, setStructure] = useState(structure0), [payslip, setPayslip] = useState(payslip0), [componentTab, setComponentTab] = useState<ComponentCategory>('Earning')
  const [billingEnabled, setBillingEnabled] = useState(false), [billingRows, setBillingRows] = useState<ClientBillingConfiguration[]>([]), [billingRow, setBillingRow] = useState<ClientBillingConfiguration>(billing0), [billingDrawerOpen, setBillingDrawerOpen] = useState(false)
  const [organizationTab, setOrganizationTab] = useState<OrganizationTab>('Organization')
  const [statutoryTab, setStatutoryTab] = useState<StatutoryTab>('Income Tax Rules')
  const [ptSlab, setPtSlab] = useState<ProfessionalTaxSlab>(ptSlab0)
  const [componentDrawerOpen, setComponentDrawerOpen] = useState(false)
  const [clientDrawerOpen, setClientDrawerOpen] = useState(false)
  const [locationDrawerOpen, setLocationDrawerOpen] = useState(false)
  const [dropDrawerOpen, setDropDrawerOpen] = useState(false)
  const [componentSaving, setComponentSaving] = useState(false)
  const [templateSaving, setTemplateSaving] = useState(false)
  const [payslipSaving, setPayslipSaving] = useState(false)
  const [saving, setSaving] = useState(false)
  const [dropState, setDropState] = useState('')
  const isErrorMessage = (message: string) => /error|unable|failed|required|resolve|select|invalid|must|cannot|some/i.test(message)

  const load = async () => {
    const [organization, rawSetup, clientRows, locationRows, dropdownRows, employeeRows, groupRows, billingModule, billingConfigs] = await Promise.all([getOrganization(org0), getSetup(settingsSetup0), getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getAttendanceGroups(), getClientBillingModule(), getClientBillingConfigurations()])
    setOrg({ ...org0, ...organization, pan: organization.pan || rawSetup.tax?.pan || '', tanNumber: organization.tanNumber || rawSetup.tax?.tan || '', professionalTaxNumber: organization.professionalTaxNumber || rawSetup.statutory?.ptNumber || '' })
    setSetup({ ...settingsSetup0, ...rawSetup, tax: { ...setup0.tax, ...rawSetup.tax, clientSettings: rawSetup.tax?.clientSettings ?? setup0.tax.clientSettings, slabs: rawSetup.tax?.slabs ?? setup0.tax.slabs, surcharges: rawSetup.tax?.surcharges ?? setup0.tax.surcharges, finalAdjustments: rawSetup.tax?.finalAdjustments ?? setup0.tax.finalAdjustments, declarationSections: rawSetup.tax?.declarationSections ?? setup0.tax.declarationSections }, schedule: { ...setup0.schedule, ...rawSetup.schedule }, statutory: { ...setup0.statutory, ...rawSetup.statutory }, salaryComponents: (rawSetup.salaryComponents ?? []).map(normalizeComponentForUi), salaryStructures: rawSetup.salaryStructures ?? [], payslipTemplates: rawSetup.payslipTemplates ?? [] })
    setClients(clientRows)
    setLocations(locationRows.filter(location => location.isActive && clientRows.some(client => client.id === location.clientId)).map(location => ({ ...location0, ...location })))
    setDrops(dropdownRows.map(item => ({ ...drop0, ...item })))
    setEmployees(employeeRows)
    setAttendanceGroups(groupRows)
    setBillingEnabled(billingModule.isEnabled)
    setBillingRows(billingConfigs.map(row => ({ ...billing0, ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), effectiveTo: row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : null })))
  }

  useEffect(() => { void load() }, [])
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
  const saveDrop = async () => {
    const actualType = drop.type === 'City' ? dropState ? cityType(dropState) : '' : drop.type
    if (!actualType || !drop.value.trim()) return notify(drop.type === 'City' ? 'Select a state and city for the city master.' : 'Dropdown value is required.', 'error')
    const value = drop.value.trim()
    if (actualType === 'Work Week' && !parseWorkWeekConfig(drop).workingDays.length) return notify('Select at least one working day.', 'error')
    const clientId = actualType === 'Employee Grade' ? Number(drop.clientId || 0) : 0
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
  const saveComponent = async () => { const rowForUi = normalizeComponentForUi(component); const errors = validateComponent(rowForUi, componentTab, setup); if (errors.length) return notify(errors[0], 'error'); const isUpdate = Boolean(rowForUi.id), locked = rowForUi.id && componentUsed(rowForUi.id, setup); const row = { ...rowForUi, category: locked ? rowForUi.category : componentTab, id: rowForUi.id || Date.now(), code: locked ? rowForUi.code : rowForUi.code.trim().toUpperCase() }; const nextSetup = { ...setup, salaryComponents: [...setup.salaryComponents.filter(item => item.id !== row.id), row] }; if (await persistComponentSetup(nextSetup, isUpdate ? 'Salary component updated successfully.' : 'Salary component added successfully.')) { setComponent(normalizeComponentForUi({ ...component0, category: componentTab })); setComponentDrawerOpen(false) } }
  const editComponent = (row: Component) => { if (componentTabs.includes(row.category as ComponentCategory)) setComponentTab(row.category as ComponentCategory); setComponent(normalizeComponentForUi(row)); setComponentDrawerOpen(true) }
  const deleteComponent = async (row: Component) => { if (blockDeleteIfLinked(row.name || row.code, componentDeleteLinks(row))) return; if (!window.confirm(`Delete ${row.name || row.code}?`)) return; await persistComponentSetup({ ...setup, salaryComponents: setup.salaryComponents.filter(item => item.id !== row.id) }, 'Salary component deleted successfully.'); if (component.id === row.id) { setComponent({ ...component0, category: componentTab }); setComponentDrawerOpen(false) } }
  const toggleBillingModule = async (enabled: boolean) => {
    const response = await saveClientBillingModule({ isEnabled: enabled })
    notify(response.ok ? enabled ? 'Client billing configuration enabled.' : 'Client billing configuration disabled.' : response.error || 'Unable to update billing module.', response.ok ? 'success' : 'error')
    if (response.ok) setBillingEnabled(enabled)
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
  const saveStructure = async () => {
    if (!structure.name.trim()) return notify('Template name is required.', 'error')
    const row = { ...structure, clientId: refId(structure.clientId), id: structure.id || Date.now() }
    const nextSetup = { ...setup, salaryStructures: [...setup.salaryStructures.filter(item => item.id !== row.id), row] }
    setTemplateSaving(true)
    const response = await saveSetup(nextSetup, { toast: false })
    setTemplateSaving(false)
    if (!response.ok) return notify(response.error || 'Unable to save salary template.', 'error')
    setSetup(nextSetup)
    setStructure(structure0)
    notify(row.id === structure.id ? 'Salary template updated.' : 'Salary template saved.')
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
    if (organizationTab === 'Organization') return <Row gutter={[16, 16]}><Col xs={24} lg={6}><AntCard title="Logo" size="small" className="settings-compact-panel organization-logo-panel"><FileDropZone accept="image/png,image/jpeg,image/svg+xml,image/webp" title="Drop logo here or browse" hint="PNG, JPG, SVG or WebP for payslips and documents." onFile={uploadLogo} preview={org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo preview" /> : <b>No logo</b>} />{org.logoDataUrl && <Button block onClick={() => o('logoDataUrl', '')}>Remove logo</Button>}</AntCard></Col><Col xs={24} lg={6}><AntCard title="Basic details" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Form.Item label="Name" required><Input value={org.name} onChange={event => o('name', event.target.value)} /></Form.Item><Form.Item label="Legal name"><Input value={org.legalName} onChange={event => o('legalName', event.target.value)} /></Form.Item><Form.Item label="Industry"><Input value={org.industry} onChange={event => o('industry', event.target.value)} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={6}><AntCard title="Tax identity" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Form.Item label="GST Number"><Input value={org.gstin} onChange={event => o('gstin', event.target.value.toUpperCase())} /></Form.Item><Form.Item label="TAN Number"><Input value={org.tanNumber} onChange={event => o('tanNumber', event.target.value.toUpperCase())} /></Form.Item><Form.Item label="PAN"><Input value={org.pan} onChange={event => o('pan', event.target.value.toUpperCase())} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={6}><AntCard title="Address" size="small" className="settings-compact-panel"><Form component={false} layout="vertical"><Form.Item label="Address" required><Input value={org.addressLine1} onChange={event => o('addressLine1', event.target.value)} /></Form.Item><Row gutter={12}><Col span={12}><Form.Item label="State"><Sel v={org.state} set={value => o('state', value)} a={stateOptions} /></Form.Item></Col><Col span={12}><Form.Item label="City" required><Input value={org.city} onChange={event => o('city', event.target.value)} /></Form.Item></Col></Row><Form.Item label="PIN" required><Input value={org.postalCode} onChange={event => o('postalCode', event.target.value.replace(/\D/g, '').slice(0, 6))} /></Form.Item></Form></AntCard></Col><Col xs={24} lg={12}><AntCard title="Registered Office Address" size="small" className="settings-compact-panel"><Input.TextArea rows={3} value={org.registeredOfficeAddress} onChange={event => o('registeredOfficeAddress', event.target.value)} /></AntCard></Col><Col xs={24} lg={12}><AntCard title="Corporate Office Address" size="small" className="settings-compact-panel"><Input.TextArea rows={3} value={org.corporateOfficeAddress} onChange={event => o('corporateOfficeAddress', event.target.value)} /></AntCard></Col></Row>
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
  const renderClientBilling = () => {
    const activeBillingRows = billingRows.filter(row => row.isActive)
    const locationOptions = billingRow.clientId ? locations.filter(item => item.clientId === billingRow.clientId) : []
    const setBilling = <K extends keyof ClientBillingConfiguration>(key: K, value: ClientBillingConfiguration[K]) => setBillingRow(current => ({ ...current, [key]: value }))
    return <AntCard title="Client Billing Configuration" size="small" className="settings-panel settings-table-panel">
      <div className="component-table-head"><div><b>Billing rate cards</b><span>Effective-dated client and work-location billing rules.</span></div><Space><AntCheckbox checked={billingEnabled} onChange={event => void toggleBillingModule(event.target.checked)}>Enable module</AntCheckbox><Button type="primary" disabled={!billingEnabled} onClick={openBillingDrawer}>Add configuration</Button></Space></div>
      {billingEnabled ? <DataTable rows={activeBillingRows} emptyText="No billing configurations added." exportFileName="client-billing-configurations" actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => editBilling(row)}>Edit</Button><Button size="small" danger onClick={() => void deleteBilling(row)}>Delete</Button></Space>} columns={[{ key: 'clientName', label: 'Client' }, { key: 'workLocationName', label: 'Work location', value: row => row.workLocationName || 'All locations' }, { key: 'rateCardType', label: 'Rate card' }, { key: 'rateType', label: 'Rate type' }, { key: 'value', label: 'Value', value: row => row.rateType === 'Percentage' ? `${row.value}%` : money(row.value) }, { key: 'taxInclusive', label: 'Tax', value: row => row.taxInclusive ? 'Inclusive' : 'Excluding' }, { key: 'gstRatePercent', label: 'GST %', value: row => `${row.gstRatePercent ?? 0}%` }, { key: 'effectiveFrom', label: 'From', value: row => String(row.effectiveFrom).slice(0, 10) }, { key: 'effectiveTo', label: 'To', value: row => row.effectiveTo ? String(row.effectiveTo).slice(0, 10) : 'Open' }]} /> : <p className="component-guide"><b>Module disabled</b><span>Enable the module to maintain client billing rate cards.</span></p>}
      {billingDrawerOpen && <div className="component-drawer-backdrop" onClick={() => setBillingDrawerOpen(false)}><aside className="component-drawer" role="dialog" aria-modal="true" aria-label={billingRow.id ? 'Edit billing configuration' : 'Add billing configuration'} onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">Billing</span><h3>{billingRow.id ? 'Edit configuration' : 'Add configuration'}</h3><p>Define client billing rate and tax basis for an effective period.</p></div><button type="button" aria-label="Close billing drawer" onClick={() => setBillingDrawerOpen(false)}>x</button></header><div className="component-drawer-form"><InfoField label="Client" help="Billing rules are maintained client-wise."><Sel v={billingRow.clientId || ''} set={value => setBillingRow(current => ({ ...current, clientId: Number(refId(value) || 0), workLocationId: null }))} a={clients.map(item => `${item.id}:${item.name}`)} /></InfoField><InfoField label="Work location" help="Leave blank to apply to all work locations of the client."><Sel v={billingRow.workLocationId || ''} set={value => setBilling('workLocationId', Number(refId(value) || 0) || null)} a={['0:All locations', ...locationOptions.map(item => `${item.id}:${item.name}`)]} /></InfoField><InfoField label="Rate card type" help="Classifies the billing component."><Sel v={billingRow.rateCardType} set={value => setBilling('rateCardType', value as ClientBillingRateCardType)} a={billingRateCardTypes} /></InfoField><InfoField label="Rate type" help="Percentage applies on billing base; fixed is an absolute amount."><Sel v={billingRow.rateType} set={value => setBilling('rateType', value as ClientBillingRateType)} a={billingRateTypes} /></InfoField><InfoField label="Value" help="Enter percentage or fixed value based on rate type."><Input type="number" min={0} step="0.01" value={billingRow.value} onChange={event => setBilling('value', Number(event.target.value || 0))} /></InfoField><InfoField label="Tax basis" help="Choose whether entered value includes tax."><Sel v={billingRow.taxInclusive ? 'Inclusive' : 'Excluding'} set={value => setBilling('taxInclusive', value === 'Inclusive')} a={['Inclusive', 'Excluding']} /></InfoField><InfoField label="GST rate %" help="GST percentage used by Client Billing Report for this billing rule."><Input type="number" min={0} max={100} step="0.01" value={billingRow.gstRatePercent} onChange={event => setBilling('gstRatePercent', Number(event.target.value || 0))} /></InfoField><InfoField label="Effective from" help="First date from which this billing rule applies."><Input type="date" value={billingRow.effectiveFrom} onChange={event => setBilling('effectiveFrom', event.target.value)} /></InfoField><InfoField label="Effective to" help="Optional expiry date."><Input type="date" value={billingRow.effectiveTo || ''} onChange={event => setBilling('effectiveTo', event.target.value || null)} /></InfoField><InfoField label="Status" help="Inactive rows are kept for reference but ignored by active billing setup."><AntCheckbox checked={billingRow.isActive} onChange={event => setBilling('isActive', event.target.checked)}>Active</AntCheckbox></InfoField></div><footer><button type="button" className="secondary" onClick={() => setBillingDrawerOpen(false)}>Cancel</button><button type="button" onClick={() => void saveBilling()}>{billingRow.id ? 'Update configuration' : 'Save configuration'}</button></footer></aside></div>}
    </AntCard>
  }
  const componentTypeOptions = componentTab === 'Earning' ? ['Basic', 'House Rent Allowance', 'Custom Allowance', 'Bonus', 'Commission'] : componentTab === 'Deduction' ? ['NPS', 'VPF', 'Non-Taxable Deduction', 'One-time Deduction', 'Recurring Deduction'] : componentTab === 'Benefit' ? ['Employer NPS', 'Insurance Benefit', 'Meal Benefit', 'Car Benefit', 'Custom Benefit'] : componentTab === 'Correction' ? ['Earning Correction', 'Deduction Correction', 'Reversal', 'Arrear Correction', 'Custom Correction'] : ['Fuel', 'Telephone', 'Internet', 'Books', 'Custom Reimbursement']
  const componentRows = setup.salaryComponents.filter(item => item.category === componentTab)
  const renderComponentDrawer = () => {
    if (!componentDrawerOpen) return null
    const calcType = normalizeCalculationType(component.calculationType)
    const setCalcType = (value: string) => setComponent({ ...component, calculationType: value, payType: value === 'Manual / Variable' ? 'Variable Pay' : component.payType })
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
        <InfoField label="Pay type" help="Fixed pay is part of regular monthly salary. Variable pay is usually event-based or manually adjusted."><Sel v={component.payType} set={value => setComponent({ ...component, payType: value })} a={['Fixed Pay', 'Variable Pay']} /></InfoField>
        <InfoField label="Calculation" help="Choose the natural behavior. Formula covers percentage of CTC/component, so those shortcuts are no longer separate options."><Sel v={calcType} set={setCalcType} a={calculationOptions} /></InfoField>
        {calcType === 'Fixed Amount' && <InfoField label="Monthly amount" help="Fixed monthly value before attendance pro-rata. Example: 2000."><input value={component.value} onChange={event => setComponent({ ...component, value: event.target.value.replace(/[^\d.-]/g, '') })} placeholder="2000" /></InfoField>}
        {calcType === 'Formula' && <InfoField label="Formula" wide help="Use component codes and generic payroll tokens. Example: GROSS * 50%, BASIC * 40%, ROUNDDOWN(BASIC * 8.33%)."><div className="formula-builder"><div className="formula-chip-group"><span>Dependent components</span><AntSelect className="formula-component-select" popupClassName="formula-component-dropdown" mode="multiple" showSearch allowClear placeholder="Search & select components" value={selectedFormulaCodes} options={formulaComponentOptions} filterOption={(input, option) => String(option?.label ?? option?.value ?? '').toLowerCase().includes(input.toLowerCase())} onSelect={value => addFormulaToken(String(value))} onDeselect={value => removeFormulaToken(String(value))} onClear={() => selectedFormulaCodes.forEach(removeFormulaToken)} /></div><textarea value={component.formula} onChange={event => setComponent({ ...component, formula: event.target.value })} rows={3} placeholder="GROSS * 50%" /><div className="formula-chip-group"><span>Tokens</span><div className="formula-chips">{formulaChips.map(token => <button type="button" key={token} onClick={() => addFormulaToken(token)}>{token}</button>)}</div></div>{formulaError && <p className="inline-error">{formulaError}</p>}</div></InfoField>}
        {calcType === 'Residual / Balancing' && <InfoField label="Balance target" help="Usually GROSS or CTC. Payroll subtracts already calculated earnings before this component."><input value={component.baseComponent || 'GROSS'} onChange={event => setComponent({ ...component, baseComponent: event.target.value.toUpperCase() })} placeholder="GROSS" /></InfoField>}
        {calcType === 'Slab Based' && <InfoField label="Slab rules" wide help="Use semicolon slabs like 0-15000:0;15001+:200."><textarea value={component.formula || component.value} onChange={event => setComponent({ ...component, formula: event.target.value })} rows={3} placeholder="0-15000:0;15001+:200" /></InfoField>}
        {calcType === 'Manual / Variable' && <div className="component-drawer-note">Value will come from payroll adjustment/import/manual entry. No formula or fixed amount is required.</div>}
        {componentTab !== 'Reimbursement' && <InfoField label="EPF treatment" help="Controls whether this component contributes to PF wage calculations and employer/employee deductions."><Sel v={component.epf} set={value => setComponent({ ...component, epf: value })} a={['Never', 'Always', 'Only if employee is PF eligible']} /></InfoField>}
        <InfoField label="Investment type" help="Optional tax classification such as 80C or 80CCD. It helps reporting and tax projection later."><input value={component.investmentType} onChange={event => setComponent({ ...component, investmentType: event.target.value })} placeholder="80C / 80CCD / Other" /></InfoField>
        <InfoField label="Priority" help="Controls calculation and display order. Lower numbers calculate earlier. Put residual/balancing after normal earnings."><input value={component.priority} onChange={event => setComponent({ ...component, priority: event.target.value.replace(/\D/g, '') })} /></InfoField>
        <div className="component-drawer-checks"><Chk l="Taxable" v={component.taxable} set={value => setComponent({ ...component, taxable: value })} /><small>Includes this amount in taxable salary and tax reports.</small><Chk l="Part of CTC" v={component.ctc} set={value => setComponent({ ...component, ctc: value })} /><small>Counts this amount in annual CTC totals.</small><Chk l="Pro-rata" v={component.proRata} set={value => setComponent({ ...component, proRata: value })} /><small>Reduces or adjusts the amount for partial attendance/pay days.</small><Chk l="FBP" v={component.fbp} set={value => setComponent({ ...component, fbp: value })} /><small>Marks this component as flexible benefit plan eligible.</small><Chk l="Restrict FBP override" v={component.restrictFbp} set={value => setComponent({ ...component, restrictFbp: value })} /><small>Prevents ad hoc changes after FBP selection is locked.</small><Chk l="ESI wages" v={component.esi} set={value => setComponent({ ...component, esi: value })} /><small>Includes this amount in ESI wage eligibility and contribution calculation.</small><Chk l="Recurring" v={component.recurring} set={value => setComponent({ ...component, recurring: value })} /><small>Runs every payroll cycle unless changed in employee salary.</small><Chk l="Scheduled earning" v={component.scheduled} set={value => setComponent({ ...component, scheduled: value })} /><small>Used for planned earnings such as bonus or future-dated payments.</small><Chk l="Active" v={component.active} set={value => setComponent({ ...component, active: value })} /><small>Inactive components stay saved but are hidden from new salary templates.</small></div>
      </div>
      <footer><button type="button" disabled={componentSaving} onClick={() => void saveComponent()}>{componentSaving ? 'Saving...' : component.id ? 'Update component' : `Add ${componentTab}`}</button></footer>
    </aside>
  </div>
  }

  return <form onSubmit={saveAll}>
    {tab === 'Organization' && <AntCard title="Organization" size="small" className="settings-panel"><PageTabs items={organizationTabs} value={organizationTab} onChange={setOrganizationTab} label="Organization sections" />{renderOrganizationBody()}</AntCard>}
    {tab === 'Clients' && <><AntCard title="Clients" size="small" className="settings-panel settings-table-panel"><div className="component-table-head"><div><b>Client master</b><span>Maintain client accounts used across payroll, attendance, billing, and reports.</span></div><Button type="primary" onClick={() => { setClient(client0); setClientDrawerOpen(true) }}>Add client</Button></div><DataTable rows={clients.filter(item => item.isActive)} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => { setClient(row); setClientDrawerOpen(true) }}>Edit</Button><Button size="small" danger onClick={() => void deleteClient(row)}>Delete</Button></Space>} columns={[{ key: 'name', label: 'Client' }, { key: 'code', label: 'Code' }, { key: 'contactPerson', label: 'Contact' }, { key: 'email', label: 'Email' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} /></AntCard><Drawer className="settings-master-drawer" title={client.id ? 'Edit client' : 'Add client'} open={clientDrawerOpen} width={440} onClose={() => { setClientDrawerOpen(false); setClient(client0) }} destroyOnClose><Form component={false} layout="vertical" className="settings-quick-form"><Form.Item label="Client name" required><Input value={client.name} onChange={event => setClient({ ...client, name: event.target.value })} /></Form.Item><Form.Item label="Code"><Input value={client.code} onChange={event => setClient({ ...client, code: event.target.value })} /></Form.Item><Form.Item label="Contact"><Input value={client.contactPerson} onChange={event => setClient({ ...client, contactPerson: event.target.value })} /></Form.Item><Form.Item label="Email"><Input value={client.email} onChange={event => setClient({ ...client, email: event.target.value })} /></Form.Item><Form.Item label="Phone"><Input value={client.phone} onChange={event => setClient({ ...client, phone: event.target.value })} /></Form.Item><Form.Item label="Address"><Input value={client.address} onChange={event => setClient({ ...client, address: event.target.value })} /></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => setClient(client0)}>Reset</Button><Button type="primary" onClick={saveClient}>{client.id ? 'Update client' : 'Add client'}</Button></Space></Row></Form></Drawer></>}
    {tab === 'Work Locations' && <><AntCard title="Work locations" size="small" className="settings-panel settings-table-panel"><div className="component-table-head"><div><b>Work-location master</b><span>Maintain operating locations, state/city, GST, and primary-location flags.</span></div><Button type="primary" onClick={() => { setLocation(location0); setLocationDrawerOpen(true) }}>Add work location</Button></div><DataTable rows={locations.filter(item => item.isActive)} columns={[{ key: 'clientName', label: 'Client', value: row => row.clientName || clients.find(item => item.id === row.clientId)?.name || '-' }, { key: 'name', label: 'Location' }, { key: 'city', label: 'City' }, { key: 'state', label: 'State' }, { key: 'postalCode', label: 'PIN' }, { key: 'gstin', label: 'GST Number' }, { key: 'isPrimary', label: 'Primary', render: item => item.isPrimary ? 'Yes' : 'No' }]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => { setLocation(row); setLocationDrawerOpen(true) }}>Edit</Button><Button size="small" danger onClick={() => void deleteLocation(row)}>Delete</Button></Space>} /></AntCard><Drawer className="settings-master-drawer" title={location.id ? 'Edit work location' : 'Add work location'} open={locationDrawerOpen} width={520} onClose={() => { setLocationDrawerOpen(false); setLocation(location0) }} destroyOnClose><Form component={false} layout="vertical" className="settings-quick-form"><Form.Item label="Client" required><Sel v={location.clientId || ''} set={applyLocationClient} a={clients.map(item => `${item.id}:${item.name}`)} /></Form.Item><Form.Item label="Location name" required><Input value={location.name} onChange={event => setLocation({ ...location, name: event.target.value })} placeholder="Head Office / WFH - Employee Name" /></Form.Item><Row gutter={12}><Col xs={24} md={12}><Form.Item label="State"><Sel v={location.state} set={value => setLocation({ ...location, state: value, city: '' })} a={stateOptions} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="City"><CitySelectWithAdd value={location.city} stateName={location.state} options={cityOptions(location.state)} onChange={value => setLocation({ ...location, city: value })} onAddCity={addCityForSelectedState} /></Form.Item></Col></Row><Row gutter={12}><Col xs={24} md={12}><Form.Item label="PIN code"><Input value={location.postalCode} onChange={event => setLocation({ ...location, postalCode: event.target.value.replace(/\D/g, '').slice(0, 6) })} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="GST Number"><Input value={location.gstin} onChange={event => setLocation({ ...location, gstin: event.target.value.toUpperCase() })} /></Form.Item></Col></Row><Form.Item label="Address"><Input value={location.address} onChange={event => setLocation({ ...location, address: event.target.value })} /></Form.Item><Form.Item><Space direction="vertical"><AntCheckbox checked={location.isPrimary} onChange={event => setLocation({ ...location, isPrimary: event.target.checked })}>Primary work location</AntCheckbox><AntCheckbox checked={location.isActive} onChange={event => setLocation({ ...location, isActive: event.target.checked })}>Active</AntCheckbox></Space></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => setLocation(location0)}>Reset</Button><Button type="primary" onClick={saveLocation}>{location.id ? 'Update location' : 'Add location'}</Button></Space></Row></Form></Drawer></>}
    {tab === 'Dropdown Masters' && <><AntCard title="Dropdown values" size="small" className="settings-panel settings-table-panel"><div className="component-table-head"><div><b>Dropdown master</b><span>Maintain reusable departments, designations, states, cities, grades, and work-week patterns.</span></div><Button type="primary" onClick={() => { setDrop({ ...drop0, type: selectedDropType, clientId: selectedDropType === 'Employee Grade' ? clients[0]?.id || 0 : 0 }); setDropState(''); setDropDrawerOpen(true) }}>Add value</Button></div><DataTable rows={visibleDrops} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => editDrop(row)}>Edit</Button><Button size="small" danger onClick={() => void deleteDrop(row)}>Delete</Button></Space>} columns={[{ key: 'master', label: 'Master', value: row => isCityType(row.type) ? 'City' : row.type }, { key: 'clientId', label: 'Client', value: row => row.type === 'Employee Grade' ? clientName(row.clientId) : '-' }, { key: 'state', label: 'State', value: row => cityState(row.type) || '-' }, { key: 'value', label: 'Value' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} /></AntCard><Drawer className="settings-master-drawer" title={drop.id ? 'Edit dropdown value' : 'Add dropdown value'} open={dropDrawerOpen} width={480} onClose={() => { setDropDrawerOpen(false); setDrop({ ...drop0, type: drop.type, clientId: drop.type === 'Employee Grade' ? drop.clientId : 0 }); setDropState('') }} destroyOnClose><Form component={false} layout="vertical" className="settings-quick-form"><Form.Item label="Master type" required><Sel v={selectedDropType} set={changeDropType} a={dropTypes} /></Form.Item>{selectedDropType === 'Employee Grade' && <Form.Item label="Client" required><Sel v={drop.clientId || ''} set={value => setDrop({ ...drop, clientId: Number(refId(value) || 0) })} a={clients.map(item => `${item.id}:${item.name}`)} /></Form.Item>}{selectedDropType === 'City' && <Form.Item label="State" required><Sel v={dropState} set={value => { setDropState(value); setDrop({ ...drop, type: 'City' }) }} a={stateOptions} /></Form.Item>}{selectedDropType === 'Work Week' ? <WorkWeekMasterFields drop={drop} setDrop={setDrop} /> : <Form.Item label={selectedDropType === 'City' ? 'City' : 'Value'} required><Input value={drop.value} onChange={event => setDrop({ ...drop, value: event.target.value })} placeholder={selectedDropType === 'City' ? 'e.g. Bengaluru / Pune' : selectedDropType === 'Employee Grade' ? 'e.g. G1 / Supervisor' : 'e.g. Finance / Manager'} /></Form.Item>}<Form.Item><AntCheckbox checked={drop.isActive} onChange={event => setDrop({ ...drop, isActive: event.target.checked })}>Active</AntCheckbox></Form.Item><Divider /><Row justify="end"><Space><Button onClick={() => { setDrop({ ...drop0, type: drop.type, clientId: drop.type === 'Employee Grade' ? drop.clientId : 0 }); setDropState('') }}>Reset</Button><Button type="primary" style={drop.id ? { background: '#f59e0b', borderColor: '#f59e0b' } : undefined} onClick={saveDrop}>{drop.id ? 'Update value' : 'Add value'}</Button></Space></Row></Form></Drawer></>}
    {tab === 'Tax Engine' && <TaxEngineManager clients={clients} onMessage={notifyFromChild} mode="company" />}
    {tab === 'Statutory Setup' && <><PageTabs items={statutoryTabs} value={statutoryTab} onChange={setStatutoryTab} label="Statutory setup sections" />{statutoryTab === 'Income Tax Rules' ? <TaxEngineManager clients={clients} onMessage={notifyFromChild} mode="statutory" /> : renderProfessionalTaxSetup()}</>}
    {tab === 'Client Billing Configuration' && renderClientBilling()}
    {tab === 'Salary Components' && <Card t="Salary components"><PageTabs items={componentTabs} value={componentTab} onChange={item => { setComponentTab(item); setComponent(normalizeComponentForUi({ ...component0, category: item })); setComponentDrawerOpen(false) }} label="Salary component categories" getLabel={item => `${item}s`} /><div className="component-table-head"><div><b>{componentTab}s</b><span>Changes save immediately to payroll setup.</span></div><button type="button" disabled={componentSaving} onClick={openNewComponent}>Add {componentTab}</button></div><div className="component-guide"><b>Setup guide</b><span>Use Formula for all derived components. Use Residual for balancing amount. Payable values are handled by Pro-rata, separate payable rows are not needed.</span></div><DataTable rows={componentRows} actions={row => <span className="row-actions"><button type="button" onClick={() => editComponent(row)}>Edit</button><button type="button" className="danger" disabled={componentSaving} onClick={() => void deleteComponent(row)}>Delete</button></span>} emptyText={`No ${componentTab.toLowerCase()} components configured yet.`} exportFileName={`salary-${componentTab.toLowerCase()}-components`} columns={[{ key: 'code', label: 'Code' }, { key: 'name', label: 'Name' }, { key: 'componentType', label: 'Type' }, { key: 'calculationType', label: 'Calculation' }, { key: 'payType', label: 'Pay Type' }, { key: 'priority', label: 'Priority' }, { key: 'locked', label: 'Lock', render: item => componentUsed(item.id, setup) ? 'Locked' : 'Open' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {tab === 'Salary Templates' && <SalaryTemplateDesigner clients={clients} components={setup.salaryComponents} structure={structure} setStructure={setStructure} templates={setup.salaryStructures.filter(item => !item.clientId || activeClientIds.has(refId(item.clientId)))} saveTemplate={saveStructure} saving={templateSaving} />}
    {tab === 'Payslip Templates' && <Card t="Payslip templates"><div className="grid"><F l="Client"><Sel v={payslip.clientId} set={value => setPayslip({ ...payslip, clientId: value })} a={clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Template name"><input value={payslip.name} onChange={event => setPayslip({ ...payslip, name: event.target.value })} /></F><F l="Theme"><Sel v={payslip.theme} set={value => setPayslip({ ...payslip, theme: value })} a={['Classic', 'Modern', 'Compact']} /></F><Chk l="Show logo" v={payslip.showLogo} set={value => setPayslip({ ...payslip, showLogo: value })} /><Chk l="Show client" v={payslip.showClient} set={value => setPayslip({ ...payslip, showClient: value })} /><Chk l="Show YTD" v={payslip.showYtd} set={value => setPayslip({ ...payslip, showYtd: value })} /><Chk l="Show bank info" v={payslip.showBank} set={value => setPayslip({ ...payslip, showBank: value })} /><Chk l="Active" v={payslip.active} set={value => setPayslip({ ...payslip, active: value })} /><F l="Footer note" w><input value={payslip.note} onChange={event => setPayslip({ ...payslip, note: event.target.value })} /></F><button type="button" disabled={payslipSaving} onClick={() => void savePayslip()}>{payslipSaving ? 'Saving...' : 'Add / Update template'}</button></div><div className={`payslip-preview ${payslip.theme.toLowerCase()}`}><header>{payslip.showLogo && <b className={org.logoDataUrl ? 'payslip-logo-mark' : ''}>{org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo" /> : 'P'}</b>}<div><h3>{org.name || 'Your Organization'}</h3><p>Payslip for June 2026</p>{payslip.showClient && <small>Client: {clientName(payslip.clientId)}</small>}</div></header><section><div><span>Employee</span><strong>Demo Employee</strong></div><div><span>Designation</span><strong>Software Engineer</strong></div><div><span>Pay Days</span><strong>30</strong></div><div><span>Bank</span><strong>{payslip.showBank ? 'HDFC ****1234' : '-'}</strong></div></section><table><thead><tr><th>Earnings</th><th>Amount</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>{previewLines.map((item, index) => <tr key={item.componentRow.id}><td>{item.componentRow.category !== 'Deduction' ? item.componentRow.name : ''}</td><td>{item.componentRow.category !== 'Deduction' ? money(item.amount) : ''}</td><td>{item.componentRow.category === 'Deduction' ? item.componentRow.name : index === 0 ? 'Professional Tax' : ''}</td><td>{item.componentRow.category === 'Deduction' ? money(item.amount) : index === 0 ? '200' : ''}</td></tr>)}</tbody></table>{payslip.showYtd && <p className="ytd">YTD Gross: Rs {money(monthly * 6)} | YTD Tax: Rs {money(1200)}</p>}<footer>{payslip.note}</footer></div><DataTable rows={setup.payslipTemplates.filter(item => !item.clientId || activeClientIds.has(refId(item.clientId)))} onEdit={setPayslip} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client', value: row => clientName(row.clientId) }, { key: 'theme', label: 'Theme' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {!['Clients', 'Client Billing Configuration', 'Salary Components', 'Salary Templates', 'Payslip Templates', 'Work Locations', 'Dropdown Masters'].includes(tab) && <div className="actions"><p>Structures are client-wise. Components are global.</p><button disabled={saving}>{saving ? 'Saving...' : 'Save settings'}</button></div>}
    {renderComponentDrawer()}
  </form>
}

function InfoField(p: { label: string; help: string; wide?: boolean; children: ReactNode }) {
  return <div className={`info-field ${p.wide ? 'wide' : ''}`}><span className="field-label">{p.label}<HelpTip text={p.help} /></span>{p.children}</div>
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
