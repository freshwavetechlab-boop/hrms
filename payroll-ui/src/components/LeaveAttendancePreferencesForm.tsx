import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Form, Row, Space, Table, Tag, type TableColumnsType } from 'antd'
import { setup0 } from '../data/payrollDefaults'
import { getClients } from '../services/payrollService'
import { getDropdowns, getSetup, getWorkLocations } from '../services/settingsService'
import { getLeaveAttendancePreferences, saveLeaveAttendancePreferences } from '../services/leaveAttendanceService'
import type { AttendanceWorkWeek, Client, Component, Drop, LeaveAttendancePreferences, WorkLocation } from '../types/payroll'
import SearchSelect, { selectOptions } from './SearchSelect'

const dayOptions = Array.from({ length: 31 }, (_, index) => index + 1)
const empty = (clientId = 0, workLocationId: number | null = null): LeaveAttendancePreferences => ({ id: 0, clientId, workLocationId, workLocationName: workLocationId ? '' : 'All locations', workWeek: '', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null })
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const bufferDays = (endDay: number, reportDay: number) => reportDay >= endDay ? reportDay - endDay : reportDay + 31 - endDay

export default function LeaveAttendancePreferencesForm({ clientId, onSaved }: { clientId: number; onSaved: (message: string) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [components, setComponents] = useState<Component[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [preferences, setPreferences] = useState<LeaveAttendancePreferences>(empty(clientId))
  const [savedRows, setSavedRows] = useState<LeaveAttendancePreferences[]>([])
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  const activeClients = useMemo(() => clients.filter(row => row.isActive), [clients])
  const clientLocations = useMemo(() => locations.filter(row => row.isActive && Number(row.clientId) === Number(preferences.clientId || clientId)), [locations, preferences.clientId, clientId])
  const selectedComponent = components.find(component => component.id === Number(preferences.leaveEncashmentSalaryComponentId || 0))
  const isFormulaBased = selectedComponent?.calculationType === 'Formula'
  const workWeeks = useMemo(() => unique([...dropdowns.filter(item => item.type === 'Work Week' && item.isActive).map(item => item.value), preferences.workWeek]), [dropdowns, preferences.workWeek])
  const buffer = useMemo(() => bufferDays(preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay), [preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay])
  const warning = preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased ? 'Leave encashment can only be enabled for formula-based salary components.' : ''

  const loadPreference = async (nextClientId = preferences.clientId, nextLocationId = Number(preferences.workLocationId || 0)) => {
    if (!nextClientId) return
    const saved = await getLeaveAttendancePreferences(nextClientId, nextLocationId)
    setPreferences({ ...empty(nextClientId, nextLocationId || null), ...saved, clientId: nextClientId, workLocationId: nextLocationId || null })
  }

  const loadSavedRows = async (clientRows = activeClients, locationRows = locations) => {
    const rows = await Promise.all(clientRows.flatMap(client => [
      getLeaveAttendancePreferences(client.id, 0),
      ...locationRows.filter(location => location.clientId === client.id && location.isActive).map(location => getLeaveAttendancePreferences(client.id, location.id))
    ]))
    const uniqueRows = rows.filter(row => row.id > 0).filter((row, index, all) => all.findIndex(item => item.clientId === row.clientId && Number(item.workLocationId || 0) === Number(row.workLocationId || 0)) === index)
    setSavedRows(uniqueRows)
  }

  useEffect(() => {
    void Promise.all([getClients(), getWorkLocations(), getSetup(setup0), getDropdowns()]).then(async ([clientRows, locationRows, setup, dropdownRows]) => {
      const active = clientRows.filter(row => row.isActive)
      const nextClientId = clientId || active[0]?.id || 0
      setClients(active)
      setLocations(locationRows.filter(row => row.isActive))
      setComponents(setup.salaryComponents ?? [])
      setDropdowns(dropdownRows)
      setPreferences(empty(nextClientId))
      await loadSavedRows(active, locationRows)
      if (nextClientId) await loadPreference(nextClientId, 0)
    })
  }, [clientId])

  const set = <K extends keyof LeaveAttendancePreferences>(key: K, value: LeaveAttendancePreferences[K]) => {
    setErrors([])
    setPreferences(current => ({ ...current, [key]: value }))
  }

  const changeClient = async (value: string) => {
    const nextClientId = Number(value)
    setPreferences(empty(nextClientId))
    await loadPreference(nextClientId, 0)
  }

  const changeLocation = async (value: string) => {
    const nextLocationId = Number(value || 0)
    const selectedLocation = locations.find(location => location.id === nextLocationId)
    await loadPreference(selectedLocation?.clientId || preferences.clientId, nextLocationId)
  }

  const validate = () => {
    const nextErrors: string[] = []
    if (!preferences.clientId) nextErrors.push('Select a client.')
    if (preferences.workLocationId === undefined) nextErrors.push('Select a work location.')
    if (!preferences.workWeek) nextErrors.push('Select a weekly off pattern.')
    if (buffer < 3 || buffer > 7) nextErrors.push('Payroll report generation day must be 3 to 7 days after attendance cycle end day.')
    if (preferences.includeLeaveEncashmentInPayRun && !preferences.leaveEncashmentSalaryComponentId) nextErrors.push('Select a salary component for leave encashment.')
    if (preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased) nextErrors.push('Selected salary component must be formula-based.')
    setErrors(nextErrors)
    return nextErrors.length === 0
  }

  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveLeaveAttendancePreferences({
      clientId: preferences.clientId,
      workLocationId: preferences.workLocationId ? Number(preferences.workLocationId) : null,
      workWeek: preferences.workWeek,
      attendanceCycleStartDay: preferences.attendanceCycleStartDay,
      attendanceCycleEndDay: preferences.attendanceCycleEndDay,
      payrollReportGenerationDay: preferences.payrollReportGenerationDay,
      includeLeaveEncashmentInPayRun: preferences.includeLeaveEncashmentInPayRun,
      leaveEncashmentSalaryComponentId: preferences.leaveEncashmentSalaryComponentId ? Number(preferences.leaveEncashmentSalaryComponentId) : null
    })
    setSaving(false)
    if (response.ok) {
      setPreferences(response.data)
      setErrors([])
      await loadSavedRows()
      onSaved('Attendance cycle saved.')
    } else setErrors([response.error || 'Unable to save attendance cycle.'])
  }

  const savedColumns: TableColumnsType<LeaveAttendancePreferences> = [
    { title: 'Client', dataIndex: 'clientId', render: value => activeClients.find(client => client.id === value)?.name || value },
    { title: 'Work Location', dataIndex: 'workLocationName', render: (_, row) => row.workLocationId ? row.workLocationName || locations.find(location => location.id === row.workLocationId)?.name : 'All locations' },
    { title: 'Off pattern', dataIndex: 'workWeek' },
    { title: 'Attendance cycle', render: (_, row) => `${row.attendanceCycleStartDay} - ${row.attendanceCycleEndDay}` },
    { title: 'Report date', dataIndex: 'payrollReportGenerationDay' },
    { title: 'Scope', render: (_, row) => <Tag color={row.workLocationId ? 'blue' : 'default'}>{row.workLocationId ? 'Location' : 'Fallback'}</Tag> }
  ]

  return <Row gutter={[16, 16]} className="settings-split attendance-cycle-layout">
    <Col xs={24} lg={10}>
      <AntCard className="settings-panel settings-form-panel" title="Attendance Cycle" size="small">
        <Form className="settings-quick-form" component={false} layout="vertical" requiredMark={false}>
          {errors.length > 0 && <Alert type="error" showIcon message={errors.join(' ')} />}
          {warning && <Alert type="warning" showIcon message={warning} />}
          <Form.Item label="Client" required><SearchSelect value={preferences.clientId} onChange={changeClient} options={activeClients.map(client => ({ value: client.id, label: client.name }))} /></Form.Item>
          <Form.Item label="Work Location" required><SearchSelect value={preferences.workLocationId ?? 0} onChange={changeLocation} options={selectOptions([{ value: 0, label: 'All locations' }, ...clientLocations.map(location => ({ value: location.id, label: `${location.name} - ${location.clientName || activeClients.find(client => client.id === location.clientId)?.name || 'Client'} - ${location.city || location.state || 'Location'}` }))])} /></Form.Item>
          <Form.Item label="Weekly off pattern" required><SearchSelect value={preferences.workWeek} onChange={value => set('workWeek', value as AttendanceWorkWeek)} options={selectOptions(workWeeks, 'Select weekly off pattern')} /></Form.Item>
          <Row gutter={12}>
            <Col span={8}><Form.Item label="Cycle start date" required><SearchSelect value={preferences.attendanceCycleStartDay} onChange={value => set('attendanceCycleStartDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
            <Col span={8}><Form.Item label="Cycle end date" required><SearchSelect value={preferences.attendanceCycleEndDay} onChange={value => set('attendanceCycleEndDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
            <Col span={8}><Form.Item label="Payroll report date" extra={`${buffer} day buffer`} required><SearchSelect value={preferences.payrollReportGenerationDay} onChange={value => set('payrollReportGenerationDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
          </Row>
          <Form.Item label="Leave encashment salary component"><SearchSelect value={preferences.leaveEncashmentSalaryComponentId ?? ''} onChange={value => set('leaveEncashmentSalaryComponentId', value ? Number(value) : null)} options={selectOptions(components.map(component => ({ value: component.id, label: `${component.name} / ${component.calculationType}` })), 'Select salary component')} /></Form.Item>
          <Form.Item><AntCheckbox checked={preferences.includeLeaveEncashmentInPayRun} disabled={!!selectedComponent && !isFormulaBased} onChange={event => set('includeLeaveEncashmentInPayRun', event.target.checked)}>Include leave encashment details in pay run</AntCheckbox></Form.Item>
          <Space><Button onClick={() => void loadPreference(preferences.clientId, Number(preferences.workLocationId || 0))}>Reset</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save cycle</Button></Space>
        </Form>
      </AntCard>
    </Col>
    <Col xs={24} lg={14}>
      <AntCard className="settings-panel settings-table-panel" title="Saved attendance cycles" size="small">
        <Table size="small" rowKey={row => `${row.clientId}-${row.workLocationId || 0}`} dataSource={savedRows} pagination={false} columns={savedColumns} />
      </AntCard>
    </Col>
  </Row>
}
