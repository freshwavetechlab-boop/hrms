import { useEffect, useMemo, useState } from 'react'
import type { Dispatch, FormEvent, SetStateAction } from 'react'
import { essApi } from '../services/essApi'
import type { LoadState, SaveTravelRequest, TravelCity, TravelDashboard, TravelOptions, TravelRequest, User, WorkflowTrail } from '../types'
import { showToast, statusClass } from '../utils/ui'

const today = new Date().toISOString().slice(0, 10)
const emptyTrip = (mode = ''): TravelCity => ({ fromLocation: '', toLocation: '', travelMode: mode, travelClass: '', remarks: '', startDateTime: `${today}T09:00`, endDateTime: `${today}T18:00` })
const draft0 = (options?: TravelOptions | null): SaveTravelRequest => ({
  id: 0,
  purpose: '',
  customer: options?.clientName ?? '',
  project: '',
  costCenter: '',
  travelScope: 'Domestic',
  travelType: options?.travelTypes?.[0] || 'Official',
  priority: 'Normal',
  fromLocation: '',
  toLocation: '',
  cities: [emptyTrip(options?.travelModes?.[0] || '')],
  startDateTime: `${today}T09:00`,
  endDateTime: `${today}T18:00`,
  estimatedCost: 0,
  travelMode: options?.travelModes?.[0] || '',
  accommodationRequired: false,
  localConveyanceRequired: false,
  advanceRequired: false,
  advanceAmount: 0,
  remarks: '',
})

