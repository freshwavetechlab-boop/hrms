import { useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  ArrowDownOutlined, ArrowUpOutlined, AuditOutlined, DeleteOutlined, FileAddOutlined,
  PlusOutlined, SaveOutlined, SendOutlined,
} from '@ant-design/icons'
import {
  Alert, Button, Card, Col, Collapse, Descriptions, Empty, Form, Input, InputNumber, List,
  Modal, Row, Select, Space, Spin, Switch, Tag, Tooltip, Typography, message,
} from 'antd'
import { getClients } from '../services/payrollService'
import { getRecruitmentRequisitions } from '../services/recruitmentService'
import {
  getRecruitmentJobDescription, getRecruitmentJobDescriptions, getRecruitmentOrchestrationLookups,
  saveRecruitmentJobDescription, submitRecruitmentJobDescription,
} from '../services/recruitmentOrchestrationService'
import type { Client, RecruitmentRequisition } from '../types/payroll'
import type {
  RecruitmentJdBenefit, RecruitmentJdCertificationRequirement, RecruitmentJdLanguageRequirement,
  RecruitmentJdQualificationRequirement, RecruitmentJdResponsibility, RecruitmentJdSkillRequirement,
  RecruitmentJobDescriptionVersion, RecruitmentOrchestrationLookups,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = {
  initialClientId?: number
  initialRequisitionId?: number
  onSaved?: (description: RecruitmentJobDescriptionVersion) => void
}

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}
const localId = () => -Math.floor(Date.now() + Math.random() * 100000)
const editableStatuses = new Set(['Draft', 'Sent Back'])

