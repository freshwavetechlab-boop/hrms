import { useState } from 'react'
import { Alert, Button, Form, InputNumber, Modal, Space, Switch, Typography } from 'antd'
import { getJsonResult, postJson } from '../services/apiClient'
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
  const allowed = session?.user.permissions.some(code => ['settings.manage', 'recruitment.manage', 'recruitment.offer.manage', 'recruitment.proposal.manage'].includes(code))
    || session?.user.roles.includes('super_admin')
  if (!allowed) return null
  const url = `/api/recruitment/applications/${applicationId}/negotiation`
  const open = async () => {
    setBusy(true); setError('')
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
  return <><Button size="small" loading={busy && !terms} onClick={event => { event.stopPropagation(); void open() }}>Negotiation / MoM</Button>
    <Modal title="Candidate negotiation & agreed terms" open={!!terms} onCancel={() => !busy && setTerms(null)} footer={<Space>
      <Button disabled={busy} onClick={() => setTerms(null)}>Cancel</Button>
      <Button loading={busy} onClick={() => void save(false)}>Save</Button>
      <Button type="primary" loading={busy} disabled={!terms?.agreedCtc} onClick={() => void save(true)}>Confirm terms & prepare MoM</Button>
    </Space>}>
      {terms && <Form layout="vertical">
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
        <Alert type="info" showIcon message="Confirm only after the candidate agrees to these terms." description="After final interview selection, the candidate signs the prepared MoM through their existing job application login. Revised terms require a fresh signature and approval." />
        {error && <Alert type="error" showIcon message={error} style={{ marginTop: 12 }} />}
      </Form>}
    </Modal></>
}
