import { useCallback, useEffect, useMemo, useState, type Key, type ReactNode } from 'react'
import { DownOutlined, FileExcelOutlined, FileOutlined, FilePdfOutlined, FileTextOutlined, FileWordOutlined } from '@ant-design/icons'
import { Button, Dropdown, Input, Table } from 'antd'
import type { ColumnsType, TablePaginationConfig, TableRowSelection } from 'antd/es/table/interface'
import type { MenuProps } from 'antd'
import { jsPDF } from 'jspdf'
import autoTable from 'jspdf-autotable'

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
  exportToolbar?: ReactNode
  onExcelExport?: (visibleRows: T[]) => void
  exportDisabled?: boolean
  pageSizeOptions?: number[]
  rowSelection?: TableRowSelection<T>
}

const text = (value: unknown) => value === null || value === undefined ? '' : String(value)
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

export default function DataTable<T extends object>(props: DataTableProps<T>) {
  const { rows, columns, onEdit, actions, rowClassName, emptyText = 'No records', title, exportFileName = 'table-export' } = props
  const [query, setQuery] = useState('')
  const [tableKey, setTableKey] = useState(0)
  const [dirtyTable, setDirtyTable] = useState(false)
  const [exportRows, setExportRows] = useState<T[]>(rows)
  const [exportFormat, setExportFormat] = useState<ExportFormat>('excel')
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
  const exportValueOf = (row: T, column: Column<T>) => column.exportValue ? column.exportValue(row) : valueOf(row, column)
  const downloadExport = () => {
    if (exportFormat === 'excel' && props.onExcelExport) {
      props.onExcelExport(exportRows)
      return
    }
    let content: string
    let mimeType: string
    let extension: string
    if (exportFormat === 'pdf') {
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
    if (exportFormat === 'csv') {
      content = `\uFEFF${columns.map(column => csvCell(column.label)).join(',')}\r\n${exportRows.map(row => columns.map(column => csvCell(exportValueOf(row, column))).join(',')).join('\r\n')}`
      mimeType = 'text/csv;charset=utf-8'
      extension = 'csv'
    } else if (exportFormat === 'txt') {
      content = `${columns.map(column => textCell(column.label)).join('\t')}\r\n${exportRows.map(row => columns.map(column => textCell(exportValueOf(row, column))).join('\t')).join('\r\n')}`
      mimeType = 'text/plain;charset=utf-8'
      extension = 'txt'
    } else {
      const header = columns.map(column => `<th>${htmlCell(column.label)}</th>`).join('')
      const body = exportRows.map(row => `<tr>${columns.map(column => `<td>${htmlCell(exportValueOf(row, column))}</td>`).join('')}</tr>`).join('')
      content = `<!doctype html><html><head><meta charset="utf-8"><style>table{border-collapse:collapse;font-family:Arial,sans-serif;font-size:11px}th,td{border:1px solid #cbd5e1;padding:6px;text-align:left}th{background:#eff6ff;color:#1d4ed8}</style></head><body><table><thead><tr>${header}</tr></thead><tbody>${body}</tbody></table></body></html>`
      mimeType = exportFormat === 'word' ? 'application/msword' : 'application/vnd.ms-excel'
      extension = exportFormat === 'word' ? 'doc' : 'xls'
    }
    const anchor = document.createElement('a')
    anchor.href = URL.createObjectURL(new Blob([content], { type: mimeType }))
    anchor.download = `${exportFileName}.${extension}`
    anchor.click()
    URL.revokeObjectURL(anchor.href)
  }
  const exportMenu: MenuProps = {
    selectedKeys: [exportFormat],
    items: [
      { key: 'excel', icon: exportIcons.excel, label: 'Excel (.xls)' },
      { key: 'csv', icon: exportIcons.csv, label: 'CSV (.csv)' },
      { key: 'txt', icon: exportIcons.txt, label: 'Text (.txt)' },
      { key: 'pdf', icon: exportIcons.pdf, label: 'PDF (.pdf)' },
      { key: 'word', icon: exportIcons.word, label: 'Word (.doc)' }
    ],
    onClick: ({ key }) => setExportFormat(key as ExportFormat)
  }

  return <div className="ant-smart-table">
    <div className="ant-table-toolbar">
      <div className="ant-table-summary">{title && <strong>{title}</strong>}<span>{data.length} of {rows.length} rows</span></div>
      <div className="ant-table-actions">
        <Input allowClear className="table-filter" placeholder="Search table..." value={query} onChange={event => setQuery(event.target.value)} />
        <Button onClick={clear} disabled={!query && !dirtyTable}>Clear</Button>
        {exportFormat === 'excel' && props.exportToolbar}
        <Dropdown.Button className={`export-split-btn export-${exportFormat}`} menu={exportMenu} icon={<DownOutlined />} onClick={downloadExport} disabled={!exportRows.length || props.exportDisabled}>
          <span className="export-button-label">{exportIcons[exportFormat]} Export {exportLabels[exportFormat]}</span>
        </Dropdown.Button>
      </div>
    </div>
    <Table<T>
      key={tableKey}
      size="middle"
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
      rowSelection={props.rowSelection}
      tableLayout="fixed"
      scroll={{ x: tableScrollX }}
      onChange={(_, __, ___, extra) => { setDirtyTable(true); setExportRows(extra.currentDataSource as T[]) }}
    />
  </div>
}
