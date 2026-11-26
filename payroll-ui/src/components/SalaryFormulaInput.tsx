import { useId, useMemo, useRef, useState, type SyntheticEvent } from 'react'
import { AutoComplete, Input } from 'antd'
import type { PayrollValue } from '../services/payrollCalculationService'
import { getSalaryFormulaTokenContext, replaceSalaryFormulaToken, replaceSalaryFormulaFunction, salaryFormulaFunctionSuggestions, salaryFormulaSuggestions } from '../utils/salaryFormulaSuggestions'
import { suggestSalaryTemplateFormula } from '../utils/salaryTemplateFormulaValidation'
import './SalaryFormulaInput.css'

type Props = {
  id?: string
  value: string
  onChange: (value: string) => void
  values: PayrollValue[]
  includedComponentIds: string[]
  excludeComponentId?: string
  reviewTokens?: Set<string>
  reviewSyntax?: boolean
  defaultOnClear?: boolean
  label: string
  placeholder?: string
  title?: string
  disabled?: boolean
  multiline?: boolean
  onFocus?: () => void
}

export default function SalaryFormulaInput({ id, value, onChange, values, includedComponentIds, excludeComponentId, reviewTokens, reviewSyntax = true, defaultOnClear = false, label, placeholder = '0', title, disabled = false, multiline = false, onFocus }: Props) {
  const input = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null)
  const edited = useRef(false)
  const feedbackId = useId()
  const inputId = id || feedbackId + '-input'
  const [selection, setSelection] = useState({ start: value.length, end: value.length })
  const [focused, setFocused] = useState(false), [dismissed, setDismissed] = useState(false)
  const context = getSalaryFormulaTokenContext(value, selection.start, selection.end)
  const query = context?.query || ''
  const options = useMemo(() => {
    if (!focused || !query) return []
    const functions = salaryFormulaFunctionSuggestions(query).map(fn => ({
      ...replaceSalaryFormulaFunction(value, fn.name, selection.start, selection.end),
      label: <div className="salary-formula-suggestion"><strong>{fn.signature}</strong><span>{fn.description}</span></div>,
    }))
    const tokens = salaryFormulaSuggestions(values, query, { includedComponentIds, excludeComponentId }).map(item => ({
      ...replaceSalaryFormulaToken(value, item.token, selection.start, selection.end),
      label: <div className="salary-formula-suggestion"><strong>{item.token}</strong><span>{item.meaning}</span></div>,
    }))
    return [...functions, ...tokens.filter(token => !functions.some(fn => fn.value === token.value))]
  }, [focused, query, values, includedComponentIds, excludeComponentId, value, selection])
  const dropdownOpen = focused && !dismissed && options.length > 0
  const hint = !disabled && reviewSyntax && value.trim() ? suggestSalaryTemplateFormula(value, reviewTokens) : undefined
  const suggestion = dropdownOpen && hint?.message.startsWith('Unknown value:') ? undefined : hint
  const captureSelection = (event: SyntheticEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    input.current = event.currentTarget
    setSelection({ start: event.currentTarget.selectionStart ?? value.length, end: event.currentTarget.selectionEnd ?? value.length })
  }
  const change = (next: string) => {
    edited.current = true
    setSelection({ start: input.current?.selectionStart ?? next.length, end: input.current?.selectionEnd ?? next.length })
    setDismissed(false)
    onChange(next)
  }
  const choose = (next: string, option: { caret?: number }) => {
    setDismissed(true)
    const caret = option.caret ?? next.length
    setSelection({ start: caret, end: caret })
    requestAnimationFrame(() => { input.current?.focus(); input.current?.setSelectionRange(caret, caret) })
  }
  const inputProps = {
    'aria-label': label, 'aria-describedby': suggestion ? feedbackId : undefined, 'aria-invalid': suggestion ? true : undefined, placeholder, title, disabled, autoComplete: 'off',
    onFocus: (event: SyntheticEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      if (value === '0') event.currentTarget.select()
      captureSelection(event)
      setFocused(true)
      setDismissed(false)
      onFocus?.()
    },
    onBlur: () => {
      setFocused(false)
      if (edited.current && defaultOnClear && !value.trim()) onChange('0')
      edited.current = false
    }, onSelect: captureSelection, onClick: captureSelection, onKeyUp: captureSelection,
  }
  return <div className="salary-formula-field">
    <div className="salary-formula-box" data-invalid={suggestion ? 'true' : undefined} data-disabled={disabled || undefined}>
      <label className="salary-formula-fx" htmlFor={inputId} aria-hidden="true">ƒx</label>
      <AutoComplete id={inputId} className={`salary-formula-input${multiline ? ' is-multiline' : ''}`} popupClassName="salary-formula-suggestions" value={value} options={options} disabled={disabled} filterOption={false} virtual={false}
        open={dropdownOpen} onChange={change} onSelect={choose} onDropdownVisibleChange={open => { if (!open) setDismissed(true) }}>
        {multiline ? <Input.TextArea {...inputProps} autoSize={{ minRows: 3, maxRows: 6 }} /> : <Input {...inputProps} />}
      </AutoComplete>
    </div>
    {suggestion && <div className="salary-formula-correction">
      <div id={feedbackId} role="status"><span>{suggestion.message}</span>{suggestion.formula && <code>{suggestion.formula}</code>}</div>
      {suggestion.formula && <button type="button" onClick={() => {
        const next = suggestion.formula
        if (!next || disabled) return
        onChange(next)
        choose(next, { caret: next.length })
      }}>Use suggestion</button>}
    </div>}
  </div>
}
