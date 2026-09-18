import { useEffect, useState } from 'react'
import { ArrowRightOutlined, BankOutlined, CheckCircleOutlined, CheckOutlined, ClearOutlined, ClockCircleOutlined, EnvironmentOutlined, FileTextOutlined, LaptopOutlined, LockOutlined, MailOutlined, PhoneOutlined, SafetyCertificateOutlined, TeamOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Form, Input, Modal, Popconfirm, Result, Skeleton, Space, Tag } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import { useToast } from '../components/ToastProvider'
import { getPublicOrganizationBrand } from '../services/settingsService'
import {
  createPublicApplicationSession, createPublicApplicationTrackingSession, deletePublicApplicationFile, fetchPublicApplicationFile, getPublicApplicationProcessingStatus, getPublicApplicationTracker, getPublicCareerJobResult, loadPublicSelectOptions, requestPublicApplicationVerification, savePublicApplicationValues,
  submitPublicApplication, uploadPublicApplicationFile,
} from '../services/recruitmentOrchestrationService'
import type {
  DynamicFormField, PublicApplicationProcessingStatus, PublicApplicationSession, PublicApplicationVerification, PublicCandidateApplicationTracker, PublicFormValue, PublicRecruitmentJob, PublicUploadedFile, PublicUploadMetadata,
} from '../types/recruitmentOrchestration'
import { formatApiDateTime } from '../utils/apiDateTime'
import '../components/RecruitmentOrchestration.css'
import './PublicCareersPage.css'

type Props = { slug?: string }

