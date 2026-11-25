import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Form, Input, Modal, Tag } from 'antd'
import { CalendarOutlined, LockOutlined, LogoutOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { postJson } from '../services/apiClient'
import DataTable from './DataTable'
import './EmployeeResignation.css'

export type ResignationEmployee = { id: number; employeeCode: string; name: string }
type EmployeeResignationActionProps = {
  clientId: number; employees: ResignationEmployee[]; disabled?: boolean; onSaved: () => void | Promise<void>
  label?: string; size?: 'small' | 'middle' | 'large'; hideTrigger?: boolean
  open?: boolean; onOpenChange?: (open: boolean) => void; onOpen?: () => void
}
export default function EmployeeResignationAction({ clientId, employees, disabled = false, onSaved, label, size, hideTrigger = false, open: controlledOpen, onOpenChange, onOpen }: EmployeeResignationActionProps) {
  const [localOpen, setLocalOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const open = controlledOpen ?? localOpen
  const changeOpen = (value: boolean) => { setLocalOpen(value); onOpenChange?.(value) }
  const [lastWorkingDate, setDate] = useState(''), [reason, setReason] = useState('Employment ended; reviewed client roster.')
  const [dates, setDates] = useState<Record<number, string>>({}), [months, setMonths] = useState<Record<number, string | null>>({}), [loadingDates, setLoadingDates] = useState(false)
  const generation = useRef(0)
  const employeesRef = useRef(employees); employeesRef.current = employees
  const selectionKey = employees.map(row => row.id).join(',')
  const single = employees.length === 1
  useEffect(() => {
    if (!open) return
    const selected = employeesRef.current
    const version = ++generation.current
    setError(''); setDate(''); setDates({}); setMonths({}); setReason('Employment ended; reviewed client roster.'); setLoadingDates(true)
    const loadDefaults = async () => {
    try {
      const response = await postJson('/api/employees/resignation-defaults', { clientId, employeeIds: selected.map(row => row.id) }, [] as { employeeId: number; lastPayrollMonth: string | null; lastWorkingDate: string | null }[], { toast: false, loader: false })
      if (version !== generation.current) return
      if (!response.ok) { setError(response.error || 'Enter reviewed last working dates; payroll defaults could not be loaded.'); return }
      const values = Object.fromEntries(response.data.map(row => [row.employeeId, row.lastWorkingDate || '']))
      setDates(values); setMonths(Object.fromEntries(response.data.map(row => [row.employeeId, row.lastPayrollMonth])))
      const common = [...new Set(selected.map(row => values[row.id] || ''))]
      setDate(common.length === 1 ? common[0] : '')
    } catch {
      if (version === generation.current) setError('Unable to load suggested dates. Enter the last working date to continue.')
    } finally { if (version === generation.current) setLoadingDates(false) }
    }
    void loadDefaults()
    return () => { generation.current++ }
  }, [open, clientId, selectionKey])
  const ready = employees.length > 0 && employees.every(row => dates[row.id])
  const save = async () => {
    if (busy || loadingDates || !ready || reason.trim().length < 3) return
    setBusy(true); setError('')
    try {
      const response = await postJson('/api/employees/mark-resigned', { clientId, employeeIds: employees.map(row => row.id), lastWorkingDate, lastWorkingDates: dates, reason }, null, { toast: false })
      if (!response.ok) { setError(response.error || 'Unable to mark these employees as resigned.'); return }
      changeOpen(false); await onSaved()
    } catch {
      setError('Unable to save the resignation. Please try again.')
    } finally { setBusy(false) }
  }
  return <>{!hideTrigger && <Button danger size={size} disabled={disabled || !clientId || !employees.length} onClick={() => { onOpen?.(); changeOpen(true) }}>{label ?? `Mark as resigned${employees.length ? ` (${employees.length})` : ''}`}</Button>}
    <Modal title={<span className="employee-resignation-title"><LogoutOutlined />{single ? 'Resign employee' : 'Resign selected employees'}</span>} className="employee-resignation-modal" bodyStyle={{ maxHeight: '65dvh', overflowY: 'auto', minWidth: 0 }} open={open} width={single ? 560 : 760} onCancel={() => { if (!busy) changeOpen(false) }} onOk={() => void save()} confirmLoading={busy} okText="Confirm resignation" okButtonProps={{ danger: true, disabled: disabled || loadingDates || !ready || reason.trim().length < 3 }} cancelButtonProps={{ disabled: busy }} closable={!busy} maskClosable={!busy} keyboard={!busy}>
      <div className="employee-resignation-summary">
        <div><strong>{single ? employees[0].name || employees[0].employeeCode : `${employees.length} employees selected`}</strong><span>{single ? employees[0].employeeCode : 'One confirmation for the selected employees'}</span></div>
        <Tag color="red">Employment exit</Tag>
      </div>
      {error && <Alert type="error" showIcon message={error} />}
      <Form layout="vertical" className="employee-resignation-form"><Form.Item label={single ? 'Last working date' : 'Last working date for all'} required={single} extra={loadingDates ? 'Loading the latest payroll date...' : single && months[employees[0].id] ? `Suggested from ${months[employees[0].id]} payroll. Edit if needed.` : single ? 'Enter the employee\'s final working date.' : 'Set one date here, or edit individual dates below.'}><Input aria-label="Last working date" type="date" value={lastWorkingDate} disabled={busy || loadingDates} onChange={event => { const date = event.target.value; setDate(date); setDates(Object.fromEntries(employees.map(row => [row.id, date]))) }} /></Form.Item>
      {employees.length > 1 && <DataTable rows={employees} getRowId={row => row.id} exportDisabled hideSearch loading={loadingDates} title="Review employee exit dates" columns={[
        { key: 'employeeCode', label: 'Code', render: row => <Tag>{row.employeeCode}</Tag> }, { key: 'name', label: 'Employee' },
        { key: 'month', label: 'Last payroll month', value: row => months[row.id] || 'No matched payroll history' },
        { key: 'date', label: 'Last working date', sortable: false, filterable: false, render: row => <Input aria-label={`Last working date ${row.employeeCode}`} type="date" value={dates[row.id] || ''} disabled={busy || loadingDates} onChange={event => { setDate(''); setDates(current => ({ ...current, [row.id]: event.target.value })) }} /> },
      ]} />}
      <Form.Item label="Reason" required><Input.TextArea aria-label="Resignation reason" value={reason} maxLength={500} autoSize={{ minRows: 2, maxRows: 3 }} disabled={busy} onChange={event => setReason(event.target.value)} /></Form.Item></Form>
      <div className="employee-resignation-impact" aria-label="Changes after the last working date"><span className="employee-resignation-impact-label">After the last working date</span><div><span><LockOutlined /> ESS access stops</span><span><CalendarOutlined /> Active payroll &amp; attendance lists stop</span></div></div>
      <p className="employee-resignation-history"><SafetyCertificateOutlined /><span>Past attendance, payslips and audit history stay available. Payroll or payable attendance after the exit date must be reviewed first.</span></p>
    </Modal></>
}
