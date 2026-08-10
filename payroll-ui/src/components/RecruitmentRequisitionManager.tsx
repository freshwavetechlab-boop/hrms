import { useEffect, useMemo, useState } from 'react'
import {
  DeleteOutlined, EditOutlined, EyeOutlined, PlusOutlined, ReloadOutlined, SaveOutlined, SearchOutlined, SendOutlined,
} from '@ant-design/icons'
import {
  AutoComplete, Button, Collapse, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Table, Tag,
  Tooltip, message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useAuthSession } from './AuthGate'
import { getClients, getEmployees } from '../services/payrollService'
import {
  deleteRecruitmentRequisition, getRecruitmentMasterOptions, getRecruitmentRequisitions, saveRecruitmentRequisition,
  submitRecruitmentRequisition,
} from '../services/recruitmentService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import type {
  Client, Drop, Employee, RecruitmentRequisition, SaveRecruitmentRequisition, WorkLocation,
} from '../types/payroll'
import RecruitmentEditorDrawer from './RecruitmentEditorDrawer'
import './RecruitmentRequisitionManager.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialOpen?: boolean
  embedded?: boolean
  onChanged?: (row: RecruitmentRequisition) => void
  onPrepareJobDescription?: (row: RecruitmentRequisition) => void
}

type MasterOptions = {
  hiringTypes: string[]
  positionCategories: string[]
  experienceRanges: string[]
  priorities: string[]
  budgetAmounts: string[]
}

const editableStatuses = new Set(['Draft', 'Sent Back'])
const emptyMasters: MasterOptions = {
  hiringTypes: [], positionCategories: [], experienceRanges: [], priorities: [], budgetAmounts: [],
}

