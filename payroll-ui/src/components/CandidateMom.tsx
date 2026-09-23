import { useEffect, useState } from 'react'
import { Alert, Button, Card, Checkbox, Input, Space, Tag } from 'antd'
import { getJsonResult, postJson } from '../services/apiClient'

type Mom = { id: number; versionNumber: number; termsVersion: number; bodySnapshot: string; status: string; approvalStatus: string; reviewComment?: string }

export default function CandidateMom({ token, applicationId, positionTitle }: { token: string; applicationId: number; positionTitle: string }) {
  const [mom, setMom] = useState<Mom | null>(null)
  const [name, setName] = useState('')
  const [consent, setConsent] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const url = `/api/public/recruitment/tracking/${encodeURIComponent(token)}/applications/${applicationId}/mom`
  useEffect(() => {
    let active = true
    void getJsonResult<Mom | null>(url, null).then(result => { if (active && result.ok) setMom(result.data) })
    return () => { active = false }
  }, [url])
  if (!mom) return null
  const sign = async () => {
    setBusy(true); setError('')
    try {
      const result = await postJson(`${url}/${mom.id}/sign`, { signerName: name.trim(), termsVersion: mom.termsVersion, consent }, null as Mom | null, { toast: 'error-only' })
      if (result.ok) setMom(result.data)
      else setError(result.error)
    } finally { setBusy(false) }
  }
  return <Card title={`MoM: ${positionTitle}`} extra={<Tag>{mom.approvalStatus}</Tag>} style={{ marginTop: 16 }}>
    <small>Document version {mom.versionNumber}</small>
    <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit', maxHeight: 420, overflow: 'auto' }}>{mom.bodySnapshot}</pre>
    {mom.reviewComment && <Alert showIcon type="warning" message={mom.reviewComment} />}
    {mom.status === 'Signed' ? <Alert showIcon type="success" message="Your signature has been recorded. HR will review these terms." /> : <Space direction="vertical" style={{ width: '100%' }}>
      <Input aria-label="Full name for MoM signature" placeholder="Enter your full name" maxLength={180} value={name} onChange={event => setName(event.target.value)} />
      <Checkbox checked={consent} onChange={event => setConsent(event.target.checked)}>I have reviewed these terms and agree to sign this MoM using my name.</Checkbox>
      <Button type="primary" loading={busy} disabled={!consent || !name.trim()} onClick={() => void sign()}>Sign MoM & send to HR</Button>
    </Space>}
    {error && <Alert type="error" showIcon message={error} style={{ marginTop: 12 }} />}
  </Card>
}
