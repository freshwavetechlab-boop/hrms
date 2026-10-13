import { useState, type ReactNode } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Input, Modal, Select, Space } from 'antd'
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import type { Component } from '../types/payroll'
import SalaryFieldHelp from './SalaryFieldHelp'
import PayrollAdvisor from './PayrollAdvisor'
import SalarySetupGuide from './SalarySetupGuide'
import { usePayrollCatalogue, valueOptions, valueHints } from '../services/payrollCalculationService'
import { attendanceHelp, calculationHelp, payTypeHelp, statutoryHelp, valueHelp, type SalaryHelpOption } from '../utils/salaryFieldGuides'
import { calculationChoices, calculationKind, componentChangeImpact, displayFormula, employeeStatutoryTypes, employerStatutoryTypes, validateSalaryComponent } from '../utils/salaryComponentConfig'
import './SalaryComponentEditor.css'
import './SalaryFormControls.css'

type Props = { value: Component; original?: Component; clientId?: number; components: Component[]; usedBy: string[]; saving: boolean; onChange: (value: Component) => void; onSave: () => Promise<void>; onClose: () => void }
function Field({ label, text, example, children, wide = false, options }: { label: string; text: string; example: string; children: ReactNode; wide?: boolean; options?: SalaryHelpOption[] }) {
  const choices = options ?? (label === 'Calculation method' ? calculationHelp : label === 'Adjust for payable days' ? attendanceHelp : label === 'Fixed / variable classification' ? payTypeHelp : [])
  return <div className={`salary-editor-field${wide ? ' wide' : ''}`}><div className="salary-editor-label"><span>{label}</span><SalaryFieldHelp label={label} text={text} example={example} options={choices} /></div>{children}</div>
}
export default function SalaryComponentEditor({ value, original, clientId, components, usedBy, saving, onChange, onSave, onClose }: Props) {
  const [attempted, setAttempted] = useState(false)
  const [formulaOpen, setFormulaOpen] = useState(false)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [acknowledged, setAcknowledged] = useState(false)
  const [token, setToken] = useState<string>()
  const patch = (change: Partial<Component>) => onChange({ ...value, ...change })
  const kind = calculationKind(value.calculationType)
  const statutory = ['Statutory Deduction', 'Employer Contribution'].includes(value.componentRole)
  const canBeStatutory = ['Deduction', 'Benefit'].includes(value.category)
  const statutoryOptions = value.category === 'Benefit' ? employerStatutoryTypes : employeeStatutoryTypes
  const errors = validateSalaryComponent(value, components, original)
  const doubleProration = kind === 'Formula' && value.proRata && /_EARNED\b|\b(PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS)\b/i.test(displayFormula(value))
  const linked = usedBy.length > 0
  const changed = componentChangeImpact(original, value)
  const daily = kind === 'Daily Rate'
  const save = async () => {
    setAttempted(true)
    if (errors.length) return
    if (doubleProration || (linked && changed)) { setAcknowledged(false); setConfirmOpen(true); return }
    await onSave()
  }
  const setKind = (next: string) => {
    if (next === kind) return
    // Change calculation inputs only after an explicit method selection, never on load/save.
    patch({ calculationType: next, formula: next === 'Formula' ? displayFormula(value) : '', value: ['Fixed Amount', 'Daily Rate'].includes(next) ? value.value : '', ...(next === 'Daily Rate' ? { proRata: false } : {}) })
  }
  const catalogue = usePayrollCatalogue(components, formulaOpen, undefined, String(value.id))
  const formulaOptions = valueOptions(catalogue.values)
  const rawSlabs = (value.formula || value.value || '').split(';').filter(Boolean)
  const slabs = rawSlabs.map(source => { const [range = '', amount = ''] = source.split(':'); const [from = '', to = ''] = range.replace('+', '').split('-'); return { from, to: range.includes('+') ? '' : to, amount } })
  const updateSlabs = (rows: typeof slabs) => patch({ formula: rows.map(row => `${row.from}${row.to ? `-${row.to}` : '+'}:${row.amount}`).join(';'), value: '' })
  const legacy = [
    ['Part of CTC', value.ctc ? 'Yes' : 'No', 'This saved flag is not currently enforced by salary totals. It is preserved, not changed.', 'Employer benefits remain separate from take-home deductions regardless of this flag.'],
    ['EPF wage base', value.epf || 'Never', 'This legacy flag does not select the base for dated PF policies. Configure the PF policy separately.', 'A published PF policy may select WAGES as its monthly base.'],
    ['ESI wage base', value.esi ? 'Yes' : 'No', 'This legacy flag does not drive the current formula calculation. Keep the existing statutory formula/policy.', 'Changing this flag would not change an ESI formula referencing WAGES_EARNED.'],
    ['Recurring', value.recurring ? 'Yes' : 'No', 'Stored classification only; it does not stop a template line running next month.', 'Use an approved period-specific adjustment for a one-time bonus.'],
    ['Scheduled', value.scheduled ? 'Yes' : 'No', 'No payment calendar is attached to this saved flag.', 'A future bonus needs a period-specific approved input.'],
    ['FBP', value.fbp ? 'Yes' : 'No', 'Flexible-benefit metadata is preserved; this screen does not implement an FBP election process.', 'A meal-benefit election would require a separate supported process.'],
    ['Restrict FBP override', value.restrictFbp ? 'Yes' : 'No', 'No payroll override lock is enforced by this legacy flag.', 'Do not rely on it to prevent employee salary changes.'],
    ['Tax / investment class', value.investmentType || 'Not set', 'Preserved classification, not an automatic tax deduction or exemption.', 'Entering 80C here does not create an approved declaration deduction.'],
    ['Correction of', value.correctionOf || 'Not set', 'Preserved reference only; it does not automatically reverse salary.', 'Correct a previous payment through the adjustment process with the original period.']
  ]
  return <>
    <Drawer open title={original ? 'Edit salary component' : 'Add salary component'} width="min(760px, 100vw)" onClose={onClose} className="salary-component-editor salary-form-controls" maskClosable={false}
      footer={<div className="salary-editor-footer"><PayrollAdvisor components={components} component={value} fixedClientId={clientId} onDisableProration={() => patch({ proRata: false })} /><Button onClick={onClose}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save component</Button></div>}>
      {attempted && errors.length > 0 && <Alert type="error" showIcon message="Check these fields" description={<ul>{errors.map(error => <li key={error}>{error}</li>)}</ul>} />}
      <div className="salary-guide-trigger"><SalarySetupGuide value={value} original={original} components={components} usedBy={usedBy} onChange={onChange} /></div>
      <section className="salary-editor-section"><h3>Basic details</h3><div className="salary-editor-grid">
        <Field label="Category" text="Determines whether the component is an earning, employee deduction or employer cost. Chosen from the salary component tab." example="Wages: Earning. Loan recovery: Deduction. Employer insurance: Benefit."><Input readOnly value={value.category === 'Benefit' ? 'Employer Benefit' : value.category} aria-label="Category" /></Field>
        <Field label="Component name" text="Name printed on payroll outputs. Renaming does not change the formula code." example="Special Allowance is easier to read on a payslip than SPL_ALW."><Input value={value.name} onChange={event => patch({ name: event.target.value })} aria-label="Component name" /></Field>
        <Field label="Component code" text="Unique identifier used by formulas and imports. Existing codes are locked; create a new component for a different identity." example="WAGES can be referenced as WAGES_EARNED in another calculation."><Input value={value.code} disabled={!!original} onChange={event => patch({ code: event.target.value.toUpperCase().replace(/\s/g, '_') })} aria-label="Component code" /></Field>
        <Field label="Active" text="Available for new assignments. Deactivating a component used by a template can block future payroll; historical payroll is not recalculated." example="Remove it from future templates before marking it inactive."><Checkbox checked={value.active} onChange={event => patch({ active: event.target.checked })}>Active</Checkbox></Field>
        {canBeStatutory && <Field label="Statutory component" text="Enable only for an actual statutory deduction or employer contribution. Non-statutory benefits need no statutory tag." example="Employee PF: Yes. Loan recovery or insurance benefit: No."><Checkbox checked={statutory} onChange={event => patch({ componentRole: event.target.checked ? value.category === 'Benefit' ? 'Employer Contribution' : 'Statutory Deduction' : value.category === 'Benefit' ? 'Employer Benefit' : 'Regular Deduction', statutoryType: 'None' })}>Statutory component</Checkbox></Field>}
        {canBeStatutory && statutory && <Field options={statutoryHelp(statutoryOptions)} label="Statutory type" text="Used by statutory reports and policy mapping. Selecting a type does not create its rate or eligibility rule." example="Employee PF goes under Deduction; Employer PF goes under Employer Benefit."><Select value={value.statutoryType === 'None' ? undefined : value.statutoryType} placeholder="Select statutory type" options={statutoryOptions.map(item => ({ value: item, label: item }))} onChange={statutoryType => patch({ statutoryType })} aria-label="Statutory type" /></Field>}
      </div></section>
      <section className="salary-editor-section"><h3>Default calculation</h3><div className="salary-editor-grid">
        <Field label="Calculation method" text="Default inherited by templates. Existing templates may override it. Daily rate is a separate opt-in method, never an automatic conversion." example="Monthly: Rs 18,000 x 26/30. Daily: Rs 700 x 22 payable days = Rs 15,400."><Select value={kind} options={calculationChoices.filter(item => item.value !== 'Daily Rate' || value.category === 'Earning').map(item => ({ ...item, disabled: !!original && (kind === 'Daily Rate') !== (item.value === 'Daily Rate') }))} onChange={setKind} aria-label="Calculation method" /></Field>
        {['Fixed Amount', 'Daily Rate'].includes(kind) && <Field label={daily ? 'Default daily rate' : 'Default monthly amount'} text={daily ? 'Rate for one payable day. Employee-specific rates are entered in Employee Master > Salary. Units come from attendance payable days, including paid days allowed by policy.' : 'Default full-month amount. A template or employee salary assignment can supply a different amount. Zero is allowed for a default to be supplied later.'} example={daily ? 'Rate 700 and 22.5 payable days gives Rs 15,750. No division by 30.' : 'Employee A can have Rs 18,000 and employee B Rs 22,000 under the same Wages component.'}><Input inputMode="decimal" value={value.value} onChange={event => patch({ value: event.target.value, formula: '' })} aria-label={daily ? 'Default daily rate' : 'Default monthly amount'} /></Field>}
        {kind === 'Formula' && <Field wide label="Formula" text="Use the full-month code or its earned value deliberately. The same parser is used by the salary simulator. Template references are also validated when calculating payroll." example="WAGES_EARNED * 5% gives Rs 900 on earned wages of Rs 18,000; do not prorate this result again."><div className="salary-editor-formula"><code>{displayFormula(value) || 'Not configured'}</code><Button onClick={() => setFormulaOpen(true)}>Build formula</Button></div></Field>}
        {kind === 'Residual / Balancing' && <Field options={[valueHelp('GROSS'), valueHelp('CTC'), ...components.filter(item => item.active && item.id !== value.id).map(item => valueHelp(item.code))]} label="Balance target" text="Subtracts previously calculated earnings from this target. Existing priority still determines aggregate order. This is not a net-pay guarantee." example="CTC target 25,000 minus prior earnings 21,000 leaves 4,000."><Select showSearch value={value.baseComponent || 'GROSS'} options={['GROSS', 'CTC', ...components.filter(item => item.active && item.id !== value.id).map(item => item.code)].map(item => ({ value: item, label: item }))} onChange={baseComponent => patch({ baseComponent })} aria-label="Balance target" /></Field>}
        {kind === 'Slab Based' && <Field wide label="Slab rules" text="Ranges are inclusive and must not overlap. The current engine compares gross earned so far against the ranges, so ordering matters. No match gives zero; do not use gaps unintentionally." example="0 to 15000: amount 0. 15000.01 and above: amount 200. Leave To empty for no upper limit."><div className="salary-slab-editor">{slabs.map((row, index) => <div className="salary-slab-row" key={index}>{(['from', 'to', 'amount'] as const).map(key => <Input key={key} placeholder={key === 'to' ? 'To (no limit)' : key === 'from' ? 'From' : 'Amount'} aria-label={`Slab ${index + 1} ${key}`} value={row[key]} onChange={event => updateSlabs(slabs.map((item, i) => i === index ? { ...item, [key]: event.target.value } : item))} />)}<Button icon={<DeleteOutlined />} aria-label={`Remove slab ${index + 1}`} onClick={() => updateSlabs(slabs.filter((_, i) => i !== index))} /></div>)}<Button icon={<PlusOutlined />} onClick={() => updateSlabs([...slabs, { from: '', to: '', amount: '' }])}>Add slab</Button></div></Field>}
        {kind === 'Manual / Variable' && <Field wide label="Amount source" text="No automatic monthly amount is calculated. Supply an approved payroll adjustment for the relevant employee and pay period." example="An approved Rs 2,500 incentive for September is picked up in that period, not automatically every month."><Input readOnly value="Approved payroll input / adjustment" /></Field>}
        {daily && <Field label="Payable units" text="Attendance policy determines payable days, including half-days and paid leave/holidays where configured. The rate is multiplied once; no second proration is applied." example="22 worked days + 1 paid holiday = 23 units only when the attendance policy pays that holiday."><Input readOnly value="Attendance payable days" /></Field>}
      </div></section>
      <section className="salary-editor-section"><h3>Payroll treatment</h3><div className="salary-editor-grid">
        {!daily && ['Earning', 'Deduction', 'Benefit'].includes(value.category) && <Field label="Adjust for payable days" text="Apply payable days / payroll days to a full-month result. Turn off for already-earned formulas. Changing this default may affect templates without an override." example="Rs 18,000 x 26/30 = Rs 15,600. WAGES_EARNED * 5% already uses earned wages, so normally choose No."><Select value={String(value.proRata)} options={[{ value: 'true', label: 'Yes: result is a full-month amount' }, { value: 'false', label: 'No: use the calculated amount' }]} onChange={proRata => patch({ proRata: proRata === 'true' })} aria-label="Adjust for payable days" /></Field>}
        {['Earning', 'Reimbursement', 'Benefit'].includes(value.category) && <Field label="Taxable" text={value.category === 'Benefit' ? 'Benefit valuation is not implemented by this checkbox. Its stored value is preserved and cannot be changed here. Do not assume a taxable benefit is included in projected tax.' : 'Includes this earning/reimbursement in the taxable salary basis used by the tax engine. It does not calculate a deduction by itself; use your approved tax configuration.'} example="Taxable wages of Rs 18,000 contribute to the tax projection; the actual tax deduction is calculated separately."><Checkbox checked={value.taxable} disabled={value.category === 'Benefit'} onChange={event => patch({ taxable: event.target.checked })}>Taxable</Checkbox></Field>}
      </div>{doubleProration && <Alert showIcon type="warning" message="Possible double proration" description="This formula already references attendance or earned values. Review whether a second attendance adjustment is intended." />}</section>
      <Collapse><Collapse.Panel key="advanced" header="Advanced settings"><div className="salary-editor-grid">
        <Field label="Business classification" text="Optional descriptive type. It does not select a tax rate or replace statutory identity." example="Basic, House Rent Allowance or Insurance Benefit."><Input value={value.componentType} onChange={event => patch({ componentType: event.target.value })} /></Field>
        <Field label="Calculation priority" text="Existing ordering is preserved. Formula dependencies resolve automatically, but balancing/aggregate calculations and dated PF policy validation still depend on ordering." example="Wages 100, allowance 200, balancing 300. Do not reorder a published PF mapping without reviewing its policy."><Input inputMode="numeric" value={value.priority} onChange={event => patch({ priority: event.target.value.replace(/\D/g, '') })} /></Field>
        <Field label="Fixed / variable classification" text="Used by fixed-earnings aggregates. This setting does not schedule or stop payment." example="SUM(FIXED EARNINGS) excludes rows marked Variable Pay."><Select value={value.payType} options={['Fixed Pay', 'Variable Pay'].map(item => ({ value: item, label: item }))} onChange={payType => patch({ payType })} /></Field>
        <Field label="Existing role" text="Preserved role for existing components. Category and statutory selection control ordinary classification; legacy recovery/correction roles are not silently replaced." example="An existing Recovery role remains Recovery unless you explicitly change statutory classification."><Input readOnly value={value.componentRole} /></Field>
      </div></Collapse.Panel><Collapse.Panel key="legacy" header="Saved legacy settings (read-only)"><div className="salary-editor-grid">{legacy.map(([label, saved, text, example]) => <Field key={label} label={label} text={text} example={example}><Input readOnly value={saved} /></Field>)}</div></Collapse.Panel></Collapse>
    </Drawer>
    <Modal className="salary-form-controls" open={formulaOpen} title="Formula builder" width={780} zIndex={1200} onCancel={() => setFormulaOpen(false)} footer={<Button type="primary" onClick={() => setFormulaOpen(false)}>Done</Button>}>
      {catalogue.loading && <Alert type="info" message="Loading engine inputs..." />}
      {catalogue.error && <Alert type="error" message={catalogue.error} action={<Button onClick={catalogue.reload}>Retry</Button>} />}
      <div className="salary-editor-grid">
        <Field options={valueHints(catalogue.values)} wide label="Available values" text="Choose a value and insert it, then add an operator. A monthly code is before attendance; an earned code is after attendance." example="Select WAGES_EARNED, then multiply by 5%."><Space.Compact block><Select dropdownStyle={{ zIndex: 1400 }} showSearch optionFilterProp="label" value={token} onChange={setToken} options={formulaOptions} placeholder="Select a value" style={{ flex: 1 }} /><Button disabled={!token} onClick={() => patch({ calculationType: 'Formula', formula: `${displayFormula(value)} ${token}`.trim(), value: '' })}>Insert</Button></Space.Compact></Field>
        <Field wide label="Expression" text="Supports +, -, *, /, %, parentheses, MIN, MAX, ROUND, ROUNDUP and ROUNDDOWN. ROUND accepts one value. Constants in statutory formulas are your configuration, not automatically maintained legal rules." example="MIN(WAGES_EARNED, 15000) * 12% means 12% of the lower value. Validate your applicable policy before using it."><Input.TextArea rows={4} value={displayFormula(value)} onChange={event => patch({ calculationType: 'Formula', formula: event.target.value, value: '' })} aria-label="Formula expression" /></Field>
        <Space wrap>{['+', '-', '*', '/', '(', ')', 'MIN(', 'MAX(', 'ROUND('].map(item => <Button key={item} onClick={() => patch({ calculationType: 'Formula', formula: `${displayFormula(value)} ${item}`.trim(), value: '' })}>{item}</Button>)}</Space>
        {errors.length > 0 && <Alert className="wide" type="warning" showIcon message={errors.join(' ')} />}
      </div>
    </Modal>
    <Modal open={confirmOpen} title="Review payroll impact" zIndex={1200} onCancel={() => setConfirmOpen(false)} okText="Confirm and save" confirmLoading={saving} okButtonProps={{ disabled: !acknowledged }} onOk={async () => { await onSave(); setConfirmOpen(false) }}>
      {linked && changed && <p>This changes defaults used by: <strong>{usedBy.join(', ')}</strong>. Future calculations without a template override may change. Review the salary simulation and statutory policy before the next run. Saved payroll results are not recalculated.</p>}
      {doubleProration && <p>The formula uses earned values or attendance and also applies proration. This may reduce the amount twice.</p>}
      <Checkbox checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)}>I have reviewed the calculation and affected templates.</Checkbox>
    </Modal>
  </>
}
