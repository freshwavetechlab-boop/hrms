import { useEffect, useState } from 'react'
import { Chk, F, Sel } from '../components/FormPrimitives'
import DataTable from '../components/DataTable'
import PageTabs from '../components/PageTabs'
import SearchSelect, { selectOptions } from '../components/SearchSelect'
import { employee0, setup0 } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { getDropdowns, getSetup, getWorkLocations, saveEmployee as persistEmployee } from '../services/settingsService'
import type { Client, Component, Drop, Employee, EmployeePaymentDetails, EmployeePersonalDetails, Setup, Structure, WorkLocation } from '../types/payroll'
import { calculateSalaryJson, calculateSalaryTotals, money } from '../utils/salary'
import { safeJsonRecord } from '../shared/json'
import '../TemplateDesigner.css'

const employeeTabs = ['Basics', 'Salary', 'Personal', 'Payment'] as const
const personal0 = employee0.personalDetails
const payment0 = employee0.paymentDetails

export default function EmployeePage() {
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [drops, setDrops] = useState<Drop[]>([]), [setup, setSetup] = useState<Setup>(setup0)
  const [employees, setEmployees] = useState<Employee[]>([]), [employee, setEmployee] = useState(employee0), [employeeTab, setEmployeeTab] = useState<'Basics' | 'Salary' | 'Personal' | 'Payment'>('Basics')
  const [modalOpen, setModalOpen] = useState(false), [clientFilter, setClientFilter] = useState(0), [query, setQuery] = useState('')
  const clientStructure = templatesForClient(setup.salaryStructures, employee.clientId)[0]
  const chosenStructure = setup.salaryStructures.find(item => String(item.id) === employee.salaryStructureId) ?? clientStructure
  const rawEmployeeSalary = salaryRecord(employee)
  const structureLineIds = chosenStructure?.lines.map(line => line.componentId) ?? []
  const linkedSalaryHasCurrentIds = structureLineIds.some(id => rawEmployeeSalary[id] !== undefined)
  const employeeSalary = linkedSalaryHasCurrentIds || !chosenStructure || !employee.annualCtc ? rawEmployeeSalary : safeJsonRecord(calculateSalaryJson(employee.annualCtc, setup.salaryComponents, chosenStructure))
  const structureComponents = setup.salaryComponents.filter(component => component.active && structureLineIds.includes(String(component.id))).sort((a, b) => structureLineIds.indexOf(String(a.id)) - structureLineIds.indexOf(String(b.id)) || Number(a.priority) - Number(b.priority))
  const deps = drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), desigs = drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value)

  const load = async () => {
    const [clientRows, locationRows, dropdownRows, employeeRows, rawSetup] = await Promise.all([getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getSetup(setup0)])
    setClients(clientRows); setLocations(locationRows); setDrops(dropdownRows); setEmployees(employeeRows.map(normalizeEmployeeDetails))
    setSetup({ ...setup0, ...rawSetup, salaryComponents: rawSetup.salaryComponents ?? [], salaryStructures: rawSetup.salaryStructures ?? [] })
  }

  useEffect(() => { void load() }, [])
  const calcSalary = (ctc: number, salaryStructure = chosenStructure) => calculateSalaryJson(ctc, setup.salaryComponents, salaryStructure)
  const withSalary = (row: Employee, salaryJson: string): Employee => ({ ...row, salaryJson, salaryComponents: numberRecord(salaryJson) })
  const normalizeEmployeeSalary = (row: Employee) => {
    row = normalizeEmployeeDetails(row)
    const salaryStructure = setup.salaryStructures.find(item => String(item.id) === row.salaryStructureId) ?? templatesForClient(setup.salaryStructures, row.clientId)[0]
    if (!salaryStructure || !row.annualCtc) return row
    const existing = salaryRecord(row)
    const hasCurrentIds = salaryStructure.lines.some(line => existing[line.componentId] !== undefined)
    const normalized = String(row.salaryStructureId) === String(salaryStructure.id) ? row : { ...row, salaryStructureId: String(salaryStructure.id) }
    return hasCurrentIds ? normalized : withSalary(normalized, calcSalary(row.annualCtc, salaryStructure))
  }
  const empLine = (componentId: string, value: string) => { const lines = salaryRecord(employee); lines[componentId] = value; setEmployee(withSalary(employee, JSON.stringify(lines))) }
  const empMonthly = (component: Component) => Number(employeeSalary[String(component.id)] || 0)
  const applyStructure = (id: string) => { const selectedId = id.split(':')[0]; const selectedStructure = setup.salaryStructures.find(item => String(item.id) === selectedId); const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setEmployee(withSalary({ ...employee, salaryStructureId: selectedId, annualCtc: ctc }, calcSalary(ctc, selectedStructure))) }
  const applyCtc = (ctc: number) => setEmployee(withSalary({ ...employee, salaryStructureId: chosenStructure ? String(chosenStructure.id) : employee.salaryStructureId, annualCtc: ctc }, calcSalary(ctc)))
  const applyClient = (value: string) => { const clientId = Number(value.split(':')[0] || 0); const selectedStructure = templatesForClient(setup.salaryStructures, clientId)[0]; const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setEmployee(withSalary({ ...employee, clientId, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc }, selectedStructure ? calcSalary(ctc, selectedStructure) : '{}')) }
  const newEmployee = () => {
    const selectedStructure = templatesForClient(setup.salaryStructures, clientFilter)[0]
    const ctc = Number(selectedStructure?.annualCtc || 0)
    setEmployee(clientFilter ? withSalary({ ...employee0, clientId: clientFilter, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc }, selectedStructure ? calcSalary(ctc, selectedStructure) : '{}') : employee0)
    setEmployeeTab('Basics'); setModalOpen(true)
  }
  const editEmployee = (row: Employee) => { setEmployee(normalizeEmployeeSalary(row)); setEmployeeTab('Basics'); setModalOpen(true) }
  const closeModal = () => { setModalOpen(false); setEmployee(employee0); setEmployeeTab('Basics') }
  const saveEmployee = async () => { const response = await persistEmployee(toEmployeePayload(normalizeEmployeeSalary(employee))); if (response.ok) { closeModal(); await load() } }
  const visibleEmployees = employees.filter(row => (!clientFilter || row.clientId === clientFilter) && `${row.employeeCode} ${row.firstName} ${row.lastName} ${row.department} ${row.designation} ${row.workEmail}`.toLowerCase().includes(query.toLowerCase()))

  return <section className="employee-master">
    <EmployeeDirectory clients={clients} employees={visibleEmployees} allCount={employees.length} clientFilter={clientFilter} setClientFilter={setClientFilter} query={query} setQuery={setQuery} onNew={newEmployee} onEdit={editEmployee} />
    {modalOpen && <div className="employee-modal-backdrop" onClick={closeModal}>
      <section className="employee-modal" role="dialog" aria-modal="true" aria-label="Employee details" onClick={event => event.stopPropagation()}>
        <EmployeePanel employee={employee} setEmployee={row => setEmployee(normalizeEmployeeSalary(row))} employeeTab={employeeTab} setEmployeeTab={setEmployeeTab} clients={clients} locations={locations} templates={setup.salaryStructures} deps={deps} desigs={desigs} applyClient={applyClient} applyStructure={applyStructure} applyCtc={applyCtc} structureComponents={structureComponents} employeeSalary={employeeSalary} empLine={empLine} empMonthly={empMonthly} saveEmployee={saveEmployee} closeModal={closeModal} />
      </section>
    </div>}
  </section>
}

