import { useMemo, useRef, useState, type SyntheticEvent } from 'react'
import { AutoComplete, Input } from 'antd'
import type { PayrollValue } from '../services/payrollCalculationService'
import { getSalaryFormulaTokenContext, replaceSalaryFormulaToken, salaryFormulaSuggestions } from '../utils/salaryFormulaSuggestions'
import './SalaryFormulaInput.css'

type Props = {
  value: string
  onChange: (value: string) => void
  values: PayrollValue[]
  includedComponentIds: string[]
  excludeComponentId?: string
  label: string
  placeholder?: string
  title?: string
  disabled?: boolean
  multiline?: boolean
  onFocus?: () => void
}

export default function SalaryFormulaInput({ value, onChange, values, includedComponentIds, excludeComponentId, label, placeholder, title, disabled = false, multiline = false, onFocus }: Props) {
  const input = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null)
  const [selection, setSelection] = useState({ start: value.length, end: value.length })
  const [focused, setFocused] = useState(false), [dismissed, setDismissed] = useState(false)
  const context = getSalaryFormulaTokenContext(value, selection.start, selection.end)
  const options = useMemo(() => salaryFormulaSuggestions(values, context?.query || '', { includedComponentIds, excludeComponentId })
    .map(item => {
      const replacement = replaceSalaryFormulaToken(value, item.token, selection.start, selection.end)
      return { value: replacement.value, caret: replacement.caret, label: <div className="salary-formula-suggestion"><strong>{item.token}</strong><span>{item.meaning}</span></div> }
    }), [values, context?.query, includedComponentIds, excludeComponentId, value, selection])
  const captureSelection = (event: SyntheticEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    input.current = event.currentTarget
    setSelection({ start: event.currentTarget.selectionStart ?? value.length, end: event.currentTarget.selectionEnd ?? value.length })
  }
  const change = (next: string) => {
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
    'aria-label': label, placeholder, title, disabled, autoComplete: 'off',
    onFocus: (event: SyntheticEvent<HTMLInputElement | HTMLTextAreaElement>) => { captureSelection(event); setFocused(true); setDismissed(false); onFocus?.() },
    onBlur: () => setFocused(false), onSelect: captureSelection, onClick: captureSelection, onKeyUp: captureSelection,
  }
  return <AutoComplete className={`salary-formula-input${multiline ? ' is-multiline' : ''}`} popupClassName="salary-formula-suggestions" value={value} options={options} disabled={disabled} filterOption={false} virtual={false}
    open={focused && !dismissed && !!context?.query && options.length > 0} onChange={change} onSelect={choose} onDropdownVisibleChange={open => { if (!open) setDismissed(true) }}>
    {multiline ? <Input.TextArea {...inputProps} autoSize={{ minRows: 3, maxRows: 6 }} /> : <Input {...inputProps} />}
  </AutoComplete>
}
