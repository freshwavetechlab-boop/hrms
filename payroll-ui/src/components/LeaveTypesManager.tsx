import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Form, Input, InputNumber, Row, Space } from 'antd'
import { drop0, location0 } from '../data/payrollDefaults'
import { deleteLeaveType, getLeaveTypes, saveLeaveType, setLeaveTypeStatus } from '../services/leaveAttendanceService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import type { Drop, LeaveType, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { type SearchOption } from './SearchSelect'
import { useToast } from './ToastProvider'

const today = new Date().toISOString().slice(0, 10)
const blank: LeaveType = { id: 0, clientId: 0, name: '', code: '', type: 'Paid', description: '', entitlement: 0, entitlementPeriod: 'Yearly', proRateForNewJoinees: false, resetEnabled: false, resetFrequency: 'Yearly', carryForwardUnusedLeaves: false, maxCarryForwardLimit: null, encashUnusedLeaves: false, maxEncashmentLimit: null, allowNegativeLeaveBalance: false, negativeBalanceHandling: 'Mark as LOP', allowPastDates: false, pastDateLimitType: 'No limit', pastDateLimitDays: null, allowFutureDates: true, futureDateLimitType: 'No limit', futureDateLimitDays: null, applicabilityMode: 'All employees', workLocation: '', department: '', designation: '', gender: '', effectiveFrom: today, expiresOn: null, postponeCreditsForNewEmployees: false, postponeCreditValue: null, postponeCreditUnit: 'Days', isActive: true }

const opts = (items: string[]): SearchOption[] => items.map(value => ({ value, label: value }))
const anyOpts = (items: string[] | SearchOption[], label = 'Any'): SearchOption[] => [{ value: '', label }, ...items.map(item => typeof item === 'string' ? { value: item, label: item } : item)]

export default function LeaveTypesManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const toast = useToast()
  const [rows, setRows] = useState<LeaveType[]>([])
  const [form, setForm] = useState<LeaveType>(blank)
  const [editing, setEditing] = useState(false)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [drops, setDrops] = useState<Drop[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const departments = useMemo(() => drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), [drops])
  const designations = useMemo(() => drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value), [drops])

  const load = async () => {
    const [leaveTypes, dropdowns, workLocations] = await Promise.all([getLeaveTypes(clientId), getDropdowns(), getWorkLocations()])
    setRows(leaveTypes)
    setDrops(dropdowns.length ? dropdowns : [drop0])
    const activeLocations = workLocations.filter(location => location.isActive && Number(location.clientId) === Number(clientId))
    setLocations(activeLocations.length ? activeLocations : [location0])
    setForm(current => current.id ? current : { ...blank, clientId })
  }
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
  const toggle = async (row: LeaveType) => {
    const response = await setLeaveTypeStatus(clientId, row.id, !row.isActive)
    if (response.ok) { onMessage(row.isActive ? 'Leave type disabled.' : 'Leave type enabled.'); await load() }
    else fail([response.error || 'Unable to update leave type status.'])
  }
  const remove = async (row: LeaveType) => {
    if (!window.confirm(`Delete ${row.name}?`)) return
    const response = await deleteLeaveType(clientId, row.id)
    if (response.ok) { onMessage('Leave type deleted.'); await load() }
    else fail([response.error || 'Unable to delete leave type.'])
  }
  const edit = (row: LeaveType) => { setForm({ ...blank, ...row, effectiveFrom: String(row.effectiveFrom).slice(0, 10), expiresOn: row.expiresOn ? String(row.expiresOn).slice(0, 10) : null }); setEditing(true); setErrors([]); setDrawerOpen(true) }
  const add = () => { setForm({ ...blank, clientId }); setEditing(false); setErrors([]); setDrawerOpen(true) }
  const close = () => { setForm({ ...blank, clientId }); setEditing(false); setErrors([]); setDrawerOpen(false) }
  const metrics = useMemo(() => ({
    total: rows.length,
    active: rows.filter(row => row.isActive).length,
    paid: rows.filter(row => row.type === 'Paid').length,
    unpaid: rows.filter(row => row.type === 'Unpaid').length
  }), [rows])

  return <section className="leave-types">
    <AntCard className="settings-panel settings-table-panel leave-types-panel" size="small" title="Leave Types" extra={<Button type="primary" onClick={add}>Add Leave Type</Button>}>
      <div className="leave-type-summary">
        <span><b>{metrics.total}</b><small>Total policies</small></span>
        <span><b>{metrics.active}</b><small>Active</small></span>
        <span><b>{metrics.paid}</b><small>Paid</small></span>
        <span><b>{metrics.unpaid}</b><small>Unpaid</small></span>
      </div>
      <LeaveTypesTable rows={rows} edit={edit} toggle={toggle} remove={remove} />
    </AntCard>
    {drawerOpen && <div className="leave-type-drawer-backdrop" onClick={close}><LeaveTypeForm form={form} editing={editing} errors={errors} busy={busy} departments={departments} designations={designations} locations={locations} set={set} save={save} cancel={close} /></div>}
  </section>
}

