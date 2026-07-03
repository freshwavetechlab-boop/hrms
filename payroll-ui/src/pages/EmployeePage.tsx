import { useEffect, useState } from 'react'
import { Chk, F, Sel } from '../components/FormPrimitives'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from '../components/BulkUploadProgressModal'
import DataTable from '../components/DataTable'
import PageTabs from '../components/PageTabs'
import SearchSelect, { selectOptions } from '../components/SearchSelect'
import { useToast } from '../components/ToastProvider'
import { employee0, setup0 } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { deleteEmployee as removeEmployee, getDropdowns, getEmployeeDeletePreview, getEmployeeImportJob, getSetup, getWorkLocations, saveEmployee as persistEmployee, startEmployeeImport } from '../services/settingsService'
import type { Client, Component, Drop, Employee, EmployeePaymentDetails, EmployeePersonalDetails, Setup, Structure, WorkLocation } from '../types/payroll'
import { calculateSalaryJson, calculateSalaryTotals, money } from '../utils/salary'
import { downloadXlsx } from '../utils/xlsx'
import { safeJsonRecord } from '../shared/json'
import '../TemplateDesigner.css'

const employeeTabs = ['Basics', 'Salary', 'Personal', 'Payment'] as const
const personal0 = employee0.personalDetails
const payment0 = employee0.paymentDetails
const employeeImportHeaders = ['Employee Code', 'First Name', 'Last Name', 'Gender', 'Date Of Joining', 'Work Email', 'Department', 'Designation', 'Grade', 'Work Location', 'Annual CTC', 'Date Of Birth', 'Mobile', 'PAN', 'Aadhaar', 'UAN Number', 'Address', 'Correspondence Address', 'Permanent Address']
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))

