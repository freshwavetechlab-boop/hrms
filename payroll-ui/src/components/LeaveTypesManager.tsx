import { useEffect, useMemo, useState } from 'react'
import { drop0, location0 } from '../data/payrollDefaults'
import { deleteLeaveType, getLeaveTypes, saveLeaveType, setLeaveTypeStatus } from '../services/leaveAttendanceService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import type { Drop, LeaveType, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { type SearchOption } from './SearchSelect'
import { useToast } from './ToastProvider'

const today = new Date().toISOString().slice(0, 10)
const blank: LeaveType = { id: 0, clientId: 0, name: '', code: '', type: 'Paid', description: '', entitlement: 0, entitlementPeriod: 'Yearly', proRateForNewJoinees: false, resetEnabled: false, resetFrequency: 'Yearly', carryForwardUnusedLeaves: false, maxCarryForwardLimit: null, encashUnusedLeaves: false, maxEncashmentLimit: null, allowNegativeLeaveBalance: false, negativeBalanceHandling: 'Mark as LOP', allowPastDates: false, pastDateLimitType: 'No limit', pastDateLimitDays: null, allowFutureDates: true, futureDateLimitType: 'No limit', futureDateLimitDays: null, applicabilityMode: 'All employees', workLocation: '', department: '', designation: '', gender: '', effectiveFrom: today, expiresOn: null, postponeCreditsForNewEmployees: false, postponeCreditValue: null, postponeCreditUnit: 'Days', isActive: true }

const required = (label: string) => <>{label} <em>*</em></>
const numOrNull = (value: string) => value === '' ? null : Number(value)
const opts = (items: string[]): SearchOption[] => items.map(value => ({ value, label: value }))
const anyOpts = (items: string[] | SearchOption[], label = 'Any'): SearchOption[] => [{ value: '', label }, ...items.map(item => typeof item === 'string' ? { value: item, label: item } : item)]