export default function RecruitmentRequisitionManager({ initialClientId = 0, clientScopeManaged = false, initialOpen = false, embedded = false, onChanged, onPrepareJobDescription }: Props) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [form] = Form.useForm<SaveRecruitmentRequisition>()
  const [clients, setClients] = useState<Client[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [masters, setMasters] = useState<MasterOptions>(emptyMasters)
  const [rows, setRows] = useState<RecruitmentRequisition[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [readOnly, setReadOnly] = useState(false)
  const [query, setQuery] = useState('')
  const [clientFilter, setClientFilter] = useState(initialClientId)
  const [statusFilter, setStatusFilter] = useState('')
  const [watchedForm, setWatchedForm] = useState({ id: 0, clientId: 0, isReplacement: false, budgetAvailable: false })
  const selectedClientId = watchedForm.clientId
  const replacementHiring = watchedForm.isReplacement
  const budgetAvailable = watchedForm.budgetAvailable
  const applyDraft = (draft: SaveRecruitmentRequisition) => {
    form.setFieldsValue(draft)
    setWatchedForm({ id: draft.id || 0, clientId: draft.clientId || 0, isReplacement: Boolean(draft.isReplacement), budgetAvailable: Boolean(draft.budgetAvailable) })
  }

  useEffect(() => {
    let active = true
    void getRecruitmentRequisitions({}).then(requestRows => {
      if (!active) return
      setRows(requestRows)
      setLoading(false)
    })
    void fetchWorkspace().then(data => {
      if (!active) return
      setClients(data.clientRows)
      setDropdowns(data.dropRows)
      setLocations(data.locationRows)
      setEmployees(data.employeeRows)
      setMasters({
        hiringTypes: data.hiringTypes, positionCategories: data.positionCategories,
        experienceRanges: data.experienceRanges, priorities: data.priorities, budgetAmounts: data.budgetAmounts,
      })
      if (initialOpen) {
        const nextClientId = initialClientId || data.clientRows[0]?.id || 0
        const draft = blankRequest(nextClientId)
        draft.requestedByEmployeeId = session?.user.employeeId
          ?? data.employeeRows.find(row => row.isActive && (!nextClientId || row.clientId === nextClientId))?.id
          ?? null
        applyDraft(draft)
        setDialogOpen(true)
      }
      setLoading(false)
    })
    return () => { active = false }
  }, [])

  async function refreshRows() {
    setLoading(true)
    setRows(await getRecruitmentRequisitions({}))
    setLoading(false)
  }

  const filteredRows = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return rows.filter(row => {
      if (clientFilter && row.clientId !== clientFilter) return false
      if (statusFilter && row.status !== statusFilter) return false
      if (!needle) return true
      return [row.rfrNumber, row.positionTitle, row.department, row.clientName, row.requestedByName]
        .some(value => String(value || '').toLowerCase().includes(needle))
    })
  }, [rows, clientFilter, statusFilter, query])

  const statuses = useMemo(() => unique(rows.map(row => row.status)), [rows])
  const departmentOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Department', selectedClientId),
    ...employees.filter(row => !selectedClientId || row.clientId === selectedClientId).map(row => row.department),
  ])), [dropdowns, employees, selectedClientId])
  const businessUnitOptions = useMemo(() => asOptions(dropValues(dropdowns, 'Business Unit', selectedClientId)), [dropdowns, selectedClientId])
  const costCenterOptions = useMemo(() => asOptions(dropValues(dropdowns, 'Cost Center', selectedClientId)), [dropdowns, selectedClientId])
  const employmentOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Employment Type', selectedClientId), 'Permanent', 'Contract', 'Intern',
  ])), [dropdowns, selectedClientId])
  const hiringOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Hiring Type', selectedClientId), ...masters.hiringTypes,
  ])), [dropdowns, masters.hiringTypes, selectedClientId])
  const categoryOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Position Category', selectedClientId), ...masters.positionCategories,
  ])), [dropdowns, masters.positionCategories, selectedClientId])
  const experienceOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Experience Range', selectedClientId), ...masters.experienceRanges,
  ])), [dropdowns, masters.experienceRanges, selectedClientId])
  const priorityOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Assignment Priority', selectedClientId), ...masters.priorities,
    'Low', 'Normal', 'High', 'Critical',
  ])), [dropdowns, masters.priorities, selectedClientId])
  const locationOptions = useMemo(() => asOptions(unique(locations
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .flatMap(row => [row.name, [row.city, row.state].filter(Boolean).join(', ')]))), [locations, selectedClientId])
  const replacementOptions = useMemo(() => employees
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .map(row => ({ value: row.id, label: `${row.employeeCode} · ${row.firstName} ${row.lastName}` })), [employees, selectedClientId])

  const requesterOptions = useMemo(() => employees
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .map(row => ({ value: row.id, label: `${row.employeeCode} - ${row.firstName} ${row.lastName}` })), [employees, selectedClientId])

  const columns: ColumnsType<RecruitmentRequisition> = [
    {
      title: 'Hiring request', key: 'request', fixed: 'left', width: 245,
      render: (_, row) => <div className="rfr-primary-cell"><b>{row.positionTitle || 'Untitled role'}</b><span>{row.rfrNumber || 'Draft request'}</span></div>,
    },
    {
      title: 'Client & team', key: 'client', width: 210,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.clientName || '—'}</b><span>{row.department || 'No department'}</span></div>,
    },
    {
      title: 'Hiring plan', key: 'plan', width: 185,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.numberOfOpenings} opening{row.numberOfOpenings === 1 ? '' : 's'}</b><span>{[row.hiringType, row.employmentType].filter(Boolean).join(' · ') || 'Not classified'}</span></div>,
    },
    {
      title: 'Target', key: 'target', width: 165,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.targetJoiningDate ? displayDate(row.targetJoiningDate) : 'Not planned'}</b><span>{row.jobLocation || row.workMode || 'Location pending'}</span></div>,
    },
    {
      title: 'Budget', dataIndex: 'budgetAmount', width: 135, align: 'right',
      render: (value: number, row) => row.budgetAvailable ? <b>{money(value, row.currency)}</b> : <span className="rfr-muted">Not tagged</span>,
    },
    {
      title: 'Status', dataIndex: 'status', width: 145,
      render: (value: string) => <Tag color={statusColor(value)}>{value || 'Draft'}</Tag>,
    },
    {
      title: 'Updated', dataIndex: 'updatedAt', width: 130,
      render: (value: string) => <span className="rfr-muted">{displayDate(value)}</span>,
    },
    {
      title: 'Actions', key: 'actions', fixed: 'right', width: 285,
      render: (_, row) => <Space size={4}>
        {editableStatuses.has(row.status)
          ? <Tooltip title="Edit draft"><Button aria-label="Edit draft" size="small" icon={<EditOutlined />} onClick={() => openRequest(row)} /></Tooltip>
          : <Tooltip title="View request"><Button aria-label="View request" size="small" icon={<EyeOutlined />} onClick={() => openRequest(row, true)} /></Tooltip>}
        {editableStatuses.has(row.status) && <Button size="small" type="link" icon={<SendOutlined />} onClick={() => confirmSubmit(row)}>Submit</Button>}
        {row.status === 'Approved' && onPrepareJobDescription && <Button size="small" type="link" onClick={() => onPrepareJobDescription(row)}>Prepare JD</Button>}
        {canDelete && <Popconfirm placement="left" title="Delete this requisition?" description="Delete its open position and job-description versions first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentRequisition(row.id); if (response.ok) await refreshRows() }}><Button title="Delete requisition" danger aria-label="Delete requisition" size="small" icon={<DeleteOutlined />} /></Popconfirm>}
      </Space>,
    },
  ]

  function openNew() {
    setReadOnly(false)
    const nextClientId = initialClientId || clientFilter || clients[0]?.id || 0
    const draft = blankRequest(nextClientId)
    draft.requestedByEmployeeId = session?.user.employeeId
      ?? employees.find(row => row.isActive && (!nextClientId || row.clientId === nextClientId))?.id
      ?? null
    applyDraft(draft)
    setDialogOpen(true)
  }

  function openRequest(row: RecruitmentRequisition, forceReadOnly = false) {
    setReadOnly(forceReadOnly || !editableStatuses.has(row.status))
    applyDraft(fromRow(row))
    setDialogOpen(true)
  }

  async function saveRequest(submitAfterSave: boolean) {
    let values: SaveRecruitmentRequisition
    try { values = await form.validateFields() } catch { return }
    setSaving(true)
    const saved = await saveRecruitmentRequisition(normalize(values))
    if (!saved.ok || !saved.data) { setSaving(false); return }
    let completed = saved.data
    if (submitAfterSave) {
      const submitted = await submitRecruitmentRequisition(saved.data.id)
      if (!submitted.ok || !submitted.data) { setSaving(false); return }
      completed = submitted.data
      message.success('Hiring request submitted for approval.')
    } else message.success('Hiring request saved as draft.')
    setSaving(false)
    setDialogOpen(false)
    onChanged?.(completed)
    await refreshRows()
  }

  function confirmSubmit(row: RecruitmentRequisition) {
    Modal.confirm({
      title: 'Submit this hiring request?',
      content: `${row.rfrNumber} will move to the configured approval workflow.`,
      okText: 'Submit request', cancelText: 'Keep as draft',
      onOk: async () => {
        const response = await submitRecruitmentRequisition(row.id)
        if (!response.ok || !response.data) return Promise.reject()
        message.success('Hiring request submitted for approval.')
        onChanged?.(response.data)
        await refreshRows()
      },
    })
  }

  const advancedFields = <div className="rfr-advanced-grid">
    <Form.Item name="businessUnit" label="Business unit"><AutoComplete options={businessUnitOptions} placeholder="Optional" /></Form.Item>
    <Form.Item name="costCenter" label="Cost center"><AutoComplete options={costCenterOptions} placeholder="Optional" /></Form.Item>
    <Form.Item name="workMode" label="Work mode"><Select options={asOptions(['Office', 'Hybrid', 'Remote'])} /></Form.Item>
    <Form.Item name="project" label="Project"><Input placeholder="Optional project or contract" /></Form.Item>
    <Form.Item name="experienceRange" label="Experience"><Select allowClear showSearch options={experienceOptions} placeholder="Select if required" /></Form.Item>
    <Form.Item name="qualification" label="Qualification"><Input placeholder="Minimum qualification" /></Form.Item>
    <Form.Item name="requiredSkills" label="Required skills" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Comma-separated must-have skills" /></Form.Item>
    <Form.Item name="preferredSkills" label="Preferred skills" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Good-to-have skills" /></Form.Item>
    <Form.Item name="certifications" label="Certifications"><Input placeholder="Optional" /></Form.Item>
    <Form.Item name="languages" label="Languages"><Input placeholder="Optional" /></Form.Item>
    <Form.Item name="salaryMin" label="Salary range"><InputNumber min={0} controls={false} addonBefore="Min" style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="salaryMax" label=" "><InputNumber min={0} controls={false} addonBefore="Max" style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="currency" label="Currency"><Select showSearch options={asOptions(['INR', 'USD', 'EUR', 'GBP'])} /></Form.Item>
    <Form.Item name="benefits" label="Benefits"><Input placeholder="Benefits summary" /></Form.Item>
    <Form.Item name="businessJustification" label="Business justification" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Why this position is needed" /></Form.Item>
    <Form.Item name="reasonForHiring" label="Hiring notes" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Optional context for approvers" /></Form.Item>
    <Form.Item name="externalPositionCode" label="Client position code" extra="Use the exact code stated by the client. If approved, this becomes the open-position code."><Input placeholder="For example, UIDAI_BTC_0_25" /></Form.Item>
    <Form.Item name="sourceType" label="Source type"><Select allowClear options={asOptions(['Formal Approval Letter', 'Hiring Initiation Email', 'Job Description', 'Modified Job Description', 'Other'])} placeholder="Select document type" /></Form.Item>
    <Form.Item name="sourceReference" label="Source reference"><Input placeholder="File number, computer number or email subject" /></Form.Item>
    <Form.Item name="sourceDocumentDate" label="Source document date"><Input type="date" /></Form.Item>
    <Form.Item name="sourceDocumentName" label="Source document" className="rfr-span-2"><Input placeholder="Original PDF file name(s)" /></Form.Item>
    <Form.Item name="sourceAuthority" label="Source authority"><Input placeholder="Requesting / approving authority" /></Form.Item>
    <Form.Item name="externalApprovalStatus" label="Client approval state"><Select allowClear options={asOptions(['Approved for Hiring', 'Hiring Initiated', 'JD Received', 'JD Awaited', 'Pending Client Confirmation', 'Not Available'])} placeholder="Separate from HRMS workflow status" /></Form.Item>
    <Form.Item name="ctcFlexibilityPercent" label="CTC flexibility (%)"><InputNumber min={0} max={100} precision={2} style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="sourceNotes" label="Source notes" className="rfr-span-2"><Input.TextArea rows={3} placeholder="Preserve ambiguities and missing facts without inventing values" /></Form.Item>
  </div>

  return <section className="rfr-manager" data-testid="requisition-manager">
    {!embedded && <header className="rfr-header">
      <div><span>Recruitment</span><h2>Hiring Requests</h2><p>Raise, review and submit workforce demand without leaving the request register.</p></div>
      <Button type="primary" icon={<PlusOutlined />} onClick={openNew} data-testid="new-hiring-request">New hiring request</Button>
    </header>}

    <div className="rfr-toolbar">
      <Input allowClear prefix={<SearchOutlined />} value={query} onChange={event => setQuery(event.target.value)} placeholder="Search role, RFR or requester" />
      {!clientScopeManaged && <Select allowClear value={clientFilter || undefined} onChange={value => setClientFilter(value || 0)} placeholder="All clients" showSearch optionFilterProp="label"
        options={clients.map(row => ({ value: row.id, label: row.name }))} />}
      <Select allowClear value={statusFilter || undefined} onChange={value => setStatusFilter(value || '')} placeholder="All statuses" options={asOptions(statuses)} />
      <Tooltip title="Refresh register"><Button aria-label="Refresh register" icon={<ReloadOutlined />} onClick={() => void refreshRows()} /></Tooltip>
    </div>

    <Table rowKey="id" loading={loading} columns={columns} dataSource={filteredRows} size="middle"
      pagination={{ pageSize: 10, showSizeChanger: true, showTotal: total => `${total} requests` }}
      scroll={{ x: 1400 }} locale={{ emptyText: 'No hiring requests match the selected filters.' }} />

    <RecruitmentEditorDrawer className="rfr-dialog" open={dialogOpen} width="min(1040px, 96vw)" destroyOnClose={false} footer={false}
      onClose={() => !saving && setDialogOpen(false)} kicker="Hiring request"
      title={readOnly ? 'Request details' : watchedForm.id ? 'Edit draft' : 'New hiring request'}
      description={readOnly ? 'Review the approved demand and its hiring context.' : 'Capture the essential demand first; advanced role and approval context stays optional.'}>
      <Form form={form} layout="vertical" disabled={readOnly} requiredMark="optional" className="rfr-form" onValuesChange={(_, values: SaveRecruitmentRequisition) => setWatchedForm({ id: values.id || 0, clientId: values.clientId || 0, isReplacement: Boolean(values.isReplacement), budgetAvailable: Boolean(values.budgetAvailable) })}>
        <div className="rfr-essential-grid">
          <Form.Item name="id" hidden><InputNumber /></Form.Item>
          <Form.Item name="branchId" hidden><InputNumber /></Form.Item>
          <Form.Item name="clientId" label="Client" rules={[{ required: true, message: 'Select the hiring client.' }]}>
            <Select showSearch optionFilterProp="label" placeholder="Select client" disabled={clientScopeManaged && initialClientId > 0} options={clients.map(row => ({ value: row.id, label: row.name }))}
              onChange={value => {
                const linkedRequester = session?.user.employeeId
                const belongs = employees.some(row => row.id === linkedRequester && row.clientId === value && row.isActive)
                form.setFieldValue('requestedByEmployeeId', belongs ? linkedRequester : undefined)
              }} />
          </Form.Item>
          <Form.Item name="requestedByEmployeeId" label="Requested by" rules={[{ required: true, message: 'Select the requester employee.' }]}>
            <Select showSearch optionFilterProp="label" placeholder="Select active employee" options={requesterOptions} />
          </Form.Item>
          <Form.Item name="requestDate" label="Request date" rules={[{ required: true, message: 'Enter the hiring request date.' }]}><Input type="date" /></Form.Item>
          <Form.Item name="positionTitle" label="Role / position" rules={[{ required: true, whitespace: true, message: 'Enter the position title.' }]}>
            <Input placeholder="For example, Senior .NET Engineer" maxLength={190} />
          </Form.Item>
          <Form.Item name="department" label="Department" rules={[{ required: true, whitespace: true, message: 'Enter the department.' }]}>
            <AutoComplete options={departmentOptions} placeholder="Select or enter department" />
          </Form.Item>
          <Form.Item name="numberOfOpenings" label="Openings" rules={[{ required: true, type: 'number', min: 1, message: 'At least one opening is required.' }]}>
            <InputNumber min={1} max={999} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="hiringType" label="Hiring type"><Select allowClear showSearch options={hiringOptions} placeholder="Select hiring type" /></Form.Item>
          <Form.Item name="employmentType" label="Employment type"><Select allowClear showSearch options={employmentOptions} placeholder="Select employment type" /></Form.Item>
          <Form.Item name="positionCategory" label="Position category"><Select allowClear showSearch options={categoryOptions} placeholder="Select category" /></Form.Item>
          <Form.Item name="hiringPriority" label="Priority"><Select showSearch options={priorityOptions} /></Form.Item>
          <Form.Item name="jobLocation" label="Work location"><AutoComplete options={locationOptions} placeholder="Office, city or remote" /></Form.Item>
          <Form.Item name="targetJoiningDate" label="Target joining"><Input type="date" min={today()} /></Form.Item>
        </div>

        <div className="rfr-switch-row">
          <Form.Item name="isReplacement" label="Replacement hiring" valuePropName="checked"><Switch /></Form.Item>
          <Form.Item name="budgetAvailable" label="Approved budget available" valuePropName="checked"><Switch /></Form.Item>
        </div>
        {(replacementHiring || budgetAvailable) && <div className="rfr-context-grid">
          {replacementHiring && <Form.Item name="replacementEmployeeId" label="Employee being replaced" rules={[{ required: true, message: 'Select the employee being replaced.' }]}>
            <Select showSearch optionFilterProp="label" options={replacementOptions} placeholder="Search employee" />
          </Form.Item>}
          {budgetAvailable && <Form.Item name="budgetAmount" label="Approved annual budget" rules={[{ required: true, type: 'number', min: 1, message: 'Enter the approved budget.' }]}>
            <InputNumber min={0} controls={false} style={{ width: '100%' }} placeholder={masters.budgetAmounts[0] || 'Amount'} />
          </Form.Item>}
        </div>}

        <Collapse ghost className="rfr-advanced">
          <Collapse.Panel key="advanced" header="Advanced role, skills and approval context">{advancedFields}</Collapse.Panel>
        </Collapse>

        <div className="rfr-dialog-actions">
          <Button onClick={() => setDialogOpen(false)}>{readOnly ? 'Close' : 'Cancel'}</Button>
          {!readOnly && <><Button icon={<SaveOutlined />} loading={saving} onClick={() => void saveRequest(false)}>Save draft</Button>
            <Button type="primary" icon={<SendOutlined />} loading={saving} onClick={() => void saveRequest(true)} data-testid="save-submit-requisition">Save & submit</Button></>}
        </div>
      </Form>
    </RecruitmentEditorDrawer>
  </section>
}

