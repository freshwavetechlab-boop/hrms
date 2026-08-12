import { useCallback, useEffect, useMemo, useState } from 'react'
import { getJson, postJson } from '../services/apiClient'
import { getPayRun, getPayRunDiagnostics } from '../services/payrollService'
import type { PayRun, PayRunDiagnostics } from '../types/payroll'
import type { RecruitmentJobDescriptionVersion } from '../types/recruitmentOrchestration'
import { PayRunReview } from './PayRunsPanel'

type Task = {
  id: number
  instanceId: number
  stageName: string
  resourceType: string
  resourceId: string
  payloadJson: string
  approverName?: string
  actorName?: string
  status?: string
  comment?: string
  createdAt: string
  actionedAt?: string
}

type TaskView = 'pending' | 'actioned'

type JobDescriptionApprovalSnapshot = {
  snapshotType?: string
  capturedAtUtc?: string
  client?: { id?: number; name?: string }
  requisition?: {
    id?: number
    rfrNumber?: string
    positionTitle?: string
    department?: string
    businessUnit?: string
    employmentType?: string
    hiringType?: string
    numberOfOpenings?: number
    jobLocation?: string
    workMode?: string
    experienceRange?: string
    qualification?: string
    sourceType?: string
    sourceReference?: string
    sourceDocumentName?: string
  }
  position?: { id?: number | null; positionCode?: string; title?: string }
  attachments?: Array<{
    publicId?: string
    attachmentType?: string
    fieldLabel?: string
    fileName?: string
    fileSizeBytes?: number
    versionNumber?: number
    verificationStatus?: string
  }>
  submittedBy?: { id?: number; displayName?: string; email?: string }
}

const formatTaskDate = (value?: string) => {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value || '-'
  const day = String(date.getDate()).padStart(2, '0')
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const year = date.getFullYear()
  let hour = date.getHours()
  const suffix = hour >= 12 ? 'PM' : 'AM'
  hour = hour % 12 || 12
  const minute = String(date.getMinutes()).padStart(2, '0')
  const second = String(date.getSeconds()).padStart(2, '0')
  return `${day}-${month}-${year} ${String(hour).padStart(2, '0')}:${minute}:${second} ${suffix}`
}

const referenceText = (row: Task) => {
  if (row.resourceType === 'PayRun') return `PayRun #${row.resourceId}`
  if (row.resourceType === 'LeaveRequest') return `Leave request #${row.resourceId}`
  return `${row.resourceType} #${row.resourceId}`
}

const statusClass = (status?: string) => {
  const value = (status || '').toLowerCase()
  if (value.includes('approved')) return 'approved'
  if (value.includes('reject')) return 'rejected'
  if (value.includes('sent')) return 'sent-back'
  return 'pending'
}

const details = (payload: string) => {
  try {
    const value = JSON.parse(payload) as Record<string, unknown>
    return Object.entries(value)
      .filter(([, item]) => item !== null && item !== '')
      .map(([key, item]) => [
        key.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()),
        typeof item === 'object' ? JSON.stringify(item) : String(item)
      ])
  } catch {
    return []
  }
}

const jobDescriptionSnapshot = (payload: string): JobDescriptionApprovalSnapshot | null => {
  try {
    const value = JSON.parse(payload) as JobDescriptionApprovalSnapshot
    return value?.snapshotType === 'RecruitmentJobDescriptionApproval' ? value : null
  } catch {
    return null
  }
}

