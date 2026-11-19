import { useEffect, useRef, useState, type Key } from 'react'
import { Alert, Button, Checkbox, Input, Modal, Space, Table, Tag } from 'antd'
import { essFetch } from '../services/essApi'
import './AttendanceApprovalPanel.css'

type Row = { employeeId: number; employeeCode: string; employeeName: string; workLocation: string; designation: string; piName: string; status: string; detail: string; projectReference: string; taskId?: number; canSubmit: boolean; canApprove: boolean; requiresRollVerification: boolean; presentDays: number | null; payableDays: number | null }
type Context = { enabled: boolean; month: string; message: string; rows: Row[] }
async function read<T>(response: Response): Promise<T> {
  const body = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(body.error || 'Attendance approval is unavailable. Payroll remains subject to its approval policy.')
  return body as T
}
export default function AttendanceApprovalPanel({ clientId, month, dirty = false }: { clientId: number; month: string; dirty?: boolean }) {
  const [context, setContext] = useState<Context | null>(null), [selected, setSelected] = useState<Key[]>([]), [project, setProject] = useState('')
  const [error, setError] = useState(''), [busy, setBusy] = useState(false), [review, setReview] = useState<Row | null>(null), [verified, setVerified] = useState(false), [remarks, setRemarks] = useState('')
  const scope = `${clientId}:${month}`
  const activeScope = useRef(scope); activeScope.current = scope
  const fetchContext = async () => { const r = await essFetch(`/api/ess/mss/attendance/approval?month=${encodeURIComponent(month)}`); return r.status === 404 ? null : read<Context>(r) }
  const load = async () => { const captured = scope; const data = await fetchContext(); if (activeScope.current === captured) { setContext(data); setSelected([]); setError('') } }
  useEffect(() => {
    let active = true
    setContext(null); setSelected([]); setReview(null); setProject(''); setError('')
    if (clientId && /^\d{4}-(0[1-9]|1[0-2])$/.test(month)) void fetchContext().then(data => { if (active) setContext(data) }).catch(e => { if (active) setError(String(e.message || e)) })
    return () => { active = false }
  }, [clientId, month, dirty]) // eslint-disable-line react-hooks/exhaustive-deps
  const submit = async () => {
    setBusy(true); setError('')
    try { await read(await essFetch('/api/ess/mss/attendance/approval/submit', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, month, employeeIds: selected, projectReference: project.trim() }) })); await load() }
    catch (e) { setError(e instanceof Error ? e.message : String(e)) } finally { setBusy(false) }
  }
  const decide = async (action: 'Approved' | 'Rejected' | 'Sent Back') => {
    if (!review?.taskId) return
    setBusy(true); setError('')
    try { await read(await essFetch(`/api/workflows/tasks/${review.taskId}/${encodeURIComponent(action)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ comment: remarks.trim(), projectRollVerified: verified }) })); setReview(null); await load() }
    catch (e) { setError(e instanceof Error ? e.message : String(e)) } finally { setBusy(false) }
  }
  if (!context?.enabled || context.month !== month) return error ? <Alert type="error" message={error} /> : null
  return <section className="attendance-two-stage" aria-label="Two-stage attendance approval">
    <div className="attendance-two-stage-heading"><div><h3>Attendance approval</h3><p>PI → Project Cell → Final for payroll</p></div><Button disabled={busy} onClick={() => void load().catch(e => setError(String(e.message || e)))}>Refresh approvals</Button></div>
    <Alert type="info" showIcon message={context.message} description={dirty ? 'Save attendance edits before submitting or approving. Unsaved edits are not part of an approval.' : `${context.rows.filter(row => row.status === 'Approved').length} of ${context.rows.length} employees finally approved for ${month}.`} />
    {error && <Alert type="error" showIcon message={error} />}
    <div className="attendance-two-stage-submit"><Input aria-label="Attendance project or work-order reference" maxLength={180} placeholder="Actual project / work-order reference" value={project} onChange={e => setProject(e.target.value)} /><Button type="primary" loading={busy} disabled={dirty || !selected.length || !project.trim()} onClick={() => void submit()}>Submit selected for PI approval ({selected.length})</Button></div>
    <Table<Row> size="small" rowKey="employeeId" dataSource={context.rows} scroll={{ x: 800 }} pagination={{ pageSize: 10, showSizeChanger: false }} rowSelection={{ selectedRowKeys: selected, onChange: keys => setSelected(keys.slice(0, 250)), getCheckboxProps: row => ({ disabled: dirty || !row.canSubmit || busy }) }} columns={[
      { title: 'Employee', key: 'employee', width: 230, render: (_, row) => <div><strong>{row.employeeCode} · {row.employeeName}</strong><div>{row.workLocation} · {row.designation}</div></div> },
      { title: 'PI', dataIndex: 'piName', width: 170 },
      { title: 'Present / payable', key: 'days', width: 130, render: (_, row) => `${row.presentDays ?? 'Not supplied'} / ${row.payableDays ?? 'Not supplied'}` },
      { title: 'Approval', key: 'status', width: 200, render: (_, row) => <div><Tag color={row.status === 'Approved' ? 'green' : row.status.startsWith('Pending') ? 'gold' : 'default'}>{row.status}</Tag>{row.detail && <small>{row.detail}</small>}</div> },
      { title: 'Action', key: 'action', width: 150, render: (_, row) => row.canApprove ? <Button disabled={dirty || busy} onClick={() => { setReview(row); setVerified(false); setRemarks(''); setError('') }}>{row.requiresRollVerification ? 'Project Cell review' : 'PI review'}</Button> : '—' },
    ]} />
    <Modal open={!!review} title={review?.requiresRollVerification ? 'Project Cell · final attendance review' : 'PI · attendance review'} onCancel={() => !busy && setReview(null)} footer={null}>
      {review && <><p><strong>{review.employeeCode} · {review.employeeName}</strong><br />{month} · {review.workLocation}<br />Project / work order: {review.projectReference}</p><p>Present days: {review.presentDays ?? 'Not supplied'} · Payable days: {review.payableDays ?? 'Not supplied'}</p>
        {review.requiresRollVerification && <Checkbox checked={verified} onChange={e => setVerified(e.target.checked)}>I verified the employee's project/role and valid employment/contract period for this attendance.</Checkbox>}
        <Input.TextArea aria-label="Attendance approval remarks" rows={3} value={remarks} placeholder="Review remarks" onChange={e => setRemarks(e.target.value)} style={{ marginBlock: 14 }} />{error && <Alert type="error" message={error} />}
        <Space wrap><Button type="primary" loading={busy} disabled={dirty || review.requiresRollVerification && !verified} onClick={() => void decide('Approved')}>{review.requiresRollVerification ? 'Approve final attendance' : 'Approve & send to Project Cell'}</Button><Button disabled={busy} onClick={() => void decide('Sent Back')}>Send back</Button><Button danger disabled={busy} onClick={() => void decide('Rejected')}>Reject</Button></Space></>}
    </Modal>
  </section>
}
