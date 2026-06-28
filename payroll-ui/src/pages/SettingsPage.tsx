import { useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Select as AntSelect } from 'antd'
import ClientPayScheduleManager from '../components/ClientPayScheduleManager'
import DataTable from '../components/DataTable'
import FileDropZone from '../components/FileDropZone'
import { Card, Chk, F, Sel } from '../components/FormPrimitives'
import PageTabs from '../components/PageTabs'
import SalaryTemplateDesigner from '../components/SalaryTemplateDesigner'
import TaxEngineManager from '../components/TaxEngineManager'
import { useToast, type ToastType } from '../components/ToastProvider'
import { client0, component0, demoComponents, drop0, dropTypes, location0, org0, payslip0, settingsMenus, setup0, states, structure0 } from '../data/payrollDefaults'
import { getClients } from '../services/payrollService'
import { getDropdowns, getOrganization, getSetup, getWorkLocations, saveClient as persistClient, saveDropdown, saveOrganization, saveSetup, saveWorkLocation } from '../services/settingsService'
import type { Client, Component, Drop, Org, ProfessionalTaxSlab, Setup, WorkLocation } from '../types/payroll'
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
const cityType = (state: string) => `City:${state}`
const isCityType = (type: string) => type.startsWith('City:')
const cityState = (type: string) => isCityType(type) ? type.slice(5) : ''

