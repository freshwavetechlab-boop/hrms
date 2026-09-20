import { useState } from 'react'
import { Alert, Button, Modal } from 'antd'
import { postJson } from '../services/apiClient'
import type { PublicCandidateApplicationTracker } from '../types/recruitmentOrchestration'
import { useAuthSession } from './AuthGate'
import CandidateTrackerStatus from './CandidateTrackerStatus'

export default function CandidateTrackingPreview({ applicationId }: { applicationId: number }) {
  const session = useAuthSession()
  const [busy, setBusy] = useState(false)
  const [tracker, setTracker] = useState<PublicCandidateApplicationTracker | null>(null)
  if (session?.user.clientId != null || !session?.user.roles.includes('super_admin')) return null
  const load = async () => {
    setBusy(true)
    try {
      const result = await postJson(`/api/recruitment/applications/${applicationId}/view-as-candidate`, {}, null as PublicCandidateApplicationTracker | null)
      if (result.ok) setTracker(result.data)
    } finally { setBusy(false) }
  }
  return <><Button size="small" loading={busy} onClick={() => void load()}>View as candidate</Button>
    <Modal open={!!tracker} width="min(1000px, 94vw)" title="Candidate view — read-only" footer={null} onCancel={() => setTracker(null)} destroyOnClose>
      <Alert type="info" showIcon message="Super-admin preview is audited. The candidate's password and sessions are unchanged." style={{ marginBottom: 16 }} />
      {tracker && <CandidateTrackerStatus tracker={tracker} />}
    </Modal></>
}