export default function RecruitmentJobDescriptionManager({ initialClientId = 0, initialRequisitionId = 0, onSaved }: Props) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [requisitions, setRequisitions] = useState<RecruitmentRequisition[]>([])
  const [requisitionId, setRequisitionId] = useState(initialRequisitionId)
  const [versions, setVersions] = useState<RecruitmentJobDescriptionVersion[]>([])
  const [draft, setDraft] = useState<RecruitmentJobDescriptionVersion | null>(null)
  const [lookups, setLookups] = useState(emptyLookups)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [approvalOpen, setApprovalOpen] = useState(false)
  const [workflowId, setWorkflowId] = useState<number>()

  useEffect(() => {
    void Promise.all([getClients(), getRecruitmentRequisitions({})]).then(([rows, requestRows]) => {
      setClients(rows)
      if (clientId || !rows.length) return
      const preferredRequest = requestRows.find(row => row.status === 'Approved') ?? requestRows[0]
      setClientId(initialClientId || preferredRequest?.clientId || rows[0].id)
    })
  }, [])

  useEffect(() => {
    if (!clientId) return
    setLoading(true)
    Promise.all([
      getRecruitmentRequisitions({ clientId }),
      getRecruitmentOrchestrationLookups(clientId),
    ]).then(([requestRows, lookupRows]) => {
      setRequisitions(requestRows)
      setLookups(lookupRows)
      const requestedId = requisitionId || initialRequisitionId
      const preferredRequest = requestRows.find(row => row.status === 'Approved') ?? requestRows[0]
      const nextId = requestRows.some(row => row.id === requestedId) ? requestedId : preferredRequest?.id ?? 0
      setRequisitionId(nextId)
      if (!nextId) { setVersions([]); setDraft(null) }
    }).finally(() => setLoading(false))
  }, [clientId])

  useEffect(() => {
    if (requisitionId) void loadVersions(requisitionId)
  }, [requisitionId])

  const selectedRequisition = requisitions.find(row => row.id === requisitionId)
  const readOnly = !!draft?.id && !editableStatuses.has(draft.status)
  const approvalWorkflows = useMemo(() => {
    const active = lookups.workflows.filter(row => row.isActive && (!row.clientId || row.clientId === clientId))
    const exact = active.filter(row => !row.resourceType || row.resourceType === 'RecruitmentJobDescription')
    return exact.length ? exact : active
  }, [lookups.workflows, clientId])

  async function loadVersions(requestId: number, preferredId = 0) {
    setLoading(true)
    const rows = await getRecruitmentJobDescriptions(requestId)
    setVersions(rows)
    const preferred = rows.find(row => row.id === preferredId)
      ?? rows.find(row => editableStatuses.has(row.status))
      ?? rows[0]
    if (preferred) {
      const full = await getRecruitmentJobDescription(preferred.id)
      setDraft(full ?? preferred)
    } else {
      const request = requisitions.find(row => row.id === requestId)
      setDraft(request ? blankDescription(request) : null)
    }
    setLoading(false)
  }

  async function chooseVersion(id: number) {
    setLoading(true)
    const row = await getRecruitmentJobDescription(id)
    if (row) setDraft(row)
    else message.error('Unable to load this job-description version.')
    setLoading(false)
  }

  function startRevision() {
    if (!selectedRequisition) return message.warning('Select a requisition first.')
    if (!draft) return setDraft(blankDescription(selectedRequisition))
    setDraft(cloneDescription(draft))
  }

  function patch(value: Partial<RecruitmentJobDescriptionVersion>) {
    setDraft(current => current ? { ...current, ...value } : current)
  }

  async function saveDraft() {
    if (!draft || readOnly) return
    const error = validateDescription(draft)
    if (error) return message.warning(error)
    setSaving(true)
    const payload = normalizeDescription(draft)
    const response = await saveRecruitmentJobDescription(payload)
    setSaving(false)
    if (!response.ok || !response.data) return
    onSaved?.(response.data)
    await loadVersions(response.data.requisitionId, response.data.id)
  }

  async function submitForApproval() {
    if (!draft?.id || !workflowId || readOnly) return
    setSaving(true)
    const response = await submitRecruitmentJobDescription(draft.id, workflowId)
    setSaving(false)
    if (!response.ok || !response.data) return
    setApprovalOpen(false)
    setWorkflowId(undefined)
    await loadVersions(draft.requisitionId, draft.id)
  }

  return <section className="orchestration-shell jd-manager">
    <div className="orchestration-toolbar">
      <div>
        <span className="orchestration-kicker">Demand to approved role profile</span>
        <h2 className="orchestration-title">Job Description Workspace</h2>
        <p className="orchestration-subtitle">Create governed JD versions against an approved hiring request and route them through the existing workflow engine.</p>
      </div>
      <Space wrap>
        <Select aria-label="Client" value={clientId || undefined} placeholder="Select client" showSearch optionFilterProp="label" style={{ minWidth: 230 }}
          options={clients.map(row => ({ value: row.id, label: row.name }))}
          onChange={value => { setClientId(value); setRequisitionId(0); setDraft(null); setVersions([]) }} />
        <Select aria-label="Requisition" value={requisitionId || undefined} placeholder="Select requisition" showSearch optionFilterProp="label" style={{ minWidth: 330 }}
          options={requisitions.map(row => ({ value: row.id, label: `${row.rfrNumber} · ${row.positionTitle} · ${row.status}` }))}
          notFoundContent="No hiring requisitions for this client"
          onChange={setRequisitionId} />
      </Space>
    </div>

    {selectedRequisition && <Card size="small" className="jd-context-card">
      <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 5 }}>
        <Descriptions.Item label="Request">{selectedRequisition.rfrNumber}</Descriptions.Item>
        <Descriptions.Item label="Department">{selectedRequisition.department || '—'}</Descriptions.Item>
        <Descriptions.Item label="Hiring type">{selectedRequisition.hiringType || '—'}</Descriptions.Item>
        <Descriptions.Item label="Openings">{selectedRequisition.numberOfOpenings}</Descriptions.Item>
        <Descriptions.Item label="Budget">{selectedRequisition.budgetAvailable ? `${selectedRequisition.currency} ${selectedRequisition.budgetAmount.toLocaleString()}` : 'Not specified'}</Descriptions.Item>
      </Descriptions>
    </Card>}

    <Spin spinning={loading}>
      {!selectedRequisition ? <Card><Empty description={requisitions.length ? 'Select a hiring requisition to prepare its job description.' : 'No hiring requisition exists for this client. Create and approve an RFR first; its JD draft will be prepared automatically here.'} /></Card> : <div className="jd-workspace-layout">
        <Card size="small" className="jd-version-list" title={<Space><AuditOutlined /> Versions</Space>} extra={<Button size="small" icon={<PlusOutlined />} onClick={startRevision}>New version</Button>}>
          <List dataSource={versions} locale={{ emptyText: 'No saved versions yet.' }} renderItem={row => <List.Item className={draft?.id === row.id ? 'active' : ''} onClick={() => void chooseVersion(row.id)}>
            <List.Item.Meta title={<Space><span>Version {row.versionNumber}</span><StatusTag status={row.status} /></Space>} description={row.title || 'Untitled job description'} />
          </List.Item>} />
          {!versions.length && <Alert showIcon type="info" message="Your first draft is ready" description="Complete the role profile, save it, then submit it to the configured approval workflow." />}
        </Card>

        {!draft ? <Card><Empty description="Create or select a version." /></Card> : <div className="jd-editor">
          <Card size="small">
            <div className="orchestration-toolbar">
              <Space wrap><Tag color="purple">v{draft.versionNumber || versions.length + 1}</Tag><StatusTag status={draft.status} />{draft.workflowInstanceId ? <Tag>Workflow #{draft.workflowInstanceId}</Tag> : null}</Space>
              <Space wrap>
                {readOnly && <Button icon={<FileAddOutlined />} onClick={startRevision}>Create revision</Button>}
                <Button icon={<SaveOutlined />} loading={saving} disabled={readOnly} onClick={() => void saveDraft()}>Save draft</Button>
                <Button type="primary" icon={<SendOutlined />} disabled={readOnly || !draft.id} onClick={() => setApprovalOpen(true)}>Submit for approval</Button>
              </Space>
            </div>
            {readOnly && <Alert className="jd-readonly-alert" type={draft.status === 'Approved' ? 'success' : 'info'} showIcon message={`${draft.status} versions are immutable`} description="Create a new version to make changes without altering the historical approved JD." />}
            <Form layout="vertical" disabled={readOnly}>
              <Row gutter={14}>
                <Col xs={24} lg={14}><Form.Item label="Public role title" required><Input value={draft.title} maxLength={240} onChange={event => patch({ title: event.target.value })} /></Form.Item></Col>
                <Col xs={24} lg={10}><Form.Item label="Internal position"><Input value={`${selectedRequisition.positionTitle} (${selectedRequisition.rfrNumber})`} disabled /></Form.Item></Col>
              </Row>
              <Form.Item label="Role summary" required extra="A concise overview shown to candidates and approvers."><Input.TextArea rows={3} value={draft.summary} maxLength={4000} showCount onChange={event => patch({ summary: event.target.value })} /></Form.Item>
              <Form.Item label="Role purpose"><Input.TextArea rows={3} value={draft.rolePurpose} maxLength={4000} showCount onChange={event => patch({ rolePurpose: event.target.value })} /></Form.Item>
            </Form>
          </Card>

          <RepeaterSection title="Responsibilities" description="Define measurable outcomes and ownership for this role." rows={draft.responsibilities} readOnly={readOnly}
            addLabel="Add responsibility" onAdd={() => patch({ responsibilities: [...draft.responsibilities, responsibility()] })}
            onChange={responsibilities => patch({ responsibilities })}
            render={(row, index, update) => <Input.TextArea rows={2} value={row.responsibilityText} placeholder={`Responsibility ${index + 1}`} maxLength={1000} onChange={event => update({ responsibilityText: event.target.value })} />} />

          <RepeaterSection title="Skills & ATS weights" description="Required skills are normalized and become the explainable ATS scoring baseline." rows={draft.skills} readOnly={readOnly}
            addLabel="Add skill" onAdd={() => patch({ skills: [...draft.skills, skill()] })} onChange={skills => patch({ skills })}
            render={(row, _index, update) => <Row gutter={10}>
              <Col xs={24} md={7}><Form.Item label="Skill" required><Input value={row.skillName} placeholder="e.g. ASP.NET Core" onChange={event => update({ skillName: event.target.value })} /></Form.Item></Col>
              <Col xs={12} md={4}><Form.Item label="Min. years"><InputNumber min={0} max={50} step={0.5} value={row.minimumYears} onChange={value => update({ minimumYears: Number(value ?? 0) })} /></Form.Item></Col>
              <Col xs={12} md={5}><Form.Item label="Proficiency"><Select value={row.minimumProficiency || undefined} allowClear options={['Beginner', 'Intermediate', 'Advanced', 'Expert'].map(value => ({ value, label: value }))} onChange={value => update({ minimumProficiency: value ?? '' })} /></Form.Item></Col>
              <Col xs={12} md={4}><Form.Item label="ATS weight %"><InputNumber min={0} max={100} value={row.weightPercent} onChange={value => update({ weightPercent: Number(value ?? 0) })} /></Form.Item></Col>
              <Col xs={12} md={4}><Form.Item label="Required"><Switch checked={row.isRequired} onChange={isRequired => update({ isRequired })} /></Form.Item></Col>
            </Row>} />

          <RepeaterSection title="Qualifications" description="Keep mandatory and preferred education requirements explicit." rows={draft.qualifications} readOnly={readOnly}
            addLabel="Add qualification" onAdd={() => patch({ qualifications: [...draft.qualifications, qualification()] })} onChange={qualifications => patch({ qualifications })}
            render={(row, _index, update) => <Row gutter={10}>
              <Col xs={24} md={10}><Form.Item label="Qualification" required><Input value={row.qualificationName} placeholder="e.g. B.Tech / MCA" onChange={event => update({ qualificationName: event.target.value })} /></Form.Item></Col>
              <Col xs={24} md={10}><Form.Item label="Specialization"><Input value={row.specialization} placeholder="Computer Science" onChange={event => update({ specialization: event.target.value })} /></Form.Item></Col>
              <Col xs={24} md={4}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col>
            </Row>} />

          <Collapse>
            <Collapse.Panel key="additional" header="Additional role requirements">
              <Space direction="vertical" size={14} style={{ width: '100%' }}>
              <RepeaterSection compact title="Certifications" rows={draft.certifications} readOnly={readOnly} addLabel="Add certification" onAdd={() => patch({ certifications: [...draft.certifications, certification()] })} onChange={certifications => patch({ certifications })}
                render={(row, _index, update) => <Row gutter={10}><Col xs={24} md={19}><Form.Item label="Certification"><Input value={row.certificationName} onChange={event => update({ certificationName: event.target.value })} /></Form.Item></Col><Col xs={24} md={5}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col></Row>} />
              <RepeaterSection compact title="Languages" rows={draft.languages} readOnly={readOnly} addLabel="Add language" onAdd={() => patch({ languages: [...draft.languages, language()] })} onChange={languages => patch({ languages })}
                render={(row, _index, update) => <Row gutter={10}><Col xs={24} md={9}><Form.Item label="Language"><Input value={row.languageName} onChange={event => update({ languageName: event.target.value })} /></Form.Item></Col><Col xs={24} md={10}><Form.Item label="Proficiency"><Select value={row.proficiency || undefined} allowClear options={['Basic', 'Conversational', 'Professional', 'Native'].map(value => ({ value, label: value }))} onChange={value => update({ proficiency: value ?? '' })} /></Form.Item></Col><Col xs={24} md={5}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col></Row>} />
                <RepeaterSection compact title="Benefits" rows={draft.benefits} readOnly={readOnly} addLabel="Add benefit" onAdd={() => patch({ benefits: [...draft.benefits, benefit()] })} onChange={benefits => patch({ benefits })}
                render={(row, _index, update) => <Row gutter={10}><Col xs={24} md={8}><Form.Item label="Benefit"><Input value={row.benefitName} onChange={event => update({ benefitName: event.target.value })} /></Form.Item></Col><Col xs={24} md={16}><Form.Item label="Description"><Input value={row.description} onChange={event => update({ description: event.target.value })} /></Form.Item></Col></Row>} />
              </Space>
            </Collapse.Panel>
          </Collapse>
        </div>}
      </div>}
    </Spin>

    <Modal title="Submit job description for approval" open={approvalOpen} okText="Submit to workflow" confirmLoading={saving}
      okButtonProps={{ disabled: !workflowId }} onOk={() => void submitForApproval()} onCancel={() => { setApprovalOpen(false); setWorkflowId(undefined) }}>
      <Alert showIcon type="info" message="The current version becomes read-only while approval is pending." />
      <Form layout="vertical" style={{ marginTop: 16 }}><Form.Item label="Approval workflow" required>
        <Select value={workflowId} showSearch optionFilterProp="label" placeholder="Select the configured approval chain" onChange={setWorkflowId}
          options={approvalWorkflows.map(row => ({ value: row.id, label: `${row.name}${row.code ? ` · ${row.code}` : ''}` }))} />
      </Form.Item></Form>
      {!approvalWorkflows.length && <Alert type="warning" showIcon message="No active JD workflow is configured" description="Create a RecruitmentJobDescription workflow in Workflow Settings before submitting." />}
    </Modal>
  </section>
}

