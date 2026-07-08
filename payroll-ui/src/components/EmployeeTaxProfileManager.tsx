import { useEffect, useMemo, useState } from 'react'
import DataTable from './DataTable'
import { Card, F, Sel } from './FormPrimitives'
import { useToast } from './ToastProvider'
import { getClients, getEmployees } from '../services/payrollService'
import { getSetup, getWorkLocations } from '../services/settingsService'
import { getEmployeeTaxProfile, getTaxEngineSetup, saveEmployeeTaxProfile, type EmployeeTaxProfile, type EmployeeTaxProfileLine, type TaxEngineSetup } from '../services/taxEngineService'
import { setup0 } from '../data/payrollDefaults'
import type { Client, Employee, Structure, TaxDeclarationSection, WorkLocation } from '../types/payroll'

const fy = `${new Date().getFullYear()}-${String(new Date().getFullYear() + 1).slice(2)}`

export default function EmployeeTaxProfileManager() {
  const notify = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [salaryStructures, setSalaryStructures] = useState<Structure[]>([])
  const [clientId, setClientId] = useState('')
  const [workLocationId, setWorkLocationId] = useState('')
  const [department, setDepartment] = useState('')
  const [designation, setDesignation] = useState('')
  const [employeeSearch, setEmployeeSearch] = useState('')
  const [financialYear, setFinancialYear] = useState(fy)
  const [searched, setSearched] = useState(false)
  const [resultRows, setResultRows] = useState<Employee[]>([])
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const [profile, setProfile] = useState<EmployeeTaxProfile | null>(null)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [loadingProfile, setLoadingProfile] = useState(false)
  const [savingProfile, setSavingProfile] = useState(false)
  const [profileMessage, setProfileMessage] = useState('')
  const [taxSetup, setTaxSetup] = useState<TaxEngineSetup | null>(null)
  const [message, setMessage] = useState('Fill any criteria and click Search.')

  useEffect(() => {
    void Promise.all([getClients(), getEmployees(), getWorkLocations(), getTaxEngineSetup(), getSetup(setup0)]).then(([clientRows, employeeRows, locationRows, setup, payrollSetup]) => {
      setClients(clientRows)
      setEmployees(employeeRows.filter(row => row.isActive))
      setLocations(locationRows.filter(row => row.isActive))
      setTaxSetup(setup)
      setSalaryStructures(payrollSetup.salaryStructures ?? [])
    })
  }, [])

  const filteredEmployees = useMemo(() => employees.filter(employee => {
    const location = locations.find(row => row.id === employee.workLocationId)
    const haystack = [employee.employeeCode, employee.firstName, employee.lastName, employee.department, employee.designation, location?.name, location?.city].filter(Boolean).join(' ').toLowerCase()
    return (!clientId || String(employee.clientId) === clientId)
      && (!workLocationId || String(employee.workLocationId) === workLocationId)
      && (!department || employee.department === department)
      && (!designation || employee.designation === designation)
      && (!employeeSearch.trim() || haystack.includes(employeeSearch.trim().toLowerCase()))
  }), [clientId, department, designation, employeeSearch, employees, locations, workLocationId])

  const clientLocations = locations.filter(location => !clientId || String(location.clientId) === clientId)
  const departments = Array.from(new Set(employees.filter(employee => !clientId || String(employee.clientId) === clientId).map(employee => employee.department).filter(Boolean))).sort()
  const designations = Array.from(new Set(employees.filter(employee => !clientId || String(employee.clientId) === clientId).map(employee => employee.designation).filter(Boolean))).sort()
  const payGroupName = (employee: Employee) => {
    if (!employee.salaryStructureId) return '-'
    const structure = salaryStructures.find(row => String(row.id) === String(employee.salaryStructureId))
    return structure?.name || `Pay group #${employee.salaryStructureId}`
  }

  const search = () => {
    setResultRows(filteredEmployees)
    setSearched(true)
    setMessage(filteredEmployees.length ? `${filteredEmployees.length} employee${filteredEmployees.length === 1 ? '' : 's'} found.` : 'No employees found for selected criteria.')
    setProfile(null)
    setSelectedEmployee(null)
    setDrawerOpen(false)
  }

  const clearFilters = () => {
    setClientId('')
    setWorkLocationId('')
    setDepartment('')
    setDesignation('')
    setEmployeeSearch('')
    setResultRows([])
    setSearched(false)
    setSelectedEmployee(null)
    setProfile(null)
    setDrawerOpen(false)
    setMessage('Fill any criteria and click Search.')
  }

  const openProfile = async (employee: Employee) => {
    setSelectedEmployee(employee)
    setDrawerOpen(true)
    setLoadingProfile(true)
    setProfileMessage('Preparing employee tax profile...')
    const row = await getEmployeeTaxProfile(employee.id, financialYear)
    const nextProfile = row ?? createBlankProfile(employee, financialYear, taxSetup)
    setProfile(nextProfile)
    const nextMessage = row ? 'Tax profile loaded.' : 'Default New regime profile is ready. Enter declaration values only if required.'
    setMessage(nextMessage)
    setProfileMessage(nextMessage)
    setLoadingProfile(false)
  }

  const closeDrawer = () => {
    setDrawerOpen(false)
    setSelectedEmployee(null)
    setProfile(null)
    setProfileMessage('')
    setSavingProfile(false)
  }

  const updateLine = (sectionId: number, patch: Partial<EmployeeTaxProfileLine>) => {
    if (!profile) return
    setProfile({ ...profile, lines: profile.lines.map(line => line.sectionId === sectionId ? { ...line, ...patch } : line) })
  }

  const save = async () => {
    if (!profile) {
      setMessage('Open an employee tax profile first.')
      setProfileMessage('Open an employee tax profile first.')
      return
    }
    setSavingProfile(true)
    setProfileMessage('Saving tax profile...')
    const payload = selectedEmployee ? { ...profile, employeeId: selectedEmployee.id, clientId: selectedEmployee.clientId } : profile
    const result = await saveEmployeeTaxProfile(payload)
    const nextMessage = result.ok ? 'Employee tax profile saved.' : result.error || `Save failed with status ${result.status}.`
    setMessage(nextMessage)
    setProfileMessage(nextMessage)
    notify(nextMessage, result.ok ? 'success' : 'error')
    if (result.ok && result.data) setProfile(result.data)
    setSavingProfile(false)
  }

  const resetScope = (patch: Partial<{ clientId: string; workLocationId: string; department: string; designation: string }>) => {
    if (patch.clientId !== undefined) {
      setClientId(patch.clientId)
      setWorkLocationId('')
      setDepartment('')
      setDesignation('')
    }
    if (patch.workLocationId !== undefined) setWorkLocationId(patch.workLocationId)
    if (patch.department !== undefined) setDepartment(patch.department)
    if (patch.designation !== undefined) setDesignation(patch.designation)
    setResultRows([])
    setSearched(false)
    setProfile(null)
    setSelectedEmployee(null)
    setDrawerOpen(false)
  }

  return <Card t="Employee Tax Profile">
    <div className="component-guide tax-guide"><b>Employee tax profile</b><span>Search employees first, then open one employee to maintain regime, planned declaration, and approved POI values.</span></div>
    <section className="tax-rule-card"><h3>Search criteria</h3><div className="grid">
      <F l="Client"><Sel v={clientId} set={value => resetScope({ clientId: value })} a={clients.map(client => `${client.id}:${client.name}`)} /></F>
      <F l="Work location"><Sel v={workLocationId} set={value => resetScope({ workLocationId: value })} a={clientLocations.map(location => `${location.id}:${location.name} - ${location.city || location.state || 'Location'}`)} /></F>
      <F l="Department"><Sel v={department} set={value => resetScope({ department: value })} a={departments} /></F>
      <F l="Designation"><Sel v={designation} set={value => resetScope({ designation: value })} a={designations} /></F>
      <F l="Financial year"><input value={financialYear} onChange={event => { setFinancialYear(event.target.value); setResultRows([]); setSearched(false) }} placeholder="2026-27" /></F>
      <F l="Employee search"><input value={employeeSearch} onChange={event => { setEmployeeSearch(event.target.value); setResultRows([]); setSearched(false) }} placeholder="Code, name, department, designation, location" /></F>
    </div><div className="tax-sticky-actions"><span>{message}</span><button type="button" className="secondary" onClick={clearFilters}>Clear</button><button type="button" onClick={search}>Search</button></div></section>

    {searched && <DataTable rows={resultRows} getRowId={row => row.id} emptyText="No employees found for selected criteria." exportFileName="employee-tax-profile-search" columns={[
      { key: 'employeeCode', label: 'Code' },
      { key: 'name', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim() },
      { key: 'clientId', label: 'Client', value: row => clients.find(client => client.id === row.clientId)?.name || `Client #${row.clientId}` },
      { key: 'workLocationId', label: 'Work location', value: row => locations.find(location => location.id === row.workLocationId)?.name || '-' },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'salaryStructureId', label: 'Pay group', value: row => payGroupName(row) }
    ]} actions={row => <button type="button" onClick={() => void openProfile(row)}>Tax Profile</button>} />}

    {drawerOpen && <div className="component-drawer-backdrop tax-profile-modal-backdrop" onClick={closeDrawer}>
      <aside className="component-drawer tax-profile-modal" role="dialog" aria-modal="true" aria-label="Employee tax profile" onClick={event => event.stopPropagation()}>
        <header><div><span className="eyebrow purple">Tax profile</span><h3>{selectedEmployee ? `${selectedEmployee.employeeCode} - ${selectedEmployee.firstName} ${selectedEmployee.lastName}` : 'Employee tax profile'}</h3><p>{financialYear} / {profile?.regime || 'New'} regime / {profile?.regimeStatus || 'Draft'}</p></div><button type="button" aria-label="Close tax profile drawer" onClick={closeDrawer}>x</button></header>
        <div className="component-drawer-form tax-profile-summary">
          {loadingProfile && <p className="component-guide"><b>Loading</b><span>Preparing employee tax profile.</span></p>}
          {profile && <>
            <F l="Employee"><input value={`${profile.employeeCode} - ${profile.employeeName}`} readOnly /></F>
            <F l="Regime"><Sel v={profile.regime || 'New'} set={value => setProfile({ ...profile, regime: value as 'Old' | 'New' })} a={['Old', 'New']} /></F>
            <F l="TDS deduction source"><input value={profile.deductionSource} readOnly /></F>
            <F l="Financial year"><input value={profile.financialYear} readOnly /></F>
          </>}
        </div>
        {profile && <div className="tax-profile-lines">
          {profile.lines.length === 0 ? <p className="component-guide"><b>No declaration sections</b><span>No active declaration sections are configured for this financial year.</span></p> : <table>
            <thead><tr><th>Section</th><th>Limit</th><th>Planned</th><th>Actual / POI</th><th>Approved POI</th><th>Remarks</th></tr></thead>
            <tbody>{profile.lines.map(line => <tr key={line.sectionId}>
              <td><b>{line.code}</b><span>{line.name}</span></td>
              <td>{line.limitAmount ?? 'No limit'}</td>
              <td><input value={String(line.plannedAmount || '')} onChange={event => updateLine(line.sectionId, { plannedAmount: Number(money(event.target.value) || 0) })} /></td>
              <td><input value={String(line.actualAmount || '')} onChange={event => updateLine(line.sectionId, { actualAmount: Number(money(event.target.value) || 0) })} /></td>
              <td><input value={String(line.approvedAmount || '')} onChange={event => updateLine(line.sectionId, { approvedAmount: Number(money(event.target.value) || 0) })} /></td>
              <td><input value={line.remarks || ''} onChange={event => updateLine(line.sectionId, { remarks: event.target.value })} /></td>
            </tr>)}</tbody>
          </table>}
        </div>}
        <footer><span className="tax-profile-save-status">{profileMessage}</span><button type="button" className="secondary" disabled={savingProfile} onClick={closeDrawer}>Cancel</button><button type="button" disabled={!profile || loadingProfile || savingProfile} onClick={() => void save()}>{savingProfile ? 'Saving...' : 'Save tax profile'}</button></footer>
      </aside>
    </div>}
  </Card>
}