export default function PublicCareersPage({ slug: suppliedSlug }: Props) {
  const notify = useToast()
  const slug = suppliedSlug || decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '')
  const [job, setJob] = useState<PublicRecruitmentJob | null>(null)
  const [brand, setBrand] = useState<{ name: string; logoDataUrl: string } | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadFailure, setLoadFailure] = useState<'not-found' | 'unavailable' | ''>('')
  const [session, setSession] = useState<PublicApplicationSession | null>(null)
  const [verification, setVerification] = useState<PublicApplicationVerification | null>(null)
  const [verificationCode, setVerificationCode] = useState('')
  const [resendRemaining, setResendRemaining] = useState(0)
  const [email, setEmail] = useState('')
  const [phone, setPhone] = useState('')
  const [values, setValues] = useState<PublicFormValue[]>([])
  const [files, setFiles] = useState<PublicUploadedFile[]>([])
  const [consent, setConsent] = useState(false)
  const [starting, setStarting] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [validationError, setValidationError] = useState('')
  const [resumePrefill, setResumePrefill] = useState<{ status: string; message: string } | null>(null)
  const [filePreview, setFilePreview] = useState<{ name: string; text: string } | null>(null)
  const [result, setResult] = useState<{ applicationCode: string; message: string; status?: string } | null>(null)
  const [processingStatus, setProcessingStatus] = useState<PublicApplicationProcessingStatus | null>(null)
  const [trackingOpen, setTrackingOpen] = useState(false)
  const [idempotencyKey] = useState(() => typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `application-${Date.now()}-${Math.random().toString(36).slice(2)}`)

  useEffect(() => {
    let active = true
    void getPublicOrganizationBrand().then(row => { if (active) setBrand(row) })
    return () => { active = false }
  }, [])

  useEffect(() => {
    let active = true; setLoading(true)
    void getPublicCareerJobResult(slug).then(response => { if (active) { setJob(response.data); setLoadFailure(response.ok ? '' : response.status === 404 ? 'not-found' : 'unavailable'); setLoading(false) } })
    return () => { active = false }
  }, [slug])

  useEffect(() => {
    if (resendRemaining <= 0) return
    const timer = window.setInterval(() => setResendRemaining(value => Math.max(0, value - 1)), 1000)
    return () => window.clearInterval(timer)
  }, [resendRemaining])

  useEffect(() => {
    if (!result || !session?.sessionToken) return
    let active = true
    let timer = 0
    let attempts = 0
    const poll = async () => {
      const response = await getPublicApplicationProcessingStatus(session.sessionToken)
      if (!active) return
      if (response.ok && response.data) setProcessingStatus(response.data)
      attempts += 1
      if ((!response.data || response.data.status === 'Processing') && attempts < 40)
        timer = window.setTimeout(() => void poll(), 1500)
    }
    setProcessingStatus({ applicationCode: result.applicationCode, status: 'Processing', resumeStatus: 'Pending', atsStatus: 'Waiting', message: 'Your application is saved. Resume processing is continuing.' })
    void poll()
    return () => { active = false; window.clearTimeout(timer) }
  }, [result, session?.sessionToken])

  const sendVerificationCode = async () => {
    if (!email.trim()) { notify('Enter your email address.', 'warning'); return }
    if (!phone.trim()) { notify('Enter your phone number.', 'warning'); return }
    if (!consent) { notify('Accept the candidate-data consent statement to continue.', 'warning'); return }
    setStarting(true)
    const response = await requestPublicApplicationVerification(slug, email.trim(), phone.trim())
    setStarting(false)
    if (!response.ok || !response.data) { notify(response.error || 'Unable to send the verification code.', 'error'); return }
    setVerification(response.data)
    setVerificationCode('')
    setResendRemaining(response.data.resendAfterSeconds)
    notify(`Verification code sent to ${response.data.maskedEmail}.`, 'success')
  }
  const begin = async () => {
    if (job?.requiresEmailVerification !== false && !verification) return void sendVerificationCode()
    if (job?.requiresEmailVerification !== false && !/^\d{6}$/.test(verificationCode)) { notify('Enter the 6-digit code sent to your email.', 'warning'); return }
    if (!email.trim()) { notify('Enter your email address.', 'warning'); return }
    if (!phone.trim()) { notify('Enter your phone number.', 'warning'); return }
    if (!consent) { notify('Accept the candidate-data consent statement to continue.', 'warning'); return }
    setStarting(true)
    const response = await createPublicApplicationSession(slug, { email: email.trim(), phone: phone.trim(), verificationToken: verification?.verificationToken ?? '', verificationCode, idempotencyKey, consentAccepted: true })
    setStarting(false)
    if (!response.ok || !response.data) { notify(response.error || 'Unable to verify your email.', 'error'); return }
    setSession(response.data)
    setValues(response.data.initialValues ?? [])
    window.setTimeout(() => document.getElementById('public-application-form')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50)
  }
  const upload = async (field: DynamicFormField, file: File, metadata: PublicUploadMetadata, onProgress: (percent: number) => void) => {
    if (!session) return { ok: false, error: 'Start the application before uploading files.' }
    const response = await uploadPublicApplicationFile(session.sessionToken, field.id, file, metadata, onProgress)
    if (response.ok && response.data) {
      setFiles(current => [...current, { ...response.data, fieldId: response.data.fieldId || field.id }])
      if (response.data.suggestedValues?.length) {
        setValues(current => mergeResumeSuggestions(current, response.data!.suggestedValues!))
        setResumePrefill({ status: 'success', message: 'Resume parsed. Please review the prefilled details before submitting.' })
      } else if (response.data.parsingStatus && !['Parsed', 'NotApplicable'].includes(response.data.parsingStatus)) {
        setResumePrefill({ status: 'warning', message: response.data.parsingError || 'Resume uploaded. Complete the remaining details manually.' })
      }
      setValidationError('')
    }
    return { ok: response.ok, error: response.error }
  }
  const previewFile = async (field: DynamicFormField, file: PublicUploadedFile) => {
    if (!session) return
    if (file.previewText?.trim()) { setFilePreview({ name: file.originalFileName, text: file.previewText }); return }
    const publicId = file.attachmentPublicId || file.publicId
    if (!publicId) { notify('The uploaded file reference is unavailable.', 'error'); return }
    try {
      const response = await fetchPublicApplicationFile(session.sessionToken, field.id, publicId)
      if (!response.ok) { notify(await response.text() || 'Unable to preview this file.', 'error'); return }
      const url = URL.createObjectURL(await response.blob())
      window.open(url, '_blank', 'noopener,noreferrer')
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } catch { notify('Unable to preview this file.', 'error') }
  }
  const removeFile = async (_field: DynamicFormField, file: PublicUploadedFile) => {
    if (!session) return { ok: false, error: 'Application session is unavailable.' }
    const publicId = file.attachmentPublicId || file.publicId
    if (!publicId) return { ok: false, error: 'The uploaded file reference is unavailable.' }
    const response = await deletePublicApplicationFile(session.sessionToken, file.fieldId, publicId)
    if (!response.ok) return { ok: false, error: response.error || 'Unable to remove this file.' }
    const suggestedFieldIds = new Set((file.suggestedValues ?? []).map(value => value.fieldId))
    const initialValues = (session.initialValues ?? []).filter(value => suggestedFieldIds.has(value.fieldId))
    setValues(current => [...current.filter(value => !suggestedFieldIds.has(value.fieldId)), ...initialValues])
    setFiles(current => current.filter(item => (item.attachmentPublicId || item.publicId) !== publicId))
    setResumePrefill(null)
    setValidationError('')
    return { ok: true }
  }
  const submit = async () => {
    if (!job?.applicationForm || !session) return
    const error = validateDynamicForm(job.applicationForm, values, files)
    if (error) { setValidationError(error); notify(error, 'warning'); return }
    setValidationError('')
    setSubmitting(true)
    const saved = await savePublicApplicationValues(session.sessionToken, values)
    if (!saved.ok) { setSubmitting(false); notify(saved.error || 'Unable to save your application.', 'error'); return }
    const response = await submitPublicApplication(session.sessionToken)
    setSubmitting(false)
    if (!response.ok || !response.data) { notify(response.error || 'Unable to submit your application.', 'error'); return }
    setResult(response.data)
  }
  const clearStart = () => {
    setEmail('')
    setPhone('')
    setConsent(false)
    setVerification(null)
    setVerificationCode('')
    setResendRemaining(0)
    setValidationError('')
  }
  const clearApplication = async () => {
    if (!session) return
    setSubmitting(true)
    for (const file of files) {
      const publicId = file.attachmentPublicId || file.publicId
      if (!publicId) continue
      const response = await deletePublicApplicationFile(session.sessionToken, file.fieldId, publicId)
      if (!response.ok) {
        setSubmitting(false)
        notify(response.error || `Unable to remove ${file.originalFileName}.`, 'error')
        return
      }
    }
    setSubmitting(false)
    setValues(session?.initialValues ?? [])
    setFiles([])
    setValidationError('')
    setResumePrefill(null)
  }

  const branding = <CareersBrand name={brand?.name || job?.clientName || 'Careers'} logo={brand?.logoDataUrl} />
  if (loading) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<Card><Skeleton active paragraph={{ rows: 12 }} /></Card></div></main>
  if (!job) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<div className="public-success"><Result status={loadFailure === 'unavailable' ? 'error' : '404'} title={loadFailure === 'unavailable' ? 'Unable to load this job' : 'Job posting not found'} subTitle={loadFailure === 'unavailable' ? 'The recruitment service is temporarily unavailable. Please try again shortly.' : 'This link may be incorrect, closed or no longer public.'} /></div></div></main>
  if (result) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<div className="public-success"><Result status="success" title={result.status === 'ResumeUpdated' ? 'Resume updated' : 'Application submitted'} subTitle={result.message || `Your application reference is ${result.applicationCode}.`} extra={<Space direction="vertical" size={12}><Tag color="green" icon={<CheckCircleOutlined />}>{result.applicationCode}</Tag><Alert showIcon type={processingStatus?.status === 'NeedsReview' ? 'warning' : processingStatus?.status === 'Completed' ? 'success' : 'info'} message={processingStatus?.status === 'NeedsReview' ? 'Processing issue detected' : processingStatus?.status === 'Completed' ? 'Processing complete' : 'Processing resume'} description={processingStatus?.message || 'Your application is saved. Resume processing is continuing.'} /><Button onClick={() => setTrackingOpen(true)}>View complete application status</Button><p>Keep this APP reference and your 6-digit tracking PIN to check this application later.</p></Space>} /></div></div><PublicApplicationTrackerModal open={trackingOpen} slug={slug} initialApplicationCode={result.applicationCode} initialPin={verificationCode} onClose={() => setTrackingOpen(false)} /></main>

  const closed = job.availabilityStatus === 'Closed'
  const unavailable = !job.isAcceptingApplications || !job.applicationForm
  const availabilityMessage = job.availabilityStatus === 'Scheduled'
    ? `Applications open on ${job.opensAtUtc ? new Date(job.opensAtUtc).toLocaleString('en-IN') : 'the configured opening date'}.`
    : job.availabilityStatus === 'Closed'
      ? 'The application window for this published job has closed.'
      : job.availabilityStatus === 'Full'
        ? 'This job has reached its configured application limit.'
        : job.availabilityStatus === 'FormUnavailable' || !job.applicationForm
          ? 'The application form is temporarily unavailable. Please contact the hiring team.'
          : job.availabilityStatus === 'ProofConfigurationUnavailable'
            ? 'Certification document collection is temporarily unavailable. Please contact the hiring team.'
          : ''
  const needsVerification = job.requiresEmailVerification !== false
  const deadline = job.closesAtUtc ? new Date(job.closesAtUtc).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' }) : ''
  const startLabel = job.availabilityStatus === 'Scheduled' ? 'Applications not open yet'
    : closed ? 'Applications closed' : job.availabilityStatus === 'Full' ? 'Application limit reached'
    : job.availabilityStatus === 'ProofConfigurationUnavailable' ? 'Document collection unavailable'
    : !job.applicationForm ? 'Application form unavailable'
    : needsVerification ? 'Email verification code' : 'Continue to application'

  return <main className="public-career-page careers-page">
    <div className={`public-career-shell${session ? ' has-application-session' : ''}`}>
      {branding}
      <div className="careers-breadcrumb"><span>Careers</span><span aria-hidden="true">/</span><span>Job details</span></div>
      <div className={`public-job-layout${session ? ' is-applying' : ''}`}>
        <section className="public-job-hero" aria-labelledby="careers-job-title">
          <div className="careers-role-eyebrow"><span className={`careers-availability${unavailable ? ' is-unavailable' : ''}`}>{unavailable ? 'Applications not open' : 'Applications open'}</span>{job.positionCode && <span className="careers-job-reference">Job ID: {job.positionCode}</span>}</div>
          <p className="careers-hiring-for">Hiring for {job.clientName}</p>
          <h1 id="careers-job-title">{job.publicTitle || job.positionTitle}</h1>
          <div className="public-job-tags">
            {job.jobLocation && <span><EnvironmentOutlined />{job.jobLocation}</span>}
            {job.employmentType && <span><FileTextOutlined />{job.employmentType}</span>}
            {job.workMode && <span><LaptopOutlined />{job.workMode}</span>}
            {job.department && <span><TeamOutlined />{job.department}</span>}
          </div>
          {(job.summary || job.rolePurpose) && <p className="careers-role-summary">{job.summary || job.rolePurpose}</p>}
          {deadline && <p className="careers-deadline"><ClockCircleOutlined /><span>{closed ? 'Applications closed on' : 'Apply by'} <strong>{deadline}</strong></span></p>}
        </section>

        <Card className="public-apply-card" id="public-application-form">
          <ol className="careers-application-steps" aria-label="Application progress">
            {['Contact', 'Your profile', 'Submit'].map((label, index) => <li key={label} className={index === (session ? 1 : 0) ? 'is-current' : session && index === 0 ? 'is-complete' : ''} aria-current={index === (session ? 1 : 0) ? 'step' : undefined}><span>{session && index === 0 ? <CheckOutlined /> : index + 1}</span>{label}</li>)}
          </ol>
          {availabilityMessage && <Alert showIcon type={job.availabilityStatus === 'Scheduled' ? 'info' : 'warning'} message="Application status" description={availabilityMessage} className="careers-status-alert" />}
          {!session ? <>
            <div className="public-apply-heading">
              <span className="careers-apply-icon" aria-hidden="true">{verification ? <MailOutlined /> : <ArrowRightOutlined />}</span>
              <h2>{verification ? 'Check your inbox' : 'Start your application'}</h2>
              <p>{verification ? `Enter the 6-digit code sent to ${verification.maskedEmail}.` : 'Your next opportunity starts here. Let’s begin with your contact details.'}</p>
            </div>
            {!verification ? <Form className="public-start-form" layout="vertical" onFinish={() => void (needsVerification ? sendVerificationCode() : begin())}>
              <Form.Item label="Email address" htmlFor="careers-email" required>
                <Input id="careers-email" prefix={<MailOutlined />} size="large" type="email" autoComplete="email" value={email} disabled={unavailable || starting} onChange={event => setEmail(event.target.value)} placeholder="name@example.com" />
              </Form.Item>
              <Form.Item label="Phone number" htmlFor="careers-phone" required>
                <Input id="careers-phone" prefix={<PhoneOutlined />} size="large" type="tel" autoComplete="tel" value={phone} disabled={unavailable || starting} onChange={event => setPhone(event.target.value)} placeholder="Your mobile number" />
              </Form.Item>
              <p className="careers-contact-note">{needsVerification ? 'We’ll email you a code to verify your email address.' : 'These details will be filled in on your application.'}</p>
              <Checkbox className="public-consent" checked={consent} disabled={unavailable || starting} onChange={event => setConsent(event.target.checked)}>I consent to the use of my information for this recruitment process and the configured retention period.</Checkbox>
              <div className="public-start-actions">
                <Button block size="large" type="primary" htmlType="submit" loading={starting} disabled={unavailable}><span>{startLabel}</span>{!unavailable && <ArrowRightOutlined />}</Button>
                <Button type="text" size="small" icon={<ClearOutlined />} disabled={starting || (!email && !phone && !consent)} onClick={clearStart}>Clear</Button>
              </div>
            </Form> : <Form className="public-otp-panel" layout="vertical" onFinish={() => void begin()}>
              <label htmlFor="application-verification-code">Verification code</label>
              <Input id="application-verification-code" aria-label="Verification code" prefix={<LockOutlined />} size="large" inputMode="numeric" autoComplete="one-time-code" autoFocus maxLength={6} value={verificationCode} disabled={starting} onChange={event => setVerificationCode(event.target.value.replace(/\D/g, '').slice(0, 6))} placeholder="Enter 6-digit code" />
              <Button block size="large" type="primary" htmlType="submit" loading={starting}>Verify & open application <ArrowRightOutlined /></Button>
              <div className="public-otp-actions"><Button type="link" disabled={resendRemaining > 0 || starting} onClick={() => void sendVerificationCode()}>{resendRemaining > 0 ? `Resend in ${resendRemaining}s` : 'Resend code'}</Button><Button type="link" disabled={starting} onClick={() => { setVerification(null); setVerificationCode('') }}>Change email or phone</Button></div>
              <small>Code expires at {new Date(verification.expiresAtUtc).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}.</small>
            </Form>}
            <div className="careers-privacy-note"><LockOutlined /><span>Your information and documents are shared privately with the hiring team.</span></div>
            <Button className="public-track-link" type="link" onClick={() => setTrackingOpen(true)}>Already applied? Track your application</Button>
          </> : job.applicationForm && <>
            <div className="public-apply-heading public-application-heading"><div><span className="careers-section-eyebrow">Tell us about yourself</span><h2>Application details</h2></div><Space wrap><Popconfirm title="Clear this form?" description="Entered values and selected uploads will be reset. Verified contact details stay filled." okText="Clear" cancelText="Keep editing" onConfirm={clearApplication}><Button icon={<ClearOutlined />} disabled={submitting}>Clear form</Button></Popconfirm><Tag color="green" icon={<CheckCircleOutlined />}>{needsVerification ? 'Email verified' : 'Contact captured'}</Tag></Space></div>
            {session.message && <Alert data-testid="existing-candidate-message" showIcon type="info" message={session.existingApplicationCode ? 'Already registered' : 'Existing profile found'} description={session.message} style={{ marginBottom: 16 }} />}
            {resumePrefill && <Alert showIcon type={resumePrefill.status === 'success' ? 'success' : 'warning'} message={resumePrefill.message} style={{ marginBottom: 16 }} />}
            <RecruitmentDynamicForm form={job.applicationForm} values={values} files={files} lockedSemanticCodes={['EMAIL', 'PHONE']} prioritizedSemanticCodes={['RESUME']} onChange={next => { setValues(next); setValidationError('') }} onUpload={upload} onPreviewFile={previewFile} onRemoveFile={removeFile} onLoadOptions={(field, search) => loadPublicSelectOptions(session.sessionToken, field.id, search)} />
            {validationError && <Alert data-testid="public-application-validation" type="error" showIcon message="Please review your application" description={validationError} style={{ marginTop: 16 }} />}
            <Button className="public-submit-button" block size="large" type="primary" loading={submitting} onClick={() => void submit()}>Submit application <ArrowRightOutlined /></Button>
            <p className="public-session-note"><LockOutlined /> Your application session expires {new Date(session.expiresAtUtc).toLocaleString('en-IN')}.</p>
          </>}
        </Card>

        <Card className="public-job-content">
          <div className="careers-details-heading"><FileTextOutlined /><h2>Role overview</h2></div>
          <div className="public-job-copy">
            {(job.rolePurpose || job.summary) && <section><h3>About the role</h3><p>{job.rolePurpose || job.summary}</p></section>}
            {!!job.responsibilities.length && <section><h3>What you will do</h3><ul>{job.responsibilities.map(item => <li key={item.id}>{item.responsibilityText}</li>)}</ul></section>}
            {!!job.qualifications.length && <section><h3>What we are looking for</h3><ul>{job.qualifications.map(item => <li key={item.id}>{item.qualificationName}{item.specialization ? ` — ${item.specialization}` : ''}{item.isMandatory ? ' (required)' : ''}</li>)}</ul></section>}
            {!!job.skills.length && <section><h3>Skills you’ll bring</h3><Space wrap>{job.skills.map(item => <Tag color={item.isRequired ? 'cyan' : 'default'} key={item.id}>{item.skillName}{item.minimumYears ? ` · ${item.minimumYears}+ yrs` : ''}</Tag>)}</Space></section>}
            {!!job.certifications?.length && <section><h3>Certifications</h3><ul>{job.certifications.map(item => <li key={item.id}>{item.certificationName}{item.isMandatory ? ' (required)' : ''}{item.candidateProofRequested && <Tag className="public-proof-tag" color="blue" icon={<SafetyCertificateOutlined />}>Supporting document requested</Tag>}</li>)}</ul></section>}
            {!!job.languages?.length && <section><h3>Languages</h3><Space wrap>{job.languages.map(item => <Tag color={item.isMandatory ? 'cyan' : 'default'} key={item.id}>{item.languageName}{item.proficiency ? ` · ${item.proficiency}` : ''}{item.isMandatory ? ' · required' : ''}</Tag>)}</Space></section>}
            {!!job.benefits?.length && <section><h3>Benefits</h3><ul>{job.benefits.map(item => <li key={item.id}><b>{item.benefitName}</b>{item.description ? `: ${item.description}` : ''}</li>)}</ul></section>}
          </div>
        </Card>
      </div>
      <footer className="careers-footer"><span>{job.clientName} · Careers</span><span>Powered by <b>Frevo</b></span></footer>
      <Modal open={Boolean(filePreview)} title={filePreview?.name || 'Resume preview'} footer={<Button type="primary" onClick={() => setFilePreview(null)}>Close</Button>} onCancel={() => setFilePreview(null)} width={820}>
        <pre className="public-resume-preview-text">{filePreview?.text}</pre>
      </Modal>
      <PublicApplicationTrackerModal open={trackingOpen} slug={slug} initialApplicationCode="" initialPin="" onClose={() => setTrackingOpen(false)} />
    </div>
  </main>
}