export default function EmployeePage() {
  const notify = useToast()
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [drops, setDrops] = useState<Drop[]>([]), [setup, setSetup] = useState<Setup>(setup0)
  const [employees, setEmployees] = useState<Employee[]>([]), [employee, setEmployee] = useState(employee0), [employeeTab, setEmployeeTab] = useState<'Basics' | 'Salary' | 'Personal' | 'Payment'>('Basics')
  const [modalOpen, setModalOpen] = useState(false), [clientFilter, setClientFilter] = useState(0), [query, setQuery] = useState('')
  const [upload, setUpload] = useState<{ open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const clientStructure = templatesForClient(setup.salaryStructures, employee.clientId)[0]
  const chosenStructure = setup.salaryStructures.find(item => String(item.id) === employee.salaryStructureId) ?? clientStructure
  const rawEmployeeSalary = salaryRecord(employee)
  const structureLineIds = chosenStructure?.lines.map(line => line.componentId) ?? []
  const linkedSalaryHasCurrentIds = structureLineIds.some(id => rawEmployeeSalary[id] !== undefined)
  const employeeSalary = linkedSalaryHasCurrentIds || !chosenStructure || !employee.annualCtc ? rawEmployeeSalary : safeJsonRecord(calculateSalaryJson(employee.annualCtc, setup.salaryComponents, chosenStructure))
  const structureComponents = setup.salaryComponents.filter(component => component.active && structureLineIds.includes(String(component.id))).sort((a, b) => structureLineIds.indexOf(String(a.id)) - structureLineIds.indexOf(String(b.id)) || Number(a.priority) - Number(b.priority))
  const deps = drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), desigs = drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value)
  const grades = drops.filter(item => item.type === 'Employee Grade' && item.isActive && (!item.clientId || item.clientId === employee.clientId)).map(item => item.value)

  const load = async () => {
    const [clientRows, locationRows, dropdownRows, employeeRows, rawSetup] = await Promise.all([getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getSetup(setup0)])
    const activeClientIds = new Set(clientRows.map(client => client.id))
    const activeLocations = locationRows.filter(location => location.isActive && activeClientIds.has(location.clientId))
    setClients(clientRows); setLocations(activeLocations); setDrops(dropdownRows); setEmployees(employeeRows.filter(employee => activeClientIds.has(employee.clientId)).map(normalizeEmployeeDetails))
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
  const deleteEmployee = async (row: Employee) => {
    const preview = await getEmployeeDeletePreview(row.id)
    if (preview.links.length) { notify(`Cannot delete ${preview.employeeName || row.employeeCode}. Linked records: ${preview.links.join(' | ')}`, 'warning'); return }
    if (!window.confirm(`Delete employee ${row.employeeCode}?`)) return
    const response = await removeEmployee(row.id)
    notify(response.ok ? 'Employee deleted.' : response.error || 'Unable to delete employee.', response.ok ? 'success' : 'error')
    if (response.ok) await load()
  }
  const downloadTemplate = async () => {
    if (!clientFilter) { setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Select a client before downloading employee template.'] } }); return }
    const client = clients.find(item => item.id === clientFilter)
    const departments = drops.filter(item => item.isActive && item.type === 'Department').map(item => item.value)
    const designations = drops.filter(item => item.isActive && item.type === 'Designation').map(item => item.value)
    const gradeValues = drops.filter(item => item.isActive && item.type === 'Employee Grade' && (!item.clientId || item.clientId === clientFilter)).map(item => item.value)
    const locationValues = locations.filter(item => item.isActive && item.clientId === clientFilter).map(item => item.name)
    downloadXlsx('employee-import-template.xlsx', [
      { name: 'Employees', rows: [employeeImportHeaders, ['EMP001', 'Rahul', 'Sharma', 'Male', '2026-04-01', 'rahul@example.com', departments[0] || '', designations[0] || '', gradeValues[0] || '', locationValues[0] || '', '600000', '1995-01-15', '9876543210', 'ABCDE1234F', '123412341234', '100200300400', 'Local address', 'Correspondence address', 'Permanent address']] },
      { name: 'Masters', rows: [['Master Type', 'Value', 'Id'], ['Client', client?.name || '', String(clientFilter)], ...departments.map(value => ['Department', value, '']), ...designations.map(value => ['Designation', value, '']), ...gradeValues.map(value => ['Employee Grade', value, '']), ...locations.filter(item => item.isActive && item.clientId === clientFilter).map(item => ['Work Location', item.name, String(item.id)])] }
    ])
  }
  const uploadEmployees = async (file: File | null) => {
    if (!file) return
    if (!clientFilter) { setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Select a client before bulk upload.'] } }); return }
    setUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: 0 } })
    const start = await startEmployeeImport(clientFilter, file)
    if (!start.ok || !start.data.jobId) { setUpload({ open: true, state: 'error', percent: 100, summary: { ...start.data, errors: start.data.errors?.length ? start.data.errors : [start.error || 'Upload failed.'] } }); return }
    let job = start.data
    while (job.state === 'Queued' || job.state === 'Processing') {
      const percent = job.totalRows ? Math.min(99, Math.round((job.completedRows / job.totalRows) * 100)) : 5
      setUpload({ open: true, state: 'uploading', percent, summary: job })
      await wait(700)
      job = await getEmployeeImportJob(job.jobId)
    }
    const percent = job.totalRows ? Math.round((job.completedRows / job.totalRows) * 100) : 100
    if (job.state === 'Completed') { setUpload({ open: true, state: 'success', percent: 100, summary: job }); await load(); return }
    setUpload({ open: true, state: 'error', percent, summary: { ...job, errors: job.errors?.length ? job.errors : ['Import failed. No rows were saved.'] } })
  }
  const visibleEmployees = employees.filter(row => row.isActive && (!clientFilter || row.clientId === clientFilter) && `${row.employeeCode} ${row.firstName} ${row.lastName} ${row.department} ${row.designation} ${row.workEmail}`.toLowerCase().includes(query.toLowerCase()))

  return <section className="employee-master">
    <EmployeeDirectory clients={clients} employees={visibleEmployees} allCount={employees.length} clientFilter={clientFilter} setClientFilter={setClientFilter} query={query} setQuery={setQuery} onNew={newEmployee} onEdit={editEmployee} onDelete={deleteEmployee} onDownloadTemplate={downloadTemplate} onUpload={uploadEmployees} />
    {modalOpen && <div className="employee-modal-backdrop" onClick={closeModal}>
      <section className="employee-modal" role="dialog" aria-modal="true" aria-label="Employee details" onClick={event => event.stopPropagation()}>
        <EmployeePanel employee={employee} setEmployee={row => setEmployee(normalizeEmployeeSalary(row))} employeeTab={employeeTab} setEmployeeTab={setEmployeeTab} clients={clients} locations={locations} templates={setup.salaryStructures} deps={deps} desigs={desigs} grades={grades} applyClient={applyClient} applyStructure={applyStructure} applyCtc={applyCtc} structureComponents={structureComponents} employeeSalary={employeeSalary} empLine={empLine} empMonthly={empMonthly} saveEmployee={saveEmployee} closeModal={closeModal} />
      </section>
    </div>}
    <BulkUploadProgressModal open={upload.open} title="Employee bulk upload" state={upload.state} percent={upload.percent} summary={upload.summary} onClose={() => setUpload(current => ({ ...current, open: false }))} />
  </section>
}

