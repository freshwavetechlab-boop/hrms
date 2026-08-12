import { useEffect, useState } from 'react'
import { CheckCircleOutlined, ClockCircleOutlined, EnvironmentOutlined, SafetyCertificateOutlined, TeamOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Form, Input, Result, Skeleton, Space, Tag } from 'antd'
import RecruitmentDynamicForm, { validateDynamicForm } from '../components/RecruitmentDynamicForm'
import { useToast } from '../components/ToastProvider'
import {
  createPublicApplicationSession, getPublicCareerJob, loadPublicSelectOptions, savePublicApplicationValues,
  submitPublicApplication, uploadPublicApplicationFile,
} from '../services/recruitmentOrchestrationService'
import type {
  DynamicFormField, PublicApplicationSession, PublicFormValue, PublicRecruitmentJob, PublicUploadedFile,
} from '../types/recruitmentOrchestration'
import '../components/RecruitmentOrchestration.css'

type Props = { slug?: string }

export default function PublicCareersPage({ slug: suppliedSlug }: Props) {
  const notify = useToast()
  const slug = suppliedSlug || decodeURIComponent(window.location.pathname.split('/').filter(Boolean).at(-1) || '')
  const [job, setJob] = useState<PublicRecruitmentJob | null>(null)
  const [loading, setLoading] = useState(true)
  const [session, setSession] = useState<PublicApplicationSession | null>(null)
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

  const begin = async () => {
    if (!email.trim() && !phone.trim()) { notify('Enter an email address or phone number.', 'warning'); return }
    if (!consent) { notify('Accept the candidate-data consent statement to continue.', 'warning'); return }
    setStarting(true)
    const response = await createPublicApplicationSession(slug, { email: email.trim(), phone: phone.trim(), idempotencyKey, consentAccepted: true })
    setStarting(false)
    if (!response.ok || !response.data) { notify(response.error || 'Unable to start an application.', 'error'); return }
    setSession(response.data)
    window.setTimeout(() => document.getElementById('public-application-form')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50)
  }
  const upload = async (field: DynamicFormField, file: File, onProgress: (percent: number) => void) => {
    if (!session) return { ok: false, error: 'Start the application before uploading files.' }
    const response = await uploadPublicApplicationFile(session.sessionToken, field.id, file, onProgress)
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
          : ''
  return <main className="public-career-page"><div className="public-career-shell">
    <header className="public-career-brand"><div><span className="orchestration-kicker">Careers</span><b>{job.clientName}</b></div></header>
    <Card className="public-job-hero"><span className="orchestration-kicker">Now hiring</span><h1>{job.publicTitle || job.positionTitle}</h1><p>{job.summary || job.rolePurpose}</p><div className="public-job-tags"><Tag icon={<TeamOutlined />}>{job.department}</Tag><Tag icon={<EnvironmentOutlined />}>{job.jobLocation}</Tag>{job.workMode && <Tag>{job.workMode}</Tag>}<Tag>{job.employmentType}</Tag>{job.closesAtUtc && <Tag icon={<ClockCircleOutlined />} color={closed ? 'red' : 'blue'}>{closed ? 'Applications closed' : `Apply by ${new Date(job.closesAtUtc).toLocaleDateString('en-IN')}`}</Tag>}</div></Card>
    <div className="public-job-layout"><Card className="public-job-content"><div className="public-job-copy"><h3>About the role</h3><p>{job.rolePurpose || job.summary}</p>{!!job.responsibilities.length && <><h3>What you will do</h3><ul>{job.responsibilities.map(item => <li key={item.id}>{item.responsibilityText}</li>)}</ul></>}{!!job.qualifications.length && <><h3>What we are looking for</h3><ul>{job.qualifications.map(item => <li key={item.id}>{item.qualificationName}{item.specialization ? ` · ${item.specialization}` : ''}{item.isMandatory ? ' (required)' : ''}</li>)}</ul></>}{!!job.skills.length && <><h3>Skills</h3><Space wrap>{job.skills.map(item => <Tag color={item.isRequired ? 'purple' : 'default'} key={item.id}>{item.skillName}{item.minimumYears ? ` · ${item.minimumYears}+ yrs` : ''}</Tag>)}</Space></>}</div></Card>
      <Card className="public-apply-card" id="public-application-form">
        {availabilityMessage && <Alert showIcon type={job.availabilityStatus === 'Scheduled' ? 'info' : 'warning'} message="Application status" description={availabilityMessage} style={{ marginBottom: 16 }} />}
        {!session ? <><h2>Start your secure application</h2><p>No employee login is required. We use these details only to create a time-limited application session and prevent duplicate submissions.</p><Alert showIcon type="info" icon={<SafetyCertificateOutlined />} message="Documents are private and never exposed through direct storage links." />
          <Form layout="vertical" style={{ marginTop: 16 }}><Form.Item label="Email address"><Input type="email" value={email} disabled={unavailable} onChange={event => setEmail(event.target.value)} placeholder="name@example.com" /></Form.Item><Form.Item label="Phone number"><Input type="tel" value={phone} disabled={unavailable} onChange={event => setPhone(event.target.value)} placeholder="Mobile number" /></Form.Item><Checkbox checked={consent} disabled={unavailable} onChange={event => setConsent(event.target.checked)}>I consent to the use of my information for this recruitment process and the configured retention period.</Checkbox><Button block size="large" type="primary" loading={starting} disabled={unavailable} onClick={() => void begin()} style={{ marginTop: 16 }}>{job.availabilityStatus === 'Scheduled' ? 'Applications not open yet' : closed ? 'Applications closed' : job.availabilityStatus === 'Full' ? 'Application limit reached' : !job.applicationForm ? 'Application form unavailable' : 'Continue to application'}</Button></Form>
        </> : job.applicationForm && <><span className="orchestration-kicker">Secure application</span><h2>Application details</h2><RecruitmentDynamicForm form={job.applicationForm} values={values} files={files} onChange={next => { setValues(next); setValidationError('') }} onUpload={upload} onLoadOptions={(field, search) => loadPublicSelectOptions(session.sessionToken, field.id, search)} />{validationError && <Alert data-testid="public-application-validation" type="error" showIcon message="Please review your application" description={validationError} style={{ marginTop: 16 }} />}<Button block size="large" type="primary" loading={submitting} onClick={() => void submit()} style={{ marginTop: 16 }}>Submit application</Button><p style={{ marginTop: 10, color: '#7b8598', fontSize: 12 }}>Session expires {new Date(session.expiresAtUtc).toLocaleString('en-IN')}. Files are handled only by the secured HRMS document service.</p></>}
      </Card>
    </div>
  </div></main>
}
