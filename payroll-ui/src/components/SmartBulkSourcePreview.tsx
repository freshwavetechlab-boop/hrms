import { useMemo, useState, type CSSProperties } from 'react'
import { Button, Dropdown, Modal, Pagination, Select, Space, Tag } from 'antd'
import { ScissorOutlined, UndoOutlined } from '@ant-design/icons'
import type { ImportPreviewSheet } from '../utils/importPreview'
import { bulkImportMappingColor, type BulkImportDefinition, type BulkImportMapping, type BulkImportNameSplitStrategy, type MaterializedBulkImportNameSplit } from '../utils/smartBulkImport'
import './SmartBulkUploadMapper.css'

type PreviewNameSplit = Omit<MaterializedBulkImportNameSplit, 'sheet'>

type Props = {
  open: boolean
  fileName: string
  sheet: ImportPreviewSheet
  definition: BulkImportDefinition
  mappings: BulkImportMapping
  split?: PreviewNameSplit
  onMap: (sourceColumnIndex: number, targetCode: string) => void
  onUnmapSource: (sourceColumnIndex: number) => void
  onSplitColumn: (sourceColumnIndex: number, strategy: BulkImportNameSplitStrategy) => void
  onSplitStrategyChange: (strategy: BulkImportNameSplitStrategy) => void
  onUndoSplit: () => void
  onClose: () => void
}

