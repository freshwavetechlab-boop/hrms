import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Drawer, Empty, Form, Select, Space, Spin, Tag, Typography } from 'antd'
import { FileTextOutlined, UserAddOutlined } from '@ant-design/icons'
import RecruitmentDynamicForm, { validateDynamicForm } from './RecruitmentDynamicForm'
import {
  createInternalApplicationSession,
  getRecruitmentForm,
  getRecruitmentForms,
  getRecruitmentJobPostings,
  loadInternalSelectOptions,
  savePublicApplicationValues,
  submitPublicApplication,
  uploadPublicApplicationFile,
} from '../services/recruitmentOrchestrationService'
import type { DynamicFormDefinition, DynamicFormField, DynamicFormVersion, PublicFormValue, PublicUploadedFile, PublicUploadMetadata, RecruitmentJobPosting } from '../types/recruitmentOrchestration'
import { useToast } from './ToastProvider'

type Props = {
  open: boolean
  clientId: number
  initialPostingId?: number
  onClose: () => void
  onCompleted: () => Promise<void> | void
}

export default function RecruitmentInternalCandidateForm({ open, clientId, initialPostingId = 0, onClose, onCompleted }: Props) {
  const notify = useToast()
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [definitions, setDefinitions] = useState<DynamicFormDefinition[]>([])
  const [postingId, setPostingId] = useState(0)
  const [values, setValues] = useState<PublicFormValue[]>([])
  const [files, setFiles] = useState<PublicUploadedFile[]>([])
  const [sessionStarted, setSessionStarted] = useState(false)
  const sessionToken = useRef('')
  const sessionPromise = useRef<Promise<string> | null>(null)

  useEffect(() => {
    if (!open) return
    void Promise.all([getRecruitmentJobPostings(clientId), getRecruitmentForms(clientId)]).then(async ([postingRows, formRows]) => {
      const publishedPostings = postingRows.filter(row => row.status === 'Published' && Boolean(row.applicationFormVersionId))
      setPostings(publishedPostings)
      setPostingId(publishedPostings.some(row => row.id === initialPostingId) ? initialPostingId : 0)
      const detailed = await Promise.all(formRows.filter(row => row.status !== 'Inactive').map(row => getRecruitmentForm(row.id)))
      setDefinitions(detailed.filter((row): row is DynamicFormDefinition => Boolean(row)))
    }).finally(() => setLoading(false))
  }, [clientId, initialPostingId, open])

  const posting = postings.find(row => row.id === postingId)
  const version = useMemo(() => findPinnedVersion(definitions, posting?.applicationFormVersionId), [definitions, posting?.applicationFormVersionId])
  const formName = definitions.find(row => row.versions.some(item => item.id === version?.id))?.formName

  const choosePosting = (id: number) => {
    setPostingId(id)
    setValues([])
    setFiles([])
    setSessionStarted(false)
    sessionToken.current = ''
    sessionPromise.current = null
  }

  const ensureSession = async () => {
    if (sessionToken.current) return sessionToken.current
    if (sessionPromise.current) return sessionPromise.current
    if (!posting || !version) throw new Error('Select a published job with a configured candidate form.')
    const email = semanticText(version, values, 'EMAIL')
    const phone = semanticText(version, values, 'PHONE')
    if (!email) throw new Error('Enter the candidate email before uploading documents.')
    if (!phone) throw new Error('Enter the candidate phone number before uploading documents.')
    const promise = createInternalApplicationSession(posting.id, { email, phone, consentAccepted: true, verificationToken: '', verificationCode: '', idempotencyKey: crypto.randomUUID() }).then(response => {
      if (!response.ok || !response.data) throw new Error(response.error || 'Candidate application session could not be created.')
      sessionToken.current = response.data.sessionToken
      setSessionStarted(true)
      setValues(current => mergeInitialValues(current, response.data!.initialValues))
      return response.data.sessionToken
    }).finally(() => { sessionPromise.current = null })
    sessionPromise.current = promise
    return promise
  }

  const upload = async (field: DynamicFormField, file: File, metadata: PublicUploadMetadata, onProgress: (percent: number) => void) => {
    try {
      const token = await ensureSession()
      const response = await uploadPublicApplicationFile(token, field.id, file, metadata, onProgress)
      if (!response.ok || !response.data) return { ok: false, error: response.error || 'Upload failed.' }
      setFiles(current => [...current, response.data!])
      return { ok: true }
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : 'Upload failed.' }
    }
  }

  const submit = async () => {
    if (!version) return
    const validationError = validateDynamicForm(version, values, files)
    if (validationError) return notify(validationError, 'error')
    setBusy(true)
    try {
      const token = await ensureSession()
      const saved = await savePublicApplicationValues(token, values)
      if (!saved.ok) return notify(saved.error || 'Candidate details could not be saved.', 'error')
      const submitted = await submitPublicApplication(token)
      if (!submitted.ok || !submitted.data) return notify(submitted.error || 'Candidate application could not be submitted.', 'error')
      notify(`${submitted.data.applicationCode || 'Application'} added successfully.`, 'success')
      await onCompleted()
      onClose()
    } catch (error) {
      notify(error instanceof Error ? error.message : 'Candidate application could not be submitted.', 'error')
    } finally {
      setBusy(false)
    }
  }

  return <Drawer
    open={open}
    onClose={onClose}
    destroyOnClose
    maskClosable={!busy}
    keyboard={!busy}
    closable={!busy}
    width="min(1040px, 96vw)"
    className="internal-candidate-form-drawer"
    title={<div className="internal-candidate-form-title"><span><UserAddOutlined /></span><div><strong>Add candidate</strong><small>Uses the same published Candidate Form as the public job link.</small></div></div>}
    footer={<div className="internal-candidate-form-footer"><Typography.Text type="secondary">Duplicate email and phone checks remain active.</Typography.Text><Space><Button disabled={busy} onClick={onClose}>Cancel</Button><Button type="primary" loading={busy} disabled={!version} onClick={() => void submit()}>Add candidate & start pipeline</Button></Space></div>}
  >
    {loading ? <div className="internal-candidate-loading"><Spin /><span>Loading published candidate forms…</span></div> : <div className="internal-candidate-form-body">
      <section className="internal-candidate-job"><div><FileTextOutlined /><span><b>Job position</b><small>The job fixes the approved JD, candidate form and candidate pipeline.</small></span></div><Select showSearch optionFilterProp="label" value={postingId || undefined} onChange={choosePosting} placeholder="Select a published job" options={postings.map(row => ({ value: row.id, label: `${row.positionTitle} (${row.positionCode})` }))} /></section>
      {!postings.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No published job with a Candidate Form is available for this client." />}
      {posting && !version && <Alert showIcon type="warning" message="The pinned Candidate Form version could not be loaded." description="Open Jobs and republish the job with a valid published Candidate Form." />}
      {posting && version && <Form layout="vertical" className="internal-candidate-dynamic-form"><div className="internal-candidate-form-context"><div><b>{posting.publicTitle || posting.positionTitle}</b><span>{posting.positionCode} · {posting.clientName}</span></div><Tag color="purple">{formName || `Candidate Form v${version.versionNumber}`}</Tag></div><RecruitmentDynamicForm form={version} values={values} files={files} disabled={busy} lockedSemanticCodes={sessionStarted ? ['EMAIL', 'PHONE'] : []} onChange={setValues} onUpload={upload} onLoadOptions={(field, search) => loadInternalSelectOptions(field.lookupSourceCode, search, posting.clientId)} /></Form>}
    </div>}
  </Drawer>
}

function findPinnedVersion(definitions: DynamicFormDefinition[], versionId?: number | null): DynamicFormVersion | null {
  if (!versionId) return null
  return definitions.flatMap(row => row.versions).find(row => row.id === versionId) ?? null
}

function semanticText(form: DynamicFormVersion, values: PublicFormValue[], semanticCode: string) {
  const field = form.sections.flatMap(section => section.fields).find(row => row.semanticCodes.some(code => code.toUpperCase() === semanticCode))
  return values.find(row => row.fieldId === field?.id)?.textValue?.trim() || ''
}

function mergeInitialValues(current: PublicFormValue[], initial: PublicFormValue[]) {
  const ids = new Set(current.map(row => row.fieldId))
  return [...current, ...initial.filter(row => !ids.has(row.fieldId))]
}
