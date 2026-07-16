import { Select } from 'antd'

export type SearchOption = { value: string | number; label: string }

export default function SearchSelect({ value, options, onChange, placeholder = 'Select', disabled = false }: { value: string | number; options: SearchOption[]; onChange: (value: string) => void; placeholder?: string; disabled?: boolean }) {
  const normalizedOptions = options.map(item => ({ value: String(item.value), label: item.label }))
  const selectedValue = resolveSelectedValue(value, normalizedOptions)
  return <Select
    className="app-search-select"
    popupClassName="app-search-select-dropdown"
    showSearch
      allowClear={false}
    disabled={disabled}
    value={selectedValue}
    placeholder={placeholder}
    optionFilterProp="label"
    filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
    onChange={next => onChange(String(next))}
    options={normalizedOptions}
  />
}

export const selectOptions = (items: Array<string | number | SearchOption>, emptyLabel?: string, emptyValue: string | number = '') => [
  ...(emptyLabel ? [{ value: emptyValue, label: emptyLabel }] : []),
  ...items.map(item => typeof item === 'object' ? item : { value: item, label: String(item) })
]

function resolveSelectedValue(value: string | number, options: SearchOption[]) {
  const raw = String(value ?? '')
  if (options.some(option => option.value === raw)) return raw
  if (!raw || raw === '0') return undefined
  const token = raw.match(/^([^:]+):(.+)$/)?.[1] || raw.match(/^(\d+)[.\-]\s*(.+)$/)?.[1]
  return token && options.some(option => option.value === token) ? token : raw
}