function EmployeeDirectory(p: { clients: Client[]; employees: Employee[]; allCount: number; clientFilter: number; setClientFilter: (id: number) => void; query: string; setQuery: (value: string) => void; onNew: () => void; onEdit: (employee: Employee) => void }) {
  const clientName = (id: number) => p.clients.find(client => client.id === id)?.name ?? `Client #${id || '-'}`
  return <section className="card employee-directory"><header><i className="blue">E</i><div><h3>Employee master</h3><p>Search client-wise employees. Create or edit details in a focused popup.</p></div><button type="button" onClick={p.onNew}>New employee</button></header>
    <div className="employee-directory-tools"><label><span>Client</span><SearchSelect value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={selectOptions(p.clients.map(client => ({ value: client.id, label: client.name })), 'All clients', 0)} /></label><label><span>Search</span><input value={p.query} onChange={event => p.setQuery(event.target.value)} placeholder="Code, name, department, email..." /></label><div><span>Showing</span><b>{p.employees.length} / {p.allCount}</b></div></div>
    <DataTable rows={p.employees} onEdit={p.onEdit} emptyText="No employees found for the selected filters." exportFileName="employees" columns={[
      { key: 'employeeName', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), render: row => <><strong>{row.firstName} {row.lastName}</strong><small>{row.employeeCode}</small></> },
      { key: 'clientName', label: 'Client', value: row => clientName(row.clientId) },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'workEmail', label: 'Work email' },
      { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
    ]} />
  </section>
}

