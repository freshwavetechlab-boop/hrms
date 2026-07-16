import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, InputNumber, Row, Space, Tag } from 'antd'
import DataTable from './DataTable'
import GeoFenceMapPicker from './GeoFenceMapPicker'
import SearchSelect, { selectOptions } from './SearchSelect'
import { deleteGeoFenceRule, geoFenceFallback, getGeoFenceEmployees, getGeoFenceRules, saveGeoFenceRule } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import type { Client, GeoFenceEmployeeOption, GeoFenceRule, WorkLocation } from '../types/payroll'

const strictness = ['Block outside fence', 'Allow with reason', 'Allow with approval'] as const

export default function GeoFenceManager({ clients, clientId, onClientChange, onMessage }: { clients: Client[]; clientId: number; onClientChange: (clientId: number) => void; onMessage: (message: string) => void }) {
  const [rules, setRules] = useState<GeoFenceRule[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<GeoFenceEmployeeOption[]>([])
  const [employeesLoading, setEmployeesLoading] = useState(false)
  const [form, setForm] = useState<GeoFenceRule>({ ...geoFenceFallback, clientId })
  const [errors, setErrors] = useState<string[]>([])
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const selectedLocation = useMemo(() => locations.find(location => location.id === form.workLocationId) ?? null, [form.workLocationId, locations])

  const load = async () => {
    const [nextRules, nextLocations] = await Promise.all([getGeoFenceRules(clientId, 'Work Location'), getWorkLocations()])
    setRules(nextRules)
    setLocations(nextLocations.filter(location => location.isActive && Number(location.clientId) === Number(clientId)))
    setForm(current => current.id ? current : { ...geoFenceFallback, clientId, scopeType: 'Work Location' })
  }

  useEffect(() => {
    let active = true
    void Promise.all([getGeoFenceRules(clientId, 'Work Location'), getWorkLocations()]).then(([nextRules, nextLocations]) => {
      if (!active) return
      setRules(nextRules)
      setLocations(nextLocations.filter(location => location.isActive && Number(location.clientId) === Number(clientId)))
      setEmployees([])
      setForm(current => current.id ? current : { ...geoFenceFallback, clientId, scopeType: 'Work Location' })
    })
    return () => { active = false }
  }, [clientId])

  const set = <K extends keyof GeoFenceRule>(key: K, value: GeoFenceRule[K]) => {
    setErrors([])
    setForm(current => ({ ...current, [key]: value }))
  }
  const loadEmployees = async (workLocationId: number) => {
    if (!workLocationId) {
      setEmployees([])
      return
    }
    setEmployeesLoading(true)
    setEmployees(await getGeoFenceEmployees(clientId, workLocationId))
    setEmployeesLoading(false)
  }
  const changeWorkLocation = async (workLocationId: number) => {
    setErrors([])
    setForm(current => ({ ...current, scopeType: 'Work Location', workLocationId, employeeIds: [], latitude: 0, longitude: 0 }))
    await loadEmployees(workLocationId)
  }
  const changeClient = (nextClientId: number) => {
    if (!nextClientId || nextClientId === clientId) return
    onClientChange(nextClientId)
    setErrors([])
    setEmployees([])
    setForm({ ...geoFenceFallback, clientId: nextClientId, scopeType: 'Work Location' })
  }
  const toggleEmployee = (employeeId: number) => setForm(current => ({ ...current, employeeIds: current.employeeIds.includes(employeeId) ? current.employeeIds.filter(id => id !== employeeId) : [...current.employeeIds, employeeId] }))
  const reset = () => { setForm({ ...geoFenceFallback, clientId, scopeType: 'Work Location' }); setEmployees([]); setErrors([]) }
  const add = () => { reset(); setDrawerOpen(true) }
  const edit = async (rule: GeoFenceRule) => {
    setForm({ ...geoFenceFallback, ...rule, scopeType: 'Work Location', effectiveFrom: String(rule.effectiveFrom).slice(0, 10), effectiveTo: rule.effectiveTo ? String(rule.effectiveTo).slice(0, 10) : null })
    setErrors([])
    setDrawerOpen(true)
    await loadEmployees(Number(rule.workLocationId ?? 0))
  }
  const closeDrawer = () => { reset(); setDrawerOpen(false) }

  const validate = () => {
    const next: string[] = []
    if (!form.name.trim()) next.push('Rule name is required.')
    if (!form.workLocationId) next.push('Select a work location.')
    if (form.latitude < -90 || form.latitude > 90 || form.longitude < -180 || form.longitude > 180) next.push('Enter valid latitude and longitude.')
    if (form.latitude === 0 && form.longitude === 0) next.push('Select the office position on the map.')
    if (form.radiusMeters < 25 || form.radiusMeters > 5000) next.push('Radius must be between 25 and 5000 meters.')
    if (form.gpsToleranceMeters < 0 || form.gpsToleranceMeters > 500) next.push('GPS tolerance must be between 0 and 500 meters.')
    if (!form.allowCheckIn && !form.allowCheckOut) next.push('Allow at least one attendance action.')
    if (form.effectiveTo && form.effectiveTo < form.effectiveFrom) next.push('Effective to cannot be before effective from.')
    setErrors(next)
    return next.length === 0
  }

  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveGeoFenceRule({ ...form, clientId: form.clientId || clientId, scopeType: 'Work Location' })
    setSaving(false)
    if (response.ok) { onMessage('Geo-fence rule saved.'); closeDrawer(); await load() }
    else setErrors([response.error || 'Unable to save geo-fence rule.'])
  }

  const remove = async (rule: GeoFenceRule) => {
    if (!window.confirm(`Delete ${rule.name}?`)) return
    const response = await deleteGeoFenceRule(clientId, rule.id)
    if (response.ok) { onMessage('Geo-fence rule deleted.'); await load() }
    else setErrors([response.error || 'Unable to delete geo-fence rule.'])
  }

  return <section className="geo-fence-manager">
    <AntCard className="settings-panel settings-table-panel geo-fence-card" size="small" title="Geo-Fencing Rules">
      <div className="component-table-head">
        <div><b>Geo-fence rule master</b><span>Select a work location, choose its employees if required, and pin one or more permitted offices on the map.</span></div>
        <Button type="primary" onClick={add}>Add rule</Button>
      </div>
      <DataTable rows={rules} emptyText="No geo-fence rules configured." exportFileName="geo-fence-rules" columns={[
        { key: 'name', label: 'Rule' },
        { key: 'target', label: 'Work location / employees', value: row => `${row.workLocationName || 'Work location'} - ${row.employeeIds.length ? `${row.employeeIds.length} selected employees` : 'All location employees'}` },
        { key: 'radiusMeters', label: 'Radius', value: row => `${row.radiusMeters}m + ${row.gpsToleranceMeters}m` },
        { key: 'strictness', label: 'Mode' },
        { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
      ]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => void edit(row)}>Edit</Button><Button size="small" danger onClick={() => void remove(row)}>Delete</Button></Space>} />
    </AntCard>

    <Drawer className="settings-master-drawer geo-fence-ant-drawer" title={form.id ? 'Edit geo-fence rule' : 'Add geo-fence rule'} open={drawerOpen} width={960} onClose={closeDrawer} destroyOnClose footer={<Space><Button onClick={closeDrawer}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>{form.id ? 'Update rule' : 'Save rule'}</Button></Space>}>
      <GeoFenceForm
        clients={clients}
        clientId={clientId}
        form={form}
        errors={errors}
        locations={locations}
        workLocation={selectedLocation}
        employees={employees}
        employeesLoading={employeesLoading}
        changeClient={changeClient}
        set={set}
        changeWorkLocation={changeWorkLocation}
        toggleEmployee={toggleEmployee}
      />
    </Drawer>
  </section>
}

