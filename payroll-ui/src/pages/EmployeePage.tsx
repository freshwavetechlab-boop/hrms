import { useEffect, useState } from 'react'
import { Chk, F, Sel } from '../components/FormPrimitives'
import DataTable from '../components/DataTable'
import PageTabs from '../components/PageTabs'
import { demoComponents, demoStructures, employee0, setup0 } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { deleteEmployee, downloadEmployeeImportSample, getDropdowns, getSetup, getWorkLocations, importEmployees, saveEmployee as persistEmployee } from '../services/settingsService'
import type { Client, Component, Drop, Employee, Setup, Structure, WorkLocation } from '../types/payroll'
import { calculateSalaryJson, calculateSalaryTotals, money } from '../utils/salary'
import { safeJsonRecord } from '../shared/json'
import '../TemplateDesigner.css'

const employeeTabs = ['Basics', 'Salary', 'Personal', 'Payment'] as const
const salaryCategories = new Set(['Earning', 'Deduction', 'Reimbursement'])

export default function EmployeePage() {
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [drops, setDrops] = useState<Drop[]>([]), [setup, setSetup] = useState<Setup>(setup0)
  const [employees, setEmployees] = useState<Employee[]>([]), [employee, setEmployee] = useState(employee0), [employeeTab, setEmployeeTab] = useState<'Basics' | 'Salary' | 'Personal' | 'Payment'>('Basics')
  const [modalOpen, setModalOpen] = useState(false), [clientFilter, setClientFilter] = useState(0), [query, setQuery] = useState(''), [importing, setImporting] = useState(false), [importMessage, setImportMessage] = useState('')
  const clientStructure = setup.salaryStructures.find(item => !!employee.clientId && item.clientId.split(':')[0] === String(employee.clientId))
  const chosenStructure = setup.salaryStructures.find(item => String(item.id) === employee.salaryStructureId) ?? clientStructure
  const fallbackLineIds = setup.salaryComponents.filter(component => component.active && salaryCategories.has(component.category)).sort((a, b) => Number(a.priority) - Number(b.priority) || a.name.localeCompare(b.name)).map(component => String(component.id))
  const rawEmployeeSalary = safeJsonRecord(employee.salaryJson)
  const structureLineIds = chosenStructure?.lines.length ? chosenStructure.lines.map(line => line.componentId) : fallbackLineIds
  const linkedSalaryHasCurrentIds = structureLineIds.some(id => rawEmployeeSalary[id] !== undefined)
  const employeeSalary = linkedSalaryHasCurrentIds || !chosenStructure || !employee.annualCtc ? rawEmployeeSalary : safeJsonRecord(calculateSalaryJson(employee.annualCtc, setup.salaryComponents, chosenStructure))
  const structureComponents = setup.salaryComponents.filter(component => component.active && structureLineIds.includes(String(component.id))).sort((a, b) => structureLineIds.indexOf(String(a.id)) - structureLineIds.indexOf(String(b.id)) || Number(a.priority) - Number(b.priority))
  const deps = drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), desigs = drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value)

  const load = async () => {
    const [clientRows, locationRows, dropdownRows, employeeRows, rawSetup] = await Promise.all([getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getSetup(setup0)])
    setClients(clientRows); setLocations(locationRows); setDrops(dropdownRows); setEmployees(employeeRows)
    setSetup({ ...setup0, ...rawSetup, salaryComponents: rawSetup.salaryComponents?.length ? rawSetup.salaryComponents : demoComponents, salaryStructures: rawSetup.salaryStructures?.length ? rawSetup.salaryStructures : demoStructures })
  }

  useEffect(() => { void load() }, [])
  const calcSalary = (ctc: number, salaryStructure = chosenStructure) => calculateSalaryJson(ctc, setup.salaryComponents, salaryStructure)
  const normalizeEmployeeSalary = (row: Employee, forceRecalculate = false) => {
    const salaryStructure = setup.salaryStructures.find(item => String(item.id) === row.salaryStructureId) ?? setup.salaryStructures.find(item => !!row.clientId && item.clientId.split(':')[0] === String(row.clientId))
    if (!row.annualCtc) return row
    const existing = safeJsonRecord(row.salaryJson)
    const lineIds = salaryStructure?.lines.length ? salaryStructure.lines.map(line => line.componentId) : fallbackLineIds
    const hasCurrentIds = lineIds.some(id => Number(existing[id] || 0) > 0)
    const normalized = salaryStructure && String(row.salaryStructureId) !== String(salaryStructure.id) ? { ...row, salaryStructureId: String(salaryStructure.id) } : row
    return hasCurrentIds && !forceRecalculate ? normalized : { ...normalized, salaryJson: calcSalary(row.annualCtc, salaryStructure) }
  }
  const empLine = (componentId: string, value: string) => { const lines = safeJsonRecord(employee.salaryJson); lines[componentId] = value; setEmployee({ ...employee, salaryJson: JSON.stringify(lines) }) }
  const empMonthly = (component: Component) => Number(employeeSalary[String(component.id)] || 0)
  const applyStructure = (id: string) => { const selectedId = id.split(':')[0]; const selectedStructure = setup.salaryStructures.find(item => String(item.id) === selectedId); const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setEmployee({ ...employee, salaryStructureId: selectedId, annualCtc: ctc, salaryJson: calcSalary(ctc, selectedStructure) }) }
  const applyCtc = (ctc: number) => setEmployee({ ...employee, salaryStructureId: chosenStructure ? String(chosenStructure.id) : employee.salaryStructureId, annualCtc: ctc, salaryJson: calcSalary(ctc) })
  const applyClient = (value: string) => { const clientId = Number(value.split(':')[0] || 0); const selectedStructure = setup.salaryStructures.find(item => item.clientId.split(':')[0] === String(clientId)); const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setEmployee({ ...employee, clientId, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc, salaryJson: calcSalary(ctc, selectedStructure) }) }
  const newEmployee = () => {
    const selectedStructure = setup.salaryStructures.find(item => !!clientFilter && item.clientId.split(':')[0] === String(clientFilter))
    const ctc = Number(selectedStructure?.annualCtc || 0)
    setEmployee(clientFilter ? { ...employee0, clientId: clientFilter, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc, salaryJson: calcSalary(ctc, selectedStructure) } : employee0)
    setEmployeeTab('Basics'); setModalOpen(true)
  }
  const editEmployee = (row: Employee) => { setEmployee(normalizeEmployeeSalary(row, true)); setEmployeeTab('Basics'); setModalOpen(true) }
  const closeModal = () => { setModalOpen(false); setEmployee(employee0); setEmployeeTab('Basics') }
  const saveEmployee = async () => { const response = await persistEmployee(normalizeEmployeeSalary(employee)); if (response.ok) { closeModal(); await load() } }
  const removeEmployee = async (row: Employee) => { if (!window.confirm(`Delete ${row.firstName} ${row.lastName}`.trim() || row.employeeCode)) return; const response = await deleteEmployee(row.id); setImportMessage(response.ok ? 'Employee removed.' : response.error); if (response.ok) await load() }
  const importFile = async (file: File) => {
    setImporting(true)
    const response = await importEmployees(clientFilter, file)
    setImporting(false)
    if (!response.ok) { setImportMessage(response.error); return }
    const data = response.data
    setImportMessage(`Imported ${data.importedCount}, updated ${data.updatedCount}, skipped ${data.skippedCount}.`)
    await load()
  }
  const downloadSample = async () => {
    const response = await downloadEmployeeImportSample()
    if (!response.ok || !response.data) { setImportMessage(response.error); return }
    const url = URL.createObjectURL(response.data)
    const link = document.createElement('a')
    link.href = url; link.download = 'employee-import-sample.xlsx'; link.click()
    URL.revokeObjectURL(url)
  }
  const visibleEmployees = employees.filter(row => (!clientFilter || row.clientId === clientFilter) && `${row.employeeCode} ${row.firstName} ${row.lastName} ${row.department} ${row.designation} ${row.workEmail}`.toLowerCase().includes(query.toLowerCase()))

  return <section className="employee-master">
    <EmployeeDirectory clients={clients} employees={visibleEmployees} allCount={employees.length} clientFilter={clientFilter} setClientFilter={setClientFilter} query={query} setQuery={setQuery} importing={importing} importMessage={importMessage} onSample={downloadSample} onImport={importFile} onNew={newEmployee} onEdit={editEmployee} onDelete={removeEmployee} />
    {modalOpen && <div className="employee-modal-backdrop" onClick={closeModal}>
      <section className="employee-modal" role="dialog" aria-modal="true" aria-label="Employee details" onClick={event => event.stopPropagation()}>
        <EmployeePanel employee={employee} setEmployee={row => setEmployee(normalizeEmployeeSalary(row))} employeeTab={employeeTab} setEmployeeTab={setEmployeeTab} clients={clients} locations={locations} templates={setup.salaryStructures} deps={deps} desigs={desigs} applyClient={applyClient} applyStructure={applyStructure} applyCtc={applyCtc} structureComponents={structureComponents} employeeSalary={employeeSalary} empLine={empLine} empMonthly={empMonthly} saveEmployee={saveEmployee} closeModal={closeModal} />
      </section>
    </div>}
  </section>
}