function money(value: string) {
  return value.replace(/[^\d.]/g, '').replace(/(\..*)\./g, '$1')
}

function createBlankProfile(employee: Employee, financialYear: string, taxSetup: TaxEngineSetup | null): EmployeeTaxProfile {
  const clientRule = taxSetup?.clientSettings.find(row => String(row.clientId) === String(employee.clientId) && row.financialYear === financialYear && row.active)
  return {
    employeeId: employee.id,
    clientId: employee.clientId,
    employeeCode: employee.employeeCode,
    employeeName: `${employee.firstName} ${employee.lastName}`.trim(),
    financialYear,
    regime: 'New',
    regimeStatus: 'Draft',
    deductionSource: clientRule?.poiProcessingMonth ? `Planned until ${clientRule.poiProcessingMonth}, then POI` : 'Planned',
    lines: (taxSetup?.declarationSections ?? [])
      .filter((section: TaxDeclarationSection) => section.financialYear === financialYear && section.active && ['Old', 'Both'].includes(section.regime))
      .map(section => ({
        sectionId: section.id,
        code: section.code,
        name: section.name,
        regime: section.regime,
        limitAmount: section.limitAmount,
        plannedAmount: 0,
        actualAmount: 0,
        approvedAmount: 0,
        status: 'Draft',
        remarks: ''
      }))
  }
}
