import { Children, isValidElement, useCallback, useMemo, useState, type Key, type ReactNode } from 'react'
import { DownOutlined, FileExcelOutlined, FileOutlined, FilePdfOutlined, FileTextOutlined, FileWordOutlined } from '@ant-design/icons'
import { Button, Dropdown, Empty, Input, Pagination, Table } from 'antd'
import type { ColumnsType, TablePaginationConfig, TableRowSelection } from 'antd/es/table/interface'
import type { MenuProps } from 'antd'
import { jsPDF } from 'jspdf'
import autoTable from 'jspdf-autotable'
import { downloadXlsx } from '../utils/xlsx'

export type Column<T> = {
  key: keyof T | string
  label: string
  render?: (row: T) => ReactNode
  value?: (row: T) => string | number | boolean | null | undefined
  exportValue?: (row: T) => string | number | boolean | null | undefined
  sortable?: boolean
  filterable?: boolean
  width?: string | number
  wrap?: boolean
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
  exportToolbar?: ReactNode
  primaryAction?: ReactNode
  onExcelExport?: (visibleRows: T[]) => void
  exportDisabled?: boolean
  hideInactiveClear?: boolean
  hideSearch?: boolean
  actionsWidth?: number
  pageSizeOptions?: number[]
  rowSelection?: TableRowSelection<T>
  view?: 'Cards' | 'Table'
  loading?: boolean
  scrollY?: number | string
  fillHeight?: boolean
}

const text = (value: unknown) => value === null || value === undefined ? '' : String(value)
const renderedText = (node: ReactNode): string => Children.toArray(node).map(child =>
  isValidElement<{ children?: ReactNode }>(child) ? renderedText(child.props.children) : typeof child === 'string' || typeof child === 'number' ? String(child) : '').join(' ')
