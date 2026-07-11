import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, Drawer, Form, Input, InputNumber, Select, Space, Switch, Tabs, Tag } from 'antd'
import DataTable from './DataTable'
import { getScheduledJobActions, getScheduledJobHandlers, getScheduledJobRuns, getScheduledJobs, runScheduledJobNow, saveScheduledJob, saveScheduledJobAction, setScheduledJobEnabled } from '../services/settingsService'
import type { ScheduledJob, ScheduledJobAction, ScheduledJobHandlerOption, ScheduledJobRun } from '../types/payroll'

const configuredActionKey = 'CONFIGURED_JOB_ACTION'
const job0: ScheduledJob = { id: 0, jobCode: '', jobName: '', description: '', handlerKey: configuredActionKey, isEnabled: true, scheduleType: 'Interval', intervalMinutes: 60, dailyRunTime: '01:00', monthlyRunDay: 1, configJson: '{}', lastStatus: 'Never Run', lastMessage: '', isRunning: false }
const action0: ScheduledJobAction = { id: 0, actionCode: '', actionName: '', actionType: 'Notification Event', description: '', configJson: '{}', isActive: true }
const scheduleOptions = ['Interval', 'Daily', 'Monthly'] as const
const statusColor = (status: string) => status === 'Completed' ? 'green' : status === 'Failed' ? 'red' : status === 'Running' ? 'blue' : 'default'
const dt = (value?: string | null) => value ? new Date(value).toLocaleString('en-IN') : '-'
const parseJson = <T,>(json: string, fallback: T): T => { try { return { ...fallback, ...JSON.parse(json || '{}') } } catch { return fallback } }
const stringify = (value: object) => JSON.stringify(value, null, 2)
type NotificationConfig = { eventCode: string; resourceType: string; resourceId: string; clientId?: number | null; payloadJson: string }
type ProcedureConfig = { procedureName: string; parameters: Record<string, string> }
type ApiConfig = { method: string; url: string; bodyJson: string; headers: Record<string, string>; timeoutSeconds: number }
type ReportEmailConfig = { reportCode: string; eventCode: string; filter: { clientId: number; month: string; payRunId?: number | null; employeeId?: number | null; fromDate?: string; toDate?: string }; previewRows: number }
type WorkflowTriggerConfig = { workflowId: number; resourceType: string; resourceIds: string; requestorUserId: number; skipIfPending: boolean; payloadJson: string }
const notification0: NotificationConfig = { eventCode: '', resourceType: 'ScheduledJob', resourceId: '', clientId: null, payloadJson: '{}' }
const procedure0: ProcedureConfig = { procedureName: '', parameters: {} }
const api0: ApiConfig = { method: 'POST', url: '', bodyJson: '{}', headers: {}, timeoutSeconds: 60 }
const reportEmail0: ReportEmailConfig = { reportCode: '', eventCode: '', filter: { clientId: 0, month: new Date().toISOString().slice(0, 7), payRunId: null, employeeId: null }, previewRows: 10 }
const workflowTrigger0: WorkflowTriggerConfig = { workflowId: 0, resourceType: '', resourceIds: '', requestorUserId: 1, skipIfPending: true, payloadJson: '{}' }
const actionTypes: ScheduledJobAction['actionType'][] = ['Notification Event', 'Internal API Call', 'Stored Procedure', 'Report Email', 'Workflow Trigger']

