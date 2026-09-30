import { useEffect, useMemo, useState } from 'react'
import DOMPurify from 'dompurify'
import { Alert, Button, Checkbox, Drawer, Input, Modal, Select, Space, Spin, Switch, Tag, Tooltip } from 'antd'
import { MailOutlined, SettingOutlined } from '@ant-design/icons'
import type { TableRowSelection } from 'antd/es/table/interface'
import type { Client, Employee } from '../types/payroll'
import type { CommunicationTemplate, EmployeeCommunicationPreview } from '../types/employeeCommunication'
import { useAuthSession } from './AuthGate'
import { useToast } from './ToastProvider'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { postJson } from '../services/apiClient'
import { getEmployeeCompletion, previewEmployeeCompletion, saveEmployeeCompletionPolicy, sendEmployeeCompletion, setupEmployeeCompletion, type CompletionMail, type CompletionSetup } from '../services/employeeProfileCompletionService'
import CommunicationRichEditor from './CommunicationRichEditor'

type Batch = { request: CompletionMail; preview: EmployeeCommunicationPreview; queued: boolean }
export function useEmployeeProfileCompletion(rows: Employee[], clients: Client[], clientId: number, revision: number) {
  const session = useAuthSession(), toast = useToast()
  const canSend = !!session?.user.permissions.includes('employee.communication.send')
  const canManage = !!session?.user.permissions.includes('employees.manage')
  const canEditTemplate = !!session?.user.permissions.includes('settings.manage')
  const [statuses, setStatuses] = useState<Awaited<ReturnType<typeof getEmployeeCompletion>>['data']>([])
  const [loading, setLoading] = useState(true)
  const [statusError, setStatusError] = useState('')
  const [refresh, setRefresh] = useState(0)
  const [savedSelection, setSelection] = useSessionPreference<number[]>('employees.ui:' + session?.user.id + ':' + clientId + ':reminder-selection', [])
  const [manageOpen, setManageOpen] = useState(false)
  const [manageClient, setManageClient] = useState(clientId)
  const [setup, setSetup] = useState<CompletionSetup | null>(null)
  const [manageLoading, setManageLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [batches, setBatches] = useState<Batch[] | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [sending, setSending] = useState(false)
  useEffect(() => {
    let active = true
    setLoading(true); setStatusError('')
    void getEmployeeCompletion(clientId).then(result => {
      if (!active) return
      setStatuses(result.ok ? result.data : []); setStatusError(result.ok ? '' : result.error || 'Missing-information status could not be checked.'); setLoading(false)
    })
    return () => { active = false }
  }, [clientId, revision, refresh, session?.user.id, rows])
  useEffect(() => {
    if (!manageOpen || !manageClient) { setSetup(null); setManageLoading(false); return }
    let active = true
    setSetup(null); setManageLoading(true)
    void setupEmployeeCompletion(manageClient).then(result => {
      if (!active) return
      setManageLoading(false)
      if (result.ok && result.data) setSetup(result.data)
      else toast(result.error || 'Unable to load profile-completion settings.', 'error')
    })
    return () => { active = false }
  }, [manageOpen, manageClient])
  const byId = useMemo(() => new Map(statuses.map(row => [row.employeeId, row])), [statuses])
  const eligible = (row: Employee) => !loading && !!byId.get(row.id)?.canSendEmail
  const selected = rows.filter(row => savedSelection.includes(row.id) && eligible(row))
  const selectedIds = selected.map(row => row.id)
  const disabledReason = (row: Employee) => loading ? 'Checking missing information...' : statusError || byId.get(row.id)?.disabledReason || 'Status is unavailable.'
  const preview = async (employees: Employee[]) => {
    setPreviewing(true)
    const result: Batch[] = []
    try {
      for (const scope of [...new Set(employees.map(row => row.clientId))]) {
        const request = { clientId: scope, employeeIds: employees.filter(row => row.clientId === scope).map(row => row.id), idempotencyKey: crypto.randomUUID() }
        const response = await previewEmployeeCompletion(request)
        if (!response.ok || !response.data) { toast(response.error || 'Unable to prepare email preview.', 'error'); return }
        result.push({ request, preview: response.data, queued: false })
      }
      setBatches(result)
    } finally { setPreviewing(false) }
  }
  const send = async () => {
    if (!batches) return
    setSending(true)
    try {
      for (const batch of batches.filter(row => !row.queued)) {
        const result = await sendEmployeeCompletion(batch.request)
        if (!result.ok) { toast(result.error || 'Unable to queue reminders. You can retry safely.', 'error'); return }
        batch.queued = true
        setBatches([...batches])
      }
      toast('Missing-information emails queued. Delivery is tracked in Employee Communication.', 'success')
      setBatches(null); setSelection([]); setRefresh(value => value + 1)
    } finally { setSending(false) }
  }
  const savePolicy = async () => {
    if (!setup) return
    setSaving(true)
    try {
      const response = await saveEmployeeCompletionPolicy(manageClient, setup.firstEditEnabled)
      if (response.ok) toast('First-time profile edit setting saved.', 'success')
    } finally { setSaving(false) }
  }
  const saveTemplate = async () => {
    if (!setup) return
    setSaving(true)
    try {
      const result = await postJson<CommunicationTemplate, CommunicationTemplate | null>('/api/communication-settings/templates', setup.template, null)
      if (result.ok && result.data) { setSetup({ ...setup, template: result.data }); toast('Mail template saved.', 'success') }
    } finally { setSaving(false) }
  }
  const rowSelection: TableRowSelection<Employee> | undefined = canSend ? { selectedRowKeys: selectedIds, onChange: keys => setSelection(keys.map(Number)), getCheckboxProps: row => ({ disabled: !eligible(row), title: eligible(row) ? 'Select employee for reminder' : disabledReason(row) }) } : undefined
  return {
    rowSelection,
    selection: canSend ? (row: Employee) => <Checkbox checked={selectedIds.includes(row.id)} disabled={!eligible(row)} onChange={event => setSelection(event.target.checked ? [...new Set([...selectedIds, row.id])] : selectedIds.filter(id => id !== row.id))}>Select</Checkbox> : undefined,
    mailAction: (row: Employee, compact = false) => canSend && <Tooltip title={eligible(row) ? 'Preview the saved template with this employee\'s details' : disabledReason(row)}><span><Button className="employee-reminder-button" aria-label="Send link to fill missing details" size="small" icon={<MailOutlined />} disabled={!eligible(row) || previewing} onClick={() => void preview([row])}>{compact ? 'Send ESS link' : 'Send link to fill missing details'}</Button></span></Tooltip>,
    bulkAction: canSend && selected.length > 0 && <Button icon={<MailOutlined />} loading={previewing} onClick={() => void preview(selected)}>Send missing-info link ({selected.length})</Button>,
    manageButton: canManage && <Button icon={<SettingOutlined />} onClick={() => { setManageClient(clientId || session?.user.clientId || 0); setManageOpen(true) }}>Manage</Button>,
    statusNotice: statusError && <Alert type="warning" showIcon message={statusError} action={<Button size="small" onClick={() => setRefresh(value => value + 1)}>Retry</Button>} />,
    dialogs: <>
      <Drawer title="Manage employee profile completion" open={manageOpen} width="min(720px, 96vw)" onClose={() => !saving && setManageOpen(false)}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {!session?.user.clientId && <Select aria-label="Profile completion client" placeholder="Select client" style={{ width: '100%' }} value={manageClient || undefined} options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={setManageClient} />}
          {manageLoading && <Spin />}
          {setup && <>
            <Space><Switch aria-label="Allow first profile edit" checked={setup.firstEditEnabled} disabled={saving} onChange={value => setSetup({ ...setup, firstEditEnabled: value })} /><strong>Allow first profile save without approval</strong></Space>
            <p>Default is ON. After the first successful save, editing locks. Each later approval allows one more save. This switch only controls employees who have never saved their profile.</p>
            <Button type="primary" loading={saving} onClick={() => void savePolicy()}>Save edit setting</Button>
            <Alert type="info" showIcon message="Edit requests appear in My Tasks and the notification bell of the assigned approver." description="Uses the client's profile-edit approval workflow. If none exists, an authorised employee administrator for the client is selected, with a global administrator as fallback." />
            <h3>Mail template: {setup.template.name}</h3><Tag color={setup.template.isActive ? 'green' : 'red'}>{setup.template.isActive ? 'Active' : 'Disabled'}</Tag>
            <p>Saved in Mail templates for this client. Placeholders are filled with real employee data when you preview or send.</p>
            <p>ESS link: <a href={setup.template.variables?.find(variable => variable.variableKey === 'essUrl')?.fallbackValue} target="_blank" rel="noreferrer">{setup.template.variables?.find(variable => variable.variableKey === 'essUrl')?.fallbackValue}</a></p>
            {canEditTemplate ? <>
              <Input aria-label="Reminder email subject" value={setup.template.subjectTemplate} onChange={event => setSetup({ ...setup, template: { ...setup.template, subjectTemplate: event.target.value } })} />
              <CommunicationRichEditor compact value={setup.template.bodyTemplate} onChange={value => setSetup(current => current ? { ...current, template: { ...current.template, bodyTemplate: value } } : current)} />
              <Button loading={saving} onClick={() => void saveTemplate()}>Save mail template</Button>
            </> : <><strong>{setup.template.subjectTemplate}</strong><div dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(setup.template.bodyTemplate) }} /></>}
          </>}
        </Space>
      </Drawer>
      <Modal title="Send missing-information reminders" open={batches !== null} width="min(800px, 96vw)" onCancel={() => !sending && setBatches(null)} onOk={() => void send()} confirmLoading={sending} okText="Send emails" okButtonProps={{ disabled: !batches?.length || batches.some(batch => !batch.preview.canSend) }}>
        {batches?.map(batch => <section key={batch.request.clientId} className="employee-reminder-preview">
          <h3>{clients.find(client => client.id === batch.request.clientId)?.name}</h3><Tag>{batch.preview.eligibleCount} recipients{batch.queued ? ' - queued' : ''}</Tag>
          {batch.preview.warnings.map(warning => <Alert key={warning} type="warning" message={warning} />)}
          <details><summary>Recipients</summary>{batch.preview.recipients.map(row => <p key={row.employeeId}>{row.employeeName} ({row.employeeCode}) - {row.destination}{row.exclusionReason && ' - ' + row.exclusionReason}</p>)}</details>
          <h4>{batch.preview.sampleSubject}</h4><div dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(batch.preview.sampleBody) }} />
        </section>)}
      </Modal>
    </>,
  }
}
