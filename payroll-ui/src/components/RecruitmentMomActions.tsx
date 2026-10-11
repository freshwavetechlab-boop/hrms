import { useEffect, useState } from 'react'
import { Alert, Button, Collapse, Space } from 'antd'
import { useNavigate } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import RecruitmentMomSignature from './RecruitmentMomSignature'
import RecruitmentMomDetails, { momWorkKey } from './RecruitmentMomDetails'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { generateRecruitmentProcessDocument, getRecruitmentMomReadiness, pingRecruitmentMomWork, saveRecruitmentProcessDocument, type RecruitmentMomReadiness, type RecruitmentMomWorkOwner } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument } from '../types/recruitmentCases'

type Props = {
  document?: RecruitmentProcessDocument
  clientId: number
  hiringCaseId?: number | null
  pipelineStageId: number
  documentType: string
  templateId?: number | null
  job?: { jobName: string; jobStatus: string; workOrderNumber: string }
  onChanged: () => Promise<void>
}

// The same next action is used in the job drawer and candidate stage documents.
export default function RecruitmentMomActions({ document, clientId, hiringCaseId, pipelineStageId, documentType, templateId, job, onChanged }: Props) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const [readiness, setReadiness] = useState<RecruitmentMomReadiness | null>(null)
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [signing, setSigning] = useState(false)
  const [pinging, setPinging] = useState('')
  const [sentPings, setSentPings] = useState(new Set<string>())
  const has = (...permissions: string[]) => Boolean(session?.user.permissions.some(p => ['settings.manage', 'recruitment.manage', ...permissions].includes(p)))
  const canManage = has('recruitment.document.manage')
  const canManageTemplate = Boolean(session?.user.permissions.some(p => ['settings.manage', 'recruitment.manage'].includes(p)))
  const load = async () => {
    if (!document) return
    setLoading(true)
    try {
      const response = await getRecruitmentMomReadiness(document.id)
      setReadiness(response.data)
      setError(response.ok ? '' : 'Unable to check the next step. Refresh to retry.')
    } finally { setLoading(false) }
  }
  useEffect(() => {
    let active = true
    setReadiness(null); setError(''); setSentPings(new Set())
    if (!document) return
    setLoading(true)
    void getRecruitmentMomReadiness(document.id).then(response => {
      if (!active) return
      setReadiness(response.data)
      setError(response.ok ? '' : 'Unable to check the next step. Refresh to retry.')
      setLoading(false)
    })
    return () => { active = false }
  }, [document?.id, document?.updatedAtUtc, document?.signatureCount, document?.workflowInstanceId])

  const prepare = async (newVersion = false) => {
    if (!canManage) return
    setBusy(true); setError('')
    try {
      let draft = document
      if (!draft || newVersion) {
        const caseId = hiringCaseId || document?.hiringCaseId
        if (!caseId) return
        const response = await saveRecruitmentProcessDocument({ id: 0, clientId, hiringCaseId: caseId, applicationId: null, interviewId: null, pipelineStageId, documentType, templateId: templateId || null, attachmentPublicId: null, status: 'Draft', workflowInstanceId: null })
        if (!response.ok || !response.data) { setError(response.error); return }
        draft = response.data
      }
      const response = await generateRecruitmentProcessDocument(draft.id)
      if (!response.ok) setError(response.error)
      await onChanged()
      // Refresh even after a failed attempt: show the actual missing work beside its action.
      const next = await getRecruitmentMomReadiness(draft.id)
      setReadiness(next.data)
    } finally { setBusy(false) }
  }

  const action = readiness?.action
  const ping = async (owner: RecruitmentMomWorkOwner) => {
    if (!document || pinging || !readiness?.canPing) return
    setPinging(momWorkKey(owner)); setError('')
    try {
      const response = await pingRecruitmentMomWork(document.id, owner)
      if (response.ok) { setSentPings(current => new Set(current).add(momWorkKey(owner))); window.dispatchEvent(new Event('hrms:actions-changed')) }
      else { setError(response.error); await load() }
    } finally { setPinging('') }
  }
  const query = `clientId=${clientId}`
  const canPrepare = canManage && (hiringCaseId || document?.hiringCaseId) && (!document || readiness?.canGenerate)
  const description = error || readiness?.message || (loading ? 'Checking the next step…' : document
    ? 'Refresh to check the next step.'
    : 'Prepare one MoM from this job’s selected candidates, confirmed terms and interview panel. The standard format is used when no custom template is assigned.')
  return <div className="recruitment-mom-next-action" data-testid="mom-next-action">
    <RecruitmentMomDetails readiness={readiness} job={job} onPing={owner => void ping(owner)} pinging={pinging} sentPings={sentPings} />
    <Alert showIcon type={error ? 'error' : action === 'Offers' ? 'success' : 'info'} message="Next step" description={description} />
    <Space wrap style={{ marginTop: 10 }}>
      {canPrepare && <Button type="primary" loading={busy} disabled={loading} onClick={() => void prepare()}>Prepare MoM</Button>}
      {action === 'Revise' && canManage && <Button type="primary" loading={busy} onClick={() => void prepare(true)}>Prepare new MoM version</Button>}
      {document?.bodySnapshot && <Button type={action === 'Sign' && readiness?.canSign ? 'primary' : 'default'} onClick={() => setSigning(true)}>{action === 'Sign' && readiness?.canSign ? 'Sign MoM' : 'View MoM & signatures'}</Button>}
      {action === 'Terms' && has('recruitment.proposal.manage', 'recruitment.offer.manage') && <Button type="primary" onClick={() => navigate(`/recruitment/mom-and-negotiation?section=negotiation&${query}`)}>Confirm agreed terms</Button>}
      {action === 'Interview' && has('recruitment.interview.panel', 'recruitment.interview.schedule') && <Button type="primary" onClick={() => navigate(`/recruitment/interviews?${query}`)}>Open Interview Tracker</Button>}
      {(action === 'Approval' || action === 'Revise') && <Button onClick={() => navigate('/tasks')}>Open My Tasks</Button>}
      {action === 'Offers' && has('recruitment.offer.manage', 'recruitment.offer.issue') && <Button type="primary" onClick={() => navigate(`/recruitment/offers-and-pre-onboarding?${query}`)}>Open Offers & Pre-boarding</Button>}
      {(action === 'Template' || action === 'ApprovalSetup') && has('recruitment.pipeline.manage') && <Button onClick={() => navigate(`/recruitment/hiring-pipeline?manage=1&${query}`)}>Open pipeline settings</Button>}
      {action === 'Job' && has('recruitment.work-order.manage') && <Button onClick={() => navigate(`/recruitment/work-orders-and-sla?${query}&hiringCaseId=${hiringCaseId || document?.hiringCaseId || ''}`)}>Open hiring journey</Button>}
      {!document && !hiringCaseId && <Button onClick={() => navigate(`/recruitment/mom-and-negotiation?${query}`)}>Open job MoM</Button>}
      {document && <Button loading={loading} disabled={busy} onClick={() => void load()}>Refresh status</Button>}
      {canManageTemplate && <Button onClick={() => navigate(`/recruitment/hiring-pipeline?manage=1&tool=templates&${query}&${document?.templateId || templateId ? `templateId=${document?.templateId || templateId}` : 'newTemplate=MoM'}`)}>Edit MoM template</Button>}
    </Space>
    <Collapse size="small" style={{ marginTop: 12 }}>
      <Collapse.Panel key="supporting" header="MoM supporting attachments">
        {document ? <EntityAttachmentPanel entityType="RECRUITMENT_PROCESS_SUPPORT" entityId={document.id} clientId={clientId} moduleCode="RECRUITMENT" formCodes={['PROCESS_SUPPORT']} title="Additional attachments" description="Optional files accompanying this MoM, stored through the global attachment service." readOnly={!has('recruitment.document.manage', 'attachment.recruitment.upload')} onChanged={() => void onChanged()} /> : <p>Prepare the MoM first to add supporting attachments.</p>}
      </Collapse.Panel>
    </Collapse>
    {!canManage && (!document || action === 'Generate' || action === 'Revise') && <p>The hiring coordinator prepares the MoM. Assigned panel members then sign using their own logins.</p>}
    {action === 'Interview' && !has('recruitment.interview.panel', 'recruitment.interview.schedule') && <p>The assigned interview panel completes this step in Interview Tracker using their own logins.</p>}
    {signing && document && <RecruitmentMomSignature document={document} readiness={readiness} readOnly={action !== 'Sign' || !readiness?.canSign} onClose={() => setSigning(false)} onSaved={async () => { await onChanged(); await load() }} />}
  </div>
}
