import { useState, type ReactNode } from 'react'
import { Avatar, Badge, Button, Empty, Input, Pagination, Segmented, Select } from 'antd'
import { SearchOutlined } from '@ant-design/icons'
import type { TableRowSelection } from 'antd/es/table/interface'
import DataTable, { type Column } from './DataTable'
import './RecruitmentTalentWorkspace.css'

export function RecruitmentQuickFilters<K extends string>({ metrics, value, onChange, label }: {
  metrics: { key: K; label: string; count: number; tone: string }[]
  value: K; onChange: (value: K) => void; label: string
}) {
  return <div className="candidate-status-strip recruitment-record-status" role="group" aria-label={label}>
    {metrics.map(metric => <button key={metric.key} type="button" aria-pressed={value === metric.key} className={value === metric.key ? 'is-active' : ''} onClick={() => onChange(metric.key)}>
      <b className={`tone-${metric.tone}`}>{metric.count}</b><span>{metric.label}</span>
    </button>)}
  </div>
}

export function RecruitmentFilterPanel({ count, children, onReset }: { count: number; children: ReactNode; onReset: () => void }) {
  return <aside className="candidate-filter-panel"><header><strong>Filters</strong><Badge count={count} showZero color="#5b4ce6" /></header>{children}<Button onClick={onReset}>Reset filters</Button></aside>
}

type RecordFilter<T> = { key: string; label: string; value: (row: T) => string }
type QuickFilter<T> = { key: string; label: string; tone: string; matches: (row: T) => boolean }

// The same rows, column renderers and action handlers drive both views.
export default function RecruitmentRecordList<T extends { id: number }>({ rows, columns, title, subtitle, filters, quickFilters = [], actions, selection, rowSelection, exportFileName, emptyText = 'No records match these filters.' }: {
  rows: T[]; columns: Column<T>[]; title: (row: T) => string; subtitle?: (row: T) => string
  filters: RecordFilter<T>[]; quickFilters?: QuickFilter<T>[]; actions?: (row: T) => ReactNode
  selection?: (row: T) => ReactNode; rowSelection?: TableRowSelection<T>; exportFileName?: string; emptyText?: string
}) {
  const [search, setSearch] = useState('')
  const [quick, setQuick] = useState('all')
  const [values, setValues] = useState<Record<string, string>>({})
  const [view, setView] = useState<string>('Cards')
  const [page, setPage] = useState(1)
  const groups = [{ key: 'all', label: 'All', tone: 'blue', matches: () => true }, ...quickFilters]
  const metrics = groups.map(group => ({ ...group, count: rows.filter(group.matches).length }))
  const term = search.trim().toLowerCase()
  const visible = rows.filter(row => (!term || [title(row), subtitle?.(row), ...columns.map(column => column.value ? column.value(row) : row[column.key as keyof T])].filter(value => typeof value === 'string' || typeof value === 'number').join(' ').toLowerCase().includes(term))
    && filters.every(filter => !values[filter.key] || filter.value(row) === values[filter.key])
    && (groups.find(group => group.key === quick)?.matches(row) ?? true))
  const currentPage = Math.min(page, Math.max(1, Math.ceil(visible.length / 10)))
  const reset = () => { setSearch(''); setValues({}); setQuick('all'); setPage(1) }
  return <section className="candidate-applications-workspace recruitment-record-workspace">
    <RecruitmentQuickFilters metrics={metrics} value={quick} onChange={value => { setQuick(value); setPage(1) }} label="Quick filters" />
    <div className="candidate-applications-layout">
      <div className="candidate-applications-main">
        <div className="candidate-list-toolbar recruitment-record-toolbar">
          <Input allowClear prefix={<SearchOutlined />} value={search} onChange={event => { setSearch(event.target.value); setPage(1) }} placeholder="Search by name, job or reference" aria-label="Search records" />
          <span>Showing <b>{visible.length}</b> of {rows.length}</span>
          <Segmented options={['Cards', 'Table']} value={view} onChange={value => setView(String(value))} />
        </div>
        {view === 'Table' ? <DataTable rows={visible} columns={columns} actions={actions} rowSelection={rowSelection} exportFileName={exportFileName} emptyText={emptyText} hideSearch /> : <>
          <div className="candidate-application-list">
            {visible.slice((currentPage - 1) * 10, currentPage * 10).map(row => <article className="candidate-application-card recruitment-record-card" key={row.id}>
              <Avatar size={46}>{title(row).split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase()}</Avatar>
              <div className="candidate-application-copy"><div className="candidate-card-title"><h3>{title(row)}</h3></div>{subtitle && <p>{subtitle(row)}</p>}
                <div className="recruitment-record-facts">{columns.map(column => <div key={String(column.key)}><span>{column.label}</span><div>{column.render ? column.render(row) : String(column.value ? column.value(row) ?? '—' : row[column.key as keyof T] ?? '—')}</div></div>)}</div>
              </div>
              {selection && <div className="recruitment-record-selection">{selection(row)}</div>}
              {actions && <footer className="recruitment-record-actions">{actions(row)}</footer>}
            </article>)}
            {!visible.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyText} />}
          </div>
          {visible.length > 10 && <Pagination className="recruitment-record-pagination" current={currentPage} pageSize={10} total={visible.length} showSizeChanger={false} onChange={setPage} />}
        </>}
      </div>
      <RecruitmentFilterPanel count={Object.values(values).filter(Boolean).length} onReset={reset}>
        {filters.map(filter => <label key={filter.key}><span>{filter.label}</span><Select allowClear showSearch optionFilterProp="label" aria-label={filter.label} placeholder={`All ${filter.label.toLowerCase()}`} value={values[filter.key] || undefined}
          options={[...new Set(rows.map(filter.value).filter(Boolean))].sort().map(value => ({ value, label: value }))}
          onChange={value => { setValues(current => ({ ...current, [filter.key]: value || '' })); setPage(1) }} /></label>)}
      </RecruitmentFilterPanel>
    </div>
  </section>
}
