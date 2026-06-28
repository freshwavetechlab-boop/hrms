import { Tabs } from 'antd'

type PageTabsProps<T extends string> = {
  items: readonly T[]
  value: T
  onChange: (value: T) => void
  label?: string
  className?: string
  getLabel?: (value: T) => string
}

export default function PageTabs<T extends string>({ items, value, onChange, label = 'Page tabs', className = '', getLabel = item => item }: PageTabsProps<T>) {
  return <Tabs
    className={`page-tabs-ant ${className}`.trim()}
    aria-label={label}
    activeKey={value}
    onChange={key => onChange(key as T)}
    items={items.map(item => ({ key: item, label: getLabel(item) }))}
  />
}