function blankRequest(clientId: number): SaveRecruitmentRequisition {
  return {
    id: 0, requestDate: today(), requestedByEmployeeId: null, clientId: clientId || undefined, branchId: 0, businessUnit: '', department: '', costCenter: '', positionTitle: '',
    positionCategory: '', employmentType: 'Permanent', hiringType: '', numberOfOpenings: 1, isReplacement: false,
    replacementEmployeeId: null, targetJoiningDate: null, jobLocation: '', workMode: 'Office', project: '', budgetAvailable: false,
    budgetAmount: 0, hiringPriority: 'Normal', businessJustification: '', reasonForHiring: '', experienceRange: '', qualification: '',
    requiredSkills: '', preferredSkills: '', certifications: '', languages: '', salaryMin: 0, salaryMax: 0, currency: 'INR', benefits: '',
    externalPositionCode: '', sourceType: '', sourceReference: '', sourceDocumentName: '', sourceDocumentDate: null,
    sourceAuthority: '', externalApprovalStatus: '', ctcFlexibilityPercent: null, sourceNotes: '',
  }
}

async function fetchWorkspace() {
  const [clientRows, dropRows, locationRows, employeeRows, hiringTypes, positionCategories, experienceRanges, priorities, budgetAmounts] = await Promise.all([
    getClients(), getDropdowns(), getWorkLocations(), getEmployees(),
    getRecruitmentMasterOptions('Hiring Type'), getRecruitmentMasterOptions('Position Category'),
    getRecruitmentMasterOptions('Experience Range'), getRecruitmentMasterOptions('Assignment Priority'),
    getRecruitmentMasterOptions('Budget Amount'),
  ])
  return { clientRows, dropRows, locationRows, employeeRows, hiringTypes, positionCategories, experienceRanges, priorities, budgetAmounts }
}

