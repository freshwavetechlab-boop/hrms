import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Checkbox, Collapse, Input, Modal, Radio, Select as AntSelect, Steps, type SelectProps } from 'antd'
import { CompassOutlined, DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import type { Component } from '../types/payroll'
import { calculationChoices, calculationKind, displayFormula, employeeStatutoryTypes, employerStatutoryTypes, validateSalaryComponent } from '../utils/salaryComponentConfig'
import { calculationHelp, attendanceHelp, payTypeHelp, statutoryHelp, valueHelp, type SalaryHelpOption } from '../utils/salaryFieldGuides'
import SalaryFieldHelp from './SalaryFieldHelp'
import { usePayrollCatalogue, valueHints } from '../services/payrollCalculationService'

type Props = { value: Component; original?: Component; components: Component[]; usedBy: string[]; onChange: (value: Component) => void }
type Slab = { from: string; to: string; amount: string }
const steps = ['Details', 'Classification', 'Calculation', 'Treatment', 'Ordering', 'Review']
const yesNo = (value: boolean) => value ? 'Yes' : 'No'
function Select(props: SelectProps<string>) {
  return <AntSelect {...props} dropdownStyle={{ zIndex: 1400 }} />
}
function Question({ label, text, example, options, children, wide = false }: { label: string; text: string; example: string; options?: SalaryHelpOption[]; children: ReactNode; wide?: boolean }) {
  return <div className={`salary-editor-field${wide ? ' wide' : ''}`}><div className="salary-editor-label"><span>{label}</span><SalaryFieldHelp label={label} text={text} example={example} options={options} /></div>{children}</div>
}

export default function SalarySetupGuide({ value, original, components, usedBy, onChange }: Props) {
  const [open, setOpen] = useState(false), [step, setStep] = useState(0)
  const [draft, setDraft] = useState(value), [initial, setInitial] = useState(value)
  const [attempted, setAttempted] = useState(false), [acknowledged, setAcknowledged] = useState(false)
  const [formulaStyle, setFormulaStyle] = useState('custom'), [base, setBase] = useState<string>(), [rate, setRate] = useState(''), [ceiling, setCeiling] = useState('')
  const [token, setToken] = useState<string>()
  const [slabs, setSlabs] = useState<Slab[]>([])
  const bodyRef = useRef<HTMLDivElement>(null)
  useEffect(() => { bodyRef.current?.parentElement?.scrollTo({ top: 0 }) }, [step, open])
  const start = () => {
    setDraft({ ...value }); setInitial({ ...value }); setStep(0); setAttempted(false); setAcknowledged(false)
    setFormulaStyle('custom'); setBase(undefined); setRate(''); setCeiling(''); setToken(undefined)
    setSlabs((value.formula || value.value || '').split(';').filter(Boolean).map(source => {
      const [range = '', amount = ''] = source.split(':'); const [from = '', to = ''] = range.replace('+', '').split('-')
      return { from, to: range.includes('+') ? '' : to, amount }
    }))
    setOpen(true)
  }
  const patch = (change: Partial<Component>) => { setDraft(current => ({ ...current, ...change })); setAcknowledged(false) }
  const kind = calculationKind(draft.calculationType), daily = kind === 'Daily Rate'
  const statutory = ['Statutory Deduction', 'Employer Contribution'].includes(draft.componentRole)
  const canBeStatutory = ['Deduction', 'Benefit'].includes(draft.category)
  const statutoryOptions = draft.category === 'Benefit' ? employerStatutoryTypes : employeeStatutoryTypes
  const catalogue = usePayrollCatalogue(components, open, undefined, String(draft.id))
  const tokens = catalogue.values.map(v => v.token)
  const targets = ['GROSS', 'CTC', ...components.filter(c => c.active && c.id !== draft.id).map(c => c.code)]
  const numeric = (text: string) => /^(?:\d+(?:\.\d*)?|\.\d+)$/.test(text.trim()) && Number.isFinite(Number(text))
  const assistedFormula = formulaStyle !== 'custom' && kind === 'Formula'
  const assistanceErrors = assistedFormula ? [
    ...(!base || !tokens.includes(base) ? ['Select the value to calculate from.'] : []),
    ...(!numeric(rate) || Number(rate) > 100 ? ['Enter a percentage from 0 to 100. Use a custom formula for other multipliers.'] : []),
    ...(formulaStyle === 'capped' && (!numeric(ceiling) || Number(ceiling) <= 0) ? ['Enter a ceiling greater than zero.'] : [])
  ] : []
  const expression = assistedFormula ? (formulaStyle === 'capped' ? `MIN(${base || ''}, ${ceiling}) * ${rate}%` : `${base || ''} * ${rate}%`) : displayFormula(draft)
  const candidate = assistedFormula ? { ...draft, calculationType: 'Formula', formula: expression, value: '' } : draft
  const doubleProration = kind === 'Formula' && candidate.proRata && /_EARNED\b|\b(PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS)\b/i.test(expression)
  const fieldErrors = (keys: (keyof Component)[]) => {
    // Keep unrelated incomplete answers out of step-level validation.
    const baseline = { ...(original || candidate), name: 'Draft', componentRole: original?.componentRole || '', statutoryType: original?.statutoryType || 'None', calculationType: 'Fixed Amount', value: '0', formula: '', baseComponent: '' }
    const subject = { ...baseline }
    for (const key of keys) Object.assign(subject, { [key]: candidate[key] })
    return validateSalaryComponent(subject, components, original ? baseline : undefined)
  }
  const stepErrors = (index: number): string[] => {
    if (index === 0) return fieldErrors(['name', 'code'])
    if (index === 1) return fieldErrors(['componentRole', 'statutoryType'])
    if (index === 2) return [...assistanceErrors, ...validateSalaryComponent({ ...candidate, name: candidate.name || 'Draft' }, components, original).filter(e => !e.includes('statutory type'))]
    if (index === 4 && (!/^\d+$/.test(draft.priority) || !Number.isSafeInteger(Number(draft.priority)) || Number(draft.priority) > 2147483647) && draft.priority !== original?.priority) return ['Enter a whole-number priority between 0 and 2147483647.']
    return []
  }
  const allErrors = [...new Set([...validateSalaryComponent(candidate, components, original), ...assistanceErrors, ...stepErrors(4)])]
  const visibleErrors = step === 5 ? allErrors : stepErrors(step)
  const changeKind = (next: string) => {
    if (next === kind) return
    patch({ calculationType: next, formula: next === 'Formula' ? displayFormula(draft) : '', value: ['Fixed Amount', 'Daily Rate'].includes(next) ? draft.value : '', ...(next === 'Daily Rate' ? { proRata: false } : {}) })
    setFormulaStyle('custom'); setSlabs([])
  }
  const updateSlabs = (rows: Slab[]) => { setSlabs(rows); patch({ formula: rows.map(row => `${row.from}${row.to ? `-${row.to}` : '+'}:${row.amount}`).join(';'), value: '' }) }
  const goNext = () => { setAttempted(true); if (visibleErrors.length) return; setStep(step + 1); setAttempted(false) }
  const reviewRows: { label: string; keys: (keyof Component)[]; show: (c: Component) => string }[] = [
    { label: 'Component name', keys: ['name'], show: c => c.name },
    { label: 'Component code', keys: ['code'], show: c => c.code },
    { label: 'Active', keys: ['active'], show: c => yesNo(c.active) },
    { label: 'Payroll role', keys: ['componentRole'], show: c => c.componentRole },
    { label: 'Statutory type', keys: ['statutoryType'], show: c => c.statutoryType },
    { label: 'Calculation', keys: ['calculationType'], show: c => c.calculationType },
    { label: 'Amount / rules', keys: ['value', 'formula', 'baseComponent'], show: c => calculationKind(c.calculationType) === 'Formula' ? displayFormula(c) : calculationKind(c.calculationType) === 'Residual / Balancing' ? c.baseComponent || 'GROSS' : c.formula || c.value || 'Period-specific input' },
    { label: 'Attendance adjustment', keys: ['proRata'], show: c => c.calculationType === 'Daily Rate' ? 'Rate x payable days, once' : yesNo(c.proRata) },
    { label: 'Taxable', keys: ['taxable'], show: c => yesNo(c.taxable) },
    { label: 'Business classification', keys: ['componentType'], show: c => c.componentType || 'Not set' },
    { label: 'Fixed / variable', keys: ['payType'], show: c => c.payType },
    { label: 'Priority', keys: ['priority'], show: c => c.priority }
  ]
  const legacy = [
    ['Part of CTC', yesNo(draft.ctc), 'Stored flag; it does not currently control salary totals.', 'An employer benefit remains separate from take-home deductions.'],
    ['EPF wage base', draft.epf || 'Never', 'The dated PF policy, not this legacy flag, selects its wage base.', 'Review the base mapped in the applicable PF policy.'],
    ['ESI wage base', yesNo(draft.esi), 'This flag does not change the current ESI formula.', 'A formula using WAGES_EARNED keeps using that value.'],
    ['Recurring', yesNo(draft.recurring), 'Stored classification; it does not stop a template running next month.', 'Use an approved period-specific adjustment for a one-time bonus.'],
    ['Scheduled', yesNo(draft.scheduled), 'No payment calendar is attached to this saved flag.', 'A future bonus needs a period-specific approved input.'],
    ['FBP', yesNo(draft.fbp), 'Preserved metadata; this guide does not implement benefit elections.', 'A meal-benefit election requires its own supported process.'],
    ['Restrict FBP override', yesNo(draft.restrictFbp), 'This flag does not enforce a payroll override lock.', 'Do not use it to prevent employee salary changes.'],
    ['Tax / investment class', draft.investmentType || 'Not set', 'Classification only; not an automatic exemption.', '80C here does not create an approved declaration deduction.'],
    ['Correction of', draft.correctionOf || 'Not set', 'Reference only; it does not reverse salary automatically.', 'Correct a prior payment through the adjustment process.']
  ]
  return <><Button aria-label="Guide me" size="small" icon={<CompassOutlined />} onClick={start}>Guide me</Button>
    <Modal className="salary-setup-guide salary-form-controls" open={open} title="Salary component setup" width={900} zIndex={1300} maskClosable={false} onCancel={() => setOpen(false)} footer={<div className="salary-editor-footer"><Button onClick={() => setOpen(false)}>Cancel</Button>{step > 0 && <Button onClick={() => { setStep(step - 1); setAttempted(false) }}>Back</Button>}{step < 5 ? <Button type="primary" onClick={goNext}>Next</Button> : <Button type="primary" disabled={allErrors.length > 0 || (doubleProration && !acknowledged)} onClick={() => { if (!allErrors.length && (!doubleProration || acknowledged)) { onChange(candidate); setOpen(false) } }}>Apply to draft</Button>}</div>}>
      <Steps current={step} size="small" responsive items={steps.map((title, index) => ({ title, disabled: index > step }))} onChange={index => { if (index < step) { setStep(index); setAttempted(false) } }} />
      <div className="salary-guide-body" key={step} ref={bodyRef}>
        <h3>Step {step + 1} of {steps.length}: {steps[step]}</h3>
        {(attempted || step === 5) && visibleErrors.length > 0 && <Alert type="error" showIcon message="Check these answers" description={<ul>{visibleErrors.map(e => <li key={e}>{e}</li>)}</ul>} />}
        {step === 0 && <div className="salary-editor-grid">
          <Question label="Where does this amount belong?" text="Category comes from the tab where you opened the component. It is not changed by this guide." example="Wages: Earning. Loan recovery: Deduction. Company insurance: Employer Benefit."><Input readOnly value={draft.category === 'Benefit' ? 'Employer Benefit' : draft.category} /></Question>
          <Question label="What should appear on the payslip?" text="Use a readable component name. This does not change its formula code." example="House Rent Allowance is clearer than HRA_AMT."><Input aria-label="Guided component name" value={draft.name} onChange={e => patch({ name: e.target.value })} /></Question>
          <Question label="What is its unique formula code?" text="Codes identify components in formulas. Existing codes are locked to protect references." example="Use WAGES; another formula can reference WAGES_EARNED."><Input aria-label="Guided component code" disabled={!!original} value={draft.code} onChange={e => patch({ code: e.target.value.toUpperCase().replace(/\s/g, '_') })} /></Question>
          <Question label="Should it be available for use?" text="Inactive components cannot be used in future calculations. Check linked templates before deactivating." example="Remove an old allowance from future templates before making it inactive."><Checkbox checked={draft.active} onChange={e => patch({ active: e.target.checked })}>Available for use</Checkbox></Question>
        </div>}
        {step === 1 && <div className="salary-editor-grid">
          {canBeStatutory && <Question wide label="Is this a statutory contribution or deduction?" text="Yes identifies a statutory item. Changing this answer changes the role and clears its old statutory type. No means an ordinary deduction or benefit." example="Employee PF: Yes. Loan recovery or private insurance: No."><Radio.Group value={statutory} onChange={e => patch({ componentRole: e.target.value ? draft.category === 'Benefit' ? 'Employer Contribution' : 'Statutory Deduction' : draft.category === 'Benefit' ? 'Employer Benefit' : 'Regular Deduction', statutoryType: 'None' })}><Radio value={true}>Yes</Radio><Radio value={false}>No</Radio></Radio.Group></Question>}
          {canBeStatutory && statutory && <Question wide label="Which statutory item is it?" text="The tag supports policy mapping and reporting. It does not supply rates, eligibility or dates." example="Choose PF Employee for the employee share, not the company share." options={statutoryHelp(statutoryOptions)}><Select aria-label="Guided statutory type" value={draft.statutoryType === 'None' ? undefined : draft.statutoryType} options={statutoryOptions.map(v => ({ value: v, label: v }))} onChange={statutoryType => patch({ statutoryType })} placeholder="Select statutory type" /></Question>}
          <Question label="Payroll role" text="The existing role is retained unless you explicitly change statutory classification. Legacy recovery or correction roles are not automatically replaced." example="Employer contributions are company costs, not take-home deductions."><Input readOnly value={draft.componentRole} /></Question>
          <Question label="How should the business classify it?" text="Optional description; this does not pick a formula, tax rate or statutory rule." example="Basic, House Rent Allowance or Insurance Benefit."><Input aria-label="Guided business classification" value={draft.componentType} onChange={e => patch({ componentType: e.target.value })} /></Question>
          {statutory && <Alert className="wide" type="info" showIcon message="Statutory rules need separate review" description="Confirm policy, wage base, eligibility, effective dates and rounding. Neither this tag nor AI suggestions certify compliance." />}
        </div>}
        {step === 2 && <div className="salary-editor-grid">
          {catalogue.loading && <Alert className="wide" type="info" message="Loading engine inputs..." />}
          {catalogue.error && <Alert className="wide" type="error" message={catalogue.error} action={<Button onClick={catalogue.reload}>Retry</Button>} />}
          <Question wide label="How should the amount be calculated?" text="Select the method matching your requirement. Templates can override the component default." example="Monthly 18,000 and daily 700 use different pay bases." options={calculationHelp}><Select aria-label="Guided calculation method" value={kind} options={calculationChoices.filter(c => c.value !== 'Daily Rate' || draft.category === 'Earning').map(c => ({ ...c, disabled: !!original && (original.calculationType === 'Daily Rate') !== (c.value === 'Daily Rate') }))} onChange={changeKind} /></Question>
          {original && <Alert className="wide" type="info" message="Monthly and daily pay bases cannot be interchanged on an existing component." />}
          {['Fixed Amount', 'Daily Rate'].includes(kind) && <Question wide label={daily ? 'What is the rate for one payable day?' : 'What is the default full-month amount?'} text={daily ? 'Enter one day of pay. Employee-specific rates can be assigned in Employee Master > Salary.' : 'A template or employee assignment can supply another amount. Zero is allowed as a default.'} example={daily ? '700 x 22.5 payable days = 15,750.' : '18,000 x 26 / 30 = 15,600 with attendance adjustment.'}><Input aria-label="Guided amount" inputMode="decimal" value={draft.value} onChange={e => patch({ value: e.target.value, formula: '' })} /></Question>}
          {kind === 'Formula' && <>
            <Question wide label="Which formula pattern fits?" text="A pattern builds arithmetic from your answers. It does not choose legal rates. Existing formulas stay unchanged until edited." example="5% of earned wages uses WAGES_EARNED * 5%." options={[{ label: 'Custom / existing formula', meaning: 'Keep or write a supported expression.', example: 'MAX(WAGES_EARNED * 5%, 500)' }, { label: 'Percentage of a value', meaning: 'Multiply a selected value by a percentage.', example: '18,000 x 5% = 900.' }, { label: 'Percentage with a ceiling', meaning: 'Take the lower of the base and ceiling before multiplying.', example: 'MIN(18,000, 15,000) x 10% = 1,500. Illustration only.' }]}><Select aria-label="Guided formula pattern" value={formulaStyle} options={[{ value: 'custom', label: 'Custom / existing formula' }, { value: 'percentage', label: 'Percentage of a value' }, { value: 'capped', label: 'Percentage with a ceiling' }]} onChange={next => { if (assistedFormula && next === 'custom') patch({ calculationType: 'Formula', formula: expression, value: '' }); setFormulaStyle(next); setAcknowledged(false) }} /></Question>
            {assistedFormula && <><Question wide label="Which value should be used?" text="Monthly means before attendance; earned means after attendance. All available values are explained below." example="WAGES_EARNED is 9,000 when monthly WAGES is 18,000 at 15/30 days." options={valueHints(catalogue.values)}><Select aria-label="Guided formula base" showSearch value={base} options={tokens.map(t => ({ value: t, label: t }))} onChange={v => { setBase(v); setAcknowledged(false) }} /></Question><Question label="What percentage?" text="Enter the percentage number, not its decimal multiplier." example="Enter 5 for 5%, not 0.05."><Input aria-label="Guided percentage" inputMode="decimal" value={rate} onChange={e => { setRate(e.target.value); setAcknowledged(false) }} /></Question>{formulaStyle === 'capped' && <Question label="What is the maximum base?" text="Caps the base before applying the percentage, not the final amount." example="Base 18,000, ceiling 15,000 and 10% gives 1,500. Arithmetic illustration only."><Input aria-label="Guided ceiling" inputMode="decimal" value={ceiling} onChange={e => { setCeiling(e.target.value); setAcknowledged(false) }} /></Question>}</>}
            {!assistedFormula && <Question wide label="Available formula values" text="Insert monthly or earned values deliberately. A component cannot reference itself." example="WAGES_EARNED already accounts for attendance." options={valueHints(catalogue.values)}><div className="salary-guide-token"><Select aria-label="Guided formula token" showSearch value={token} options={tokens.map(t => ({ value: t, label: t }))} onChange={setToken} /><Button disabled={!token} onClick={() => patch({ calculationType: 'Formula', formula: `${displayFormula(draft)} ${token}`.trim(), value: '' })}>Insert</Button></div></Question>}
            <Question wide label="Formula expression" text="Supports +, -, *, /, %, parentheses, MIN, MAX, ROUND, ROUNDUP and ROUNDDOWN. ROUND accepts one value. Referenced components must also be included in the salary template." example="WAGES_EARNED * 5% already includes attendance."><Input.TextArea aria-label="Guided formula expression" rows={3} readOnly={assistedFormula} value={expression} onChange={e => patch({ calculationType: 'Formula', formula: e.target.value, value: '' })} /></Question>
          </>}
          {kind === 'Residual / Balancing' && <Question wide label="Which target should the earnings balance to?" text="Subtracts earlier earnings from this target. Priority matters; it does not guarantee net take-home pay." example="Target 25,000 minus earlier earnings 21,000 leaves 4,000." options={targets.map(valueHelp)}><Select aria-label="Guided balance target" showSearch value={draft.baseComponent || 'GROSS'} options={targets.map(t => ({ value: t, label: t }))} onChange={baseComponent => patch({ baseComponent })} /></Question>}
          {kind === 'Slab Based' && <Question wide label="Which amount applies in each range?" text="Ranges include both endpoints and must not overlap. The engine uses gross earned so far. No match means zero. Order this component after the earnings it needs." example="0-15000 pays 0; 15000.01 and above pays 200. This selects one amount, not progressive income tax."><div className="salary-slab-editor">{slabs.map((row, index) => <div className="salary-slab-row" key={index}>{(['from', 'to', 'amount'] as const).map(key => <Input key={key} inputMode="decimal" aria-label={`Guided slab ${index + 1} ${key}`} placeholder={key === 'to' ? 'To (no limit)' : key === 'from' ? 'From' : 'Amount'} value={row[key]} onChange={e => updateSlabs(slabs.map((s, i) => i === index ? { ...s, [key]: e.target.value } : s))} />)}<Button icon={<DeleteOutlined />} aria-label={`Remove guided slab ${index + 1}`} onClick={() => updateSlabs(slabs.filter((_, i) => i !== index))} /></div>)}<Button aria-label="Add range" icon={<PlusOutlined />} onClick={() => updateSlabs([...slabs, { from: '', to: '', amount: '' }])}>Add range</Button></div></Question>}
          {kind === 'Manual / Variable' && <Alert className="wide" type="info" message="Amount comes from an approved payroll input" description="For example, approve a 2,500 incentive for the employee and September pay period. It does not automatically repeat in later periods." />}
        </div>}
        {step === 3 && <div className="salary-editor-grid">
          {daily ? <Question wide label="Which days determine pay?" text="Attendance policy supplies payable days, including fractions and paid leave where applicable. Daily rates are multiplied once." example="700 x 22.5 payable days = 15,750."><Input readOnly value="Attendance payable days" /></Question> : ['Earning', 'Deduction', 'Benefit'].includes(draft.category) && <Question wide label="Should unpaid days reduce the amount?" text="Choose Yes for a full-month result. Choose No when attendance is already in the formula or the amount is earned." example="18,000 at 15/30 days becomes 9,000 with Yes." options={attendanceHelp}><Radio.Group aria-label="Guided attendance" value={draft.proRata} onChange={e => patch({ proRata: e.target.value })} className="salary-guide-options"><Radio value={true}>Yes: result is a full-month amount</Radio><Radio value={false}>No: use the calculated amount</Radio></Radio.Group></Question>}
          {['Earning', 'Reimbursement', 'Benefit'].includes(draft.category) && <Question wide label="Does this contribute to taxable salary?" text={draft.category === 'Benefit' ? 'Benefit valuation is not implemented by this checkbox; its saved value is retained.' : 'Includes the earning in the taxable salary basis. This does not calculate a deduction or approve an exemption.'} example="Taxable wages contribute to the projection; actual tax comes from the tax configuration."><Checkbox disabled={draft.category === 'Benefit'} checked={draft.taxable} onChange={e => patch({ taxable: e.target.checked })}>Include in taxable salary</Checkbox></Question>}
          {doubleProration && <Alert className="wide" showIcon type="warning" message="Attendance may be applied twice" description="This formula uses earned values or attendance and reduces the result again. An earned 900 becomes 450 at 15/30 days." />}
        </div>}
        {step === 4 && <div className="salary-editor-grid">
          <Question label="Fixed or variable for salary aggregates?" text="Controls fixed-earnings aggregates, not whether it runs each month." example="SUM(FIXED EARNINGS) excludes a Variable Pay incentive." options={payTypeHelp}><Select aria-label="Guided pay classification" value={draft.payType} options={['Fixed Pay', 'Variable Pay'].map(t => ({ value: t, label: t }))} onChange={payType => patch({ payType })} /></Question>
          <Question label="When should it be calculated?" text="Lower numbers run earlier. Formula dependencies resolve automatically, but balancing, aggregate values and dated PF mappings still depend on ordering." example="Wages 100, allowance 200, balance 300. Review policies before changing priority."><Input aria-label="Guided priority" inputMode="numeric" value={draft.priority} onChange={e => patch({ priority: e.target.value })} /></Question>
          <Question wide label="Which other component priorities are in use?" text="Only components in a template take part in its calculation. This is the available master list, not a simulation." example="Put a slab using gross earned after its earnings."><div className="salary-guide-reference">{components.filter(c => c.active && c.id !== draft.id).sort((a, b) => Number(a.priority) - Number(b.priority)).map(c => <div key={c.id}><span>{c.code}</span><span>{c.priority || 'Not set'}</span></div>)}{!components.some(c => c.active && c.id !== draft.id) && <span>No other active components.</span>}</div></Question>
          <Collapse className="wide"><Collapse.Panel key="legacy" header="Preserved legacy settings (read-only)"><div className="salary-editor-grid">{legacy.map(([label, saved, text, example]) => <Question key={label} label={label} text={text} example={example}><Input readOnly value={saved} /></Question>)}</div></Collapse.Panel></Collapse>
        </div>}
        {step === 5 && <>
          <div className="salary-guide-review"><div className="salary-guide-review-head"><span>Setting</span><span>Before guide</span><span>Proposed draft</span></div>{reviewRows.map(row => <div className={row.keys.some(k => initial[k] !== candidate[k]) ? 'is-changed' : ''} key={row.label}><strong>{row.label}</strong><span>{row.show(initial) || 'Not set'}</span><span>{row.show(candidate) || 'Not set'}</span></div>)}</div>
          {usedBy.length > 0 && <Alert type="warning" showIcon message="Used by existing templates" description={`These templates may inherit changed defaults: ${usedBy.join(', ')}. Template overrides remain in place. Saved payroll is not recalculated.`} />}
          {doubleProration && <Alert type="warning" showIcon message="Confirm the second attendance adjustment" description={<Checkbox checked={acknowledged} onChange={e => setAcknowledged(e.target.checked)}>I intend to adjust this attendance-based formula again.</Checkbox>} />}
          <Alert type="info" showIcon message="Apply updates the draft only" description="Save separately after review. Use salary-template simulation to check employee amounts and payable days. This guide does not approve statutory compliance." />
        </>}
      </div>
    </Modal></>
}
