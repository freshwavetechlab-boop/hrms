import { useRef, useState } from 'react'
import { Alert, Button, Form, Input, Modal, Tag } from 'antd'
import { postJson } from '../services/apiClient'
import DataTable from './DataTable'
import './EmployeeResignation.css'

export type ResignationEmployee = { id: number; employeeCode: string; name: string }
export default function EmployeeResignationAction({ clientId, employees, disabled = false, onSaved }: { clientId: number; employees: ResignationEmployee[]; disabled?: boolean; onSaved: () => void | Promise<void> }) {
  const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [lastWorkingDate, setDate] = useState(''), [reason, setReason] = useState('Employment ended; reviewed client roster.')
  const [dates, setDates] = useState<Record<number, string>>({}), [months, setMonths] = useState<Record<number, string | null>>({}), [loadingDates, setLoadingDates] = useState(false)
  const generation = useRef(0)
  const begin = async () => {
    const version = ++generation.current
    setError(''); setDate(''); setDates({}); setMonths({}); setOpen(true); setLoadingDates(true)
    try {
      const response = await postJson('/api/employees/resignation-defaults', { clientId, employeeIds: employees.map(row => row.id) }, [] as { employeeId: number; lastPayrollMonth: string | null; lastWorkingDate: string | null }[], { toast: false, loader: false })
      if (version !== generation.current) return
      if (!response.ok) { setError(response.error || 'Enter reviewed last working dates; payroll defaults could not be loaded.'); return }
      const values = Object.fromEntries(response.data.map(row => [row.employeeId, row.lastWorkingDate || '']))
      setDates(values); setMonths(Object.fromEntries(response.data.map(row => [row.employeeId, row.lastPayrollMonth])))
      const common = [...new Set(employees.map(row => values[row.id] || ''))]
      setDate(common.length === 1 ? common[0] : '')
    } finally { if (version === generation.current) setLoadingDates(false) }
  }
  const ready = employees.every(row => dates[row.id])
  const save = async () => {
    if (busy || loadingDates || !ready || reason.trim().length < 3) return
    setBusy(true); setError('')
    try {
      const response = await postJson('/api/employees/mark-resigned', { clientId, employeeIds: employees.map(row => row.id), lastWorkingDate, lastWorkingDates: dates, reason }, null, { toast: false })
      if (!response.ok) { setError(response.error || 'Unable to mark these employees as resigned.'); return }
      setOpen(false); await onSaved()
    } finally { setBusy(false) }
  }
  return <><Button danger disabled={disabled || !clientId || !employees.length} onClick={() => void begin()}>Mark as resigned{employees.length ? ` (${employees.length})` : ''}</Button>
    <Modal title="Mark selected employees as resigned" className="employee-resignation-modal" bodyStyle={{ maxHeight: '65dvh', overflowY: 'auto', minWidth: 0 }} open={open} width={760} onCancel={() => { if (!busy) { generation.current++; setOpen(false); setLoadingDates(false) } }} onOk={() => void save()} confirmLoading={busy} okText="Mark as resigned" okButtonProps={{ danger: true, disabled: loadingDates || !ready || reason.trim().length < 3 }} cancelButtonProps={{ disabled: busy }}>
      <Alert type="info" showIcon message="Employment ends after the last working date" description="Future attendance, payroll and payslip selection will stop. Previously saved payslips, reports and audit history remain available. Existing payroll in the exit period must be reviewed first." />
      {error && <Alert type="error" showIcon message={error} />}
      <p>Default dates are the last calendar day of each employee's latest saved payroll month, including Excel payslips. Review and edit before confirming; no date is assumed when history is unavailable.</p>
      <Form layout="vertical"><Form.Item label={employees.length === 1 ? 'Last working date' : 'Apply one last working date to all (optional)'}><Input aria-label="Last working date" type="date" value={lastWorkingDate} disabled={busy || loadingDates} onChange={event => { const date = event.target.value; setDate(date); setDates(Object.fromEntries(employees.map(row => [row.id, date]))) }} /></Form.Item>
      {employees.length > 1 && <DataTable rows={employees} getRowId={row => row.id} exportDisabled hideSearch loading={loadingDates} title="Review employee exit dates" columns={[
        { key: 'employeeCode', label: 'Code', render: row => <Tag>{row.employeeCode}</Tag> }, { key: 'name', label: 'Employee' },
        { key: 'month', label: 'Last payroll month', value: row => months[row.id] || 'No matched payroll history' },
        { key: 'date', label: 'Last working date', sortable: false, filterable: false, render: row => <Input aria-label={`Last working date ${row.employeeCode}`} type="date" value={dates[row.id] || ''} disabled={busy || loadingDates} onChange={event => { setDate(''); setDates(current => ({ ...current, [row.id]: event.target.value })) }} /> },
      ]} />}
      <Form.Item label="Reason" required><Input.TextArea aria-label="Resignation reason" value={reason} maxLength={500} disabled={busy} onChange={event => setReason(event.target.value)} /></Form.Item></Form>
    </Modal></>
}
