import { Select } from 'antd'
import type { ReactNode } from 'react'
import './PayrollScopeSelect.css'

export type PayrollScopeOption = {
  value: string | number
  label: string
  count: number
  hint?: string
  available?: boolean
  hasPayroll?: boolean
  hasTemplate?: boolean
}

type Props = {
  value: string | number
  options: PayrollScopeOption[]
  onChange: (value: string | number) => void
  disabled?: boolean
  ariaLabel: string
  placeholder?: string
  availabilityKind?: 'employees' | 'monthly-payroll'
}

type RenderedOption = {
  value: string | number
  label: ReactNode
  selectedLabel: ReactNode
  plainLabel: string
  title: string
}

const employeeCount = (count: number) => Math.max(0, Number.isFinite(count) ? Math.trunc(count) : 0)

function Count({ count, selected = false, available = count > 0 }: { count: number; selected?: boolean; available?: boolean }) {
  const text = count.toLocaleString('en-IN')
  return <span className={`payroll-scope-count${available ? ' has-employees' : ' is-empty'}${selected ? ' is-selected-label' : ''}`}
    aria-label={`${text} employees`} title={`${text} employees`}>{text}</span>
}

function Markers({ available, hasPayroll, label, presenceLabel = 'Payroll exists · any month or location' }: { available: boolean; hasPayroll: boolean; label: string; presenceLabel?: string }) {
  return <span className="payroll-scope-markers">
    <span className={`payroll-scope-dot payroll-scope-availability-dot${available ? ' has-employees' : ''}`} role="img"
      aria-label={label} title={label} />
    {hasPayroll && <span className="payroll-scope-dot payroll-scope-payroll-dot" role="img"
      aria-label={presenceLabel} title={presenceLabel} />}
  </span>
}

/** Scoped payroll picker; counts are supplied by the already authorized roster. */
export default function PayrollScopeSelect({ value, options, onChange, disabled = false, ariaLabel, placeholder = 'Select', availabilityKind = 'employees' }: Props) {
  const selected = options.find(option => option.value === value)
    ?? options.find(option => String(option.value) === String(value))
  const rendered: RenderedOption[] = options.map(option => {
    const count = employeeCount(option.count)
    const available = option.available ?? count > 0
    const hasPayroll = option.hasPayroll === true || option.hasTemplate === true
    const presenceLabel = option.hasTemplate ? 'Salary template configured' : 'Payroll exists · any month or location'
    const availabilityLabel = availabilityKind === 'monthly-payroll' ? available ? 'Payroll exists for selected month' : 'No saved payroll for selected month' : available ? 'Employees available' : 'No active employees'
    const title = `${option.label} · ${count.toLocaleString('en-IN')} employees${hasPayroll ? ' · ' + presenceLabel : ''}`
    return {
      value: option.value,
      plainLabel: option.label,
      title,
      label: <div className={`payroll-scope-option${available ? ' has-employees' : ' is-empty'}`}>
        <Markers available={available} hasPayroll={hasPayroll} label={availabilityLabel} presenceLabel={presenceLabel} />
        <span className="payroll-scope-option-copy"><span className="payroll-scope-option-name">{option.label}</span>{option.hint && <span className="payroll-scope-option-hint">{option.hint}</span>}</span>
        <Count count={count} available={available} />
      </div>,
      // AntD5.5's optionLabelProp provides the compact selected rendering;
      // optionRender/labelRender are unavailable in the installed version.
      selectedLabel: <span className="payroll-scope-selected" title={title}><Markers available={available} hasPayroll={hasPayroll} label={availabilityLabel} presenceLabel={presenceLabel} /><span className="payroll-scope-selected-name">{option.label}</span><Count count={count} selected available={available} /></span>,
    }
  })

  return <Select<string | number, RenderedOption>
    className="app-search-select payroll-scope-select"
    popupClassName="payroll-scope-select-popup"
    aria-label={ariaLabel}
    disabled={disabled}
    value={selected?.value}
    title={selected?.label}
    placeholder={placeholder}
    options={rendered}
    showSearch
    optionFilterProp="plainLabel"
    filterOption={(search, option) => (option?.plainLabel ?? '').toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())}
    optionLabelProp="selectedLabel"
    popupMatchSelectWidth={360}
    virtual={false}
    onChange={next => onChange(next)}
  />
}
