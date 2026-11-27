import { useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Modal, Select, Space } from 'antd'
import type { Employee, Structure } from '../types/payroll'
import { employeeSalaryTemplates } from '../utils/employeeSalaryTemplate'

export type SalaryAssignmentRequest = { employeeIds: number[]; templateId: string; removeTestPayroll: boolean }
export type SalaryAssignmentReview = {
  ready: number
  alreadyAssigned: number
  blocked: { id: number; employee: string; reason: string }[]
  testPayslips?: { runId: number; payPeriod: string; paid: boolean; legacy: boolean; employee: string }[]
  apply: () => Promise<void>
}
type Props = {
  clientId: number; clientName: string; employees: Employee[]; templates: Structure[]; loading: boolean; filtered: boolean
  canRemoveTestPayroll: boolean
  onCancel: () => void; onAssign: (request: SalaryAssignmentRequest) => Promise<SalaryAssignmentReview | undefined>
}

export default function EmployeeBulkSalaryAssignmentModal({ clientId, clientName, employees, templates, loading, filtered, canRemoveTestPayroll, onCancel, onAssign }: Props) {
  const [templateId, setTemplateId] = useState<string>()
  const [selection, setSelection] = useState<number[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [review, setReview] = useState<SalaryAssignmentReview>()
  const [removeTestPayroll, setRemoveTestPayroll] = useState(false)
  const removals = review?.testPayslips ?? []
  const paid = removals.filter(row => row.paid).length
  const submitting = useRef(false)
  const templateOptions = useMemo(() => employeeSalaryTemplates(templates, clientId)
    .map(template => ({ value: String(template.id), label: template.name })), [templates, clientId])
  const employeeOptions = useMemo(() => employees
    .filter(employee => employee.clientId === clientId && employee.isActive)
    .map(employee => ({ value: employee.id, label: `${employee.employeeCode} · ${employee.firstName} ${employee.lastName}`.trim() })), [employees, clientId])
  const selected = useMemo(() => {
    const allowed = selection === null ? null : new Set(selection)
    return employeeOptions.map(option => option.value).filter(id => allowed === null || allowed.has(id))
  }, [employeeOptions, selection])
  const template = templateOptions.find(option => option.value === templateId)
  const canAssign = !loading && !!template && selected.length > 0 && (!review || review.ready > 0)
  const submit = async () => {
    if (submitting.current || !template || !canAssign) return
    submitting.current = true
    setBusy(true)
    setError('')
    try {
      if (review) await review.apply()
      else setReview(await onAssign({ employeeIds: selected, templateId: template.value, removeTestPayroll: canRemoveTestPayroll && removeTestPayroll }))
    } catch (failure) {
      setReview(undefined)
      setError(failure instanceof Error ? failure.message : 'Could not assign this salary template.')
    } finally {
      submitting.current = false
      setBusy(false)
    }
  }
  return <Modal open title={`Assign salary template · ${clientName}`} width={580} onCancel={onCancel}
    closable={!busy} maskClosable={!busy} keyboard={!busy} footer={<Space wrap>
      <Button onClick={onCancel} disabled={busy}>Cancel</Button>
      {review && <Button onClick={() => { setReview(undefined); setError('') }} disabled={busy}>Edit selection</Button>}
      <Button type="primary" danger={!!review && removals.length > 0} loading={busy} disabled={!canAssign}
        onClick={() => void submit()}>{review ? removals.length ? `Assign and delete ${removals.length} test payslips` : `Assign eligible (${review.ready})`
          : removeTestPayroll ? 'Review assignment and deletion' : `Assign template (${selected.length})`}</Button>
    </Space>}>
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <div><label htmlFor="bulk-assignment-template">Salary template</label>
        <Select id="bulk-assignment-template" aria-label="Salary template to assign" showSearch optionFilterProp="label"
          style={{ width: '100%' }} value={templateId} onChange={setTemplateId} placeholder="Choose salary template"
          options={templateOptions} loading={loading} disabled={loading || busy || !!review}
          notFoundContent="No active salary template for this client" />
        <small>New payroll calculations use this template for any pay month, including earlier months.</small></div>
      <div><label htmlFor="bulk-assignment-employees">Employees</label>
        <Select id="bulk-assignment-employees" aria-label="Employees to assign" mode="multiple" showSearch optionFilterProp="label"
          style={{ width: '100%' }} options={employeeOptions} value={selected} onChange={setSelection} loading={loading}
          disabled={loading || busy || !!review} placeholder="Choose employees" maxTagCount={0}
          maxTagPlaceholder={hidden => `${hidden.length} selected`} notFoundContent="No matching active employees" />
        <Space size="middle" wrap>
          <Checkbox checked={employeeOptions.length > 0 && selected.length === employeeOptions.length}
            indeterminate={selected.length > 0 && selected.length < employeeOptions.length}
            disabled={loading || busy || !!review || !employeeOptions.length}
            onChange={event => setSelection(event.target.checked ? null : [])}>{filtered ? 'Select all matching active employees' : 'Select all active employees'}</Checkbox>
          <Button size="small" type="link" disabled={loading || busy || !!review || !selected.length} onClick={() => setSelection([])}>Clear selection</Button>
        </Space></div>
      {!loading && (!templateOptions.length || !employeeOptions.length) && <Alert showIcon type="info" message={!employeeOptions.length
        ? 'There are no active employees for this client.' : 'Create an active salary template for this client to assign it.'} />}
      {canRemoveTestPayroll && <Checkbox checked={removeTestPayroll} disabled={loading || busy || !!review} onChange={e => setRemoveTestPayroll(e.target.checked)}>
        Remove old test payroll (including paid)
      </Checkbox>}
      {removeTestPayroll && !review && <small>Review all approved and paid regular payslips across all months for the selected employees, including this template’s saved results. A new payslip is generated the next time you run payroll.</small>}
      {!!removals.length && <Alert type="warning" showIcon message={`${paid} paid and ${removals.length - paid} approved unpaid test payslips will be deleted`}
        description={<details><summary>Review employees, months and runs</summary>
          <ul style={{ maxHeight: 160, overflowY: 'auto', paddingInlineStart: 18 }}>
            {removals.map(row => <li key={row.runId + ':' + row.employee}>{row.employee} · {row.payPeriod} · Run #{row.runId} · {row.paid ? 'Paid' : 'Approved unpaid'}{row.legacy ? ' · No template recorded on payslip' : ''}</li>)}
          </ul></details>} />}
      {error && <Alert showIcon type="error" message={error} />}
      {review && <div role="status" data-testid="salary-assignment-exceptions">
        <Alert showIcon type={review.blocked.length ? 'warning' : 'success'} message={`${review.ready} ready · ${review.alreadyAssigned} already assigned · ${review.blocked.length} blocked`}
          description={review.blocked.length > 0 && <ul style={{ margin: 0, paddingInlineStart: 18, maxHeight: 190, overflowY: 'auto' }}>
            {review.blocked.map(row => <li key={row.id}><strong>{row.employee}</strong> — {row.reason}</li>)}
          </ul>} />
      </div>}
    </Space>
  </Modal>
}
