import { useState } from 'react'
import { Button, Space, Tag, Typography } from 'antd'
import type { RecruitmentOffer } from '../types/payroll'
import { postJson } from '../services/apiClient'
import OfferLetterPreview from './OfferLetterPreview'

export default function FinalOfferActions({ offer, reload }: { offer: RecruitmentOffer; reload: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  if (offer.status !== 'Accepted' || (!offer.canRequestFinalOffer && !offer.finalApprovalWorkflowInstanceId)) return null
  const status = offer.finalApprovalStatus || 'Not requested'
  const request = async () => {
    setBusy(true)
    try {
      const result = await postJson(`/api/recruitment/offers/${offer.id}/final-letter`, {}, null as RecruitmentOffer | null)
      if (result.ok) { window.dispatchEvent(new Event('hrms:actions-changed')); await reload() }
    } finally { setBusy(false) }
  }
  return <Space direction="vertical" size={4}>
    {offer.finalOfferLetterAttachmentPublicId
      ? <OfferLetterPreview publicId={offer.finalOfferLetterAttachmentPublicId} title={`${offer.candidateName} · Final signed offer`} label="Final signed letter" />
      : <>
        <Tag color={status === 'Pending' ? 'gold' : 'default'}>Final letter: {status === 'Pending' ? 'Departmental approval pending' : status}</Tag>
        {status !== 'Pending' && <Button size="small" loading={busy} onClick={() => void request()}>{status === 'Approved' ? 'Retry final PDF' : 'Request final approval'}</Button>}
        {offer.finalOfferError && <Typography.Text type="danger" style={{ maxWidth: 320, whiteSpace: 'normal' }}>{offer.finalOfferError}</Typography.Text>}
      </>}
  </Space>
}