const htmlCell = (value: unknown) => text(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]!))
const csvCell = (value: unknown) => {
  const cell = text(value)
  return /[",\r\n]/.test(cell) ? `"${cell.replace(/"/g, '""')}"` : cell
}
const textCell = (value: unknown) => text(value).replace(/[\t\r\n]+/g, ' ')
type ExportFormat = 'excel' | 'csv' | 'txt' | 'pdf' | 'word'
const exportLabels: Record<ExportFormat, string> = { excel: 'Excel', csv: 'CSV', txt: 'TXT', pdf: 'PDF', word: 'Word' }
const exportIcons: Record<ExportFormat, ReactNode> = {
  excel: <FileExcelOutlined style={{ color: '#15803d' }} />,
  csv: <FileTextOutlined style={{ color: '#c2410c' }} />,
  txt: <FileOutlined style={{ color: '#0284c7' }} />,
  pdf: <FilePdfOutlined style={{ color: '#dc2626' }} />,
  word: <FileWordOutlined style={{ color: '#2563eb' }} />
}

const columnWidthInPixels = (width: string | number | undefined, fallback = 150) => {
  if (typeof width === 'number') return Number.isFinite(width) && width > 0 ? width : fallback
  if (!width) return fallback
  const normalized = width.trim().toLowerCase()
  const parsed = Number.parseFloat(normalized)
  if (!Number.isFinite(parsed) || parsed <= 0) return fallback
  if (normalized.endsWith('rem') || normalized.endsWith('em')) return parsed * 16
  if (normalized.endsWith('px') || /^\d+(?:\.\d+)?$/.test(normalized)) return parsed
  return fallback
}

const defaultColumnWidth = <T extends object>(column: Column<T>) => {
  const words = `${String(column.key).replace(/([a-z])([A-Z])/g, '$1 $2')} ${column.label}`.toLowerCase()
  if (/\b(address|description|details?|email|message|notes?|path|purpose|reason|remarks?|summary|url)\b/.test(words)) return 240
  if (/\b(active|amount|code|commission|count|date|duration|enabled|fy|gender|gst|id|lock|month|number|order|percent|percentage|priority|proof|rate|status|time|type|year)\b/.test(words)) return 128
  if (/\b(action|candidate|client|consultant|department|designation|employee|location|name|position|role|schedule|template|vendor|work order)\b/.test(words)) return 190
  return 150
}

const resolvedColumnWidth = <T extends object>(column: Column<T>) => columnWidthInPixels(column.width, defaultColumnWidth(column))

export default function DataTable<T extends object>(props: DataTableProps<T>) {
  const { rows, columns, onEdit, actions, rowClassName, emptyText = 'No records', title, exportFileName = 'table-export' } = props
  const [query, setQuery] = useState('')
  const [tableKey, setTableKey] = useState(0)
  const [dirtyTable, setDirtyTable] = useState(false)
  const [visibleTableState, setVisibleTableState] = useState<{ source: T[]; rows: T[] } | null>(null)
  const [exportFormat, setExportFormat] = useState<ExportFormat>('excel')
  const [cardPage, setCardPage] = useState(1)
  const pageSizeOptions = props.pageSizeOptions ?? [10, 25, 50, 100]
  const actionsWidth = props.actionsWidth ?? 170
  const selectionWidth = props.rowSelection ? columnWidthInPixels(props.rowSelection.columnWidth, 32) : 0
  const tableScrollX = Math.ceil(Math.max(640, columns.reduce((total, column) => total + resolvedColumnWidth(column), selectionWidth) + (actions || onEdit ? actionsWidth : 0)))
  const valueOf = useCallback((row: T, column: Column<T>) => column.value ? column.value(row) : (row as Record<string, unknown>)[String(column.key)], [])
  const searchable = useCallback((row: T) => columns.map(column => text(valueOf(row, column))).join(' ').toLowerCase(), [columns, valueOf])

  const data = useMemo(() => rows.filter(row => !query || searchable(row).includes(query.toLowerCase())), [rows, query, searchable])
  const exportRows = props.view !== 'Cards' && visibleTableState?.source === data ? visibleTableState.rows : data
  const antColumns = useMemo<ColumnsType<T>>(() => {
    const mapped = columns.map(column => {
      const key = String(column.key)
      const filters = column.filterable === false ? undefined : Array.from(new Set(rows.map(row => text(valueOf(row, column))).filter(Boolean))).sort((a, b) => a.localeCompare(b)).map(value => ({ text: value, value }))
      return {
        key,
        title: column.label,
        width: resolvedColumnWidth(column),
        ellipsis: !column.wrap,
        sorter: column.sortable === false ? undefined : (a: T, b: T) => text(valueOf(a, column)).localeCompare(text(valueOf(b, column)), undefined, { numeric: true, sensitivity: 'base' }),
        filters,
        filterSearch: true,
        onFilter: column.filterable === false ? undefined : (value: boolean | Key, row: T) => text(valueOf(row, column)) === String(value),
        render: (_: unknown, row: T) => {
          const content = column.render ? column.render(row) : text(valueOf(row, column))
          return column.wrap
            ? <span title={text(valueOf(row, column)) || undefined} style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: 1.35 }}>{content}</span>
            : content
        }
      }
    })
    return actions || onEdit ? [...mapped, { key: '__actions', title: 'Actions', fixed: 'right' as const, width: actionsWidth, render: (_: unknown, row: T) => <div className="ant-table-row-actions">{actions ? actions(row) : <Button size="small" onClick={() => onEdit?.(row)}>Edit</Button>}</div> }] : mapped
  }, [columns, rows, actions, onEdit, valueOf, actionsWidth])

  const pagination: TablePaginationConfig = { defaultPageSize: pageSizeOptions[0], pageSizeOptions: pageSizeOptions.map(String), showSizeChanger: true, showTotal: (total, range) => `${range[0]}-${range[1]} of ${total}` }
  const clear = () => { setQuery(''); setDirtyTable(false); setVisibleTableState(null); setTableKey(value => value + 1) }
  const exportValueOf = (row: T, column: Column<T>) => {
    if (column.exportValue) return column.exportValue(row)
    const value = valueOf(row, column)
    return (value == null || typeof value === 'object') && column.render ? renderedText(column.render(row)) : value
  }
  const downloadExport = (format = exportFormat) => {
    if (format === 'excel' && props.onExcelExport) {
      props.onExcelExport(exportRows)
      return
    }
    if (format === 'excel') {
      downloadXlsx(exportFileName + '.xlsx', [{ name: 'Records', rows: [
        columns.map(column => column.label),
        ...exportRows.map(row => columns.map(column => text(exportValueOf(row, column)))),
      ] }])
      return
    }
    let content: string
    let mimeType: string
    let extension: string
    if (format === 'pdf') {
      const pdf = new jsPDF({ format: 'a4', orientation: columns.length > 6 ? 'landscape' : 'portrait', unit: 'mm' })
      autoTable(pdf, {
        head: [columns.map(column => column.label)],
        body: exportRows.map(row => columns.map(column => text(exportValueOf(row, column)))),
        margin: 10,
        styles: { cellPadding: 2.5, fontSize: 8, overflow: 'linebreak' },
        headStyles: { fillColor: [220, 38, 38], textColor: 255, fontStyle: 'bold' },
        alternateRowStyles: { fillColor: [254, 242, 242] }
      })
      pdf.save(`${exportFileName}.pdf`)
      return
    }
    if (format === 'csv') {
      content = `\uFEFF${columns.map(column => csvCell(column.label)).join(',')}\r\n${exportRows.map(row => columns.map(column => csvCell(exportValueOf(row, column))).join(',')).join('\r\n')}`
      mimeType = 'text/csv;charset=utf-8'
      extension = 'csv'
    } else if (format === 'txt') {
      content = `${columns.map(column => textCell(column.label)).join('\t')}\r\n${exportRows.map(row => columns.map(column => textCell(exportValueOf(row, column))).join('\t')).join('\r\n')}`
      mimeType = 'text/plain;charset=utf-8'
      extension = 'txt'
    } else {
      const header = columns.map(column => `<th>${htmlCell(column.label)}</th>`).join('')
      const body = exportRows.map(row => `<tr>${columns.map(column => `<td>${htmlCell(exportValueOf(row, column))}</td>`).join('')}</tr>`).join('')
      content = `<!doctype html><html><head><meta charset="utf-8"><style>table{border-collapse:collapse;font-family:Arial,sans-serif;font-size:11px}th,td{border:1px solid #cbd5e1;padding:6px;text-align:left}th{background:#eff6ff;color:#1d4ed8}</style></head><body><table><thead><tr>${header}</tr></thead><tbody>${body}</tbody></table></body></html>`
      mimeType = 'application/msword'
      extension = 'doc'
    }
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(new Blob([content], { type: mimeType }))
    anchor.download = `${exportFileName}.${extension}`
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    window.setTimeout(() => URL.revokeObjectURL(anchor.href), 60_000)
  }
  const exportMenu: MenuProps = {
    selectedKeys: [exportFormat],
    items: [
      { key: 'excel', icon: exportIcons.excel, label: 'Excel (.xlsx)' },
      { key: 'csv', icon: exportIcons.csv, label: 'CSV (.csv)' },
      { key: 'txt', icon: exportIcons.txt, label: 'Text (.txt)' },
      { key: 'pdf', icon: exportIcons.pdf, label: 'PDF (.pdf)' },
      { key: 'word', icon: exportIcons.word, label: 'Word (.doc)' }
    ],
    onClick: ({ key }) => { setExportFormat(key as ExportFormat); downloadExport(key as ExportFormat) }
  }

  return <div className={`ant-smart-table${props.fillHeight ? ' table-fill-height' : ''}`}>
    <div className="ant-table-toolbar">
      <div className="ant-table-summary">{title && <strong>{title}</strong>}<span>{data.length} of {rows.length} rows</span></div>
      <div className="ant-table-actions">
        {!props.hideSearch && <Input allowClear className="table-filter" placeholder="Search table..." value={query} onChange={event => setQuery(event.target.value)} />}
        {(props.hideInactiveClear === false || query || dirtyTable) && <Button onClick={clear}>Reset</Button>}
        {exportFormat === 'excel' && props.exportToolbar}
        <Dropdown.Button className={`export-split-btn export-${exportFormat}`} menu={exportMenu} icon={<DownOutlined />} onClick={() => downloadExport()} disabled={!exportRows.length || props.exportDisabled}>
          <span className="export-button-label">{exportIcons[exportFormat]} Export {exportLabels[exportFormat]}</span>
        </Dropdown.Button>
        {props.primaryAction}
      </div>
    </div>
    {props.view === 'Cards' ? <>
      <div className="shared-record-cards" aria-busy={props.loading}>
        {data.slice((Math.min(cardPage, Math.max(1, Math.ceil(data.length / 10))) - 1) * 10, Math.min(cardPage, Math.max(1, Math.ceil(data.length / 10))) * 10).map((row, index) => <article key={String(props.getRowId ? props.getRowId(row, rows.indexOf(row)) : (row as Record<string, unknown>).id ?? index)}>
          <dl>{columns.map(column => <div key={String(column.key)}><dt>{column.label}</dt><dd>{column.render ? column.render(row) : text(valueOf(row, column)) || '—'}</dd></div>)}</dl>
          {(actions || onEdit) && <footer>{actions ? actions(row) : <Button onClick={() => onEdit?.(row)}>Edit</Button>}</footer>}
        </article>)}
        {!data.length && <Empty description={emptyText} />}
      </div>
      {data.length > 10 && <Pagination current={Math.min(cardPage, Math.ceil(data.length / 10))} pageSize={10} total={data.length} showSizeChanger={false} onChange={setCardPage} />}
    </> : <Table<T>
      key={tableKey}
      size="middle"
      loading={props.loading}
      className="zoho-ant-table"
      columns={antColumns}
      dataSource={data}
      rowKey={row => {
        const rowIndex = rows.indexOf(row)
        return String(props.getRowId ? props.getRowId(row, rowIndex) : (row as Record<string, unknown>).id ?? rowIndex)
      }}
      rowClassName={row => rowClassName?.(row) ?? ''}
      locale={{ emptyText }}
      pagination={pagination}
      rowSelection={props.rowSelection ? { ...props.rowSelection, selectedRowKeys: props.rowSelection.selectedRowKeys?.map(String) } : undefined}
      tableLayout="fixed"
      scroll={{ x: tableScrollX, y: props.fillHeight ? '100%' : props.scrollY }}
      onChange={(_, __, ___, extra) => { setDirtyTable(true); setVisibleTableState({ source: data, rows: extra.currentDataSource as T[] }) }}
    />}
  </div>
}
