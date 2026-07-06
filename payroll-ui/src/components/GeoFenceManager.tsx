import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, InputNumber, Row, Space, Tag } from 'antd'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { deleteGeoFenceRule, geoFenceFallback, getGeoFenceRules, saveGeoFenceRule } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import { getEmployees } from '../services/payrollService'
import type { Client, Employee, GeoFenceRule, GeoFenceScope, WorkLocation } from '../types/payroll'

const scopes: GeoFenceScope[] = ['Client Default', 'Work Location', 'Employee']
const strictness = ['Block outside fence', 'Allow with reason', 'Allow with approval'] as const

export default function GeoFenceManager({ clients, clientId, onClientChange, onMessage }: { clients: Client[]; clientId: number; onClientChange: (clientId: number) => void; onMessage: (message: string) => void }) {
  const [rules, setRules] = useState<GeoFenceRule[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [form, setForm] = useState<GeoFenceRule>({ ...geoFenceFallback, clientId })
  const [errors, setErrors] = useState<string[]>([])
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [employees, clientId])

  const load = async () => {
    const [nextRules, nextLocations, nextEmployees] = await Promise.all([getGeoFenceRules(clientId), getWorkLocations(), getEmployees()])
    setRules(nextRules)
    setLocations(nextLocations.filter(location => location.isActive && Number(location.clientId) === Number(clientId)))
    setEmployees(nextEmployees)
    setForm(current => current.id ? current : { ...geoFenceFallback, clientId })
  }

  useEffect(() => { void load() }, [clientId])

  const set = <K extends keyof GeoFenceRule>(key: K, value: GeoFenceRule[K]) => {
    setErrors([])
    setForm(current => ({ ...current, [key]: value }))
  }
  const changeScope = (scopeType: GeoFenceScope) => {
    setErrors([])
    setForm(current => ({ ...current, scopeType, workLocationId: scopeType === 'Work Location' ? current.workLocationId : null, employeeIds: scopeType === 'Employee' ? current.employeeIds : [] }))
  }
  const changeClient = (nextClientId: number) => {
    if (!nextClientId || nextClientId === clientId) return
    onClientChange(nextClientId)
    setErrors([])
    setForm({ ...geoFenceFallback, clientId: nextClientId })
  }
  const toggleEmployee = (employeeId: number) => setForm(current => ({ ...current, employeeIds: current.employeeIds.includes(employeeId) ? current.employeeIds.filter(id => id !== employeeId) : [...current.employeeIds, employeeId] }))
  const reset = () => { setForm({ ...geoFenceFallback, clientId }); setErrors([]) }
  const add = () => { reset(); setDrawerOpen(true) }
  const edit = (rule: GeoFenceRule) => {
    setForm({ ...geoFenceFallback, ...rule, effectiveFrom: String(rule.effectiveFrom).slice(0, 10), effectiveTo: rule.effectiveTo ? String(rule.effectiveTo).slice(0, 10) : null })
    setErrors([])
    setDrawerOpen(true)
  }
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
    setSaving(true)
    const response = await saveGeoFenceRule({ ...form, clientId: form.clientId || clientId })
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
        <div><b>Geo-fence rule master</b><span>Mobile attendance uses Employee override, then Work Location, then Client Default.</span></div>
        <Button type="primary" onClick={add}>Add rule</Button>
      </div>
      <DataTable rows={rules} emptyText="No geo-fence rules configured." exportFileName="geo-fence-rules" columns={[
        { key: 'name', label: 'Rule' },
        { key: 'scopeType', label: 'Scope' },
        { key: 'target', label: 'Target', value: row => row.scopeType === 'Client Default' ? 'All employees' : row.scopeType === 'Work Location' ? row.workLocationName : row.employeeNames },
        { key: 'radiusMeters', label: 'Radius', value: row => `${row.radiusMeters}m + ${row.gpsToleranceMeters}m` },
        { key: 'strictness', label: 'Mode' },
        { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
      ]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => edit(row)}>Edit</Button><Button size="small" danger onClick={() => void remove(row)}>Delete</Button></Space>} />
    </AntCard>

    <Drawer className="settings-master-drawer geo-fence-ant-drawer" title={form.id ? 'Edit geo-fence rule' : 'Add geo-fence rule'} open={drawerOpen} width={680} onClose={closeDrawer} destroyOnClose footer={<Space><Button onClick={closeDrawer}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>{form.id ? 'Update rule' : 'Save rule'}</Button></Space>}>
      <GeoFenceForm
        clients={clients}
        clientId={clientId}
        form={form}
        errors={errors}
        locations={locations}
        employees={clientEmployees}
        changeClient={changeClient}
        set={set}
        changeScope={changeScope}
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
  employees: Employee[]
  changeClient: (clientId: number) => void
  set: <K extends keyof GeoFenceRule>(key: K, value: GeoFenceRule[K]) => void
  changeScope: (scopeType: GeoFenceScope) => void
  toggleEmployee: (employeeId: number) => void
}) {
  return <Form className="settings-quick-form geo-fence-form" component={false} layout="vertical" requiredMark={false}>
    {p.errors.length > 0 && <Alert type="error" showIcon message={p.errors.join(' ')} />}
    <Row gutter={12}>
      <Col xs={24} md={12}><Form.Item label="Client" required><SearchSelect value={p.clientId} onChange={value => p.changeClient(Number(value))} options={p.clients.map(client => ({ value: client.id, label: client.name }))} /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Rule name" required><Input value={p.form.name} onChange={event => p.set('name', event.target.value)} placeholder="Head office fence" /></Form.Item></Col>
      <Col xs={24} md={12}><Form.Item label="Scope" required><SearchSelect value={p.form.scopeType} onChange={value => p.changeScope(value as GeoFenceScope)} options={selectOptions([...scopes])} /></Form.Item></Col>
      {p.form.scopeType === 'Work Location' && <Col xs={24} md={12}><Form.Item label="Work location" required><SearchSelect value={p.form.workLocationId ?? ''} onChange={value => p.set('workLocationId', Number(value))} options={selectOptions(p.locations.map(location => ({ value: location.id, label: `${location.name}${location.city ? ` - ${location.city}` : ''}` })))} /></Form.Item></Col>}
    </Row>

    <Divider orientation="left">Fence coordinates</Divider>
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

    {p.form.scopeType === 'Employee' && <>
      <Divider orientation="left">Employees</Divider>
      <div className="location-picker employee-picker geo-employee-picker">
        {p.employees.map(employee => <div className={`geo-employee-row ${p.form.employeeIds.includes(employee.id) ? 'selected' : ''}`} key={employee.id}>
          <AntCheckbox checked={p.form.employeeIds.includes(employee.id)} onChange={() => p.toggleEmployee(employee.id)} />
          <span>{employee.firstName} {employee.lastName}</span>
          <small>{employee.employeeCode} / {employee.department || 'No department'}</small>
        </div>)}
      </div>
    </>}
  </Form>
}
