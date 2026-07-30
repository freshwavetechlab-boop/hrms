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
  const [busy, setBusy] = useState(false)
  const load = async () => setDocuments(await getRecruitmentProcessDocuments(hiringCaseId, applicationId))
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
    <Alert showIcon type="info" message={title} description="Generate the draft, obtain the committee signatures, then replace it with the final signed file. Only that separately uploaded file can be marked signed." />
    {requirements.map(requirement => {
      const document = documents.find(row => row.pipelineStageId === pipelineStageId && row.documentType === requirement.documentType)
      return <article key={requirement.id} data-testid={`process-document-${applicationId || hiringCaseId}-${requirement.documentType}`}>
        <header><div><b>{requirement.documentType.replaceAll('_', ' ')}</b><span>{requirement.isRequired ? 'Required' : 'Optional'}{requirement.requiresSignature ? ' · final signature required' : ''}</span></div><Space>
          {document ? <Tag color={document.status === 'Signed' ? 'green' : 'blue'}>v{document.versionNumber} · {document.status}</Tag> : <Button loading={busy} onClick={() => void prepare(requirement)}>Prepare</Button>}
          {document && requirement.templateId && document.status !== 'Signed' && <Button loading={busy} onClick={() => void generate(document)}>{document.attachmentPublicId ? 'Regenerate PDF' : 'Generate PDF'}</Button>}
          {document && requirement.requiresSignature && document.status !== 'Signed' && document.hasFinalSignedAttachment && <Button loading={busy} onClick={() => void sign(document)}>Mark signed</Button>}
          {document && requirement.requiresSignature && document.status !== 'Signed' && !document.hasFinalSignedAttachment && <Tag color="orange">Upload signed final</Tag>}
        </Space></header>
        {document && <EntityAttachmentPanel entityType="RECRUITMENT_PROCESS_DOCUMENT" entityId={document.id} clientId={clientId} moduleCode="RECRUITMENT" formCodes={['PROCESS_DOCUMENT']} title={`${requirement.documentType.replaceAll('_', ' ')} attachment`} description="Versioned, access-controlled and stored through the active attachment mount." onChanged={() => void load()} />}
      </article>
    })}
  </section>
}
