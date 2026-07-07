import { useEffect, useMemo, useState } from 'react'
import { Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, Row, Select, Space } from 'antd'
import { getClients, getEmployees } from '../services/payrollService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import { deleteAttendanceGroup, getAttendanceGroups, saveAttendanceGroupBatch } from '../services/leaveAttendanceService'
import type { AttendanceGroup, AttendanceWorkWeek, Client, Drop, Employee, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'

type AttendancePolicyForm = AttendanceGroup & { workLocationIds: number[]; departments: string[]; designations: string[] }
type AttendancePolicyRow = AttendanceGroup & { rows: AttendanceGroup[]; locationCount: number; departmentCount: number; designationCount: number }

const emptyGroup: AttendanceGroup = { id: 0, clientId: 0, clientName: '', policyBatchId: '', name: '', workLocationId: 0, workLocationName: '', department: '', designation: '', workWeek: '', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, isActive: true, employeeIds: [], employeeNames: '', employeeCount: 0 }
const emptyForm: AttendancePolicyForm = { ...emptyGroup, workLocationIds: [], departments: [], designations: [] }
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const scopeOptions = (items: string[]) => unique(items).filter(item => item.toLowerCase() !== 'all')
const fullName = (employee: Employee) => `${employee.firstName} ${employee.lastName}`.trim() || employee.employeeCode
const bufferDays = (endDay: number, reportDay: number, monthDays: number) => reportDay >= endDay ? reportDay - endDay : reportDay + monthDays - endDay
const currentMonth = () => {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`
}
const daysInPreviewMonth = (month: string) => {
  const [year, monthNumber] = month.split('-').map(Number)
  return new Date(year || new Date().getFullYear(), monthNumber || 1, 0).getDate()
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
const clampCycleForMonth = (month: string, form: AttendancePolicyForm) => {
  const monthDays = daysInPreviewMonth(month)
  const normalized = {
    ...form,
    attendanceCycleStartDay: Math.min(form.attendanceCycleStartDay, monthDays),
    attendanceCycleEndDay: Math.min(form.attendanceCycleEndDay, monthDays),
    payrollReportGenerationDay: Math.min(form.payrollReportGenerationDay, monthDays)
  }
  const preview = cyclePreviewFor(month, normalized.attendanceCycleStartDay, normalized.attendanceCycleEndDay)
  if (preview.days <= monthDays) return { form: normalized, changed: normalized !== form && (normalized.attendanceCycleStartDay !== form.attendanceCycleStartDay || normalized.attendanceCycleEndDay !== form.attendanceCycleEndDay || normalized.payrollReportGenerationDay !== form.payrollReportGenerationDay) }
  const endMonth = monthStart(month)
  const startMonth = normalized.attendanceCycleStartDay > 1 ? addMonths(endMonth, -1) : endMonth
  const start = new Date(startMonth.getFullYear(), startMonth.getMonth(), clampDay(startMonth, normalized.attendanceCycleStartDay))
  const maxEnd = new Date(start.getFullYear(), start.getMonth(), start.getDate() + monthDays - 1)
  return { form: { ...normalized, attendanceCycleEndDay: maxEnd.getDate() }, changed: true }
}

export default function AttendanceGroupsManager({ onMessage }: { onMessage: (message: string) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [groups, setGroups] = useState<AttendanceGroup[]>([])
  const [form, setForm] = useState<AttendancePolicyForm>(emptyForm)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)
  const [previewMonth, setPreviewMonth] = useState(currentMonth())
  const [employeeQuery, setEmployeeQuery] = useState('')

  const activeEmployees = useMemo(() => employees.filter(employee => employee.isActive), [employees])
  const clientLocations = useMemo(() => locations.filter(location => location.isActive && (!form.clientId || location.clientId === form.clientId)), [locations, form.clientId])
  const locationEmployees = useMemo(() => activeEmployees.filter(employee => employee.clientId === form.clientId && form.workLocationIds.includes(employee.workLocationId)), [activeEmployees, form.clientId, form.workLocationIds])
  const departments = useMemo(() => scopeOptions([...dropdowns.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), ...locationEmployees.map(employee => employee.department), ...form.departments]), [dropdowns, locationEmployees, form.departments])
  const departmentEmployees = useMemo(() => locationEmployees.filter(employee => form.departments.includes(employee.department)), [locationEmployees, form.departments])
  const designations = useMemo(() => scopeOptions([...dropdowns.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value), ...departmentEmployees.map(employee => employee.designation), ...form.designations]), [dropdowns, departmentEmployees, form.designations])
  const workWeeks = useMemo(() => unique([...dropdowns.filter(item => item.type === 'Work Week' && item.isActive).map(item => item.value), form.workWeek]), [dropdowns, form.workWeek])
  const mappedEmployeePolicyById = useMemo(() => {
    const currentBatch = form.policyBatchId || ''
    const mapped = new Map<number, string>()
    groups.filter(group => group.isActive && (!currentBatch || (group.policyBatchId || '') !== currentBatch)).forEach(group => group.employeeIds.forEach(employeeId => mapped.set(employeeId, group.name)))
    return mapped
  }, [groups, form.policyBatchId])
  const matchingEmployees = useMemo(() => locationEmployees.filter(employee => form.departments.includes(employee.department) && form.designations.includes(employee.designation) && !mappedEmployeePolicyById.has(employee.id)), [locationEmployees, form.departments, form.designations, mappedEmployeePolicyById])
  const visibleMatchingEmployees = useMemo(() => {
    const query = employeeQuery.trim().toLowerCase()
    if (!query) return matchingEmployees
    return matchingEmployees.filter(employee => `${fullName(employee)} ${employee.employeeCode} ${employee.department} ${employee.designation}`.toLowerCase().includes(query))
  }, [employeeQuery, matchingEmployees])
  const matchingIds = useMemo(() => matchingEmployees.map(employee => employee.id), [matchingEmployees])
  const selectedMatchingCount = form.employeeIds.filter(id => matchingIds.includes(id)).length
  const monthDays = daysInPreviewMonth(previewMonth)
  const dayOptions = useMemo(() => Array.from({ length: monthDays }, (_, index) => index + 1), [monthDays])
  const buffer = bufferDays(form.attendanceCycleEndDay, form.payrollReportGenerationDay, monthDays)
  const cyclePreview = useMemo(() => cyclePreviewFor(previewMonth, form.attendanceCycleStartDay, form.attendanceCycleEndDay), [previewMonth, form.attendanceCycleStartDay, form.attendanceCycleEndDay])

  const employeeIdsFor = (group: AttendancePolicyForm, sourceEmployees = activeEmployees) => sourceEmployees.filter(employee => employee.clientId === group.clientId && group.workLocationIds.includes(employee.workLocationId) && group.departments.includes(employee.department) && group.designations.includes(employee.designation) && !mappedEmployeePolicyById.has(employee.id)).map(employee => employee.id)
  const defaultFor = (clientRows = clients, locationRows = locations, employeeRows = activeEmployees) => {
    const clientId = clientRows.find(client => client.isActive)?.id || 0
    const workLocationId = locationRows.find(location => location.isActive && location.clientId === clientId)?.id || 0
    const next = { ...emptyForm, clientId, workLocationId, workLocationIds: workLocationId ? [workLocationId] : [] }
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

  const policyRows = useMemo<AttendancePolicyRow[]>(() => {
    const grouped = new Map<string, AttendancePolicyRow & { locationSet: Set<string>; departmentSet: Set<string>; designationSet: Set<string>; employeeSet: Set<number> }>()
    groups.forEach(group => {
      const suffix = ` - ${group.workLocationName} - ${group.department} - ${group.designation}`
      const baseName = group.name.endsWith(suffix) ? group.name.slice(0, -suffix.length) : group.name
      const key = group.policyBatchId || `single-${group.id}`
      if (!grouped.has(key)) grouped.set(key, { ...group, policyBatchId: group.policyBatchId || key, name: baseName, rows: [], locationSet: new Set(), departmentSet: new Set(), designationSet: new Set(), employeeSet: new Set(), locationCount: 0, departmentCount: 0, designationCount: 0 })
      const row = grouped.get(key)!
      row.rows.push(group)
      if (group.workLocationName) row.locationSet.add(group.workLocationName)
      if (group.department) row.departmentSet.add(group.department)
      if (group.designation) row.designationSet.add(group.designation)
      group.employeeIds.forEach(id => row.employeeSet.add(id))
      row.isActive = row.isActive || group.isActive
    })
    return Array.from(grouped.values()).map(row => ({
      ...row,
      workLocationName: Array.from(row.locationSet).join(', ') || '-',
      department: Array.from(row.departmentSet).join(', ') || '-',
      designation: Array.from(row.designationSet).join(', ') || '-',
      employeeIds: Array.from(row.employeeSet),
      employeeCount: row.employeeSet.size,
      locationCount: row.locationSet.size,
      departmentCount: row.departmentSet.size,
      designationCount: row.designationSet.size
    }))
  }, [groups])

  const reset = () => { setErrors([]); setEmployeeQuery(''); setForm(defaultFor()) }
  const openNew = () => { reset(); setDrawerOpen(true) }
  const applyScope = (patch: Partial<AttendancePolicyForm>) => {
    setErrors([])
    setForm(current => {
      const next = { ...current, ...patch }
      const scopedEmployees = activeEmployees.filter(employee => employee.clientId === next.clientId && next.workLocationIds.includes(employee.workLocationId))
      const keepDepartments = (items: string[]) => {
        const available = new Set(scopedEmployees.map(employee => employee.department).filter(Boolean))
        const kept = items.filter(item => available.has(item))
        return kept.length ? kept : items
      }
      const keepDesignations = (items: string[], departmentsForScope: string[]) => {
        const available = new Set(scopedEmployees.filter(employee => departmentsForScope.includes(employee.department)).map(employee => employee.designation).filter(Boolean))
        const kept = items.filter(item => available.has(item))
        return kept.length ? kept : items
      }
      const normalized = patch.workLocationIds !== undefined ? (() => {
        if (!next.workLocationIds.length) return { ...next, workLocationId: 0, department: '', designation: '', departments: [], designations: [] }
        const departments = keepDepartments(next.departments)
        const designations = keepDesignations(next.designations, departments)
        return { ...next, workLocationId: patch.workLocationIds[0] || 0, department: departments[0] || '', designation: designations[0] || '', departments, designations }
      })() : patch.clientId ? (() => {
        const workLocationId = locations.find(location => location.isActive && location.clientId === patch.clientId)?.id || 0
        return { ...next, workLocationId, workLocationIds: workLocationId ? [workLocationId] : [], department: '', designation: '', departments: [], designations: [] }
      })() : patch.departments !== undefined ? (() => {
        if (!patch.departments.length) return { ...next, department: '', designation: '', departments: [], designations: [] }
        const designations = keepDesignations(next.designations, patch.departments)
        return { ...next, department: patch.departments[0] || '', designation: designations[0] || '', designations }
      })() : patch.designations !== undefined ? { ...next, designation: patch.designations[0] || '' } : next
      return { ...normalized, employeeIds: employeeIdsFor(normalized) }
    })
  }
  const set = <K extends keyof AttendancePolicyForm>(key: K, value: AttendancePolicyForm[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const toggleEmployee = (id: number) => set('employeeIds', form.employeeIds.includes(id) ? form.employeeIds.filter(item => item !== id) : [...form.employeeIds, id])
  const setCycle = (patch: Partial<AttendancePolicyForm>, month = previewMonth) => {
    setErrors([])
    setForm(current => {
      const next = { ...current, ...patch }
      const clamped = clampCycleForMonth(month, next)
      if (clamped.changed) onMessage(`Attendance cycle cannot exceed ${daysInPreviewMonth(month)} days for ${month}. Cycle end date adjusted.`)
      return clamped.form
    })
  }
  const changePreviewMonth = (month: string) => {
    const nextMonth = month || currentMonth()
    setPreviewMonth(nextMonth)
    setCycle({}, nextMonth)
  }

  const validate = () => {
    const next: string[] = []
    if (!form.name.trim()) next.push('Policy name is required.')
    if (!form.clientId) next.push('Select a client.')
    if (!form.workLocationIds.length) next.push('Select at least one work location.')
    if (!form.departments.length) next.push('Select at least one department.')
    if (!form.designations.length) next.push('Select at least one designation.')
    if (!form.workWeek) next.push('Select a weekly off pattern.')
    if (cyclePreview.days > monthDays) next.push(`Attendance cycle cannot exceed ${monthDays} days for ${previewMonth}.`)
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
    const response = await saveAttendanceGroupBatch({
      policyBatchId: form.policyBatchId,
      clientId: form.clientId,
      name: form.name,
      workLocationIds: form.workLocationIds,
      departments: form.departments,
      designations: form.designations,
      workWeek: form.workWeek,
      attendanceCycleStartDay: form.attendanceCycleStartDay,
      attendanceCycleEndDay: form.attendanceCycleEndDay,
      payrollReportGenerationDay: form.payrollReportGenerationDay,
      isActive: form.isActive,
      employeeIds: form.employeeIds.filter(id => matchingIds.includes(id))
    })
    setSaving(false)
    if (response.ok) {
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
  const edit = (group: AttendancePolicyRow) => {
    const rows = group.rows.length ? group.rows : [group]
    setErrors([])
    setForm({
      ...emptyForm,
      ...group,
      workLocationIds: Array.from(new Set(rows.map(row => row.workLocationId).filter(Boolean))),
      departments: unique(rows.map(row => row.department)),
      designations: unique(rows.map(row => row.designation)),
      employeeIds: Array.from(new Set(rows.flatMap(row => row.employeeIds || [])))
    })
    setDrawerOpen(true)
  }
  const remove = async (group: AttendancePolicyRow) => {
    if (!window.confirm(`Delete policy ${group.name}?`)) return
    const rows = group.rows.length ? group.rows : [group]
    const results = await Promise.all(rows.map(row => deleteAttendanceGroup(row.clientId, row.id)))
    if (results.every(response => response.ok)) {
      setGroups(await getAttendanceGroups())
      if (form.id === group.id) reset()
      onMessage('Attendance policy deleted.')
    } else {
      const error = results.find(response => !response.ok)?.error || 'Unable to delete attendance policy.'
      setErrors([error])
      onMessage(error)
    }
  }

  return <section className="attendance-groups">
        <AntCard className="settings-panel settings-table-panel attendance-group-panel attendance-group-table" title="Attendance Policies" size="small">
          <div className="component-table-head"><div><b>Attendance policy master</b><span>Define client, location, employee scope, work-week, attendance cycle, and payroll report day.</span></div><Button type="primary" onClick={openNew}>Add policy</Button></div>
          <DataTable rows={policyRows} getRowId={row => row.policyBatchId || row.id} emptyText="No attendance policies configured." exportFileName="attendance-policies" columns={[
            { key: 'name', label: 'Policy' },
            { key: 'clientName', label: 'Client' },
            { key: 'workLocationName', label: 'Location' },
            { key: 'department', label: 'Department', value: row => row.department || '-' },
            { key: 'designation', label: 'Designation', value: row => row.designation || '-' },
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
        <Form.Item label="Work Location" required><Select mode="multiple" className="app-search-select attendance-policy-multi" popupClassName="app-search-select-dropdown" showSearch value={form.workLocationIds.map(String)} optionFilterProp="label" onChange={values => applyScope({ workLocationIds: values.map(Number), id: 0 })} options={clientLocations.map(location => ({ value: String(location.id), label: `${location.name} - ${location.city || location.state || 'Location'}` }))} /></Form.Item>
        <Row gutter={12}>
          <Col xs={24} md={12}><Form.Item label="Department" required><Select mode="multiple" className="app-search-select attendance-policy-multi" popupClassName="app-search-select-dropdown" showSearch value={form.departments} optionFilterProp="label" onChange={values => applyScope({ departments: values, id: 0 })} options={departments.map(item => ({ value: item, label: item }))} /></Form.Item></Col>
          <Col xs={24} md={12}><Form.Item label="Designation" required><Select mode="multiple" className="app-search-select attendance-policy-multi" popupClassName="app-search-select-dropdown" showSearch value={form.designations} optionFilterProp="label" onChange={values => applyScope({ designations: values, id: 0 })} options={designations.map(item => ({ value: item, label: item }))} /></Form.Item></Col>
        </Row>
        <Form.Item label="Weekly off pattern" required><SearchSelect value={form.workWeek} onChange={value => set('workWeek', value as AttendanceWorkWeek)} options={selectOptions(workWeeks, 'Select weekly off pattern')} /></Form.Item>
        <Form.Item label="Payroll month preview" extra={`${cyclePreview.label} / ${cyclePreview.days} of ${monthDays} days`}><Input type="month" value={previewMonth} onChange={event => changePreviewMonth(event.target.value)} /></Form.Item>
        <Row gutter={12}>
          <Col span={8}><Form.Item label="Cycle start date" required><SearchSelect value={form.attendanceCycleStartDay} onChange={value => setCycle({ attendanceCycleStartDay: Number(value) })} options={selectOptions(dayOptions)} /></Form.Item></Col>
          <Col span={8}><Form.Item label="Cycle end date" extra={`Max ${monthDays} days`} required><SearchSelect value={form.attendanceCycleEndDay} onChange={value => setCycle({ attendanceCycleEndDay: Number(value) })} options={selectOptions(dayOptions)} /></Form.Item></Col>
          <Col span={8}><Form.Item label="Payroll report date" extra={`${buffer} day buffer`} required><SearchSelect value={form.payrollReportGenerationDay} onChange={value => setCycle({ payrollReportGenerationDay: Number(value) })} options={selectOptions(dayOptions)} /></Form.Item></Col>
        </Row>
        <Form.Item><AntCheckbox checked={form.isActive} onChange={event => set('isActive', event.target.checked)}>Active</AntCheckbox></Form.Item>
        <AntCard className="attendance-group-panel attendance-group-members" title={`Employees (${selectedMatchingCount}/${matchingEmployees.length})`} size="small" extra={<Space className="attendance-employee-tools" wrap><Input allowClear placeholder="Search employee..." value={employeeQuery} onChange={event => setEmployeeQuery(event.target.value)} /><Button size="small" onClick={() => set('employeeIds', matchingIds)}>Select all</Button><Button size="small" onClick={() => set('employeeIds', [])}>Clear</Button></Space>}>
          <div className="attendance-employee-picker">{visibleMatchingEmployees.length ? visibleMatchingEmployees.map(employee => <label className={form.employeeIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={form.employeeIds.includes(employee.id)} onChange={() => toggleEmployee(employee.id)} /><span>{fullName(employee)}</span><small>{employee.employeeCode} / {employee.department || 'No department'} / {employee.designation || 'No designation'}</small></label>) : <p>No employees match this scope.</p>}</div>
        </AntCard>
        <Divider />
        <div className="attendance-group-actions"><Button htmlType="button" onClick={reset}>Reset</Button><Button htmlType="button" type="primary" loading={saving} onClick={() => void save()}>{form.id ? 'Update policy' : 'Save policy'}</Button></div>
      </Form>
    </Drawer>
  </section>
}
