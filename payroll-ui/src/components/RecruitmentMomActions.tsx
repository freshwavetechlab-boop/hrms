import { useEffect, useState } from 'react'
import { Alert, Button, Space } from 'antd'
import { useNavigate } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import RecruitmentMomSignature from './RecruitmentMomSignature'
import { generateRecruitmentProcessDocument, getRecruitmentMomReadiness, saveRecruitmentProcessDocument, type RecruitmentMomReadiness } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument } from '../types/recruitmentCases'

type Props = {
  document?: RecruitmentProcessDocument
  clientId: number
  hiringCaseId?: number | null
  pipelineStageId: number
  documentType: string
  templateId?: number | null
  onChanged: () => Promise<void>
}

// The same next action is used in the job drawer and candidate stage documents.
export default function RecruitmentMomActions({ document, clientId, hiringCaseId, pipelineStageId, documentType, templateId, onChanged }: Props) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const [readiness, setReadiness] = useState<RecruitmentMomReadiness | null>(null)
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [signing, setSigning] = useState(false)
  const has = (...permissions: string[]) => Boolean(session?.user.permissions.some(p => ['settings.manage', 'recruitment.manage', ...permissions].includes(p)))
  const canManage = has('recruitment.document.manage')
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
    setReadiness(null); setError('')
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
  const query = `clientId=${clientId}`
  const canPrepare = canManage && (hiringCaseId || document?.hiringCaseId) && (!document || readiness?.canGenerate)
  const description = error || readiness?.message || (loading ? 'Checking the next step…' : document
    ? 'Refresh to check the next step.'
    : 'Prepare one MoM from this job’s selected candidates, confirmed terms and interview panel. The standard format is used when no custom template is assigned.')
  return <div className="recruitment-mom-next-action" data-testid="mom-next-action">
    <Alert showIcon type={error ? 'error' : action === 'Offers' ? 'success' : 'info'} message="Next step" description={description} />
    <Space wrap style={{ marginTop: 10 }}>
      {canPrepare && <Button type="primary" loading={busy} disabled={loading} onClick={() => void prepare()}>Prepare MoM</Button>}
      {action === 'Revise' && canManage && <Button type="primary" loading={busy} onClick={() => void prepare(true)}>Prepare new MoM version</Button>}
      {document && action === 'Sign' && <Button type={readiness?.canSign ? 'primary' : 'default'} onClick={() => setSigning(true)}>{readiness?.canSign ? 'Sign MoM' : 'View MoM & signatures'}</Button>}
      {action === 'Terms' && has('recruitment.proposal.manage', 'recruitment.offer.manage') && <Button type="primary" onClick={() => navigate(`/recruitment/mom-and-negotiation?section=negotiation&${query}`)}>Confirm agreed terms</Button>}
      {action === 'Interview' && has('recruitment.interview.panel', 'recruitment.interview.schedule') && <Button type="primary" onClick={() => navigate(`/recruitment/interviews?${query}`)}>Open Interview Tracker</Button>}
      {(action === 'Approval' || action === 'Revise') && <Button onClick={() => navigate('/tasks')}>Open My Tasks</Button>}
      {action === 'Offers' && has('recruitment.offer.manage', 'recruitment.offer.issue') && <Button type="primary" onClick={() => navigate(`/recruitment/offers-and-pre-onboarding?${query}`)}>Open Offers & Pre-boarding</Button>}
      {(action === 'Template' || action === 'ApprovalSetup') && has('recruitment.pipeline.manage') && <Button onClick={() => navigate(`/recruitment/hiring-pipeline?manage=1&${query}`)}>Open pipeline settings</Button>}
      {action === 'Job' && has('recruitment.work-order.manage') && <Button onClick={() => navigate(`/recruitment/work-orders-and-sla?${query}&hiringCaseId=${hiringCaseId || document?.hiringCaseId || ''}`)}>Open hiring journey</Button>}
      {!document && !hiringCaseId && <Button onClick={() => navigate(`/recruitment/mom-and-negotiation?${query}`)}>Open job MoM</Button>}
      {document && <Button loading={loading} disabled={busy} onClick={() => void load()}>Refresh status</Button>}
    </Space>
    {!canManage && (!document || action === 'Generate' || action === 'Revise') && <p>The hiring coordinator prepares the MoM. Assigned panel members then sign using their own logins.</p>}
    {action === 'Interview' && !has('recruitment.interview.panel', 'recruitment.interview.schedule') && <p>The assigned interview panel completes this step in Interview Tracker using their own logins.</p>}
    {signing && document && <RecruitmentMomSignature document={document} readOnly={!readiness?.canSign} onClose={() => setSigning(false)} onSaved={async () => { await onChanged(); await load() }} />}
  </div>
}
