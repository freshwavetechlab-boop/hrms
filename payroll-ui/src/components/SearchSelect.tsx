import { Select } from 'antd'

export type SearchOption = { value: string | number; label: string }

export default function SearchSelect({ value, options, onChange, placeholder = 'Select', disabled = false }: { value: string | number; options: SearchOption[]; onChange: (value: string) => void; placeholder?: string; disabled?: boolean }) {
  return <Select
    className="app-search-select"
    popupClassName="app-search-select-dropdown"
    showSearch
    allowClear={false}
    disabled={disabled}
    value={String(value)}
    placeholder={placeholder}
    optionFilterProp="label"
    filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
    onChange={next => onChange(String(next))}
    options={options.map(item => ({ value: String(item.value), label: item.label }))}
  />
}

export const selectOptions = (items: Array<string | number | SearchOption>, emptyLabel?: string, emptyValue: string | number = '') => [
  ...(emptyLabel ? [{ value: emptyValue, label: emptyLabel }] : []),
  ...items.map(item => typeof item === 'object' ? item : { value: item, label: String(item) })
]
