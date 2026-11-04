import { Alert, Button, Form, Input } from 'antd'
import type { RecruitmentTemplate } from '../types/payroll'
import FileDropZone from './FileDropZone'
import { documentLayoutMarker, readDocumentLayout } from '../utils/recruitmentDocumentLayout'

export default function RecruitmentTemplateBody({ template, onChange, onError }: { template: RecruitmentTemplate; onChange: (value: RecruitmentTemplate) => void; onError: (message: string) => void }) {
  const layout = template.bodyTemplate.trimStart().startsWith(documentLayoutMarker)
  const structured = (() => { try { return layout ? readDocumentLayout(template.bodyTemplate) : null } catch { return null } })()
  const textBody = <Form.Item label={layout ? 'Layout JSON (advanced)' : 'Body'} extra={template.templateType === 'Offer Letter' && !layout ? 'Supported placeholders: {{candidateName}}, {{candidateFirstName}}, {{positionTitle}}, {{clientName}}, {{currency}}, {{formattedCtc}}, {{joiningDate}}, {{expiryDate}}, {{offerDate}}, {{offerNumber}}, {{remarks}}.' : ['Process Document', 'MoM', 'Score Annexure', 'HR Proposal', 'Joining Intimation', 'Candidate Pack'].includes(template.templateType) ? 'Use {{name}} or #NAME#. Selection MoM also supports: approvalDate, shortlistedCount, presentCount, panelMembersList, candidateAttendanceTable, candidateResultTable, scoreAnnexureTable and panelSignatureBlock.' : undefined}><Input.TextArea rows={8} aria-label={layout ? 'Document layout JSON' : undefined} value={template.bodyTemplate} onChange={e => onChange({ ...template, bodyTemplate: e.target.value })} /></Form.Item>
  return <>
    {template.templateType === 'Offer Letter' && <Form.Item label="Structured PDF layout (optional)" extra="Upload a reusable layout to retain tables, letterhead, page breaks and annexures. Text templates keep their existing rendering."><FileDropZone accept=".json" title="Upload document layout" hint="Version 1 layout JSON, up to 64 KB. Inputs and source-group presets are configured in the layout." onFile={async file => {
      try { const result = readDocumentLayout(await file.text()); onChange({ ...template, bodyTemplate: result.body, isHtml: false }); onError('') }
      catch (e) { onError(e instanceof Error ? e.message : 'Unable to read the document layout.') }
    }} /></Form.Item>}
    {layout ? <><Alert type={structured ? 'success' : 'warning'} showIcon message={structured ? `${structured.pages} configured pages · ${structured.fields.length} input fields` : 'Review the layout JSON before saving'} description="Generate unsigned drafts from Employee lists & draft letters in this client's profiles or Offers & Pre-boarding. Signing and release remain separate actions." action={<Button size="small" onClick={() => {
      const blob = new Blob([template.bodyTemplate.slice(template.bodyTemplate.indexOf(documentLayoutMarker) + documentLayoutMarker.length).trim()], { type: 'application/json' }), url = URL.createObjectURL(blob), link = document.createElement('a')
      link.href = url; link.download = `${template.templateCode || 'document'}-layout.json`; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    }}>Download layout</Button>} /><details style={{ marginTop: 16 }}><summary>Advanced layout content</summary>{textBody}</details></> : textBody}
  </>
}
