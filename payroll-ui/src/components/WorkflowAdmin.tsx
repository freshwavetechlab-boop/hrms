import { useEffect, useState } from 'react'
import { Alert, Collapse, Modal, Tag } from 'antd'
import { getJson, postJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'
import { apiCatalog, type ApiCatalogRow } from '../data/apiCatalog'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'

type Stage = { id: number; stageOrder: number; name: string; approverType: string; approverUserId?: number | null }
type Flow = { id: number; clientId?: number | null; code: string; name: string; resourceType: string; isActive: boolean; stages: Stage[] }
type Activity = { id: number; activityCode: string; displayName: string; moduleCode: string; resourceType: string; description: string; isActive: boolean }
type Approver = { id: number; displayName: string; email?: string; clientId?: number | null; clientName: string }
type ApproverPreview = { approverType: string; resolutionStatus: string; isDynamic: boolean; approver?: Approver | null; message: string }
type ActionRule = { id: number; activityCode: string; httpMethod: string; pathPattern: string; resourceType: string; resourceIdSource: string; clientIdSource: string; clientIdSql: string; clientLookupTable: string; clientLookupKeyColumn: string; clientLookupClientColumn: string; workflowId?: number | null; triggerMode: string; isActive: boolean }
const newWorkflow = (): Flow => ({ id: 0, code: '', name: '', resourceType: '', isActive: true, stages: [{ id: 0, stageOrder: 1, name: 'Manager approval', approverType: 'Reporting Manager' }] })
const newActivity = (): Activity => ({ id: 0, activityCode: '', displayName: '', moduleCode: '', resourceType: '', description: '', isActive: true })
const newActionRule = (): ActionRule => ({ id: 0, activityCode: '', httpMethod: 'POST', pathPattern: '', resourceType: '', resourceIdSource: 'route.id', clientIdSource: '', clientIdSql: '', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '', workflowId: null, triggerMode: 'AfterSuccess', isActive: true })
const sourceLocations = [{ value: 'route', label: 'URL value' }, { value: 'body', label: 'Form/request value' }, { value: 'query', label: 'Query string value' }, { value: 'response', label: 'API response value' }]
const coreModules = ['Payroll', 'Leave & Attendance', 'Travel & Expense', 'Employees', 'Recruitment', 'Tax', 'Security', 'Settings', 'Client Billing', 'Workflow']
const resourcePresets: Record<string, Array<{ value: string; label: string }>> = {
  Payroll: [{ value: 'PayRun', label: 'Payroll run' }, { value: 'PayrollAdjustment', label: 'Payroll adjustment' }],
  'Leave & Attendance': [{ value: 'LeaveRequest', label: 'Leave request' }, { value: 'AttendanceRegularization', label: 'Attendance regularization' }],
  'Travel & Expense': [{ value: 'TravelRequest', label: 'Travel request' }, { value: 'ExpenseClaim', label: 'Expense claim' }],
  Employees: [{ value: 'EmployeeAction', label: 'Employee action' }, { value: 'SalaryRevision', label: 'Salary revision' }],
  Recruitment: [{ value: 'RecruitmentPipelineTransition', label: 'Pipeline stage movement' }, { value: 'RecruitmentPipelineStageAction', label: 'Pipeline stage action' }, { value: 'RecruitmentRequisition', label: 'Hiring requisition (RFR)' }, { value: 'RecruitmentJobDescription', label: 'Job description approval' }, { value: 'RecruitmentOffer', label: 'Candidate offer approval' }],
  Tax: [{ value: 'TaxProof', label: 'Employee tax proof' }],
}
const safeGenericEndpointKeys = new Set(['POST /api/pay-runs/{id}/submit'])
const safeEndpointActivity: Record<string, string> = { 'POST /api/pay-runs/{id}/submit': 'PAYRUN.SUBMIT' }
const workflowEndpoints = apiCatalog.filter(row => safeGenericEndpointKeys.has(`${row.method.toUpperCase()} ${row.path}`))
const directWorkflowResources = new Set(['RecruitmentRequisition', 'RecruitmentJobDescription', 'RecruitmentOffer', 'RecruitmentPipelineTransition', 'RecruitmentPipelineStageAction'])
const directConfigurationLocation: Record<string, string> = {
  RecruitmentRequisition: 'Recruitment Administration > Approvals > RFR approval',
  RecruitmentJobDescription: 'Job Description submission > Approval workflow',
  RecruitmentOffer: 'Pipeline Designer > Offer stage approval, or Recruitment Administration > Approvals > Offer approval',
  RecruitmentPipelineTransition: 'Pipeline Designer > Transition approval workflow',
  RecruitmentPipelineStageAction: 'Pipeline Designer > Stage actions > Start workflow',
}
const approverLabel = (type: string) => type === 'Specific User' ? 'Specific user approval' : `${type} approval`
const codeToken = (value: string) => value.trim().toUpperCase().replace(/&/g, ' AND ').replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '')
const activityCodeFor = (moduleCode: string, displayName: string) => [codeToken(moduleCode), codeToken(displayName)].filter(Boolean).join('.')
const pascal = (value: string) => value.replace(/[^A-Za-z0-9]+/g, ' ').trim().split(/\s+/).filter(Boolean).map(part => `${part[0]?.toUpperCase() || ''}${part.slice(1)}`).join('')
const humanize = (value: string) => value.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ')
const inferredResourceType = (moduleCode: string, displayName: string) => {
  const value = `${moduleCode} ${displayName}`.toLowerCase()
  if (value.includes('recruit')) {
    if (value.includes('job description') || value.includes(' jd ')) return 'RecruitmentJobDescription'
    if (value.includes('offer')) return 'RecruitmentOffer'
    if (value.includes('requisition') || value.includes(' rfr')) return 'RecruitmentRequisition'
    if (value.includes('stage action') || value.includes('start workflow')) return 'RecruitmentPipelineStageAction'
    if (value.includes('transition') || value.includes('movement') || value.includes('shortlist') || value.includes('interview') || value.includes('discussion') || value.includes('pre-onboard')) return 'RecruitmentPipelineTransition'
    return ''
  }
  if (value.includes('payroll adjustment')) return 'PayrollAdjustment'
  if (value.includes('payroll') || value.includes('pay run')) return 'PayRun'
  if (value.includes('leave')) return 'LeaveRequest'
  if (value.includes('travel')) return 'TravelRequest'
  if (value.includes('expense')) return 'ExpenseClaim'
  if (value.includes('salary revision')) return 'SalaryRevision'
  if (value.includes('employee')) return 'EmployeeAction'
  if (value.includes('tax')) return 'TaxProof'
  return pascal(displayName) || pascal(moduleCode) || 'BusinessRecord'
}
const endpointKey = (row: Pick<ApiCatalogRow, 'method' | 'path'>) => `${row.method.toUpperCase()} ${row.path}`
const pathParameter = (path: string) => path.match(/\{([^}:]+)(?::[^}]+)?\}/)?.[1] || ''
const lookupPreset = (path: string) => {
  if (path.startsWith('/api/pay-runs')) return { table: 'payruns', key: 'Id', client: 'ClientId' }
  if (path.startsWith('/api/ess/leave/requests')) return { table: 'essleaverequests', key: 'Id', client: 'ClientId' }
  if (path.startsWith('/api/ess/travel/requests')) return { table: 'ess_travel_requests', key: 'Id', client: 'ClientId' }
  if (path.startsWith('/api/ess/expenses/claims')) return { table: 'ess_expense_claims', key: 'Id', client: 'ClientId' }
  if (path.startsWith('/api/employees')) return { table: 'employees', key: 'Id', client: 'ClientId' }
  return null
}
const recommendedRuleFor = (current: ActionRule, endpoint: ApiCatalogRow, activity?: Activity): ActionRule => {
  const parameter = pathParameter(endpoint.path)
  const lookup = lookupPreset(endpoint.path)
  const hasClientInput = /(?:Query:.*clientId|clientId)/i.test(endpoint.notes || '')
  return {
    ...current,
    activityCode: activity?.activityCode || current.activityCode,
    httpMethod: endpoint.method.toUpperCase(),
    pathPattern: endpoint.path,
    resourceType: activity?.resourceType || current.resourceType,
    resourceIdSource: parameter ? `route.${parameter}` : 'response.id',
    clientIdSource: lookup ? '' : hasClientInput ? (endpoint.method === 'GET' ? 'query.clientId' : 'body.clientId') : '',
    clientIdSql: '',
    clientLookupTable: lookup?.table || '',
    clientLookupKeyColumn: lookup?.key || '',
    clientLookupClientColumn: lookup?.client || '',
    triggerMode: 'AfterSuccess',
  }
}
const splitSource = (source: string) => {
  const [location = 'route', ...fieldParts] = source.split('.')
  return { location, field: fieldParts.join('.') || 'id' }
}
const joinSource = (location: string, field: string) => `${location || 'route'}.${field || 'id'}`

