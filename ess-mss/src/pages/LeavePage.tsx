import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { LeaveBalance, LeaveRequest, LoadState, User, WorkflowTrail } from '../types'
import { essApi } from '../services/essApi'
import { showToast, statusClass } from '../utils/ui'

type LeaveMode = 'list' | 'create'

export function LeavePage({ user }: { user: User }) {
  const [rows, setRows] = useState<LeaveBalance[]>([])
  const [requests, setRequests] = useState<LeaveRequest[]>([])
  const [state, setState] = useState<LoadState>('loading')
  const [mode, setMode] = useState<LeaveMode>('list')
  const [leaveCode, setLeaveCode] = useState('')
  const [fromDate, setFromDate] = useState('')
  const [toDate, setToDate] = useState('')
  const [dayType, setDayType] = useState('Full Day')
  const [reason, setReason] = useState('')
  const [statusFilter, setStatusFilter] = useState('All')
  const [typeFilter, setTypeFilter] = useState('All')
  const [query, setQuery] = useState('')
  const [trail, setTrail] = useState<WorkflowTrail | null>(null)
  const [trailRequest, setTrailRequest] = useState<LeaveRequest | null>(null)
  const selectedLeave = rows.find(row => row.leaveCode === leaveCode)

  const load = () => Promise.all([essApi.leaveBalances(), essApi.leaveRequests()])
    .then(([balances, items]) => { setRows(balances); setRequests(items); setLeaveCode(current => current || balances[0]?.leaveCode || ''); setState('ready') })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])
  useEffect(() => {
    const openCreate = () => setMode('create')
    const openList = () => setMode('list')
    window.addEventListener('ess:leave:new', openCreate)
    window.addEventListener('ess:leave:list', openList)
    return () => {
      window.removeEventListener('ess:leave:new', openCreate)
      window.removeEventListener('ess:leave:list', openList)
    }
  }, [])
  useEffect(() => {
    if (dayType !== 'Full Day' && fromDate && toDate !== fromDate) setToDate(fromDate)
  }, [dayType, fromDate, toDate])
  useEffect(() => {
    if (dayType !== 'Full Day' && selectedLeave && !selectedLeave.allowHalfDay) setDayType('Full Day')
  }, [dayType, selectedLeave])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Time & leave', title: mode === 'create' ? 'Apply leave' : 'Leave history' } }))
  }, [mode])

  const resetForm = () => {
    setFromDate('')
    setToDate('')
    setDayType('Full Day')
    setReason('')
  }
  const apply = async (event: FormEvent) => {
    event.preventDefault()
    try {
      await essApi.createLeaveRequest({ leaveCode, fromDate, toDate: dayType === 'Full Day' ? toDate : fromDate, dayType, reason })
      showToast('Leave request submitted for approval.', 'success')
      resetForm()
      setMode('list')
      void load()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Unable to submit leave request.', 'error')
    }
  }

  const openTrail = async (request: LeaveRequest) => {
    setTrailRequest(request)
    setTrail(null)
    try { setTrail(await essApi.leaveTrail(request.id)) }
    catch { showToast('Unable to load approval trail.', 'error') }
  }

  const statuses = ['All', ...Array.from(new Set(requests.map(item => item.status)))]
  const types = ['All', ...Array.from(new Set(requests.map(item => item.leaveType)))]
  const filtered = requests.filter(item => (statusFilter === 'All' || item.status === statusFilter) && (typeFilter === 'All' || item.leaveType === typeFilter) && (!query || `${item.leaveType} ${item.leaveCode} ${item.reason} ${item.status}`.toLowerCase().includes(query.toLowerCase())))

  if (state === 'loading') return <section className="leave-workspace"><div className="empty-work"><span>Loading your leave balances...</span></div></section>
  if (state === 'error') return <section className="leave-workspace"><div className="empty-work"><b>Leave data is unavailable.</b><span>Contact HR for assistance.</span></div></section>

  if (mode === 'create') return <section className="leave-workspace">
    <section className="leave-create-layout">
      <form className="leave-form-page" onSubmit={apply}>
        <section className="leave-form-section"><h4>Request details</h4>{rows.length ? <div className="leave-form-grid">
          <label className="wide"><span>Leave type</span><select value={leaveCode} onChange={event => setLeaveCode(event.target.value)}>{rows.map(item => <option value={item.leaveCode} key={item.leaveCode}>{item.leaveType} ({item.leaveCode}) / Balance {item.balance}</option>)}</select><small>{selectedLeave?.allowHalfDay ? 'Half-day leave is allowed for this leave type.' : 'Only full-day leave is allowed for this leave type.'}</small></label>
          <label><span>Duration</span><select value={dayType} onChange={event => setDayType(event.target.value)}><option>Full Day</option>{selectedLeave?.allowHalfDay && <option>First Half</option>}{selectedLeave?.allowHalfDay && <option>Second Half</option>}</select></label>
          <label><span>From date</span><input required type="date" value={fromDate} onChange={event => setFromDate(event.target.value)} /></label>
          {dayType === 'Full Day' && <label><span>To date</span><input required type="date" value={toDate} onChange={event => setToDate(event.target.value)} /></label>}
          <label className="wide"><span>Reason</span><textarea required value={reason} onChange={event => setReason(event.target.value)} placeholder="Brief reason for leave" /></label>
        </div> : <div className="empty-work"><b>No leave type available.</b><span>Please ask HR to activate Leave Types for your client.</span></div>}</section>
        <footer className="leave-form-actions"><button type="button" className="secondary" onClick={() => setMode('list')}>Cancel</button><button disabled={!rows.length}>Submit for approval</button></footer>
      </form>
      <aside className="leave-balance-panel"><h4>Available balances</h4><div>{rows.map(row => <article key={`${row.leaveCode}-${row.balanceDate}`}><span>{row.leaveCode}</span><b>{row.leaveType}</b><strong>{row.balance}</strong><small>{row.allowHalfDay ? 'Half-day allowed' : 'Full-day only'}</small></article>)}</div></aside>
    </section>
  </section>

  return <section className="leave-workspace">
    <div className="leave-head"><button type="button" onClick={() => setMode('create')}>Apply leave</button></div>
    <div className="balance-grid">{rows.map(row => <article key={`${row.leaveCode}-${row.balanceDate}`}><span>{row.leaveCode}</span><strong>{row.balance}</strong><b>{row.leaveType}</b><small>{row.allowHalfDay ? 'Half-day allowed' : 'Full-day only'} / As of {new Date(row.balanceDate).toLocaleDateString('en-IN')}</small></article>)}{!rows.length && <div className="empty-work"><b>No active leave types found.</b><span>Configure active leave types for your client in Settings / Leave Types.</span></div>}</div>
    <section className="request-list"><div className="request-list-head"><h3>Requests</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search requests" /><select value={typeFilter} onChange={event => setTypeFilter(event.target.value)}>{types.map(item => <option key={item}>{item}</option>)}</select><select value={statusFilter} onChange={event => setStatusFilter(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></div></div>{filtered.map(item => <article key={item.id}><div><b>{item.leaveType}</b><span>{item.leaveCode} / {item.dayType || 'Full Day'} / {item.days} day(s)</span></div><div><b>{item.fromDate.slice(0, 10)}{item.fromDate.slice(0, 10) !== item.toDate.slice(0, 10) ? ` to ${item.toDate.slice(0, 10)}` : ''}</b><span>{item.reason || 'No reason added'}</span></div><div><b>{item.createdAt ? new Date(item.createdAt).toLocaleString('en-IN') : '-'}</b><span>Created</span></div><small className={`status-pill ${statusClass(item.status)}`}>{item.status}</small><button className="trail-link" type="button" onClick={() => void openTrail(item)}><span aria-hidden="true">i</span>Details</button></article>)}{!filtered.length && <p>No matching leave requests.</p>}</section>
    {trailRequest && <TrailModal request={trailRequest} trail={trail} onClose={() => { setTrailRequest(null); setTrail(null) }} />}
  </section>
}

function TrailModal({ request, trail, onClose }: { request: LeaveRequest; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{request.leaveType} / {request.fromDate.slice(0, 10)}</h3></div><small className={`trail-status ${statusClass(request.status)}`}>{request.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This request may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}
