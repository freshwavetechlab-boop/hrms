import { useEffect, useState, type FormEvent } from 'react'
import { essApi } from '../services/essApi'
import type { LoadState, RecruitmentDashboard, RecruitmentEmployeeReferral, RecruitmentInternalOpening, RecruitmentOptions, RecruitmentRequisition, SaveEmployeeReferral, SaveRecruitmentRequisition, User, WorkflowTrail } from '../types'
import { showToast, statusClass } from '../utils/ui'

const today = new Date().toISOString().slice(0, 10)
const draft0 = (options?: RecruitmentOptions | null): SaveRecruitmentRequisition => ({
  id: 0,
  branchId: 0,
  businessUnit: options?.businessUnits?.[0] || '',
  department: options?.departments?.[0] || '',
  costCenter: options?.costCenters?.[0] || '',
  positionTitle: '',
  positionCategory: options?.positionCategories?.[0] || '',
  employmentType: options?.employmentTypes?.[0] || '',
  hiringType: options?.hiringTypes?.[0] || '',
  numberOfOpenings: 1,
  isReplacement: false,
  replacementEmployeeId: undefined,
  targetJoiningDate: today,
  jobLocation: options?.workLocations?.[0] || '',
  workMode: 'Office',
  project: '',
  budgetAvailable: false,
  budgetAmount: numberOption(options?.budgetAmounts?.[0]),
  hiringPriority: options?.priorities?.[1] || 'Normal',
  businessJustification: '',
  reasonForHiring: '',
  experienceRange: options?.experienceRanges?.[0] || '',
  qualification: '',
  requiredSkills: '',
  preferredSkills: '',
  certifications: '',
  languages: '',
  salaryMin: 0,
  salaryMax: 0,
  currency: 'INR',
  benefits: '',
})