function fromRow(row: RecruitmentRequisition): SaveRecruitmentRequisition {
  return {
    id: row.id, requestDate: row.requestDate?.slice(0, 10) || today(), requestedByEmployeeId: row.requestedByEmployeeId, clientId: row.clientId, branchId: row.branchId || 0, businessUnit: row.businessUnit || '', department: row.department || '',
    costCenter: row.costCenter || '', positionTitle: row.positionTitle || '', positionCategory: row.positionCategory || '',
    employmentType: row.employmentType || '', hiringType: row.hiringType || '', numberOfOpenings: row.numberOfOpenings || 1,
    isReplacement: row.isReplacement, replacementEmployeeId: row.replacementEmployeeId ?? null,
    targetJoiningDate: row.targetJoiningDate?.slice(0, 10) || null, jobLocation: row.jobLocation || '', workMode: row.workMode || 'Office',
    project: row.project || '', budgetAvailable: row.budgetAvailable, budgetAmount: Number(row.budgetAmount || 0),
    hiringPriority: row.hiringPriority || 'Normal', businessJustification: row.businessJustification || '', reasonForHiring: row.reasonForHiring || '',
    experienceRange: row.experienceRange || '', qualification: row.qualification || '', requiredSkills: row.requiredSkills || '',
    preferredSkills: row.preferredSkills || '', certifications: row.certifications || '', languages: row.languages || '',
    salaryMin: Number(row.salaryMin || 0), salaryMax: Number(row.salaryMax || 0), currency: row.currency || 'INR', benefits: row.benefits || '',
    externalPositionCode: row.externalPositionCode || '', sourceType: row.sourceType || '', sourceReference: row.sourceReference || '',
    sourceDocumentName: row.sourceDocumentName || '', sourceDocumentDate: row.sourceDocumentDate?.slice(0, 10) || null,
    sourceAuthority: row.sourceAuthority || '', externalApprovalStatus: row.externalApprovalStatus || '',
    ctcFlexibilityPercent: row.ctcFlexibilityPercent == null ? null : Number(row.ctcFlexibilityPercent), sourceNotes: row.sourceNotes || '',
  }
}