function EmployeeDirectory(p: { clients: Client[]; employees: Employee[]; allCount: number; clientFilter: number; setClientFilter: (id: number) => void; query: string; setQuery: (value: string) => void; importing: boolean; importMessage: string; onSample: () => void; onImport: (file: File) => void; onNew: () => void; onEdit: (employee: Employee) => void; onDelete: (employee: Employee) => void }) {
  const clientName = (id: number) => p.clients.find(client => client.id === id)?.name ?? `Client #${id || '-'}`
  return <section className="card employee-directory"><header><i className="blue">E</i><div><h3>Employee master</h3><p>Search client-wise employees. Create or edit details in a focused popup.</p></div><div className="employee-directory-head-actions"><button type="button" className="secondary" onClick={p.onSample}>Sample Excel</button><label className="secondary">Import<input type="file" accept=".csv,.xlsx,.xls" disabled={p.importing} onChange={event => { const file = event.target.files?.[0]; if (file) p.onImport(file); event.target.value = '' }} /></label><button type="button" onClick={p.onNew}>New employee</button></div></header>
    <div className="employee-directory-tools"><label><span>Client</span><select value={p.clientFilter} onChange={event => p.setClientFilter(Number(event.target.value))}><option value="0">All clients</option>{p.clients.map(client => <option value={client.id} key={client.id}>{client.name}</option>)}</select></label><label><span>Search</span><input value={p.query} onChange={event => p.setQuery(event.target.value)} placeholder="Code, name, department, email..." /></label><div><span>Showing</span><b>{p.employees.length} / {p.allCount}</b></div></div>
    {p.importMessage && <p className="employee-import-message">{p.importing ? 'Importing...' : p.importMessage}</p>}
    <DataTable rows={p.employees} emptyText="No employees found for the selected filters." exportFileName="employees" columns={[
      { key: 'employeeName', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), render: row => <><strong>{row.firstName} {row.lastName}</strong><small>{row.employeeCode}</small></> },
      { key: 'clientName', label: 'Client', value: row => clientName(row.clientId) },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'workEmail', label: 'Work email' },
      { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
    ]} actions={row => <span className="row-actions"><button type="button" onClick={() => p.onEdit(row)}>Edit</button><button type="button" className="danger" onClick={() => p.onDelete(row)}>Delete</button></span>} />
  </section>
}