export default function SmartBulkSourcePreview(p: Props) {
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [selectedColumn, setSelectedColumn] = useState<number | null>(null)
  const lastPage = Math.max(1, Math.ceil(p.sheet.rows.length / pageSize))
  const currentPage = Math.min(page, lastPage)
  const pageStart = (currentPage - 1) * pageSize
  const visibleRows = p.sheet.rows.slice(pageStart, pageStart + pageSize)
  const mappedSourceColumns = useMemo(() => new Set(Object.values(p.mappings)), [p.mappings])
  const groupedOptions = useMemo(() => Array.from(new Set(p.definition.fields.map(field => field.group))).map(group => ({
    label: group,
    options: p.definition.fields.filter(field => field.group === group).map(field => ({ value: field.code, label: field.label }))
  })), [p.definition.fields])
  const fieldForSource = (sourceIndex: number) => p.definition.fields.find(field => p.mappings[field.code] === sourceIndex)

  return <Modal
    className="smart-bulk-source-preview-modal"
    width="min(1480px,98vw)"
    open={p.open}
    title={<div className="smart-bulk-preview-title"><span>UPLOAD WORKING PREVIEW</span><b>{p.fileName}</b><small>{p.sheet.name} / {p.sheet.rows.length} data rows / {p.split ? `${p.sheet.headers.length - 1} original → ${p.sheet.headers.length} working columns` : `${p.sheet.headers.length} columns`}</small></div>}
    onCancel={p.onClose}
    footer={<Button type="primary" onClick={p.onClose}>Use mapped preview</Button>}
    afterOpenChange={open => { if (open) { setPage(1); setSelectedColumn(null) } }}
  >
    <div className={`smart-bulk-source-preview${p.split ? ' has-split' : ''}`} data-testid="smart-bulk-source-preview">
      <div className="smart-bulk-source-preview-help">
        <div><b>Split and map directly in this grid</b><span>Use Split name on a source column to create two real working columns. The values visible here are the values sent to the final import review.</span></div>
        <Space wrap><Tag color="blue">{mappedSourceColumns.size} mapped</Tag><Tag>{Math.max(0, p.sheet.headers.length - mappedSourceColumns.size)} skipped</Tag></Space>
      </div>

      {p.split && <div className="smart-bulk-preview-split-summary" data-testid="smart-bulk-preview-split-summary">
        <div><ScissorOutlined /><span><b>{p.split.sourceHeader}</b><small>is now two independently mappable working columns.</small></span></div>
        <Space wrap>
          <Select<BulkImportNameSplitStrategy>
            data-testid="smart-bulk-preview-split-strategy"
            value={p.split.strategy}
            options={[{ value: 'last-token', label: 'Last word = Last Name' }, { value: 'first-token', label: 'First word = First Name' }]}
            onChange={p.onSplitStrategyChange}
          />
          <Button data-testid="smart-bulk-preview-split-undo" icon={<UndoOutlined />} onClick={p.onUndoSplit}>Undo split</Button>
        </Space>
      </div>}

      <div className="smart-bulk-source-preview-table" data-testid="smart-bulk-source-preview-table">
        <table>
          <thead><tr><th className="row-number">#</th>{p.sheet.headers.map((header, sourceIndex) => {
            const field = fieldForSource(sourceIndex)
            const fieldIndex = field ? p.definition.fields.indexOf(field) : -1
            const color = field ? bulkImportMappingColor(fieldIndex) : '#94a3b8'
            const splitRole = p.split?.firstNameColumnIndex === sourceIndex ? 'First Name' : p.split?.lastNameColumnIndex === sourceIndex ? 'Last Name' : ''
            const canSplit = !p.split && canSplitNameColumn(header, p.sheet.rows, sourceIndex)
            return <th
              key={`${sourceIndex}-${header}`}
              className={`${field ? 'mapped' : 'unmapped'}${selectedColumn === sourceIndex ? ' selected' : ''}${splitRole ? ' split-derived' : ''}`}
              style={{ '--map-color': color } as CSSProperties}
              data-testid="smart-bulk-preview-column"
              data-source-column={header || `Column ${sourceIndex + 1}`}
              data-mapped={Boolean(field)}
              data-column-kind={splitRole ? 'split' : 'source'}
              data-split-source={splitRole ? p.split?.sourceHeader : undefined}
              onClick={() => setSelectedColumn(sourceIndex)}
            >
              <div className="smart-bulk-preview-column-head">
                <span title={header}>{header || `Unnamed column ${sourceIndex + 1}`}</span>
                {splitRole && <Tag className="smart-bulk-preview-derived-tag" color="purple">{splitRole} · split result</Tag>}
                {canSplit && <Dropdown
                  trigger={['click']}
                  menu={{
                    items: [
                      { key: 'last-token', label: 'Last word = Last Name' },
                      { key: 'first-token', label: 'First word = First Name' }
                    ],
                    onClick: info => { info.domEvent.stopPropagation(); p.onSplitColumn(sourceIndex, info.key as BulkImportNameSplitStrategy) }
                  }}
                >
                  <Button data-testid="smart-bulk-preview-split" data-source-column={header || `Column ${sourceIndex + 1}`} className="smart-bulk-preview-split-button" size="small" icon={<ScissorOutlined />} onClick={event => event.stopPropagation()}>Split name</Button>
                </Dropdown>}
                <Select
                  className="smart-bulk-preview-map-chip"
                  data-testid="smart-bulk-preview-map-select"
                  data-source-column={header || `Column ${sourceIndex + 1}`}
                  aria-label={`Map ${header || `Column ${sourceIndex + 1}`} to HRMS field`}
                  showSearch
                  allowClear
                  value={field?.code}
                  placeholder="Map"
                  optionFilterProp="label"
                  filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
                  options={groupedOptions}
                  onClick={event => event.stopPropagation()}
                  onChange={targetCode => targetCode ? p.onMap(sourceIndex, targetCode) : p.onUnmapSource(sourceIndex)}
                />
                <small>{field ? `Mapped to ${field.label}` : splitRole ? `Created from ${p.split?.sourceHeader}; map independently` : 'Not mapped / will be skipped'}</small>
              </div>
            </th>
          })}</tr></thead>
          <tbody>{visibleRows.map((row, visibleIndex) => <tr key={pageStart + visibleIndex}><td className="row-number">{pageStart + visibleIndex + 2}</td>{p.sheet.headers.map((header, sourceIndex) => {
            const field = fieldForSource(sourceIndex)
            const color = field ? bulkImportMappingColor(p.definition.fields.indexOf(field)) : '#94a3b8'
            const value = row[sourceIndex] ?? ''
            const splitRole = p.split?.firstNameColumnIndex === sourceIndex || p.split?.lastNameColumnIndex === sourceIndex
            return <td key={`${sourceIndex}-${header}`} className={`${field ? 'mapped' : 'unmapped'}${selectedColumn === sourceIndex ? ' selected' : ''}${splitRole ? ' split-derived' : ''}`} style={{ '--map-color': color } as CSSProperties} title={value}>{value || <span className="empty-cell">Blank</span>}</td>
          })}</tr>)}</tbody>
        </table>
      </div>

      <div className="smart-bulk-source-preview-pagination"><span>Showing {p.sheet.rows.length ? pageStart + 1 : 0}-{Math.min(pageStart + pageSize, p.sheet.rows.length)} of {p.sheet.rows.length} rows</span><Pagination size="small" current={currentPage} pageSize={pageSize} total={p.sheet.rows.length} showSizeChanger pageSizeOptions={[25, 50, 100, 250]} onChange={(nextPage, nextSize) => { setPageSize(nextSize); setPage(nextSize !== pageSize ? 1 : nextPage) }} /></div>
    </div>
  </Modal>
}

function canSplitNameColumn(header: string, rows: string[][], sourceIndex: number) {
  const normalizedHeader = header.toLowerCase().replace(/[^a-z]/g, '')
  return normalizedHeader.includes('name') || rows.slice(0, 100).some(row => (row[sourceIndex] ?? '').trim().split(/\s+/).filter(Boolean).length > 1)
}