export default function WorkflowTasks() {
  const [rows, setRows] = useState<Task[]>([])
  const [view, setView] = useState<TaskView>('pending')
  const [selected, setSelected] = useState<Task | null>(null)
  const [payRun, setPayRun] = useState<PayRun | null>(null)
  const [diagnostics, setDiagnostics] = useState<PayRunDiagnostics | null>(null)
  const [loadingPayRun, setLoadingPayRun] = useState(false)
  const [jobDescription, setJobDescription] = useState<RecruitmentJobDescriptionVersion | null>(null)
  const [loadingJobDescription, setLoadingJobDescription] = useState(false)
  const [remark, setRemark] = useState('')
  const [message, setMessage] = useState('')
  const materialVarianceCount = useMemo(() => payRun?.employees.filter(employee => !employee.isSkipped && (Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000)).length ?? 0, [payRun])

  const load = useCallback((nextView: TaskView = view) =>
    getJson<Task[]>(nextView === 'actioned' ? '/api/workflows/tasks/actioned?scope=all' : '/api/workflows/tasks/pending', []).then(setRows), [view])

  useEffect(() => { void load(view) }, [load, view])

  useEffect(() => {
    let cancelled = false
    if (selected?.resourceType !== 'PayRun') return
    const payRunId = Number(selected.resourceId)
    if (!Number.isFinite(payRunId) || payRunId <= 0) return
    void Promise.all([getPayRun(payRunId), getPayRunDiagnostics(payRunId)]).then(([run, diagnosticRows]) => {
      if (!cancelled) {
        setPayRun(run)
        setDiagnostics(diagnosticRows)
      }
    }).finally(() => {
      if (!cancelled) setLoadingPayRun(false)
    })
    return () => { cancelled = true }
  }, [selected?.id, selected?.resourceId, selected?.resourceType])

  useEffect(() => {
    let cancelled = false
    if (selected?.resourceType !== 'RecruitmentJobDescription') return
    void getJson<RecruitmentJobDescriptionVersion | null>(`/api/workflows/tasks/${selected.id}/recruitment-job-description`, null).then(row => {
      if (!cancelled) setJobDescription(row)
    }).finally(() => {
      if (!cancelled) setLoadingJobDescription(false)
    })
    return () => { cancelled = true }
  }, [selected?.id, selected?.resourceType])

  const action = async (actionName: string) => {
    if (!selected) return
    const response = await postJson(`/api/workflows/tasks/${selected.id}/${actionName}`, { comment: remark.trim() }, null)
    setMessage(response.ok ? `Task ${actionName.toLowerCase()}.` : response.error || 'Unable to update task.')
    if (response.ok) {
      setSelected(null)
      setPayRun(null)
      setDiagnostics(null)
      setLoadingPayRun(false)
      setJobDescription(null)
      setLoadingJobDescription(false)
      setRemark('')
      await load()
    }
  }

  const openTask = (task: Task) => {
    setPayRun(null)
    setDiagnostics(null)
    setLoadingPayRun(task.resourceType === 'PayRun')
    setJobDescription(null)
    setLoadingJobDescription(task.resourceType === 'RecruitmentJobDescription')
    setSelected(task)
    setRemark('')
    setMessage('')
  }

  if (selected) {
    const payloadDetails = details(selected.payloadJson)
    const approvalSnapshot = jobDescriptionSnapshot(selected.payloadJson)
    return <section className="card workflow-admin workflow-task-detail-page">
      <header className="workflow-detail-head">
        <div>
          <button type="button" className="secondary" onClick={() => setSelected(null)}>Back to tasks</button>
          <span className="eyebrow">{view === 'pending' ? 'Task review' : 'Task history'}</span>
          <h3>{referenceText(selected)}</h3>
          <p>{[selected.stageName, selected.status || (view === 'pending' ? 'Pending' : ''), selected.actorName ? `By ${selected.actorName}` : '', selected.actionedAt ? `Actioned ${formatTaskDate(selected.actionedAt)}` : ''].filter(Boolean).join(' / ')}</p>
        </div>
        <em className={`workflow-task-status ${statusClass(selected.status || (view === 'pending' ? 'Pending' : ''))}`}>{selected.status || (view === 'pending' ? 'Pending' : '-')}</em>
      </header>
      {message && <p className="form-warning">{message}</p>}
      <section className="workflow-task-meta-grid">
        <article><span>Resource</span><b>{selected.resourceType}</b></article>
        <article><span>Reference</span><b>{selected.resourceId}</b></article>
        <article><span>Received</span><b>{formatTaskDate(selected.createdAt)}</b></article>
        <article><span>Approver</span><b>{selected.approverName || '-'}</b></article>
        {view === 'actioned' && <article><span>Action by</span><b>{selected.actorName || selected.approverName || '-'}</b></article>}
        {view === 'actioned' && <article><span>Actioned on</span><b>{formatTaskDate(selected.actionedAt)}</b></article>}
      </section>
      {selected.resourceType === 'PayRun' ? <>
        {loadingPayRun && <p className="form-warning">Loading payroll review...</p>}
        {payRun ? <PayRunReview selected={payRun} diagnostics={diagnostics} busy={false} materialVarianceCount={materialVarianceCount} actions={false} /> : !loadingPayRun && <p className="empty">Payroll run data is not available.</p>}
        {selected.comment && <section className="workflow-task-remarks"><span>Remarks</span><p>{selected.comment}</p></section>}
        {view === 'pending' && <ApprovalControls remark={remark} setRemark={setRemark} action={action} />}
      </> : selected.resourceType === 'RecruitmentJobDescription' ? <>
        {loadingJobDescription && <p className="form-warning">Loading the job description submitted for review...</p>}
        {jobDescription ? <JobDescriptionReview description={jobDescription} snapshot={approvalSnapshot} /> : !loadingJobDescription && <p className="empty">The submitted job-description details are not available.</p>}
        {selected.comment && <section className="workflow-task-remarks"><span>Remarks</span><p>{selected.comment}</p></section>}
        {view === 'pending' && <ApprovalControls remark={remark} setRemark={setRemark} action={action} />}
      </> : <>
        <section className="workflow-task-detail-table">
          {payloadDetails.map(([key, value]) => <div key={key}><span>{key}</span><b>{value}</b></div>)}
          {selected.actorName && <div><span>Action by</span><b>{selected.actorName}</b></div>}
          {selected.comment && <div><span>Remarks</span><b>{selected.comment}</b></div>}
          {!payloadDetails.length && !selected.comment && <p className="empty">No additional request details were recorded.</p>}
        </section>
        {view === 'pending' && <ApprovalControls remark={remark} setRemark={setRemark} action={action} />}
      </>}
    </section>
  }

  return <section className="card workflow-admin">
    <header className="workflow-task-head"><div><h3>My Tasks</h3><p>Review pending approvals and revisit completed workflow actions.</p></div></header>
    <nav className="workflow-task-tabs" aria-label="Task status tabs">
      <button type="button" className={view === 'pending' ? 'active' : ''} onClick={() => { setView('pending'); setSelected(null); setMessage('') }}>Pending <b>{view === 'pending' ? rows.length : ''}</b></button>
      <button type="button" className={view === 'actioned' ? 'active' : ''} onClick={() => { setView('actioned'); setSelected(null); setMessage('') }}>Actioned <b>{view === 'actioned' ? rows.length : ''}</b></button>
    </nav>
    {message && <p className="form-warning">{message}</p>}
    <div className="workflow-task-table-card">
      <table className="workflow-task-table">
        <thead><tr><th>Resource</th><th>Reference</th><th>Stage</th><th>Received</th>{view === 'actioned' && <th>Actioned on</th>}<th>{view === 'actioned' ? 'Action by' : 'Status'}</th><th>Action</th></tr></thead>
        <tbody>
          {rows.map(row => <tr key={row.id}>
            <td><b>{row.resourceType}</b></td>
            <td><b>{referenceText(row)}</b><small>Task #{row.id}</small></td>
            <td>{row.stageName}</td>
            <td>{formatTaskDate(row.createdAt)}</td>
            {view === 'actioned' && <td>{formatTaskDate(row.actionedAt)}</td>}
            <td>{view === 'actioned' && <small>{row.actorName || row.approverName || '-'}</small>}<em className={`workflow-task-status ${statusClass(row.status || (view === 'pending' ? 'Pending' : ''))}`}>{row.status || (view === 'pending' ? 'Pending' : '-')}</em></td>
            <td><button type="button" onClick={() => openTask(row)}>{view === 'pending' ? 'Review' : 'View'}</button></td>
          </tr>)}
        </tbody>
      </table>
      {!rows.length && <div className="workflow-task-empty"><b>{view === 'pending' ? 'No pending tasks.' : 'No actioned tasks found.'}</b><span>{view === 'pending' ? 'New approvals assigned to you will appear here.' : 'Approved, rejected, and sent-back requests will appear here once workflow action is recorded.'}</span></div>}
    </div>
  </section>
}

