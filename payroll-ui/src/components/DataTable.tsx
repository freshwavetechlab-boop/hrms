import { useCallback, useEffect, useMemo, useState, type Key, type ReactNode } from 'react'
import { Button, Input, Space, Table } from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'

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
  const [tableKey, setTableKey] = useState(0)
  const [dirtyTable, setDirtyTable] = useState(false)
  const [exportRows, setExportRows] = useState<T[]>(rows)
  const pageSizeOptions = props.pageSizeOptions ?? [10, 25, 50, 100]
  const tableScrollX = Math.max(720, columns.length * 150 + (actions || onEdit ? 170 : 0))
  const valueOf = useCallback((row: T, column: Column<T>) => column.value ? column.value(row) : (row as Record<string, unknown>)[String(column.key)], [])
  const searchable = useCallback((row: T) => columns.map(column => text(valueOf(row, column))).join(' ').toLowerCase(), [columns, valueOf])

  const data = useMemo(() => rows.filter(row => !query || searchable(row).includes(query.toLowerCase())), [rows, query, searchable])
  useEffect(() => setExportRows(data), [data])
  const antColumns = useMemo<ColumnsType<T>>(() => {
    const mapped = columns.map(column => {
      const key = String(column.key)
      const filters = column.filterable === false ? undefined : Array.from(new Set(rows.map(row => text(valueOf(row, column))).filter(Boolean))).sort((a, b) => a.localeCompare(b)).map(value => ({ text: value, value }))
      return {
        key,
        title: column.label,
        width: column.width ?? 150,
        ellipsis: true,
        sorter: column.sortable === false ? undefined : (a: T, b: T) => text(valueOf(a, column)).localeCompare(text(valueOf(b, column)), undefined, { numeric: true, sensitivity: 'base' }),
        filters,
        filterSearch: true,
        onFilter: column.filterable === false ? undefined : (value: boolean | Key, row: T) => text(valueOf(row, column)) === String(value),
        render: (_: unknown, row: T) => column.render ? column.render(row) : text(valueOf(row, column))
      }
    })
    return actions || onEdit ? [...mapped, { key: '__actions', title: 'Actions', fixed: 'right' as const, width: 170, render: (_: unknown, row: T) => <div className="ant-table-row-actions">{actions ? actions(row) : <Button size="small" onClick={() => onEdit?.(row)}>Edit</Button>}</div> }] : mapped
  }, [columns, rows, actions, onEdit, valueOf])

  const pagination: TablePaginationConfig = { defaultPageSize: pageSizeOptions[0], pageSizeOptions: pageSizeOptions.map(String), showSizeChanger: true, showTotal: (total, range) => `${range[0]}-${range[1]} of ${total}` }
  const clear = () => { setQuery(''); setDirtyTable(false); setTableKey(value => value + 1) }
  const exportExcel = () => {
    const header = columns.map(column => `<th>${htmlCell(column.label)}</th>`).join('')
    const body = exportRows.map(row => `<tr>${columns.map(column => `<td>${htmlCell(column.exportValue ? column.exportValue(row) : valueOf(row, column))}</td>`).join('')}</tr>`).join('')
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(new Blob([`<table><thead><tr>${header}</tr></thead><tbody>${body}</tbody></table>`], { type: 'application/vnd.ms-excel' }))
    anchor.download = `${exportFileName}.xls`
    anchor.click()
    URL.revokeObjectURL(anchor.href)
  }

  return <div className="ant-smart-table">
    <div className="ant-table-toolbar">
      <div className="ant-table-summary">{title && <strong>{title}</strong>}<span>{data.length} of {rows.length} rows</span></div>
      <Space className="ant-table-actions" wrap>
        <Input allowClear className="table-filter" placeholder="Search table..." value={query} onChange={event => setQuery(event.target.value)} />
        <Button onClick={clear} disabled={!query && !dirtyTable}>Clear</Button>
        <Button className="excel-export-btn" onClick={exportExcel} disabled={!exportRows.length}>Export Excel</Button>
      </Space>
    </div>
    <Table<T>
      key={tableKey}
      size="middle"
      className="zoho-ant-table"
      columns={antColumns}
      dataSource={data}
      rowKey={(row, index) => String(props.getRowId ? props.getRowId(row, index ?? 0) : (row as Record<string, unknown>).id ?? index)}
      rowClassName={row => rowClassName?.(row) ?? ''}
      locale={{ emptyText }}
      pagination={pagination}
      tableLayout="fixed"
      scroll={{ x: tableScrollX }}
      onChange={(_, __, ___, extra) => { setDirtyTable(true); setExportRows(extra.currentDataSource as T[]) }}
    />
  </div>
}