function LeaveTypesTable(p: { rows: LeaveType[]; edit: (row: LeaveType) => void; toggle: (row: LeaveType) => void; remove: (row: LeaveType) => void }) {
  return <DataTable rows={p.rows} emptyText="No leave types configured." exportFileName="leave-types" columns={[
    { key: 'name', label: 'Leave Type Name' },
    { key: 'code', label: 'Code' },
    { key: 'type', label: 'Paid/Unpaid' },
    { key: 'entitlementText', label: 'Entitlement', value: row => `${row.entitlement} / ${row.entitlementPeriod}` },
    { key: 'resetPolicy', label: 'Reset Policy', value: row => row.resetEnabled ? `${row.resetFrequency}${row.carryForwardUnusedLeaves ? ' + CF' : ''}${row.encashUnusedLeaves ? ' + Encash' : ''}` : 'No reset' },
    { key: 'applicabilityMode', label: 'Applicability', value: row => row.applicabilityMode === 'All employees' ? 'All employees' : [row.workLocation, row.department, row.designation, row.gender].filter(Boolean).join(' / ') || 'Criteria based' },
    { key: 'effective', label: 'Effective', value: row => `${formatDate(row.effectiveFrom)}${row.expiresOn ? ` - ${formatDate(row.expiresOn)}` : ''}` },
    { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Disabled', render: row => <span className={`setup-status ${row.isActive ? 'completed' : 'disabled'}`}>{row.isActive ? 'Active' : 'Disabled'}</span> }
  ]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => p.edit(row)}>Edit</Button><Button size="small" onClick={() => void p.toggle(row)}>{row.isActive ? 'Disable' : 'Enable'}</Button><Button size="small" danger onClick={() => void p.remove(row)}>Delete</Button></Space>} />
}