type OrderedRow = { id: number; displayOrder: number }
type RepeaterProps<T extends OrderedRow> = {
  title: string
  description?: string
  rows: T[]
  readOnly: boolean
  addLabel: string
  compact?: boolean
  onAdd: () => void
  onChange: (rows: T[]) => void
  render: (row: T, index: number, update: (value: Partial<T>) => void) => ReactNode
}

export function RepeaterSection<T extends OrderedRow>({ title, description, rows, readOnly, addLabel, compact, onAdd, onChange, render }: RepeaterProps<T>) {
  const update = (index: number, value: Partial<T>) => onChange(rows.map((row, rowIndex) => rowIndex === index ? { ...row, ...value } : row))
  const remove = (index: number) => onChange(rows.filter((_row, rowIndex) => rowIndex !== index).map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })))
  const move = (index: number, delta: number) => {
    const target = index + delta
    if (target < 0 || target >= rows.length) return
    const next = [...rows]; [next[index], next[target]] = [next[target], next[index]]
    onChange(next.map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })))
  }
  return <Card size="small" className={compact ? 'jd-repeater compact' : 'jd-repeater'} title={<div><Typography.Text strong>{title}</Typography.Text>{description && <Typography.Text type="secondary">{description}</Typography.Text>}</div>}
    extra={<Button size="small" icon={<PlusOutlined />} disabled={readOnly} onClick={onAdd}>{addLabel}</Button>}>
    {!rows.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={`No ${title.toLowerCase()} added.`} /> : rows.map((row, index) => <Card key={`${row.id}-${index}`} size="small" className="jd-repeater-row">
      <div className="jd-repeater-actions">
        <Tag>{index + 1}</Tag>
        <Tooltip title="Move up"><Button size="small" icon={<ArrowUpOutlined />} disabled={readOnly || index === 0} onClick={() => move(index, -1)} /></Tooltip>
        <Tooltip title="Move down"><Button size="small" icon={<ArrowDownOutlined />} disabled={readOnly || index === rows.length - 1} onClick={() => move(index, 1)} /></Tooltip>
        <Tooltip title="Remove"><Button size="small" danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => remove(index)} /></Tooltip>
      </div>
      <div className="jd-repeater-content"><Form layout="vertical" disabled={readOnly}>{render(row, index, value => update(index, value))}</Form></div>
    </Card>)}
  </Card>
}