export function TravelPage({ user }: { user: User }) {
  const [state, setState] = useState<LoadState>('loading')
  const [options, setOptions] = useState<TravelOptions | null>(null)
  const [dashboard, setDashboard] = useState<TravelDashboard | null>(null)
  const [requests, setRequests] = useState<TravelRequest[]>([])
  const [form, setForm] = useState<SaveTravelRequest>(draft0())
  const [editor, setEditor] = useState(false)
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('All')
  const [trail, setTrail] = useState<WorkflowTrail | null>(null)
  const [trailRequest, setTrailRequest] = useState<TravelRequest | null>(null)
  const [cancelRequest, setCancelRequest] = useState<TravelRequest | null>(null)
  const [cancelReason, setCancelReason] = useState('')

  const load = () => Promise.all([essApi.travelOptions(), essApi.travelDashboard(), essApi.travelRequests()])
    .then(([nextOptions, nextDashboard, nextRequests]) => {
      setOptions(nextOptions)
      setDashboard(nextDashboard)
      setRequests(nextRequests)
      setForm(current => current.id || editor ? current : draft0(nextOptions))
      setState('ready')
    })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])

  const set = <K extends keyof SaveTravelRequest>(key: K, value: SaveTravelRequest[K]) => setForm(current => ({ ...current, [key]: value }))
  const openNew = () => { setForm(draft0(options)); setEditor(true) }
  const openEdit = (row: TravelRequest) => {
    const trips = requestLegs(row, options?.travelModes?.[0] || '')
    setForm({
      id: row.id,
      purpose: row.purpose,
      customer: options?.clientName || row.customer,
      project: row.project,
      costCenter: row.costCenter,
      travelScope: row.travelScope,
      travelType: row.travelType,
      priority: row.priority,
      fromLocation: row.fromLocation,
      toLocation: row.toLocation,
      cities: trips,
      startDateTime: inputDateTime(row.startDateTime),
      endDateTime: inputDateTime(row.endDateTime),
      estimatedCost: 0,
      travelMode: trips[0]?.travelMode || row.travelMode,
      accommodationRequired: row.accommodationRequired,
      localConveyanceRequired: row.localConveyanceRequired,
      advanceRequired: row.advanceRequired,
      advanceAmount: row.advanceAmount || 0,
      remarks: row.remarks || '',
    })
    setEditor(true)
  }
  const copyRequest = (row: TravelRequest) => { openEdit({ ...row, id: 0, requestNumber: '', status: 'Draft' }); setForm(current => ({ ...current, id: 0 })) }

  const save = async (event: FormEvent) => {
    event.preventDefault()
    try {
      const payload = normalizeForSave(form, options)
      const saved = await essApi.saveTravelRequest(payload)
      showToast('Travel request draft saved.', 'success')
      setForm(current => ({ ...current, id: saved.id }))
      setEditor(false)
      void load()
    } catch (error) { showToast(error instanceof Error ? error.message : 'Unable to save travel request.', 'error') }
  }
  const submit = async (row: TravelRequest) => {
    try { await essApi.submitTravelRequest(row.id); showToast('Travel request submitted for approval.', 'success'); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to submit travel request.', 'error') }
  }
  const withdraw = async (row: TravelRequest) => {
    try { await essApi.withdrawTravelRequest(row.id); showToast('Travel request withdrawn.', 'success'); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to withdraw travel request.', 'error') }
  }
  const cancel = async () => {
    if (!cancelRequest) return
    try { await essApi.cancelTravelRequest(cancelRequest.id, cancelReason); showToast('Cancellation requested.', 'success'); setCancelRequest(null); setCancelReason(''); void load() }
    catch (error) { showToast(error instanceof Error ? error.message : 'Unable to request cancellation.', 'error') }
  }
  const openTrail = async (row: TravelRequest) => {
    setTrailRequest(row); setTrail(null)
    try { setTrail(await essApi.travelTrail(row.id)) }
    catch { showToast('Unable to load approval trail.', 'error') }
  }

  const statuses = ['All', ...Array.from(new Set(requests.map(item => item.status)))]
  const filtered = requests.filter(item => (status === 'All' || item.status === status) && (!query || `${item.requestNumber} ${item.purpose} ${item.customer} ${item.project} ${item.toLocation} ${item.status}`.toLowerCase().includes(query.toLowerCase())))
  const warnings = options?.validationMessages ?? []

  if (state === 'loading') return <section className="travel-workspace"><div className="empty-work"><span>Loading travel workspace...</span></div></section>
  if (state === 'error') return <section className="travel-workspace"><div className="empty-work"><b>Travel workspace is unavailable.</b><span>Contact HR if this continues.</span></div></section>
  if (editor) return <TravelEditor form={form} options={options} set={set} setForm={setForm} onSave={save} onBack={() => setEditor(false)} />

  return <section className="travel-workspace">
    <div className="travel-head"><div><span className="eyebrow">Travel request</span><h3>My travel requests</h3><p>Draft and submit travel requests based on the applicable policy.</p></div><button type="button" onClick={openNew}>New travel request</button></div>
    {warnings.length > 0 && <div className="travel-warning">{warnings.map(item => <span key={item}>{item}</span>)}</div>}
    <div className="travel-kpis">{dashboard && Object.entries({ Draft: dashboard.draftRequests, Pending: dashboard.pendingApproval, Approved: dashboard.approved, Rejected: dashboard.rejected, Upcoming: dashboard.upcomingTravel, Cancelled: dashboard.cancelledTrips }).map(([label, value]) => <article key={label}><span>{label}</span><strong>{value}</strong></article>)}</div>
    <section className="travel-table-card"><div className="request-list-head"><h3>Requests</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search travel requests" /><select value={status} onChange={event => setStatus(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Request</th><th>Trip</th><th>Dates</th><th>Policy</th><th>Advance</th><th>Status</th><th>Actions</th></tr></thead><tbody>{filtered.map(row => <tr key={row.id}><td><b>{row.requestNumber || 'Draft'}</b><small>{row.travelType} / {row.priority}</small></td><td><b>{row.fromLocation} to {row.toLocation}</b><small>{tripSummary(row)}</small></td><td><b>{dateText(row.startDateTime)} - {dateText(row.endDateTime)}</b><small>{row.purpose}</small></td><td><b>{row.policyName || 'Not resolved'}</b><small>{row.customer || row.project || '-'}</small></td><td><b>{row.advanceRequired ? `Rs ${Number(row.advanceAmount || 0).toLocaleString('en-IN')}` : 'No advance'}</b><small>{row.accommodationRequired ? 'Accommodation' : row.localConveyanceRequired ? 'Local conveyance' : '-'}</small></td><td><span className={`task-status ${statusClass(row.status)}`}>{row.status}</span></td><td><div className="travel-row-actions">{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => openEdit(row)}>Edit</button>}{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => void submit(row)}>Submit</button>}{row.status === 'Pending Approval' && <button type="button" onClick={() => void withdraw(row)}>Withdraw</button>}{row.status === 'Approved' && new Date(row.startDateTime) > new Date() && <button type="button" onClick={() => setCancelRequest(row)}>Cancel</button>}<button type="button" onClick={() => copyRequest(row)}>Copy</button><button type="button" onClick={() => void openTrail(row)}>Trail</button></div></td></tr>)}{!filtered.length && <tr><td colSpan={7}>No travel requests found.</td></tr>}</tbody></table></div></section>
    {trailRequest && <TravelTrailModal request={trailRequest} trail={trail} onClose={() => { setTrailRequest(null); setTrail(null) }} />}
    {cancelRequest && <div className="ess-modal-backdrop" onClick={() => setCancelRequest(null)}><section className="travel-cancel-modal" onClick={event => event.stopPropagation()}><header><h3>Request cancellation</h3><button type="button" onClick={() => setCancelRequest(null)}>x</button></header><p>{cancelRequest.requestNumber} / {cancelRequest.fromLocation} to {cancelRequest.toLocation}</p><textarea value={cancelReason} onChange={event => setCancelReason(event.target.value)} placeholder="Cancellation reason" /><div className="task-actions"><button type="button" className="secondary" onClick={() => setCancelRequest(null)}>Close</button><button type="button" onClick={() => void cancel()}>Submit cancellation</button></div></section></div>}
  </section>
}

function TravelEditor({ form, options, set, setForm, onSave, onBack }: { form: SaveTravelRequest; options: TravelOptions | null; set: <K extends keyof SaveTravelRequest>(key: K, value: SaveTravelRequest[K]) => void; setForm: Dispatch<SetStateAction<SaveTravelRequest>>; onSave: (event: FormEvent) => Promise<void>; onBack: () => void }) {
  const modeOptions = options?.travelModes ?? []
  const locationOptions = options?.locations ?? []
  const typeOptions = options?.travelTypes ?? []
  const classOptions = options?.travelClasses ?? []
  const trips = form.cities.length ? form.cities : [emptyTrip(modeOptions[0] || '')]
  const updateTrip = (index: number, patch: Partial<TravelCity>) => setForm(current => ({ ...current, cities: current.cities.map((item, itemIndex) => itemIndex === index ? { ...item, ...patch } : item) }))
  const addTrip = () => setForm(current => ({ ...current, cities: [...current.cities, emptyTrip(modeOptions[0] || '')] }))
  const removeTrip = (index: number) => setForm(current => ({ ...current, cities: current.cities.length <= 1 ? current.cities : current.cities.filter((_, itemIndex) => itemIndex !== index) }))

  return <section className="travel-editor-page">
    <header className="travel-editor-head"><button type="button" onClick={onBack}>Back</button><div><span className="eyebrow">Travel request</span><h3>{form.id ? 'Edit travel request' : 'New travel request'}</h3><p>{options?.policyName ? `Applicable policy: ${options.policyName}` : 'Policy will be resolved from your employee profile.'}</p></div></header>
    <form className="travel-full-form" onSubmit={onSave}>
      <section className="travel-form-section"><h4>Request details</h4><div className="travel-form-grid"><label className="wide"><span>Purpose of travel</span><input required value={form.purpose} onChange={event => set('purpose', event.target.value)} /></label><label><span>Customer / Client</span><input value={options?.clientName || form.customer} readOnly /></label><label><span>Project</span><input value={form.project} onChange={event => set('project', event.target.value)} /></label><label><span>Cost center</span><input value={form.costCenter} onChange={event => set('costCenter', event.target.value)} /></label><label><span>Scope</span><select value={form.travelScope} onChange={event => set('travelScope', event.target.value as SaveTravelRequest['travelScope'])}><option>Domestic</option><option>International</option></select></label><label><span>Travel type</span><select value={form.travelType} onChange={event => set('travelType', event.target.value)}>{typeOptions.map(item => <option key={item}>{item}</option>)}</select></label><label><span>Priority</span><select value={form.priority} onChange={event => set('priority', event.target.value)}>{(options?.priorities ?? []).map(item => <option key={item}>{item}</option>)}</select></label></div></section>
      <section className="travel-form-section"><div className="travel-section-title"><div><h4>Trip details</h4><p>Add one row for every leg of travel.</p></div><button type="button" onClick={addTrip}>Add trip row</button></div><div className="travel-trip-table-wrap"><table className="travel-trip-table"><thead><tr><th>From</th><th>To</th><th>Start</th><th>End</th><th>Mode</th><th>Class</th><th>Remarks</th><th></th></tr></thead><tbody>{trips.map((trip, index) => <tr key={index}><td><SelectLike value={trip.fromLocation} options={locationOptions} onChange={value => updateTrip(index, { fromLocation: value })} /></td><td><SelectLike value={trip.toLocation} options={locationOptions} onChange={value => updateTrip(index, { toLocation: value })} /></td><td><input type="datetime-local" value={inputDateTime(trip.startDateTime || '')} onChange={event => updateTrip(index, { startDateTime: event.target.value })} /></td><td><input type="datetime-local" value={inputDateTime(trip.endDateTime || '')} onChange={event => updateTrip(index, { endDateTime: event.target.value })} /></td><td><select value={trip.travelMode} onChange={event => updateTrip(index, { travelMode: event.target.value })}>{modeOptions.map(item => <option key={item}>{item}</option>)}</select></td><td><select value={trip.travelClass} onChange={event => updateTrip(index, { travelClass: event.target.value })}><option value="">Select</option>{classOptions.map(item => <option key={item}>{item}</option>)}</select></td><td><input value={trip.remarks} onChange={event => updateTrip(index, { remarks: event.target.value })} /></td><td><button type="button" className="trip-remove" disabled={trips.length <= 1} onClick={() => removeTrip(index)}>Remove</button></td></tr>)}</tbody></table></div>{modeOptions.length === 0 && <p className="inline-error">No travel mode is allowed in the applicable policy. Configure Travel Mode rules in Travel & Expense Policy.</p>}</section>
      <section className="travel-form-section"><h4>Requirements</h4><div className="travel-form-grid"><div className="travel-checks"><label><input type="checkbox" checked={form.accommodationRequired} onChange={event => set('accommodationRequired', event.target.checked)} /> Accommodation</label><label><input type="checkbox" checked={form.localConveyanceRequired} onChange={event => set('localConveyanceRequired', event.target.checked)} /> Local conveyance</label><label><input type="checkbox" checked={form.advanceRequired} onChange={event => set('advanceRequired', event.target.checked)} /> Advance</label></div>{form.advanceRequired && <label><span>Advance amount</span><input type="number" min={0} value={form.advanceAmount} onChange={event => set('advanceAmount', Number(event.target.value || 0))} /></label>}<label className="wide"><span>Remarks</span><textarea value={form.remarks} onChange={event => set('remarks', event.target.value)} /></label></div></section>
      <footer className="travel-editor-actions"><button type="button" className="secondary" onClick={onBack}>Cancel</button><button disabled={modeOptions.length === 0}>Save draft</button></footer>
    </form>
  </section>
}

function SelectLike({ value, options, onChange }: { value: string; options: string[]; onChange: (value: string) => void }) {
  const id = useMemo(() => `travel-list-${Math.random().toString(36).slice(2)}`, [])
  if (options.length) return <select value={value} onChange={event => onChange(event.target.value)}><option value="">Select</option>{options.map(item => <option key={item}>{item}</option>)}</select>
  return <><input list={id} value={value} onChange={event => onChange(event.target.value)} placeholder="Maintain Travel Location master" /><datalist id={id}>{options.map(item => <option key={item} value={item} />)}</datalist></>
}

function TravelTrailModal({ request, trail, onClose }: { request: TravelRequest; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{request.requestNumber || 'Travel request'}</h3></div><small className={`trail-status ${statusClass(request.status)}`}>{request.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This request may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

function normalizeForSave(form: SaveTravelRequest, options: TravelOptions | null): SaveTravelRequest {
  const cities = form.cities.filter(item => item.fromLocation || item.toLocation || item.travelMode || item.startDateTime || item.endDateTime)
  const first = cities[0]
  const last = cities[cities.length - 1]
  return {
    ...form,
    customer: options?.clientName || form.customer,
    cities,
    fromLocation: first?.fromLocation || '',
    toLocation: last?.toLocation || '',
    travelMode: first?.travelMode || '',
    startDateTime: first?.startDateTime || form.startDateTime,
    endDateTime: last?.endDateTime || form.endDateTime,
    estimatedCost: 0,
  }
}

function inputDateTime(value: string) {
  return value ? value.slice(0, 16) : ''
}

function dateText(value: string) {
  return value ? new Date(value).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '-'
}

function requestLegs(row: TravelRequest, defaultMode: string): TravelCity[] {
  if (Array.isArray(row.legs) && row.legs.length) return row.legs.map(item => ({ fromLocation: item.fromLocation || '', toLocation: item.toLocation || '', travelMode: item.travelMode || row.travelMode || defaultMode, travelClass: item.travelClass || '', remarks: item.remarks || '', startDateTime: inputDateTime(item.startDateTime || row.startDateTime), endDateTime: inputDateTime(item.endDateTime || row.endDateTime) }))
  return [{ fromLocation: row.fromLocation, toLocation: row.toLocation, travelMode: row.travelMode || defaultMode, travelClass: '', remarks: '', startDateTime: inputDateTime(row.startDateTime), endDateTime: inputDateTime(row.endDateTime) }]
}

function tripSummary(row: TravelRequest) {
  const trips = requestLegs(row, row.travelMode)
  const modes = Array.from(new Set(trips.map(item => item.travelMode).filter(Boolean)))
  return `${trips.length} leg${trips.length === 1 ? '' : 's'}${modes.length ? ` / ${modes.join(', ')}` : ''} / ${row.travelScope}`
}
