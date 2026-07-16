import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button, Input, Progress, Space, Tag } from 'antd'
import { useToast } from './ToastProvider'
import { deleteEntityAttachment, getEffectiveAttachmentConfigurations, getEntityAttachments, openAttachmentWithTicket, rejectEntityAttachment, uploadEntityAttachment, verifyEntityAttachment } from '../services/attachmentService'
import type { AttachmentFieldConfiguration, EntityAttachment } from '../types/payroll'
import './EmployeeAttachmentPanel.css'

type Draft = { file: File | null; documentNumber: string; issueDate: string; expiryDate: string }
const draft0 = (): Draft => ({ file: null, documentNumber: '', issueDate: '', expiryDate: '' })
const extensions = (configuration: AttachmentFieldConfiguration) => {
  try { return JSON.parse(configuration.allowedExtensionsJson) as string[] } catch { return [] }
}
const formatBytes = (value: number) => value >= 1024 * 1024 ? `${(value / 1024 / 1024).toFixed(value >= 10 * 1024 * 1024 ? 0 : 1)} MB` : `${Math.ceil(value / 1024)} KB`
const statusColor = (value: string) => value === 'Verified' ? 'green' : value === 'Rejected' ? 'red' : value === 'Pending' ? 'orange' : 'default'

export default function EmployeeAttachmentPanel({ employeeId, clientId, selfService = false }: { employeeId: number; clientId: number; selfService?: boolean }) {
  const notify = useToast()
  const [configurations, setConfigurations] = useState<AttachmentFieldConfiguration[]>([])
  const [attachments, setAttachments] = useState<EntityAttachment[]>([])
  const [drafts, setDrafts] = useState<Record<number, Draft>>({})
  const [progress, setProgress] = useState<Record<number, number>>({})
  const [busy, setBusy] = useState<Record<number, boolean>>({})

  const load = useCallback(async () => {
    if (!employeeId || !clientId) return
    const [createEditRows, profileRows, attachmentRows] = await Promise.all([
      getEffectiveAttachmentConfigurations(clientId, 'EMPLOYEE', 'EMPLOYEE_CREATE_EDIT'),
      getEffectiveAttachmentConfigurations(clientId, 'EMPLOYEE', 'EMPLOYEE_PROFILE'),
      getEntityAttachments('EMPLOYEE', employeeId)
    ])
    const configurationRows = Array.from(new Map([...createEditRows, ...profileRows].map(row => [row.id, row])).values())
      .filter(row => !selfService || row.ownerCanView || row.ownerCanUpload)
    setConfigurations(configurationRows)
    setAttachments(attachmentRows)
    setDrafts(current => Object.fromEntries(configurationRows.map(row => [row.id, current[row.id] ?? draft0()])))
  }, [clientId, employeeId, selfService])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const grouped = useMemo(() => new Map(configurations.map(row => [row.id, attachments.filter(item => item.fieldConfigurationId === row.id)])), [attachments, configurations])
  const requiredMissing = configurations.filter(row => row.isRequired && !(grouped.get(row.id)?.length))
  const patchDraft = (id: number, patch: Partial<Draft>) => setDrafts(current => ({ ...current, [id]: { ...(current[id] ?? draft0()), ...patch } }))

  const upload = async (configuration: AttachmentFieldConfiguration) => {
    const draft = drafts[configuration.id] ?? draft0()
    if (!draft.file) { notify('Select a file first.', 'warning'); return }
    setBusy(current => ({ ...current, [configuration.id]: true }))
    setProgress(current => ({ ...current, [configuration.id]: 1 }))
    const response = await uploadEntityAttachment(configuration.id, 'EMPLOYEE', employeeId, draft.file, draft, percent => setProgress(current => ({ ...current, [configuration.id]: percent })))
    setBusy(current => ({ ...current, [configuration.id]: false }))
    if (!response.ok) { notify(response.error || 'Attachment upload failed.', 'error'); return }
    notify(`${configuration.fieldLabel} uploaded.`, 'success')
    patchDraft(configuration.id, draft0())
    setProgress(current => ({ ...current, [configuration.id]: 0 }))
    await load()
  }
  const remove = async (attachment: EntityAttachment) => {
    if (!window.confirm(`Delete ${attachment.originalFileName}?`)) return
    const response = await deleteEntityAttachment(attachment.publicId)
    notify(response.ok ? 'Attachment deleted.' : response.error || 'Unable to delete attachment.', response.ok ? 'success' : 'error')
    if (response.ok) await load()
  }
  const review = async (attachment: EntityAttachment, approve: boolean) => {
    const reason = approve ? '' : window.prompt('Rejection reason') || ''
    if (!approve && !reason.trim()) return
    const response = approve ? await verifyEntityAttachment(attachment.publicId) : await rejectEntityAttachment(attachment.publicId, reason)
    if (response.ok) await load()
  }
  const open = async (attachment: EntityAttachment, purpose: 'Preview' | 'Download') => {
    const response = await openAttachmentWithTicket(attachment.publicId, purpose)
    if (!response.ok) notify(response.error || `Unable to ${purpose.toLowerCase()} attachment.`, 'error')
  }

  if (!employeeId) return <div className="employee-document-empty"><h4>Documents</h4><p>Save the employee first. Configured attachment fields will be available after Employee ID is generated.</p></div>
  if (!configurations.length) return <div className="employee-document-empty"><h4>Documents</h4><p>No attachment fields are configured for this employee form. Configure them from Settings → Attachments.</p></div>

  return <section className="employee-document-panel">
    <div className="employee-document-summary">
      <div><h4>Employee documents</h4><p>Files are stored privately and can only be opened through an authorized, short-lived link.</p></div>
      <Tag color={requiredMissing.length ? 'orange' : 'green'}>{requiredMissing.length ? `${requiredMissing.length} required pending` : 'Required documents complete'}</Tag>
    </div>
    <div className="employee-document-grid">
      {configurations.map(configuration => {
        const files = grouped.get(configuration.id) ?? []
        const draft = drafts[configuration.id] ?? draft0()
        const allowed = extensions(configuration)
        const canAdd = (!selfService || configuration.ownerCanUpload) && (configuration.allowMultiple
          ? files.length < configuration.maximumFileCount
          : files.length === 0 || ((!selfService || configuration.ownerCanReplace) && configuration.versioningEnabled))
        return <article className={`employee-document-card${configuration.isRequired && !files.length ? ' required-missing' : ''}`} key={configuration.id}>
          <header><div><strong>{configuration.fieldLabel}{configuration.isRequired ? ' *' : ''}</strong><span>{configuration.attributeName} · {configuration.dataClassification}</span></div><Tag>{configuration.allowMultiple ? `${files.length}/${configuration.maximumFileCount} files` : files.length ? 'Uploaded' : 'Single file'}</Tag></header>
          <p>{configuration.helpText || `${allowed.map(value => value.toUpperCase()).join(', ')} · Maximum ${formatBytes(configuration.maximumFileSizeBytes)}`}</p>
          {files.length > 0 && <div className="employee-document-files">{files.map(file => <div className="employee-document-file" key={file.publicId}>
            <div><b title={file.originalFileName}>{file.originalFileName}</b><span>{formatBytes(file.fileSizeBytes)} · v{file.versionNumber} · {new Date(file.uploadedAtUtc).toLocaleString('en-IN')}</span></div>
            <Tag color={statusColor(file.verificationStatus)}>{file.verificationStatus}</Tag>
            <Space size={4} wrap>
              <Button size="small" onClick={() => void open(file, 'Preview')}>Preview</Button>
              <Button size="small" onClick={() => void open(file, 'Download')}>Download</Button>
              {!selfService && file.verificationStatus === 'Pending' && <><Button size="small" type="primary" onClick={() => void review(file, true)}>Verify</Button><Button size="small" danger onClick={() => void review(file, false)}>Reject</Button></>}
              {(!selfService || configuration.ownerCanDelete) && <Button size="small" danger onClick={() => void remove(file)}>Delete</Button>}
            </Space>
          </div>)}</div>}
          {canAdd && <div className="employee-document-upload">
            {(configuration.requiresDocumentNumber || configuration.requiresIssueDate || configuration.requiresExpiryDate) && <div className="employee-document-metadata">
              {configuration.requiresDocumentNumber && <label><span>Document number</span><Input value={draft.documentNumber} onChange={event => patchDraft(configuration.id, { documentNumber: event.target.value })} /></label>}
              {configuration.requiresIssueDate && <label><span>Issue date</span><Input type="date" value={draft.issueDate} onChange={event => patchDraft(configuration.id, { issueDate: event.target.value })} /></label>}
              {configuration.requiresExpiryDate && <label><span>Expiry date</span><Input type="date" value={draft.expiryDate} onChange={event => patchDraft(configuration.id, { expiryDate: event.target.value })} /></label>}
            </div>}
            <label className="employee-document-file-picker"><input
              type="file"
              accept={allowed.map(value => `.${value}`).join(',')}
              onChange={event => {
                const selectedFile = event.currentTarget.files?.[0] ?? null
                patchDraft(configuration.id, { file: selectedFile })
                event.currentTarget.value = ''
              }}
            /><span>{draft.file?.name || (files.length && !configuration.allowMultiple ? 'Choose replacement file' : 'Choose file')}</span></label>
            <Button type="primary" disabled={!draft.file} loading={busy[configuration.id]} onClick={() => void upload(configuration)}>{files.length && !configuration.allowMultiple ? 'Replace' : 'Upload'}</Button>
            {busy[configuration.id] && <Progress percent={progress[configuration.id] || 0} size="small" />}
          </div>}
        </article>
      })}
    </div>
  </section>
}
