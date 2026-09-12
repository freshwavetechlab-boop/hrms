import { useEffect, useState } from 'react'
import { CheckCircleOutlined, ClockCircleOutlined, EnvironmentOutlined, LockOutlined, MailOutlined, SafetyCertificateOutlined, TeamOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Form, Input, Result, Skeleton, Space, Tag } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import { useToast } from '../components/ToastProvider'
import {
  createPublicApplicationSession, getPublicCareerJob, loadPublicSelectOptions, requestPublicApplicationVerification, savePublicApplicationValues,
  submitPublicApplication, uploadPublicApplicationFile,
} from '../services/recruitmentOrchestrationService'
import type {
  DynamicFormField, PublicApplicationSession, PublicApplicationVerification, PublicFormValue, PublicRecruitmentJob, PublicUploadedFile, PublicUploadMetadata,
} from '../types/recruitmentOrchestration'
import '../components/RecruitmentOrchestration.css'

type Props = { slug?: string }

export default function PublicCareersPage({ slug: suppliedSlug }: Props) {
  const notify = useToast()
  const slug = suppliedSlug || decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '')
  const [job, setJob] = useState<PublicRecruitmentJob | null>(null)
  const [loading, setLoading] = useState(true)
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
  const [result, setResult] = useState<{ applicationCode: string; message: string } | null>(null)
  const [idempotencyKey] = useState(() => typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `application-${Date.now()}-${Math.random().toString(36).slice(2)}`)

  useEffect(() => {
    let active = true; setLoading(true)
    void getPublicCareerJob(slug).then(row => { if (active) { setJob(row); setLoading(false) } })
    return () => { active = false }
  }, [slug])

  useEffect(() => {
    if (resendRemaining <= 0) return
    const timer = window.setInterval(() => setResendRemaining(value => Math.max(0, value - 1)), 1000)
    return () => window.clearInterval(timer)
  }, [resendRemaining])

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
    if (response.ok && response.data) { setFiles(current => [...current, { ...response.data, fieldId: response.data.fieldId || field.id }]); setValidationError('') }
    return { ok: response.ok, error: response.error }
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

  if (loading) return <main className="public-career-page"><div className="public-career-shell"><Card><Skeleton active paragraph={{ rows: 12 }} /></Card></div></main>
  if (!job) return <main className="public-career-page"><div className="public-career-shell public-success"><Result status="404" title="Job posting not found" subTitle="This link may be incorrect, closed or no longer public." /></div></main>
  if (result) return <main className="public-career-page"><div className="public-career-shell public-success"><Result status="success" title="Application submitted" subTitle={result.message || `Your application reference is ${result.applicationCode}.`} extra={<><Tag color="green" icon={<CheckCircleOutlined />}>{result.applicationCode}</Tag><p>Keep this reference for future communication. You can safely close this page.</p></>} /></div></main>

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
  return <main className="public-career-page"><div className={`public-career-shell${session ? ' has-application-session' : ''}`}>
    <header className="public-career-brand"><div><span className="orchestration-kicker">Careers</span><b>{job.clientName}</b></div></header>
    <Card className="public-job-hero">
      <div className="public-job-hero-copy">
        <span className="orchestration-kicker">Now hiring</span>
        <h1>{job.publicTitle || job.positionTitle}</h1>
        <p>{job.summary || job.rolePurpose}</p>
      </div>
      <div className="public-job-tags"><Tag icon={<TeamOutlined />}>{job.department}</Tag><Tag icon={<EnvironmentOutlined />}>{job.jobLocation}</Tag>{job.workMode && <Tag>{job.workMode}</Tag>}<Tag>{job.employmentType}</Tag>{job.closesAtUtc && <Tag icon={<ClockCircleOutlined />} color={closed ? 'red' : 'blue'}>{closed ? 'Applications closed' : `Apply by ${new Date(job.closesAtUtc).toLocaleDateString('en-IN')}`}</Tag>}</div>
    </Card>
    <div className={`public-job-layout${session ? ' is-applying' : ''}`}><Card className="public-job-content"><div className="public-job-copy"><h3>About the role</h3><p>{job.rolePurpose || job.summary}</p>{!!job.responsibilities.length && <><h3>What you will do</h3><ul>{job.responsibilities.map(item => <li key={item.id}>{item.responsibilityText}</li>)}</ul></>}{!!job.qualifications.length && <><h3>What we are looking for</h3><ul>{job.qualifications.map(item => <li key={item.id}>{item.qualificationName}{item.specialization ? ` - ${item.specialization}` : ''}{item.isMandatory ? ' (required)' : ''}</li>)}</ul></>}{!!job.skills.length && <><h3>Skills</h3><Space wrap>{job.skills.map(item => <Tag color={item.isRequired ? 'purple' : 'default'} key={item.id}>{item.skillName}{item.minimumYears ? ` - ${item.minimumYears}+ yrs` : ''}</Tag>)}</Space></>}{!!job.certifications?.length && <><h3>Certifications</h3><ul>{job.certifications.map(item => <li key={item.id}>{item.certificationName}{item.isMandatory ? ' (required)' : ''}{item.candidateProofRequested && <Tag className="public-proof-tag" color="blue" icon={<SafetyCertificateOutlined />}>Proof requested during the candidate journey</Tag>}</li>)}</ul></>}{!!job.languages?.length && <><h3>Languages</h3><Space wrap>{job.languages.map(item => <Tag color={item.isMandatory ? 'purple' : 'default'} key={item.id}>{item.languageName}{item.proficiency ? ` - ${item.proficiency}` : ''}{item.isMandatory ? ' - required' : ''}</Tag>)}</Space></>}{!!job.benefits?.length && <><h3>Benefits</h3><ul>{job.benefits.map(item => <li key={item.id}><b>{item.benefitName}</b>{item.description ? `: ${item.description}` : ''}</li>)}</ul></>}</div></Card>
      <Card className="public-apply-card" id="public-application-form">
        {availabilityMessage && <Alert showIcon type={job.availabilityStatus === 'Scheduled' ? 'info' : 'warning'} message="Application status" description={availabilityMessage} style={{ marginBottom: 16 }} />}
        {!session ? <><div className="public-apply-heading"><span className="orchestration-kicker">Secure application</span><h2>{verification ? 'Verify your email' : 'Start your application'}</h2><p>{verification ? `Enter the one-time code sent to ${verification.maskedEmail}.` : job.requiresEmailVerification === false ? 'Enter your contact details to open the application form.' : 'Verify your email before the application form opens. Your verified contact details will be filled automatically.'}</p></div><Alert showIcon type="info" icon={<SafetyCertificateOutlined />} message="Documents are private and never exposed through direct storage links." />
          {!verification ? <Form className="public-start-form" layout="vertical"><Form.Item label="Email address" required><Input prefix={<MailOutlined />} size="large" type="email" value={email} disabled={unavailable} onChange={event => setEmail(event.target.value)} placeholder="name@example.com" /></Form.Item><Form.Item label="Phone number" required><Input size="large" type="tel" value={phone} disabled={unavailable} onChange={event => setPhone(event.target.value)} placeholder="Mobile number" /></Form.Item><Checkbox className="public-consent" checked={consent} disabled={unavailable} onChange={event => setConsent(event.target.checked)}>I consent to the use of my information for this recruitment process and the configured retention period.</Checkbox><Button block size="large" type="primary" loading={starting} disabled={unavailable} onClick={() => void (job.requiresEmailVerification === false ? begin() : sendVerificationCode())}>{job.availabilityStatus === 'Scheduled' ? 'Applications not open yet' : closed ? 'Applications closed' : job.availabilityStatus === 'Full' ? 'Application limit reached' : job.availabilityStatus === 'ProofConfigurationUnavailable' ? 'Document collection unavailable' : !job.applicationForm ? 'Application form unavailable' : job.requiresEmailVerification === false ? 'Continue to application' : 'Email verification code'}</Button></Form>
            : <div className="public-otp-panel"><label htmlFor="application-verification-code">Verification code</label><Input id="application-verification-code" prefix={<LockOutlined />} size="large" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={verificationCode} onChange={event => setVerificationCode(event.target.value.replace(/\D/g, '').slice(0, 6))} placeholder="Enter 6-digit code" /><Button block size="large" type="primary" loading={starting} onClick={() => void begin()}>Verify & open application</Button><div className="public-otp-actions"><Button type="link" disabled={resendRemaining > 0 || starting} onClick={() => void sendVerificationCode()}>{resendRemaining > 0 ? `Resend in ${resendRemaining}s` : 'Resend code'}</Button><Button type="link" onClick={() => { setVerification(null); setVerificationCode('') }}>Change email or phone</Button></div><small>Code expires {new Date(verification.expiresAtUtc).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}.</small></div>}
        </> : job.applicationForm && <><div className="public-apply-heading public-application-heading"><div><span className="orchestration-kicker">Application</span><h2>Application details</h2></div><Tag color="green" icon={<CheckCircleOutlined />}>{job.requiresEmailVerification === false ? 'Contact captured' : 'Email verified'}</Tag></div><RecruitmentDynamicForm form={job.applicationForm} values={values} files={files} lockedSemanticCodes={['EMAIL', 'PHONE']} onChange={next => { setValues(next); setValidationError('') }} onUpload={upload} onLoadOptions={(field, search) => loadPublicSelectOptions(session.sessionToken, field.id, search)} />{validationError && <Alert data-testid="public-application-validation" type="error" showIcon message="Please review your application" description={validationError} style={{ marginTop: 16 }} />}<Button className="public-submit-button" block size="large" type="primary" loading={submitting} onClick={() => void submit()}>Submit application</Button><p className="public-session-note">Session expires {new Date(session.expiresAtUtc).toLocaleString('en-IN')}. Files are handled only by the secured HRMS document service.</p></>}
      </Card>
    </div>
  </div></main>
}
