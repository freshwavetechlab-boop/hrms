import { useEffect, useState } from 'react'
import { essFetch } from '../services/essApi'
import type { AdditionalEmployeeInformation } from '../types'

export function ProfileAdditionalFields({ information, values, employeeId, clientId, onChange }: {
  information?: AdditionalEmployeeInformation
  values: { code: string; value: string }[]
  employeeId: number
  clientId: number
  onChange: (code: string, value: string) => void
}) {
  if (!information?.fields.length) return null
  return <section className="profile-form-section"><h4>Additional information</h4><div className="travel-form-grid profile-form-grid">
    {information.fields.map(column => {
      const field = column.field, value = values.find(value => value.code === column.code)?.value || ''
      const choices = field.options.filter(option => option.isActive)
      return <label key={column.code}><span>{field.label}{field.isRequired ? ' *' : ''}</span>
        {field.lookupSourceCode ? <ProfileFieldLookup field={field} employeeId={employeeId} clientId={clientId} value={value} onChange={value => onChange(column.code, value)} /> : choices.length ? <select aria-label={field.label} required={field.isRequired} multiple={field.fieldTypeCode === 'MULTI_SELECT'} value={field.fieldTypeCode === 'MULTI_SELECT' ? value.split(';').map(value => value.trim()).filter(Boolean) : value}
          onChange={event => onChange(column.code, field.fieldTypeCode === 'MULTI_SELECT' ? [...event.target.selectedOptions].map(option => option.value).join('; ') : event.target.value)}>
          {field.fieldTypeCode !== 'MULTI_SELECT' && <option value="">Select</option>}{choices.map(option => <option key={option.optionCode} value={option.optionCode}>{option.optionLabel}</option>)}
        </select> : field.fieldTypeCode === 'CHECKBOX' ? <input aria-label={field.label} type="checkbox" checked={['true', 'yes', '1'].includes(value.toLowerCase())} onChange={event => onChange(column.code, event.target.checked ? 'TRUE' : 'FALSE')} />
          : field.fieldTypeCode === 'TEXTAREA' ? <textarea aria-label={field.label} required={field.isRequired} value={value} onChange={event => onChange(column.code, event.target.value)} />
          : <input aria-label={field.label} required={field.isRequired} type={({ NUMBER: 'number', DATE: 'date', DATETIME: 'datetime-local', EMAIL: 'email', PHONE: 'tel' } as Record<string, string>)[field.fieldTypeCode] || 'text'} step={field.fieldTypeCode === 'NUMBER' ? 'any' : undefined} value={field.fieldTypeCode === 'DATETIME' ? value.replace(/Z$/, '') : value}
            minLength={field.minimumLength ?? undefined} maxLength={field.maximumLength ?? undefined} min={field.minimumNumber ?? undefined} max={field.maximumNumber ?? undefined} onChange={event => onChange(column.code, event.target.value)} />}
        {field.helpText && <small>{field.helpText}</small>}
      </label>
    })}
  </div></section>
}

function ProfileFieldLookup({ field, employeeId, clientId, value, onChange }: {
  field: AdditionalEmployeeInformation['fields'][number]['field']; employeeId: number; clientId: number; value: string; onChange: (value: string) => void
}) {
  const [search, setSearch] = useState(''), [options, setOptions] = useState<{ value: string; label: string }[]>([]), [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      try {
        const response = await essFetch(`/api/employees/${employeeId}/dynamic-fields/${field.id}/lookup?clientId=${clientId}&search=${encodeURIComponent(search)}`, { signal: controller.signal })
        const data = await response.json()
        if (!response.ok) throw new Error(data.error || 'Unable to load options.')
        setOptions(data); setError('')
      } catch (error) { if (!controller.signal.aborted) setError(error instanceof Error ? error.message : 'Unable to load options.') }
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [employeeId, clientId, field.id, search])
  const selected = value.split(';').map(value => value.trim()).filter(Boolean), multiple = field.fieldTypeCode === 'MULTI_SELECT'
  const choices = [...options, ...selected.filter(value => !options.some(option => option.value === value)).map(value => ({ value, label: value }))]
  return <>
    <input aria-label={`Search ${field.label}`} placeholder="Search options" value={search} onChange={event => setSearch(event.target.value)} />
    <select aria-label={field.label} multiple={multiple} required={field.isRequired} value={multiple ? selected : value} onChange={event => onChange(multiple ? [...event.target.selectedOptions].map(option => option.value).join('; ') : event.target.value)}>
      {!multiple && <option value="">Select</option>}{choices.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>{error && <small role="status">{error}</small>}
  </>
}