function EmployeePanel(p: { employee: Employee; setEmployee: (employee: Employee) => void; employeeTab: 'Basics' | 'Salary' | 'Personal' | 'Payment'; setEmployeeTab: (tab: 'Basics' | 'Salary' | 'Personal' | 'Payment') => void; clients: Client[]; locations: WorkLocation[]; templates: Structure[]; deps: string[]; desigs: string[]; applyClient: (value: string) => void; applyStructure: (value: string) => void; applyCtc: (value: number) => void; structureComponents: Component[]; employeeSalary: Record<string, string>; empLine: (id: string, value: string) => void; empMonthly: (component: Component) => number; saveEmployee: () => void; closeModal: () => void }) {
  const personal = p.employee.personalDetails, payment = p.employee.paymentDetails
  const salaryRows = p.structureComponents.map(component => ({ component, monthly: p.empMonthly(component), annual: p.empMonthly(component) * 12 }))
  const totals = calculateSalaryTotals(salaryRows.map(row => ({ line: { componentId: String(row.component.id), value: '' }, ...row })))
  const badgeClass = (category: string) => category.toLowerCase().replace(/\s+/g, '-')
  const setPersonal = <K extends keyof EmployeePersonalDetails>(key: K, value: EmployeePersonalDetails[K]) => p.setEmployee({ ...p.employee, personalDetails: { ...personal, [key]: value } })
  const setPayment = <K extends keyof EmployeePaymentDetails>(key: K, value: EmployeePaymentDetails[K]) => p.setEmployee({ ...p.employee, paymentDetails: { ...payment, [key]: value } })
  return <section className="employee-card"><header><div><span className="eyebrow purple">{p.employee.id ? 'Edit employee' : 'New employee'}</span><h3>{p.employee.id ? `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode : 'Employee details'}</h3><p>Client linked profile, salary template and payroll details.</p></div><button type="button" className="employee-modal-close" onClick={p.closeModal}>×</button></header><PageTabs items={employeeTabs} value={p.employeeTab} onChange={p.setEmployeeTab} label="Employee detail sections" />
    {p.employeeTab === 'Basics' && <div className="grid"><F l="Client"><Sel v={String(p.employee.clientId || '')} set={p.applyClient} a={p.clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Employee code"><input value={p.employee.employeeCode} onChange={event => p.setEmployee({ ...p.employee, employeeCode: event.target.value })} /></F><F l="First name"><input value={p.employee.firstName} onChange={event => p.setEmployee({ ...p.employee, firstName: event.target.value })} /></F><F l="Last name"><input value={p.employee.lastName} onChange={event => p.setEmployee({ ...p.employee, lastName: event.target.value })} /></F><F l="Gender"><Sel v={p.employee.gender} set={value => p.setEmployee({ ...p.employee, gender: value })} a={['Male', 'Female', 'Other']} /></F><F l="Date of joining"><input type="date" value={p.employee.dateOfJoining} onChange={event => p.setEmployee({ ...p.employee, dateOfJoining: event.target.value })} /></F><F l="Work email"><input value={p.employee.workEmail} onChange={event => p.setEmployee({ ...p.employee, workEmail: event.target.value })} /></F><F l="Department"><Sel v={p.employee.department} set={value => p.setEmployee({ ...p.employee, department: value })} a={p.deps} /></F><F l="Designation"><Sel v={p.employee.designation} set={value => p.setEmployee({ ...p.employee, designation: value })} a={p.desigs} /></F><F l="Work location"><Sel v={String(p.employee.workLocationId || '')} set={value => p.setEmployee({ ...p.employee, workLocationId: Number(value.split(':')[0] || 0) })} a={p.locations.map(item => `${item.id}:${item.name}`)} /></F><Chk l="Portal access" v={p.employee.portalAccess} set={value => p.setEmployee({ ...p.employee, portalAccess: value })} /><Chk l="Active" v={p.employee.isActive} set={value => p.setEmployee({ ...p.employee, isActive: value })} /></div>}
    {p.employeeTab === 'Salary' && <div className="employee-salary-panel">
      <div className="employee-salary-controls"><F l="Salary template"><Sel v={p.employee.salaryStructureId} set={p.applyStructure} a={templatesForClient(p.templates, p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F><F l="Annual CTC"><input value={p.employee.annualCtc} onChange={event => p.applyCtc(Number(event.target.value.replace(/\D/g, '')))} /></F></div>
      <div className="employee-salary-summary"><article><span>Monthly gross</span><b>{money(totals.gross)}</b></article><article><span>Deductions</span><b>{money(totals.deductions)}</b></article><article><span>Monthly net</span><b>{money(totals.net)}</b></article><article><span>Annual CTC</span><b>{money(p.employee.annualCtc)}</b></article></div>
      <div className="employee-salary-table">
        <div className="employee-salary-row employee-salary-head"><span>Component</span><span>Name</span><span>Monthly</span><span>Annual</span><span>Override</span></div>
        {salaryRows.length ? salaryRows.map(({ component, monthly, annual }) => <div className="employee-salary-row" key={component.id}>
          <div className="employee-salary-code"><span className={`salary-badge ${badgeClass(component.category)}`}>{component.category}</span><b title={component.code}>{component.code}</b></div>
          <strong title={component.name}>{component.name}</strong>
          <output>{money(monthly)}</output>
          <output>{money(annual)}</output>
          <input value={p.employeeSalary[String(component.id)] ?? ''} onChange={event => p.empLine(String(component.id), event.target.value.replace(/[^\d.-]/g, ''))} aria-label={`${component.name} override`} />
        </div>) : <p className="employee-salary-empty">Select a client and salary template, then enter Annual CTC to calculate the salary breakup.</p>}
      </div>
    </div>}
    {p.employeeTab === 'Personal' && <div className="grid"><F l="Date of birth"><input type="date" value={personal.dateOfBirth || ''} onChange={event => setPersonal('dateOfBirth', event.target.value)} /></F><F l="PAN"><input value={personal.panNumber || ''} onChange={event => setPersonal('panNumber', event.target.value.toUpperCase())} /></F><F l="Aadhaar"><input value={personal.aadhaarNumber || ''} onChange={event => setPersonal('aadhaarNumber', event.target.value.replace(/\D/g, '').slice(0, 12))} /></F><F l="Mobile"><input value={personal.mobile || ''} onChange={event => setPersonal('mobile', event.target.value)} /></F><F l="Address" w><input value={personal.address || ''} onChange={event => setPersonal('address', event.target.value)} /></F></div>}
    {p.employeeTab === 'Payment' && <div className="grid"><F l="Bank"><input value={payment.bankName || ''} onChange={event => setPayment('bankName', event.target.value)} /></F><F l="Account no"><input value={payment.bankAccountNo || ''} onChange={event => setPayment('bankAccountNo', event.target.value)} /></F><F l="IFSC"><input value={payment.ifscCode || ''} onChange={event => setPayment('ifscCode', event.target.value.toUpperCase())} /></F><F l="Payment mode"><Sel v={payment.paymentMode || ''} set={value => setPayment('paymentMode', value)} a={['Bank Transfer', 'Cheque', 'Cash']} /></F></div>}
    <div className="actions"><button type="button" className="secondary" onClick={p.closeModal}>Cancel</button><button type="button" onClick={p.saveEmployee}>Save employee</button></div></section>
}

function normalizeEmployeeDetails(row: Employee): Employee {
  const personalJson = safeJsonRecord(row.personalJson)
  const paymentJson = safeJsonRecord(row.paymentJson)
  const salaryComponents = Object.keys(row.salaryComponents || {}).length ? row.salaryComponents : numberRecord(row.salaryJson)
  return {
    ...row,
    salaryComponents,
    salaryJson: JSON.stringify(salaryComponents),
    personalDetails: {
      ...personal0,
      ...row.personalDetails,
      dateOfBirth: row.personalDetails?.dateOfBirth || personalJson.dateOfBirth || personalJson.dob || '',
      panNumber: row.personalDetails?.panNumber || personalJson.panNumber || personalJson.pan || '',
      aadhaarNumber: row.personalDetails?.aadhaarNumber || personalJson.aadhaarNumber || personalJson.aadhaar || '',
      uanNumber: row.personalDetails?.uanNumber || personalJson.uanNumber || personalJson.uan || '',
      esicNumber: row.personalDetails?.esicNumber || personalJson.esicNumber || personalJson.esic || '',
      mobile: row.personalDetails?.mobile || personalJson.mobile || '',
      address: row.personalDetails?.address || personalJson.address || ''
    },
    paymentDetails: {
      ...payment0,
      ...row.paymentDetails,
      bankName: row.paymentDetails?.bankName || paymentJson.bankName || paymentJson.bank || '',
      bankAccountNo: row.paymentDetails?.bankAccountNo || paymentJson.bankAccountNo || paymentJson.account || paymentJson.accountNumber || '',
      ifscCode: row.paymentDetails?.ifscCode || paymentJson.ifscCode || paymentJson.ifsc || '',
      paymentMode: row.paymentDetails?.paymentMode || paymentJson.paymentMode || paymentJson.mode || ''
    }
  }
}

function toEmployeePayload(row: Employee): Employee {
  const salaryComponents = Object.fromEntries(Object.entries(row.salaryComponents || {}).map(([key, value]) => [key, Number(value) || 0]))
  const personalJson = {
    dob: row.personalDetails.dateOfBirth,
    dateOfBirth: row.personalDetails.dateOfBirth,
    mobile: row.personalDetails.mobile,
    pan: row.personalDetails.panNumber,
    panNumber: row.personalDetails.panNumber,
    aadhaar: row.personalDetails.aadhaarNumber,
    aadhaarNumber: row.personalDetails.aadhaarNumber,
    uan: row.personalDetails.uanNumber,
    uanNumber: row.personalDetails.uanNumber,
    esic: row.personalDetails.esicNumber,
    esicNumber: row.personalDetails.esicNumber,
    address: row.personalDetails.address,
    source: row.personalDetails.source,
    sourceLocation: row.personalDetails.sourceLocation,
    city: row.personalDetails.city,
    district: row.personalDetails.district,
    state: row.personalDetails.state,
    rawDesignation: row.personalDetails.rawDesignation,
    originalEmployeeCode: row.personalDetails.originalEmployeeCode,
    duplicateResolution: row.personalDetails.duplicateResolution,
    excelRow: row.personalDetails.excelRow,
    esicEmployee: row.personalDetails.esicEmployee,
    ptLwfWorkmenComp: row.personalDetails.ptLwfWorkmenComp,
    tds: row.personalDetails.tds,
    recovery: row.personalDetails.recovery
  }
  const paymentJson = {
    bank: row.paymentDetails.bankName,
    bankName: row.paymentDetails.bankName,
    account: row.paymentDetails.bankAccountNo,
    bankAccountNo: row.paymentDetails.bankAccountNo,
    ifsc: row.paymentDetails.ifscCode,
    ifscCode: row.paymentDetails.ifscCode,
    mode: row.paymentDetails.paymentMode,
    paymentMode: row.paymentDetails.paymentMode
  }
  return { ...row, salaryComponents, salaryJson: JSON.stringify(salaryComponents), personalJson: JSON.stringify(personalJson), paymentJson: JSON.stringify(paymentJson) }
}

function salaryRecord(row: Employee) {
  return Object.keys(row.salaryComponents || {}).length ? Object.fromEntries(Object.entries(row.salaryComponents).map(([key, value]) => [key, String(value)])) : safeJsonRecord(row.salaryJson)
}

function numberRecord(json: string) {
  return Object.fromEntries(Object.entries(safeJsonRecord(json)).map(([key, value]) => [key, Number(value) || 0]))
}

function refId(value: string | number | null | undefined) {
  return String(value ?? '').split(':')[0]
}

function templatesForClient(templates: Structure[], clientId: number | string) {
  const active = templates.filter(template => template.active !== false)
  const client = refId(clientId)
  if (!client) return active
  const scoped = active.filter(template => refId(template.clientId) === client)
  return scoped.length ? scoped : active
}
