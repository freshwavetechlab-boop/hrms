import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { LeaveBalance, LeaveRequest, LoadState, User, WorkflowTrail } from '../types'
import { essApi } from '../services/essApi'
import { showToast, statusClass } from '../utils/ui'

export function LeavePage({ user }: { user: User }) {
  const [rows, setRows] = useState<LeaveBalance[]>([])
  const [requests, setRequests] = useState<LeaveRequest[]>([])
  const [state, setState] = useState<LoadState>('loading')
  const [leaveCode, setLeaveCode] = useState('')
  const [fromDate, setFromDate] = useState('')
  const [toDate, setToDate] = useState('')
  const [reason, setReason] = useState('')
  const [drawer, setDrawer] = useState(false)
  const [statusFilter, setStatusFilter] = useState('All')
  const [typeFilter, setTypeFilter] = useState('All')
  const [query, setQuery] = useState('')
  const [trail, setTrail] = useState<WorkflowTrail | null>(null)
  const [trailRequest, setTrailRequest] = useState<LeaveRequest | null>(null)

  const load = () => Promise.all([essApi.leaveBalances(), essApi.leaveRequests()])
    .then(([balances, items]) => { setRows(balances); setRequests(items); setLeaveCode(current => current || balances[0]?.leaveCode || ''); setState('ready') })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])

  const apply = async (event: FormEvent) => {
    event.preventDefault()
    try {
      await essApi.createLeaveRequest({ leaveCode, fromDate, toDate, reason })
      showToast('Leave request submitted for approval.', 'success')
      setFromDate(''); setToDate(''); setReason(''); setDrawer(false)
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

  return <section className="leave-workspace">{state === 'loading' && <div className="empty-work"><span>Loading your leave balances...</span></div>}{state === 'error' && <div className="empty-work"><b>Leave data is unavailable.</b><span>Contact HR for assistance.</span></div>}{state === 'ready' && <><div className="leave-toolbar"><button type="button" onClick={() => setDrawer(true)}>Apply leave</button></div><div className="balance-grid">{rows.map(row => <article key={`${row.leaveCode}-${row.balanceDate}`}><span>{row.leaveCode}</span><strong>{row.balance}</strong><b>{row.leaveType}</b><small>As of {new Date(row.balanceDate).toLocaleDateString('en-IN')}</small></article>)}</div><section className="request-list"><div className="request-list-head"><h3>My requests</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search requests" /><select value={typeFilter} onChange={event => setTypeFilter(event.target.value)}>{types.map(item => <option key={item}>{item}</option>)}</select><select value={statusFilter} onChange={event => setStatusFilter(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></div></div>{filtered.map(item => <article key={item.id}><div><b>{item.leaveType}</b><span>{item.leaveCode} / {item.days} day(s)</span></div><div><b>{item.fromDate.slice(0, 10)} to {item.toDate.slice(0, 10)}</b><span>{item.reason || 'No reason added'}</span></div><div><b>{item.createdAt ? new Date(item.createdAt).toLocaleString('en-IN') : '-'}</b><span>Created</span></div><small className={`status-pill ${statusClass(item.status)}`}>{item.status}</small><button className="trail-link" type="button" onClick={() => void openTrail(item)}><span aria-hidden="true">i</span>Details</button></article>)}{!filtered.length && <p>No matching leave requests.</p>}</section>{drawer && <div className="drawer-backdrop" onClick={() => setDrawer(false)}><form className="leave-drawer" onSubmit={apply} onClick={event => event.stopPropagation()}><header><h3>Apply leave</h3><button type="button" onClick={() => setDrawer(false)}>x</button></header><label><span>Leave type</span><select value={leaveCode} onChange={event => setLeaveCode(event.target.value)}>{rows.map(item => <option value={item.leaveCode} key={item.leaveCode}>{item.leaveType} ({item.leaveCode}) / {item.balance}</option>)}</select></label><label><span>From date</span><input required type="date" value={fromDate} onChange={event => setFromDate(event.target.value)} /></label><label><span>To date</span><input required type="date" value={toDate} onChange={event => setToDate(event.target.value)} /></label><label><span>Reason</span><textarea required value={reason} onChange={event => setReason(event.target.value)} placeholder="Brief reason for leave" /></label><button>Submit for approval</button></form></div>}{trailRequest && <TrailModal request={trailRequest} trail={trail} onClose={() => { setTrailRequest(null); setTrail(null) }} />}</>}</section>
}

function TrailModal({ request, trail, onClose }: { request: LeaveRequest; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{request.leaveType} / {request.fromDate.slice(0, 10)}</h3></div><small className={`trail-status ${statusClass(request.status)}`}>{request.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This request may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