export default function WorkflowAdmin() {
  const [section, setSection] = useState<'activities' | 'designer' | 'rules'>('designer')
  const [rows, setRows] = useState<Flow[]>([])
  const [flow, setFlow] = useState<Flow>(newWorkflow)
  const [selectedFlowActivityCode, setSelectedFlowActivityCode] = useState('')
  const [clients, setClients] = useState<Client[]>([])
  const [activities, setActivities] = useState<Activity[]>([])
  const [activityRows, setActivityRows] = useState<Activity[]>([])
  const [activity, setActivity] = useState<Activity>(newActivity)
  const [activityCodeAuto, setActivityCodeAuto] = useState(true)
  const [activityResourceAuto, setActivityResourceAuto] = useState(true)
  const [approvers, setApprovers] = useState<Approver[]>([])
  const [approverPreviews, setApproverPreviews] = useState<Record<number, ApproverPreview>>({})
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

  const approverPreviewSignature = flow.stages.map(stage => `${stage.approverType}:${stage.approverUserId || 0}`).join('|')
  useEffect(() => {
    if (section !== 'designer') return
    let cancelled = false
    setApproverPreviews({})
    void Promise.all(flow.stages.map(async (stage, index) => {
      const query = new URLSearchParams({ approverType: stage.approverType })
      if (flow.clientId) query.set('clientId', String(flow.clientId))
      if (stage.approverUserId) query.set('approverUserId', String(stage.approverUserId))
      const preview = await getJson<ApproverPreview>(`/api/workflows/approver-preview?${query.toString()}`, { approverType: stage.approverType, resolutionStatus: 'Unresolved', isDynamic: false, approver: null, message: 'Approver preview could not be loaded.' })
      return [index, preview] as const
    })).then(entries => {
      if (!cancelled) setApproverPreviews(Object.fromEntries(entries))
    })
    return () => { cancelled = true }
  }, [section, flow.clientId, approverPreviewSignature])

  const updateStage = (index: number, changes: Partial<Stage>) => setFlow(current => ({ ...current, stages: current.stages.map((stage, position) => position === index ? { ...stage, ...changes } : stage) }))
  const visibleApprovers = approvers.filter(user => flow.clientId ? !user.clientId || user.clientId === flow.clientId : !user.clientId)
  const selectedActivity = activityRows.find(activity => activity.activityCode === (selectedFlowActivityCode || flow.code))
  const selectedRuleActivity = activityRows.find(activity => activity.activityCode === rule.activityCode)
  const selectedEndpoint = apiCatalog.find(endpoint => endpointKey(endpoint) === `${rule.httpMethod.toUpperCase()} ${rule.pathPattern}`)
  const moduleOptions = Array.from(new Set([...coreModules, ...apiCatalog.map(row => row.module), ...activityRows.map(row => row.moduleCode)].filter(Boolean))).sort().map(value => ({ value, label: value }))
  const resourceOptions = Array.from(new Map([
    ...activityRows.filter(row => row.moduleCode === activity.moduleCode).map(row => ({ value: row.resourceType, label: humanize(row.resourceType) })),
    ...(activity.resourceType ? [{ value: activity.resourceType, label: humanize(activity.resourceType) }] : []),
    ...(resourcePresets[activity.moduleCode] || []),
  ].map(item => [item.value, item])).values())
  const compatibleEndpoints = workflowEndpoints.filter(endpoint => !selectedRuleActivity || safeEndpointActivity[endpointKey(endpoint)] === selectedRuleActivity.activityCode)
  const selectableEndpoints = selectedEndpoint && !compatibleEndpoints.some(endpoint => endpointKey(endpoint) === endpointKey(selectedEndpoint)) ? [...compatibleEndpoints, selectedEndpoint] : compatibleEndpoints
  const endpointOptions = selectableEndpoints.map(endpoint => ({ value: endpointKey(endpoint), label: `${endpoint.purpose.replace(/\.$/, '')} - ${endpoint.module} - ${endpoint.method} ${endpoint.path}` }))
  const activityRecommendation = { activityCode: activityCodeFor(activity.moduleCode, activity.displayName), resourceType: inferredResourceType(activity.moduleCode, activity.displayName) }
  const activityCustomized = Boolean(activity.displayName && activity.moduleCode) && (activity.activityCode !== activityRecommendation.activityCode || activity.resourceType !== activityRecommendation.resourceType)
  const workflowCustomized = selectedActivity ? flow.code !== selectedActivity.activityCode || flow.resourceType !== selectedActivity.resourceType : false
  const isDirectSelectedActivity = Boolean(selectedActivity && directWorkflowResources.has(selectedActivity.resourceType))
  const isDirectWorkflow = Boolean(selectedRuleActivity && directWorkflowResources.has(selectedRuleActivity.resourceType))
  const selectedEndpointIsSafe = Boolean(selectedEndpoint && selectedRuleActivity && safeGenericEndpointKeys.has(endpointKey(selectedEndpoint)) && safeEndpointActivity[endpointKey(selectedEndpoint)] === selectedRuleActivity.activityCode)
  const ruleMatchesVerifiedPreset = selectedEndpointIsSafe && rule.resourceType === 'PayRun' && rule.resourceIdSource === 'route.id' && !rule.clientIdSource && !rule.clientIdSql && rule.clientLookupTable === 'payruns' && rule.clientLookupKeyColumn === 'Id' && rule.clientLookupClientColumn === 'ClientId' && rule.triggerMode === 'AfterSuccess'
  const ruleWorkflowOptions = rows.filter(item => item.isActive && item.code === rule.activityCode && item.resourceType === rule.resourceType)
  const currentRuleWorkflow = rows.find(item => item.id === rule.workflowId)
  const recordSource = splitSource(rule.resourceIdSource)
  const clientSource = splitSource(rule.clientIdSource)
  const clientMode = rule.clientIdSql ? 'sql' : rule.clientIdSource ? 'request' : rule.clientLookupTable ? 'lookup' : 'login'
  const clientResolutionLabel = clientMode === 'lookup' ? `${rule.clientLookupTable || 'table'}.${rule.clientLookupClientColumn || 'ClientId'}` : clientMode === 'request' ? rule.clientIdSource : clientMode === 'sql' ? 'Legacy SQL lookup' : 'Logged-in user'
  const selectActivity = (activityCode: string) => {
    const activity = activityRows.find(item => item.activityCode === activityCode)
    if (!activity) return
    setSelectedFlowActivityCode(activityCode)
    setFlow(current => ({
      ...current,
      code: activity.activityCode,
      resourceType: activity.resourceType,
      name: current.name && current.code === activity.activityCode ? current.name : `${activity.displayName} workflow`
    }))
  }
  const selectRuleActivity = (activityCode: string) => {
    const activity = activityRows.find(item => item.activityCode === activityCode)
    const match = workflowEndpoints.filter(endpoint => safeEndpointActivity[endpointKey(endpoint)] === activityCode)
    setRule(current => {
      const next = current.id === 0
        ? { ...newActionRule(), activityCode, resourceType: activity?.resourceType ?? '', workflowId: null }
        : { ...current, activityCode, resourceType: activity?.resourceType ?? current.resourceType }
      return current.id === 0 && match.length === 1 ? recommendedRuleFor(next, match[0], activity) : next
    })
  }
  const selectFlowClient = (value: string) => {
    const clientId = value ? Number(value) : null
    setFlow(current => ({ ...current, clientId, stages: current.stages.map(stage => stage.approverType === 'Specific User' ? { ...stage, approverUserId: null } : stage) }))
  }
  const updateActivityBusinessField = (changes: Partial<Pick<Activity, 'displayName' | 'moduleCode'>>) => setActivity(current => {
    const next = { ...current, ...changes }
    if (current.id === 0 && activityCodeAuto) next.activityCode = activityCodeFor(next.moduleCode, next.displayName)
    if (current.id === 0 && activityResourceAuto) next.resourceType = inferredResourceType(next.moduleCode, next.displayName)
    return next
  })
  const resetActivityTechnical = () => {
    setActivity(current => ({ ...current, activityCode: activityCodeFor(current.moduleCode, current.displayName), resourceType: inferredResourceType(current.moduleCode, current.displayName) }))
    setActivityCodeAuto(true)
    setActivityResourceAuto(true)
  }
  const applyEndpoint = (value: string) => {
    const endpoint = selectableEndpoints.find(row => endpointKey(row) === value)
    if (!endpoint) return
    setRule(current => recommendedRuleFor(current, endpoint, selectedRuleActivity))
  }
  const resetRuleTechnical = () => {
    if (selectedEndpoint && selectedEndpointIsSafe) setRule(current => recommendedRuleFor(current, selectedEndpoint, selectedRuleActivity))
  }
  const resetWorkflowTechnical = () => {
    if (selectedActivity) setFlow(current => ({ ...current, code: selectedActivity.activityCode, resourceType: selectedActivity.resourceType }))
  }
  const applyApprovalPreset = (preset: string) => {
    const types = preset === 'manager-hr' ? ['Reporting Manager', 'HR Manager'] : preset === 'hr' ? ['HR Manager'] : preset === 'department' ? ['Department Head'] : ['Reporting Manager']
    setFlow(current => ({ ...current, stages: types.map((approverType, index) => ({ id: 0, stageOrder: index + 1, name: approverLabel(approverType), approverType })) }))
  }
  const edit = (row: Flow) => { setSelectedFlowActivityCode(row.code); setFlow({ ...row, stages: row.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }); setSection('designer'); setMessage(`Editing ${row.name}.`) }
  const cancel = () => { setSelectedFlowActivityCode(''); setFlow(newWorkflow()); setMessage('') }
  const editActivity = (row: Activity) => { setActivity({ ...row }); setActivityCodeAuto(false); setActivityResourceAuto(false); setSection('activities'); setMessage(`Editing activity ${row.displayName}.`) }
  const cancelActivity = () => { setActivity(newActivity()); setActivityCodeAuto(true); setActivityResourceAuto(true); setMessage('') }
  const editRule = (row: ActionRule) => { setRule({ ...row }); setSection('rules'); setRuleDialogOpen(true); setMessage(`Editing start rule for ${row.activityCode}.`) }
  const openRule = (seed?: Partial<ActionRule>) => { setRule({ ...newActionRule(), ...seed }); setSection('rules'); setRuleDialogOpen(true); setMessage('') }
  const openRuleForWorkflow = (workflow: Flow) => {
    const existing = rules.find(item => item.activityCode === workflow.code && (item.workflowId == null || item.workflowId === workflow.id))
    if (existing) return editRule(existing)
    openRule({ activityCode: workflow.code, resourceType: workflow.resourceType, workflowId: null })
  }
  const cancelRule = () => { setRule(newActionRule()); setRuleDialogOpen(false); setMessage('') }
  const setClientMode = (mode: string) => setRule(current => mode === 'request'
    ? { ...current, clientIdSource: current.clientIdSource || 'body.clientId', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '', clientIdSql: '' }
    : mode === 'lookup'
      ? { ...current, clientIdSource: '', clientIdSql: '', clientLookupTable: current.clientLookupTable || 'payruns', clientLookupKeyColumn: current.clientLookupKeyColumn || 'Id', clientLookupClientColumn: current.clientLookupClientColumn || 'ClientId' }
      : mode === 'sql'
        ? { ...current, clientIdSource: '', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '', clientIdSql: current.clientIdSql || 'SELECT ClientId FROM ... WHERE Id = @ResourceId' }
        : { ...current, clientIdSource: '', clientIdSql: '', clientLookupTable: '', clientLookupKeyColumn: '', clientLookupClientColumn: '' })

  const save = async () => {
    if (!flow.name.trim() || !flow.code.trim() || !flow.resourceType.trim() || flow.stages.length === 0 || flow.stages.some(stage => !stage.name.trim() || !stage.approverType.trim())) {
      setMessage('Workflow name and at least one complete approval stage are required.')
      return
    }
    if (rows.some(row => row.id !== flow.id && (row.clientId ?? null) === (flow.clientId ?? null) && row.code.toUpperCase() === flow.code.toUpperCase())) {
      setMessage('This client already has a workflow for the selected activity. Edit that workflow instead.')
      return
    }
    if (flow.stages.some(stage => stage.approverType === 'Specific User' && !stage.approverUserId)) {
      setMessage('Select an assigned user for every Specific User stage.')
      return
    }
    if (flow.stages.some(stage => stage.approverType === 'Specific User') && !flow.clientId) {
      setMessage('Select a client before using a Specific User approver. Global workflows should use a role-based approver.')
      return
    }
    if (flow.stages.some(stage => stage.approverType === 'Specific User' && approvers.some(user => user.id === stage.approverUserId && user.clientId && user.clientId !== flow.clientId))) {
      setMessage('A Specific User approver must belong to the selected client.')
      return
    }
    const response = await postJson('/api/workflows', { ...flow, stages: flow.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }, null)
    if (!response.ok) {
      setMessage('Unable to save workflow. Check the details and try again.')
      return
    }
    setMessage(flow.id ? 'Workflow updated.' : 'Workflow created.')
    setSelectedFlowActivityCode('')
    setFlow(newWorkflow())
    load()
  }

  const saveActivity = async () => {
    if (!activity.activityCode || !activity.displayName || !activity.moduleCode || !activity.resourceType) {
      setMessage('Activity code, activity name, module, and record type are required.')
      return
    }
    if (activityRows.some(row => row.id !== activity.id && row.activityCode.trim().toUpperCase() === activity.activityCode.trim().toUpperCase())) {
      setMessage('This activity code already exists. Change the activity name or technical code.')
      return
    }
    const wasNew = activity.id === 0
    const response = await postJson<typeof activity, Activity>('/api/workflows/activities', { ...activity, activityCode: activity.activityCode.trim().toUpperCase() }, activity, { successMessage: 'Workflow activity saved.' })
    if (!response.ok) {
      setMessage(response.error || 'Unable to save workflow activity.')
      return
    }
    await loadActivities()
    if (wasNew) {
      setSelectedFlowActivityCode(response.data.activityCode)
      setFlow({ ...newWorkflow(), code: response.data.activityCode, name: `${response.data.displayName} workflow`, resourceType: response.data.resourceType })
      setSection('designer')
      setMessage('Activity saved. Now select the client and confirm who should approve it.')
    } else setMessage('Workflow activity updated.')
    setActivity(newActivity())
    setActivityCodeAuto(true)
    setActivityResourceAuto(true)
  }

  const saveRule = async () => {
    if (isDirectWorkflow && rule.id === 0) {
      setMessage('This Recruitment activity starts from its own configuration screen and does not need a Start Rule.')
      return
    }
    if (!rule.activityCode || !rule.httpMethod || !rule.pathPattern || !rule.resourceType || !rule.resourceIdSource) {
      setMessage('Activity, method, path, resource type, and resource id source are required.')
      return
    }
    if (rules.some(item => item.id !== rule.id && item.httpMethod.toUpperCase() === rule.httpMethod.toUpperCase() && item.pathPattern.toLowerCase() === rule.pathPattern.toLowerCase())) {
      setMessage('This screen/API request already has a start rule. Edit the existing rule because only one rule can safely own a request.')
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
    const response = await postJson('/api/workflows/action-rules', { ...rule, httpMethod: rule.httpMethod.toUpperCase(), workflowId: rule.workflowId || null }, null, { successMessage: 'Workflow trigger mapping saved.' })
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
    <section className="card workflow-admin" data-testid="workflow-setup">
      <header><div><h3>Workflow Setup</h3><p>Tell the system what needs approval, who approves it, and only when required, which screen action starts it.</p></div></header>
      <div className="workflow-setup-guide" aria-label="Workflow setup steps">
        <article className={section === 'activities' ? 'active' : ''}><b>1</b><div><strong>What needs approval?</strong><span>Create or select a business activity.</span></div></article>
        <article className={section === 'designer' ? 'active' : ''}><b>2</b><div><strong>Who approves it?</strong><span>Choose client and approval chain.</span></div></article>
        <article className={section === 'rules' ? 'active' : ''}><b>3</b><div><strong>When should it start?</strong><span>Only generic API actions need a start rule.</span></div></article>
      </div>
      {message && <p className="form-warning" role="status">{message}</p>}
      <div className="page-tabs" role="tablist">
        <button type="button" role="tab" aria-selected={section === 'activities'} className={section === 'activities' ? 'active' : ''} onClick={() => { setMessage(''); setSection('activities') }}>Activity Master</button>
        <button type="button" role="tab" aria-selected={section === 'designer'} className={section === 'designer' ? 'active' : ''} onClick={() => { setMessage(''); setSection('designer') }}>Workflow Designer</button>
        <button type="button" role="tab" aria-selected={section === 'rules'} className={section === 'rules' ? 'active' : ''} onClick={() => { setMessage(''); setSection('rules') }}>Start Rules</button>
      </div>
      {section === 'activities' && <section className="approval-stages" data-testid="workflow-section-activities">
        <div className="approval-stages-heading"><div><h3>What needs approval?</h3><p>Enter a familiar business name. Technical identity is prepared automatically and remains editable in Advanced configuration.</p></div><span>{activityRows.length} activities</span></div>
        <div className="grid workflow-business-grid">
          <label><span>Activity name</span><input data-testid="workflow-activity-name" value={activity.displayName} onChange={event => updateActivityBusinessField({ displayName: event.target.value })} placeholder="HR Discussion to Pre-Onboarding" /><small>Write what the user is trying to approve.</small></label>
          <label><span>Module</span><SearchSelect testId="workflow-activity-module" value={activity.moduleCode} onChange={moduleCode => updateActivityBusinessField({ moduleCode })} options={[{ value: '', label: 'Select module' }, ...moduleOptions]} /><small>Only standard application modules are shown.</small></label>
          <label><span>Record being approved</span><SearchSelect testId="workflow-activity-resource" value={activity.resourceType} onChange={resourceType => { setActivity(current => ({ ...current, resourceType })); setActivityResourceAuto(false) }} options={[{ value: '', label: 'Auto detect from activity' }, ...resourceOptions]} /><small>Recommended automatically; choose another business record if needed.</small></label>
          <label><span>Status</span><SearchSelect value={activity.isActive ? 'active' : 'inactive'} onChange={value => setActivity({ ...activity, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
          <label className="workflow-wide"><span>Description</span><input value={activity.description} onChange={event => setActivity({ ...activity, description: event.target.value })} placeholder="Why does this action need approval? (optional)" /></label>
        </div>
        <div className="workflow-generated-summary"><div><span>Generated activity code</span><b>{activity.activityCode || 'Enter activity name and module'}</b></div><div><span>System record</span><b>{activity.resourceType || 'Auto detected after module selection'}</b></div><Tag color={activityCustomized ? 'gold' : 'green'}>{activityCustomized ? 'Customized' : 'Auto configured'}</Tag></div>
        <Collapse className="workflow-advanced-collapse"><Collapse.Panel key="activity-technical" header="Advanced configuration">
          {activity.id > 0 && <Alert showIcon type="warning" message="Technical identity is already in use" description="Changing an existing activity code or record type does not rename linked workflows or start rules. Change these values only when you also intend to update those mappings." />}
          <div className="grid"><label><span>Activity code</span><input data-testid="workflow-activity-code" value={activity.activityCode} onChange={event => { setActivity({ ...activity, activityCode: event.target.value.toUpperCase().replace(/[^A-Z0-9._]/g, '_') }); setActivityCodeAuto(false) }} placeholder="RECRUITMENT.HR_DISCUSSION_TO_PRE_ONBOARDING" /><small>Stable technical key in MODULE.ACTION format.</small></label><label><span>Raw resource type</span><input data-testid="workflow-activity-resource-raw" value={activity.resourceType} onChange={event => { setActivity({ ...activity, resourceType: event.target.value }); setActivityResourceAuto(false) }} placeholder="RecruitmentPipelineTransition" /></label></div>
          <div className="actions"><button type="button" className="secondary" onClick={resetActivityTechnical}>Reset to recommended values</button></div>
        </Collapse.Panel></Collapse>
        <div className="actions">{activity.id > 0 && <button type="button" className="secondary" onClick={cancelActivity}>Cancel</button>}<button type="button" onClick={() => void saveActivity()} disabled={!activity.activityCode || !activity.displayName || !activity.moduleCode || !activity.resourceType}>{activity.id ? 'Update activity' : 'Save & design approval'}</button></div>
        <DataTable rows={activityRows} emptyText="No workflow activities configured yet." exportFileName="workflow-activities" columns={[
          { key: 'displayName', label: 'Activity', render: row => <>{row.displayName}<small>{row.activityCode}</small></> },
          { key: 'moduleCode', label: 'Module' },
          { key: 'resourceType', label: 'Record type' },
          { key: 'description', label: 'Purpose' },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <button type="button" className="secondary" onClick={() => editActivity(row)}>Edit</button>} />
      </section>}
      {section === 'designer' && <section className="approval-stages" data-testid="workflow-section-designer">
        <div className="approval-stages-heading"><div><h3>Who should approve?</h3><p>Select the business activity and use a ready-made approval chain. You can still fine-tune every stage.</p></div><span>{rows.length} workflows</span></div>
        <div className="grid workflow-business-grid">
          <label><span>Applies to</span><SearchSelect testId="workflow-client" value={flow.clientId ?? ''} onChange={selectFlowClient} options={[{ value: '', label: 'All clients (global default)' }, ...clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name }))]} /><small>Choose a client only when its approval chain is different.</small></label>
          <label><span>Activity</span><SearchSelect testId="workflow-designer-activity" value={selectedFlowActivityCode || flow.code} onChange={selectActivity} options={[{ value: '', label: 'Select what needs approval' }, ...Array.from(new Map([...activities, ...(selectedActivity ? [selectedActivity] : [])].map(item => [item.activityCode, item])).values()).map(item => ({ value: item.activityCode, label: `${item.displayName} - ${item.moduleCode}` }))]} /><small>{selectedActivity?.description || 'Create it first in Activity Master if it is not listed.'}</small></label>
          <label><span>Workflow name</span><input data-testid="workflow-name" value={flow.name} onChange={event => setFlow({ ...flow, name: event.target.value })} placeholder="For example, TA hiring approval" /><small>A familiar name users can recognise.</small></label>
        </div>
        <div className="workflow-generated-summary"><div><span>Activity code</span><b>{flow.code || 'Select an activity'}</b></div><div><span>Record</span><b>{flow.resourceType ? humanize(flow.resourceType) : 'Selected automatically'}</b></div><Tag color={workflowCustomized ? 'gold' : 'green'}>{workflowCustomized ? 'Customized' : 'Auto configured'}</Tag></div>
        {isDirectSelectedActivity && <Alert className="workflow-direct-note" showIcon type="success" message="This approval starts automatically from Recruitment" description={<>No Start Rule is needed. Select this workflow at <b>{directConfigurationLocation[selectedActivity?.resourceType || '']}</b>.</>} />}
        <div className="workflow-quick-presets" aria-label="Approval chain presets">
          <div><strong>Quick approval chain</strong><span>Pick the closest option; you can edit it below.</span></div>
          <button type="button" className="secondary" onClick={() => applyApprovalPreset('manager')}>Reporting Manager</button>
          <button type="button" className="secondary" onClick={() => applyApprovalPreset('hr')}>HR Manager</button>
          <button type="button" className="secondary" onClick={() => applyApprovalPreset('department')}>Department Head</button>
          <button type="button" className="secondary" onClick={() => applyApprovalPreset('manager-hr')}>Manager, then HR</button>
        </div>
        <section className="workflow-stage-section"><div className="approval-stages-heading"><div><h3>Approval stages</h3><p>The request moves from stage 1 onward. Stage names are filled automatically when the approver changes.</p></div><span>{flow.stages.length} {flow.stages.length === 1 ? 'stage' : 'stages'}</span></div><div className="stage-list">
          {flow.stages.map((stage, index) => <article className="workflow-stage" key={stage.id || index}><div className="stage-number"><b>{index + 1}</b><span>{index === 0 ? 'Starts here' : 'Then'}</span></div><label><span>Stage name</span><input value={stage.name} onChange={event => updateStage(index, { name: event.target.value })} placeholder={`Approval ${index + 1}`} /></label><label><span>Who approves?</span><SearchSelect value={stage.approverType} onChange={value => updateStage(index, { approverType: value, name: approverLabel(value), approverUserId: value === 'Specific User' ? stage.approverUserId : null })} options={['Reporting Manager', 'HR Manager', 'Department Head', ...(flow.clientId || stage.approverType === 'Specific User' ? ['Specific User'] : [])].map(value => ({ value, label: value }))} /><small>{!flow.clientId ? 'Select a client to assign one specific user safely.' : ''}</small>{approverPreviews[index] && <span className={`workflow-approver-preview ${approverPreviews[index].resolutionStatus !== 'Resolved' && !approverPreviews[index].isDynamic ? 'warning' : ''}`} data-testid={`workflow-approver-preview-${index}`}>{approverPreviews[index].approver ? `Assigned to: ${approverPreviews[index].approver?.displayName}${approverPreviews[index].approver?.email ? ` (${approverPreviews[index].approver?.email})` : ''}` : approverPreviews[index].message}</span>}</label>{stage.approverType === 'Specific User' && <label className="specific-user"><span>Select user</span><SearchSelect value={stage.approverUserId ?? ''} onChange={value => updateStage(index, { approverUserId: value ? Number(value) : null })} options={[{ value: '', label: 'Select a user' }, ...visibleApprovers.map(user => ({ value: user.id, label: `${user.displayName} - ${user.clientName}` }))]} /></label>}<button type="button" className="stage-remove" onClick={() => setFlow({ ...flow, stages: flow.stages.filter((_, position) => position !== index) })} disabled={flow.stages.length === 1}>Remove</button></article>)}
        </div><button type="button" className="add-stage" onClick={() => setFlow({ ...flow, stages: [...flow.stages, { id: 0, stageOrder: flow.stages.length + 1, name: 'HR Manager approval', approverType: 'HR Manager' }] })}>+ Add another approver</button></section>
        <Collapse className="workflow-advanced-collapse"><Collapse.Panel key="workflow-technical" header="Advanced configuration">
          {flow.id > 0 && <Alert showIcon type="warning" message="This workflow may already be linked" description="Changing its code or record type does not update existing start rules or Recruitment configuration automatically." />}
          <div className="grid"><label><span>Workflow code</span><input data-testid="workflow-code" value={flow.code} onChange={event => setFlow({ ...flow, code: event.target.value.toUpperCase().replace(/[^A-Z0-9._]/g, '_') })} placeholder="RECRUITMENT.HR_DISCUSSION" /></label><label><span>Raw resource type</span><input data-testid="workflow-resource-raw" value={flow.resourceType} onChange={event => setFlow({ ...flow, resourceType: event.target.value })} placeholder="RecruitmentPipelineTransition" /></label><label><span>Status</span><SearchSelect value={flow.isActive ? 'active' : 'inactive'} onChange={value => setFlow({ ...flow, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label></div>
          <div className="actions"><button type="button" className="secondary" onClick={resetWorkflowTechnical} disabled={!selectedActivity}>Reset to activity defaults</button></div>
        </Collapse.Panel></Collapse>
        <div className="actions">{flow.id > 0 && <button type="button" className="secondary" onClick={cancel}>Cancel</button>}<button type="button" onClick={() => void save()} disabled={!flow.code || !flow.name || !flow.resourceType}>{flow.id ? 'Update approval workflow' : 'Save approval workflow'}</button></div>
        <DataTable rows={rows} emptyText="No workflows have been configured yet." exportFileName="workflows" columns={[
          { key: 'workflow', label: 'Workflow', value: row => row.name, render: row => <>{row.name}<small>{row.code}</small></> },
          { key: 'clientName', label: 'Applies to', value: row => row.clientId ? clients.find(client => client.id === row.clientId)?.name ?? `Client #${row.clientId}` : 'All clients' },
          { key: 'resourceType', label: 'Record', value: row => humanize(row.resourceType) },
          { key: 'stagesText', label: 'Approval chain', value: row => row.stages.map(stage => stage.name).join(' -> ') },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <div className="ant-table-row-actions"><button type="button" className="secondary" onClick={() => edit(row)}>Edit</button>{!directWorkflowResources.has(row.resourceType) && <button type="button" className="secondary" onClick={() => openRuleForWorkflow(row)}>{rules.some(item => item.activityCode === row.code) ? 'Edit start rule' : 'Start rule'}</button>}</div>} />
      </section>}
      {section === 'rules' && <section className="approval-stages" data-testid="workflow-section-rules">
        <div className="approval-stages-heading"><div><h3>When should approval start?</h3><p>Most Recruitment actions are already connected. Use a Start Rule only for a supported generic screen/API action.</p></div><button type="button" className="add-stage" onClick={() => openRule()}>+ Connect a screen action</button></div>
        <Alert className="workflow-direct-note" showIcon type="info" message="Recruitment usually needs no Start Rule" description="Requisition, Job Description, Offer, and Pipeline Transition approvals start from their Recruitment configuration screens. Existing mappings remain available below for review." />
        <DataTable rows={rules} emptyText="No workflow start rules have been configured yet." exportFileName="workflow-start-rules" columns={[
          { key: 'activityCode', label: 'Action' },
          { key: 'methodPath', label: 'Request', value: row => `${row.httpMethod} ${row.pathPattern}` },
          { key: 'resource', label: 'Record', value: row => `${row.resourceType} from ${row.resourceIdSource}` },
          { key: 'client', label: 'Client', value: row => row.clientLookupTable ? `${row.clientLookupTable}.${row.clientLookupClientColumn}` : row.clientIdSource || 'Logged-in user' },
          { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} actions={row => <button type="button" className="secondary" onClick={() => editRule(row)}>Edit</button>} />
      </section>}
      <Modal title={rule.id ? 'Edit screen action connection' : 'Connect a screen action'} open={ruleDialogOpen} onCancel={cancelRule} footer={null} width={900}>
        <div className="grid workflow-business-grid workflow-simple-request">
          <label><span>What needs approval?</span><SearchSelect testId="workflow-rule-activity" value={rule.activityCode} onChange={selectRuleActivity} options={[{ value: '', label: 'Select activity' }, ...Array.from(new Map([...activities, ...(selectedRuleActivity ? [selectedRuleActivity] : [])].map(item => [item.activityCode, item])).values()).map(item => ({ value: item.activityCode, label: `${item.displayName} - ${item.moduleCode}` }))]} /><small>{selectedRuleActivity?.description || 'Choose the Activity Master entry.'}</small></label>
          {!isDirectWorkflow && <label><span>Screen/API action</span><SearchSelect testId="workflow-rule-endpoint" value={selectedEndpoint ? endpointKey(selectedEndpoint) : ''} onChange={applyEndpoint} options={[{ value: '', label: endpointOptions.length ? 'Select supported action' : 'No safe automatic mapping available' }, ...endpointOptions]} /><small>Choosing a supported action fills every technical field automatically.</small></label>}
        </div>
        {isDirectWorkflow && <Alert className="workflow-direct-note" showIcon type="success" message="No Start Rule is required" description={<>This activity starts inside Recruitment. Configure it at <b>{directConfigurationLocation[selectedRuleActivity?.resourceType || '']}</b>.{rule.id > 0 ? ' This existing legacy mapping can still be reviewed or disabled in Advanced configuration.' : ''}</>} />}
        {!isDirectWorkflow && selectedRuleActivity && endpointOptions.length === 0 && <Alert className="workflow-direct-note" showIcon type="warning" message="No verified automatic connection is available" description="The Activity and Workflow can still be saved. Ask a technical administrator to create a custom mapping from Advanced configuration only after verifying the API lifecycle." />}
        {!isDirectWorkflow && <div className="workflow-generated-summary" data-testid="workflow-rule-summary"><div><span>Request</span><b>{rule.pathPattern ? `${rule.httpMethod} ${rule.pathPattern}` : 'Select a supported screen action'}</b></div><div><span>Record number</span><b>{rule.resourceIdSource || 'Auto configured'}</b></div><div><span>Client found from</span><b>{clientResolutionLabel}</b></div><Tag color={ruleMatchesVerifiedPreset ? 'green' : rule.pathPattern ? 'gold' : 'default'}>{ruleMatchesVerifiedPreset ? 'Verified preset' : rule.pathPattern ? 'Customized' : 'Not connected'}</Tag></div>}
        <Collapse className="workflow-advanced-collapse"><Collapse.Panel key="rule-technical" header="Advanced configuration">
          <Alert showIcon type="warning" message="Technical administrator area" description="Incorrect endpoint, record source, or client lookup values can start approval for the wrong transaction. Existing values are preserved unless you change them." />
          <div className="grid">
            <label><span>Request type</span><SearchSelect value={rule.httpMethod} onChange={value => setRule({ ...rule, httpMethod: value })} options={['POST', 'PUT', 'PATCH', 'DELETE'].map(value => ({ value, label: value }))} /></label>
            <label className="workflow-wide"><span>Request path</span><input data-testid="workflow-rule-path" value={rule.pathPattern} onChange={event => setRule({ ...rule, pathPattern: event.target.value })} placeholder="/api/pay-runs/{id}/submit" /><small>Use braces for route values, for example {'{id}'}.</small></label>
            <label><span>Resource type</span><input value={rule.resourceType} onChange={event => setRule({ ...rule, resourceType: event.target.value })} placeholder="PayRun" /></label>
            <label><span>Record number comes from</span><SearchSelect value={recordSource.location} onChange={value => setRule({ ...rule, resourceIdSource: joinSource(value, recordSource.field) })} options={sourceLocations} /></label>
            <label><span>Record number field</span><input value={recordSource.field} onChange={event => setRule({ ...rule, resourceIdSource: joinSource(recordSource.location, event.target.value) })} placeholder="id" /></label>
            <label><span>Find client using</span><SearchSelect value={clientMode} onChange={setClientMode} options={[{ value: 'lookup', label: 'Lookup from table' }, { value: 'request', label: 'Value in request' }, { value: 'login', label: 'Logged-in user client' }, { value: 'sql', label: 'Legacy SQL lookup' }]} /></label>
            {clientMode === 'request' && <><label><span>Client value comes from</span><SearchSelect value={clientSource.location} onChange={value => setRule({ ...rule, clientIdSource: joinSource(value, clientSource.field) })} options={sourceLocations} /></label><label><span>Client field</span><input value={clientSource.field} onChange={event => setRule({ ...rule, clientIdSource: joinSource(clientSource.location, event.target.value) })} placeholder="clientId" /></label></>}
            {clientMode === 'lookup' && <><label><span>Table name</span><input value={rule.clientLookupTable} onChange={event => setRule({ ...rule, clientLookupTable: event.target.value })} placeholder="payruns" /></label><label><span>Match column</span><input value={rule.clientLookupKeyColumn} onChange={event => setRule({ ...rule, clientLookupKeyColumn: event.target.value })} placeholder="Id" /><small>This column stores the record number.</small></label><label><span>Client column</span><input value={rule.clientLookupClientColumn} onChange={event => setRule({ ...rule, clientLookupClientColumn: event.target.value })} placeholder="ClientId" /></label></>}
            {clientMode === 'sql' && <label className="workflow-wide"><span>Legacy client lookup SQL</span><textarea value={rule.clientIdSql} onChange={event => setRule({ ...rule, clientIdSql: event.target.value })} rows={3} /><small>Kept for backward compatibility. Prefer a table lookup for new rules.</small></label>}
            <label className="workflow-wide"><span>Force one workflow (optional)</span><SearchSelect value={rule.workflowId ?? ''} onChange={value => setRule({ ...rule, workflowId: value ? Number(value) : null })} options={[{ value: '', label: 'Auto select by activity and client (recommended)' }, ...Array.from(new Map([...ruleWorkflowOptions, ...(currentRuleWorkflow ? [currentRuleWorkflow] : [])].map(item => [item.id, item])).values()).map(item => ({ value: item.id, label: `${item.name} - ${item.clientId ? clients.find(client => client.id === item.clientId)?.name || 'Client' : 'Global'}` }))]} /><small>Keep Auto for multi-client safety. A forced client workflow applies to every matching request because Start Rules are global.</small></label>
            <label><span>Trigger mode</span><input value={rule.triggerMode || 'AfterSuccess'} disabled /><small>The current engine starts only after a successful API response.</small></label>
            <label><span>Status</span><SearchSelect value={rule.isActive ? 'active' : 'inactive'} onChange={value => setRule({ ...rule, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
          </div>
          <div className="actions"><button type="button" className="secondary" onClick={resetRuleTechnical} disabled={!selectedEndpointIsSafe}>Reset to verified preset</button></div>
        </Collapse.Panel></Collapse>
        <div className="actions"><button type="button" className="secondary" onClick={cancelRule}>Close</button>{(!isDirectWorkflow || rule.id > 0) && <button type="button" onClick={() => void saveRule()} disabled={!rule.activityCode || !rule.pathPattern || !rule.resourceType || !rule.resourceIdSource}>{rule.id ? 'Update connection' : 'Save connection'}</button>}</div>
      </Modal>
    </section>
  )
}