function StatusTag({ status }: { status: string }) {
  const color = status === 'Approved' ? 'green' : status === 'Pending Approval' ? 'blue' : status === 'Rejected' ? 'red' : status === 'Sent Back' ? 'orange' : 'gold'
  return <Tag color={color}>{status || 'Draft'}</Tag>
}

function blankDescription(request: RecruitmentRequisition): RecruitmentJobDescriptionVersion {
  const required = splitTerms(request.requiredSkills)
  const preferred = splitTerms(request.preferredSkills)
  const qualifications = splitTerms(request.qualification)
  return {
    id: 0, requisitionId: request.id, clientId: request.clientId, versionNumber: 1,
    title: request.positionTitle, summary: request.businessJustification || request.reasonForHiring || '',
    rolePurpose: request.reasonForHiring || '', status: 'Draft', workflowInstanceId: null,
    responsibilities: [responsibility(request.businessJustification || '')],
    skills: [...required.map(name => skill(name, true)), ...preferred.map(name => skill(name, false))],
    qualifications: qualifications.map(name => qualification(name)), certifications: [], languages: [], benefits: [],
  }
}

function cloneDescription(source: RecruitmentJobDescriptionVersion): RecruitmentJobDescriptionVersion {
  const reset = <T extends { id: number; jobDescriptionVersionId: number; displayOrder: number }>(rows: T[]) => rows.map((row, index) => ({ ...row, id: localId(), jobDescriptionVersionId: 0, displayOrder: index + 1 }))
  return {
    ...source, id: 0, versionNumber: source.versionNumber + 1, status: 'Draft', workflowInstanceId: null,
    approvedByUserId: null, approvedAtUtc: null,
    responsibilities: reset(source.responsibilities), skills: reset(source.skills), qualifications: reset(source.qualifications),
    certifications: reset(source.certifications), languages: reset(source.languages), benefits: reset(source.benefits),
  }
}

