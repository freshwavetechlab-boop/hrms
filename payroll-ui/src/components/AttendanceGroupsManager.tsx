import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Form, Input, Row, Space } from 'antd'
import { getClients, getEmployees } from '../services/payrollService'
import { getWorkLocations } from '../services/settingsService'
import { deleteAttendanceGroup, getAttendanceGroups, saveAttendanceGroup } from '../services/leaveAttendanceService'
import type { AttendanceGroup, AttendanceWorkWeek, Client, Employee, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'

const dayOptions = Array.from({ length: 31 }, (_, index) => index + 1)
const workWeekOptions: AttendanceWorkWeek[] = ['Monday - Friday', 'Monday - Saturday', 'All days', 'Sunday + 2nd Saturday off', 'Sunday + 2nd/4th Saturday off', 'Only 2nd Saturday off']
const emptyGroup: AttendanceGroup = { id: 0, clientId: 0, clientName: '', name: '', workLocationId: 0, workLocationName: '', department: '', designation: '', workWeek: 'Monday - Friday', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, isActive: true, employeeIds: [], employeeNames: '', employeeCount: 0 }
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const fullName = (employee: Employee) => `${employee.firstName} ${employee.lastName}`.trim() || employee.employeeCode
const bufferDays = (endDay: number, reportDay: number) => reportDay >= endDay ? reportDay - endDay : reportDay + 31 - endDay

export default function AttendanceGroupsManager({ onMessage }: { onMessage: (message: string) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [groups, setGroups] = useState<AttendanceGroup[]>([])
  const [form, setForm] = useState<AttendanceGroup>(emptyGroup)
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  const activeEmployees = useMemo(() => employees.filter(employee => employee.isActive), [employees])
  const clientLocations = useMemo(() => locations.filter(location => location.isActive && location.clientId === form.clientId), [locations, form.clientId])
  const locationEmployees = useMemo(() => activeEmployees.filter(employee => employee.clientId === form.clientId && employee.workLocationId === form.workLocationId), [activeEmployees, form.clientId, form.workLocationId])
  const departments = useMemo(() => unique(locationEmployees.map(employee => employee.department)), [locationEmployees])
  const designations = useMemo(() => unique(locationEmployees.filter(employee => !form.department || employee.department === form.department).map(employee => employee.designation)), [locationEmployees, form.department])
  const matchingEmployees = useMemo(() => locationEmployees.filter(employee => (!form.department || employee.department === form.department) && (!form.designation || employee.designation === form.designation)), [locationEmployees, form.department, form.designation])
  const matchingIds = useMemo(() => matchingEmployees.map(employee => employee.id), [matchingEmployees])
  const selectedMatchingCount = form.employeeIds.filter(id => matchingIds.includes(id)).length
  const buffer = bufferDays(form.attendanceCycleEndDay, form.payrollReportGenerationDay)

  const employeeIdsFor = (group: AttendanceGroup, sourceEmployees = activeEmployees) => sourceEmployees.filter(employee => employee.clientId === group.clientId && employee.workLocationId === group.workLocationId && (!group.department || employee.department === group.department) && (!group.designation || employee.designation === group.designation)).map(employee => employee.id)
  const defaultFor = (clientRows = clients, locationRows = locations, employeeRows = activeEmployees) => {
    const clientId = clientRows.find(client => client.isActive)?.id || 0
    const workLocationId = locationRows.find(location => location.isActive && location.clientId === clientId)?.id || 0
    const next = { ...emptyGroup, clientId, workLocationId }
    return { ...next, employeeIds: employeeIdsFor(next, employeeRows.filter(employee => employee.isActive)) }
  }

  const load = async () => {
    const [clientRows, locationRows, employeeRows, groupRows] = await Promise.all([getClients(), getWorkLocations(), getEmployees(), getAttendanceGroups()])
    const activeClients = clientRows.filter(client => client.isActive)
    const activeLocations = locationRows.filter(location => location.isActive)
    const activeEmployeeRows = employeeRows.filter(employee => employee.isActive)
    setClients(activeClients)
    setLocations(activeLocations)
    setEmployees(employeeRows)
    setGroups(groupRows)
    setForm(current => current.clientId ? current : defaultFor(activeClients, activeLocations, activeEmployeeRows))
  }

  useEffect(() => { void load() }, [])

  const reset = () => { setErrors([]); setForm(defaultFor()) }
  const applyScope = (patch: Partial<AttendanceGroup>) => {
    setErrors([])
    setForm(current => {
      const next = { ...current, ...patch }
      const normalized = patch.clientId ? { ...next, workLocationId: locations.find(location => location.isActive && location.clientId === patch.clientId)?.id || 0, department: '', designation: '' } : patch.workLocationId ? { ...next, department: '', designation: '' } : patch.department !== undefined ? { ...next, designation: '' } : next
      return { ...normalized, employeeIds: employeeIdsFor(normalized) }
    })
  }
  const set = <K extends keyof AttendanceGroup>(key: K, value: AttendanceGroup[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const toggleEmployee = (id: number) => set('employeeIds', form.employeeIds.includes(id) ? form.employeeIds.filter(item => item !== id) : [...form.employeeIds, id])

  const validate = () => {
    const next: string[] = []
    if (!form.name.trim()) next.push('Group name is required.')
    if (!form.clientId) next.push('Select a client.')
    if (!form.workLocationId) next.push('Select a work location.')
    if (buffer < 3 || buffer > 7) next.push('Payroll report generation day must be 3 to 7 days after attendance cycle end day.')
    if (!form.employeeIds.length) next.push('Select at least one employee.')
    if (form.employeeIds.some(id => !matchingIds.includes(id))) next.push('Selected employees must match the selected client, location, department and designation.')
    setErrors(next)
    return next.length === 0
  }

  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveAttendanceGroup({ ...form, employeeIds: form.employeeIds.filter(id => matchingIds.includes(id)) })
    setSaving(false)
    if (response.ok && response.data) {
      setForm(response.data)
      setGroups(await getAttendanceGroups())
      setErrors([])
      onMessage('Attendance group saved.')
    } else setErrors([response.error || 'Unable to save attendance group.'])
  }
  const edit = (group: AttendanceGroup) => { setErrors([]); setForm({ ...emptyGroup, ...group, employeeIds: group.employeeIds || [] }) }
  const remove = async (group: AttendanceGroup) => {
    if (!window.confirm(`Delete group ${group.name}?`)) return
    const response = await deleteAttendanceGroup(group.clientId, group.id)
    if (response.ok) {
      setGroups(await getAttendanceGroups())
      if (form.id === group.id) reset()
      onMessage('Attendance group deleted.')
    } else setErrors([response.error || 'Unable to delete attendance group.'])
  }

  return <section className="attendance-groups">
    <Row gutter={[16, 16]} className="attendance-group-layout">
      <Col xs={24} lg={10}>
        <AntCard className="attendance-group-panel attendance-group-form-panel" title={form.id ? 'Edit group' : 'Add group'} size="small" extra={form.id ? <Button htmlType="button" onClick={reset}>New</Button> : null}>
          <Form className="attendance-group-form" component={false} layout="vertical" requiredMark={false}>
            {errors.length > 0 && <Alert type="error" showIcon message={errors.join(' ')} />}
            <Form.Item label="Group name" required><Input value={form.name} onChange={event => set('name', event.target.value)} placeholder="Consultants - RECL Site A" /></Form.Item>
            <Form.Item label="Client" required><SearchSelect value={form.clientId} onChange={value => applyScope({ clientId: Number(value), id: 0 })} options={clients.map(client => ({ value: client.id, label: client.name }))} /></Form.Item>
            <Form.Item label="Work Location" required><SearchSelect value={form.workLocationId} onChange={value => applyScope({ workLocationId: Number(value), id: 0 })} options={selectOptions(clientLocations.map(location => ({ value: location.id, label: `${location.name} - ${location.city || location.state || 'Location'}` })), 'Select work location', 0)} /></Form.Item>
            <Row gutter={12}>
              <Col xs={24} md={12} lg={24} xl={12}><Form.Item label="Department"><SearchSelect value={form.department} onChange={value => applyScope({ department: value, id: 0 })} options={selectOptions(departments, 'All departments')} /></Form.Item></Col>
              <Col xs={24} md={12} lg={24} xl={12}><Form.Item label="Designation"><SearchSelect value={form.designation} onChange={value => applyScope({ designation: value, id: 0 })} options={selectOptions(designations, 'All designations')} /></Form.Item></Col>
            </Row>
            <Form.Item label="Work week / off pattern" required><SearchSelect value={form.workWeek} onChange={value => set('workWeek', value as AttendanceWorkWeek)} options={selectOptions(workWeekOptions)} /></Form.Item>
            <Row gutter={12}>
              <Col span={8}><Form.Item label="Start day" required><SearchSelect value={form.attendanceCycleStartDay} onChange={value => set('attendanceCycleStartDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
              <Col span={8}><Form.Item label="End day" required><SearchSelect value={form.attendanceCycleEndDay} onChange={value => set('attendanceCycleEndDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
              <Col span={8}><Form.Item label="Report day" extra={`${buffer} day buffer`} required><SearchSelect value={form.payrollReportGenerationDay} onChange={value => set('payrollReportGenerationDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
            </Row>
            <Form.Item><AntCheckbox checked={form.isActive} onChange={event => set('isActive', event.target.checked)}>Active</AntCheckbox></Form.Item>
            <Divider />
            <div className="attendance-group-actions"><Button htmlType="button" onClick={reset}>Reset</Button><Button htmlType="button" type="primary" loading={saving} onClick={() => void save()}>{form.id ? 'Update group' : 'Save group'}</Button></div>
          </Form>
        </AntCard>
        <AntCard className="attendance-group-panel attendance-group-members" title={`Employees (${selectedMatchingCount}/${matchingEmployees.length})`} size="small" extra={<Space><Button size="small" onClick={() => set('employeeIds', matchingIds)}>Select all</Button><Button size="small" onClick={() => set('employeeIds', [])}>Clear</Button></Space>}>
          <div className="attendance-employee-picker">{matchingEmployees.length ? matchingEmployees.map(employee => <label className={form.employeeIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={form.employeeIds.includes(employee.id)} onChange={() => toggleEmployee(employee.id)} /><span>{fullName(employee)}</span><small>{employee.employeeCode} / {employee.department || 'No department'} / {employee.designation || 'No designation'}</small></label>) : <p>No employees match this scope.</p>}</div>
        </AntCard>
      </Col>
      <Col xs={24} lg={14}>
        <AntCard className="attendance-group-panel attendance-group-table" title="Saved groups" size="small">
          <DataTable rows={groups} getRowId={row => row.id} emptyText="No attendance groups configured." exportFileName="attendance-groups" columns={[
            { key: 'name', label: 'Group' },
            { key: 'clientName', label: 'Client' },
            { key: 'workLocationName', label: 'Location' },
            { key: 'department', label: 'Department', value: row => row.department || 'All' },
            { key: 'designation', label: 'Designation', value: row => row.designation || 'All' },
            { key: 'cycle', label: 'Cycle', value: row => `${row.attendanceCycleStartDay} - ${row.attendanceCycleEndDay}` },
            { key: 'workWeek', label: 'Work week' },
            { key: 'employeeCount', label: 'Employees' },
            { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
          ]} actions={row => <Space size={6}><Button htmlType="button" size="small" type="primary" onClick={() => edit(row)}>Edit</Button><Button htmlType="button" size="small" danger onClick={() => void remove(row)}>Delete</Button></Space>} />
        </AntCard>
      </Col>
    </Row>
  </section>
}