function LeaveTypeForm(p: { form: LeaveType; editing: boolean; errors: string[]; busy: boolean; departments: string[]; designations: string[]; locations: WorkLocation[]; set: <K extends keyof LeaveType>(key: K, value: LeaveType[K]) => void; save: () => void; cancel: () => void }) {
  return <AntCard className="leave-type-form leave-type-drawer settings-panel" role="dialog" aria-modal="true" aria-label={`${p.editing ? 'Edit' : 'Add'} leave type`} size="small" title={p.editing ? 'Edit Leave Type' : 'Add Leave Type'} extra={<Button type="text" aria-label="Close leave type drawer" onClick={p.cancel}>x</Button>} onClick={event => event.stopPropagation()}>
    <Form className="settings-quick-form leave-type-drawer-body" layout="vertical" requiredMark={false}>
      <div className="leave-type-drawer-scroll">
        {p.errors.length > 0 && <Alert type="error" showIcon message={p.errors.join(' ')} />}
        <section className="leave-type-form-section">
          <h3>Basic details</h3>
          <Row gutter={12}>
            <Col xs={24} md={12}><Form.Item label="Leave Type Name" required><Input value={p.form.name} onChange={event => p.set('name', event.target.value)} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Code" required><Input value={p.form.code} onChange={event => p.set('code', event.target.value.toUpperCase())} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Type" required><SearchSelect value={p.form.type} onChange={value => p.set('type', value as LeaveType['type'])} options={opts(['Paid', 'Unpaid'])} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Number of leaves" required><InputNumber min={0} step={0.5} value={Number.isFinite(p.form.entitlement) ? p.form.entitlement : null} onChange={value => p.set('entitlement', value === null ? Number.NaN : Number(value))} style={{ width: '100%' }} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Period" required><SearchSelect value={p.form.entitlementPeriod} onChange={value => p.set('entitlementPeriod', value as LeaveType['entitlementPeriod'])} options={opts(['Monthly', 'Yearly'])} /></Form.Item></Col>
            <Col xs={24}><Form.Item label="Description"><Input value={p.form.description} onChange={event => p.set('description', event.target.value)} /></Form.Item></Col>
          </Row>
        </section>
        <section className="leave-type-form-section">
          <h3>Accrual and reset</h3>
          <Row gutter={12}>
            <Check label="Pro-rata for new joinees" value={p.form.proRateForNewJoinees} set={value => p.set('proRateForNewJoinees', value)} />
            <Check label="Enable reset" value={p.form.resetEnabled} set={value => p.set('resetEnabled', value)} />
            <Col xs={24} md={12}><Form.Item label="Reset frequency"><SearchSelect value={p.form.resetFrequency} onChange={value => p.set('resetFrequency', value as LeaveType['resetFrequency'])} options={opts(['Monthly', 'Yearly'])} /></Form.Item></Col>
            <Check label="Carry forward unused leaves" value={p.form.carryForwardUnusedLeaves} set={value => p.set('carryForwardUnusedLeaves', value)} />
            <Col xs={24} md={12}><Form.Item label="Max carry forward limit"><InputNumber value={p.form.maxCarryForwardLimit} onChange={value => p.set('maxCarryForwardLimit', value === null ? null : Number(value))} style={{ width: '100%' }} /></Form.Item></Col>
            <Check label="Encash unused leaves" value={p.form.encashUnusedLeaves} set={value => p.set('encashUnusedLeaves', value)} />
            <Col xs={24} md={12}><Form.Item label="Max encashment limit"><InputNumber value={p.form.maxEncashmentLimit} onChange={value => p.set('maxEncashmentLimit', value === null ? null : Number(value))} style={{ width: '100%' }} /></Form.Item></Col>
          </Row>
        </section>
        <section className="leave-type-form-section">
          <h3>Leave request rules</h3>
          <Row gutter={12}>
            <Check label="Allow negative leave balance" value={p.form.allowNegativeLeaveBalance} set={value => p.set('allowNegativeLeaveBalance', value)} />
            <Col xs={24} md={12}><Form.Item label="Negative balance handling"><SearchSelect value={p.form.negativeBalanceHandling} onChange={value => p.set('negativeBalanceHandling', value as LeaveType['negativeBalanceHandling'])} options={opts(['Mark as LOP', 'Without limit', 'Up to year-end limit'])} /></Form.Item></Col>
            <Check label="Allow applying for past dates" value={p.form.allowPastDates} set={value => p.set('allowPastDates', value)} />
            <Col xs={24} md={12}><Form.Item label="Past date limit"><SearchSelect value={p.form.pastDateLimitType} onChange={value => p.set('pastDateLimitType', value as LeaveType['pastDateLimitType'])} options={opts(['No limit', 'Set number of days'])} /></Form.Item></Col>
            {p.form.pastDateLimitType !== 'No limit' && <Col xs={24} md={12}><Form.Item label="Past date days"><InputNumber value={p.form.pastDateLimitDays} onChange={value => p.set('pastDateLimitDays', value === null ? null : Number(value))} style={{ width: '100%' }} /></Form.Item></Col>}
            <Check label="Allow applying for future dates" value={p.form.allowFutureDates} set={value => p.set('allowFutureDates', value)} />
            <Col xs={24} md={12}><Form.Item label="Future date limit"><SearchSelect value={p.form.futureDateLimitType} onChange={value => p.set('futureDateLimitType', value as LeaveType['futureDateLimitType'])} options={opts(['No limit', 'Set number of days'])} /></Form.Item></Col>
            {p.form.futureDateLimitType !== 'No limit' && <Col xs={24} md={12}><Form.Item label="Future date days"><InputNumber value={p.form.futureDateLimitDays} onChange={value => p.set('futureDateLimitDays', value === null ? null : Number(value))} style={{ width: '100%' }} /></Form.Item></Col>}
          </Row>
        </section>
        <section className="leave-type-form-section">
          <h3>Applicability and validity</h3>
          <Row gutter={12}>
            <Col xs={24} md={12}><Form.Item label="Applicability"><SearchSelect value={p.form.applicabilityMode} onChange={value => p.set('applicabilityMode', value as LeaveType['applicabilityMode'])} options={opts(['All employees', 'Criteria based employees'])} /></Form.Item></Col>
            {p.form.applicabilityMode !== 'All employees' && <><Col xs={24} md={12}><Form.Item label="Work Location"><SearchSelect value={p.form.workLocation} onChange={value => p.set('workLocation', value)} options={anyOpts(p.locations.map(item => item.name))} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Department"><SearchSelect value={p.form.department} onChange={value => p.set('department', value)} options={anyOpts(p.departments)} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Designation"><SearchSelect value={p.form.designation} onChange={value => p.set('designation', value)} options={anyOpts(p.designations)} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Gender"><SearchSelect value={p.form.gender} onChange={value => p.set('gender', value)} options={anyOpts(['Male', 'Female', 'Other'])} /></Form.Item></Col></>}
            <Col xs={24} md={12}><Form.Item label="Effective from" required><Input type="date" value={p.form.effectiveFrom} onChange={event => p.set('effectiveFrom', event.target.value)} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Expiry date"><Input type="date" value={p.form.expiresOn ?? ''} onChange={event => p.set('expiresOn', event.target.value || null)} /></Form.Item></Col>
            <Check label="Postpone leave credits for new employees" value={p.form.postponeCreditsForNewEmployees} set={value => p.set('postponeCreditsForNewEmployees', value)} />
            {p.form.postponeCreditsForNewEmployees && <><Col xs={24} md={12}><Form.Item label="Delay value"><InputNumber value={p.form.postponeCreditValue} onChange={value => p.set('postponeCreditValue', value === null ? null : Number(value))} style={{ width: '100%' }} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Delay unit"><SearchSelect value={p.form.postponeCreditUnit} onChange={value => p.set('postponeCreditUnit', value as LeaveType['postponeCreditUnit'])} options={opts(['Days', 'Months'])} /></Form.Item></Col></>}
          </Row>
        </section>
      </div>
      <div className="leave-type-drawer-footer"><Space><Button onClick={p.cancel}>Cancel</Button><Button type="primary" loading={p.busy} onClick={() => void p.save()}>{p.editing ? 'Update leave type' : 'Save leave type'}</Button></Space></div>
    </Form>
  </AntCard>
}

function Check({ label, value, set }: { label: string; value: boolean; set: (value: boolean) => void }) {
  return <Col xs={24} md={12}><Form.Item><AntCheckbox checked={value} onChange={event => set(event.target.checked)}>{label}</AntCheckbox></Form.Item></Col>
}

function formatDate(value?: string | null) {
  if (!value) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' })
}