function normalizeDescription(source: RecruitmentJobDescriptionVersion) {
  const order = <T extends { displayOrder: number }>(rows: T[]) => rows.map((row, index) => ({ ...row, displayOrder: index + 1 }))
  return {
    id: source.id, requisitionId: source.requisitionId, title: source.title.trim(), summary: source.summary.trim(), rolePurpose: source.rolePurpose.trim(),
    responsibilities: order(source.responsibilities.filter(row => row.responsibilityText.trim()).map(row => ({ ...row, responsibilityText: row.responsibilityText.trim() }))),
    skills: order(source.skills.filter(row => row.skillName.trim()).map(row => ({ ...row, skillName: row.skillName.trim() }))),
    qualifications: order(source.qualifications.filter(row => row.qualificationName.trim()).map(row => ({ ...row, qualificationName: row.qualificationName.trim(), specialization: row.specialization.trim() }))),
    certifications: order(source.certifications.filter(row => row.certificationName.trim()).map(row => ({ ...row, certificationName: row.certificationName.trim() }))),
    languages: order(source.languages.filter(row => row.languageName.trim()).map(row => ({ ...row, languageName: row.languageName.trim() }))),
    benefits: order(source.benefits.filter(row => row.benefitName.trim()).map(row => ({ ...row, benefitName: row.benefitName.trim(), description: row.description.trim() }))),
  }
}

