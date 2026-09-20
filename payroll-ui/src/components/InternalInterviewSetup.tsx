import { useEffect, useState } from 'react'
import { Alert, Button, Card, Checkbox, Form, Input, InputNumber, Select, Space, Tag } from 'antd'
import type { InternalInterviewConfiguration, InternalInterviewContext, InterviewQuestion } from '../types/internalInterviews'
import { configureInternalInterview, getInterviewQuestions, saveInterviewQuestion } from '../services/internalInterviewService'

export default function InternalInterviewSetup({ context, onSaved, initial, bankOnly = false }: { context: InternalInterviewContext; onSaved: () => void; initial?: Partial<InternalInterviewConfiguration>; bankOnly?: boolean }) {
  const [config, setConfig] = useState<InternalInterviewConfiguration>(() => ({ mode: 'Human', language: 'en', difficulty: 'Intermediate', maxQuestions: 8, answerSeconds: 120, followUpLimit: 1, recordingEnabled: false, transcriptionEnabled: false, questionIds: [], ...initial }))
  const [questions, setQuestions] = useState<InterviewQuestion[]>([])
  const [draft, setDraft] = useState<InterviewQuestion>({ id: 0, clientId: context.clientId, positionId: context.positionId, skill: '', difficulty: 'Intermediate', language: 'en', question: '', evaluationCriteria: '', followUpInstructions: '', isActive: true })
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const load = async () => { const result = await getInterviewQuestions(context.positionId); if (result.ok) setQuestions(result.data); else setError(result.error) }
  useEffect(() => { void load() }, [context.positionId])
  return <div className="internal-interview-setup">
    {error && <Alert showIcon type="error" message={error} />}
    {!bankOnly && <Card title="Configure internal interview">
      <Alert type="info" showIcon message="Uses the existing interview schedule and panel. Completing this session will not make a hiring decision." />
      <Form layout="vertical" className="internal-interview-form">
        <Form.Item label="Interview mode"><Select value={config.mode} onChange={mode => setConfig({ ...config, mode, transcriptionEnabled: mode === 'Human' ? config.transcriptionEnabled : true })} options={[{ value: 'Human', label: 'Human Interview' }, { value: 'AI', label: 'AI Interview' }, { value: 'Hybrid', label: 'Human + AI Hybrid' }]} /></Form.Item>
        <Form.Item label="Language"><Select value={config.language} onChange={language => setConfig({ ...config, language })} options={[{ value: 'en', label: 'English' }, { value: 'hi', label: 'Hindi' }]} /></Form.Item>
        <Form.Item label="Difficulty"><Select value={config.difficulty} onChange={difficulty => setConfig({ ...config, difficulty })} options={['Beginner', 'Intermediate', 'Advanced'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item label="Maximum questions"><InputNumber min={1} max={40} value={config.maxQuestions} onChange={value => setConfig({ ...config, maxQuestions: value ?? 8 })} /></Form.Item>
        <Form.Item label="Answer duration (seconds)"><InputNumber min={15} max={900} value={config.answerSeconds} onChange={value => setConfig({ ...config, answerSeconds: value ?? 120 })} /></Form.Item>
        <Form.Item label="Follow-up limit"><InputNumber min={0} max={3} value={config.followUpLimit} onChange={value => setConfig({ ...config, followUpLimit: value ?? 0 })} /></Form.Item>
        <Form.Item label="Question set" className="interview-wide"><Select mode="multiple" value={config.questionIds} onChange={questionIds => setConfig({ ...config, questionIds })} options={questions.filter(q => q.isActive).map(q => ({ value: q.id, label: `${q.skill} · ${q.difficulty} · ${q.question}` }))} optionLabelProp="label" showSearch optionFilterProp="label" /></Form.Item>
        <Form.Item className="interview-wide"><Space wrap><Checkbox checked={config.recordingEnabled} onChange={e => setConfig({ ...config, recordingEnabled: e.target.checked })}>Request recording consent</Checkbox><Checkbox checked={config.transcriptionEnabled} onChange={e => setConfig({ ...config, transcriptionEnabled: e.target.checked })}>Request transcription consent</Checkbox></Space></Form.Item>
      </Form>
      <Button type="primary" loading={saving} onClick={async () => { setSaving(true); try { const result = await configureInternalInterview(context.interviewId, config); if (result.ok) onSaved(); else setError(result.error) } finally { setSaving(false) } }}>Save internal interview</Button>
    </Card>}
    <Card title="Job question bank" extra={<Tag>{context.positionTitle}</Tag>}>
      <Form layout="vertical" className="internal-interview-form">
        <Form.Item label="Skill"><Input maxLength={120} value={draft.skill} onChange={e => setDraft({ ...draft, skill: e.target.value })} /></Form.Item>
        <Form.Item label="Question difficulty"><Select value={draft.difficulty} onChange={difficulty => setDraft({ ...draft, difficulty })} options={['Beginner', 'Intermediate', 'Advanced'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item className="interview-wide" label="Question"><Input.TextArea maxLength={2000} value={draft.question} onChange={e => setDraft({ ...draft, question: e.target.value })} /></Form.Item>
        <Form.Item className="interview-wide" label="Expected evaluation criteria"><Input.TextArea maxLength={3000} value={draft.evaluationCriteria} onChange={e => setDraft({ ...draft, evaluationCriteria: e.target.value })} /></Form.Item>
        <Form.Item label="Question language"><Select value={draft.language} onChange={language => setDraft({ ...draft, language })} options={[{ value: 'en', label: 'English' }, { value: 'hi', label: 'Hindi' }]} /></Form.Item>
        <Form.Item className="interview-wide" label="Approved follow-up questions" extra="One complete question per line, maximum three. AI may choose these exact questions; it cannot invent unrelated topics."><Input.TextArea maxLength={1500} value={draft.followUpInstructions} onChange={e => setDraft({ ...draft, followUpInstructions: e.target.value })} /></Form.Item>
      </Form>
      <Button onClick={async () => { const result = await saveInterviewQuestion(draft); if (result.ok) { setDraft({ ...draft, id: 0, question: '', evaluationCriteria: '', followUpInstructions: '' }); await load() } else setError(result.error) }}>{draft.id ? 'Update question' : 'Add question'}</Button>
      <div className="internal-question-list">{questions.map(q => <section key={q.id}><p><b>{q.skill}</b> · {q.question} {!q.isActive && <Tag>Archived</Tag>}</p><Space wrap><Button size="small" onClick={() => setDraft(q)}>Edit</Button><Button size="small" onClick={async () => { const result = await saveInterviewQuestion({ ...q, isActive: !q.isActive }); if (result.ok) await load(); else setError(result.error) }}>{q.isActive ? 'Archive' : 'Restore'}</Button></Space></section>)}</div>
      <small>Existing sessions retain their original question/rubric snapshot when bank questions are edited or archived.</small>
    </Card>
  </div>
}