export function RecruitmentPage({ user }: { user: User }) {
  const [state, setState] = useState<LoadState>('loading')
  const [options, setOptions] = useState<RecruitmentOptions | null>(null)
  const [dashboard, setDashboard] = useState<RecruitmentDashboard | null>(null)
  const [rows, setRows] = useState<RecruitmentRequisition[]>([])
  const [openings, setOpenings] = useState<RecruitmentInternalOpening[]>([])
  const [referrals, setReferrals] = useState<RecruitmentEmployeeReferral[]>([])
  const [form, setForm] = useState<SaveRecruitmentRequisition>(draft0())
  const [referral, setReferral] = useState<SaveEmployeeReferral>({ positionId: 0, candidateName: '', candidateEmail: '', candidatePhone: '', relationship: '', remarks: '' })
  const [editor, setEditor] = useState(false)
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('All')
  const [trailRow, setTrailRow] = useState<RecruitmentRequisition | null>(null)
  const [trail, setTrail] = useState<WorkflowTrail | null>(null)

  const load = () => Promise.all([essApi.recruitmentOptions(), essApi.recruitmentDashboard(), essApi.recruitmentRequisitions(), essApi.recruitmentInternalOpenings(), essApi.recruitmentReferrals()])
    .then(([nextOptions, nextDashboard, nextRows, nextOpenings, nextReferrals]) => { setOptions(nextOptions); setDashboard(nextDashboard); setRows(nextRows); setOpenings(nextOpenings); setReferrals(nextReferrals); setForm(current => current.id || editor ? current : draft0(nextOptions)); setState('ready') })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])
  useEffect(() => { window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Recruitment', title: editor ? (form.id ? 'Edit requisition' : 'Create requisition') : 'My requisitions' } })) }, [editor, form.id])
  useEffect(() => {
    const open = () => { setForm(draft0(options)); setEditor(true) }
    const list = () => setEditor(false)
    window.addEventListener('ess:recruitment:new', open)
    window.addEventListener('ess:recruitment:list', list)
    return () => { window.removeEventListener('ess:recruitment:new', open); window.removeEventListener('ess:recruitment:list', list) }
  }, [options])

  const set = <K extends keyof SaveRecruitmentRequisition>(key: K, value: SaveRecruitmentRequisition[K]) => setForm(current => ({ ...current, [key]: value }))
  const openNew = () => { setForm(draft0(options)); setEditor(true) }
  const openEdit = (row: RecruitmentRequisition) => { setForm(fromRow(row)); setEditor(true) }
  const copy = (row: RecruitmentRequisition) => { setForm({ ...fromRow(row), id: 0, positionTitle: `${row.positionTitle} Copy` }); setEditor(true) }
  const save = async (event: FormEvent) => {
    event.preventDefault()
    try { const saved = await essApi.saveRecruitmentRequisition(form); showToast('Recruitment requisition draft saved.', 'success'); setForm(current => ({ ...current, id: saved.id })); setEditor(false); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to save requisition.', 'error') }
  }
  const submit = async (row: RecruitmentRequisition) => {
    try { await essApi.submitRecruitmentRequisition(row.id); showToast('Recruitment requisition submitted.', 'success'); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to submit requisition.', 'error') }
  }
  const withdraw = async (row: RecruitmentRequisition) => {
    try { await essApi.withdrawRecruitmentRequisition(row.id); showToast('Recruitment requisition withdrawn.', 'success'); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to withdraw requisition.', 'error') }
  }
  const deleteDraft = async (row: RecruitmentRequisition) => {
    try { await essApi.deleteRecruitmentDraft(row.id); showToast('Draft deleted.', 'success'); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to delete draft.', 'error') }
  }
  const openTrail = async (row: RecruitmentRequisition) => {
    setTrailRow(row); setTrail(null)
    try { setTrail(await essApi.recruitmentTrail(row.id)) } catch { showToast('Unable to load approval trail.', 'error') }
  }
  const submitReferral = async (event: FormEvent) => {
    event.preventDefault()
    try { await essApi.submitRecruitmentReferral(referral); showToast('Referral submitted.', 'success'); setReferral({ positionId: 0, candidateName: '', candidateEmail: '', candidatePhone: '', relationship: '', remarks: '' }); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to submit referral.', 'error') }
  }

  const statuses = ['All', ...Array.from(new Set(rows.map(item => item.status)))]
  const filtered = rows.filter(item => (status === 'All' || item.status === status) && (!query || `${item.rfrNumber} ${item.positionTitle} ${item.department} ${item.hiringType} ${item.status}`.toLowerCase().includes(query.toLowerCase())))
  const warnings = options?.validationMessages ?? []
  const hasRecruitmentAccess = canAccessRecruitment(user)
  if (!hasRecruitmentAccess) return <section className="travel-workspace"><div className="empty-work"><b>You do not have recruitment access.</b><span>Ask the administrator to assign recruitment.rfr.view or recruitment.rfr.create permission.</span></div></section>
  if (state === 'loading') return <section className="travel-workspace"><div className="empty-work"><span>Loading recruitment workspace...</span></div></section>
  if (state === 'error') return <section className="travel-workspace"><div className="empty-work"><b>Recruitment workspace is unavailable.</b><span>Contact HR if this continues.</span></div></section>
  if (!options?.moduleEnabled) return <section className="travel-workspace"><div className="empty-work"><b>Recruitment is not enabled for your client.</b><span>Contact HR if you need access to recruitment requisitions.</span></div></section>
  if (editor) return <RecruitmentEditor form={form} options={options} set={set} onSave={save} onBack={() => setEditor(false)} />

  return <section className="travel-workspace recruitment-workspace">
    <div className="travel-head"><button type="button" disabled={!options?.enabled} onClick={openNew}>New requisition</button></div>
    {warnings.length > 0 && <div className="travel-warning">{warnings.map(item => <span key={item}>{item}</span>)}</div>}
    <div className="travel-kpis">{dashboard && Object.entries({ Drafts: dashboard.drafts, Pending: dashboard.pendingApproval, Approved: dashboard.approved, Rejected: dashboard.rejected, Returned: dashboard.returned, Withdrawn: dashboard.withdrawn }).map(([label, value]) => <article key={label}><span>{label}</span><strong>{value}</strong></article>)}</div>
    <section className="travel-table-card"><div className="request-list-head"><h3>Recruitment requisitions</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search requisitions" /><select value={status} onChange={event => setStatus(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>RFR</th><th>Position</th><th>Scope</th><th>Target</th><th>Status</th><th>Actions</th></tr></thead><tbody>{filtered.map(row => <tr key={row.id}><td><b>{row.rfrNumber || 'Draft'}</b><small>{dateText(row.requestDate)}</small></td><td><b>{row.positionTitle}</b><small>{row.hiringType} / {row.employmentType}</small></td><td><b>{row.department || '-'}</b><small>{row.clientName} / {row.jobLocation || '-'}</small></td><td><b>{row.numberOfOpenings} opening(s)</b><small>{row.targetJoiningDate ? dateText(row.targetJoiningDate) : '-'}</small></td><td><span className={`task-status ${statusClass(row.status)}`}>{row.status}</span></td><td><div className="travel-row-actions">{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => openEdit(row)}>Edit</button>}{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => void submit(row)}>Submit</button>}{row.status === 'Draft' && <button type="button" onClick={() => void deleteDraft(row)}>Delete</button>}{row.status === 'Pending Approval' && <button type="button" onClick={() => void withdraw(row)}>Withdraw</button>}<button type="button" onClick={() => copy(row)}>Copy</button><button type="button" onClick={() => void openTrail(row)}>Trail</button></div></td></tr>)}{!filtered.length && <tr><td colSpan={6}>No recruitment requisitions found.</td></tr>}</tbody></table></div></section>
    {options?.enableInternalHiring && options?.enableReferralHiring && <section className="travel-table-card ess-referral-panel"><div className="request-list-head"><h3>Internal openings</h3><div><span>{openings.length} openings</span></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Position</th><th>Department</th><th>Location</th><th>Reward</th><th>Action</th></tr></thead><tbody>{openings.map(row => <tr key={row.positionId}><td><b>{row.positionTitle}</b><small>{row.positionCode} / closes {dateText(row.endDate)}</small></td><td>{row.department}</td><td>{row.jobLocation}</td><td>{row.referralReward ? row.referralReward.toLocaleString('en-IN') : '-'}</td><td><button type="button" onClick={() => setReferral(current => ({ ...current, positionId: row.positionId }))}>Refer</button></td></tr>)}{!openings.length && <tr><td colSpan={5}>No internal openings available.</td></tr>}</tbody></table></div>{referral.positionId > 0 && <form className="ess-referral-form" onSubmit={submitReferral}><h4>Submit referral</h4><input value={referral.candidateName} required onChange={event => setReferral({ ...referral, candidateName: event.target.value })} placeholder="Candidate name" /><input value={referral.candidateEmail} onChange={event => setReferral({ ...referral, candidateEmail: event.target.value })} placeholder="Candidate email" /><input value={referral.candidatePhone} onChange={event => setReferral({ ...referral, candidatePhone: event.target.value })} placeholder="Candidate phone" /><input value={referral.relationship} onChange={event => setReferral({ ...referral, relationship: event.target.value })} placeholder="Relationship" /><textarea value={referral.remarks} onChange={event => setReferral({ ...referral, remarks: event.target.value })} placeholder="Remarks" /><button>Submit referral</button></form>}</section>}
    {options?.enableReferralHiring && <section className="travel-table-card"><div className="request-list-head"><h3>My referrals</h3></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Candidate</th><th>Position</th><th>Status</th><th>Submitted</th></tr></thead><tbody>{referrals.map(row => <tr key={row.id}><td><b>{row.candidateName}</b><small>{row.candidateEmail || row.candidatePhone}</small></td><td><b>{row.positionTitle}</b><small>{row.positionCode}</small></td><td><span className={`task-status ${statusClass(row.status)}`}>{row.status}</span></td><td>{dateText(row.createdAt)}</td></tr>)}{!referrals.length && <tr><td colSpan={4}>No referrals submitted.</td></tr>}</tbody></table></div></section>}
    {trailRow && <TrailModal request={trailRow} trail={trail} onClose={() => { setTrailRow(null); setTrail(null) }} />}
  </section>
}

function canAccessRecruitment(user: User) {
  return user.permissions.some(permission => ['recruitment.rfr.view', 'recruitment.rfr.create', 'recruitment.manage'].includes(permission.toLowerCase()))
}

function RecruitmentEditor({ form, options, set, onSave, onBack }: { form: SaveRecruitmentRequisition; options: RecruitmentOptions | null; set: <K extends keyof SaveRecruitmentRequisition>(key: K, value: SaveRecruitmentRequisition[K]) => void; onSave: (event: FormEvent) => Promise<void>; onBack: () => void }) {
  const [step, setStep] = useState(0)
  const steps = ['General', 'Position', 'Business', 'Requirements', 'Compensation']
  const isLast = step === steps.length - 1
  return <section className="travel-editor-page recruitment-editor-page">
    <form className="travel-full-form recruitment-step-form" onSubmit={onSave}>
      <nav className="recruitment-stepper" aria-label="Recruitment requisition steps">
        {steps.map((label, index) => <button type="button" key={label} className={index === step ? 'active' : index < step ? 'done' : ''} onClick={() => setStep(index)}>
          <i>{index + 1}</i><span>{label}</span>
        </button>)}
      </nav>
      <section className="travel-form-section recruitment-step-card">
        <header className="recruitment-step-head"><div><h4>{steps[step]}</h4><span>Step {step + 1} of {steps.length}</span></div></header>
        {step === 0 && <div className="travel-form-grid recruitment-form-grid"><label><span>Company / client</span><input value={options?.clientName || ''} readOnly /></label><label><span>Department</span><SelectLike value={form.department} options={options?.departments ?? []} onChange={value => set('department', value)} /></label><label><span>Business unit</span><SelectLike value={form.businessUnit} options={options?.businessUnits ?? []} onChange={value => set('businessUnit', value)} /></label><label><span>Cost center</span><SelectLike value={form.costCenter} options={options?.costCenters ?? []} onChange={value => set('costCenter', value)} /></label></div>}
        {step === 1 && <div className="travel-form-grid recruitment-form-grid"><label className="wide"><span>Position title</span><input required value={form.positionTitle} onChange={event => set('positionTitle', event.target.value)} /></label><label><span>Position category</span><SelectLike value={form.positionCategory} options={options?.positionCategories ?? []} onChange={value => set('positionCategory', value)} /></label><label><span>Employment type</span><SelectLike value={form.employmentType} options={options?.employmentTypes ?? []} onChange={value => set('employmentType', value)} /></label><label><span>Hiring type</span><SelectLike value={form.hiringType} options={options?.hiringTypes ?? []} onChange={value => set('hiringType', value)} /></label><label><span>Openings</span><input type="number" min={1} value={form.numberOfOpenings} onChange={event => set('numberOfOpenings', Number(event.target.value || 1))} /></label><label><span>Target joining date</span><input type="date" value={form.targetJoiningDate || ''} onChange={event => set('targetJoiningDate', event.target.value)} /></label><label><span>Job location</span><SelectLike value={form.jobLocation} options={options?.workLocations ?? []} onChange={value => set('jobLocation', value)} /></label><label><span>Work mode</span><select value={form.workMode} onChange={event => set('workMode', event.target.value)}><option>Office</option><option>Hybrid</option><option>Remote</option></select></label>{options?.allowReplacementHiring && <label className="travel-checks recruitment-check"><input type="checkbox" checked={form.isReplacement} onChange={event => set('isReplacement', event.target.checked)} /> Replacement hiring</label>}{options?.allowReplacementHiring && form.isReplacement && <label><span>Replacement employee</span><select value={form.replacementEmployeeId || ''} onChange={event => set('replacementEmployeeId', event.target.value ? Number(event.target.value) : undefined)}><option value="">Select employee</option>{(options?.employees ?? []).map(item => <option value={item.id} key={item.id}>{item.employeeCode} - {item.employeeName}</option>)}</select></label>}</div>}
        {step === 2 && <div className="travel-form-grid recruitment-form-grid"><label><span>Project</span><input value={form.project} onChange={event => set('project', event.target.value)} /></label><label><span>Priority</span><select value={form.hiringPriority} onChange={event => set('hiringPriority', event.target.value)}>{(options?.priorities ?? ['Normal']).map(item => <option key={item}>{item}</option>)}</select></label><label className="travel-checks recruitment-check"><input type="checkbox" checked={form.budgetAvailable} onChange={event => set('budgetAvailable', event.target.checked)} /> Budget available</label><label><span>Budget amount</span><NumberSelectLike value={form.budgetAmount} options={options?.budgetAmounts ?? []} onChange={value => set('budgetAmount', value)} /></label><label className="wide"><span>Business justification</span><textarea required value={form.businessJustification} onChange={event => set('businessJustification', event.target.value)} /></label><label className="wide"><span>Reason for hiring</span><input value={form.reasonForHiring} onChange={event => set('reasonForHiring', event.target.value)} /></label></div>}
        {step === 3 && <div className="travel-form-grid recruitment-form-grid"><label><span>Experience range</span><SelectLike value={form.experienceRange} options={options?.experienceRanges ?? []} onChange={value => set('experienceRange', value)} /></label><label><span>Qualification</span><input value={form.qualification} onChange={event => set('qualification', event.target.value)} /></label><label className="wide"><span>Required skills</span><textarea value={form.requiredSkills} onChange={event => set('requiredSkills', event.target.value)} /></label><label className="wide"><span>Preferred skills</span><textarea value={form.preferredSkills} onChange={event => set('preferredSkills', event.target.value)} /></label><label><span>Certifications</span><input value={form.certifications} onChange={event => set('certifications', event.target.value)} /></label><label><span>Languages</span><input value={form.languages} onChange={event => set('languages', event.target.value)} /></label></div>}
        {step === 4 && <div className="travel-form-grid recruitment-form-grid"><label><span>Salary min</span><input type="number" min={0} value={form.salaryMin} onChange={event => set('salaryMin', Number(event.target.value || 0))} /></label><label><span>Salary max</span><input type="number" min={0} value={form.salaryMax} onChange={event => set('salaryMax', Number(event.target.value || 0))} /></label><label><span>Currency</span><input value={form.currency} onChange={event => set('currency', event.target.value.toUpperCase())} /></label><label className="wide"><span>Benefits</span><textarea value={form.benefits} onChange={event => set('benefits', event.target.value)} /></label></div>}
      </section>
      <footer className="travel-editor-actions recruitment-step-actions">
        <button type="button" className="secondary" onClick={onBack}>Cancel</button>
        <span />
        <button type="button" className="secondary" disabled={step === 0} onClick={() => setStep(value => Math.max(0, value - 1))}>Previous</button>
        {!isLast && <button type="button" onClick={() => setStep(value => Math.min(steps.length - 1, value + 1))}>Next</button>}
        {isLast && <button disabled={!options?.enabled}>Save draft</button>}
      </footer>
    </form>
  </section>
}

function SelectLike({ value, options, onChange }: { value: string; options: string[]; onChange: (value: string) => void }) {
  if (options.length) return <select value={value} onChange={event => onChange(event.target.value)}><option value="">Select</option>{options.map(item => <option key={item}>{item}</option>)}</select>
  return <input value={value} onChange={event => onChange(event.target.value)} />
}

function NumberSelectLike({ value, options, onChange }: { value: number; options: string[]; onChange: (value: number) => void }) {
  if (options.length) return <select value={String(value || '')} onChange={event => onChange(numberOption(event.target.value))}><option value="">Select</option>{options.map(item => <option value={numberOption(item)} key={item}>{moneyLabel(item)}</option>)}</select>
  return <input type="number" min={0} value={value} onChange={event => onChange(Number(event.target.value || 0))} />
}

function numberOption(value?: string) {
  const amount = Number(String(value ?? '').replace(/[^\d.]/g, ''))
  return Number.isFinite(amount) ? amount : 0
}

function moneyLabel(value: string) {
  const amount = numberOption(value)
  return amount ? amount.toLocaleString('en-IN') : value
}

function TrailModal({ request, trail, onClose }: { request: RecruitmentRequisition; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{request.rfrNumber || 'Recruitment requisition'}</h3></div><small className={`trail-status ${statusClass(request.status)}`}>{request.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This requisition may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

function fromRow(row: RecruitmentRequisition): SaveRecruitmentRequisition {
  return { id: row.id, branchId: row.branchId || 0, businessUnit: row.businessUnit || '', department: row.department || '', costCenter: row.costCenter || '', positionTitle: row.positionTitle || '', positionCategory: row.positionCategory || '', employmentType: row.employmentType || '', hiringType: row.hiringType || '', numberOfOpenings: row.numberOfOpenings || 1, isReplacement: row.isReplacement, replacementEmployeeId: row.replacementEmployeeId, targetJoiningDate: row.targetJoiningDate?.slice(0, 10), jobLocation: row.jobLocation || '', workMode: row.workMode || 'Office', project: row.project || '', budgetAvailable: row.budgetAvailable, budgetAmount: row.budgetAmount || 0, hiringPriority: row.hiringPriority || 'Normal', businessJustification: row.businessJustification || '', reasonForHiring: row.reasonForHiring || '', experienceRange: row.experienceRange || '', qualification: row.qualification || '', requiredSkills: row.requiredSkills || '', preferredSkills: row.preferredSkills || '', certifications: row.certifications || '', languages: row.languages || '', salaryMin: row.salaryMin || 0, salaryMax: row.salaryMax || 0, currency: row.currency || 'INR', benefits: row.benefits || '' }
}

function dateText(value: string) {
  return value ? new Date(value).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) : '-'
}