function PublicApplicationTrackerModal({ open, slug, initialApplicationCode, initialPin, onClose }: { open: boolean; slug: string; initialApplicationCode: string; initialPin: string; onClose: () => void }) {
  const [applicationCode, setApplicationCode] = useState(initialApplicationCode)
  const [pin, setPin] = useState(initialPin)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [tracker, setTracker] = useState<PublicCandidateApplicationTracker | null>(null)

  useEffect(() => {
    if (!open) return
    if (!applicationCode && initialApplicationCode) setApplicationCode(initialApplicationCode)
    if (!pin && initialPin) setPin(initialPin)
  }, [open, initialApplicationCode, initialPin, applicationCode, pin])

  const login = async () => {
    setBusy(true)
    setError('')
    const sessionResponse = await createPublicApplicationTrackingSession(slug, applicationCode.trim(), pin.trim())
    if (!sessionResponse.ok || !sessionResponse.data) {
      setBusy(false)
      setError(sessionResponse.error || 'Unable to open application tracking.')
      return
    }
    const trackerResponse = await getPublicApplicationTracker(sessionResponse.data.trackingToken)
    setBusy(false)
    if (!trackerResponse.ok || !trackerResponse.data) {
      setError(trackerResponse.error || 'Unable to load your applications.')
      return
    }
    setTracker(trackerResponse.data)
  }

  const close = () => { setError(''); setTracker(null); onClose() }
  return <Modal className="public-tracker-modal" open={open} title="Track your applications" footer={null} onCancel={close} width="min(920px, 96vw)" destroyOnClose>
    {!tracker ? <Form layout="vertical" onFinish={() => void login()}>
      <Alert showIcon type="info" message="Track your application" description="Use the APP reference shown after submission and the 6-digit tracking PIN sent during your first verification." style={{ marginBottom: 16 }} />
      <Form.Item label="Application reference" required><Input prefix={<FileTextOutlined />} autoComplete="off" placeholder="APP-UIDAI-000072" value={applicationCode} onChange={event => setApplicationCode(event.target.value.toUpperCase())} /></Form.Item>
      <Form.Item label="Tracking PIN" required><Input.Password prefix={<LockOutlined />} inputMode="numeric" autoComplete="current-password" maxLength={6} value={pin} onChange={event => setPin(event.target.value.replace(/\D/g, '').slice(0, 6))} /></Form.Item>
      {error && <Alert showIcon type="error" message={error} style={{ marginBottom: 16 }} />}
      <Button block type="primary" htmlType="submit" loading={busy}>View application status</Button>
    </Form> : <section className="public-tracker">
      <header><div><span>Candidate</span><h2>{tracker.candidateName || 'Your applications'}</h2></div><Tag color="blue">{tracker.applications.length} application{tracker.applications.length === 1 ? '' : 's'}</Tag></header>
      <div className="public-tracker-list">{tracker.applications.map(application => <article key={application.applicationId} className="public-tracker-card">
        <div className="public-tracker-title"><div><span>{application.applicationCode}</span><h3>{application.positionTitle}</h3><small>Applied {formatApiDateTime(application.appliedAt)}</small></div><Tag color={application.currentStatus === 'Rejected' ? 'red' : application.currentStatus === 'Joined' ? 'green' : 'blue'}>{application.currentStage || application.currentStatus}</Tag></div>
        <Alert showIcon type={application.processingStatus === 'NeedsReview' ? 'warning' : application.processingStatus === 'Completed' ? 'success' : 'info'} message={application.processingMessage} />
        <div className="public-tracker-grid">
          <div><h4>Application timeline</h4><ol>{application.timeline.map((stage, index) => <li key={`${stage.stage}-${stage.changedAt}-${index}`}><i /><div><b>{stage.stage}</b><small>{formatApiDateTime(stage.changedAt)}</small></div></li>)}</ol></div>
          <div><h4>Interviews</h4>{application.interviews.length ? application.interviews.map((interview, index) => <div className="public-tracker-event" key={`${interview.round}-${index}`}><b>{interview.round}</b><span>{formatApiDateTime(interview.scheduledStart)} · {interview.mode}</span><Tag color={interview.status === 'Completed' ? 'green' : interview.status === 'Cancelled' || interview.status === 'No Show' ? 'red' : 'blue'}>{interview.status}{interview.result && interview.result !== 'Pending' ? ` · ${interview.result}` : ''}</Tag>{interview.locationOrLink && <a href={interview.locationOrLink.startsWith('http') ? interview.locationOrLink : undefined} target="_blank" rel="noreferrer">{interview.locationOrLink}</a>}</div>) : <p>No interview scheduled yet.</p>}
          {application.offer && <><h4>Offer & joining</h4><div className="public-tracker-event"><b>{application.offer.offerNumber}</b><Tag color={application.offer.status === 'Accepted' ? 'green' : 'blue'}>{application.offer.status}</Tag><span>Proposed joining: {new Date(application.offer.proposedJoiningDate).toLocaleDateString('en-IN')}</span></div></>}</div>
        </div>
      </article>)}</div>
    </section>}
  </Modal>
}