export default function LeaveTypesManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const toast = useToast()
  const [rows, setRows] = useState<LeaveType[]>([]), [form, setForm] = useState<LeaveType>(blank), [editing, setEditing] = useState(false), [drawerOpen, setDrawerOpen] = useState(false), [errors, setErrors] = useState<string[]>([]), [busy, setBusy] = useState(false)
  const [drops, setDrops] = useState<Drop[]>([]), [locations, setLocations] = useState<WorkLocation[]>([])
  const departments = useMemo(() => drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), [drops])
  const designations = useMemo(() => drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value), [drops])
  const load = async () => { const [leaveTypes, dropdowns, workLocations] = await Promise.all([getLeaveTypes(clientId), getDropdowns(), getWorkLocations()]); setRows(leaveTypes); setDrops(dropdowns.length ? dropdowns : [drop0]); setLocations(workLocations.length ? workLocations : [location0]); setForm({ ...blank, clientId }) }
  useEffect(() => { void load() }, [clientId])
  const fail = (items: string[]) => { setErrors(items); items.forEach(item => toast(item, 'error')); return false }
  const set = <K extends keyof LeaveType>(key: K, value: LeaveType[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const validate = () => {
    const next: string[] = []
    const code = form.code.trim().toUpperCase()
    if (!form.name.trim()) next.push('Leave Type Name is required.')
    if (!code) next.push('Code is required.')
    if (!Number.isFinite(form.entitlement)) next.push('Number of leaves is required.')
    if (Number.isFinite(form.entitlement) && form.entitlement < 0) next.push('Number of leaves cannot be negative.')
    if (!form.entitlementPeriod) next.push('Period is required.')
    if (!form.effectiveFrom) next.push('Effective from is required.')
    if (rows.some(row => row.id !== form.id && row.code.toUpperCase() === code)) next.push(`Leave type code "${code}" already exists. Use a unique code.`)
    if (form.expiresOn && form.expiresOn < form.effectiveFrom) next.push('Expiry date cannot be before effective date.')
    return next.length ? fail(next) : true
  }
  const save = async () => {
    if (!validate()) return
    setBusy(true)
    const response = await saveLeaveType({ ...form, clientId, code: form.code.trim().toUpperCase(), name: form.name.trim(), expiresOn: form.expiresOn || null })
    setBusy(false)
    if (response.ok) { setForm({ ...blank, clientId }); setEditing(false); setDrawerOpen(false); onMessage('Leave type saved.'); await load() }
    else fail([response.error || 'Unable to save leave type.'])
  }
  const toggle = async (row: LeaveType) => { const response = await setLeaveTypeStatus(clientId, row.id, !row.isActive); if (response.ok) { onMessage(row.isActive ? 'Leave type disabled.' : 'Leave type enabled.'); await load() } else fail([response.error || 'Unable to update leave type status.']) }
  const remove = async (row: LeaveType) => { if (!window.confirm(`Delete ${row.name}?`)) return; const response = await deleteLeaveType(clientId, row.id); if (response.ok) { onMessage('Leave type deleted.'); await load() } else fail([response.error || 'Unable to delete leave type.']) }
  const edit = (row: LeaveType) => { setForm({ ...blank, ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), expiresOn: row.expiresOn ? String(row.expiresOn).slice(0, 10) : null }); setEditing(true); setErrors([]); setDrawerOpen(true) }
  const add = () => { setForm({ ...blank, clientId }); setEditing(false); setErrors([]); setDrawerOpen(true) }
  const close = () => { setForm({ ...blank, clientId }); setEditing(false); setErrors([]); setDrawerOpen(false) }

  return <section className="leave-types"><div className="card"><header><i className="blue">L</i><div><h3>Leave Types</h3><p>Create paid/unpaid leave policies, reset rules and applicability criteria.</p></div></header><div className="component-table-head"><div><b>Leave policies</b><span>Manage existing leave types here. Add or edit opens the setup drawer.</span></div><button type="button" onClick={add}>Add Leave Type</button></div><div className="component-guide"><b>Setup guide</b><span>Use a unique code for each leave type. Entitlement, reset, carry-forward and applicability rules affect employee balances and leave requests.</span></div><LeaveTypesTable rows={rows} edit={edit} toggle={toggle} remove={remove} /></div>{drawerOpen && <div className="leave-type-drawer-backdrop" onClick={close}><LeaveTypeForm form={form} editing={editing} errors={errors} busy={busy} departments={departments} designations={designations} locations={locations} set={set} save={save} cancel={close} /></div>}</section>
}

