import { useState } from 'react'
import { Alert, Button, Form, Input, InputNumber, Modal, Select, Space, Switch, Typography } from 'antd'
import { getJsonResult, postJson } from '../services/apiClient'
import { getRecruitmentApplicationTransitions, transitionRecruitmentApplicationSilently } from '../services/recruitmentOrchestrationService'
import type { RecruitmentPipelineTransition } from '../types/recruitmentOrchestration'
import { useAuthSession } from './AuthGate'

type Terms = {
  applicationId: number; expectedCtc?: number; approvedBudget: number; budgetBasis: string; currency: string
  negotiationOverride: boolean | null; required: boolean; agreedCtc?: number; termsConfirmedAtUtc?: string
  approvalStatus?: string; reviewComment?: string
}

export default function RecruitmentNegotiationAction({ applicationId, onSaved }: { applicationId: number; onSaved: () => void | Promise<void> }) {
  const session = useAuthSession()
  const [terms, setTerms] = useState<Terms | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [rejecting, setRejecting] = useState(false)
  const [reason, setReason] = useState('')
  const [rejectRoutes, setRejectRoutes] = useState<RecruitmentPipelineTransition[]>([])
  const [rejectRoute, setRejectRoute] = useState<number>()
  const canReject = session?.user.permissions.some(code => ['settings.manage', 'recruitment.manage'].includes(code))
  const allowed = session?.user.permissions.some(code => ['settings.manage', 'recruitment.manage', 'recruitment.offer.manage', 'recruitment.proposal.manage'].includes(code))
    || session?.user.roles.includes('super_admin')
  if (!allowed) return null
  const url = `/api/recruitment/applications/${applicationId}/negotiation`
  const open = async () => {
    setBusy(true); setError(''); setRejecting(false); setReason('')
    try {
      const response = await getJsonResult<Terms | null>(url, null)
      if (response.ok && response.data) setTerms({ ...response.data, agreedCtc: response.data.agreedCtc ?? response.data.expectedCtc })
    } finally { setBusy(false) }
  }
  const save = async (confirmTerms: boolean) => {
    if (!terms) return
    setBusy(true); setError('')
    try {
      const response = await postJson(url, { negotiationOverride: terms.negotiationOverride, agreedCtc: terms.agreedCtc, confirmTerms }, null as Terms | null, { toast: 'error-only' })
      if (!response.ok) { setError(response.error); return }
      await onSaved(); setTerms(null)
    } finally { setBusy(false) }
  }
  const reject = async () => {
    if (!rejectRoute || !reason.trim()) return
    setBusy(true); setError('')
    try {
      const response = await transitionRecruitmentApplicationSilently(applicationId, rejectRoute, reason.trim())
      if (!response.ok) { setError(response.error || 'Candidate could not be rejected.'); return }
      await onSaved(); setTerms(null)
    } finally { setBusy(false) }
  }
  const openRejection = async () => {
    setBusy(true); setError('')
    try {
      const routes = (await getRecruitmentApplicationTransitions(applicationId)).filter(row => /reject/i.test(row.outcomeCode) || /reject/i.test(row.toStageCode))
      setRejectRoutes(routes); setRejectRoute(routes[0]?.id); setRejecting(true)
      if (!routes.length) setError('No rejection route is configured from this candidate’s current stage. Configure it in the candidate pipeline.')
    } finally { setBusy(false) }
  }
  return <><Button size="small" loading={busy && !terms} onClick={event => { event.stopPropagation(); void open() }}>Negotiation / MoM</Button>
    <Modal title={rejecting ? 'Reject candidate' : 'Candidate negotiation & agreed terms'} open={!!terms} onCancel={() => !busy && setTerms(null)} footer={<Space wrap>
      <Button disabled={busy} onClick={() => setTerms(null)}>Cancel</Button>
      {rejecting ? <><Button disabled={busy} onClick={() => { setRejecting(false); setError('') }}>Back to negotiation</Button><Button danger type="primary" loading={busy} disabled={!rejectRoute || !reason.trim()} onClick={() => void reject()}>Confirm rejection</Button></> : <>
      {canReject && <Button danger disabled={busy} onClick={() => void openRejection()}>Reject candidate</Button>}
      <Button loading={busy} onClick={() => void save(false)}>Save</Button>
      <Button type="primary" loading={busy} disabled={!terms?.agreedCtc} onClick={() => void save(true)}>Confirm terms & prepare MoM</Button>
      </>}
    </Space>}>
      {terms && <Form layout="vertical" disabled={busy}>
        {rejecting ? <><Alert showIcon type="warning" message="This rejects the candidate’s application for this job." description="The reason and pipeline history are retained. Other job applications are unaffected." />
          {rejectRoutes.length > 1 && <Form.Item label="Rejection route" required><Select value={rejectRoute} options={rejectRoutes.map(row => ({ value: row.id, label: row.actionLabel }))} onChange={setRejectRoute} /></Form.Item>}
          <Form.Item label="Rejection reason" required><Input.TextArea rows={3} value={reason} onChange={event => setReason(event.target.value)} /></Form.Item>
        </> : <>
        {terms.approvalStatus && <Typography.Paragraph>{terms.approvalStatus}</Typography.Paragraph>}
        {terms.reviewComment && <Alert showIcon type="warning" message="HR returned these terms" description={terms.reviewComment} style={{ marginBottom: 12 }} />}
        <Typography.Paragraph>Expected CTC: {terms.currency} {Number(terms.expectedCtc || 0).toLocaleString('en-IN')} · Approved limit: {terms.approvedBudget > 0 ? `${terms.currency} ${terms.approvedBudget.toLocaleString('en-IN')}` : 'Not configured'}</Typography.Paragraph>
        <Form.Item label="Negotiation required" extra={terms.negotiationOverride == null ? 'Following the job’s configured budget rule.' : 'Manual choice saved for this application.'}>
          <Space><Switch checked={terms.negotiationOverride ?? (Number(terms.expectedCtc) > terms.approvedBudget && terms.approvedBudget > 0)} onChange={value => setTerms({ ...terms, negotiationOverride: value })} checkedChildren="ON" unCheckedChildren="OFF" />
            <Button type="link" onClick={() => setTerms({ ...terms, negotiationOverride: null })}>Use job rule</Button></Space>
        </Form.Item>
        <Form.Item label={(terms.negotiationOverride ?? (Number(terms.expectedCtc) > terms.approvedBudget && terms.approvedBudget > 0)) ? 'Negotiated annual CTC' : 'Agreed annual CTC'} required>
          <InputNumber min={1} max={1000000000} value={terms.agreedCtc} onChange={value => setTerms({ ...terms, agreedCtc: value ?? undefined })} style={{ width: '100%' }} />
        </Form.Item>
        <Alert type="info" showIcon message="Confirm only after the candidate agrees to these terms." description="The assigned interview panel signs the prepared MoM, then it goes to HR Division for approval. Revised terms require fresh panel signatures and approval." />
        </>}
        {error && <Alert type="error" showIcon message={error} style={{ marginTop: 12 }} />}
      </Form>}
    </Modal></>
}
