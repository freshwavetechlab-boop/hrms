import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

export type Column<T> = {
  key: keyof T | string
  label: string
  render?: (row: T) => ReactNode
  value?: (row: T) => string | number | boolean | null | undefined
  exportValue?: (row: T) => string | number | boolean | null | undefined
  sortable?: boolean
  filterable?: boolean
  width?: string
}

type DataTableProps<T> = {
  rows: T[]
  columns: Column<T>[]
  onEdit?: (row: T) => void
  actions?: (row: T) => ReactNode
  getRowId?: (row: T, index: number) => string | number
  rowClassName?: (row: T) => string
  emptyText?: string
  title?: string
  exportFileName?: string
  pageSizeOptions?: number[]
}

const text = (value: unknown) => value === null || value === undefined ? '' : String(value)
const htmlCell = (value: unknown) => text(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]!))

export default function DataTable<T extends object>(props: DataTableProps<T>) {
  const { rows, columns, onEdit, actions, rowClassName, emptyText = 'No records', title, exportFileName = 'table-export' } = props
  const [query, setQuery] = useState('')
  const [sortKey, setSortKey] = useState(String(columns[0]?.key ?? ''))
  const [desc, setDesc] = useState(false)
  const [filters, setFilters] = useState<Record<string, string>>({})
  const [openFilter, setOpenFilter] = useState('')
  const [filterPos, setFilterPos] = useState({ top: 0, left: 0 })
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(props.pageSizeOptions?.[0] ?? 10)
  const tableRef = useRef<HTMLDivElement>(null), filterRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const close = (event: MouseEvent) => { const target = event.target as Node; if (!tableRef.current?.contains(target) && !filterRef.current?.contains(target)) setOpenFilter('') }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [])

  const valueOf = (row: T, column: Column<T>) => column.value ? column.value(row) : (row as Record<string, unknown>)[String(column.key)]
  const searchable = (row: T) => columns.map(column => text(valueOf(row, column))).join(' ').toLowerCase()
  const selectedFilters = (key: string) => (filters[key] || '').split('\u0001').filter(Boolean)
  const isFiltered = (key: string) => selectedFilters(key).length > 0
  const toggleFilter = (key: string, option: string) => setFilters(current => {
    const selected = new Set(selectedFilters(key))
    selected.has(option) ? selected.delete(option) : selected.add(option)
    return { ...current, [key]: Array.from(selected).join('\u0001') }
  })

  const filterOptions = useMemo(() => {
    const result: Record<string, string[]> = {}
    columns.forEach(column => {
      if (column.filterable === false) return
      const key = String(column.key)
      result[key] = Array.from(new Set(rows.map(row => text(valueOf(row, column))).filter(Boolean))).sort((a, b) => a.localeCompare(b))
    })
    return result
  }, [rows, columns])

  const data = useMemo(() => {
    const activeFilters = Object.entries(filters).filter(([, value]) => value)
    return rows
      .filter(row => !query || searchable(row).includes(query.toLowerCase()))
      .filter(row => activeFilters.every(([key, value]) => {
        const column = columns.find(item => String(item.key) === key)
        return column ? value.split('\u0001').includes(text(valueOf(row, column))) : true
      }))
      .sort((a, b) => {
        const column = columns.find(item => String(item.key) === sortKey)
        if (!column || column.sortable === false) return 0
        return (desc ? -1 : 1) * text(valueOf(a, column)).localeCompare(text(valueOf(b, column)), undefined, { numeric: true, sensitivity: 'base' })
      })
  }, [rows, query, filters, sortKey, desc, columns])

  const totalPages = Math.max(1, Math.ceil(data.length / pageSize))
  const pageSafe = Math.min(page, totalPages)
  const pageRows = data.slice((pageSafe - 1) * pageSize, pageSafe * pageSize)
  const clickSort = (column: Column<T>) => { if (column.sortable === false) return; const key = String(column.key); setDesc(sortKey === key ? !desc : false); setSortKey(key) }
  const clearFilters = () => { setQuery(''); setFilters({}); setPage(1) }
  const exportExcel = () => {
    const header = columns.map(column => `<th>${htmlCell(column.label)}</th>`).join('')
    const body = data.map(row => `<tr>${columns.map(column => `<td>${htmlCell(column.exportValue ? column.exportValue(row) : valueOf(row, column))}</td>`).join('')}</tr>`).join('')
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(new Blob([`<table><thead><tr>${header}</tr></thead><tbody>${body}</tbody></table>`], { type: 'application/vnd.ms-excel' }))
    anchor.download = `${exportFileName}.xls`
    anchor.click()
    URL.revokeObjectURL(anchor.href)
  }

  return <div className="data-table smart-table" ref={tableRef}>
    <div className="smart-table-toolbar">
      <div>{title && <strong>{title}</strong>}<span>{data.length} of {rows.length} rows</span></div>
      <input className="table-filter" placeholder="Search table..." value={query} onChange={event => { setQuery(event.target.value); setPage(1) }} />
      <button type="button" onClick={clearFilters} disabled={!query && !Object.values(filters).some(Boolean)}>Clear</button>
      <button type="button" onClick={exportExcel} disabled={!data.length}>Export</button>
    </div>
    <div className="smart-table-scroll">
      <table>
        <thead><tr>{columns.map(column => {
          const key = String(column.key)
          return <th style={{ width: column.width }} key={key}><span className="table-head-cell" onClick={() => clickSort(column)}>{column.label}{column.sortable !== false && <b>{sortKey === key ? (desc ? '↓' : '↑') : '↕'}</b>}</span>{column.filterable !== false && <button className={`column-filter ${isFiltered(key) ? 'active' : ''}`} type="button" aria-label={`Filter ${column.label}`} onClick={event => { event.stopPropagation(); const box = event.currentTarget.getBoundingClientRect(); setFilterPos({ top: box.bottom + 8, left: Math.min(box.left, window.innerWidth - 270) }); setOpenFilter(openFilter === key ? '' : key) }}><span /></button>}{openFilter === key && createPortal(<div className="column-filter-menu" ref={filterRef} style={{ top: filterPos.top, left: filterPos.left }}><div className="filter-menu-head"><strong>{column.label}</strong><button type="button" onClick={() => { setFilters(current => ({ ...current, [key]: '' })); setPage(1); setOpenFilter('') }}>Clear</button></div>{(filterOptions[key] ?? []).map(option => <label className={selectedFilters(key).includes(option) ? 'active' : ''} key={option}><input type="checkbox" checked={selectedFilters(key).includes(option)} onChange={() => { toggleFilter(key, option); setPage(1) }} /><span>{option}</span></label>)}</div>, document.body)}</th>
        })}{(onEdit || actions) && <th>Actions</th>}</tr></thead>
        <tbody>{pageRows.map((row, index) => <tr key={props.getRowId ? props.getRowId(row, index) : text((row as Record<string, unknown>).id ?? index)} className={rowClassName?.(row) ?? ''}>{columns.map(column => <td key={String(column.key)}>{column.render ? column.render(row) : text(valueOf(row, column))}</td>)}{(onEdit || actions) && <td>{actions ? actions(row) : <button type="button" onClick={() => onEdit?.(row)}>Edit</button>}</td>}</tr>)}</tbody>
      </table>
    </div>
    <div className="smart-table-pager"><span>Page {pageSafe} of {totalPages}</span><select value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}>{(props.pageSizeOptions ?? [10, 25, 50, 100]).map(size => <option key={size} value={size}>{size} / page</option>)}</select><button type="button" disabled={pageSafe <= 1} onClick={() => setPage(value => Math.max(1, value - 1))}>Prev</button><button type="button" disabled={pageSafe >= totalPages} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next</button></div>
    {!data.length && <p className="empty">{emptyText}</p>}
  </div>
}
