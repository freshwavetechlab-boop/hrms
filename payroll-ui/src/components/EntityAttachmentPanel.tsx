import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button, Input, Progress, Space, Tag } from 'antd'
import { useToast } from './ToastProvider'
import { deleteEntityAttachment, getEffectiveAttachmentConfigurations, getEntityAttachments, openAttachmentWithTicket, rejectEntityAttachment, uploadEntityAttachment, verifyEntityAttachment } from '../services/attachmentService'
import type { AttachmentFieldConfiguration, EntityAttachment } from '../types/payroll'
import './EmployeeAttachmentPanel.css'

export type EntityAttachmentDraft = { file: File | null; documentNumber: string; issueDate: string; expiryDate: string }
export type EntityAttachmentUploadResult = { ok: boolean; error?: string }
type Props = {
  entityType: string; entityId: number; clientId: number; moduleCode: string; formCodes: string[]
  title?: string; description?: string; selfService?: boolean; readOnly?: boolean; emptyMessage?: string
  uploadOverride?: (configuration: AttachmentFieldConfiguration, draft: EntityAttachmentDraft, onProgress: (percent: number) => void) => Promise<EntityAttachmentUploadResult>
  onChanged?: () => void
}

const blankDraft = (): EntityAttachmentDraft => ({ file: null, documentNumber: '', issueDate: '', expiryDate: '' })
const extensions = (row: AttachmentFieldConfiguration) => { try { return JSON.parse(row.allowedExtensionsJson) as string[] } catch { return [] } }
const formatBytes = (value: number) => value >= 1048576 ? `${(value / 1048576).toFixed(value >= 10485760 ? 0 : 1)} MB` : `${Math.ceil(value / 1024)} KB`
const statusColor = (value: string) => value === 'Verified' ? 'green' : value === 'Rejected' ? 'red' : value === 'Pending' ? 'orange' : 'default'

