import { useEffect, useState } from 'react'
import { CheckCircleOutlined, ClearOutlined, FileProtectOutlined, SafetyCertificateOutlined, SaveOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Form, Input, Modal, Popconfirm, Result, Skeleton, Space, Tag, message } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import OfferLetterPreview from '../components/OfferLetterPreview'
import {
  completePublicCandidateAction, getPublicCandidateAction, loadPublicCandidateActionOptions, savePublicCandidateActionValues,
  uploadPublicCandidateActionFile,
} from '../services/recruitmentOrchestrationService'
import type {
  CandidateActionDecision, DynamicFormField, PublicCandidateActionContext, PublicFormValue, PublicUploadedFile, PublicUploadMetadata,
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
  const [validationError, setValidationError] = useState('')
  const [completed, setCompleted] = useState<{ status: string; message: string } | null>(null)

  useEffect(() => {
    let active = true
    setLoading(true)
    void getPublicCandidateAction(token).then(row => {
      if (!active) return
      setContext(row || null); setValues([...(row?.existingValues ?? [])]); setFiles([...(row?.uploadedFiles ?? [])]); setLoading(false)
    })
    return () => { active = false }
  }, [token])

  const upload = async (field: DynamicFormField, file: File, metadata: PublicUploadMetadata, onProgress: (percent: number) => void) => {
    const response = await uploadPublicCandidateActionFile(token, field.id, file, metadata, onProgress)
    if (response.ok && response.data) {
      setFiles(current => [...current, { ...response.data, fieldId: response.data.fieldId || field.id }])
      setValidationError('')
    }
    return { ok: response.ok, error: response.error }
  }
  const submitForm = async () => {
    if (!context?.form) return
    const error = validateDynamicForm(context.form, values, files)
    if (error) { setValidationError(error); return message.warning(error) }
    setValidationError('')
    await complete(undefined)
  }
  const clearForm = () => {
    setValues([...(context?.existingValues ?? [])])
    setValidationError('')
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
  const disabled = expired || completedBefore
  const actionTitle = context.purpose === 'OfferResponse' ? 'Your offer response' : 'Candidate information'
  return <main className="public-career-page"><div className="public-career-shell has-application-session">
    <header className="public-career-brand"><div><span className="orchestration-kicker">Secure candidate portal</span><b>{context.organizationName}</b></div></header>
    <Card className="public-job-hero"><Space wrap><Tag color="purple">{context.purpose.replace(/([A-Z])/g, ' $1').trim()}</Tag><Tag color={expired ? 'red' : 'blue'}>{expired ? 'Expired' : `Valid until ${new Date(context.expiresAtUtc).toLocaleString('en-IN')}`}</Tag></Space><h1>{context.candidateName}</h1><p>{context.positionTitle}</p>{context.message && <Alert showIcon type="info" message={context.message} />}</Card>
    <div className="public-job-layout is-applying candidate-action-layout">
      <Card className="public-job-content candidate-action-context-card">
        <div className="candidate-action-context-heading"><FileProtectOutlined /><div><span className="orchestration-kicker">Requested action</span><h2>{context.purpose === 'OfferResponse' ? 'Review your offer' : 'Review and complete'}</h2><p>Details already available with HR are prefilled. Review them and complete only the missing information.</p></div></div>
        <div className="candidate-action-context-list"><div><span>Candidate</span><b>{context.candidateName}</b></div><div><span>Position</span><b>{context.positionTitle}</b></div><div><span>Link validity</span><b>{new Date(context.expiresAtUtc).toLocaleString('en-IN')}</b></div></div>
        <Alert className="candidate-action-security" showIcon icon={<SafetyCertificateOutlined />} type="info" message="Protected candidate action" description="Your responses and documents are handled by the secured HRMS document service." />
        {context.purpose === 'OfferResponse' && context.offer && <OfferSummary context={context} />}
      </Card>
      <Card className="public-apply-card" id="public-candidate-action-form">
        <div className="public-apply-heading public-application-heading"><div><span className="orchestration-kicker">Verified candidate action</span><h2>{actionTitle}</h2><p>Review the prefilled details and provide only what is still missing.</p></div>{context.form && <Space wrap><Popconfirm title="Reset your field changes?" description="Entered field changes will reset to the HR records. Uploaded documents stay securely attached." okText="Reset" cancelText="Keep editing" onConfirm={clearForm}><Button icon={<ClearOutlined />} disabled={disabled || submitting || savingDraft}>Clear changes</Button></Popconfirm><Tag color="green" icon={<CheckCircleOutlined />}>Secure link</Tag></Space>}</div>
        {expired ? <Alert type="error" showIcon message="This link has expired. Request a new secure link from HR." /> : completedBefore ? <Alert type="warning" showIcon message={context.message || `This action is ${context.status.toLowerCase()}.`} /> : <>
          {context.form && <RecruitmentDynamicForm disabled={disabled} form={context.form} values={values} files={files} onChange={next => { setValues(next); setValidationError('') }} onUpload={upload} onLoadOptions={(field, search) => loadPublicCandidateActionOptions(token, field.id, search)} />}
          {validationError && <Alert data-testid="public-candidate-action-validation" type="error" showIcon message="Please review your information" description={validationError} style={{ marginTop: 16 }} />}
          {context.purpose === 'OfferResponse' ? <div className="candidate-action-decision-actions"><Button size="large" type="primary" onClick={() => setDecisionDraft('Accepted')}>Accept offer</Button><Button size="large" onClick={() => setDecisionDraft('Negotiation')}>Request discussion</Button><Button size="large" danger onClick={() => setDecisionDraft('Rejected')}>Decline offer</Button></div> : context.form ? <div className={`candidate-action-form-actions${context.allowSaveDraft ? ' has-draft' : ''}`}>{context.allowSaveDraft && <Button size="large" icon={<SaveOutlined />} loading={savingDraft} disabled={submitting} onClick={() => void saveDraft()}>Save draft</Button>}<Button className="public-submit-button" block size="large" type="primary" loading={submitting} disabled={savingDraft} onClick={() => void submitForm()}>Submit securely</Button></div> : <Alert type="info" showIcon message="No additional information is requested for this action." />}
        </>}
        <p className="public-session-note">Do not forward this personal link. HR can revoke it at any time. Files are handled only by the secured HRMS document service.</p>
      </Card>
    </div>
    <Modal title={decisionDraft ? `${decisionDraft} offer` : 'Offer response'} open={!!decisionDraft} onCancel={() => setDecisionDraft(null)} onOk={() => decisionDraft && void complete(decisionDraft)} confirmLoading={submitting} okText={decisionDraft === 'Accepted' ? 'Confirm acceptance' : 'Submit response'} okButtonProps={{ danger: decisionDraft === 'Rejected', disabled: decisionDraft !== 'Accepted' && !remarks.trim() }}>
      <Form layout="vertical"><p>Your response is final for this secure link and will be recorded in the recruitment timeline.</p><Form.Item label={decisionDraft === 'Accepted' ? 'Note to HR (optional)' : 'Reason / message'} required={decisionDraft !== 'Accepted'}><Input.TextArea rows={4} value={remarks} onChange={event => setRemarks(event.target.value)} /></Form.Item></Form>
    </Modal>
  </div></main>
}

function OfferSummary({ context }: { context: PublicCandidateActionContext }) {
  const offer = context.offer!
  return <section><h2>Offer details</h2><div className="candidate-action-offer"><div><span>Offer number</span><b>{offer.offerNumber}</b></div><div><span>Annual CTC</span><b>{offer.currency} {Number(offer.offeredCtc).toLocaleString('en-IN')}</b></div><div><span>Proposed joining</span><b>{new Date(offer.proposedJoiningDate).toLocaleDateString('en-IN')}</b></div><div><span>Response due</span><b>{offer.expiryDate ? new Date(offer.expiryDate).toLocaleDateString('en-IN') : 'As communicated by HR'}</b></div></div>{offer.documentUrl && <OfferLetterPreview documentUrl={offer.documentUrl} title={`Offer ${offer.offerNumber}`} label="View secured offer letter" />}</section>
}
