import { useEffect, useMemo, useState } from 'react'
import { Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, Row, Space } from 'antd'
import { getClients, getEmployees } from '../services/payrollService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import { deleteAttendanceGroup, getAttendanceGroups, saveAttendanceGroup } from '../services/leaveAttendanceService'
import type { AttendanceGroup, AttendanceWorkWeek, Client, Drop, Employee, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'

const dayOptions = Array.from({ length: 31 }, (_, index) => index + 1)
const emptyGroup: AttendanceGroup = { id: 0, clientId: 0, clientName: '', name: '', workLocationId: 0, workLocationName: '', department: '', designation: '', workWeek: '', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, isActive: true, employeeIds: [], employeeNames: '', employeeCount: 0 }
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const fullName = (employee: Employee) => `${employee.firstName} ${employee.lastName}`.trim() || employee.employeeCode
const bufferDays = (endDay: number, reportDay: number) => reportDay >= endDay ? reportDay - endDay : reportDay + 31 - endDay
const cycleDays = (startDay: number, endDay: number) => startDay === 1 ? endDay : 31 - startDay + 1 + endDay
const currentMonth = () => {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`
}
const monthStart = (month: string) => {
  const [year, monthNumber] = month.split('-').map(Number)
  return new Date(year || new Date().getFullYear(), (monthNumber || 1) - 1, 1)
}
const addMonths = (date: Date, months: number) => new Date(date.getFullYear(), date.getMonth() + months, 1)
const clampDay = (date: Date, day: number) => Math.min(Math.max(1, day), new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate())
const shortDate = (date: Date) => date.toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' })
const cyclePreviewFor = (month: string, startDay: number, endDay: number) => {
  const endMonth = monthStart(month)
  const startMonth = startDay > 1 ? addMonths(endMonth, -1) : endMonth
  const start = new Date(startMonth.getFullYear(), startMonth.getMonth(), clampDay(startMonth, startDay))
  const end = new Date(endMonth.getFullYear(), endMonth.getMonth(), clampDay(endMonth, endDay))
  return { label: `${shortDate(start)} - ${shortDate(end)}`, days: Math.round((end.getTime() - start.getTime()) / 86400000) + 1 }
}

export default function AttendanceGroupsManager({ onMessage }: { onMessage: (message: string) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [groups, setGroups] = useState<AttendanceGroup[]>([])
  const [form, setForm] = useState<AttendanceGroup>(emptyGroup)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)
  const [previewMonth, setPreviewMonth] = useState(currentMonth())

  const activeEmployees = useMemo(() => employees.filter(employee => employee.isActive), [employees])
  const clientLocations = useMemo(() => locations.filter(location => location.isActive), [locations])
  const locationEmployees = useMemo(() => activeEmployees.filter(employee => employee.clientId === form.clientId && employee.workLocationId === form.workLocationId), [activeEmployees, form.clientId, form.workLocationId])
  const departments = useMemo(() => unique([...dropdowns.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), ...locationEmployees.map(employee => employee.department), form.department]), [dropdowns, locationEmployees, form.department])
  const designations = useMemo(() => unique([...dropdowns.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value), ...locationEmployees.map(employee => employee.designation), form.designation]), [dropdowns, locationEmployees, form.designation])
  const workWeeks = useMemo(() => unique([...dropdowns.filter(item => item.type === 'Work Week' && item.isActive).map(item => item.value), form.workWeek]), [dropdowns, form.workWeek])
  const matchingEmployees = useMemo(() => locationEmployees.filter(employee => (!form.department || employee.department === form.department) && (!form.designation || employee.designation === form.designation)), [locationEmployees, form.department, form.designation])
  const matchingIds = useMemo(() => matchingEmployees.map(employee => employee.id), [matchingEmployees])
  const selectedMatchingCount = form.employeeIds.filter(id => matchingIds.includes(id)).length
  const buffer = bufferDays(form.attendanceCycleEndDay, form.payrollReportGenerationDay)
  const cycleLength = cycleDays(form.attendanceCycleStartDay, form.attendanceCycleEndDay)
  const cyclePreview = useMemo(() => cyclePreviewFor(previewMonth, form.attendanceCycleStartDay, form.attendanceCycleEndDay), [previewMonth, form.attendanceCycleStartDay, form.attendanceCycleEndDay])

  const employeeIdsFor = (group: AttendanceGroup, sourceEmployees = activeEmployees) => sourceEmployees.filter(employee => employee.clientId === group.clientId && employee.workLocationId === group.workLocationId && (!group.department || employee.department === group.department) && (!group.designation || employee.designation === group.designation)).map(employee => employee.id)
  const defaultFor = (clientRows = clients, locationRows = locations, employeeRows = activeEmployees) => {
    const clientId = clientRows.find(client => client.isActive)?.id || 0
    const workLocationId = locationRows.find(location => location.isActive && location.clientId === clientId)?.id || 0
    const next = { ...emptyGroup, clientId, workLocationId }
    return { ...next, employeeIds: employeeIdsFor(next, employeeRows.filter(employee => employee.isActive)) }
  }

  const load = async () => {
    const [clientRows, locationRows, employeeRows, groupRows, dropdownRows] = await Promise.all([getClients(), getWorkLocations(), getEmployees(), getAttendanceGroups(), getDropdowns()])
    const activeClients = clientRows.filter(client => client.isActive)
    const activeLocations = locationRows.filter(location => location.isActive)
    const activeEmployeeRows = employeeRows.filter(employee => employee.isActive)
    setClients(activeClients)
    setLocations(activeLocations)
    setEmployees(employeeRows)
    setDropdowns(dropdownRows)
    setGroups(groupRows)
    setForm(current => current.clientId ? current : defaultFor(activeClients, activeLocations, activeEmployeeRows))
  }

  useEffect(() => { void load() }, [])

  const reset = () => { setErrors([]); setForm(defaultFor()) }
  const openNew = () => { reset(); setDrawerOpen(true) }
  const applyScope = (patch: Partial<AttendanceGroup>) => {
    setErrors([])
    setForm(current => {
      const next = { ...current, ...patch }
      const normalized = patch.clientId ? { ...next, workLocationId: locations.find(location => location.isActive && location.clientId === patch.clientId)?.id || 0, department: '', designation: '' } : patch.workLocationId ? { ...next, department: '', designation: '' } : patch.department !== undefined ? { ...next, designation: '' } : next
      return { ...normalized, employeeIds: employeeIdsFor(normalized) }
    })
  }
  const applyWorkLocation = (value: string) => {
    const workLocationId = Number(value)
    const location = locations.find(item => item.id === workLocationId)
    applyScope({ workLocationId, clientId: location?.clientId || form.clientId, id: 0 })
  }
  const set = <K extends keyof AttendanceGroup>(key: K, value: AttendanceGroup[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const toggleEmployee = (id: number) => set('employeeIds', form.employeeIds.includes(id) ? form.employeeIds.filter(item => item !== id) : [...form.employeeIds, id])

  const validate = () => {
    const next: string[] = []
    if (!form.name.trim()) next.push('Policy name is required.')
    if (!form.clientId) next.push('Select a client.')
    if (!form.workLocationId) next.push('Select a work location.')
    if (!form.workWeek) next.push('Select a weekly off pattern.')
    if (cycleLength > 31) next.push('Attendance cycle cannot exceed 31 days in any payroll month.')
    if (buffer < 3 || buffer > 7) next.push('Payroll report generation day must be 3 to 7 days after attendance cycle end day.')
    if (!form.employeeIds.length) next.push('Select at least one employee.')
    if (form.employeeIds.some(id => !matchingIds.includes(id))) next.push('Selected employees must match the selected client, location, department and designation.')
    setErrors(next)
    if (next.length) onMessage(next.join(' '))
    return next.length === 0
  }

  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveAttendanceGroup({ ...form, employeeIds: form.employeeIds.filter(id => matchingIds.includes(id)) })
    setSaving(false)
    if (response.ok && response.data) {
      setForm(response.data)
      setDrawerOpen(false)
      setGroups(await getAttendanceGroups())
      setErrors([])
      onMessage('Attendance policy saved.')
    } else {
      const error = response.error || 'Unable to save attendance policy.'
      setErrors([error])
      onMessage(error)
    }
  }
  const edit = (group: AttendanceGroup) => { setErrors([]); setForm({ ...emptyGroup, ...group, employeeIds: group.employeeIds || [] }); setDrawerOpen(true) }
  const remove = async (group: AttendanceGroup) => {
    if (!window.confirm(`Delete policy ${group.name}?`)) return
    const response = await deleteAttendanceGroup(group.clientId, group.id)
    if (response.ok) {
      setGroups(await getAttendanceGroups())
      if (form.id === group.id) reset()
      onMessage('Attendance policy deleted.')
    } else {
      const error = response.error || 'Unable to delete attendance policy.'
      setErrors([error])
      onMessage(error)
    }
  }

  return <section className="attendance-groups">
        <AntCard className="settings-panel settings-table-panel attendance-group-panel attendance-group-table" title="Attendance Policies" size="small">
          <div className="component-table-head"><div><b>Attendance policy master</b><span>Define client, location, employee scope, work-week, attendance cycle, and payroll report day.</span></div><Button type="primary" onClick={openNew}>Add policy</Button></div>
          <DataTable rows={groups} getRowId={row => row.id} emptyText="No attendance policies configured." exportFileName="attendance-policies" columns={[
            { key: 'name', label: 'Policy' },
            { key: 'clientName', label: 'Client' },
            { key: 'workLocationName', label: 'Location' },
            { key: 'department', label: 'Department', value: row => row.department || 'All' },
            { key: 'designation', label: 'Designation', value: row => row.designation || 'All' },
            { key: 'cycle', label: 'Attendance cycle', value: row => `${row.attendanceCycleStartDay} - ${row.attendanceCycleEndDay}` },
            { key: 'workWeek', label: 'Off pattern' },
            { key: 'employeeCount', label: 'Employees' },
            { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
          ]} actions={row => <Space size={6}><Button htmlType="button" size="small" type="primary" onClick={() => edit(row)}>Edit</Button><Button htmlType="button" size="small" danger onClick={() => void remove(row)}>Delete</Button></Space>} />
        </AntCard>
    <Drawer className="settings-master-drawer attendance-policy-master-drawer" title={<div className="settings-drawer-title"><span>Attendance policy</span><h3>{form.id ? 'Edit attendance policy' : 'Add attendance policy'}</h3><p>Define client, location, employee scope, weekly off, cycle, and payroll report day.</p></div>} open={drawerOpen} width={760} onClose={() => setDrawerOpen(false)} destroyOnClose>
      <Form className="attendance-group-form settings-quick-form" component="div" layout="vertical" requiredMark={false}>
        <Form.Item label="Policy name" required><Input value={form.name} onChange={event => set('name', event.target.value)} placeholder="Consultants - RECL Site A" /></Form.Item>
        <Form.Item label="Client" required><SearchSelect value={form.clientId} onChange={value => applyScope({ clientId: Number(value), id: 0 })} options={clients.map(client => ({ value: client.id, label: client.name }))} /></Form.Item>
        <Form.Item label="Work Location" required><SearchSelect value={form.workLocationId} onChange={applyWorkLocation} options={selectOptions(clientLocations.map(location => ({ value: location.id, label: `${location.name} - ${location.clientName || clients.find(client => client.id === location.clientId)?.name || 'Client'} - ${location.city || location.state || 'Location'}` })), 'Select work location', 0)} /></Form.Item>
        <Row gutter={12}>
          <Col xs={24} md={12}><Form.Item label="Department"><SearchSelect value={form.department} onChange={value => applyScope({ department: value, id: 0 })} options={selectOptions(departments, 'All departments')} /></Form.Item></Col>
          <Col xs={24} md={12}><Form.Item label="Designation"><SearchSelect value={form.designation} onChange={value => applyScope({ designation: value, id: 0 })} options={selectOptions(designations, 'All designations')} /></Form.Item></Col>
        </Row>
        <Form.Item label="Weekly off pattern" required><SearchSelect value={form.workWeek} onChange={value => set('workWeek', value as AttendanceWorkWeek)} options={selectOptions(workWeeks, 'Select weekly off pattern')} /></Form.Item>
        <Form.Item label="Payroll month preview" extra={`${cyclePreview.label} / ${cyclePreview.days} days`}><Input type="month" value={previewMonth} onChange={event => setPreviewMonth(event.target.value || currentMonth())} /></Form.Item>
        <Row gutter={12}>
          <Col span={8}><Form.Item label="Cycle start date" required><SearchSelect value={form.attendanceCycleStartDay} onChange={value => set('attendanceCycleStartDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
          <Col span={8}><Form.Item label="Cycle end date" extra={`Max ${cycleLength} days`} required><SearchSelect value={form.attendanceCycleEndDay} onChange={value => set('attendanceCycleEndDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
          <Col span={8}><Form.Item label="Payroll report date" extra={`${buffer} day buffer`} required><SearchSelect value={form.payrollReportGenerationDay} onChange={value => set('payrollReportGenerationDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
        </Row>
        <Form.Item><AntCheckbox checked={form.isActive} onChange={event => set('isActive', event.target.checked)}>Active</AntCheckbox></Form.Item>
        <AntCard className="attendance-group-panel attendance-group-members" title={`Employees (${selectedMatchingCount}/${matchingEmployees.length})`} size="small" extra={<Space><Button size="small" onClick={() => set('employeeIds', matchingIds)}>Select all</Button><Button size="small" onClick={() => set('employeeIds', [])}>Clear</Button></Space>}>
          <div className="attendance-employee-picker">{matchingEmployees.length ? matchingEmployees.map(employee => <label className={form.employeeIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={form.employeeIds.includes(employee.id)} onChange={() => toggleEmployee(employee.id)} /><span>{fullName(employee)}</span><small>{employee.employeeCode} / {employee.department || 'No department'} / {employee.designation || 'No designation'}</small></label>) : <p>No employees match this scope.</p>}</div>
        </AntCard>
        <Divider />
        <div className="attendance-group-actions"><Button htmlType="button" onClick={reset}>Reset</Button><Button htmlType="button" type="primary" loading={saving} onClick={() => void save()}>{form.id ? 'Update policy' : 'Save policy'}</Button></div>
      </Form>
    </Drawer>
  </section>
}
