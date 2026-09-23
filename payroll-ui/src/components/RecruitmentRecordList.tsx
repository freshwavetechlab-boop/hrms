import { useState, type ReactNode } from 'react'
import { Avatar, Badge, Button, Checkbox, Drawer, Empty, Input, Pagination, Segmented, Select, Spin, Tooltip } from 'antd'
import { useRecruitmentPreference, useRecruitmentView, type RecruitmentView } from '../hooks/useRecruitmentPreferences'
import { SearchOutlined } from '@ant-design/icons'
import type { TableRowSelection } from 'antd/es/table/interface'
import DataTable, { type Column } from './DataTable'
import './RecruitmentTalentWorkspace.css'

export function RecruitmentQuickFilters<K extends string>({ metrics, value, onChange, label }: {
  metrics: { key: K; label: string; count: number; tone: string }[]
  value: K; onChange: (value: K) => void; label: string
}) {
  const view = useRecruitmentView()
  if (view === 'Table') return null
  return <div className="candidate-status-strip recruitment-record-status" role="group" aria-label={label}>
    {metrics.map(metric => <button key={metric.key} type="button" aria-pressed={value === metric.key} className={value === metric.key ? 'is-active' : ''} onClick={() => onChange(metric.key)}>
      <b className={`tone-${metric.tone}`}>{metric.count}</b><span>{metric.label}</span>
    </button>)}
  </div>
}

export function RecruitmentFilterPanel({ count, children, onReset }: { count: number; children: ReactNode; onReset: () => void }) {
  const view = useRecruitmentView()
  const [open, setOpen] = useState(false)
  if (view === 'Table') return null
  const panel = <aside className="candidate-filter-panel"><header><strong>Filters</strong><Badge count={count} showZero color="#5b4ce6" /></header>{children}<Button onClick={onReset}>Reset filters</Button></aside>
  return <><div className="recruitment-desktop-filters">{panel}</div><Button className="recruitment-mobile-filters" onClick={() => setOpen(true)}>Filters ({count})</Button><Drawer title="Filters" width="min(360px, 94vw)" open={open} onClose={() => setOpen(false)} footer={<Button type="primary" onClick={() => setOpen(false)}>Show results</Button>}>{panel}</Drawer></>
}

type RecordFilter<T> = { key: string; label: string; value: (row: T) => string; options?: string[]; selectedValue?: string; onChange?: (value: string) => void }
type QuickFilter<T> = { key: string; label: string; tone: string; matches: (row: T) => boolean }