function EmployeeDirectory(p: { clients: Client[]; employees: Employee[]; allCount: number; clientFilter: number; setClientFilter: (id: number) => void; query: string; setQuery: (value: string) => void; onNew: () => void; onEdit: (employee: Employee) => void; onDelete: (employee: Employee) => void; onDownloadTemplate: () => void; onUpload: (file: File | null) => void }) {
  const clientName = (id: number) => p.clients.find(client => client.id === id)?.name ?? `Client #${id || '-'}`
  return <section className="card employee-directory"><header><i className="blue">E</i><div><h3>Employee master</h3><p>Search client-wise employees. Create or edit details in a focused popup.</p></div><div className="employee-directory-actions"><button type="button" disabled={!p.clientFilter} title={p.clientFilter ? 'Download Excel template' : 'Select a client first'} onClick={p.onDownloadTemplate}>Download Excel template</button><label className={`employee-upload-action ${!p.clientFilter ? 'disabled' : ''}`} title={p.clientFilter ? 'Upload Excel or CSV' : 'Select a client first'}><input type="file" disabled={!p.clientFilter} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { p.onUpload(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} />Bulk upload</label><button type="button" onClick={p.onNew}>New employee</button></div></header>
    <div className="employee-directory-tools"><label><span>Client</span><SearchSelect value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={selectOptions(p.clients.map(client => ({ value: client.id, label: client.name })), 'All clients', 0)} /></label><label><span>Search</span><input value={p.query} onChange={event => p.setQuery(event.target.value)} placeholder="Code, name, department, email..." /></label><div className="employee-directory-count"><span>Showing</span><b>{p.employees.length} / {p.allCount}</b></div></div>
    <DataTable rows={p.employees} emptyText="No employees found for the selected filters." exportFileName="employees" columns={[
      { key: 'employeeName', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), render: row => <strong>{row.firstName} {row.lastName}</strong> },
      { key: 'employeeCode', label: 'Code' },
      { key: 'clientName', label: 'Client', value: row => clientName(row.clientId) },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'grade', label: 'Grade' },
      { key: 'workEmail', label: 'Work email' },
      { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
    ]} actions={row => <span className="row-actions"><button type="button" onClick={() => p.onEdit(row)}>Edit</button><button type="button" className="danger" onClick={() => p.onDelete(row)}>Delete</button></span>} />
  </section>
}