function LeaveTypesTable(p: { rows: LeaveType[]; edit: (row: LeaveType) => void; toggle: (row: LeaveType) => void; remove: (row: LeaveType) => void }) {
  return <DataTable rows={p.rows} emptyText="No leave types configured." exportFileName="leave-types" columns={[
    { key: 'name', label: 'Leave Type Name' },
    { key: 'code', label: 'Code' },
    { key: 'type', label: 'Paid/Unpaid' },
    { key: 'entitlementText', label: 'Entitlement', value: row => `${row.entitlement} / ${row.entitlementPeriod}` },
    { key: 'resetPolicy', label: 'Reset Policy', value: row => row.resetEnabled ? `${row.resetFrequency}${row.carryForwardUnusedLeaves ? ' + CF' : ''}${row.encashUnusedLeaves ? ' + Encash' : ''}` : 'No reset' },
    { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Disabled', render: row => <span className={`setup-status ${row.isActive ? 'completed' : 'disabled'}`}>{row.isActive ? 'Active' : 'Disabled'}</span> }
  ]} actions={row => <><button type="button" onClick={() => p.edit(row)}>Edit</button><button type="button" onClick={() => void p.toggle(row)}>{row.isActive ? 'Disable' : 'Enable'}</button><button type="button" className="danger" onClick={() => void p.remove(row)}>Delete</button></>} />
}

function LeaveTypeForm(p: { form: LeaveType; editing: boolean; errors: string[]; busy: boolean; departments: string[]; designations: string[]; locations: WorkLocation[]; set: <K extends keyof LeaveType>(key: K, value: LeaveType[K]) => void; save: () => void; cancel: () => void }) {
  const entitlement = Number.isFinite(p.form.entitlement) ? String(p.form.entitlement) : ''
  return <section className="card leave-type-form leave-type-drawer" role="dialog" aria-modal="true" aria-label={`${p.editing ? 'Edit' : 'Add'} leave type`} onClick={event => event.stopPropagation()}>
    <header><i className="blue">{p.editing ? 'E' : '+'}</i><div><h3>{p.editing ? 'Edit Leave Type' : 'Add Leave Type'}</h3><p>Policy, applicability and validity controls.</p></div><button type="button" className="drawer-close" aria-label="Close leave type drawer" onClick={p.cancel}>×</button></header>
    <div className="leave-type-drawer-body">
    {p.errors.length > 0 && <div className="form-errors sticky-errors">{p.errors.map(error => <p key={error}>{error}</p>)}</div>}
    <div className="grid">
      <label><span>{required('Leave Type Name')}</span><input value={p.form.name} onChange={event => p.set('name', event.target.value)} /></label>
      <label><span>{required('Code')}</span><input value={p.form.code} onChange={event => p.set('code', event.target.value.toUpperCase())} /></label>
      <label><span>{required('Type')}</span><SearchSelect value={p.form.type} onChange={value => p.set('type', value as LeaveType['type'])} options={opts(['Paid', 'Unpaid'])} /></label>
      <label className="wide"><span>Description</span><input value={p.form.description} onChange={event => p.set('description', event.target.value)} /></label>
      <label><span>{required('Number of leaves')}</span><input type="number" min="0" step="0.5" value={entitlement} onChange={event => p.set('entitlement', event.target.value === '' ? Number.NaN : Number(event.target.value))} /></label>
      <label><span>{required('Period')}</span><SearchSelect value={p.form.entitlementPeriod} onChange={value => p.set('entitlementPeriod', value as LeaveType['entitlementPeriod'])} options={opts(['Monthly', 'Yearly'])} /></label>
      <Check label="Pro-rata for new joinees" value={p.form.proRateForNewJoinees} set={value => p.set('proRateForNewJoinees', value)} />
      <Check label="Enable reset" value={p.form.resetEnabled} set={value => p.set('resetEnabled', value)} />
      <label><span>Reset frequency</span><SearchSelect value={p.form.resetFrequency} onChange={value => p.set('resetFrequency', value as LeaveType['resetFrequency'])} options={opts(['Monthly', 'Yearly'])} /></label>
      <Check label="Carry forward unused leaves" value={p.form.carryForwardUnusedLeaves} set={value => p.set('carryForwardUnusedLeaves', value)} />
      <label><span>Max carry forward limit</span><input type="number" value={p.form.maxCarryForwardLimit ?? ''} onChange={event => p.set('maxCarryForwardLimit', numOrNull(event.target.value))} /></label>
      <Check label="Encash unused leaves" value={p.form.encashUnusedLeaves} set={value => p.set('encashUnusedLeaves', value)} />
      <label><span>Max encashment limit</span><input type="number" value={p.form.maxEncashmentLimit ?? ''} onChange={event => p.set('maxEncashmentLimit', numOrNull(event.target.value))} /></label>
    </div>
    <h3>Leave request preferences</h3>
    <div className="grid">
      <Check label="Allow negative leave balance" value={p.form.allowNegativeLeaveBalance} set={value => p.set('allowNegativeLeaveBalance', value)} />
      <label><span>Negative balance handling</span><SearchSelect value={p.form.negativeBalanceHandling} onChange={value => p.set('negativeBalanceHandling', value as LeaveType['negativeBalanceHandling'])} options={opts(['Mark as LOP', 'Without limit', 'Up to year-end limit'])} /></label>
      <Check label="Allow applying for past dates" value={p.form.allowPastDates} set={value => p.set('allowPastDates', value)} />
      <label><span>Past date limit</span><SearchSelect value={p.form.pastDateLimitType} onChange={value => p.set('pastDateLimitType', value as LeaveType['pastDateLimitType'])} options={opts(['No limit', 'Set number of days'])} /></label>
      {p.form.pastDateLimitType !== 'No limit' && <label><span>Past date days</span><input type="number" value={p.form.pastDateLimitDays ?? ''} onChange={event => p.set('pastDateLimitDays', numOrNull(event.target.value))} /></label>}
      <Check label="Allow applying for future dates" value={p.form.allowFutureDates} set={value => p.set('allowFutureDates', value)} />
      <label><span>Future date limit</span><SearchSelect value={p.form.futureDateLimitType} onChange={value => p.set('futureDateLimitType', value as LeaveType['futureDateLimitType'])} options={opts(['No limit', 'Set number of days'])} /></label>
      {p.form.futureDateLimitType !== 'No limit' && <label><span>Future date days</span><input type="number" value={p.form.futureDateLimitDays ?? ''} onChange={event => p.set('futureDateLimitDays', numOrNull(event.target.value))} /></label>}
    </div>
    <h3>Applicability & validity</h3>
    <div className="grid">
      <label><span>Applicability</span><SearchSelect value={p.form.applicabilityMode} onChange={value => p.set('applicabilityMode', value as LeaveType['applicabilityMode'])} options={opts(['All employees', 'Criteria based employees'])} /></label>
      {p.form.applicabilityMode !== 'All employees' && <>
        <label><span>Work Location</span><SearchSelect value={p.form.workLocation} onChange={value => p.set('workLocation', value)} options={anyOpts(p.locations.map(item => item.name))} /></label>
        <label><span>Department</span><SearchSelect value={p.form.department} onChange={value => p.set('department', value)} options={anyOpts(p.departments)} /></label>
        <label><span>Designation</span><SearchSelect value={p.form.designation} onChange={value => p.set('designation', value)} options={anyOpts(p.designations)} /></label>
        <label><span>Gender</span><SearchSelect value={p.form.gender} onChange={value => p.set('gender', value)} options={anyOpts(['Male', 'Female', 'Other'])} /></label>
      </>}
      <label><span>{required('Effective from')}</span><input type="date" value={p.form.effectiveFrom} onChange={event => p.set('effectiveFrom', event.target.value)} /></label>
      <label><span>Expiry date</span><input type="date" value={p.form.expiresOn ?? ''} onChange={event => p.set('expiresOn', event.target.value || null)} /></label>
      <Check label="Postpone leave credits for new employees" value={p.form.postponeCreditsForNewEmployees} set={value => p.set('postponeCreditsForNewEmployees', value)} />
      {p.form.postponeCreditsForNewEmployees && <>
        <label><span>Delay value</span><input type="number" value={p.form.postponeCreditValue ?? ''} onChange={event => p.set('postponeCreditValue', numOrNull(event.target.value))} /></label>
        <label><span>Delay unit</span><SearchSelect value={p.form.postponeCreditUnit} onChange={value => p.set('postponeCreditUnit', value as LeaveType['postponeCreditUnit'])} options={opts(['Days', 'Months'])} /></label>
      </>}
    </div>
    </div>
    <div className="actions"><p>Fields marked <em>*</em> are mandatory.</p><span><button type="button" className="secondary" onClick={p.cancel}>Cancel</button><button type="button" disabled={p.busy} onClick={() => void p.save()}>{p.busy ? 'Saving...' : p.editing ? 'Update leave type' : 'Save leave type'}</button></span></div>
  </section>
}

function Check({ label, value, set }: { label: string; value: boolean; set: (value: boolean) => void }) {
  return <label><span>{label}</span><input type="checkbox" checked={value} onChange={event => set(event.target.checked)} /></label>
}
