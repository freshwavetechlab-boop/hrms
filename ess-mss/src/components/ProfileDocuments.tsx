import { useCallback, useEffect, useMemo, useState } from 'react'
import type { AttachmentFieldConfiguration, EntityAttachment } from '../types'
import { apiBase, essApi } from '../services/essApi'
import { showToast } from '../utils/ui'

type Draft = { file: File | null; documentNumber: string; issueDate: string; expiryDate: string }
const emptyDraft = (): Draft => ({ file: null, documentNumber: '', issueDate: '', expiryDate: '' })
const allowedExtensions = (configuration: AttachmentFieldConfiguration) => {
  try { return JSON.parse(configuration.allowedExtensionsJson) as string[] } catch { return [] }
}
const formatBytes = (bytes: number) => bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(bytes >= 10 * 1024 * 1024 ? 0 : 1)} MB` : `${Math.ceil(bytes / 1024)} KB`

export function ProfileDocuments({ employeeId, clientId }: { employeeId: number; clientId: number }) {
  const [configurations, setConfigurations] = useState<AttachmentFieldConfiguration[]>([])
  const [attachments, setAttachments] = useState<EntityAttachment[]>([])
  const [drafts, setDrafts] = useState<Record<number, Draft>>({})
  const [busy, setBusy] = useState<Record<number, boolean>>({})
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    if (!employeeId || !clientId) return
    setLoading(true)
    try {
      const [configured, files] = await Promise.all([essApi.attachmentConfigurations(clientId), essApi.attachments(employeeId)])
      const visible = configured.filter(row => row.ownerCanView || row.ownerCanUpload)
      setConfigurations(visible)
      setAttachments(files)
      setDrafts(current => Object.fromEntries(visible.map(row => [row.id, current[row.id] ?? emptyDraft()])))
    } finally {
      setLoading(false)
    }
  }, [clientId, employeeId])

  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const grouped = useMemo(() => new Map(configurations.map(row => [row.id, attachments.filter(file => file.fieldConfigurationId === row.id)])), [attachments, configurations])
  const patchDraft = (id: number, patch: Partial<Draft>) => setDrafts(current => ({ ...current, [id]: { ...(current[id] ?? emptyDraft()), ...patch } }))

  const upload = async (configuration: AttachmentFieldConfiguration) => {
    const draft = drafts[configuration.id] ?? emptyDraft()
    if (!draft.file) return showToast('Choose a document first.', 'error')
    if (draft.file.size > configuration.maximumFileSizeBytes) return showToast(`Maximum allowed size is ${formatBytes(configuration.maximumFileSizeBytes)}.`, 'error')
    setBusy(current => ({ ...current, [configuration.id]: true }))
    try {
      await essApi.uploadAttachment(configuration.id, employeeId, draft.file, draft)
      patchDraft(configuration.id, emptyDraft())
      showToast(`${configuration.fieldLabel} uploaded.`, 'success')
      await load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to upload document.', 'error')
    } finally {
      setBusy(current => ({ ...current, [configuration.id]: false }))
    }
  }

  const remove = async (file: EntityAttachment) => {
    if (!window.confirm(`Delete ${file.originalFileName}?`)) return
    try {
      await essApi.deleteAttachment(file.publicId)
      showToast('Document deleted.', 'success')
      await load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to delete document.', 'error')
    }
  }

  const open = async (file: EntityAttachment, purpose: 'Preview' | 'Download') => {
    const previewWindow = purpose === 'Preview' ? window.open('', '_blank') : null
    if (previewWindow) previewWindow.opener = null
    try {
      const ticket = await essApi.attachmentTicket(file.publicId, purpose)
      const url = `${apiBase}${ticket.url}`
      if (purpose === 'Preview') {
        if (previewWindow) previewWindow.location.replace(url)
        else window.open(url, '_blank', 'noopener,noreferrer')
      } else {
        const anchor = document.createElement('a')
        anchor.href = url
        anchor.rel = 'noopener noreferrer'
        anchor.click()
      }
    } catch (error) {
      previewWindow?.close()
      showToast(error instanceof Error ? error.message : `Unable to ${purpose.toLowerCase()} document.`, 'error')
    }
  }

  if (loading) return <section className="profile-form-section profile-document-section"><h4>Documents</h4><p className="profile-document-empty">Loading configured documents...</p></section>
  if (!configurations.length) return null

  return <section className="profile-form-section profile-document-section">
    <h4>Documents</h4>
    <div className="profile-document-grid">
      {configurations.map(configuration => {
        const files = grouped.get(configuration.id) ?? []
        const draft = drafts[configuration.id] ?? emptyDraft()
        const extensions = allowedExtensions(configuration)
        const fileInputId = `profile-document-file-${configuration.id}`
        const canReplace = !files.length || (configuration.ownerCanReplace && configuration.versioningEnabled)
        const canUpload = configuration.ownerCanUpload && (configuration.allowMultiple ? files.length < configuration.maximumFileCount : canReplace)
        return <article className={configuration.isRequired && !files.length ? 'missing' : ''} key={configuration.id}>
          <header><div><b>{configuration.fieldLabel}{configuration.isRequired ? ' *' : ''}</b><span>{configuration.attributeName} · {configuration.dataClassification}</span></div><small>{files.length ? `${files.length} uploaded` : 'Pending'}</small></header>
          <p>{configuration.helpText || `${extensions.join(', ').toUpperCase()} · Maximum ${formatBytes(configuration.maximumFileSizeBytes)}`}</p>
          {files.map(file => <div className="profile-document-file" key={file.publicId}>
            <div><b>{file.originalFileName}</b><span>{formatBytes(file.fileSizeBytes)} · v{file.versionNumber} · {file.verificationStatus}</span></div>
            <div>
              <button type="button" onClick={() => void open(file, 'Preview')}>Preview</button>
              <button type="button" onClick={() => void open(file, 'Download')}>Download</button>
              {configuration.ownerCanDelete && <button type="button" className="danger" onClick={() => void remove(file)}>Delete</button>}
            </div>
          </div>)}
          {canUpload && <div className="profile-document-upload">
            {(configuration.requiresDocumentNumber || configuration.requiresIssueDate || configuration.requiresExpiryDate) && <div className="profile-document-metadata">
              {configuration.requiresDocumentNumber && <label><span>Document number</span><input value={draft.documentNumber} onChange={event => patchDraft(configuration.id, { documentNumber: event.target.value })} /></label>}
              {configuration.requiresIssueDate && <label><span>Issue date</span><input type="date" value={draft.issueDate} onChange={event => patchDraft(configuration.id, { issueDate: event.target.value })} /></label>}
              {configuration.requiresExpiryDate && <label><span>Expiry date</span><input type="date" value={draft.expiryDate} onChange={event => patchDraft(configuration.id, { expiryDate: event.target.value })} /></label>}
            </div>}
            <div className="profile-document-picker"><input
              id={fileInputId}
              className="profile-document-native-input"
              type="file"
              accept={extensions.map(value => `.${value}`).join(',')}
              onChange={event => {
                const selectedFile = event.currentTarget.files?.[0] ?? null
                patchDraft(configuration.id, { file: selectedFile })
                event.currentTarget.value = ''
              }}
            />
              <label className="profile-document-choose" htmlFor={fileInputId}>
                {files.length && !configuration.allowMultiple ? 'Choose replacement' : 'Choose file'}
              </label>
              <span className={draft.file ? 'profile-document-selected selected' : 'profile-document-selected'}>
                {draft.file?.name || 'No file selected'}
              </span>
              <button type="button" disabled={!draft.file || busy[configuration.id]} onClick={() => void upload(configuration)}>
                {busy[configuration.id] ? 'Uploading...' : files.length && !configuration.allowMultiple ? 'Upload replacement' : 'Upload document'}
              </button>
            </div>
          </div>}
        </article>
      })}
    </div>
  </section>
}