function GeoFenceForm(p: {
  clients: Client[]
  clientId: number
  form: GeoFenceRule
  errors: string[]
  locations: WorkLocation[]
  workLocation: WorkLocation | null
  employees: GeoFenceEmployeeOption[]
  employeesLoading: boolean
  changeClient: (clientId: number) => void
  set: <K extends keyof GeoFenceRule>(key: K, value: GeoFenceRule[K]) => void
  changeWorkLocation: (workLocationId: number) => Promise<void>
  toggleEmployee: (employeeId: number) => void
}) {
  const mapSearchHints = p.workLocation ? locationSearchHints(p.workLocation) : []
  return <Form className="settings-quick-form geo-fence-form" component={false} layout="vertical" requiredMark={false}>
    {p.errors.length > 0 && <Alert type="error" showIcon message={p.errors.join(' ')} />}
    <Row gutter={12}>
      <Col xs={24} md={12}><Form.Item label="Client" required><SearchSelect value={p.clientId} onChange={value => p.changeClient(Number(value))} options={p.clients.map(client => ({ value: client.id, label: client.name }))} /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Rule name" required><Input value={p.form.name} onChange={event => p.set('name', event.target.value)} placeholder="Head office fence" /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Work location" required><SearchSelect value={p.form.workLocationId ?? ''} onChange={value => void p.changeWorkLocation(Number(value))} options={selectOptions(p.locations.map(location => ({ value: location.id, label: `${location.name}${location.city ? ` - ${location.city}` : ''}` })))} /></Form.Item></Col>
    </Row>

    <Divider orientation="left">Office position and fence radius</Divider>
    <GeoFenceMapPicker
      key={`work-location-${p.form.workLocationId ?? 'none'}`}
      latitude={p.form.latitude}
      longitude={p.form.longitude}
      radiusMeters={p.form.radiusMeters}
      searchHints={mapSearchHints}
      onChange={(latitude, longitude) => {
        p.set('latitude', latitude)
        p.set('longitude', longitude)
      }}
    />
    <Row gutter={12}>
      <Col xs={24} md={8}><Form.Item label="Latitude" required><InputNumber min={-90} max={90} step={0.0000001} value={p.form.latitude} onChange={value => p.set('latitude', Number(value ?? 0))} style={{ width: '100%' }} /></Form.Item></Col>
      <Col xs={24} md={8}><Form.Item label="Longitude" required><InputNumber min={-180} max={180} step={0.0000001} value={p.form.longitude} onChange={value => p.set('longitude', Number(value ?? 0))} style={{ width: '100%' }} /></Form.Item></Col>
      <Col xs={24} md={8}><Form.Item label="Radius meters" required><InputNumber min={25} max={5000} value={p.form.radiusMeters} onChange={value => p.set('radiusMeters', Number(value ?? 0))} style={{ width: '100%' }} /></Form.Item></Col>
      <Col xs={24} md={8}><Form.Item label="GPS tolerance meters"><InputNumber min={0} max={500} value={p.form.gpsToleranceMeters} onChange={value => p.set('gpsToleranceMeters', Number(value ?? 0))} style={{ width: '100%' }} /></Form.Item></Col>
    </Row>

    <Divider orientation="left">Rule behavior</Divider>
    <Row gutter={12}>
      <Col xs={24} md={12}><Form.Item label="Strictness"><SearchSelect value={p.form.strictness} onChange={value => p.set('strictness', value as GeoFenceRule['strictness'])} options={selectOptions([...strictness])} /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Effective from" required><Input type="date" value={p.form.effectiveFrom} onChange={event => p.set('effectiveFrom', event.target.value)} /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Effective to"><Input type="date" value={p.form.effectiveTo ?? ''} onChange={event => p.set('effectiveTo', event.target.value || null)} /></Form.Item></Col>
      <Col xs={24}><div className="geo-fence-check-grid">
        <AntCheckbox checked={p.form.allowCheckIn} onChange={event => p.set('allowCheckIn', event.target.checked)}>Allow check-in</AntCheckbox>
        <AntCheckbox checked={p.form.allowCheckOut} onChange={event => p.set('allowCheckOut', event.target.checked)}>Allow check-out</AntCheckbox>
        <AntCheckbox checked={p.form.isActive} onChange={event => p.set('isActive', event.target.checked)}>Active</AntCheckbox>
      </div></Col>
    </Row>

    {Boolean(p.form.workLocationId) && <EmployeeRulePicker
      employees={p.employees}
      selectedIds={p.form.employeeIds}
      locationName={p.workLocation?.name || ''}
      loading={p.employeesLoading}
      onToggle={p.toggleEmployee}
      onSelectAll={() => p.set('employeeIds', p.employees.map(employee => employee.id))}
      onClear={() => p.set('employeeIds', [])}
    />}
  </Form>
}

