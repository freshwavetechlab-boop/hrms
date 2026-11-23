import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { Alert, Button, Card, Form, Input, Result, Skeleton } from 'antd'
import { DeleteOutlined, FileTextOutlined } from '@ant-design/icons'
import FileDropZone from '../components/FileDropZone'
import { getCandidateDocumentRequest, submitCandidateDocuments, type CandidateDocumentRequest, type CandidateDocumentRequirement, type CandidateDocumentSubmission } from '../services/publicCandidateDocumentService'
import { candidateDocumentMetadata, documentAccept, documentSizeLabel, emptyDocumentSelection, maximumCandidateDocumentNumberLength, validateCandidateDocuments, validateDocumentFiles, type CandidateDocumentSelection } from '../utils/candidateDocumentUpload'
import { publicRecruitmentEmployer } from '../utils/publicRecruitmentPresentation'
import './PublicCandidateDocumentsPage.css'

function pathToken() { try { return decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '') } catch { return '' } }

export default function PublicCandidateDocumentsPage({ token: suppliedToken }: { token?: string }) {
  const { token: routeToken } = useParams<{ token: string }>()
  const token = suppliedToken ?? routeToken ?? pathToken()
  const [context, setContext] = useState<CandidateDocumentRequest | null>(null)
  const [loading, setLoading] = useState(true), [loadError, setLoadError] = useState(''), [attempt, setAttempt] = useState(0)
  const [email, setEmail] = useState(''), [selections, setSelections] = useState<Record<string, CandidateDocumentSelection>>({})
  const [error, setError] = useState(''), [submitting, setSubmitting] = useState(false), [completed, setCompleted] = useState<CandidateDocumentSubmission | null>(null)
  const saving = useRef(false), generation = useRef(0)
  const feedback = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    const previous = document.title
    document.title = 'Submit documents | GA Digital'
    return () => { document.title = previous }
  }, [])
  useEffect(() => {
    const current = ++generation.current, controller = new AbortController()
    saving.current = false; setSubmitting(false)
    setLoading(true); setLoadError(''); setContext(null); setCompleted(null); setEmail(''); setSelections({}); setError('')
    if (!token) { setContext({ status: 'Unavailable', message: '', documents: [] }); setLoading(false); return }
    void getCandidateDocumentRequest(token, controller.signal).then(response => {
      if (current !== generation.current) return
      if (response.ok && response.data && ['Open', 'Completed', 'Expired', 'Unavailable'].includes(response.data.status))
        setContext({ ...response.data, documents: Array.isArray(response.data.documents) ? response.data.documents : [] })
      else if ([404, 410].includes(response.status)) setContext({ status: response.status === 410 ? 'Expired' : 'Unavailable', message: '', documents: [] })
      else setLoadError(response.error || 'We could not load this link. Please try again.')
      setLoading(false)
    })
    return () => { generation.current++; controller.abort() }
  }, [token, attempt])
  useEffect(() => { if (error) feedback.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) }, [error])

  const setSelection = (id: string, patch: Partial<CandidateDocumentSelection>) => {
    if (saving.current) return
    setSelections(current => ({ ...current, [id]: { ...(current[id] || emptyDocumentSelection()), ...patch } })); setError('')
  }
  const pickFiles = (document: CandidateDocumentRequirement, files: File[]) => {
    if (saving.current) return
    const problem = validateDocumentFiles(document, files)
    if (problem) { setError(problem); return }
    setSelection(document.id, { files })
  }
  const submit = async () => {
    if (!context || context.status !== 'Open' || saving.current || completed) return
    const problem = validateCandidateDocuments(context.documents, email, selections)
    if (problem) { setError(problem); return }
    saving.current = true; setSubmitting(true); setError('')
    const current = generation.current
    try {
      const response = await submitCandidateDocuments(token, email, candidateDocumentMetadata(context.documents, selections),
        Object.fromEntries(context.documents.map(document => [document.id, selections[document.id]?.files || []])))
      if (current !== generation.current) return
      if (response.ok && response.data?.status === 'Completed') { setCompleted(response.data); setSelections({}); return }
      // If another tab completed while this page was open, reflect the permanent
      // state instead of offering another upload after the server rejected it.
      if ([409, 410].includes(response.status)) {
        const refreshed = await getCandidateDocumentRequest(token)
        if (current !== generation.current) return
        if (refreshed.ok && refreshed.data?.status === 'Completed') { setContext(refreshed.data); setSelections({}); return }
      }
      setError(response.error || 'Your documents were not submitted. Please try again.')
    } finally { if (current === generation.current) { saving.current = false; setSubmitting(false) } }
  }

  const alreadySubmitted = context?.status === 'Completed' || completed?.alreadySubmitted
  return <main className="candidate-documents-page">
    <meta name="referrer" content="no-referrer" />
    <div className="candidate-documents-shell">
      <header className="candidate-documents-brand"><FileTextOutlined aria-hidden="true" /><span>{publicRecruitmentEmployer}</span></header>
      {loading ? <Card><Skeleton active paragraph={{ rows: 6 }} /></Card> : loadError ? <Card><Result status="warning" title="We could not load this link" subTitle={loadError} extra={<Button onClick={() => setAttempt(value => value + 1)}>Try again</Button>} /></Card>
        : alreadySubmitted || completed ? <Card><Result status="success" title={alreadySubmitted ? 'Already submitted' : 'Documents submitted'} subTitle="Your documents have been submitted to HR. This link no longer accepts uploads." /></Card>
        : !context || context.status !== 'Open' ? <Card><Result status="warning" title={context?.status === 'Expired' ? 'Link expired' : 'Link unavailable'} subTitle="Please contact the HR team for help with your document request." /></Card>
        : <Card className="candidate-documents-card">
          <div className="candidate-documents-heading"><h1>Submit your documents</h1><p>Enter the email address that received your offer letter and upload the documents listed below.</p></div>
          <Form layout="vertical" requiredMark={false} onFinish={() => void submit()}>
            <Form.Item label="Email address" required><Input aria-label="Email address" type="email" autoComplete="email" maxLength={254} value={email} disabled={submitting} onChange={event => { setEmail(event.target.value); setError('') }} placeholder="Email used for your offer letter" /></Form.Item>
            <fieldset disabled={submitting} className="candidate-documents-fields">
              {context.documents.map(document => {
                const selected = selections[document.id] || emptyDocumentSelection()
                return <section key={document.id} className="candidate-document-item" aria-label={document.label}>
                  <div className="candidate-document-title"><h2>{document.label}</h2><span>{document.required || document.minimumFileCount > 0 ? 'Required' : 'Optional'}</span></div>
                  <FileDropZone title={`Choose ${document.label}`} hint={`${document.allowedExtensions.join(', ')}${document.maximumFileSizeBytes > 0 ? ` · Up to ${documentSizeLabel(document.maximumFileSizeBytes)} per file` : ''}${document.maximumFileCount > 1 ? ` · Up to ${document.maximumFileCount} files` : ''}`}
                    accept={documentAccept(document)} fileName={selected.files.length ? `${selected.files.length} file${selected.files.length === 1 ? '' : 's'} selected` : ''}
                    onFile={file => pickFiles(document, [file])} onFiles={document.maximumFileCount > 1 ? files => pickFiles(document, files) : undefined} />
                  {!!selected.files.length && <ul className="candidate-document-files">{selected.files.map((file, index) => <li key={`${file.name}:${index}`}><span>{file.name}<small>{documentSizeLabel(file.size)}</small></span><Button type="text" icon={<DeleteOutlined />} aria-label={`Remove ${file.name}`} disabled={submitting} onClick={() => setSelection(document.id, { files: selected.files.filter((_, i) => i !== index) })} /></li>)}</ul>}
                  {(document.requiresDocumentNumber || document.requiresIssueDate || document.requiresExpiryDate) && <div className="candidate-document-metadata">
                    {document.requiresDocumentNumber && <Form.Item label="Document number" required><Input aria-label={`${document.label} document number`} value={selected.documentNumber} maxLength={maximumCandidateDocumentNumberLength} onChange={event => setSelection(document.id, { documentNumber: event.target.value })} /></Form.Item>}
                    {document.requiresIssueDate && <Form.Item label="Issue date" required><Input aria-label={`${document.label} issue date`} type="date" value={selected.issueDate} onChange={event => setSelection(document.id, { issueDate: event.target.value })} /></Form.Item>}
                    {document.requiresExpiryDate && <Form.Item label="Expiry date" required><Input aria-label={`${document.label} expiry date`} type="date" value={selected.expiryDate} onChange={event => setSelection(document.id, { expiryDate: event.target.value })} /></Form.Item>}
                  </div>}
                </section>
              })}
            </fieldset>
            {!context.documents.length && <Alert showIcon type="info" message="No documents are listed for this request. Please contact HR." />}
            <div ref={feedback} role={error ? 'alert' : undefined}>{error && <Alert type="error" showIcon message={error} />}</div>
            <footer className="candidate-documents-submit"><p>Check your files before submitting. Once submitted, this link cannot be used to upload again.</p><Button type="primary" size="large" htmlType="submit" loading={submitting} disabled={!context.documents.length || submitting}>Submit documents</Button></footer>
          </Form>
        </Card>}
      <p className="candidate-documents-private">This is a personal document-upload link. Do not forward it.</p>
    </div>
  </main>
}
