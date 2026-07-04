import { useEffect, useState } from 'react'
import { Modal } from 'antd'
import { getJson, postJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'

type Stage = { id: number; stageOrder: number; name: string; approverType: string; approverUserId?: number | null }
type Flow = { id: number; clientId?: number | null; code: string; name: string; resourceType: string; isActive: boolean; stages: Stage[] }
type Activity = { id: number; activityCode: string; displayName: string; moduleCode: string; resourceType: string; description: string; isActive: boolean }
type Approver = { id: number; displayName: string; clientId?: number | null; clientName: string }
type ActionRule = { id: number; activityCode: string; httpMethod: string; pathPattern: string; resourceType: string; resourceIdSource: string; clientIdSource: string; clientIdSql: string; clientLookupTable: string; clientLookupKeyColumn: string; clientLookupClientColumn: string; workflowId?: number | null; triggerMode: string; isActive: boolean }
const newWorkflow = (): Flow => ({ id: 0, code: '', name: '', resourceType: '', isActive: true, stages: [{ id: 0, stageOrder: 1, name: 'Manager approval', approverType: 'Reporting Manager' }] })
const newActivity = (): Activity => ({ id: 0, activityCode: '', displayName: '', moduleCode: '', resourceType: '', description: '', isActive: true })
const newActionRule = (): ActionRule => ({ id: 0, activityCode: '', httpMethod: 'POST', pathPattern: '', resourceType: '', resourceIdSource: 'route.id', clientIdSource: '', clientIdSql: '', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '', workflowId: null, triggerMode: 'AfterSuccess', isActive: true })
const sourceLocations = [{ value: 'route', label: 'URL value' }, { value: 'body', label: 'Form/request value' }, { value: 'query', label: 'Query string value' }, { value: 'response', label: 'API response value' }]
const splitSource = (source: string) => {
  const [location = 'route', ...fieldParts] = source.split('.')
  return { location, field: fieldParts.join('.') || 'id' }
}
const joinSource = (location: string, field: string) => `${location || 'route'}.${field || 'id'}`

export default function WorkflowAdmin() {
  const [section, setSection] = useState<'activities' | 'designer' | 'rules'>('designer')
  const [rows, setRows] = useState<Flow[]>([])
  const [flow, setFlow] = useState<Flow>(newWorkflow)
  const [clients, setClients] = useState<Client[]>([])
  const [activities, setActivities] = useState<Activity[]>([])
  const [activityRows, setActivityRows] = useState<Activity[]>([])
  const [activity, setActivity] = useState<Activity>(newActivity)
  const [approvers, setApprovers] = useState<Approver[]>([])
  const [rules, setRules] = useState<ActionRule[]>([])
  const [rule, setRule] = useState<ActionRule>(newActionRule)
  const [ruleDialogOpen, setRuleDialogOpen] = useState(false)
  const [message, setMessage] = useState('')
  const load = () => getJson<Flow[]>('/api/workflows', []).then(setRows)
  const loadRules = () => getJson<ActionRule[]>('/api/workflows/action-rules', []).then(setRules)
  const loadActivities = () => getJson<Activity[]>('/api/workflows/activities/catalog', []).then(items => { setActivityRows(items); setActivities(items.filter(item => item.isActive)) })

  useEffect(() => {
    void load()
    void loadRules()
    void getClients().then(setClients)
    void loadActivities()
    void getJson<Approver[]>('/api/workflows/approvers', []).then(setApprovers)
  }, [])

  const updateStage = (index: number, changes: Partial<Stage>) => setFlow(current => ({ ...current, stages: current.stages.map((stage, position) => position === index ? { ...stage, ...changes } : stage) }))
  const visibleApprovers = approvers.filter(user => !flow.clientId || !user.clientId || user.clientId === flow.clientId)
  const selectedActivity = activities.find(activity => activity.activityCode === flow.code)
  const selectedRuleActivity = activities.find(activity => activity.activityCode === rule.activityCode)
  const recordSource = splitSource(rule.resourceIdSource)
  const clientSource = splitSource(rule.clientIdSource)
  const clientMode = rule.clientIdSource ? 'request' : rule.clientLookupTable ? 'lookup' : 'login'
  const selectActivity = (activityCode: string) => {
    const activity = activities.find(item => item.activityCode === activityCode)
    if (!activity) return
    setFlow(current => ({
      ...current,
      code: activity.activityCode,
      resourceType: activity.resourceType,
      name: current.name && current.code === activity.activityCode ? current.name : `${activity.displayName} workflow`
    }))
  }
  const selectRuleActivity = (activityCode: string) => {
    const activity = activities.find(item => item.activityCode === activityCode)
    setRule(current => ({ ...current, activityCode, resourceType: activity?.resourceType ?? current.resourceType }))
  }
  const edit = (row: Flow) => { setFlow({ ...row, stages: row.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }); setSection('designer'); setMessage(`Editing ${row.name}.`) }
  const cancel = () => { setFlow(newWorkflow()); setMessage('') }
  const editActivity = (row: Activity) => { setActivity({ ...row }); setSection('activities'); setMessage(`Editing activity ${row.displayName}.`) }
  const cancelActivity = () => { setActivity(newActivity()); setMessage('') }
  const editRule = (row: ActionRule) => { setRule({ ...row }); setSection('rules'); setRuleDialogOpen(true); setMessage(`Editing start rule for ${row.activityCode}.`) }
  const openRule = (seed?: Partial<ActionRule>) => { setRule({ ...newActionRule(), ...seed }); setSection('rules'); setRuleDialogOpen(true); setMessage('') }
  const cancelRule = () => { setRule(newActionRule()); setRuleDialogOpen(false); setMessage('') }
  const setClientMode = (mode: string) => setRule(current => mode === 'request'
    ? { ...current, clientIdSource: current.clientIdSource || 'body.clientId', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '', clientIdSql: '' }
    : mode === 'lookup'
      ? { ...current, clientIdSource: '', clientIdSql: '', clientLookupTable: current.clientLookupTable || 'payruns', clientLookupKeyColumn: current.clientLookupKeyColumn || 'Id', clientLookupClientColumn: current.clientLookupClientColumn || 'ClientId' }
      : { ...current, clientIdSource: '', clientIdSql: '', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '' })

  const save = async () => {
    if (flow.stages.some(stage => stage.approverType === 'Specific User' && !stage.approverUserId)) {
      setMessage('Select an assigned user for every Specific User stage.')
      return
    }
    const response = await postJson('/api/workflows', { ...flow, stages: flow.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }, null)
    if (!response.ok) {
      setMessage('Unable to save workflow. Check the details and try again.')
      return
    }
    setMessage(flow.id ? 'Workflow updated.' : 'Workflow created.')
    setFlow(newWorkflow())
    load()
  }

  const saveActivity = async () => {
    if (!activity.activityCode || !activity.displayName || !activity.moduleCode || !activity.resourceType) {
      setMessage('Activity code, activity name, module, and record type are required.')
      return
    }
    const response = await postJson('/api/workflows/activities', { ...activity, activityCode: activity.activityCode.trim().toUpperCase() }, null, { successMessage: 'Workflow activity saved.' })
    if (!response.ok) {
      setMessage(response.error || 'Unable to save workflow activity.')
      return
    }
    setMessage('Workflow activity saved.')
    setActivity(newActivity())
    loadActivities()
  }

  const saveRule = async () => {
    if (!rule.activityCode || !rule.httpMethod || !rule.pathPattern || !rule.resourceType || !rule.resourceIdSource) {
      setMessage('Activity, method, path, resource type, and resource id source are required.')
      return
    }
    if (!rule.resourceIdSource.includes('.')) {
      setMessage('Select where the record number is available and enter the field name.')
      return
    }
    if (clientMode === 'lookup' && (!rule.clientLookupTable || !rule.clientLookupKeyColumn || !rule.clientLookupClientColumn)) {
      setMessage('For client lookup, enter table name, matching column, and client column.')
      return
    }
    const response = await postJson('/api/workflows/action-rules', { ...rule, clientIdSql: '', httpMethod: rule.httpMethod.toUpperCase(), workflowId: rule.workflowId || null }, null, { successMessage: 'Workflow trigger mapping saved.' })
    if (!response.ok) {
      setMessage(response.error || 'Unable to save workflow trigger mapping.')
      return
    }
    setMessage('Workflow trigger mapping saved.')
    setRule(newActionRule())
    setRuleDialogOpen(false)
    loadRules()
  }

  return (
    <section className="card workflow-admin">
      <header><div><h3>Workflow Setup</h3><p>Maintain activities, approval design, and start rules separately.</p></div></header>
      {message && <p className="form-warning">{message}</p>}
      <div className="page-tabs">
        <button type="button" className={section === 'activities' ? 'active' : ''} onClick={() => setSection('activities')}>Activity Master</button>
        <button type="button" className={section === 'designer' ? 'active' : ''} onClick={() => setSection('designer')}>Workflow Designer</button>
        <button type="button" className={section === 'rules' ? 'active' : ''} onClick={() => setSection('rules')}>Start Rules</button>
      </div>
      {section === 'activities' && <section className="approval-stages">
        <div className="approval-stages-heading"><div><h3>Activity master</h3><p>Create business actions that can be routed through workflow.</p></div><span>{activityRows.length} activities</span></div>
        <div className="grid">
          <label><span>Activity name</span><input value={activity.displayName} onChange={event => setActivity({ ...activity, displayName: event.target.value })} placeholder="Submit payroll for approval" /></label>
          <label><span>Activity code</span><input value={activity.activityCode} onChange={event => setActivity({ ...activity, activityCode: event.target.value.toUpperCase() })} placeholder="PAYRUN.SUBMIT" /><small>Use a stable code like MODULE.ACTION.</small></label>
          <label><span>Module</span><input value={activity.moduleCode} onChange={event => setActivity({ ...activity, moduleCode: event.target.value })} placeholder="Payroll" /></label>
          <label><span>Record type</span><input value={activity.resourceType} onChange={event => setActivity({ ...activity, resourceType: event.target.value })} placeholder="PayRun" /><small>This becomes the workflow resource type.</small></label>
          <label><span>Description</span><input value={activity.description} onChange={event => setActivity({ ...activity, description: event.target.value })} placeholder="Lock a draft payroll run and route it for approval." /></label>
          <label><span>Status</span><SearchSelect value={activity.isActive ? 'active' : 'inactive'} onChange={value => setActivity({ ...activity, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
        </div>
        <div className="actions">{activity.id > 0 && <button type="button" className="secondary" onClick={cancelActivity}>Cancel</button>}<button type="button" onClick={() => void saveActivity()} disabled={!activity.activityCode || !activity.displayName || !activity.moduleCode || !activity.resourceType}>{activity.id ? 'Update activity' : 'Save activity'}</button></div>
        <DataTable rows={activityRows} emptyText="No workflow activities configured yet." exportFileName="workflow-activities" columns={[
          { key: 'displayName', label: 'Activity', render: row => <>{row.displayName}<small>{row.activityCode}</small></> },
          { key: 'moduleCode', label: 'Module' },
          { key: 'resourceType', label: 'Record type' },
          { key: 'description', label: 'Purpose' },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <button type="button" className="secondary" onClick={() => editActivity(row)}>Edit</button>} />
      </section>}
      {section === 'designer' && <>
        <div className="grid">
          <label><span>Applies to</span><SearchSelect value={flow.clientId ?? ''} onChange={value => setFlow({ ...flow, clientId: value ? Number(value) : null })} options={[{ value: '', label: 'All clients (global default)' }, ...clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name }))]} /><small>Client workflow takes priority over the global default.</small></label>
          <label><span>Activity</span><SearchSelect value={flow.code} onChange={selectActivity} options={[{ value: '', label: 'Select workflow activity' }, ...activities.map(activity => ({ value: activity.activityCode, label: `${activity.displayName} - ${activity.moduleCode}` }))]} /><small>{selectedActivity?.description || 'Activities come from Activity Master.'}</small></label>
          <label><span>Workflow code</span><input value={flow.code} readOnly placeholder="Select an activity" /></label>
          <label><span>Workflow name</span><input value={flow.name} onChange={event => setFlow({ ...flow, name: event.target.value })} placeholder="Leave request approval" /></label>
          <label><span>Resource type</span><input value={flow.resourceType} readOnly placeholder="Selected activity resource" /></label>
          <label><span>Status</span><SearchSelect value={flow.isActive ? 'active' : 'inactive'} onChange={value => setFlow({ ...flow, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
        </div>
        <section className="approval-stages"><div className="approval-stages-heading"><div><h3>Approval stages</h3><p>Requests move through these stages in the order shown.</p></div><span>{flow.stages.length} {flow.stages.length === 1 ? 'stage' : 'stages'}</span></div><div className="stage-list">
          {flow.stages.map((stage, index) => <article className="workflow-stage" key={stage.id || index}><div className="stage-number"><b>{index + 1}</b><span>{index === 0 ? 'Starts here' : 'Then'}</span></div><label><span>Stage name</span><input value={stage.name} onChange={event => updateStage(index, { name: event.target.value })} placeholder={`Approval ${index + 1}`} /></label><label><span>Approver type</span><SearchSelect value={stage.approverType} onChange={value => updateStage(index, { approverType: value, approverUserId: value === 'Specific User' ? stage.approverUserId : null })} options={['Reporting Manager', 'HR Manager', 'Specific User', 'Department Head'].map(value => ({ value, label: value }))} /></label>{stage.approverType === 'Specific User' && <label className="specific-user"><span>Assigned user</span><SearchSelect value={stage.approverUserId ?? ''} onChange={value => updateStage(index, { approverUserId: value ? Number(value) : null })} options={[{ value: '', label: 'Select a user' }, ...visibleApprovers.map(user => ({ value: user.id, label: `${user.displayName} - ${user.clientName}` }))]} /></label>}<button type="button" className="stage-remove" onClick={() => setFlow({ ...flow, stages: flow.stages.filter((_, position) => position !== index) })} disabled={flow.stages.length === 1}>Remove</button></article>)}
        </div><button type="button" className="add-stage" onClick={() => setFlow({ ...flow, stages: [...flow.stages, { id: 0, stageOrder: flow.stages.length + 1, name: `Approval ${flow.stages.length + 1}`, approverType: 'HR Manager' }] })}>+ Add approval stage</button></section>
        <div className="actions">{flow.id > 0 && <button type="button" className="secondary" onClick={cancel}>Cancel</button>}<button type="button" onClick={() => void save()} disabled={!flow.code || !flow.name || !flow.resourceType}>{flow.id ? 'Update workflow' : 'Save workflow'}</button></div>
        <DataTable rows={rows} emptyText="No workflows have been configured yet." exportFileName="workflows" columns={[
          { key: 'workflow', label: 'Workflow', value: row => row.name, render: row => <>{row.name}<small>{row.code}</small></> },
          { key: 'clientName', label: 'Applies to', value: row => row.clientId ? clients.find(client => client.id === row.clientId)?.name ?? `Client #${row.clientId}` : 'All clients' },
          { key: 'resourceType', label: 'Resource' },
          { key: 'stagesText', label: 'Stages', value: row => row.stages.map(stage => stage.name).join(' -> ') },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <div className="ant-table-row-actions"><button type="button" className="secondary" onClick={() => edit(row)}>Edit</button><button type="button" className="secondary" onClick={() => openRule({ activityCode: row.code, resourceType: row.resourceType, workflowId: row.id })}>Start rule</button></div>} />
      </>}
      {section === 'rules' && <section className="approval-stages">
        <div className="approval-stages-heading"><div><h3>Start rules</h3><p>Choose when a saved workflow should begin. Add or edit rules in a dialog.</p></div><button type="button" className="add-stage" onClick={() => openRule()}>+ Add start rule</button></div>
        <DataTable rows={rules} emptyText="No workflow start rules have been configured yet." exportFileName="workflow-start-rules" columns={[
          { key: 'activityCode', label: 'Action' },
          { key: 'methodPath', label: 'Request', value: row => `${row.httpMethod} ${row.pathPattern}` },
          { key: 'resource', label: 'Record', value: row => `${row.resourceType} from ${row.resourceIdSource}` },
          { key: 'client', label: 'Client', value: row => row.clientLookupTable ? `${row.clientLookupTable}.${row.clientLookupClientColumn}` : row.clientIdSource || 'Logged-in user' },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <button type="button" className="secondary" onClick={() => editRule(row)}>Edit</button>} />
      </section>}
      <Modal title={rule.id ? 'Edit workflow start rule' : 'Add workflow start rule'} open={ruleDialogOpen} onCancel={cancelRule} footer={null} width={980}>
        <div className="grid">
          <label><span>Screen action</span><SearchSelect value={rule.activityCode} onChange={selectRuleActivity} options={[{ value: '', label: 'Select action' }, ...activities.map(activity => ({ value: activity.activityCode, label: `${activity.displayName} - ${activity.moduleCode}` }))]} /><small>{selectedRuleActivity?.description || 'This is the action that needs approval.'}</small></label>
          <label><span>Request type</span><SearchSelect value={rule.httpMethod} onChange={value => setRule({ ...rule, httpMethod: value })} options={['POST', 'PUT', 'PATCH', 'DELETE'].map(value => ({ value, label: value }))} /></label>
          <label><span>Request path</span><input value={rule.pathPattern} onChange={event => setRule({ ...rule, pathPattern: event.target.value })} placeholder="/api/pay-runs/{id}/submit" /><small>Use braces for changing values, for example {'{id}'}.</small></label>
          <label><span>Record type</span><input value={rule.resourceType} onChange={event => setRule({ ...rule, resourceType: event.target.value })} placeholder="PayRun" /></label>
          <label><span>Record number comes from</span><SearchSelect value={recordSource.location} onChange={value => setRule({ ...rule, resourceIdSource: joinSource(value, recordSource.field) })} options={sourceLocations} /></label>
          <label><span>Record number field</span><input value={recordSource.field} onChange={event => setRule({ ...rule, resourceIdSource: joinSource(recordSource.location, event.target.value) })} placeholder="id" /></label>
          <label><span>Find client using</span><SearchSelect value={clientMode} onChange={setClientMode} options={[{ value: 'lookup', label: 'Lookup from table' }, { value: 'request', label: 'Value in request' }, { value: 'login', label: 'Logged-in user client' }]} /></label>
          {clientMode === 'request' && <><label><span>Client value comes from</span><SearchSelect value={clientSource.location} onChange={value => setRule({ ...rule, clientIdSource: joinSource(value, clientSource.field) })} options={sourceLocations} /></label><label><span>Client field</span><input value={clientSource.field} onChange={event => setRule({ ...rule, clientIdSource: joinSource(clientSource.location, event.target.value) })} placeholder="clientId" /></label></>}
          {clientMode === 'lookup' && <><label><span>Table name</span><input value={rule.clientLookupTable} onChange={event => setRule({ ...rule, clientLookupTable: event.target.value })} placeholder="payruns" /></label><label><span>Match this column</span><input value={rule.clientLookupKeyColumn} onChange={event => setRule({ ...rule, clientLookupKeyColumn: event.target.value })} placeholder="Id" /><small>This column stores the record number.</small></label><label><span>Client column</span><input value={rule.clientLookupClientColumn} onChange={event => setRule({ ...rule, clientLookupClientColumn: event.target.value })} placeholder="ClientId" /></label></>}
          <label><span>Workflow</span><SearchSelect value={rule.workflowId ?? ''} onChange={value => setRule({ ...rule, workflowId: value ? Number(value) : null })} options={[{ value: '', label: 'Auto select by action/client' }, ...rows.filter(item => item.isActive).map(item => ({ value: item.id, label: `${item.name} - ${item.code}` }))]} /></label>
          <label><span>Status</span><SearchSelect value={rule.isActive ? 'active' : 'inactive'} onChange={value => setRule({ ...rule, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
        </div>
        <div className="actions">{rule.id > 0 && <button type="button" className="secondary" onClick={cancelRule}>Cancel</button>}<button type="button" onClick={() => void saveRule()} disabled={!rule.activityCode || !rule.pathPattern || !rule.resourceType || !rule.resourceIdSource}>{rule.id ? 'Update rule' : 'Save rule'}</button></div>
      </Modal>
    </section>
  )
}
