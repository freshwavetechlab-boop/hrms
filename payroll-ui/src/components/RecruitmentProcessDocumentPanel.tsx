import RecruitmentMomSignature from './RecruitmentMomSignature'
import { useEffect, useState } from 'react'
import { Alert, Button, Empty, Space, Tag } from 'antd'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { generateRecruitmentProcessDocument, getRecruitmentProcessDocuments, saveRecruitmentProcessDocument } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument } from '../types/recruitmentCases'
import type { RecruitmentStageProcessDocumentRequirement } from '../types/recruitmentOrchestration'

type Props = {
  clientId: number
  pipelineStageId: number
  requirements: RecruitmentStageProcessDocumentRequirement[]
  hiringCaseId?: number | null
  applicationId?: number | null
  title?: string
}

export default function RecruitmentProcessDocumentPanel({ clientId, pipelineStageId, requirements, hiringCaseId = null, applicationId = null, title = 'Stage process documents' }: Props) {
  const [documents, setDocuments] = useState<RecruitmentProcessDocument[]>([])
  const [signing, setSigning] = useState<RecruitmentProcessDocument | null>(null)
  const [busy, setBusy] = useState(false)
  const load = async () => {
    const rows = await getRecruitmentProcessDocuments(hiringCaseId, applicationId)
    setDocuments(rows)
    if (signing) setSigning(rows.find(row => row.id === signing.id) || null)
  }
  useEffect(() => { void load() }, [hiringCaseId, applicationId, pipelineStageId])

  const prepare = async (requirement: RecruitmentStageProcessDocumentRequirement) => {
    setBusy(true)
    const response = await saveRecruitmentProcessDocument({ id: 0, clientId, hiringCaseId, applicationId, interviewId: null, pipelineStageId, documentType: requirement.documentType, templateId: requirement.templateId || null, attachmentPublicId: null, status: 'Draft', workflowInstanceId: null })
    setBusy(false)
    if (response.ok) await load()
  }
  const sign = async (document: RecruitmentProcessDocument) => {
    setBusy(true)
    const response = await saveRecruitmentProcessDocument({ ...document, status: 'Signed' })
    setBusy(false)
    if (response.ok) await load()
  }
  const generate = async (document: RecruitmentProcessDocument) => {
    setBusy(true)
    const response = await generateRecruitmentProcessDocument(document.id)
    setBusy(false)
    if (response.ok) await load()
  }

  if (!requirements.length) return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="This stage has no process-document requirement." />
  return <section className="recruitment-process-documents">
    <Alert showIcon type="info" message={title} description="Prepare creates a draft record, not a file. Upload the relevant document, or generate a PDF when a template is configured. MoM requires signatures from all assigned panel members." />
    {requirements.map(requirement => {
      const document = documents.find(row => row.pipelineStageId === pipelineStageId && row.documentType === requirement.documentType)
      const isMom = ['MOM', 'SIGNED_MOM'].includes(requirement.documentType)
      return <article key={requirement.id} data-testid={`process-document-${applicationId || hiringCaseId}-${requirement.documentType}`}>
        <header><div><b>{requirement.documentType.replaceAll('_', ' ')}</b><span>{requirement.isRequired ? 'Required' : 'Optional'}{requirement.requiresSignature ? ' · final signature required' : ''}</span></div><Space>
          {document ? <Tag color={document.status === 'Signed' ? 'green' : 'blue'}>v{document.versionNumber} · {document.status}</Tag> : <Button loading={busy} onClick={() => void prepare(requirement)}>Prepare</Button>}
          {document && requirement.templateId && document.status !== 'Signed' && <Button loading={busy} onClick={() => void generate(document)}>{document.attachmentPublicId ? 'Regenerate PDF' : 'Generate PDF'}</Button>}
          {document && !isMom && requirement.requiresSignature && document.status !== 'Signed' && document.hasFinalSignedAttachment && <Button loading={busy} onClick={() => void sign(document)}>Mark signed</Button>}
          {document && requirement.requiresSignature && document.status !== 'Signed' && !document.hasFinalSignedAttachment && <Tag color="orange">Upload signed final</Tag>}
          {document && isMom && <><Tag>{document.signatureCount}/{document.requiredSignatureCount} panel signatures</Tag><Button onClick={() => setSigning(document)}>{document.status === 'Signed' ? 'View signatures' : 'Sign MoM'}</Button></>}
        </Space></header>
        {document && <EntityAttachmentPanel entityType="RECRUITMENT_PROCESS_DOCUMENT" entityId={document.id} clientId={clientId} moduleCode="RECRUITMENT" formCodes={['PROCESS_DOCUMENT']} title="Document file" description="Private, versioned storage with secure preview and download." singleFieldLabel={requirement.documentType.replaceAll('_', ' ')} singleFieldHelp={isMom ? 'A PDF upload does not replace the required panel signatures.' : requirement.requiresSignature ? 'Upload the final signed copy. Mark signed becomes available after a separately uploaded final file is linked.' : 'Upload the source document for this requirement. Use Generate PDF only when a template is configured.'} onChanged={() => void load()} />}
      </article>
    })}
    {signing && <RecruitmentMomSignature key={signing.id} document={signing} onClose={() => setSigning(null)} onSaved={load} />}
  </section>
}
