import { clientChoice } from './ClientOption'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Form, Row, Space, Table, Tag, type TableColumnsType } from 'antd'
import { setup0, workWeekOptionsFromDrops } from '../data/payrollDefaults'
import { getClients } from '../services/payrollService'
import { getDropdowns, getSetup, getWorkLocations } from '../services/settingsService'
import { getLeaveAttendancePreferenceResult, saveLeaveAttendancePreferences } from '../services/leaveAttendanceService'
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
  const [referencesReady, setReferencesReady] = useState(false)
  const [preferenceReady, setPreferenceReady] = useState(false)
  const [rowsLoading, setRowsLoading] = useState(true)
  const [tableError, setTableError] = useState('')
  const preferenceRequest = useRef(0)
  const tableRequest = useRef(0)
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  const activeClients = useMemo(() => clients.filter(row => row.isActive), [clients])
  const clientLocations = useMemo(() => locations.filter(row => row.isActive && Number(row.clientId) === Number(preferences.clientId || clientId)), [locations, preferences.clientId, clientId])
  const selectedComponent = components.find(component => component.id === Number(preferences.leaveEncashmentSalaryComponentId || 0))
  const isFormulaBased = selectedComponent?.calculationType === 'Formula'
  const workWeeks = useMemo(() => workWeekOptionsFromDrops(dropdowns, preferences.workWeek), [dropdowns, preferences.workWeek])
  const buffer = useMemo(() => bufferDays(preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay), [preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay])
  const warning = preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased ? 'Leave encashment can only be enabled for formula-based salary components.' : ''

  const loadPreference = async (nextClientId = preferences.clientId, nextLocationId = Number(preferences.workLocationId || 0)) => {
    const request = ++preferenceRequest.current
    setPreferenceReady(false)
    setErrors([])
    setPreferences(empty(nextClientId, nextLocationId || null))
    if (!nextClientId) return
    const result = await getLeaveAttendancePreferenceResult(nextClientId, nextLocationId)
    if (request !== preferenceRequest.current) return
    if (!result.ok) { setErrors([result.error || 'Unable to load the selected attendance cycle.']); return }
    setPreferences({ ...empty(nextClientId, nextLocationId || null), ...result.data, clientId: nextClientId, workLocationId: nextLocationId || null })
    setPreferenceReady(true)
  }

  const loadSavedRows = async (clientRows = activeClients, locationRows = locations) => {
    const request = ++tableRequest.current
    setRowsLoading(true)
    setTableError('')
    const scopes = clientRows.flatMap(client => [0, ...locationRows
      .filter(location => location.clientId === client.id && location.isActive)
      .map(location => location.id)].map(locationId => ({ clientId: client.id, locationId })))
    const rows: (LeaveAttendancePreferences | undefined)[] = []
    let index = 0
    let incomplete = false
    // Keep background table requests from crowding out a user's selected cycle.
    const worker = async () => {
      while (request === tableRequest.current && index < scopes.length) {
        const position = index++
        const scope = scopes[position]
        const result = await getLeaveAttendancePreferenceResult(scope.clientId, scope.locationId)
        if (!result.ok) incomplete = true
        else if (result.data.id > 0) rows[position] = result.data
      }
    }
    await Promise.all(Array.from({ length: Math.min(4, scopes.length) }, worker))
    if (request !== tableRequest.current) return
    const unique = new Map(rows.flatMap(row => row ? [[row.id, row] as const] : []))
    setSavedRows([...unique.values()])
    setTableError(incomplete ? 'Some saved cycles could not be loaded.' : '')
    setRowsLoading(false)
  }

  useEffect(() => {
    let active = true
    setReferencesReady(false)
    setPreferenceReady(false)
    setRowsLoading(true)
    setTableError('')
    setSavedRows([])
    const directory = getClients(true).then(rows => {
      const activeClients = rows.filter(row => row.isActive)
      if (active) {
        setClients(activeClients)
        void loadPreference(clientId || activeClients[0]?.id || 0, 0)
      }
      return activeClients
    })
    void Promise.all([directory, getWorkLocations(), getSetup(setup0), getDropdowns()]).then(([clientRows, locationRows, setup, dropdownRows]) => {
      if (!active) return
      setLocations(locationRows.filter(row => row.isActive))
      setComponents(setup.salaryComponents ?? [])
      setDropdowns(dropdownRows)
      setReferencesReady(true)
      void loadSavedRows(clientRows, locationRows)
    })
    return () => { active = false; ++preferenceRequest.current; ++tableRequest.current }
  }, [clientId])

  const set = <K extends keyof LeaveAttendancePreferences>(key: K, value: LeaveAttendancePreferences[K]) => {
    setErrors([])
    setPreferences(current => ({ ...current, [key]: value }))
  }

  const changeClient = async (value: string) => {
    const nextClientId = Number(value)
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
    if (!preferenceReady || !referencesReady || !validate()) return
    const request = preferenceRequest.current
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
    if (request !== preferenceRequest.current) return
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
          <Form.Item label="Client" required><SearchSelect disabled={saving} value={preferences.clientId} onChange={changeClient} options={activeClients.map(clientChoice)} /></Form.Item>
          <Form.Item label="Work Location" required><SearchSelect disabled={!referencesReady || saving} value={preferences.workLocationId ?? 0} onChange={changeLocation} options={selectOptions([{ value: 0, label: 'All locations' }, ...clientLocations.map(location => ({ value: location.id, label: `${location.name} - ${location.clientName || activeClients.find(client => client.id === location.clientId)?.name || 'Client'} - ${location.city || location.state || 'Location'}` }))])} /></Form.Item>
          {preferenceReady && referencesReady ? <>
          <Form.Item label="Weekly off pattern" required><SearchSelect value={preferences.workWeek} onChange={value => set('workWeek', value as AttendanceWorkWeek)} options={selectOptions(workWeeks, 'Select weekly off pattern')} /></Form.Item>
          <Row gutter={12}>
            <Col span={8}><Form.Item label="Cycle start date" required><SearchSelect value={preferences.attendanceCycleStartDay} onChange={value => set('attendanceCycleStartDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
            <Col span={8}><Form.Item label="Cycle end date" required><SearchSelect value={preferences.attendanceCycleEndDay} onChange={value => set('attendanceCycleEndDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
            <Col span={8}><Form.Item label="Payroll report date" extra={`${buffer} day buffer`} required><SearchSelect value={preferences.payrollReportGenerationDay} onChange={value => set('payrollReportGenerationDay', Number(value))} options={selectOptions(dayOptions)} /></Form.Item></Col>
          </Row>
          <Form.Item label="Leave encashment salary component"><SearchSelect value={preferences.leaveEncashmentSalaryComponentId ?? ''} onChange={value => set('leaveEncashmentSalaryComponentId', value ? Number(value) : null)} options={selectOptions(components.map(component => ({ value: component.id, label: `${component.name} / ${component.calculationType}` })), 'Select salary component')} /></Form.Item>
          <Form.Item><AntCheckbox checked={preferences.includeLeaveEncashmentInPayRun} disabled={!!selectedComponent && !isFormulaBased} onChange={event => set('includeLeaveEncashmentInPayRun', event.target.checked)}>Include leave encashment details in pay run</AntCheckbox></Form.Item>
          <Space><Button onClick={() => void loadPreference(preferences.clientId, Number(preferences.workLocationId || 0))}>Reset</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save cycle</Button></Space></> : errors.length ? <Button onClick={() => void loadPreference()}>Retry</Button> : <small role="status">{preferences.clientId ? 'Loading attendance cycle…' : 'Select a client.'}</small>}
        </Form>
      </AntCard>
    </Col>
    <Col xs={24} lg={14}>
      <AntCard className="settings-panel settings-table-panel" title="Saved attendance cycles" size="small">
        {tableError && <Alert type="warning" showIcon message={tableError} action={<Button onClick={() => void loadSavedRows()}>Retry</Button>} />}
        <Table loading={rowsLoading} size="small" rowKey={row => `${row.clientId}-${row.workLocationId || 0}`} dataSource={savedRows} pagination={false} columns={savedColumns} />
      </AntCard>
    </Col>
  </Row>
}