export default function SettingsPage({ tab, onMessage }: { tab: SettingsTab; onMessage: (message: string) => void }) {
  const toast = useToast()
  const [org, setOrg] = useState(org0), [setup, setSetup] = useState(settingsSetup0), [clients, setClients] = useState<Client[]>([]), [client, setClient] = useState(client0)
  const [locations, setLocations] = useState<WorkLocation[]>([]), [location, setLocation] = useState(location0), [drops, setDrops] = useState<Drop[]>([]), [drop, setDrop] = useState(drop0)
  const [component, setComponent] = useState(component0), [structure, setStructure] = useState(structure0), [payslip, setPayslip] = useState(payslip0), [componentTab, setComponentTab] = useState<ComponentCategory>('Earning')
  const [organizationTab, setOrganizationTab] = useState<OrganizationTab>('Organization')
  const [statutoryTab, setStatutoryTab] = useState<StatutoryTab>('Income Tax Rules')
  const [ptSlab, setPtSlab] = useState<ProfessionalTaxSlab>(ptSlab0)
  const [componentDrawerOpen, setComponentDrawerOpen] = useState(false)
  const [componentSaving, setComponentSaving] = useState(false)
  const [templateSaving, setTemplateSaving] = useState(false)
  const [payslipSaving, setPayslipSaving] = useState(false)
  const [saving, setSaving] = useState(false)
  const [dropState, setDropState] = useState('')

  const load = async () => {
    const [organization, rawSetup, clientRows, locationRows, dropdownRows] = await Promise.all([getOrganization(org0), getSetup(settingsSetup0), getClients(), getWorkLocations(), getDropdowns()])
    setOrg({ ...org0, ...organization, professionalTaxNumber: organization.professionalTaxNumber || rawSetup.statutory?.ptNumber || '' })
    setSetup({ ...settingsSetup0, ...rawSetup, tax: { ...setup0.tax, ...rawSetup.tax, clientSettings: rawSetup.tax?.clientSettings ?? setup0.tax.clientSettings, slabs: rawSetup.tax?.slabs ?? setup0.tax.slabs, surcharges: rawSetup.tax?.surcharges ?? setup0.tax.surcharges, finalAdjustments: rawSetup.tax?.finalAdjustments ?? setup0.tax.finalAdjustments, declarationSections: rawSetup.tax?.declarationSections ?? setup0.tax.declarationSections }, schedule: { ...setup0.schedule, ...rawSetup.schedule }, statutory: { ...setup0.statutory, ...rawSetup.statutory }, salaryComponents: (rawSetup.salaryComponents?.length ? rawSetup.salaryComponents : demoComponents).map(normalizeComponentForUi), salaryStructures: rawSetup.salaryStructures ?? [], payslipTemplates: rawSetup.payslipTemplates ?? [] })
    setClients(clientRows)
    setLocations(locationRows)
    setDrops(dropdownRows)
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
    onMessage('Click Save settings to persist Professional Tax slabs.')
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
  const saveAll = async (event: FormEvent) => { event.preventDefault(); setSaving(true); await saveOrganization(org); await saveSetup({ ...setup, statutory: { ...setup.statutory, ptNumber: org.professionalTaxNumber } }); onMessage('Settings saved.'); setSaving(false) }
  const saveClient = async () => { if (!client.name.trim()) return; await persistClient(client); setClient(client0); await load(); onMessage('Client saved.') }
  const applyLocationClient = (value: string) => {
    const [id, ...nameParts] = value.split(':')
    setLocation(current => ({ ...current, clientId: Number(id) || 0, clientName: nameParts.join(':') }))
  }
  const saveLocation = async () => { if (!location.clientId) return notify('Client select karo.', 'error'); if (!location.name.trim()) return notify('Location name required hai.', 'error'); const response = await saveWorkLocation(location); notify(response.ok ? 'Work location saved.' : response.error || 'Location fields check karo.', response.ok ? 'success' : 'error'); if (response.ok) { setLocation(location0); await load() } }
  const deleteLocation = async (row: WorkLocation) => {
    if (!window.confirm(`Delete ${row.name}? Existing employees or holidays linked to this location will keep their history, but this location will no longer be active for new use.`)) return
    const response = await saveWorkLocation({ ...row, isActive: false, isPrimary: false })
    notify(response.ok ? 'Work location deleted.' : response.error || 'Unable to delete work location.', response.ok ? 'success' : 'error')
    if (response.ok) { if (location.id === row.id) setLocation(location0); await load() }
  }
  const notify = (message: string, type: ToastType = 'success') => { onMessage(message); toast(message, type) }
  const stateOptions = unique([...states, ...drops.filter(item => item.isActive && item.type === 'State').map(item => item.value), ...locations.map(item => item.state), org.state, location.state, dropState])
  const cityOptions = (state: string) => unique(drops.filter(item => item.isActive && isCityType(item.type) && (!state || item.type === cityType(state))).map(item => item.value))
  const selectedDropType = drop.type === 'City' ? 'City' : drop.type
  const visibleDrops = drops.filter(item => item.isActive && (selectedDropType === 'City' ? isCityType(item.type) && (!dropState || item.type === cityType(dropState)) : item.type === selectedDropType))
  const changeDropType = (type: string) => { setDrop({ ...drop0, type }); setDropState('') }
  const editDrop = (row: Drop) => { if (isCityType(row.type)) { setDropState(cityState(row.type)); setDrop({ ...row, type: 'City' }); return } setDropState(''); setDrop(row) }
  const saveDrop = async () => {
    const actualType = drop.type === 'City' ? dropState ? cityType(dropState) : '' : drop.type
    if (!actualType || !drop.value.trim()) return notify(drop.type === 'City' ? 'City master ke liye state aur city dono required hain.' : 'Dropdown value required hai.', 'error')
    const value = drop.value.trim()
    const duplicate = drops.find(item => item.id !== drop.id && item.type.toLowerCase() === actualType.toLowerCase() && item.value.trim().toLowerCase() === value.toLowerCase())
    if (duplicate?.isActive) return notify(`${value} already exists in ${drop.type === 'City' ? dropState : actualType}.`, 'error')
    const payload = duplicate && !drop.id ? { ...duplicate, value, type: actualType, isActive: true } : { ...drop, type: actualType, value }
    const response = await saveDropdown(payload, { toast: false })
    notify(response.ok ? drop.id ? 'Dropdown value updated.' : 'Dropdown value added.' : response.error || 'Dropdown save failed.', response.ok ? 'success' : 'error')
    if (response.ok) { setDrop({ ...drop0, type: drop.type }); await load() }
  }
  const deleteDrop = async (row: Drop) => {
    if (!window.confirm(`Delete ${row.value}?`)) return
    const response = await saveDropdown({ ...row, isActive: false }, { toast: false })
    notify(response.ok ? 'Dropdown value deleted.' : response.error || 'Dropdown delete failed.', response.ok ? 'success' : 'error')
    if (response.ok) await load()
  }
  const openNewComponent = () => { setComponent(normalizeComponentForUi({ ...component0, category: componentTab })); setComponentDrawerOpen(true) }
  const persistComponentSetup = async (nextSetup: Setup, success: string) => { setComponentSaving(true); const response = await saveSetup(nextSetup, { toast: false }); setComponentSaving(false); if (!response.ok) { notify(response.error || 'Unable to save salary components.', 'error'); return false } setSetup(nextSetup); notify(success); return true }
  const saveComponent = async () => { const rowForUi = normalizeComponentForUi(component); const errors = validateComponent(rowForUi, componentTab, setup); if (errors.length) return notify(errors[0], 'error'); const isUpdate = Boolean(rowForUi.id), locked = rowForUi.id && componentUsed(rowForUi.id, setup); const row = { ...rowForUi, category: locked ? rowForUi.category : componentTab, id: rowForUi.id || Date.now(), code: locked ? rowForUi.code : rowForUi.code.trim().toUpperCase() }; const nextSetup = { ...setup, salaryComponents: [...setup.salaryComponents.filter(item => item.id !== row.id), row] }; if (await persistComponentSetup(nextSetup, isUpdate ? 'Salary component updated successfully.' : 'Salary component added successfully.')) { setComponent(normalizeComponentForUi({ ...component0, category: componentTab })); setComponentDrawerOpen(false) } }
  const editComponent = (row: Component) => { if (componentTabs.includes(row.category as ComponentCategory)) setComponentTab(row.category as ComponentCategory); setComponent(normalizeComponentForUi(row)); setComponentDrawerOpen(true) }
  const deleteComponent = async (row: Component) => { if (componentUsed(row.id, setup)) return notify('This component is used in salary templates. Remove it from templates before deleting.', 'error'); if (!window.confirm(`Delete ${row.name || row.code}?`)) return; await persistComponentSetup({ ...setup, salaryComponents: setup.salaryComponents.filter(item => item.id !== row.id) }, 'Salary component deleted successfully.'); if (component.id === row.id) { setComponent({ ...component0, category: componentTab }); setComponentDrawerOpen(false) } }
  const saveStructure = async () => {
    if (!structure.name.trim()) return notify('Template name is required.', 'error')
    const row = { ...structure, id: structure.id || Date.now() }
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
    const row = { ...payslip, id: payslip.id || Date.now() }
    const nextSetup = { ...setup, payslipTemplates: [...setup.payslipTemplates.filter(item => item.id !== row.id), row] }
    setPayslipSaving(true)
    const response = await saveSetup(nextSetup, { toast: false })
    setPayslipSaving(false)
    if (!response.ok) return notify(response.error || 'Unable to save payslip template.', 'error')
    setSetup(nextSetup)
    setPayslip(payslip0)
    notify(row.id === payslip.id ? 'Payslip template updated.' : 'Payslip template saved.')
  }
  const previewStructure = setup.salaryStructures.find(item => item.clientId === payslip.clientId) ?? setup.salaryStructures[0]
  const monthly = Number(previewStructure?.annualCtc || 600000) / 12
  const previewLines = setup.salaryComponents.filter(item => item.active).slice(0, 6).map((componentRow, index) => ({ componentRow, amount: componentRow.category === 'Deduction' ? monthly * 0.048 : index === 0 ? monthly * 0.4 : index === 1 ? monthly * 0.2 : monthly * 0.08 }))
  const renderOrganizationBody = () => {
    if (organizationTab === 'Organization') return <div className="grid"><F l="Organization logo" w><div className="logo-uploader"><FileDropZone accept="image/png,image/jpeg,image/svg+xml,image/webp" title="Drop logo here or browse" hint="PNG, JPG, SVG or WebP for payslips and documents." onFile={uploadLogo} preview={org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo preview" /> : <b>No logo</b>} />{org.logoDataUrl && <button type="button" className="secondary" onClick={() => o('logoDataUrl', '')}>Remove logo</button>}</div></F><F l="Name"><input required value={org.name} onChange={event => o('name', event.target.value)} /></F><F l="Legal name"><input value={org.legalName} onChange={event => o('legalName', event.target.value)} /></F><F l="Industry"><input value={org.industry} onChange={event => o('industry', event.target.value)} /></F><F l="State"><Sel v={org.state} set={value => o('state', value)} a={stateOptions} /></F><F l="Address" w><input required value={org.addressLine1} onChange={event => o('addressLine1', event.target.value)} /></F><F l="City"><input required value={org.city} onChange={event => o('city', event.target.value)} /></F><F l="PIN"><input required value={org.postalCode} onChange={event => o('postalCode', event.target.value.replace(/\D/g, '').slice(0, 6))} /></F></div>
    if (organizationTab === 'Tax') return <div className="grid"><F l="PAN"><input value={setup.tax.pan} onChange={event => u('tax', 'pan', event.target.value.toUpperCase())} /></F><F l="TAN"><input value={setup.tax.tan} onChange={event => u('tax', 'tan', event.target.value.toUpperCase())} /></F><F l="AO Code"><input value={setup.tax.aoCode} onChange={event => u('tax', 'aoCode', event.target.value)} /></F><F l="Frequency"><Sel v={setup.tax.frequency} set={value => u('tax', 'frequency', value)} a={['Monthly', 'Quarterly']} /></F></div>
    if (organizationTab === 'EPF') return <div className="grid"><Chk l="Enable EPF" v={setup.statutory.epf} set={value => u('statutory', 'epf', value)} /><F l="EPF registration no"><input value={setup.statutory.epfNumber} onChange={event => u('statutory', 'epfNumber', event.target.value)} /></F><F l="Contribution"><Sel v={setup.statutory.epfContribution} set={value => u('statutory', 'epfContribution', value)} a={['Both Employee and Employer', 'Employee only', 'Employer only']} /></F><Chk l="Employer PF in CTC" v={setup.statutory.epfCtc} set={value => u('statutory', 'epfCtc', value)} /><Chk l="Restrict PF to statutory wage ceiling" v={setup.statutory.restrictPf} set={value => u('statutory', 'restrictPf', value)} /><Chk l="ABRY applicable" v={setup.statutory.abry} set={value => u('statutory', 'abry', value)} /></div>
    if (organizationTab === 'ESI') return <div className="grid"><Chk l="Enable ESI" v={setup.statutory.esi} set={value => u('statutory', 'esi', value)} /><F l="ESI registration no"><input value={setup.statutory.esiNumber} onChange={event => u('statutory', 'esiNumber', event.target.value)} /></F></div>
    if (organizationTab === 'Professional Tax') return <div className="grid"><F l="PT registration no"><input value={org.professionalTaxNumber} onChange={event => o('professionalTaxNumber', event.target.value)} /></F></div>
    return <div className="grid"><Chk l="Enable LWF" v={setup.statutory.lwf} set={value => u('statutory', 'lwf', value)} /><F l="LWF state"><Sel v={setup.statutory.lwfState} set={value => u('statutory', 'lwfState', value)} a={stateOptions} /></F><F l="Deduction cycle"><Sel v={setup.statutory.lwfCycle} set={value => u('statutory', 'lwfCycle', value)} a={['Monthly', 'Half-yearly', 'Yearly']} /></F><F l="Eligibility wage limit"><input value={setup.statutory.lwfEligibilityLimit} onChange={event => u('statutory', 'lwfEligibilityLimit', event.target.value.replace(/\D/g, ''))} /></F><F l="Employee contribution"><input value={setup.statutory.lwfEmployeeContribution} onChange={event => u('statutory', 'lwfEmployeeContribution', event.target.value)} /></F><F l="Employer contribution"><input value={setup.statutory.lwfEmployerContribution} onChange={event => u('statutory', 'lwfEmployerContribution', event.target.value)} /></F></div>
  }
  const renderProfessionalTaxSetup = () => <Card t="Professional Tax">
    <div className="grid"><Chk l="Enable PT" v={setup.statutory.pt} set={value => u('statutory', 'pt', value)} /><F l="Default PT state"><Sel v={setup.statutory.ptState} set={value => u('statutory', 'ptState', value)} a={stateOptions} /></F><F l="Deduction cycle"><Sel v={setup.statutory.ptCycle} set={value => u('statutory', 'ptCycle', value)} a={['Monthly', 'Half-yearly', 'Yearly']} /></F></div>
    <section className="pt-slab-manager"><h3>State-wise tax slab management</h3><div className="grid"><F l="State"><Sel v={ptSlab.state} set={value => setPtSlabField('state', value)} a={stateOptions} /></F><F l="Salary from"><input value={ptSlab.salaryFrom} onChange={event => setPtSlabField('salaryFrom', event.target.value.replace(/\D/g, ''))} /></F><F l="Salary to"><input value={ptSlab.salaryTo} onChange={event => setPtSlabField('salaryTo', event.target.value.replace(/\D/g, ''))} placeholder="No upper limit" /></F><F l="Deduction amount"><input value={ptSlab.deductionAmount} onChange={event => setPtSlabField('deductionAmount', event.target.value.replace(/\D/g, ''))} /></F><F l="Effective from"><input type="date" value={ptSlab.effectiveFrom} onChange={event => setPtSlabField('effectiveFrom', event.target.value)} /></F><F l="Effective to"><input type="date" value={ptSlab.effectiveTo} onChange={event => setPtSlabField('effectiveTo', event.target.value)} /></F><F l="Gender"><Sel v={ptSlab.gender} set={value => setPtSlabField('gender', value)} a={['All', 'Male', 'Female', 'Other']} /></F><Chk l="Active slab" v={ptSlab.active} set={value => setPtSlabField('active', value)} /><F l="Notes" w><input value={ptSlab.notes} onChange={event => setPtSlabField('notes', event.target.value)} placeholder="e.g. February special deduction" /></F><button type="button" onClick={savePtSlab}>{ptSlab.id ? 'Update slab' : 'Add slab'}</button></div><DataTable rows={setup.statutory.ptStateSlabs ?? []} columns={[{ key: 'state', label: 'State' }, { key: 'salaryRange', label: 'Salary Range', value: row => `${row.salaryFrom || '0'} - ${row.salaryTo || 'No limit'}` }, { key: 'deductionAmount', label: 'Deduction' }, { key: 'cycle', label: 'Cycle', value: () => setup.statutory.ptCycle }, { key: 'effective', label: 'Effective', value: row => `${row.effectiveFrom || '-'} to ${row.effectiveTo || 'Open'}` }, { key: 'gender', label: 'Gender' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <span className="row-actions"><button type="button" onClick={() => setPtSlab(row)}>Edit</button><button type="button" className="danger" onClick={() => removePtSlab(row)}>Delete</button></span>} /></section>
  </Card>
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
    {tab === 'Organization' && <Card t="Organization"><PageTabs items={organizationTabs} value={organizationTab} onChange={setOrganizationTab} label="Organization sections" />{renderOrganizationBody()}</Card>}
    {tab === 'Clients' && <Card t="Clients"><div className="grid"><F l="Client name"><input value={client.name} onChange={event => setClient({ ...client, name: event.target.value })} /></F><F l="Code"><input value={client.code} onChange={event => setClient({ ...client, code: event.target.value })} /></F><F l="Contact"><input value={client.contactPerson} onChange={event => setClient({ ...client, contactPerson: event.target.value })} /></F><F l="Email"><input value={client.email} onChange={event => setClient({ ...client, email: event.target.value })} /></F><F l="Phone"><input value={client.phone} onChange={event => setClient({ ...client, phone: event.target.value })} /></F><F l="Address" w><input value={client.address} onChange={event => setClient({ ...client, address: event.target.value })} /></F><button type="button" onClick={saveClient}>Add / Update client</button></div><DataTable rows={clients} onEdit={setClient} columns={[{ key: 'name', label: 'Client' }, { key: 'code', label: 'Code' }, { key: 'contactPerson', label: 'Contact' }, { key: 'email', label: 'Email' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} /></Card>}
    {tab === 'Work Locations' && <Card t="Work locations"><div className="grid"><F l="Client"><Sel v={location.clientId ? `${location.clientId}:${location.clientName || clients.find(item => item.id === location.clientId)?.name || ''}` : ''} set={applyLocationClient} a={clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Location name"><input value={location.name} onChange={event => setLocation({ ...location, name: event.target.value })} placeholder="Head Office / WFH - Employee Name" /></F><F l="State"><Sel v={location.state} set={value => setLocation({ ...location, state: value })} a={stateOptions} /></F><F l="Address" w><input value={location.address} onChange={event => setLocation({ ...location, address: event.target.value })} /></F><F l="City"><input value={location.city} onChange={event => setLocation({ ...location, city: event.target.value })} /></F><F l="PIN code"><input value={location.postalCode} onChange={event => setLocation({ ...location, postalCode: event.target.value.replace(/\D/g, '').slice(0, 6) })} /></F><Chk l="Primary work location" v={location.isPrimary} set={value => setLocation({ ...location, isPrimary: value })} /><Chk l="Active" v={location.isActive} set={value => setLocation({ ...location, isActive: value })} /><button type="button" onClick={saveLocation}>{location.id ? 'Update location' : 'Add location'}</button></div><DataTable rows={locations.filter(item => item.isActive)} columns={[{ key: 'clientName', label: 'Client', value: row => row.clientName || clients.find(item => item.id === row.clientId)?.name || '-' }, { key: 'name', label: 'Location' }, { key: 'city', label: 'City' }, { key: 'state', label: 'State' }, { key: 'postalCode', label: 'PIN' }, { key: 'isPrimary', label: 'Primary', render: item => item.isPrimary ? 'Yes' : 'No' }]} actions={row => <span className="row-actions"><button type="button" onClick={() => setLocation(row)}>Edit</button><button type="button" className="danger" onClick={() => void deleteLocation(row)}>Delete</button></span>} /></Card>}
    {tab === 'Dropdown Masters' && <Card t="Dropdown masters"><div className="grid"><F l="Master type"><Sel v={selectedDropType} set={changeDropType} a={dropTypes} /></F>{selectedDropType === 'City' && <F l="State"><Sel v={dropState} set={value => { setDropState(value); setDrop({ ...drop, type: 'City' }) }} a={stateOptions} /></F>}<F l={selectedDropType === 'City' ? 'City' : 'Value'}><input value={drop.value} onChange={event => setDrop({ ...drop, value: event.target.value })} placeholder={selectedDropType === 'City' ? 'e.g. Bengaluru / Pune' : 'e.g. Finance / Manager'} /></F><Chk l="Active" v={drop.isActive} set={value => setDrop({ ...drop, isActive: value })} /><button type="button" onClick={saveDrop}>{drop.id ? 'Update value' : 'Add value'}</button></div><DataTable rows={visibleDrops} actions={row => <span className="row-actions"><button type="button" onClick={() => editDrop(row)}>Edit</button><button type="button" className="danger" onClick={() => void deleteDrop(row)}>Delete</button></span>} columns={[{ key: 'master', label: 'Master', value: row => isCityType(row.type) ? 'City' : row.type }, { key: 'state', label: 'State', value: row => cityState(row.type) || '-' }, { key: 'value', label: 'Value' }, { key: 'isActive', label: 'Status', render: item => item.isActive ? 'Active' : 'Inactive' }]} /></Card>}
    {tab === 'Pay Schedule' && <Card t="Client pay schedule"><ClientPayScheduleManager clients={clients} reload={load} /></Card>}
    {tab === 'Tax Engine' && <TaxEngineManager clients={clients} onMessage={onMessage} mode="company" />}
    {tab === 'Statutory Setup' && <><PageTabs items={statutoryTabs} value={statutoryTab} onChange={setStatutoryTab} label="Statutory setup sections" />{statutoryTab === 'Income Tax Rules' ? <TaxEngineManager clients={clients} onMessage={onMessage} mode="statutory" /> : renderProfessionalTaxSetup()}</>}
    {tab === 'Salary Components' && <Card t="Salary components"><PageTabs items={componentTabs} value={componentTab} onChange={item => { setComponentTab(item); setComponent(normalizeComponentForUi({ ...component0, category: item })); setComponentDrawerOpen(false) }} label="Salary component categories" getLabel={item => `${item}s`} /><div className="component-table-head"><div><b>{componentTab}s</b><span>Changes save immediately to payroll setup.</span></div><button type="button" disabled={componentSaving} onClick={openNewComponent}>Add {componentTab}</button></div><div className="component-guide"><b>Setup guide</b><span>Use Formula for all derived components. Use Residual for balancing amount. Payable values are handled by Pro-rata, separate payable rows are not needed.</span></div><DataTable rows={componentRows} actions={row => <span className="row-actions"><button type="button" onClick={() => editComponent(row)}>Edit</button><button type="button" className="danger" disabled={componentSaving} onClick={() => void deleteComponent(row)}>Delete</button></span>} emptyText={`No ${componentTab.toLowerCase()} components configured yet.`} exportFileName={`salary-${componentTab.toLowerCase()}-components`} columns={[{ key: 'code', label: 'Code' }, { key: 'name', label: 'Name' }, { key: 'componentType', label: 'Type' }, { key: 'calculationType', label: 'Calculation' }, { key: 'payType', label: 'Pay Type' }, { key: 'priority', label: 'Priority' }, { key: 'locked', label: 'Lock', render: item => componentUsed(item.id, setup) ? 'Locked' : 'Open' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {tab === 'Salary Templates' && <SalaryTemplateDesigner clients={clients} components={setup.salaryComponents} structure={structure} setStructure={setStructure} templates={setup.salaryStructures} saveTemplate={saveStructure} saving={templateSaving} />}
    {tab === 'Payslip Templates' && <Card t="Payslip templates"><div className="grid"><F l="Client"><Sel v={payslip.clientId} set={value => setPayslip({ ...payslip, clientId: value })} a={clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Template name"><input value={payslip.name} onChange={event => setPayslip({ ...payslip, name: event.target.value })} /></F><F l="Theme"><Sel v={payslip.theme} set={value => setPayslip({ ...payslip, theme: value })} a={['Classic', 'Modern', 'Compact']} /></F><Chk l="Show logo" v={payslip.showLogo} set={value => setPayslip({ ...payslip, showLogo: value })} /><Chk l="Show client" v={payslip.showClient} set={value => setPayslip({ ...payslip, showClient: value })} /><Chk l="Show YTD" v={payslip.showYtd} set={value => setPayslip({ ...payslip, showYtd: value })} /><Chk l="Show bank info" v={payslip.showBank} set={value => setPayslip({ ...payslip, showBank: value })} /><Chk l="Active" v={payslip.active} set={value => setPayslip({ ...payslip, active: value })} /><F l="Footer note" w><input value={payslip.note} onChange={event => setPayslip({ ...payslip, note: event.target.value })} /></F><button type="button" disabled={payslipSaving} onClick={() => void savePayslip()}>{payslipSaving ? 'Saving...' : 'Add / Update template'}</button></div><div className={`payslip-preview ${payslip.theme.toLowerCase()}`}><header>{payslip.showLogo && <b>P</b>}<div><h3>{org.name || 'Your Organization'}</h3><p>Payslip for June 2026</p>{payslip.showClient && <small>Client: {payslip.clientId || 'Default'}</small>}</div></header><section><div><span>Employee</span><strong>Demo Employee</strong></div><div><span>Designation</span><strong>Software Engineer</strong></div><div><span>Pay Days</span><strong>30</strong></div><div><span>Bank</span><strong>{payslip.showBank ? 'HDFC ****1234' : '-'}</strong></div></section><table><thead><tr><th>Earnings</th><th>Amount</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>{previewLines.map((item, index) => <tr key={item.componentRow.id}><td>{item.componentRow.category !== 'Deduction' ? item.componentRow.name : ''}</td><td>{item.componentRow.category !== 'Deduction' ? money(item.amount) : ''}</td><td>{item.componentRow.category === 'Deduction' ? item.componentRow.name : index === 0 ? 'Professional Tax' : ''}</td><td>{item.componentRow.category === 'Deduction' ? money(item.amount) : index === 0 ? '200' : ''}</td></tr>)}</tbody></table>{payslip.showYtd && <p className="ytd">YTD Gross: Rs {money(monthly * 6)} | YTD Tax: Rs {money(1200)}</p>}<footer>{payslip.note}</footer></div><DataTable rows={setup.payslipTemplates} onEdit={setPayslip} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client' }, { key: 'theme', label: 'Theme' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} /></Card>}
    {!['Salary Components', 'Salary Templates', 'Payslip Templates', 'Work Locations', 'Pay Schedule'].includes(tab) && <div className="actions"><p>Structures are client-wise. Components are global.</p><button disabled={saving || tab === 'Clients'}>{saving ? 'Saving...' : 'Save settings'}</button></div>}
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