function validateDescription(row: RecruitmentJobDescriptionVersion) {
  if (!row.title.trim()) return 'Enter the job title.'
  if (!row.summary.trim()) return 'Enter a candidate-facing role summary.'
  if (!row.responsibilities.some(item => item.responsibilityText.trim())) return 'Add at least one responsibility.'
  if (row.skills.some(item => item.weightPercent < 0 || item.weightPercent > 100)) return 'Every ATS skill weight must be between 0 and 100.'
  const weight = row.skills.reduce((sum, item) => sum + Number(item.weightPercent || 0), 0)
  if (weight > 100) return 'Combined ATS skill weights cannot exceed 100%.'
  return ''
}

function splitTerms(value: string) { return (value || '').split(/[,;\n]/).map(item => item.trim()).filter(Boolean) }
function responsibility(text = ''): RecruitmentJdResponsibility { return { id: localId(), jobDescriptionVersionId: 0, responsibilityText: text, displayOrder: 100 } }
function skill(name = '', isRequired = true): RecruitmentJdSkillRequirement { return { id: localId(), jobDescriptionVersionId: 0, skillId: null, skillName: name, isRequired, minimumYears: 0, minimumProficiency: '', weightPercent: 0, displayOrder: 100 } }
function qualification(name = ''): RecruitmentJdQualificationRequirement { return { id: localId(), jobDescriptionVersionId: 0, qualificationName: name, specialization: '', isMandatory: true, displayOrder: 100 } }
function certification(): RecruitmentJdCertificationRequirement { return { id: localId(), jobDescriptionVersionId: 0, certificationName: '', isMandatory: false, displayOrder: 100 } }
function language(): RecruitmentJdLanguageRequirement { return { id: localId(), jobDescriptionVersionId: 0, languageName: '', proficiency: '', isMandatory: false, displayOrder: 100 } }
function benefit(): RecruitmentJdBenefit { return { id: localId(), jobDescriptionVersionId: 0, benefitName: '', description: '', displayOrder: 100 } }