export default function ScheduledJobsManager() {
  const [jobs, setJobs] = useState<ScheduledJob[]>([])
  const [actions, setActions] = useState<ScheduledJobAction[]>([])
  const [handlers, setHandlers] = useState<ScheduledJobHandlerOption[]>([])
  const [runs, setRuns] = useState<ScheduledJobRun[]>([])
  const [selectedJobId, setSelectedJobId] = useState<number | undefined>()
  const [job, setJob] = useState<ScheduledJob>(job0)
  const [action, setAction] = useState<ScheduledJobAction>(action0)
  const [jobOpen, setJobOpen] = useState(false)
  const [actionOpen, setActionOpen] = useState(false)
  const [saving, setSaving] = useState(false)

  const load = async (focusJobId = selectedJobId) => {
    const [jobRows, actionRows, handlerRows] = await Promise.all([getScheduledJobs(), getScheduledJobActions(), getScheduledJobHandlers()])
    setJobs(jobRows)
    setActions(actionRows)
    setHandlers(handlerRows)
    const nextJobId = focusJobId || jobRows[0]?.id
    setSelectedJobId(nextJobId)
    setRuns(nextJobId ? await getScheduledJobRuns(nextJobId) : [])
  }

  useEffect(() => { void load() }, [])

  const selectedJob = useMemo(() => jobs.find(item => item.id === selectedJobId), [jobs, selectedJobId])
  const selectedActionId = parseJson(job.configJson, { actionId: 0 }).actionId
  const selectedAction = actions.find(item => item.id === selectedActionId)
  const notificationConfig = parseJson(action.configJson, notification0)
  const procedureConfig = parseJson(action.configJson, procedure0)
  const apiConfig = parseJson(action.configJson, api0)
  const reportEmailConfig = parseJson(action.configJson, reportEmail0)
  const workflowTriggerConfig = parseJson(action.configJson, workflowTrigger0)
  const handlerName = (key: string) => handlers.find(item => item.handlerKey === key)?.name || key
  const scheduleLabel = (row: ScheduledJob) => row.scheduleType === 'Interval' ? `Every ${row.intervalMinutes} min` : row.scheduleType === 'Monthly' ? `Day ${row.monthlyRunDay} at ${row.dailyRunTime}` : `Daily at ${row.dailyRunTime}`
  const actionNameForJob = (row: ScheduledJob) => {
    if (row.handlerKey !== configuredActionKey) return handlerName(row.handlerKey)
    const actionId = parseJson(row.configJson, { actionId: 0 }).actionId
    return actions.find(item => item.id === actionId)?.actionName || 'Configured action'
  }

  const openJob = (row?: ScheduledJob) => { setJob(row ? { ...row } : job0); setJobOpen(true) }
  const openAction = (row?: ScheduledJobAction) => { setAction(row ? { ...row } : { ...action0, configJson: stringify(notification0) }); setActionOpen(true) }
  const selectJob = async (id: number) => { setSelectedJobId(id); setRuns(await getScheduledJobRuns(id)) }
  const saveJob = async () => { setSaving(true); const response = await saveScheduledJob(job); setSaving(false); if (response.ok) { setJobOpen(false); void load(response.data?.id || selectedJobId) } }
  const saveAction = async () => { setSaving(true); const response = await saveScheduledJobAction(action); setSaving(false); if (response.ok) { setActionOpen(false); void load(selectedJobId) } }
  const toggle = async (row: ScheduledJob) => { const response = await setScheduledJobEnabled(row.id, !row.isEnabled); if (response.ok) void load(row.id) }
  const runNow = async (row: ScheduledJob) => { const response = await runScheduledJobNow(row.id); if (response.ok) void load(row.id) }
  const setJobAction = (actionId: number) => setJob(current => ({ ...current, handlerKey: configuredActionKey, configJson: stringify({ actionId }) }))
  const setNotificationConfig = (changes: Partial<NotificationConfig>) => setAction(current => ({ ...current, configJson: stringify({ ...parseJson(current.configJson, notification0), ...changes }) }))
  const setProcedureConfig = (changes: Partial<ProcedureConfig>) => setAction(current => ({ ...current, configJson: stringify({ ...parseJson(current.configJson, procedure0), ...changes }) }))
  const setApiConfig = (changes: Partial<ApiConfig>) => setAction(current => ({ ...current, configJson: stringify({ ...parseJson(current.configJson, api0), ...changes }) }))
  const setReportEmailConfig = (changes: Partial<ReportEmailConfig>) => setAction(current => ({ ...current, configJson: stringify({ ...parseJson(current.configJson, reportEmail0), ...changes }) }))
  const setWorkflowTriggerConfig = (changes: Partial<WorkflowTriggerConfig>) => setAction(current => ({ ...current, configJson: stringify({ ...parseJson(current.configJson, workflowTrigger0), ...changes }) }))
  const configForType = (type: ScheduledJobAction['actionType']) => stringify(type === 'Stored Procedure' ? procedure0 : type === 'Internal API Call' ? api0 : type === 'Report Email' ? reportEmail0 : type === 'Workflow Trigger' ? workflowTrigger0 : notification0)

  return <section className="scheduled-jobs-settings">
    <Card title="Scheduled Jobs" size="small" className="settings-panel settings-table-panel">
      <Tabs items={[
        { key: 'actions', label: 'Job Actions', children: <>
          <div className="component-table-head"><div><b>Reusable job actions</b><span>Create what the scheduler can do. Schedules will reuse these actions.</span></div><Space className="settings-master-actions" size={8} wrap><Button onClick={() => void load()}>Refresh</Button><Button type="primary" onClick={() => openAction()}>Add action</Button></Space></div>
          <DataTable rows={actions} getRowId={row => row.id} exportFileName="scheduled-job-actions" columns={[
            { key: 'actionName', label: 'Action', render: row => <>{row.actionName}<small>{row.actionCode}</small></>, width: '240px' },
            { key: 'actionType', label: 'Type' },
            { key: 'description', label: 'Description', width: '320px' },
            { key: 'isActive', label: 'Status', render: row => row.isActive ? 'Active' : 'Inactive' }
          ]} actions={row => <Button size="small" type="primary" onClick={() => openAction(row)}>Edit</Button>} />
        </> },
        { key: 'jobs', label: 'Schedules', children: <>
          <div className="component-table-head"><div><b>Schedule control</b><span>Decide when a built-in process or configured action should run.</span></div><Space className="settings-master-actions" size={8} wrap><Button onClick={() => void load()}>Refresh</Button><Button type="primary" onClick={() => openJob()}>Add schedule</Button></Space></div>
          <DataTable rows={jobs} getRowId={row => row.id} exportFileName="scheduled-jobs" columns={[
            { key: 'jobName', label: 'Schedule', render: row => <>{row.jobName}<small>{row.jobCode}</small></>, width: '220px' },
            { key: 'handlerKey', label: 'Job Action', render: row => <>{actionNameForJob(row)}<small>{row.handlerKey === configuredActionKey ? 'Configured action' : row.handlerKey}</small></>, width: '240px' },
            { key: 'scheduleType', label: 'When', value: scheduleLabel },
            { key: 'lastStatus', label: 'Last status', render: row => <Tag color={statusColor(row.lastStatus)}>{row.lastStatus}</Tag> },
            { key: 'lastRunAt', label: 'Last run', value: row => dt(row.lastRunAt) },
            { key: 'nextRunAt', label: 'Next run', value: row => dt(row.nextRunAt) },
            { key: 'isEnabled', label: 'Enabled', render: row => row.isEnabled ? 'Yes' : 'No' }
          ]} actions={row => <Space size={6} wrap><Button size="small" onClick={() => void selectJob(row.id)}>Logs</Button><Button size="small" type="primary" disabled={row.isRunning} onClick={() => void runNow(row)}>Run now</Button><Button size="small" onClick={() => openJob(row)}>Edit</Button><Button size="small" danger={row.isEnabled} onClick={() => void toggle(row)}>{row.isEnabled ? 'Pause' : 'Enable'}</Button></Space>} />
        </> },
        { key: 'logs', label: 'Run Logs', children: <DataTable rows={runs} getRowId={row => row.id} exportFileName="scheduled-job-runs" title={selectedJob ? `Run logs - ${selectedJob.jobName}` : 'Run logs'} columns={[
          { key: 'startedAt', label: 'Started', value: row => dt(row.startedAt) },
          { key: 'completedAt', label: 'Completed', value: row => dt(row.completedAt) },
          { key: 'status', label: 'Status', render: row => <Tag color={statusColor(row.status)}>{row.status}</Tag> },
          { key: 'successCount', label: 'Success' },
          { key: 'failureCount', label: 'Failed' },
          { key: 'message', label: 'Message', width: '320px' },
          { key: 'triggeredBy', label: 'Triggered by' },
          { key: 'durationMs', label: 'Duration ms' }
        ]} /> }
      ]} />
    </Card>

    <Drawer className="settings-master-drawer scheduled-job-drawer" title={<div className="settings-drawer-title"><span>Job action</span><h3>{action.id ? 'Edit job action' : 'Add job action'}</h3><p>Create reusable work that can be scheduled later.</p></div>} open={actionOpen} width={760} onClose={() => setActionOpen(false)} destroyOnClose footer={<Space><Button onClick={() => setActionOpen(false)}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void saveAction()}>{action.id ? 'Update action' : 'Save action'}</Button></Space>}>
      <Form component="div" layout="vertical" className="settings-quick-form scheduled-job-form" requiredMark={false}>
        <Alert className="scheduled-job-help" type="info" showIcon message="Create action first, schedule later" description="Notification Event is fully configurable from UI. Stored Procedure is for approved database procedures whose name starts with job_." />
        <div className="scheduled-form-section">
          <h4>Basic details</h4>
          <div className="scheduled-form-grid two"><Form.Item label="Action code" required><Input value={action.actionCode} onChange={event => setAction({ ...action, actionCode: event.target.value.toUpperCase().replace(/\s+/g, '_') })} placeholder="MONTHLY_LEAVE_REMINDER" /></Form.Item><Form.Item label="Action name" required><Input value={action.actionName} onChange={event => setAction({ ...action, actionName: event.target.value })} placeholder="Monthly leave reminder" /></Form.Item></div>
          <Form.Item label="Description"><Input.TextArea rows={2} value={action.description} onChange={event => setAction({ ...action, description: event.target.value })} /></Form.Item>
          <div className="scheduled-form-grid two"><Form.Item label="Action type"><Select value={action.actionType} onChange={value => setAction({ ...action, actionType: value, configJson: configForType(value) })} options={actionTypes.map(value => ({ value, label: value }))} /></Form.Item><Form.Item label="Active"><Switch checked={action.isActive} onChange={value => setAction({ ...action, isActive: value })} /></Form.Item></div>
        </div>
        {action.actionType === 'Notification Event' && <div className="scheduled-form-section">
          <h4>Notification event</h4>
          <div className="scheduled-form-grid two"><Form.Item label="Event code" required><Input value={notificationConfig.eventCode} onChange={event => setNotificationConfig({ eventCode: event.target.value.toUpperCase().replace(/\s+/g, '_') })} placeholder="MONTHLY_LEAVE_REMINDER" /></Form.Item><Form.Item label="Record type"><Input value={notificationConfig.resourceType} onChange={event => setNotificationConfig({ resourceType: event.target.value })} placeholder="ScheduledJob" /></Form.Item><Form.Item label="Record reference"><Input value={notificationConfig.resourceId} onChange={event => setNotificationConfig({ resourceId: event.target.value })} placeholder="Optional" /></Form.Item><Form.Item label="Client ID"><InputNumber min={1} value={notificationConfig.clientId ?? undefined} onChange={value => setNotificationConfig({ clientId: value ? Number(value) : null })} placeholder="Optional" /></Form.Item></div>
          <Form.Item label="Payload JSON"><Input.TextArea rows={3} value={notificationConfig.payloadJson} onChange={event => setNotificationConfig({ payloadJson: event.target.value || '{}' })} /></Form.Item>
        </div>}
        {action.actionType === 'Stored Procedure' && <div className="scheduled-form-section">
          <h4>Stored procedure</h4>
          <Form.Item label="Procedure name" required><Input value={procedureConfig.procedureName} onChange={event => setProcedureConfig({ procedureName: event.target.value })} placeholder="job_month_end_reconcile" /></Form.Item>
          <Form.Item label="Parameters JSON"><Input.TextArea rows={4} value={stringify(procedureConfig.parameters)} onChange={event => { try { setProcedureConfig({ parameters: JSON.parse(event.target.value || '{}') }) } catch { setProcedureConfig({ parameters: procedureConfig.parameters }) } }} /></Form.Item>
          <small className="scheduled-config-hint">For safety, only stored procedures starting with job_ can run.</small>
        </div>}
        {action.actionType === 'Internal API Call' && <div className="scheduled-form-section">
          <h4>Internal API call</h4>
          <div className="scheduled-form-grid three"><Form.Item label="Method"><Select value={apiConfig.method} onChange={value => setApiConfig({ method: value })} options={['GET', 'POST', 'PUT', 'DELETE'].map(value => ({ value, label: value }))} /></Form.Item><Form.Item label="API URL" required><Input value={apiConfig.url} onChange={event => setApiConfig({ url: event.target.value })} placeholder="/api/..." /></Form.Item><Form.Item label="Timeout seconds"><InputNumber min={5} max={600} value={apiConfig.timeoutSeconds} onChange={value => setApiConfig({ timeoutSeconds: Number(value || 60) })} /></Form.Item></div>
          <Form.Item label="Body JSON"><Input.TextArea rows={4} value={apiConfig.bodyJson} onChange={event => setApiConfig({ bodyJson: event.target.value || '{}' })} /></Form.Item>
          <Form.Item label="Headers JSON"><Input.TextArea rows={3} value={stringify(apiConfig.headers)} onChange={event => { try { setApiConfig({ headers: JSON.parse(event.target.value || '{}') }) } catch { setApiConfig({ headers: apiConfig.headers }) } }} /></Form.Item>
        </div>}
        {action.actionType === 'Report Email' && <div className="scheduled-form-section">
          <h4>Report email</h4>
          <div className="scheduled-form-grid three"><Form.Item label="Report code" required><Input value={reportEmailConfig.reportCode} onChange={event => setReportEmailConfig({ reportCode: event.target.value })} placeholder="pf-report" /></Form.Item><Form.Item label="Notification event" required><Input value={reportEmailConfig.eventCode} onChange={event => setReportEmailConfig({ eventCode: event.target.value.toUpperCase().replace(/\s+/g, '_') })} placeholder="REPORT_READY" /></Form.Item><Form.Item label="Preview rows"><InputNumber min={1} max={50} value={reportEmailConfig.previewRows} onChange={value => setReportEmailConfig({ previewRows: Number(value || 10) })} /></Form.Item></div>
          <div className="scheduled-form-grid three"><Form.Item label="Client ID"><InputNumber min={0} value={reportEmailConfig.filter.clientId} onChange={value => setReportEmailConfig({ filter: { ...reportEmailConfig.filter, clientId: Number(value || 0) } })} /></Form.Item><Form.Item label="Month"><Input value={reportEmailConfig.filter.month} onChange={event => setReportEmailConfig({ filter: { ...reportEmailConfig.filter, month: event.target.value } })} placeholder="2026-07" /></Form.Item><Form.Item label="Pay run ID"><InputNumber min={1} value={reportEmailConfig.filter.payRunId ?? undefined} onChange={value => setReportEmailConfig({ filter: { ...reportEmailConfig.filter, payRunId: value ? Number(value) : null } })} /></Form.Item></div>
        </div>}
        {action.actionType === 'Workflow Trigger' && <div className="scheduled-form-section">
          <h4>Workflow trigger</h4>
          <div className="scheduled-form-grid three"><Form.Item label="Workflow ID" required><InputNumber min={1} value={workflowTriggerConfig.workflowId || undefined} onChange={value => setWorkflowTriggerConfig({ workflowId: Number(value || 0) })} /></Form.Item><Form.Item label="Resource type" required><Input value={workflowTriggerConfig.resourceType} onChange={event => setWorkflowTriggerConfig({ resourceType: event.target.value })} placeholder="PayRun" /></Form.Item><Form.Item label="Requestor user ID"><InputNumber min={1} value={workflowTriggerConfig.requestorUserId} onChange={value => setWorkflowTriggerConfig({ requestorUserId: Number(value || 1) })} /></Form.Item></div>
          <Form.Item label="Resource IDs"><Input value={workflowTriggerConfig.resourceIds} onChange={event => setWorkflowTriggerConfig({ resourceIds: event.target.value })} placeholder="26,27,28" /></Form.Item>
          <Form.Item label="Payload JSON"><Input.TextArea rows={3} value={workflowTriggerConfig.payloadJson} onChange={event => setWorkflowTriggerConfig({ payloadJson: event.target.value || '{}' })} /></Form.Item>
          <Form.Item label="Skip if pending"><Switch checked={workflowTriggerConfig.skipIfPending} onChange={value => setWorkflowTriggerConfig({ skipIfPending: value })} /></Form.Item>
        </div>}
      </Form>
    </Drawer>

    <Drawer className="settings-master-drawer scheduled-job-drawer" title={<div className="settings-drawer-title"><span>Schedule</span><h3>{job.id ? 'Edit schedule' : 'Add schedule'}</h3><p>Select a job action and decide when it should run.</p></div>} open={jobOpen} width={760} onClose={() => setJobOpen(false)} destroyOnClose footer={<Space><Button onClick={() => setJobOpen(false)}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void saveJob()}>{job.id ? 'Update schedule' : 'Save schedule'}</Button></Space>}>
      <Form component="div" layout="vertical" className="settings-quick-form scheduled-job-form" requiredMark={false}>
        <div className="scheduled-form-section"><h4>Basic details</h4><div className="scheduled-form-grid two"><Form.Item label="Schedule code" required><Input value={job.jobCode} onChange={event => setJob({ ...job, jobCode: event.target.value.toUpperCase().replace(/\s+/g, '_') })} placeholder="DAILY_LEAVE_REMINDER" /></Form.Item><Form.Item label="Schedule name" required><Input value={job.jobName} onChange={event => setJob({ ...job, jobName: event.target.value })} /></Form.Item></div><Form.Item label="Description"><Input.TextArea rows={2} value={job.description} onChange={event => setJob({ ...job, description: event.target.value })} /></Form.Item></div>
        <div className="scheduled-form-section"><h4>What should run?</h4><div className="scheduled-form-grid action"><Form.Item label="Job Action" required><Select showSearch value={selectedActionId || undefined} placeholder="Select configured action" optionFilterProp="searchText" onChange={setJobAction} options={actions.filter(item => item.isActive).map(item => ({ value: item.id, label: `${item.actionName} - ${item.actionType}`, searchText: `${item.actionName} ${item.actionCode} ${item.actionType}` }))} /></Form.Item><Form.Item label="Enabled"><Switch checked={job.isEnabled} onChange={value => setJob({ ...job, isEnabled: value })} /></Form.Item></div>{selectedAction && <div className="scheduled-action-note"><b>{selectedAction.actionName}</b><span>{selectedAction.description || selectedAction.actionType}</span><code>{selectedAction.actionCode}</code></div>}</div>
        <div className="scheduled-form-section"><h4>When should it run?</h4><div className="scheduled-form-grid three"><Form.Item label="Run frequency"><Select value={job.scheduleType} onChange={value => setJob({ ...job, scheduleType: value })} options={scheduleOptions.map(value => ({ value, label: value === 'Interval' ? 'Repeated interval' : value }))} /></Form.Item>{job.scheduleType === 'Interval' && <Form.Item label="Repeat every"><InputNumber addonAfter="minutes" min={1} max={1440} value={job.intervalMinutes} onChange={value => setJob({ ...job, intervalMinutes: Number(value || 1) })} /></Form.Item>}{job.scheduleType !== 'Interval' && <Form.Item label="Run time"><Input value={job.dailyRunTime} onChange={event => setJob({ ...job, dailyRunTime: event.target.value })} placeholder="01:00" /></Form.Item>}{job.scheduleType === 'Monthly' && <Form.Item label="Day of month"><InputNumber min={1} max={31} value={job.monthlyRunDay} onChange={value => setJob({ ...job, monthlyRunDay: Number(value || 1) })} /></Form.Item>}</div></div>
      </Form>
    </Drawer>
  </section>
}