function CareersBrand({ name, logo }: { name: string; logo?: string }) {
  const initials = name.trim().split(/\s+/).slice(0, 2).map(word => word[0]).join('').toUpperCase()
  return <header className="public-career-brand">
    <div className="careers-company">{logo ? <img className="careers-organization-logo" src={logo} alt={`${name} logo`} /> : <span className="careers-company-mark" aria-hidden="true">{initials || <BankOutlined />}</span>}<div><b>{name}</b><span>Careers & opportunities</span></div></div>
    <span className="careers-header-note"><LockOutlined /> Candidate application</span>
  </header>
}

function mergeResumeSuggestions(current: PublicFormValue[], suggestions: PublicFormValue[]) {
  const next = new Map(current.map(value => [value.fieldId, value]))
  for (const suggestion of suggestions) {
    const existing = next.get(suggestion.fieldId)
    const alreadyFilled = Boolean(existing?.textValue?.trim())
      || existing?.integerValue != null
      || existing?.decimalValue != null
      || Boolean(existing?.dateValue || existing?.dateTimeValue)
      || existing?.booleanValue != null
      || Boolean(existing?.selectedOptionIds?.length || existing?.selectedOptionValues?.length)
    if (!alreadyFilled) next.set(suggestion.fieldId, suggestion)
  }
  return [...next.values()]
}
