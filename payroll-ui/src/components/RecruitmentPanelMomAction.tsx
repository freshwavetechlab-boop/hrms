import { useState } from 'react'
import { Alert, Button, Modal, Space, Tag } from 'antd'
import { getRecruitmentProcessDocuments } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument } from '../types/recruitmentCases'
import RecruitmentMomSignature from './RecruitmentMomSignature'

export default function RecruitmentPanelMomAction({ applicationId }: { applicationId: number }) {
  const [documents, setDocuments] = useState<RecruitmentProcessDocument[] | null>(null)
  const [signing, setSigning] = useState<RecruitmentProcessDocument | null>(null)
  const [loading, setLoading] = useState(false)
  const load = async () => {
    setLoading(true)
    try {
      const rows = (await getRecruitmentProcessDocuments(null, applicationId)).filter(row => row.documentType === 'MOM' && row.isCurrentJobMom)
      setDocuments(rows.slice(0, 1))
      if (signing) setSigning(rows.find(row => row.id === signing.id) || null)
    } finally { setLoading(false) }
  }
  return <><Button size="small" loading={loading} onClick={() => void load()}>Job MoM</Button>
    <Modal title="Job MoM · Panel signatures" open={documents !== null && !signing} onCancel={() => setDocuments(null)} footer={null}>
      {!documents?.length && <Alert showIcon type="info" message="MoM is not prepared yet." description="HR confirms individual terms for the selected candidates, then prepares one combined job MoM in MoM & Negotiation." />}
      {documents?.map(row => <Space key={row.id} wrap><Tag>MoM v{row.versionNumber}</Tag><Tag>{row.signatureCount}/{row.requiredSignatureCount} panel signatures</Tag><Button onClick={() => setSigning(row)}>{row.status === 'Signed' ? 'View signed MoM' : 'Review & sign MoM'}</Button></Space>)}
    </Modal>
    {signing && <RecruitmentMomSignature key={signing.id} document={signing} onClose={() => setSigning(null)} onSaved={load} />}
  </>
}