function EmployeeRulePicker({
  employees,
  selectedIds,
  locationName,
  loading,
  onToggle,
  onSelectAll,
  onClear
}: {
  employees: GeoFenceEmployeeOption[]
  selectedIds: number[]
  locationName: string
  loading: boolean
  onToggle: (employeeId: number) => void
  onSelectAll: () => void
  onClear: () => void
}) {
  const [search, setSearch] = useState('')
  const visibleEmployees = useMemo(() => {
    const query = search.trim().toLowerCase()
    if (!query) return employees
    return employees.filter(employee => `${employee.firstName} ${employee.lastName} ${employee.employeeCode} ${employee.department} ${employee.designation}`.toLowerCase().includes(query))
  }, [employees, search])

  return <>
    <Divider orientation="left">Location employees (optional)</Divider>
    <Alert
      className="geo-employee-scope-alert"
      type={selectedIds.length ? 'info' : 'success'}
      showIcon
      message={selectedIds.length
        ? `This office fence applies to ${selectedIds.length} selected employee${selectedIds.length === 1 ? '' : 's'} from ${locationName}.`
        : `No employee is selected, so this office fence applies to every active employee at ${locationName}.`}
    />
    <div className="geo-employee-toolbar">
      <Input.Search allowClear value={search} onChange={event => setSearch(event.target.value)} placeholder="Search employee code, name, department, or designation" />
      <Space>
        <Button disabled={employees.length === 0 || selectedIds.length === employees.length} onClick={onSelectAll}>Select all ({employees.length})</Button>
        <Button disabled={selectedIds.length === 0} onClick={onClear}>Clear</Button>
      </Space>
    </div>
    {loading
      ? <Alert type="info" showIcon message="Loading employees assigned to this work location..." />
      : employees.length === 0
      ? <Alert type="warning" showIcon message="No active employee is assigned to this work location." />
      : <div className="location-picker employee-picker geo-employee-picker">
        {visibleEmployees.map(employee => <div className={`geo-employee-row ${selectedIds.includes(employee.id) ? 'selected' : ''}`} key={employee.id}>
          <AntCheckbox checked={selectedIds.includes(employee.id)} onChange={() => onToggle(employee.id)} />
          <span>{employee.firstName} {employee.lastName}</span>
          <small>{employee.employeeCode} / {employee.department || 'No department'} / {employee.designation || 'No designation'}</small>
        </div>)}
      </div>}
  </>
}

function locationSearchHints(location: WorkLocation) {
  const queries = [
    uniqueAddress([location.address, location.city, location.state, location.postalCode, 'India']),
    uniqueAddress([location.name, location.city, location.state, location.postalCode, 'India']),
    uniqueAddress([location.city, location.state, location.postalCode, 'India']),
    uniqueAddress([location.city, location.state, 'India'])
  ].filter(Boolean)
  return Array.from(new Set(queries.map(query => query.toLowerCase()))).map(key => queries.find(query => query.toLowerCase() === key)!)
}

function uniqueAddress(values: Array<string | null | undefined>) {
  const seen = new Set<string>()
  return values
    .flatMap(value => String(value || '').split(','))
    .map(value => value.trim())
    .filter(value => {
      if (!value) return false
      const key = value.toLowerCase()
      if (seen.has(key)) return false
      seen.add(key)
      return true
    })
    .join(', ')
}
