type PageTabsProps<T extends string> = {
  items: readonly T[]
  value: T
  onChange: (value: T) => void
  label?: string
  className?: string
  getLabel?: (value: T) => string
}

export default function PageTabs<T extends string>({ items, value, onChange, label = 'Page tabs', className = '', getLabel = item => item }: PageTabsProps<T>) {
  return <div className={`page-tabs ${className}`.trim()} role="tablist" aria-label={label}>{items.map(item => <button type="button" role="tab" aria-selected={value === item} className={value === item ? 'active' : ''} onClick={() => onChange(item)} key={item}>{getLabel(item)}</button>)}</div>
}