// The same rows, column renderers and action handlers drive both views.
export default function RecruitmentRecordList<T extends { id: number }>({ rows, columns, title, subtitle, filters, quickFilters = [], actions, selection, rowSelection, selectionGroup, exportFileName, emptyText = 'No records match these filters.', view: requestedView, searchValue, onSearchChange, remoteSearch = false, loading = false, actionsWidth, searchPlaceholder = 'Search by name, job or reference', hiddenCardColumns = [], cardSummaryColumns }: {
  rows: T[]; columns: Column<T>[]; title: (row: T) => string; subtitle?: (row: T) => string
  filters: RecordFilter<T>[]; quickFilters?: QuickFilter<T>[]; actions?: (row: T) => ReactNode
  selection?: (row: T) => ReactNode; rowSelection?: TableRowSelection<T>; exportFileName?: string; emptyText?: string
  selectionGroup?: (row: T) => string | number
  view?: RecruitmentView; searchValue?: string; onSearchChange?: (value: string) => void; remoteSearch?: boolean; loading?: boolean; actionsWidth?: number; searchPlaceholder?: string
  hiddenCardColumns?: string[]
  cardSummaryColumns?: string[]
}) {
  const prefix = exportFileName || 'records'
  const [savedSearch, setSavedSearch] = useRecruitmentPreference(prefix + ':search', '')
  const search = searchValue ?? savedSearch
  const setSearch = onSearchChange ?? setSavedSearch
  const [quick, setQuick] = useRecruitmentPreference(prefix + ':quick', 'all')
  const [savedValues, setValues] = useRecruitmentPreference<Record<string, string>>(prefix + ':filters', {})
  const values = { ...savedValues, ...Object.fromEntries(filters.filter(filter => filter.selectedValue !== undefined).map(filter => [filter.key, filter.selectedValue])) }
  const [localView, setView] = useState<string>('Cards')
  const sharedView = useRecruitmentView()
  const view = requestedView || sharedView || localView
  const [page, setPage] = useRecruitmentPreference(prefix + ':page', 1)
  const groups = [{ key: 'all', label: 'All', tone: 'blue', matches: () => true }, ...quickFilters]
  const metrics = groups.map(group => ({ ...group, count: rows.filter(group.matches).length }))
  const term = remoteSearch ? '' : search.trim().toLowerCase()
  const visible = rows.filter(row => (!term || [title(row), subtitle?.(row), ...columns.map(column => column.value ? column.value(row) : row[column.key as keyof T])].filter(value => typeof value === 'string' || typeof value === 'number').join(' ').toLowerCase().includes(term))
    && (view === 'Table' || filters.every(filter => !values[filter.key] || filter.value(row) === values[filter.key]))
    && (view === 'Table' || (groups.find(group => group.key === quick)?.matches(row) ?? true)))
  const currentPage = Math.min(page, Math.max(1, Math.ceil(visible.length / 10)))
  const selectable = visible.filter(row => !rowSelection?.getCheckboxProps?.(row).disabled)
  const multipleGroups = selectionGroup && new Set(selectable.map(selectionGroup)).size > 1
  const selected = new Set(rowSelection?.selectedRowKeys ?? [])
  const selectedCount = selectable.filter(row => selected.has(row.id)).length
  const selectAll = (checked: boolean) => {
    const next = new Set(selected)
    selectable.forEach(row => checked ? next.add(row.id) : next.delete(row.id))
    rowSelection?.onChange?.([...next], rows.filter(row => next.has(row.id)), { type: 'all' })
  }
  const reset = () => { setSearch(''); setValues({}); filters.forEach(filter => filter.onChange?.('')); setQuick('all'); setPage(1) }
  const cardColumns = columns.filter(column => !['candidateName', 'positionTitle', ...hiddenCardColumns].includes(String(column.key)))
  const summaryColumns = cardSummaryColumns ? cardColumns.filter(column => cardSummaryColumns.includes(String(column.key)))
    : [...cardColumns].sort((a, b) => Number(/status|stage|score/i.test(String(b.key))) - Number(/status|stage|score/i.test(String(a.key)))).slice(0, 4)
  const detailColumns = cardColumns.filter(column => !summaryColumns.includes(column))
  const facts = (row: T, fields: Column<T>[]) => <div className="recruitment-record-facts">{fields.map(column => <div key={String(column.key)}><span>{column.label}</span><div>{column.render ? column.render(row) : String(column.value ? column.value(row) ?? '—' : row[column.key as keyof T] ?? '—')}</div></div>)}</div>
  return <section className="candidate-applications-workspace recruitment-record-workspace">
    {view !== 'Table' && !!quickFilters.length && <RecruitmentQuickFilters metrics={metrics} value={quick} onChange={value => { setQuick(value); setPage(1) }} label="Quick filters" />}
    <div className="candidate-applications-layout">
      <div className="candidate-applications-main">
        <div className="candidate-list-toolbar recruitment-record-toolbar">
          <Input allowClear prefix={<SearchOutlined />} value={search} onChange={event => { setSearch(event.target.value); setPage(1) }} placeholder={searchPlaceholder} aria-label="Search records" />
          <span>Showing <b>{visible.length}</b> of {rows.length}</span>
          {!!selected.size && <Button size="small" onClick={() => rowSelection?.onChange?.([], [], { type: 'none' })}>Clear selection</Button>}
          {rowSelection && <Tooltip title={multipleGroups ? 'Filter to one job or select one candidate first.' : 'Select all matching candidates for this job'}><Checkbox disabled={!selectable.length || Boolean(multipleGroups)} checked={!!selectable.length && selectedCount === selectable.length} indeterminate={selectedCount > 0 && selectedCount < selectable.length} onChange={event => selectAll(event.target.checked)}>Select all</Checkbox></Tooltip>}
          {!sharedView && !requestedView && <Segmented options={['Cards', 'Table']} value={view} onChange={value => setView(String(value))} />}
        </div>
        {view === 'Table' ? <DataTable rows={visible} columns={columns} actions={actions} rowSelection={rowSelection} exportFileName={exportFileName} emptyText={emptyText} hideSearch fillHeight loading={loading} actionsWidth={actionsWidth} /> : <>
          <div className="candidate-application-list">
            {visible.slice((currentPage - 1) * 10, currentPage * 10).map(row => <article className="candidate-application-card recruitment-record-card" key={row.id}>
              <Avatar size={38}>{title(row).split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase()}</Avatar>
              <div className="candidate-application-copy"><div className="candidate-card-title"><h3>{title(row)}</h3></div>{subtitle && <p>{subtitle(row)}</p>}
                {facts(row, summaryColumns)}
                {!!detailColumns.length && <details className="recruitment-record-details"><summary>More details</summary>{facts(row, detailColumns)}</details>}
              </div>
              {selection && <div className="recruitment-record-selection">{selection(row)}</div>}
              {actions && <footer className="recruitment-record-actions">{actions(row)}</footer>}
            </article>)}
            {loading ? <Spin /> : !visible.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyText} />}
          </div>
          {visible.length > 10 && <Pagination className="recruitment-record-pagination" current={currentPage} pageSize={10} total={visible.length} showSizeChanger={false} onChange={setPage} />}
        </>}
      </div>
      {view !== 'Table' && <RecruitmentFilterPanel count={Object.values(values).filter(Boolean).length} onReset={reset}>
        {filters.map(filter => <label key={filter.key}><span>{filter.label}</span><Select allowClear showSearch optionFilterProp="label" aria-label={filter.label} placeholder={`All ${filter.label.toLowerCase()}`} value={values[filter.key] || undefined}
          options={(filter.options ?? [...new Set(rows.map(filter.value).filter(Boolean))].sort()).map(value => ({ value, label: value }))}
          onChange={value => { if (filter.onChange) filter.onChange(value || ''); else setValues(current => ({ ...current, [filter.key]: value || '' })); setPage(1) }} /></label>)}
      </RecruitmentFilterPanel>}
    </div>
  </section>
}
