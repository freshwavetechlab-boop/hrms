import { useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Input, InputNumber, Modal, Select, Space, Spin, Tag, Typography } from 'antd'
import { CheckSquareOutlined, FileSearchOutlined, ReloadOutlined } from '@ant-design/icons'
import { useAuthSession } from './AuthGate'
import { useToast } from './ToastProvider'
import RecruitmentEligibilityBadge from './RecruitmentEligibilityBadge'
import { getJsonResult, postJson } from '../services/apiClient'
import { openAttachmentWithTicket } from '../services/attachmentService'
import type { EligibilityReviewContext, EligibilityReviewDecision, SaveEligibilityReview } from '../types/recruitmentEligibilityReview'
import './RecruitmentEligibilityReview.css'

export default function RecruitmentEligibilityReviewButton({ applicationId, onChanged, disabled = false }: {
  applicationId: number; onChanged?: () => void | Promise<void>; disabled?: boolean
}) {
  const session = useAuthSession(), notify = useToast()
  const canReview = session?.user.permissions.some(value => ['settings.manage', 'recruitment.manage'].includes(value))
  const [open, setOpen] = useState(false), [loading, setLoading] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [context, setContext] = useState<EligibilityReviewContext | null>(null)
  const [confirmation, setConfirmation] = useState<'reload' | 'close' | null>(null)
  const [drafts, setDrafts] = useState<Record<string, EligibilityReviewDecision>>({})
  const [useExperience, setUseExperience] = useState(false), [experienceMonths, setExperienceMonths] = useState(0), [experienceNote, setExperienceNote] = useState('')
  const updateDraft = (key: string, patch: Partial<EligibilityReviewDecision>) => {
    setError('')
    setDrafts(old => ({ ...old, [key]: { ...(old[key] || { key, decision: 'Pending', note: '' }), ...patch } }))
  }
  const apply = (row: EligibilityReviewContext) => {
    setContext(row)
    setDrafts(Object.fromEntries(row.items.map(item => [item.key, { key: item.key, decision: item.review?.decision || 'Pending', note: item.review?.note || '' }])))
    setUseExperience(row.verifiedExperienceMonths != null)
    setExperienceMonths(row.verifiedExperienceMonths ?? row.parsedExperienceMonths ?? 0)
    setExperienceNote(row.experienceEvidence || '')
  }
  const launch = async () => {
    setOpen(true); setLoading(true); setError(''); setContext(null)
    try {
      const result = await getJsonResult(`/api/recruitment/applications/${applicationId}/eligibility-review`, null as EligibilityReviewContext | null, { loader: false, toast: false })
      if (!result.ok || !result.data) setError(result.error || 'The eligibility checklist could not be loaded. Refresh and retry.')
      else apply(result.data)
    } finally { setLoading(false) }
  }
  const decisions = context?.items.filter(item => item.canReview || item.canWaive).flatMap(item => {
    const draft = drafts[item.key]
    return draft && (draft.decision !== (item.review?.decision || 'Pending') || draft.note !== (item.review?.note || '')) ? [draft] : []
  }) || []
  const experienceChanged = Boolean(context && (useExperience !== (context.verifiedExperienceMonths != null)
    || useExperience && (experienceMonths !== context.verifiedExperienceMonths || experienceNote !== context.experienceEvidence)))
  const dirty = decisions.length > 0 || experienceChanged
  const reload = () => {
    if (dirty) setConfirmation('reload')
    else void launch()
  }
  const close = () => {
    if (busy) return
    if (dirty) setConfirmation('close')
    else setOpen(false)
  }
  const save = async (runAtsOnly: boolean) => {
    if (!context || busy) return
    if (!runAtsOnly && (decisions.some(row => row.decision !== 'Pending' && row.note.trim().length < 8)
      || experienceChanged && useExperience && experienceNote.trim().length < 8)) return setError('Add supporting evidence of at least 8 characters for every reviewed item.')
    setBusy(true); setError('')
    try {
      const request: SaveEligibilityReview = { expectedResumeId: context.resumeId, expectedScoreId: context.scoreId,
        requirementsHash: context.requirementsHash, decisions: runAtsOnly ? [] : decisions, experienceChanged: !runAtsOnly && experienceChanged,
        verifiedExperienceMonths: useExperience ? experienceMonths : null, experienceEvidence: experienceNote, runAtsOnly }
      const response = await postJson(`/api/recruitment/applications/${applicationId}/eligibility-review`, request,
        null as { review: EligibilityReviewContext; scoringError: string } | null, { loader: false, toast: false, timeoutMs: 180000 })
      if (!response.ok || !response.data) {
        setError(response.error || 'The review could not be saved. Check the latest assessment before retrying.')
        if (/assessment.*changed|resume.*changed|jd.*changed/i.test(response.error || '')) setContext(old => old ? { ...old, scoreIsStale: true } : old)
        return
      }
      apply(response.data.review)
      if (response.data.scoringError) { setError(`Evidence saved. ${response.data.scoringError}`); notify('Review evidence saved; ATS still needs attention.', 'warning') }
      else notify(response.data.review.eligibilityStatus === 'NeedsReview' ? 'ATS recalculated. The remaining requirements still need review.' : 'Review saved and ATS recalculated.', 'success')
      await onChanged?.()
    } finally { setBusy(false) }
  }
  if (!canReview) return null
  const reviewItems = context?.items.filter(item => item.status !== 'Met' || item.review) || []
  const automaticItems = context?.items.filter(item => item.status === 'Met' && !item.review) || []
  return <>
    <Button size="small" icon={<CheckSquareOutlined />} disabled={disabled} onClick={() => void launch()}>Review eligibility</Button>
    <Drawer title="Review eligibility" open={open} onClose={close} width="min(900px, 98vw)" maskClosable={!busy} keyboard={!busy}
      className="recruitment-eligibility-review" footer={<div className="eligibility-review-footer"><Typography.Text type="secondary">Review recalculates ATS and keeps the current stage. Apply any score override afterward.</Typography.Text><Space wrap><Button onClick={close} disabled={busy}>Close</Button><Button type="primary" loading={busy} disabled={!context || !dirty || context.scoreIsStale || !context.scoreId} onClick={() => void save(false)}>Save review &amp; Run ATS</Button></Space></div>}>
      {loading ? <Spin tip="Loading eligibility evidence"><div style={{ minHeight: 180 }} /></Spin> : context && <div className="eligibility-review-content">
        <div className="eligibility-review-heading"><div><Typography.Title level={4}>{context.candidateName}</Typography.Title><Typography.Text type="secondary">{context.applicationCode}</Typography.Text></div><RecruitmentEligibilityBadge row={{ ...context, eligibilityReasons: context.reasons }} showScore /></div>
        <Space wrap>{context.attachmentPublicId && <Button icon={<FileSearchOutlined />} onClick={() => void openAttachmentWithTicket(context.attachmentPublicId!, 'Preview')}>Open original resume</Button>}<Button disabled={busy} onClick={reload}>Reload checklist</Button><Button icon={<ReloadOutlined />} disabled={busy || dirty || !context.resumeId} onClick={() => void save(true)}>Recalculate ATS</Button></Space>
        {(context.scoreIsStale || !context.scoreId) && <Alert type="warning" showIcon message={context.scoreIsStale ? 'The resume or job requirements changed.' : 'No current ATS assessment is available.'} description="Recalculate ATS first, then review the current requirements." />}
        <Alert type={context.eligibilityStatus === 'Ineligible' ? 'warning' : 'info'} showIcon message={context.eligibilityStatus === 'NeedsReview' ? 'These requirements need evidence' : 'Current eligibility evidence'} description={<ul>{context.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>} />
        <section className="eligibility-review-experience"><Typography.Title level={5}>Total experience</Typography.Title><div>JD requirement: <strong>{context.requiredExperienceRange || 'No minimum configured'}</strong></div><div>Resume/profile: <strong>{context.parsedExperienceMonths == null ? 'Not established' : `${Math.floor(context.parsedExperienceMonths / 12)} years ${context.parsedExperienceMonths % 12} months`}</strong></div>
          <Checkbox checked={useExperience} disabled={busy || context.scoreIsStale || !context.scoreId} onChange={event => setUseExperience(event.target.checked)}>Record verified total experience</Checkbox>
          {useExperience && <><Space wrap><label className="eligibility-review-time-input">Years<InputNumber aria-label="Verified experience years" disabled={busy} min={0} max={80} precision={0} value={Math.floor(experienceMonths / 12)} onChange={value => setExperienceMonths(Math.min(960, Number(value || 0) * 12 + experienceMonths % 12))} /></label><label className="eligibility-review-time-input">Months<InputNumber aria-label="Verified experience months" disabled={busy} min={0} max={11} precision={0} value={experienceMonths % 12} onChange={value => setExperienceMonths(Math.min(960, Math.floor(experienceMonths / 12) * 12 + Number(value || 0)))} /></label></Space><Input.TextArea aria-label="Experience review evidence" disabled={busy} rows={2} maxLength={400} value={experienceNote} onChange={event => setExperienceNote(event.target.value)} placeholder="Cite the resume dates, total-experience statement or document you verified." /></>}
          <Typography.Text type="secondary">Record only actual experience here. To relax the JD minimum, approve a separate exception below with its business reason.</Typography.Text>
        </section>
        {reviewItems.map(item => <section className="eligibility-review-item" key={item.key}>
          <div className="eligibility-review-item-title"><strong>{item.requirement}</strong><Tag color={item.review?.decision === 'Waived' ? 'gold' : item.status === 'Met' ? 'green' : item.status === 'NotMet' ? 'red' : 'gold'}>{item.review?.decision === 'Waived' ? 'Exception approved' : item.status === 'Met' ? 'Verified' : item.status === 'NotMet' ? 'Not met' : 'Needs review'}</Tag></div>
          {item.evidence && <Typography.Paragraph type="secondary">Recorded evidence: {item.evidence}</Typography.Paragraph>}
          {item.canReview || item.canWaive ? <><Select aria-label={`Review ${item.requirement}`} value={drafts[item.key]?.decision || 'Pending'} disabled={busy || context.scoreIsStale} options={[{ value: 'Pending', label: 'Needs review' }, ...(item.canReview ? [{ value: 'Verified', label: 'Meets requirement — evidence verified' }, { value: 'NotMet', label: 'Does not meet requirement' }] : []), ...(item.canWaive ? [{ value: 'Waived', label: 'Approve eligibility exception — reason required' }] : [])]} onChange={decision => updateDraft(item.key, { decision })} /><Input.TextArea aria-label={`Evidence for ${item.requirement}`} rows={2} maxLength={400} disabled={busy} value={drafts[item.key]?.note || ''} placeholder={drafts[item.key]?.decision === 'Waived' ? 'Document who approved relaxing this requirement and the business reason. Candidate facts remain unchanged.' : 'Explain which CV section, dates, qualification or credential you verified.'} onChange={event => updateDraft(item.key, { note: event.target.value })} />{drafts[item.key]?.decision === 'Waived' && <Alert type="warning" showIcon message="This approves an exception for this application and JD only. It does not add experience, a qualification or a certificate to the candidate." />}</>
            : <Alert type="warning" showIcon message="The recorded duration is below this skill's minimum. Correct the source evidence or JD requirement and recalculate ATS." />}
          {item.review?.reviewerName && <Typography.Text type="secondary">Reviewed by {item.review.reviewerName}{item.review.reviewedAtUtc ? ` · ${new Date(item.review.reviewedAtUtc).toLocaleString('en-IN')}` : ''}</Typography.Text>}
        </section>)}
        {automaticItems.length > 0 && <Collapse><Collapse.Panel key="automatic" header={`${automaticItems.length} requirements matched automatically`}><ul>{automaticItems.map(item => <li key={item.key}>{item.requirement}</li>)}</ul></Collapse.Panel></Collapse>}
      </div>}
      {error && <Alert type="error" showIcon message={error} style={{ marginTop: 12 }} />}
    </Drawer>
    <Modal className="recruitment-eligibility-review-confirm" open={confirmation !== null}
      title={confirmation === 'reload' ? 'Reload checklist and discard unsaved evidence?' : 'Discard unsaved eligibility evidence?'}
      okText={confirmation === 'reload' ? 'Reload checklist' : 'Discard'} onCancel={() => setConfirmation(null)}
      onOk={() => { const action = confirmation; setConfirmation(null); if (action === 'reload') void launch(); else setOpen(false) }}>
      Your unsaved review notes will be discarded.
    </Modal>
  </>
}
