import { useEffect, useMemo, useState } from 'react'
import DataTable from './DataTable'
import { deleteGeoFenceRule, geoFenceFallback, getGeoFenceRules, saveGeoFenceRule } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import { getEmployees } from '../services/payrollService'
import type { Employee, GeoFenceRule, GeoFenceScope, WorkLocation } from '../types/payroll'

const scopes: GeoFenceScope[] = ['Client Default', 'Work Location', 'Employee']
const strictness = ['Block outside fence', 'Allow with reason', 'Allow with approval'] as const

export default function GeoFenceManager({ clientId, clientName, onMessage }: { clientId: number; clientName: string; onMessage: (message: string) => void }) {
  const [rules, setRules] = useState<GeoFenceRule[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [form, setForm] = useState<GeoFenceRule>({ ...geoFenceFallback, clientId })
  const [errors, setErrors] = useState<string[]>([])
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [locationSearch, setLocationSearch] = useState('')
  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [employees, clientId])
  const selectedLocation = locations.find(location => location.id === form.workLocationId)

  const load = async () => {
    const [nextRules, nextLocations, nextEmployees] = await Promise.all([getGeoFenceRules(clientId), getWorkLocations(), getEmployees()])
    setRules(nextRules)
    setLocations(nextLocations.filter(location => location.isActive))
    setEmployees(nextEmployees)
    setForm(current => current.id ? current : { ...geoFenceFallback, clientId })
  }

  useEffect(() => { void load() }, [clientId])

  const set = <K extends keyof GeoFenceRule>(key: K, value: GeoFenceRule[K]) => {
    setErrors([])
    setForm(current => ({ ...current, [key]: value }))
  }

  const changeScope = (scopeType: GeoFenceScope) => setForm(current => ({ ...current, scopeType, workLocationId: scopeType === 'Work Location' ? current.workLocationId : null, employeeIds: scopeType === 'Employee' ? current.employeeIds : [] }))
  const pickLocation = (value: string) => {
    setLocationSearch(value)
    const match = locations.find(location => location.name.toLowerCase() === value.toLowerCase() || `${location.name} - ${location.city}`.toLowerCase() === value.toLowerCase())
    if (match) set('workLocationId', match.id)
  }
  const toggleEmployee = (employeeId: number) => setForm(current => ({ ...current, employeeIds: current.employeeIds.includes(employeeId) ? current.employeeIds.filter(id => id !== employeeId) : [...current.employeeIds, employeeId] }))
  const add = () => { setForm({ ...geoFenceFallback, clientId }); setLocationSearch(''); setErrors([]); setDrawerOpen(true) }
  const edit = (rule: GeoFenceRule) => { setForm({ ...geoFenceFallback, ...rule, effectiveFrom: String(rule.effectiveFrom).slice(0, 10), effectiveTo: rule.effectiveTo ? String(rule.effectiveTo).slice(0, 10) : null }); setLocationSearch(rule.workLocationName || ''); setErrors([]); setDrawerOpen(true) }
  const reset = () => { setForm({ ...geoFenceFallback, clientId }); setLocationSearch(''); setErrors([]) }
  const closeDrawer = () => { reset(); setDrawerOpen(false) }

  const validate = () => {
    const next: string[] = []
    if (!form.name.trim()) next.push('Rule name is required.')
    if (form.scopeType === 'Work Location' && !form.workLocationId) next.push('Select a work location.')
    if (form.scopeType === 'Employee' && form.employeeIds.length === 0) next.push('Select at least one employee.')
    if (form.latitude < -90 || form.latitude > 90 || form.longitude < -180 || form.longitude > 180) next.push('Enter valid latitude and longitude.')
    if (form.radiusMeters < 25 || form.radiusMeters > 5000) next.push('Radius must be between 25 and 5000 meters.')
    if (form.gpsToleranceMeters < 0 || form.gpsToleranceMeters > 500) next.push('GPS tolerance must be between 0 and 500 meters.')
    if (!form.allowCheckIn && !form.allowCheckOut) next.push('Allow at least one attendance action.')
    if (form.effectiveTo && form.effectiveTo < form.effectiveFrom) next.push('Effective to cannot be before effective from.')
    setErrors(next)
    return next.length === 0
  }

  const save = async () => {
    if (!validate()) return
    const response = await saveGeoFenceRule({ ...form, clientId })
    if (response.ok) { onMessage('Geo-fence rule saved.'); closeDrawer(); await load() } else setErrors([response.error || 'Unable to save geo-fence rule.'])
  }

  const remove = async (rule: GeoFenceRule) => {
    if (!window.confirm(`Delete ${rule.name}?`)) return
    const response = await deleteGeoFenceRule(clientId, rule.id)
    if (response.ok) { onMessage('Geo-fence rule deleted.'); await load() } else setErrors([response.error || 'Unable to delete geo-fence rule.'])
  }

  const drawer = drawerOpen && <div className="component-drawer-backdrop" onClick={closeDrawer}><aside className="component-drawer geo-fence-drawer" role="dialog" aria-modal="true" aria-label={`${form.id ? 'Edit' : 'Add'} geo-fence rule`} onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">Geo-Fencing</span><h3>{form.id ? 'Edit Geo-Fence' : 'Add Geo-Fence'}</h3><p>Use employee rules only for exceptions like remote, field or temporary site users.</p></div><button type="button" aria-label="Close geo-fence drawer" onClick={closeDrawer}>x</button></header><div className="component-drawer-form geo-fence-drawer-form"><label className="info-field"><span>Client</span><input value={clientName || `Client #${clientId}`} readOnly /></label><label className="info-field"><span>Rule name</span><input value={form.name} onChange={event => set('name', event.target.value)} placeholder="Head office fence" /></label><label className="info-field"><span>Scope</span><select value={form.scopeType} onChange={event => changeScope(event.target.value as GeoFenceScope)}>{scopes.map(scope => <option key={scope}>{scope}</option>)}</select></label>{form.scopeType === 'Work Location' && <label className="info-field"><span>Work location</span><input list="geo-location-options" value={locationSearch} onChange={event => pickLocation(event.target.value)} onBlur={() => setLocationSearch(selectedLocation?.name || '')} placeholder="Search location..." /><datalist id="geo-location-options">{locations.map(location => <option value={`${location.name} - ${location.city}`} key={location.id} />)}</datalist></label>}<label className="info-field"><span>Latitude</span><input type="number" step="0.0000001" value={form.latitude} onChange={event => set('latitude', Number(event.target.value))} /></label><label className="info-field"><span>Longitude</span><input type="number" step="0.0000001" value={form.longitude} onChange={event => set('longitude', Number(event.target.value))} /></label><label className="info-field"><span>Radius meters</span><input type="number" value={form.radiusMeters} onChange={event => set('radiusMeters', Number(event.target.value))} /></label><label className="info-field"><span>GPS tolerance meters</span><input type="number" value={form.gpsToleranceMeters} onChange={event => set('gpsToleranceMeters', Number(event.target.value))} /></label><label className="info-field"><span>Strictness</span><select value={form.strictness} onChange={event => set('strictness', event.target.value as GeoFenceRule['strictness'])}>{strictness.map(item => <option key={item}>{item}</option>)}</select></label><label className="info-field"><span>Effective from</span><input type="date" value={form.effectiveFrom} onChange={event => set('effectiveFrom', event.target.value)} /></label><label className="info-field"><span>Effective to</span><input type="date" value={form.effectiveTo ?? ''} onChange={event => set('effectiveTo', event.target.value || null)} /></label><div className="component-drawer-checks"><label><input type="checkbox" checked={form.allowCheckIn} onChange={event => set('allowCheckIn', event.target.checked)} /><span>Allow check-in</span></label><small>Mobile app can accept check-in under this rule.</small><label><input type="checkbox" checked={form.allowCheckOut} onChange={event => set('allowCheckOut', event.target.checked)} /><span>Allow check-out</span></label><small>Mobile app can accept check-out under this rule.</small><label><input type="checkbox" checked={form.isActive} onChange={event => set('isActive', event.target.checked)} /><span>Active</span></label><small>Inactive rules stay saved but are ignored by applicable-rule lookup.</small></div>{form.scopeType === 'Employee' && <div className="location-picker employee-picker geo-employee-picker">{clientEmployees.map(employee => <label className={form.employeeIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={form.employeeIds.includes(employee.id)} onChange={() => toggleEmployee(employee.id)} /><span>{employee.firstName} {employee.lastName}</span><small>{employee.employeeCode} / {employee.department || 'No department'}</small></label>)}</div>}{errors.length > 0 && <div className="form-errors geo-drawer-errors">{errors.map(error => <p key={error}>{error}</p>)}</div>}</div><footer><small>Priority: Employee override &gt; Work Location &gt; Client Default.</small><button type="button" className="secondary" onClick={closeDrawer}>Cancel</button><button type="button" onClick={() => void save()}>{form.id ? 'Update rule' : 'Save rule'}</button></footer></aside></div>

  return <section className="geo-fence-manager"><div className="card"><header><i className="blue">G</i><div><h3>Geo-Fencing Rules</h3><p>Mobile attendance uses Employee override, then Work Location, then Client Default.</p></div><button type="button" className="geo-add-rule" onClick={add}>Add rule</button></header><DataTable rows={rules} emptyText="No geo-fence rules configured." exportFileName="geo-fence-rules" columns={[{ key: 'name', label: 'Rule' }, { key: 'scopeType', label: 'Scope' }, { key: 'target', label: 'Target', value: row => row.scopeType === 'Client Default' ? 'All employees' : row.scopeType === 'Work Location' ? row.workLocationName : row.employeeNames }, { key: 'radiusMeters', label: 'Radius', value: row => `${row.radiusMeters}m + ${row.gpsToleranceMeters}m` }, { key: 'strictness', label: 'Mode' }, { key: 'isActive', label: 'Status', render: row => row.isActive ? 'Active' : 'Inactive' }]} actions={row => <span className="row-actions"><button type="button" onClick={() => edit(row)}>Edit</button><button type="button" className="danger" onClick={() => void remove(row)}>Delete</button></span>} /></div>{drawer}</section>
}