function EmployeePanel(p: { employee: Employee; setEmployee: (employee: Employee) => void; employeeTab: 'Basics' | 'Salary' | 'Personal' | 'Payment'; setEmployeeTab: (tab: 'Basics' | 'Salary' | 'Personal' | 'Payment') => void; clients: Client[]; locations: WorkLocation[]; templates: Structure[]; deps: string[]; desigs: string[]; grades: string[]; applyClient: (value: string) => void; applyStructure: (value: string) => void; applyCtc: (value: number) => void; structureComponents: Component[]; employeeSalary: Record<string, string>; empLine: (id: string, value: string) => void; empMonthly: (component: Component) => number; saveEmployee: () => void; closeModal: () => void }) {
  const personal = p.employee.personalDetails, payment = p.employee.paymentDetails
  const salaryRows = p.structureComponents.map(component => ({ component, monthly: p.empMonthly(component), annual: p.empMonthly(component) * 12 }))
  const totals = calculateSalaryTotals(salaryRows.map(row => ({ line: { componentId: String(row.component.id), value: '' }, ...row })))
  const badgeClass = (category: string) => category.toLowerCase().replace(/\s+/g, '-')
  const setPersonal = <K extends keyof EmployeePersonalDetails>(key: K, value: EmployeePersonalDetails[K]) => p.setEmployee({ ...p.employee, personalDetails: { ...personal, [key]: value } })
  const copyCorrespondence = (checked: boolean) => p.setEmployee({ ...p.employee, personalDetails: { ...personal, permanentAddress: checked ? personal.correspondenceAddress : personal.permanentAddress } })
  const setPayment = <K extends keyof EmployeePaymentDetails>(key: K, value: EmployeePaymentDetails[K]) => p.setEmployee({ ...p.employee, paymentDetails: { ...payment, [key]: value } })
  return <section className="employee-card"><header><div><span className="eyebrow purple">{p.employee.id ? 'Edit employee' : 'New employee'}</span><h3>{p.employee.id ? `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode : 'Employee details'}</h3><p>Client linked profile, salary template and payroll details.</p></div><button type="button" className="employee-modal-close" onClick={p.closeModal}>×</button></header><PageTabs items={employeeTabs} value={p.employeeTab} onChange={p.setEmployeeTab} label="Employee detail sections" />
    {p.employeeTab === 'Basics' && <div className="grid"><F l="Client"><Sel v={String(p.employee.clientId || '')} set={p.applyClient} a={p.clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Employee code"><input value={p.employee.employeeCode} onChange={event => p.setEmployee({ ...p.employee, employeeCode: event.target.value })} /></F><F l="First name"><input value={p.employee.firstName} onChange={event => p.setEmployee({ ...p.employee, firstName: event.target.value })} /></F><F l="Last name"><input value={p.employee.lastName} onChange={event => p.setEmployee({ ...p.employee, lastName: event.target.value })} /></F><F l="Gender"><Sel v={p.employee.gender} set={value => p.setEmployee({ ...p.employee, gender: value })} a={['Male', 'Female', 'Other']} /></F><F l="Date of joining"><input type="date" value={p.employee.dateOfJoining} onChange={event => p.setEmployee({ ...p.employee, dateOfJoining: event.target.value })} /></F><F l="Work email"><input value={p.employee.workEmail} onChange={event => p.setEmployee({ ...p.employee, workEmail: event.target.value })} /></F><F l="Department"><Sel v={p.employee.department} set={value => p.setEmployee({ ...p.employee, department: value })} a={p.deps} /></F><F l="Designation"><Sel v={p.employee.designation} set={value => p.setEmployee({ ...p.employee, designation: value })} a={p.desigs} /></F><F l="Employee Grade"><Sel v={p.employee.grade} set={value => p.setEmployee({ ...p.employee, grade: value })} a={p.grades} /></F><F l="Work location"><Sel v={String(p.employee.workLocationId || '')} set={value => p.setEmployee({ ...p.employee, workLocationId: Number(value.split(':')[0] || 0) })} a={p.locations.filter(item => item.clientId === p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F><Chk l="Portal access" v={p.employee.portalAccess} set={value => p.setEmployee({ ...p.employee, portalAccess: value })} /><Chk l="Active" v={p.employee.isActive} set={value => p.setEmployee({ ...p.employee, isActive: value })} /></div>}
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
    {p.employeeTab === 'Personal' && <div className="grid"><F l="Date of birth"><input type="date" value={personal.dateOfBirth || ''} onChange={event => setPersonal('dateOfBirth', event.target.value)} /></F><F l="PAN"><input value={personal.panNumber || ''} onChange={event => setPersonal('panNumber', event.target.value.toUpperCase())} /></F><F l="Aadhaar"><input value={personal.aadhaarNumber || ''} onChange={event => setPersonal('aadhaarNumber', event.target.value.replace(/\D/g, '').slice(0, 12))} /></F><F l="UAN Number"><input value={personal.uanNumber || ''} onChange={event => setPersonal('uanNumber', event.target.value)} /></F><F l="Mobile"><input value={personal.mobile || ''} onChange={event => setPersonal('mobile', event.target.value)} /></F><F l="Address"><input value={personal.address || ''} onChange={event => setPersonal('address', event.target.value)} /></F><F l="Correspondence Address" w><input value={personal.correspondenceAddress || ''} onChange={event => setPersonal('correspondenceAddress', event.target.value)} /></F><label className="employee-same-address"><input type="checkbox" checked={!!personal.correspondenceAddress && personal.permanentAddress === personal.correspondenceAddress} onChange={event => copyCorrespondence(event.target.checked)} />Same as correspondence address</label><F l="Permanent Address" w><input value={personal.permanentAddress || ''} onChange={event => setPersonal('permanentAddress', event.target.value)} /></F></div>}
    {p.employeeTab === 'Payment' && <div className="grid"><F l="Bank"><input value={payment.bankName || ''} onChange={event => setPayment('bankName', event.target.value)} /></F><F l="Account no"><input value={payment.bankAccountNo || ''} onChange={event => setPayment('bankAccountNo', event.target.value)} /></F><F l="IFSC"><input value={payment.ifscCode || ''} onChange={event => setPayment('ifscCode', event.target.value.toUpperCase())} /></F><F l="Payment mode"><Sel v={payment.paymentMode || ''} set={value => setPayment('paymentMode', value)} a={['Bank Transfer', 'Cheque', 'Cash']} /></F></div>}
    <div className="actions"><button type="button" className="secondary" onClick={p.closeModal}>Cancel</button><button type="button" onClick={p.saveEmployee}>Save employee</button></div></section>
}

function normalizeEmployeeDetails(row: Employee): Employee {
  const personalJson = safeJsonRecord(row.personalJson)
  const paymentJson = safeJsonRecord(row.paymentJson)
  const salaryComponents = Object.keys(row.salaryComponents || {}).length ? row.salaryComponents : numberRecord(row.salaryJson)
  return {
    ...row,
    grade: row.grade || '',
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
      address: row.personalDetails?.address || personalJson.address || '',
      correspondenceAddress: row.personalDetails?.correspondenceAddress || personalJson.correspondenceAddress || '',
      permanentAddress: row.personalDetails?.permanentAddress || personalJson.permanentAddress || ''
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
    correspondenceAddress: row.personalDetails.correspondenceAddress,
    permanentAddress: row.personalDetails.permanentAddress,
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
