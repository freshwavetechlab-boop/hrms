import { useEffect, useState } from 'react'
import { FileProtectOutlined, SafetyCertificateOutlined, SaveOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Form, Input, Modal, Result, Skeleton, Space, Tag, message } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import {
  completePublicCandidateAction, getPublicCandidateAction, loadPublicCandidateActionOptions, savePublicCandidateActionValues,
  uploadPublicCandidateActionFile,
} from '../services/recruitmentOrchestrationService'
import type {
  CandidateActionDecision, DynamicFormField, PublicCandidateActionContext, PublicFormValue, PublicUploadedFile,
} from '../types/recruitmentOrchestration'
import '../components/RecruitmentOrchestration.css'

type Props = { token?: string }

export default function PublicCandidateActionPage({ token: suppliedToken }: Props) {
  const token = suppliedToken || decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '')
  const [context, setContext] = useState<PublicCandidateActionContext | null>(null)
  const [loading, setLoading] = useState(true)
  const [values, setValues] = useState<PublicFormValue[]>([])
  const [files, setFiles] = useState<PublicUploadedFile[]>([])
  const [decisionDraft, setDecisionDraft] = useState<CandidateActionDecision | null>(null)
  const [remarks, setRemarks] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [savingDraft, setSavingDraft] = useState(false)
  const [completed, setCompleted] = useState<{ status: string; message: string } | null>(null)

  useEffect(() => {
    let active = true
    setLoading(true)
    void getPublicCandidateAction(token).then(row => {
      if (!active) return
      setContext(row || null); setValues(row?.existingValues ?? []); setFiles(row?.uploadedFiles ?? []); setLoading(false)
    })
    return () => { active = false }
  }, [token])

  const upload = async (field: DynamicFormField, file: File, onProgress: (percent: number) => void) => {
    const response = await uploadPublicCandidateActionFile(token, field.id, file, onProgress)
    if (response.ok && response.data) setFiles(current => [...current, { ...response.data, fieldId: response.data.fieldId || field.id }])
    return { ok: response.ok, error: response.error }
  }
  const submitForm = async () => {
    if (!context?.form) return
    const error = validateDynamicForm(context.form, values, files)
    if (error) return message.warning(error)
    await complete(undefined)
  }
  const saveDraft = async () => {
    if (!context?.allowSaveDraft) return
    setSavingDraft(true)
    const response = await savePublicCandidateActionValues(token, values)
    setSavingDraft(false)
    if (response.ok) message.success('Draft saved securely. You can return using the same link.')
    else message.error(response.error || 'Unable to save this draft.')
  }
  const complete = async (decision?: CandidateActionDecision) => {
    setSubmitting(true)
    const response = await completePublicCandidateAction(token, values, decision, remarks)
    setSubmitting(false)
    if (!response.ok || !response.data) return message.error(response.error || 'Unable to complete this action.')
    setCompleted(response.data); setDecisionDraft(null)
  }

  if (loading) return <main className="public-career-page"><div className="public-career-shell"><Card><Skeleton active paragraph={{ rows: 10 }} /></Card></div></main>
  if (!context) return <main className="public-career-page"><div className="public-career-shell public-success"><Result status="error" title="Secure link is unavailable" subTitle="This link is invalid, expired or has been revoked. Contact the recruitment team for a new link." /></div></main>
  if (completed) return <main className="public-career-page"><div className="public-career-shell public-success"><Result status="success" title={completed.status} subTitle={completed.message || 'Your response was submitted successfully.'} /></div></main>

  const expired = new Date(context.expiresAtUtc).getTime() < Date.now()
  const completedBefore = !['Active', 'Pending', 'Open'].includes(context.status)
  return <main className="public-career-page"><div className="public-career-shell">
    <header className="public-career-brand"><div><span className="orchestration-kicker">Secure candidate portal</span><b>{context.organizationName}</b></div></header>
    <Card className="public-job-hero"><Space wrap><Tag color="purple">{context.purpose.replace(/([A-Z])/g, ' $1').trim()}</Tag><Tag color={expired ? 'red' : 'blue'}>{expired ? 'Expired' : `Valid until ${new Date(context.expiresAtUtc).toLocaleString('en-IN')}`}</Tag></Space><h1>{context.candidateName}</h1><p>{context.positionTitle}</p>{context.message && <Alert showIcon type="info" message={context.message} />}</Card>
    <div className="public-job-layout">
      <Card className="public-job-content">
        <Alert showIcon icon={<SafetyCertificateOutlined />} type="success" message="Protected candidate action" description="This purpose-specific link does not expose a storage path. It expires automatically and every upload or response is audit logged." />
        {context.purpose === 'OfferResponse' && context.offer && <OfferSummary context={context} />}
        {context.form && <div style={{ marginTop: 20 }}><RecruitmentDynamicForm disabled={expired || completedBefore} form={context.form} values={values} files={files} onChange={setValues} onUpload={upload} onLoadOptions={(field, search) => loadPublicCandidateActionOptions(token, field.id, search)} /></div>}
      </Card>
      <Card className="public-apply-card">
        <FileProtectOutlined style={{ fontSize: 34, color: '#6b4eff' }} />
        <h2>{context.purpose === 'OfferResponse' ? 'Your offer response' : 'Complete requested details'}</h2>
        {expired ? <Alert type="error" showIcon message="This link has expired. Request a new secure link from HR." /> : completedBefore ? <Alert type="warning" showIcon message={context.message || `This action is ${context.status.toLowerCase()}.`} /> : context.purpose === 'OfferResponse' ? <Space direction="vertical" style={{ width: '100%' }}>
          <Button block size="large" type="primary" onClick={() => setDecisionDraft('Accepted')}>Accept offer</Button>
          <Button block size="large" onClick={() => setDecisionDraft('Negotiation')}>Request discussion</Button>
          <Button block size="large" danger onClick={() => setDecisionDraft('Rejected')}>Decline offer</Button>
        </Space> : <Space direction="vertical" style={{ width: '100%' }}>
          {context.allowSaveDraft && <Button block size="large" icon={<SaveOutlined />} loading={savingDraft} onClick={() => void saveDraft()}>Save draft</Button>}
          <Button block size="large" type="primary" loading={submitting} onClick={() => void submitForm()}>Submit securely</Button>
        </Space>}
        <p style={{ marginTop: 12, color: '#7b8598', fontSize: 12 }}>Do not forward this personal link. HR can revoke it at any time.</p>
      </Card>
    </div>
    <Modal title={decisionDraft ? `${decisionDraft} offer` : 'Offer response'} open={!!decisionDraft} onCancel={() => setDecisionDraft(null)} onOk={() => decisionDraft && void complete(decisionDraft)} confirmLoading={submitting} okText={decisionDraft === 'Accepted' ? 'Confirm acceptance' : 'Submit response'} okButtonProps={{ danger: decisionDraft === 'Rejected', disabled: decisionDraft !== 'Accepted' && !remarks.trim() }}>
      <Form layout="vertical"><p>Your response is final for this secure link and will be recorded in the recruitment timeline.</p><Form.Item label={decisionDraft === 'Accepted' ? 'Note to HR (optional)' : 'Reason / message'} required={decisionDraft !== 'Accepted'}><Input.TextArea rows={4} value={remarks} onChange={event => setRemarks(event.target.value)} /></Form.Item></Form>
    </Modal>
  </div></main>
}

function OfferSummary({ context }: { context: PublicCandidateActionContext }) {
  const offer = context.offer!
  return <section><h2>Offer details</h2><div className="candidate-action-offer"><div><span>Offer number</span><b>{offer.offerNumber}</b></div><div><span>Annual CTC</span><b>{offer.currency} {Number(offer.offeredCtc).toLocaleString('en-IN')}</b></div><div><span>Proposed joining</span><b>{new Date(offer.proposedJoiningDate).toLocaleDateString('en-IN')}</b></div><div><span>Response due</span><b>{offer.expiryDate ? new Date(offer.expiryDate).toLocaleDateString('en-IN') : 'As communicated by HR'}</b></div></div>{offer.documentUrl && <Button icon={<FileProtectOutlined />} onClick={() => window.open(offer.documentUrl!, '_blank', 'noopener,noreferrer')}>View secured offer letter</Button>}</section>
}