function EmployeePanel(p: { employee: Employee; setEmployee: (employee: Employee) => void; employeeTab: 'Basics' | 'Salary' | 'Personal' | 'Payment'; setEmployeeTab: (tab: 'Basics' | 'Salary' | 'Personal' | 'Payment') => void; clients: Client[]; locations: WorkLocation[]; templates: Structure[]; deps: string[]; desigs: string[]; applyClient: (value: string) => void; applyStructure: (value: string) => void; applyCtc: (value: number) => void; structureComponents: Component[]; employeeSalary: Record<string, string>; empLine: (id: string, value: string) => void; empMonthly: (component: Component) => number; saveEmployee: () => void; closeModal: () => void }) {
  const personal = safeJsonRecord(p.employee.personalJson), payment = safeJsonRecord(p.employee.paymentJson)
  const salaryRows = p.structureComponents.map(component => ({ component, monthly: p.empMonthly(component), annual: p.empMonthly(component) * 12 }))
  const totals = calculateSalaryTotals(salaryRows.map(row => ({ line: { componentId: String(row.component.id), value: '' }, ...row })))
  const badgeClass = (category: string) => category.toLowerCase().replace(/\s+/g, '-')
  const setPersonal = (key: string, value: string) => p.setEmployee({ ...p.employee, personalJson: JSON.stringify({ ...personal, [key]: value }) })
  const setPayment = (key: string, value: string) => p.setEmployee({ ...p.employee, paymentJson: JSON.stringify({ ...payment, [key]: value }) })
  return <section className="employee-card"><header><div><span className="eyebrow purple">{p.employee.id ? 'Edit employee' : 'New employee'}</span><h3>{p.employee.id ? `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode : 'Employee details'}</h3><p>Client linked profile, salary template and payroll details.</p></div><button type="button" className="employee-modal-close" onClick={p.closeModal}>×</button></header><PageTabs items={employeeTabs} value={p.employeeTab} onChange={p.setEmployeeTab} label="Employee detail sections" />
    {p.employeeTab === 'Basics' && <div className="grid"><F l="Client"><Sel v={String(p.employee.clientId || '')} set={p.applyClient} a={p.clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Employee code"><input value={p.employee.employeeCode} onChange={event => p.setEmployee({ ...p.employee, employeeCode: event.target.value })} /></F><F l="First name"><input value={p.employee.firstName} onChange={event => p.setEmployee({ ...p.employee, firstName: event.target.value })} /></F><F l="Last name"><input value={p.employee.lastName} onChange={event => p.setEmployee({ ...p.employee, lastName: event.target.value })} /></F><F l="Gender"><Sel v={p.employee.gender} set={value => p.setEmployee({ ...p.employee, gender: value })} a={['Male', 'Female', 'Other']} /></F><F l="Date of joining"><input type="date" value={p.employee.dateOfJoining} onChange={event => p.setEmployee({ ...p.employee, dateOfJoining: event.target.value })} /></F><F l="Work email"><input value={p.employee.workEmail} onChange={event => p.setEmployee({ ...p.employee, workEmail: event.target.value })} /></F><F l="Department"><Sel v={p.employee.department} set={value => p.setEmployee({ ...p.employee, department: value })} a={p.deps} /></F><F l="Designation"><Sel v={p.employee.designation} set={value => p.setEmployee({ ...p.employee, designation: value })} a={p.desigs} /></F><F l="Work location"><Sel v={String(p.employee.workLocationId || '')} set={value => p.setEmployee({ ...p.employee, workLocationId: Number(value.split(':')[0] || 0) })} a={p.locations.map(item => `${item.id}:${item.name}`)} /></F><Chk l="Portal access" v={p.employee.portalAccess} set={value => p.setEmployee({ ...p.employee, portalAccess: value })} /><Chk l="Active" v={p.employee.isActive} set={value => p.setEmployee({ ...p.employee, isActive: value })} /></div>}
    {p.employeeTab === 'Salary' && <div className="employee-salary-panel">
      <div className="employee-salary-controls"><F l="Salary template"><Sel v={p.employee.salaryStructureId} set={p.applyStructure} a={p.templates.filter(item => !p.employee.clientId || item.clientId.split(':')[0] === String(p.employee.clientId)).map(item => `${item.id}:${item.name}`)} /></F><F l="Annual CTC"><input value={p.employee.annualCtc} onChange={event => p.applyCtc(Number(event.target.value.replace(/\D/g, '')))} /></F></div>
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
    {p.employeeTab === 'Personal' && <div className="grid"><F l="Date of birth"><input type="date" value={personal.dob || ''} onChange={event => setPersonal('dob', event.target.value)} /></F><F l="PAN"><input value={personal.pan || ''} onChange={event => setPersonal('pan', event.target.value.toUpperCase())} /></F><F l="Aadhaar"><input value={personal.aadhaar || ''} onChange={event => setPersonal('aadhaar', event.target.value.replace(/\D/g, '').slice(0, 12))} /></F><F l="Mobile"><input value={personal.mobile || ''} onChange={event => setPersonal('mobile', event.target.value)} /></F><F l="Address" w><input value={personal.address || ''} onChange={event => setPersonal('address', event.target.value)} /></F></div>}
    {p.employeeTab === 'Payment' && <div className="grid"><F l="Bank"><input value={payment.bank || ''} onChange={event => setPayment('bank', event.target.value)} /></F><F l="Account no"><input value={payment.account || ''} onChange={event => setPayment('account', event.target.value)} /></F><F l="IFSC"><input value={payment.ifsc || ''} onChange={event => setPayment('ifsc', event.target.value.toUpperCase())} /></F><F l="Payment mode"><Sel v={payment.mode || ''} set={value => setPayment('mode', value)} a={['Bank Transfer', 'Cheque', 'Cash']} /></F></div>}
    <div className="actions"><button type="button" className="secondary" onClick={p.closeModal}>Cancel</button><button type="button" onClick={p.saveEmployee}>Save employee</button></div></section>
}