function normalize(row: SaveRecruitmentRequisition): SaveRecruitmentRequisition {
  const clean = (value?: string | null) => String(value || '').trim()
  return {
    ...row, id: Number(row.id || 0), requestedByEmployeeId: Number(row.requestedByEmployeeId || 0) || null, clientId: Number(row.clientId || 0) || undefined, branchId: Number(row.branchId || 0),
    positionTitle: clean(row.positionTitle), department: clean(row.department), businessUnit: clean(row.businessUnit), costCenter: clean(row.costCenter),
    positionCategory: clean(row.positionCategory), employmentType: clean(row.employmentType), hiringType: clean(row.hiringType),
    numberOfOpenings: Number(row.numberOfOpenings || 1), replacementEmployeeId: row.isReplacement ? Number(row.replacementEmployeeId || 0) || null : null,
    targetJoiningDate: row.targetJoiningDate || null, jobLocation: clean(row.jobLocation), workMode: clean(row.workMode) || 'Office', project: clean(row.project),
    budgetAmount: row.budgetAvailable ? Number(row.budgetAmount || 0) : 0, hiringPriority: clean(row.hiringPriority) || 'Normal',
    businessJustification: clean(row.businessJustification), reasonForHiring: clean(row.reasonForHiring), experienceRange: clean(row.experienceRange),
    qualification: clean(row.qualification), requiredSkills: clean(row.requiredSkills), preferredSkills: clean(row.preferredSkills),
    certifications: clean(row.certifications), languages: clean(row.languages), salaryMin: Number(row.salaryMin || 0), salaryMax: Number(row.salaryMax || 0),
    currency: clean(row.currency) || 'INR', benefits: clean(row.benefits),
  }
}

function dropValues(rows: Drop[], type: string, clientId: number) {
  return rows.filter(row => row.isActive && row.type.toLowerCase() === type.toLowerCase() && (!row.clientId || !clientId || row.clientId === clientId)).map(row => row.value)
}
function unique(values: (string | undefined | null)[]) { return [...new Set(values.map(value => String(value || '').trim()).filter(Boolean))] }
function asOptions(values: string[]) { return values.map(value => ({ value, label: value })) }
function displayDate(value: string) { const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) }
function money(value: number, currency = 'INR') { try { return new Intl.NumberFormat('en-IN', { style: 'currency', currency, maximumFractionDigits: 0 }).format(Number(value || 0)) } catch { return `${currency} ${Number(value || 0).toLocaleString('en-IN')}` } }
function today() { const date = new Date(); date.setMinutes(date.getMinutes() - date.getTimezoneOffset()); return date.toISOString().slice(0, 10) }
function statusColor(status: string) {
  if (status === 'Approved') return 'green'
  if (status === 'Pending Approval') return 'blue'
  if (status === 'Rejected') return 'red'
  if (status === 'Sent Back') return 'orange'
  if (status === 'Withdrawn') return 'default'
  return 'gold'
}
