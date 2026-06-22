import { useMemo, useState, type ReactNode } from 'react'

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
}

const text = (value: unknown) => value === null || value === undefined ? '' : String(value)
const csvCell = (value: unknown) => JSON.stringify(text(value))

export default function DataTable<T extends object>(props: DataTableProps<T>) {
  const { rows, columns, onEdit, actions, rowClassName, emptyText = 'No records', title, exportFileName = 'table-export' } = props
  const [query, setQuery] = useState('')
  const [sortKey, setSortKey] = useState<string>(String(columns[0]?.key ?? ''))
  const [desc, setDesc] = useState(false)
  const [showFilters, setShowFilters] = useState(false)
  const [filters, setFilters] = useState<Record<string, string>>({})

  const valueOf = (row: T, column: Column<T>) => column.value ? column.value(row) : (row as Record<string, unknown>)[String(column.key)]
  const searchable = (row: T) => columns.map((column) => text(valueOf(row, column))).join(' ').toLowerCase()

  const filterOptions = useMemo(() => {
    const result: Record<string, string[]> = {}
    columns.forEach((column) => {
      if (column.filterable === false) return
      const key = String(column.key)
      result[key] = Array.from(new Set(rows.map((row) => text(valueOf(row, column))).filter(Boolean))).sort((a, b) => a.localeCompare(b))
    })
    return result
  }, [rows, columns])

  const data = useMemo(() => {
    const activeFilters = Object.entries(filters).filter(([, value]) => value)
    return rows
      .filter((row) => !query || searchable(row).includes(query.toLowerCase()))
      .filter((row) => activeFilters.every(([key, value]) => {
        const column = columns.find((item) => String(item.key) === key)
        return column ? text(valueOf(row, column)) === value : true
      }))
      .sort((a, b) => {
        const column = columns.find((item) => String(item.key) === sortKey)
        if (!column || column.sortable === false) return 0
        const left = text(valueOf(a, column))
        const right = text(valueOf(b, column))
        return (desc ? -1 : 1) * left.localeCompare(right, undefined, { numeric: true, sensitivity: 'base' })
      })
  }, [rows, query, filters, sortKey, desc, columns])

  const clickSort = (column: Column<T>) => {
    if (column.sortable === false) return
    const key = String(column.key)
    setDesc(sortKey === key ? !desc : false)
    setSortKey(key)
  }

  const exportCsv = () => {
    const headers = columns.map((column) => column.label)
    const body = data.map((row) => columns.map((column) => csvCell(column.exportValue ? column.exportValue(row) : valueOf(row, column))).join(','))
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(new Blob([[headers.join(','), ...body].join('\n')], { type: 'text/csv' }))
    anchor.download = `${exportFileName}.csv`
    anchor.click()
    URL.revokeObjectURL(anchor.href)
  }

  const clearFilters = () => {
    setQuery('')
    setFilters({})
  }

  return (
    <div className="data-table smart-table">
      <div className="smart-table-toolbar">
        <div>
          {title && <strong>{title}</strong>}
          <span>{data.length} of {rows.length} rows</span>
        </div>
        <input className="table-filter" placeholder="Search table..." value={query} onChange={(event) => setQuery(event.target.value)} />
        <button type="button" onClick={() => setShowFilters((value) => !value)}>Filters</button>
        <button type="button" onClick={clearFilters} disabled={!query && !Object.values(filters).some(Boolean)}>Clear</button>
        <button type="button" onClick={exportCsv} disabled={!data.length}>Export CSV</button>
      </div>

      {showFilters && (
        <div className="smart-table-filters">
          {columns.filter((column) => column.filterable !== false).map((column) => {
            const key = String(column.key)
            return (
              <label key={key}>
                <span>{column.label}</span>
                <select value={filters[key] ?? ''} onChange={(event) => setFilters((current) => ({ ...current, [key]: event.target.value }))}>
                  <option value="">All</option>
                  {(filterOptions[key] ?? []).map((option) => <option value={option} key={option}>{option}</option>)}
                </select>
              </label>
            )
          })}
        </div>
      )}

      <div className="smart-table-scroll">
        <table>
          <thead>
            <tr>
              {columns.map((column) => {
                const key = String(column.key)
                return (
                  <th style={{ width: column.width }} onClick={() => clickSort(column)} key={key}>
                    {column.label}
                    {sortKey === key && column.sortable !== false ? <b>{desc ? 'down' : 'up'}</b> : null}
                  </th>
                )
              })}
              {(onEdit || actions) && <th>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {data.map((row, index) => (
              <tr key={props.getRowId ? props.getRowId(row, index) : text((row as Record<string, unknown>).id ?? index)} className={rowClassName?.(row) ?? ''}>
                {columns.map((column) => <td key={String(column.key)}>{column.render ? column.render(row) : text(valueOf(row, column))}</td>)}
                {(onEdit || actions) && <td>{actions ? actions(row) : <button type="button" onClick={() => onEdit?.(row)}>Edit</button>}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!data.length && <p className="empty">{emptyText}</p>}
    </div>
  )
}
