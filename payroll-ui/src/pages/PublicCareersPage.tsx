import { useEffect, useState } from 'react'
import { ArrowRightOutlined, BankOutlined, CheckCircleOutlined, CheckOutlined, ClearOutlined, ClockCircleOutlined, EnvironmentOutlined, FileTextOutlined, LaptopOutlined, LockOutlined, MailOutlined, PhoneOutlined, SafetyCertificateOutlined, TeamOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Form, Input, Popconfirm, Result, Skeleton, Space, Tag } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import { useToast } from '../components/ToastProvider'
import { getPublicOrganizationBrand } from '../services/settingsService'
import {
  createPublicApplicationSession, getPublicCareerJob, loadPublicSelectOptions, requestPublicApplicationVerification, savePublicApplicationValues,
  submitPublicApplication, uploadPublicApplicationFile,
} from '../services/recruitmentOrchestrationService'
import type {
  DynamicFormField, PublicApplicationSession, PublicApplicationVerification, PublicFormValue, PublicRecruitmentJob, PublicUploadedFile, PublicUploadMetadata,
} from '../types/recruitmentOrchestration'
import '../components/RecruitmentOrchestration.css'
import './PublicCareersPage.css'

type Props = { slug?: string }

export default function PublicCareersPage({ slug: suppliedSlug }: Props) {
  const notify = useToast()
  const slug = suppliedSlug || decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '')
  const [job, setJob] = useState<PublicRecruitmentJob | null>(null)
  const [brand, setBrand] = useState<{ name: string; logoDataUrl: string } | null>(null)
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
    let active = true
    void getPublicOrganizationBrand().then(row => { if (active) setBrand(row) })
    return () => { active = false }
  }, [])

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
  const clearStart = () => {
    setEmail('')
    setPhone('')
    setConsent(false)
    setVerification(null)
    setVerificationCode('')
    setResendRemaining(0)
    setValidationError('')
  }
  const clearApplication = () => {
    setValues(session?.initialValues ?? [])
    setFiles([])
    setValidationError('')
  }

  const branding = <CareersBrand name={brand?.name || job?.clientName || 'Careers'} logo={brand?.logoDataUrl} />
  if (loading) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<Card><Skeleton active paragraph={{ rows: 12 }} /></Card></div></main>
  if (!job) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<div className="public-success"><Result status="404" title="Job posting not found" subTitle="This link may be incorrect, closed or no longer public." /></div></div></main>
  if (result) return <main className="public-career-page careers-page"><div className="public-career-shell">{branding}<div className="public-success"><Result status="success" title="Application submitted" subTitle={result.message || `Your application reference is ${result.applicationCode}.`} extra={<><Tag color="green" icon={<CheckCircleOutlined />}>{result.applicationCode}</Tag><p>Keep this reference for future communication. You can safely close this page.</p></>} /></div></div></main>

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
          </> : job.applicationForm && <>
            <div className="public-apply-heading public-application-heading"><div><span className="careers-section-eyebrow">Tell us about yourself</span><h2>Application details</h2></div><Space wrap><Popconfirm title="Clear this form?" description="Entered values and selected uploads will be reset. Verified contact details stay filled." okText="Clear" cancelText="Keep editing" onConfirm={clearApplication}><Button icon={<ClearOutlined />} disabled={submitting}>Clear form</Button></Popconfirm><Tag color="green" icon={<CheckCircleOutlined />}>{needsVerification ? 'Email verified' : 'Contact captured'}</Tag></Space></div>
            <RecruitmentDynamicForm form={job.applicationForm} values={values} files={files} lockedSemanticCodes={['EMAIL', 'PHONE']} onChange={next => { setValues(next); setValidationError('') }} onUpload={upload} onLoadOptions={(field, search) => loadPublicSelectOptions(session.sessionToken, field.id, search)} />
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
    </div>
  </main>
}

function CareersBrand({ name, logo }: { name: string; logo?: string }) {
  const initials = name.trim().split(/\s+/).slice(0, 2).map(word => word[0]).join('').toUpperCase()
  return <header className="public-career-brand">
    <div className="careers-company">{logo ? <img className="careers-organization-logo" src={logo} alt={`${name} logo`} /> : <span className="careers-company-mark" aria-hidden="true">{initials || <BankOutlined />}</span>}<div><b>{name}</b><span>Careers & opportunities</span></div></div>
    <span className="careers-header-note"><LockOutlined /> Candidate application</span>
  </header>
}