function JobDescriptionReview({ description, snapshot }: { description: RecruitmentJobDescriptionVersion; snapshot: JobDescriptionApprovalSnapshot | null }) {
  const requisition = snapshot?.requisition
  const attachments = snapshot?.attachments ?? []
  const mustHaveWeight = description.skills.filter(skill => skill.isRequired).reduce((total, skill) => total + Number(skill.weightPercent || 0), 0)
  const preferredWeight = description.skills.filter(skill => !skill.isRequired).reduce((total, skill) => total + Number(skill.weightPercent || 0), 0)
  const workspaceUrl = `/recruitment/job-descriptions?clientId=${description.clientId}&requisitionId=${description.requisitionId}&jobDescriptionId=${description.id}`
  return <section className="workflow-jd-review" data-testid="workflow-jd-review">
    <header>
      <div>
        <span className="eyebrow">Submitted role profile · Version {description.versionNumber}</span>
        <h4>{description.title || requisition?.positionTitle || `Job description #${description.id}`}</h4>
        <p>{description.summary || 'No role summary was provided.'}</p>
      </div>
      <a className="secondary workflow-jd-link" href={workspaceUrl}>Open JD workspace</a>
    </header>

    <section className="workflow-task-meta-grid workflow-jd-context">
      <article><span>Client</span><b>{snapshot?.client?.name || `Client #${description.clientId}`}</b></article>
      <article><span>Hiring request</span><b>{requisition?.rfrNumber || `Requisition #${description.requisitionId}`}</b></article>
      <article><span>Position</span><b>{snapshot?.position?.positionCode ? `${snapshot.position.positionCode} · ${snapshot.position.title || description.title}` : requisition?.positionTitle || description.title}</b></article>
      <article><span>Department</span><b>{requisition?.department || '-'}</b></article>
      <article><span>Openings</span><b>{requisition?.numberOfOpenings ?? '-'}</b></article>
      <article><span>Experience</span><b>{requisition?.experienceRange || 'Defined per skill below'}</b></article>
      <article><span>Employment</span><b>{[requisition?.hiringType, requisition?.employmentType, requisition?.workMode].filter(Boolean).join(' · ') || '-'}</b></article>
      <article><span>Location</span><b>{requisition?.jobLocation || '-'}</b></article>
      <article><span>Submitted by</span><b>{snapshot?.submittedBy?.displayName || snapshot?.submittedBy?.email || '-'}</b></article>
      <article><span>Snapshot captured</span><b>{formatTaskDate(snapshot?.capturedAtUtc)}</b></article>
    </section>

    <div className="workflow-jd-copy-grid">
      <article><h5>Role purpose</h5><p>{description.rolePurpose || 'Not specified.'}</p></article>
      <article><h5>Responsibilities</h5>{description.responsibilities.length
        ? <ol>{description.responsibilities.map(item => <li key={item.id}>{item.responsibilityText}</li>)}</ol>
        : <p>None recorded.</p>}</article>
    </div>

    <section className="workflow-jd-section">
      <header><div><h5>Skills and ATS scoring</h5><p>Must-have eligibility and preferred ranking evidence are reviewed separately. Relative weights are normalized inside each group.</p></div><b>Must-have {mustHaveWeight ? `${mustHaveWeight}%` : 'equal'} / Preferred {preferredWeight ? `${preferredWeight}%` : 'equal'}</b></header>
      {description.skills.length ? <div className="workflow-jd-table-wrap"><table data-testid="workflow-jd-skill-table"><thead><tr><th>Skill</th><th>Requirement</th><th>Minimum experience</th><th>Proficiency</th><th>ATS weight</th></tr></thead><tbody>
        {description.skills.map(skill => <tr key={skill.id}><td><b>{skill.skillName}</b></td><td>{skill.isRequired ? 'Required' : 'Preferred'}</td><td>{Number(skill.minimumYears || 0)} years</td><td>{skill.minimumProficiency || '-'}</td><td>{Number(skill.weightPercent || 0)}%</td></tr>)}
      </tbody></table></div> : <p className="empty">No skill criteria recorded.</p>}
    </section>

    <div className="workflow-jd-requirement-grid">
      <RequirementList title="Qualifications" empty="No qualifications recorded." items={description.qualifications.map(item => `${item.qualificationName}${item.specialization ? ` · ${item.specialization}` : ''}${item.isMandatory ? ' · Mandatory' : ' · Preferred'}`)} />
      <RequirementList title="Certifications" empty="No certifications recorded." items={description.certifications.map(item => `${item.certificationName}${item.isMandatory ? ' · Mandatory' : ' · Preferred'}`)} />
      <RequirementList title="Languages" empty="No languages recorded." items={description.languages.map(item => `${item.languageName}${item.proficiency ? ` · ${item.proficiency}` : ''}${item.isMandatory ? ' · Mandatory' : ''}`)} />
      <RequirementList title="Benefits" empty="No benefits recorded." items={description.benefits.map(item => `${item.benefitName}${item.description ? ` · ${item.description}` : ''}`)} />
    </div>

    <section className="workflow-jd-section workflow-jd-attachments">
      <header><div><h5>Linked documents</h5><p>Files captured with the requisition, JD or linked work order when approval was submitted.</p></div><b>{attachments.length}</b></header>
      {attachments.length ? <ul>{attachments.map((file, index) => <li key={file.publicId || `${file.fileName}-${index}`}><div><b>{file.fileName || file.fieldLabel || 'Attachment'}</b><span>{file.attachmentType || file.fieldLabel || 'Supporting document'}</span></div><span>v{file.versionNumber || 1} · {formatFileSize(file.fileSizeBytes)} · {file.verificationStatus || 'Not verified'}</span></li>)}</ul>
        : requisition?.sourceDocumentName ? <p>{requisition.sourceDocumentName}{requisition.sourceReference ? ` · ${requisition.sourceReference}` : ''}</p>
          : <p className="empty">No linked files were captured with this approval submission.</p>}
    </section>
  </section>
}

function RequirementList({ title, items, empty }: { title: string; items: string[]; empty: string }) {
  return <article><h5>{title}</h5>{items.length ? <ul>{items.map((item, index) => <li key={`${title}-${index}`}>{item}</li>)}</ul> : <p>{empty}</p>}</article>
}

function formatFileSize(value?: number) {
  if (!value || value <= 0) return 'Size unavailable'
  if (value < 1024 * 1024) return `${Math.max(1, Math.round(value / 1024))} KB`
  return `${(value / (1024 * 1024)).toFixed(1)} MB`
}

function ApprovalControls({ remark, setRemark, action }: { remark: string; setRemark: (value: string) => void; action: (actionName: string) => Promise<void> }) {
  return <>
    <label className="workflow-task-approval-remarks"><span>Remarks</span><textarea value={remark} onChange={event => setRemark(event.target.value)} placeholder="Add approval, rejection, or send-back remarks..." /></label>
    <div className="workflow-review-actions">
      <button type="button" onClick={() => void action('Approved')}>Approve</button>
      <button type="button" className="secondary" onClick={() => void action('Sent Back')}>Send back</button>
      <button type="button" className="danger" onClick={() => void action('Rejected')}>Reject</button>
    </div>
  </>
}