export default function EntityAttachmentPanel({ entityType, entityId, clientId, moduleCode, formCodes, title = 'Documents', description = 'Files are private and open only through an authorized short-lived link.', selfService = false, readOnly = false, emptyMessage, uploadOverride, onChanged }: Props) {
  const notify = useToast()
  const [configurations, setConfigurations] = useState<AttachmentFieldConfiguration[]>([])
  const [attachments, setAttachments] = useState<EntityAttachment[]>([])
  const [drafts, setDrafts] = useState<Record<number, EntityAttachmentDraft>>({})
  const [progress, setProgress] = useState<Record<number, number>>({})
  const [busy, setBusy] = useState<Record<number, boolean>>({})
  const formKey = formCodes.join('|')

  const load = useCallback(async () => {
    if (!entityId || !clientId) return
    const configurationSets = await Promise.all(formKey.split('|').filter(Boolean).map(formCode => getEffectiveAttachmentConfigurations(clientId, moduleCode, formCode)))
    const attachmentRows = await getEntityAttachments(entityType, entityId)
    const rows = Array.from(new Map(configurationSets.flat().map(row => [row.id, row])).values()).filter(row => !selfService || row.ownerCanView || row.ownerCanUpload)
    setConfigurations(rows); setAttachments(attachmentRows)
    setDrafts(current => Object.fromEntries(rows.map(row => [row.id, current[row.id] ?? blankDraft()])))
  }, [clientId, entityId, entityType, formKey, moduleCode, selfService])
  useEffect(() => { void load() }, [load])

  const displayConfigurations = useMemo(() => configurations.filter((row, index, rows) => rows.findIndex(candidate => candidate.attachmentAttributeId === row.attachmentAttributeId) === index).map(defaultRow => {
    const latestFile = attachments.filter(item => item.attachmentAttributeId === defaultRow.attachmentAttributeId).sort((a, b) => String(b.uploadedAtUtc).localeCompare(String(a.uploadedAtUtc)))[0]
    return configurations.find(row => row.id === latestFile?.fieldConfigurationId) ?? defaultRow
  }), [attachments, configurations])
  const grouped = useMemo(() => new Map(displayConfigurations.map(row => [row.id, attachments.filter(item => item.attachmentAttributeId === row.attachmentAttributeId)])), [attachments, displayConfigurations])
  const requiredMissing = displayConfigurations.filter(row => row.isRequired && !(grouped.get(row.id)?.length))
  const patchDraft = (id: number, patch: Partial<EntityAttachmentDraft>) => setDrafts(current => ({ ...current, [id]: { ...(current[id] ?? blankDraft()), ...patch } }))
  const refresh = async () => { await load(); onChanged?.() }

  const upload = async (configuration: AttachmentFieldConfiguration) => {
    const draft = drafts[configuration.id] ?? blankDraft()
    if (!draft.file) { notify('Select a file first.', 'warning'); return }
    setBusy(current => ({ ...current, [configuration.id]: true })); setProgress(current => ({ ...current, [configuration.id]: 1 }))
    const report = (percent: number) => setProgress(current => ({ ...current, [configuration.id]: percent }))
    const response = uploadOverride ? await uploadOverride(configuration, draft, report) : await uploadEntityAttachment(configuration.id, entityType, entityId, draft.file, draft, report)
    setBusy(current => ({ ...current, [configuration.id]: false }))
    if (!response.ok) { notify(response.error || 'Attachment upload failed.', 'error'); return }
    notify(`${configuration.fieldLabel} uploaded.`, 'success'); patchDraft(configuration.id, blankDraft()); report(0); await refresh()
  }
  const remove = async (file: EntityAttachment) => {
    if (!window.confirm(`Delete ${file.originalFileName}?`)) return
    const response = await deleteEntityAttachment(file.publicId)
    notify(response.ok ? 'Attachment deleted.' : response.error || 'Unable to delete attachment.', response.ok ? 'success' : 'error')
    if (response.ok) await refresh()
  }
  const review = async (file: EntityAttachment, approve: boolean) => {
    const reason = approve ? '' : window.prompt('Rejection reason') || ''
    if (!approve && !reason.trim()) return
    const response = approve ? await verifyEntityAttachment(file.publicId) : await rejectEntityAttachment(file.publicId, reason)
    if (response.ok) await refresh()
  }
  const open = async (file: EntityAttachment, purpose: 'Preview' | 'Download') => {
    const response = await openAttachmentWithTicket(file.publicId, purpose)
    if (!response.ok) notify(response.error || `Unable to ${purpose.toLowerCase()} attachment.`, 'error')
  }

  if (!entityId) return <div className="employee-document-empty"><h4>{title}</h4><p>{emptyMessage || 'Save the record first to manage documents.'}</p></div>
  if (!configurations.length) return <div className="employee-document-empty"><h4>{title}</h4><p>No attachment fields are configured for this form. Configure them from Settings → Attachments.</p></div>
  return <section className="employee-document-panel">
    <div className="employee-document-summary"><div><h4>{title}</h4><p>{description}</p></div><Tag color={requiredMissing.length ? 'orange' : 'green'}>{requiredMissing.length ? `${requiredMissing.length} required pending` : 'Required documents complete'}</Tag></div>
    <div className="employee-document-grid">{displayConfigurations.map(configuration => {
      const files = grouped.get(configuration.id) ?? []; const draft = drafts[configuration.id] ?? blankDraft(); const allowed = extensions(configuration)
      const canAdd = !readOnly && (!selfService || configuration.ownerCanUpload) && (configuration.allowMultiple ? files.length < configuration.maximumFileCount : files.length === 0 || ((!selfService || configuration.ownerCanReplace) && configuration.versioningEnabled))
      return <article className={`employee-document-card${configuration.isRequired && !files.length ? ' required-missing' : ''}`} key={configuration.id}>
        <header><div><strong>{configuration.fieldLabel}{configuration.isRequired ? ' *' : ''}</strong><span>{configuration.attributeName} · {configuration.dataClassification}</span></div><Tag>{configuration.allowMultiple ? `${files.length}/${configuration.maximumFileCount} files` : files.length ? 'Uploaded' : 'Single file'}</Tag></header>
        <p>{configuration.helpText || `${allowed.map(value => value.toUpperCase()).join(', ')} · Maximum ${formatBytes(configuration.maximumFileSizeBytes)}`}</p>
        {!!files.length && <div className="employee-document-files">{files.map(file => <div className="employee-document-file" key={file.publicId}>
          <div><b title={file.originalFileName}>{file.originalFileName}</b><span>{formatBytes(file.fileSizeBytes)} · v{file.versionNumber} · {new Date(file.uploadedAtUtc).toLocaleString('en-IN')}</span></div><Tag color={statusColor(file.verificationStatus)}>{file.verificationStatus}</Tag>
          <Space size={4} wrap><Button size="small" onClick={() => void open(file, 'Preview')}>Preview</Button><Button size="small" onClick={() => void open(file, 'Download')}>Download</Button>
            {!readOnly && !selfService && file.verificationStatus === 'Pending' && <><Button size="small" type="primary" onClick={() => void review(file, true)}>Verify</Button><Button size="small" danger onClick={() => void review(file, false)}>Reject</Button></>}
            {!readOnly && (!selfService || configuration.ownerCanDelete) && <Button size="small" danger onClick={() => void remove(file)}>Delete</Button>}</Space>
        </div>)}</div>}
        {canAdd && <div className="employee-document-upload">
          {(configuration.requiresDocumentNumber || configuration.requiresIssueDate || configuration.requiresExpiryDate) && <div className="employee-document-metadata">
            {configuration.requiresDocumentNumber && <label><span>Document number</span><Input value={draft.documentNumber} onChange={event => patchDraft(configuration.id, { documentNumber: event.target.value })} /></label>}
            {configuration.requiresIssueDate && <label><span>Issue date</span><Input type="date" value={draft.issueDate} onChange={event => patchDraft(configuration.id, { issueDate: event.target.value })} /></label>}
            {configuration.requiresExpiryDate && <label><span>Expiry date</span><Input type="date" value={draft.expiryDate} onChange={event => patchDraft(configuration.id, { expiryDate: event.target.value })} /></label>}
          </div>}
          <label className="employee-document-file-picker"><input type="file" accept={allowed.map(value => `.${value}`).join(',')} onChange={event => { patchDraft(configuration.id, { file: event.currentTarget.files?.[0] ?? null }); event.currentTarget.value = '' }} /><span>{draft.file?.name || (files.length && !configuration.allowMultiple ? 'Choose replacement file' : 'Choose file')}</span></label>
          <Button type="primary" disabled={!draft.file} loading={busy[configuration.id]} onClick={() => void upload(configuration)}>{files.length && !configuration.allowMultiple ? 'Replace' : 'Upload'}</Button>{busy[configuration.id] && <Progress percent={progress[configuration.id] || 0} size="small" />}
        </div>}
      </article>
    })}</div>
  </section>
}
