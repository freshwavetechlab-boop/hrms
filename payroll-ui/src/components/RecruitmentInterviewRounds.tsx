import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Input, Modal, Space, Spin, Tag } from 'antd'
import { getInterviewRounds, saveInterviewRounds } from '../services/recruitmentInterviewRoundsService'
import type { InterviewRoundDefinition, InterviewRoundPlan } from '../types/recruitmentInterviewRounds'
import './RecruitmentInterviewRounds.css'

// One small, collapsed view is shared by scheduling, tracker and pipeline cards.
export default function RecruitmentInterviewRounds({ applicationId, editable = true, jobOnly = false, onChanged, onSchedule }: { applicationId: number; editable?: boolean; jobOnly?: boolean; onChanged?: () => void; onSchedule?: () => void }) {
  const [plan, setPlan] = useState<InterviewRoundPlan | null>(null)
  const [error, setError] = useState(''), [loading, setLoading] = useState(false), [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState(false), [draft, setDraft] = useState<InterviewRoundDefinition[]>([])
  const active = useRef(true), pending = useRef(false)
  const requestVersion = useRef(0)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  useEffect(() => { requestVersion.current++; pending.current = false; setPlan(null); setError(''); setEditing(false); setLoading(false); setSaving(false) }, [applicationId])
  const load = async () => {
    if (pending.current) return
    pending.current = true; const version = ++requestVersion.current; setLoading(true); setError('')
    try {
      const result = await getInterviewRounds(applicationId)
      if (!active.current || version !== requestVersion.current) return
      if (result.ok && result.data) setPlan(result.data)
      else setError(result.error || 'Unable to load interview rounds.')
    } catch (failure) { if (active.current && version === requestVersion.current) setError(failure instanceof Error ? failure.message : 'Unable to load interview rounds.') }
    finally { if (version === requestVersion.current) { pending.current = false; if (active.current) setLoading(false) } }
  }
  const save = async () => {
    if (!plan || saving) return
    setSaving(true); setError(''); const version = ++requestVersion.current
    try {
      const result = await saveInterviewRounds(applicationId, plan.revision, draft)
      if (!active.current || version !== requestVersion.current) return
      if (!result.ok || !result.data) { setError(result.error || 'Unable to save interview rounds.'); return }
      setPlan(result.data); setEditing(false); onChanged?.()
    } catch (failure) { if (active.current && version === requestVersion.current) setError(failure instanceof Error ? failure.message : 'Unable to save interview rounds.') }
    finally { if (active.current && version === requestVersion.current) setSaving(false) }
  }
  return <details className="recruitment-interview-rounds" onToggle={event => { if (event.currentTarget.open) void load() }}>
    <summary>Interview rounds{plan ? jobOnly ? ` · ${plan.rounds.length} rounds` : ` · ${plan.completedCount}/${plan.rounds.length} completed` : ''}</summary>
    <div className="recruitment-interview-rounds-body">
      {loading && <Spin size="small" />}
      {error && !editing && <Alert type="error" showIcon message={error} action={<Button size="small" onClick={() => void load()}>Retry</Button>} />}
      {plan?.rounds.map((round, index) => <div key={round.name} className="recruitment-interview-round-row"><span><b>{index + 1}. {round.name}</b><small>{round.interviewType}</small></span>{!jobOnly && <Tag color={round.passed ? 'green' : ['Scheduled', 'Rescheduled'].includes(round.status) ? 'blue' : round.result === 'Rejected' ? 'red' : 'default'}>{round.passed ? 'Cleared' : round.status === 'Completed' ? round.result : round.status}</Tag>}</div>)}
      {plan && <Space wrap>
        {editable && plan.canConfigure && <Button size="small" disabled={loading} onClick={() => { setDraft(plan.rounds.map(({ name, interviewType }) => ({ name, interviewType }))); setError(''); setEditing(true) }}>Configure rounds</Button>}
        {onSchedule && plan.rounds.some(round => round.canSchedule) && <Button size="small" onClick={onSchedule}>Schedule next round</Button>}
      </Space>}
    </div>
    <Modal title="Interview rounds for this job" open={editing} onCancel={() => { if (!saving) { setEditing(false); setError('') } }} onOk={() => void save()} okText="Save rounds" confirmLoading={saving} closable={!saving} maskClosable={!saving} cancelButtonProps={{ disabled: saving }} okButtonProps={{ disabled: !draft.length || draft.some(row => !row.name.trim() || !row.interviewType.trim()) }} destroyOnClose>
      <p>These rounds apply to candidates in this job's {plan?.stageName} stage. Each round uses the existing schedule, panel and feedback form.</p>
      {error && <Alert type="error" showIcon message={error} />}
      <div className="recruitment-interview-round-editor">{draft.map((round, index) => <div key={index} className="recruitment-interview-round-inputs">
        <label>Round {index + 1}<Input aria-label={`Round ${index + 1} name`} maxLength={80} value={round.name} disabled={saving} onChange={event => setDraft(current => current.map((row, i) => i === index ? { ...row, name: event.target.value } : row))} /></label>
        <label>Interview type<Input aria-label={`Round ${index + 1} type`} maxLength={80} value={round.interviewType} disabled={saving} onChange={event => setDraft(current => current.map((row, i) => i === index ? { ...row, interviewType: event.target.value } : row))} /></label>
        <Button size="small" danger disabled={saving || draft.length === 1} onClick={() => setDraft(current => current.filter((_, i) => i !== index))}>Remove</Button>
      </div>)}</div>
      <Button disabled={saving || draft.length >= 100} onClick={() => setDraft(current => [...current, { name: `Round ${current.length + 1}`, interviewType: current.at(-1)?.interviewType || 'Technical' }])}>Add round</Button>
      <p className="recruitment-interview-round-note">Scheduled round names and their order are preserved. All rounds must clear before final selection.</p>
    </Modal>
  </details>
}
