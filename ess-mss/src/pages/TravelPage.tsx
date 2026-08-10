import { useEffect, useMemo, useState } from 'react'
import type { Dispatch, FormEvent, SetStateAction } from 'react'
import { Button, Checkbox, Radio, Select } from 'antd'
import { BankOutlined, CarOutlined, EnvironmentOutlined, FormOutlined } from '@ant-design/icons'
import { AdminRecordMaintenanceModal } from '../components/AdminRecordMaintenanceModal'
import type { AdminMaintenanceAction } from '../components/AdminRecordMaintenanceModal'
import { essApi } from '../services/essApi'
import type { LoadState, LocalTravelDetail, SaveTravelRequest, TravelAccommodation, TravelBookingAction, TravelCity, TravelDashboard, TravelOptions, TravelRequest, User, View, WorkflowTrail } from '../types'
import { showToast, statusClass } from '../utils/ui'
import { canMaintainTravelExpense } from '../utils/access'

const today = new Date().toISOString().slice(0, 10)
const tomorrow = new Date(Date.now() + 86400000).toISOString().slice(0, 10)
const emptyTrip = (mode = ''): TravelCity => ({ fromLocation: '', toLocation: '', travelMode: mode, travelClass: '', bookingAction: 'Book by myself', remarks: '', startDateTime: `${today}T09:00`, endDateTime: `${today}T18:00` })
const emptyAccommodation = (): TravelAccommodation => ({ city: '', checkInDateTime: `${today}T14:00`, checkOutDateTime: `${tomorrow}T10:00`, occupancy: 'Single', roomPreference: '', bookingAction: 'Book by myself', remarks: '' })
const emptyLocalTravel = (mode = ''): LocalTravelDetail => ({ city: '', travelDateTime: `${today}T09:00`, fromLocation: '', toLocation: '', travelMode: mode, bookingAction: 'Book by myself', remarks: '' })
const draft0 = (options?: TravelOptions | null): SaveTravelRequest => ({
  id: 0,
  purpose: '',
  customer: '',
  project: '',
  costCenter: '',
  travelScope: 'Domestic',
  travelType: options?.travelTypes?.[0] || '',
  priority: 'Normal',
  fromLocation: '',
  toLocation: '',
  cities: options?.showTripDetails ? [emptyTrip(options?.travelModes?.[0] || '')] : [],
  accommodationDetails: [],
  localTravelDetails: [],
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

export function TravelPage({ user, setView }: { user: User; setView: (view: View) => void }) {
  const adminMaintenance = canMaintainTravelExpense(user)
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
  const [maintenanceRequest, setMaintenanceRequest] = useState<TravelRequest | null>(null)

  const load = () => adminMaintenance
    ? essApi.adminTravelRequests().then(nextRequests => {
      setOptions(null)
      setDashboard(null)
      setRequests(nextRequests)
      setEditor(false)
      setState('ready')
    }).catch(() => setState('error'))
    : Promise.all([essApi.travelOptions(), essApi.travelDashboard(), essApi.travelRequests()])
    .then(([nextOptions, nextDashboard, nextRequests]) => {
      setOptions(nextOptions)
      setDashboard(nextDashboard)
      setRequests(nextRequests)
      setForm(current => current.id || editor ? current : draft0(nextOptions))
      setState('ready')
    })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Travel & expense', title: adminMaintenance ? 'Travel request administration' : editor ? (form.id ? 'Edit travel request' : 'Create travel request') : 'Travel requests' } }))
  }, [adminMaintenance, editor, form.id])

  const set = <K extends keyof SaveTravelRequest>(key: K, value: SaveTravelRequest[K]) => setForm(current => ({ ...current, [key]: value }))
  const openNew = () => { setForm(draft0(options)); setEditor(true) }
  useEffect(() => {
    const open = () => { if (!adminMaintenance) openNew() }
    const list = () => setEditor(false)
    window.addEventListener('ess:travel:new', open)
    window.addEventListener('ess:travel:list', list)
    return () => {
      window.removeEventListener('ess:travel:new', open)
      window.removeEventListener('ess:travel:list', list)
    }
  }, [adminMaintenance, options])
  const openEdit = (row: TravelRequest) => {
    const trips = requestLegs(row, options?.travelModes?.[0] || '')
    setForm({
      id: row.id,
      purpose: row.purpose,
      customer: '',
      project: '',
      costCenter: row.costCenter,
      travelScope: 'Domestic',
      travelType: row.travelType,
      priority: 'Normal',
      fromLocation: row.fromLocation,
      toLocation: row.toLocation,
      cities: trips,
      accommodationDetails: requestAccommodation(row),
      localTravelDetails: requestLocalTravel(row, options?.localTravelModes?.[0] || ''),
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
  const createClaim = (row: TravelRequest) => {
    setView('Expense')
    window.setTimeout(() => window.dispatchEvent(new CustomEvent('ess:expense:from-travel', { detail: { travelRequestId: row.id } })), 0)
  }

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
  const maintain = async (action: AdminMaintenanceAction, reason: string) => {
    if (!maintenanceRequest) return
    try {
      const result = action === 'revert'
        ? await essApi.adminRevertTravelRequest(maintenanceRequest.id, reason)
        : await essApi.adminDeleteTravelRequest(maintenanceRequest.id, reason)
      showToast(result.message, 'success')
      setMaintenanceRequest(null)
      await load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to clean this travel request.', 'error')
      throw error
    }
  }

  const statuses = ['All', ...Array.from(new Set(requests.map(item => item.status)))]
  const filtered = requests.filter(item => (status === 'All' || item.status === status) && (!query || `${item.requestNumber} ${item.purpose} ${item.travelType} ${item.toLocation} ${item.status}`.toLowerCase().includes(query.toLowerCase())))
  const warnings = options?.validationMessages ?? []

  if (state === 'loading') return <section className="travel-workspace"><div className="empty-work"><span>Loading travel workspace...</span></div></section>
  if (state === 'error') return <section className="travel-workspace"><div className="empty-work"><b>Travel workspace is unavailable.</b><span>Contact HR if this continues.</span></div></section>
  if (editor && !adminMaintenance) return <TravelEditor form={form} options={options} set={set} setForm={setForm} onSave={save} onBack={() => setEditor(false)} />

  return <section className="travel-workspace">
    <div className="travel-head">{adminMaintenance ? <div className="admin-maintenance-heading"><b>Travel request cleanup</b><span>Revert approved test records to Draft or permanently remove records that have no paid or settled advance.</span></div> : <button type="button" onClick={openNew}>New travel request</button>}</div>
    {adminMaintenance && <div className="admin-maintenance-banner"><b>Admin safety is active</b><span>Requests linked to expense claims or financial travel advances stay protected. Clean those dependencies first.</span></div>}
    {warnings.length > 0 && <div className="travel-warning">{warnings.map(item => <span key={item}>{item}</span>)}</div>}
    <div className="travel-kpis">{dashboard && Object.entries({ Draft: dashboard.draftRequests, Pending: dashboard.pendingApproval, Approved: dashboard.approved, Rejected: dashboard.rejected, Upcoming: dashboard.upcomingTravel, Cancelled: dashboard.cancelledTrips }).map(([label, value]) => <article key={label}><span>{label}</span><strong>{value}</strong></article>)}</div>
    <section className="travel-table-card"><div className="request-list-head"><h3>{adminMaintenance ? 'All accessible requests' : 'Requests'}</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search travel requests" /><Select aria-label="Travel request status" value={status} onChange={setStatus} options={statuses.map(item => ({ label: item, value: item }))} /></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Request</th><th>Trip</th><th>Dates</th><th>Policy</th><th>Advance</th><th>Status</th><th>Actions</th></tr></thead><tbody>{filtered.map(row => <tr key={row.id}><td><b>{row.requestNumber || 'Draft'}</b><small>{adminMaintenance ? row.employeeName || 'Employee unavailable' : row.travelType}</small>{adminMaintenance && <small>{row.travelType}</small>}</td><td><b>{row.fromLocation && row.toLocation ? `${row.fromLocation} to ${row.toLocation}` : 'Travel Desk not requested'}</b><small>{tripSummary(row)}</small></td><td><b>{dateText(row.startDateTime)} - {dateText(row.endDateTime)}</b><small>{row.purpose}</small></td><td><b>{row.policyName || 'Not resolved'}</b><small>Applied for signed-in employee</small></td><td><b>{row.advanceRequired ? `Rs ${Number(row.advanceAmount || 0).toLocaleString('en-IN')}` : 'No advance'}</b><small>{row.accommodationRequired ? 'Accommodation' : row.localConveyanceRequired ? 'Local conveyance' : '-'}</small></td><td><span className={`task-status ${statusClass(row.status)}`}>{row.status}</span></td><td><div className="travel-row-actions">{adminMaintenance ? <button type="button" className="admin-cleanup" onClick={() => setMaintenanceRequest(row)}>{row.status === 'Draft' ? 'Delete' : 'Revert / Delete'}</button> : <>{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => openEdit(row)}>Edit</button>}{['Draft', 'Sent Back'].includes(row.status) && <button type="button" onClick={() => void submit(row)}>Submit</button>}{row.status === 'Pending Approval' && <button type="button" onClick={() => void withdraw(row)}>Withdraw</button>}{row.status === 'Approved' && <button type="button" onClick={() => createClaim(row)}>Create claim</button>}{row.status === 'Approved' && new Date(row.startDateTime) > new Date() && <button type="button" onClick={() => setCancelRequest(row)}>Cancel</button>}<button type="button" onClick={() => copyRequest(row)}>Copy</button><button type="button" onClick={() => void openTrail(row)}>Trail</button><button type="button" onClick={() => printTravelRequest(row)}>Print</button></>}</div></td></tr>)}{!filtered.length && <tr><td colSpan={7}>No travel requests found.</td></tr>}</tbody></table></div></section>
    {trailRequest && <TravelTrailModal request={trailRequest} trail={trail} onClose={() => { setTrailRequest(null); setTrail(null) }} />}
    {cancelRequest && <div className="ess-modal-backdrop" onClick={() => setCancelRequest(null)}><section className="travel-cancel-modal" onClick={event => event.stopPropagation()}><header><h3>Request cancellation</h3><button type="button" onClick={() => setCancelRequest(null)}>x</button></header><p>{cancelRequest.requestNumber} / {cancelRequest.fromLocation} to {cancelRequest.toLocation}</p><textarea value={cancelReason} onChange={event => setCancelReason(event.target.value)} placeholder="Cancellation reason" /><div className="task-actions"><button type="button" className="secondary" onClick={() => setCancelRequest(null)}>Close</button><button type="button" onClick={() => void cancel()}>Submit cancellation</button></div></section></div>}
    {maintenanceRequest && <AdminRecordMaintenanceModal open recordType="Travel request" recordLabel={maintenanceRequest.requestNumber || `Request #${maintenanceRequest.id}`} status={maintenanceRequest.status} onClose={() => setMaintenanceRequest(null)} onConfirm={maintain} />}
  </section>
}

function TravelEditor({ form, options, set, setForm, onSave, onBack }: { form: SaveTravelRequest; options: TravelOptions | null; set: <K extends keyof SaveTravelRequest>(key: K, value: SaveTravelRequest[K]) => void; setForm: Dispatch<SetStateAction<SaveTravelRequest>>; onSave: (event: FormEvent) => Promise<void>; onBack: () => void }) {
  const modeOptions = options?.travelModes ?? []
  const localModeOptions = options?.localTravelModes ?? []
  const locationOptions = options?.locations ?? []
  const typeOptions = options?.travelTypes ?? []
  const classOptions = options?.travelClasses ?? []
  const visibleSteps = useMemo(() => [
    'Travel Request',
    options?.showTripDetails ? 'Trip Details' : '',
    options?.showAccommodationDetails ? 'Accommodation Details' : '',
    options?.showLocalTravelDetails ? 'Local Travel Details' : '',
  ].filter(Boolean), [options?.showAccommodationDetails, options?.showLocalTravelDetails, options?.showTripDetails])
  const [activeStep, setActiveStep] = useState('Travel Request')
  useEffect(() => {
    if (!visibleSteps.includes(activeStep)) setActiveStep('Travel Request')
  }, [activeStep, visibleSteps])
  const activeStepIndex = Math.max(0, visibleSteps.indexOf(activeStep))
  const trips = form.cities.length ? form.cities : [emptyTrip(modeOptions[0] || '')]
  const stays = form.accommodationDetails.length ? form.accommodationDetails : []
  const rides = form.localTravelDetails.length ? form.localTravelDetails : []
  const updateTrip = (index: number, patch: Partial<TravelCity>) => setForm(current => ({ ...current, cities: current.cities.map((item, itemIndex) => itemIndex === index ? { ...item, ...patch } : item) }))
  const addTrip = () => setForm(current => ({ ...current, cities: [...current.cities, emptyTrip(modeOptions[0] || '')] }))
  const removeTrip = (index: number) => setForm(current => ({ ...current, cities: current.cities.length <= 1 ? current.cities : current.cities.filter((_, itemIndex) => itemIndex !== index) }))
  const updateStay = (index: number, patch: Partial<TravelAccommodation>) => setForm(current => ({ ...current, accommodationDetails: current.accommodationDetails.map((item, itemIndex) => itemIndex === index ? { ...item, ...patch } : item) }))
  const addStay = () => setForm(current => ({ ...current, accommodationRequired: true, accommodationDetails: [...current.accommodationDetails, emptyAccommodation()] }))
  const removeStay = (index: number) => setForm(current => {
    const rows = current.accommodationDetails.filter((_, itemIndex) => itemIndex !== index)
    return { ...current, accommodationDetails: rows, accommodationRequired: rows.length > 0 ? current.accommodationRequired : false }
  })
  const updateRide = (index: number, patch: Partial<LocalTravelDetail>) => setForm(current => ({ ...current, localTravelDetails: current.localTravelDetails.map((item, itemIndex) => itemIndex === index ? { ...item, ...patch } : item) }))
  const addRide = () => setForm(current => ({ ...current, localConveyanceRequired: true, localTravelDetails: [...current.localTravelDetails, emptyLocalTravel(localModeOptions[0] || '')] }))
  const removeRide = (index: number) => setForm(current => {
    const rows = current.localTravelDetails.filter((_, itemIndex) => itemIndex !== index)
    return { ...current, localTravelDetails: rows, localConveyanceRequired: rows.length > 0 ? current.localConveyanceRequired : false }
  })

  return <section className="travel-editor-page">
    <form className="travel-full-form" onSubmit={onSave}>
      <nav className="travel-journey" aria-label="Travel request steps">
        {visibleSteps.map((step, index) => <button type="button" key={step} className={`travel-journey-step ${activeStep === step ? 'active' : ''} ${index < activeStepIndex ? 'complete' : ''}`} onClick={() => setActiveStep(step)}><i>{step === 'Travel Request' ? <FormOutlined /> : step === 'Trip Details' ? <EnvironmentOutlined /> : step === 'Accommodation Details' ? <BankOutlined /> : <CarOutlined />}</i><span>{step}</span><small>{step === 'Travel Request' ? 'Request' : step.replace(' Details', '')}</small></button>)}
      </nav>

      {activeStep === 'Travel Request' && <>
        <section className="travel-form-section travel-request-primary"><div className="travel-section-title"><div><h4>Travel request</h4><p>Your client and policy are resolved automatically from your employee login.</p></div></div><div className="travel-form-grid"><label><span>Travel type</span><Select aria-label="Travel type" showSearch optionFilterProp="label" placeholder="Select travel type" value={form.travelType || undefined} onChange={value => set('travelType', value)} options={typeOptions.map(item => ({ label: item, value: item }))} /></label><label className="wide"><span>Purpose of travel</span><input required value={form.purpose} onChange={event => set('purpose', event.target.value)} placeholder="Briefly describe the official purpose" /></label><label><span>Cost center</span><input value={form.costCenter} onChange={event => set('costCenter', event.target.value)} /></label></div></section>
        <section className="travel-form-section"><h4>Advance and remarks</h4><div className="travel-form-grid"><div className="travel-checks"><Checkbox checked={form.advanceRequired} onChange={event => set('advanceRequired', event.target.checked)}>Advance required</Checkbox></div>{form.advanceRequired && <label><span>Advance amount</span><input type="number" min={0} value={form.advanceAmount} onChange={event => set('advanceAmount', Number(event.target.value || 0))} /></label>}<label className="wide"><span>Remarks</span><textarea value={form.remarks} onChange={event => set('remarks', event.target.value)} /></label></div></section>
      </>}

      {activeStep === 'Trip Details' && <section className="travel-form-section"><div className="travel-section-title compact"><div><h4>Trip details</h4><p>{options?.travelDeskEnabled ? 'Add one row for every leg and tell the Travel Desk who will book it.' : 'Add one row for every travel leg.'}</p></div><Button type="primary" htmlType="button" onClick={addTrip}>Add trip row</Button></div><div className="travel-trip-table-wrap"><table className="travel-trip-table"><thead><tr><th>From</th><th>To</th><th>Start</th><th>End</th><th>Mode</th><th>Class</th>{options?.travelDeskEnabled && <th>Travel Desk action</th>}<th>Remarks</th><th></th></tr></thead><tbody>{trips.map((trip, index) => <tr key={index}><td><SelectLike value={trip.fromLocation} options={locationOptions} onChange={value => updateTrip(index, { fromLocation: value })} /></td><td><SelectLike value={trip.toLocation} options={locationOptions} onChange={value => updateTrip(index, { toLocation: value })} /></td><td><input type="datetime-local" value={inputDateTime(trip.startDateTime || '')} onChange={event => updateTrip(index, { startDateTime: event.target.value })} /></td><td><input type="datetime-local" value={inputDateTime(trip.endDateTime || '')} onChange={event => updateTrip(index, { endDateTime: event.target.value })} /></td><td><SelectLike value={trip.travelMode} options={modeOptions} onChange={value => updateTrip(index, { travelMode: value })} /></td><td><SelectLike value={trip.travelClass} options={classOptions} onChange={value => updateTrip(index, { travelClass: value })} /></td>{options?.travelDeskEnabled && <td><BookingChoice value={trip.bookingAction} onChange={value => updateTrip(index, { bookingAction: value })} /></td>}<td><input value={trip.remarks} onChange={event => updateTrip(index, { remarks: event.target.value })} /></td><td><Button danger htmlType="button" disabled={trips.length <= 1} onClick={() => removeTrip(index)}>Remove</Button></td></tr>)}</tbody></table></div>{modeOptions.length === 0 && <p className="inline-error">No travel mode is allowed in the applicable policy. Configure Travel Mode rules in Travel & Expense Policy.</p>}</section>}

      {activeStep === 'Accommodation Details' && <section className="travel-form-section"><div className="travel-section-title compact"><div><h4>Accommodation details</h4><p>{options?.travelDeskEnabled ? 'Add each stay and choose whether the Travel Desk should book it.' : 'Add each required stay.'}</p></div><Button type="primary" htmlType="button" onClick={addStay}>Add accommodation row</Button></div><div className="travel-arrangement-note"><Checkbox checked={form.accommodationRequired} onChange={event => set('accommodationRequired', event.target.checked)}>Accommodation is required</Checkbox></div><div className="travel-trip-table-wrap"><table className="travel-trip-table accommodation-table"><thead><tr><th>City</th><th>Check-in</th><th>Check-out</th><th>Occupancy</th><th>Room preference</th>{options?.travelDeskEnabled && <th>Travel Desk action</th>}<th>Remarks</th><th></th></tr></thead><tbody>{stays.map((stay, index) => <tr key={index}><td><SelectLike value={stay.city} options={locationOptions} onChange={value => updateStay(index, { city: value })} /></td><td><input type="datetime-local" value={inputDateTime(stay.checkInDateTime || '')} onChange={event => updateStay(index, { checkInDateTime: event.target.value })} /></td><td><input type="datetime-local" value={inputDateTime(stay.checkOutDateTime || '')} onChange={event => updateStay(index, { checkOutDateTime: event.target.value })} /></td><td><SelectLike value={stay.occupancy} options={['Single', 'Double Sharing', 'Twin Sharing', 'Family']} onChange={value => updateStay(index, { occupancy: value })} /></td><td><input value={stay.roomPreference} onChange={event => updateStay(index, { roomPreference: event.target.value })} placeholder="Near office, non-smoking, etc." /></td>{options?.travelDeskEnabled && <td><BookingChoice value={stay.bookingAction} onChange={value => updateStay(index, { bookingAction: value })} /></td>}<td><input value={stay.remarks} onChange={event => updateStay(index, { remarks: event.target.value })} /></td><td><Button danger htmlType="button" onClick={() => removeStay(index)}>Remove</Button></td></tr>)}{stays.length === 0 && <tr><td colSpan={options?.travelDeskEnabled ? 8 : 7}>No accommodation rows. Add a row when a stay is required.</td></tr>}</tbody></table></div></section>}

      {activeStep === 'Local Travel Details' && <section className="travel-form-section"><div className="travel-section-title compact"><div><h4>Local travel details</h4><p>{options?.travelDeskEnabled ? 'Add pickup, drop, or city travel and choose who will book it.' : 'Add pickup, drop, or city travel details.'}</p></div><Button type="primary" htmlType="button" onClick={addRide}>Add local travel row</Button></div><div className="travel-arrangement-note"><Checkbox checked={form.localConveyanceRequired} onChange={event => set('localConveyanceRequired', event.target.checked)}>Local travel is required</Checkbox></div><div className="travel-trip-table-wrap"><table className="travel-trip-table local-travel-table"><thead><tr><th>City</th><th>Date/time</th><th>From</th><th>To</th><th>Mode</th>{options?.travelDeskEnabled && <th>Travel Desk action</th>}<th>Remarks</th><th></th></tr></thead><tbody>{rides.map((ride, index) => <tr key={index}><td><SelectLike value={ride.city} options={locationOptions} onChange={value => updateRide(index, { city: value })} /></td><td><input type="datetime-local" value={inputDateTime(ride.travelDateTime || '')} onChange={event => updateRide(index, { travelDateTime: event.target.value })} /></td><td><SelectLike value={ride.fromLocation} options={locationOptions} onChange={value => updateRide(index, { fromLocation: value })} /></td><td><SelectLike value={ride.toLocation} options={locationOptions} onChange={value => updateRide(index, { toLocation: value })} /></td><td><SelectLike value={ride.travelMode} options={localModeOptions} onChange={value => updateRide(index, { travelMode: value })} /></td>{options?.travelDeskEnabled && <td><BookingChoice value={ride.bookingAction} onChange={value => updateRide(index, { bookingAction: value })} /></td>}<td><input value={ride.remarks} onChange={event => updateRide(index, { remarks: event.target.value })} /></td><td><Button danger htmlType="button" onClick={() => removeRide(index)}>Remove</Button></td></tr>)}{rides.length === 0 && <tr><td colSpan={options?.travelDeskEnabled ? 8 : 7}>No local travel rows. Add a row when pickup, drop, or city travel is required.</td></tr>}</tbody></table></div></section>}

      <footer className="travel-editor-actions"><Button htmlType="button" onClick={onBack}>Cancel</Button>{activeStepIndex > 0 && <Button htmlType="button" onClick={() => setActiveStep(visibleSteps[activeStepIndex - 1])}>Back</Button>}{activeStepIndex < visibleSteps.length - 1 && <Button type="primary" htmlType="button" onClick={() => setActiveStep(visibleSteps[activeStepIndex + 1])}>Continue</Button>}<Button type="primary" htmlType="submit" disabled={!form.travelType || Boolean(options?.showTripDetails && modeOptions.length === 0)}>Save draft</Button></footer>
    </form>
  </section>
}

function SelectLike({ value, options, onChange }: { value: string; options: string[]; onChange: (value: string) => void }) {
  return <Select showSearch allowClear optionFilterProp="label" value={value || undefined} placeholder={options.length ? 'Select' : 'No configured values'} disabled={!options.length} onChange={next => onChange(next ?? '')} options={options.map(item => ({ label: item, value: item }))} />
}

function BookingChoice({ value, onChange }: { value?: TravelBookingAction; onChange: (value: TravelBookingAction) => void }) {
  return <Radio.Group className="travel-booking-choice" value={value || 'Book by myself'} onChange={event => onChange(event.target.value)}><Radio value="Require Booking">Require Booking</Radio><Radio value="Book by myself">Book by myself</Radio></Radio.Group>
}

function TravelTrailModal({ request, trail, onClose }: { request: TravelRequest; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{request.requestNumber || 'Travel request'}</h3></div><small className={`trail-status ${statusClass(request.status)}`}>{request.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This request may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

function normalizeForSave(form: SaveTravelRequest, options: TravelOptions | null): SaveTravelRequest {
  const cities = options?.showTripDetails ? form.cities.filter(item => item.fromLocation || item.toLocation || item.travelMode || item.startDateTime || item.endDateTime) : []
  const accommodationDetails = options?.showAccommodationDetails ? (form.accommodationDetails ?? []).filter(item => item.city || item.checkInDateTime || item.checkOutDateTime || item.occupancy || item.roomPreference || item.remarks) : []
  const localTravelDetails = options?.showLocalTravelDetails ? (form.localTravelDetails ?? []).filter(item => item.city || item.travelDateTime || item.fromLocation || item.toLocation || item.travelMode || item.remarks) : []
  const first = cities[0]
  const last = cities[cities.length - 1]
  return {
    ...form,
    customer: '',
    project: '',
    travelScope: 'Domestic',
    priority: 'Normal',
    cities,
    accommodationDetails,
    localTravelDetails,
    fromLocation: first?.fromLocation || '',
    toLocation: last?.toLocation || '',
    travelMode: first?.travelMode || '',
    startDateTime: first?.startDateTime || form.startDateTime,
    endDateTime: last?.endDateTime || form.endDateTime,
    estimatedCost: 0,
    accommodationRequired: Boolean(options?.showAccommodationDetails && (form.accommodationRequired || accommodationDetails.length > 0)),
    localConveyanceRequired: Boolean(options?.showLocalTravelDetails && (form.localConveyanceRequired || localTravelDetails.length > 0)),
  }
}

function inputDateTime(value: string) {
  return value ? value.slice(0, 16) : ''
}

function dateText(value: string) {
  return value ? new Date(value).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '-'
}

function requestLegs(row: TravelRequest, defaultMode: string): TravelCity[] {
  if (Array.isArray(row.legs) && row.legs.length) return row.legs.map(item => ({ fromLocation: item.fromLocation || '', toLocation: item.toLocation || '', travelMode: item.travelMode || row.travelMode || defaultMode, travelClass: item.travelClass || '', bookingAction: item.bookingAction || 'Book by myself', remarks: item.remarks || '', startDateTime: inputDateTime(item.startDateTime || row.startDateTime), endDateTime: inputDateTime(item.endDateTime || row.endDateTime) }))
  if (!row.fromLocation && !row.toLocation && !row.travelMode) return []
  return [{ fromLocation: row.fromLocation, toLocation: row.toLocation, travelMode: row.travelMode || defaultMode, travelClass: '', bookingAction: 'Book by myself', remarks: '', startDateTime: inputDateTime(row.startDateTime), endDateTime: inputDateTime(row.endDateTime) }]
}

function requestAccommodation(row: TravelRequest): TravelAccommodation[] {
  return (row.accommodationDetails ?? []).map(item => ({
    city: item.city || '',
    checkInDateTime: inputDateTime(item.checkInDateTime || ''),
    checkOutDateTime: inputDateTime(item.checkOutDateTime || ''),
    occupancy: item.occupancy || 'Single',
    roomPreference: item.roomPreference || '',
    bookingAction: item.bookingAction || 'Book by myself',
    remarks: item.remarks || '',
  }))
}

function requestLocalTravel(row: TravelRequest, defaultMode: string): LocalTravelDetail[] {
  return (row.localTravelDetails ?? []).map(item => ({
    city: item.city || '',
    travelDateTime: inputDateTime(item.travelDateTime || ''),
    fromLocation: item.fromLocation || '',
    toLocation: item.toLocation || '',
    travelMode: item.travelMode || defaultMode,
    bookingAction: item.bookingAction || 'Book by myself',
    remarks: item.remarks || '',
  }))
}

function tripSummary(row: TravelRequest) {
  const trips = requestLegs(row, row.travelMode)
  if (!trips.length) return 'Travel Desk not requested'
  const modes = Array.from(new Set(trips.map(item => item.travelMode).filter(Boolean)))
  return `${trips.length} leg${trips.length === 1 ? '' : 's'}${modes.length ? ` / ${modes.join(', ')}` : ''}`
}

function printTravelRequest(row: TravelRequest) {
  const popup = window.open('', '_blank', 'width=980,height=760')
  if (!popup) {
    showToast('Please allow pop-ups to print the travel request.', 'error')
    return
  }
  popup.document.open()
  popup.document.write(travelPrintHtml(row))
  popup.document.close()
  popup.focus()
  window.setTimeout(() => popup.print(), 300)
}

function travelPrintHtml(row: TravelRequest) {
  const legs = requestLegs(row, row.travelMode)
  const stays = row.accommodationDetails ?? []
  const rides = row.localTravelDetails ?? []
  return `<!doctype html><html><head><meta charset="utf-8"><title>Travel Request ${escapeHtml(row.requestNumber || String(row.id))}</title><style>${travelPrintCss()}</style></head><body><main class="doc">
    <header class="doc-head"><div><h1>Travel Request</h1><p>${escapeHtml(row.requestNumber || `Draft #${row.id}`)}</p></div><strong>${escapeHtml(row.status || '-')}</strong></header>
    <section class="info-grid">
      ${printField('Employee', row.employeeName)}
      ${printField('Department', row.department)}
      ${printField('Designation', row.designation)}
      ${printField('Reporting Manager', row.reportingManager)}
      ${printField('Request Date', printDate(row.requestDate))}
      ${printField('Travel Type', row.travelType || '-')}
      ${printField('Policy', row.policyName || 'Not resolved')}
      ${printField('Cost Center', row.costCenter)}
      ${printField('Purpose', row.purpose)}
    </section>
    ${printTable('Trip Details', ['From', 'To', 'Start', 'End', 'Mode', 'Class', 'Booking', 'Remarks'], legs.map(item => [item.fromLocation, item.toLocation, printDateTime(item.startDateTime), printDateTime(item.endDateTime), item.travelMode, item.travelClass, item.bookingAction, item.remarks]))}
    ${printTable('Accommodation Details', ['City', 'Check-in', 'Check-out', 'Occupancy', 'Room preference', 'Booking', 'Remarks'], stays.map(item => [item.city, printDateTime(item.checkInDateTime), printDateTime(item.checkOutDateTime), item.occupancy, item.roomPreference, item.bookingAction, item.remarks]), row.accommodationRequired ? 'Accommodation requested.' : 'No accommodation requested.')}
    ${printTable('Local Travel Details', ['City', 'Date/time', 'From', 'To', 'Mode', 'Booking', 'Remarks'], rides.map(item => [item.city, printDateTime(item.travelDateTime), item.fromLocation, item.toLocation, item.travelMode, item.bookingAction, item.remarks]), row.localConveyanceRequired ? 'Local travel requested.' : 'No local travel requested.')}
    <section class="summary">
      ${printField('Advance Required', row.advanceRequired ? 'Yes' : 'No')}
      ${printField('Advance Amount', row.advanceRequired ? `Rs ${Number(row.advanceAmount || 0).toLocaleString('en-IN')}` : '-')}
      ${printField('Remarks', row.remarks)}
      ${row.cancellationReason ? printField('Cancellation Reason', row.cancellationReason) : ''}
    </section>
    <footer><span>Printed on ${escapeHtml(new Date().toLocaleString('en-IN'))}</span><span>Employee Self Service</span></footer>
  </main></body></html>`
}

function printField(label: string, value?: string | number | null) {
  return `<article><span>${escapeHtml(label)}</span><b>${escapeHtml(value === null || value === undefined || value === '' ? '-' : String(value))}</b></article>`
}

function printTable(title: string, columns: string[], rows: Array<Array<string | number | null | undefined>>, emptyText = 'No details provided.') {
  const body = rows.length ? rows.map(row => `<tr>${columns.map((_, index) => `<td>${escapeHtml(row[index] === null || row[index] === undefined || row[index] === '' ? '-' : String(row[index]))}</td>`).join('')}</tr>`).join('') : `<tr><td colspan="${columns.length}">${escapeHtml(emptyText)}</td></tr>`
  return `<section class="table-section"><h2>${escapeHtml(title)}</h2><table><thead><tr>${columns.map(column => `<th>${escapeHtml(column)}</th>`).join('')}</tr></thead><tbody>${body}</tbody></table></section>`
}

function printDate(value?: string) {
  if (!value) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value.slice(0, 10) : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' })
}

function printDateTime(value?: string) {
  if (!value) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

function escapeHtml(value: string) {
  return value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char] || char))
}

function travelPrintCss() {
  return `@page{size:A4;margin:12mm}*{box-sizing:border-box}body{margin:0;background:#eef2f7;color:#111827;font-family:Arial,Helvetica,sans-serif;font-size:11px}.doc{width:190mm;min-height:277mm;margin:12px auto;padding:16px;background:#fff;border:1px solid #d7dde7}.doc-head{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding-bottom:12px;border-bottom:2px solid #172136}.doc-head h1{margin:0;font-size:22px;letter-spacing:.02em}.doc-head p{margin:4px 0 0;color:#4b5563;font-size:12px}.doc-head strong{border:1px solid #172136;border-radius:999px;padding:5px 12px;font-size:11px;text-transform:uppercase}.info-grid,.summary{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:12px}.summary{grid-template-columns:repeat(2,1fr)}article{min-height:48px;padding:8px;border:1px solid #dfe5ee;background:#fbfcff}article span{display:block;margin-bottom:4px;color:#64748b;font-size:9px;text-transform:uppercase;font-weight:700;letter-spacing:.04em}article b{display:block;font-size:11px;line-height:1.35;word-break:break-word}.table-section{margin-top:14px}.table-section h2{margin:0 0 6px;font-size:13px;color:#172136}table{width:100%;border-collapse:collapse;page-break-inside:auto}tr{page-break-inside:avoid;page-break-after:auto}th{background:#172136;color:#fff;text-align:left;font-size:9px;text-transform:uppercase;letter-spacing:.04em}th,td{padding:7px;border:1px solid #cfd7e3;vertical-align:top;line-height:1.3}td{font-size:10.5px}footer{display:flex;justify-content:space-between;gap:12px;margin-top:18px;padding-top:10px;border-top:1px solid #dfe5ee;color:#64748b;font-size:9px}@media print{body{background:#fff}.doc{width:auto;min-height:auto;margin:0;padding:0;border:0}}`
}
