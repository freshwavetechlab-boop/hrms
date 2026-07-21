import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { Alert, Button, Drawer, Pagination, Space, Tag } from 'antd'
import { hasCellIssue, type ImportPreviewIssue } from '../utils/importPreview'

export type BulkUploadPreviewSheet = {
  name: string
  headers: string[]
  rows: string[][]
  issues?: ImportPreviewIssue[]
}

export type BulkUploadPreviewState = {
  open: boolean
  title: string
  fileName: string
  headers: string[]
  rows: string[][]
  issues: ImportPreviewIssue[]
  sheets?: BulkUploadPreviewSheet[]
  columnMeta?: Record<string, BulkUploadPreviewColumnMeta>
}

export type BulkUploadPreviewColumnMeta = { sourceHeader: string; color: string; kind: 'mapped' | 'default' | 'generated' | 'transformed' }

export const emptyBulkUploadPreview: BulkUploadPreviewState = { open: false, title: '', fileName: '', headers: [], rows: [], issues: [] }

export default function BulkUploadPreviewModal(p: { preview: BulkUploadPreviewState; importing?: boolean; onCancel: () => void; onConfirm: (preview: BulkUploadPreviewState) => void; onResolveDuplicates?: (mode: 'skip' | 'replace' | 'replaceAll', sheetName: string) => void; uniqueFields?: string[][] }) {
  const [activeSheet, setActiveSheet] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(100)
  const [draft, setDraft] = useState<BulkUploadPreviewState>(p.preview)
  const sheets = useMemo(() => draft.sheets?.length ? draft.sheets : [{ name: 'Preview', headers: draft.headers, rows: draft.rows, issues: draft.issues }], [draft])
  const current = sheets[Math.min(activeSheet, sheets.length - 1)] ?? sheets[0]
  const currentIssues = current?.issues ?? draft.issues
  const totalRows = draft.sheets?.length ? draft.sheets.reduce((sum, sheet) => sum + sheet.rows.length, 0) : draft.rows.length
  const allVisibleRows = current?.rows ?? []
  const lastPage = Math.max(1, Math.ceil(allVisibleRows.length / pageSize))
  const currentPage = Math.min(page, lastPage)
  const pageStart = (currentPage - 1) * pageSize
  const visibleRows = allVisibleRows.slice(pageStart, pageStart + pageSize)
  const hasErrors = draft.issues.length > 0
  const hasDuplicateIssues = draft.issues.some(issue => issue.message.toLowerCase().includes('duplicates row'))
  useEffect(() => {
    setDraft(p.preview)
    if (p.preview.open) { setActiveSheet(0); setPage(1) }
  }, [p.preview])
  const applyDraft = (next: BulkUploadPreviewState) => setDraft(rebuildUniqueIssues(next, p.uniqueFields ?? []))
  const updateCell = (rowIndex: number, colIndex: number, value: string) => {
    if (draft.sheets?.length) {
      const nextSheets = draft.sheets.map((sheet, index) => index === activeSheet ? { ...sheet, rows: sheet.rows.map((row, r) => r === rowIndex ? row.map((cell, c) => c === colIndex ? value : cell) : row) } : sheet)
      applyDraft({ ...draft, rows: nextSheets.flatMap(sheet => sheet.rows), sheets: nextSheets })
      return
    }
    const rows = draft.rows.map((row, r) => r === rowIndex ? row.map((cell, c) => c === colIndex ? value : cell) : row)
    applyDraft({ ...draft, rows })
  }
  const deleteRow = (rowIndex: number) => {
    if (draft.sheets?.length) {
      const nextSheets = draft.sheets.map((sheet, index) => index === activeSheet ? { ...sheet, rows: sheet.rows.filter((_, r) => r !== rowIndex) } : sheet)
      applyDraft({ ...draft, rows: nextSheets.flatMap(sheet => sheet.rows), sheets: nextSheets })
      return
    }
    applyDraft({ ...draft, rows: draft.rows.filter((_, r) => r !== rowIndex) })
  }
  return <Drawer className="bulk-preview-modal" width="min(1280px,96vw)" placement="right" open={draft.open} title={draft.title} onClose={p.onCancel} maskClosable={!p.importing} closable={!p.importing} footer={<Space><Button disabled={p.importing} onClick={p.onCancel}>Cancel</Button><Button data-testid="bulk-preview-import" type="primary" loading={p.importing} disabled={hasErrors || !draft.rows.length} onClick={() => p.onConfirm(draft)}>Import</Button></Space>}>
    <div className="bulk-preview-content" data-testid="smart-bulk-preview">
      <Space wrap>
        <Tag>{draft.fileName || 'Selected file'}</Tag>
        <Tag color="blue">{totalRows} rows</Tag>
        <Tag color={hasErrors ? 'red' : 'green'}>{hasErrors ? `${draft.issues.length} issue(s)` : 'Ready to import'}</Tag>
      </Space>
      {hasErrors && <Alert type="error" showIcon message="Review highlighted cells before import." description={draft.issues.slice(0, 8).map(issue => <div key={`${issue.rowNumber}-${issue.column}-${issue.message}`}>Row {issue.rowNumber}{issue.column ? `, ${issue.column}` : ''}: {issue.message}</div>)} />}
      {!hasErrors && <Alert type="info" showIcon message="Preview looks okay. Click Import to start upload." />}
      {hasDuplicateIssues && p.onResolveDuplicates && <div className="bulk-preview-resolve">
        <span>Duplicate rows found.</span>
        <Space wrap size={8}>
          <Button size="small" onClick={() => p.onResolveDuplicates?.('skip', current?.name ?? '')}>Skip</Button>
          <Button size="small" onClick={() => p.onResolveDuplicates?.('replace', current?.name ?? '')}>Replace</Button>
          <Button size="small" type="primary" onClick={() => p.onResolveDuplicates?.('replaceAll', current?.name ?? '')}>Replace all</Button>
        </Space>
      </div>}
      <div className="bulk-preview-grid-shell">
        <div className="bulk-preview-table">
          <table>
            <thead><tr><th>#</th><th>Action</th>{(current?.headers ?? []).map(header => {
              const meta = draft.columnMeta?.[header]
              return <th key={header} className={meta ? 'mapped-preview-column' : ''} style={meta ? { '--preview-map-color': meta.color } as CSSProperties : undefined}><div className="bulk-preview-column-head"><span>{header}</span>{meta && <small>{meta.kind === 'mapped' ? `From: ${meta.sourceHeader}` : meta.sourceHeader}</small>}</div></th>
            })}</tr></thead>
            <tbody>{visibleRows.map((row, visibleRowIndex) => {
              const rowIndex = pageStart + visibleRowIndex
              const rowNumber = rowIndex + 2
              const rowIssue = currentIssues.some(issue => issue.rowNumber === rowNumber)
              return <tr key={rowIndex} className={rowIssue ? 'has-error' : ''}><td>{rowNumber}</td><td><button type="button" className="bulk-preview-row-delete" onClick={() => deleteRow(rowIndex)}>Skip</button></td>{(current?.headers ?? []).map((header, colIndex) => {
                const meta = draft.columnMeta?.[header]
                const issue = hasCellIssue(currentIssues, rowNumber, header)
                return <td key={header} className={`${issue ? 'cell-error' : ''}${meta ? ' mapped-preview-column' : ''}`} style={meta ? { '--preview-map-color': meta.color } as CSSProperties : undefined}><input value={row[colIndex] || ''} onChange={event => updateCell(rowIndex, colIndex, event.target.value)} aria-label={`${header} row ${rowNumber}`} /></td>
              })}</tr>
            })}</tbody>
          </table>
        </div>
        <div className="bulk-preview-pagination"><span>Showing {allVisibleRows.length ? pageStart + 1 : 0}-{Math.min(pageStart + pageSize, allVisibleRows.length)} of {allVisibleRows.length}</span><Pagination size="small" current={currentPage} pageSize={pageSize} total={allVisibleRows.length} showSizeChanger pageSizeOptions={[50, 100, 250]} onChange={(nextPage, nextSize) => { setPageSize(nextSize); setPage(nextSize !== pageSize ? 1 : nextPage) }} /></div>
        {sheets.length > 1 && <div className="bulk-preview-sheet-tabs">{sheets.map((sheet, index) => <button type="button" key={sheet.name} className={index === activeSheet ? 'active' : ''} onClick={() => { setActiveSheet(index); setPage(1) }}>{sheet.name}<span>{sheet.rows.length}</span>{(sheet.issues?.length ?? 0) > 0 && <b>{sheet.issues?.length}</b>}</button>)}</div>}
      </div>
    </div>
  </Drawer>
}

function rebuildUniqueIssues(preview: BulkUploadPreviewState, uniqueFields: string[][]): BulkUploadPreviewState {
  if (!uniqueFields.length) return preview
  const baseIssues = preview.issues.filter(issue => !issue.message.toLowerCase().includes('duplicates row'))
  if (preview.sheets?.length) {
    const globalOnly = baseIssues.filter(issue => issue.column === 'Client')
    const sheets = preview.sheets.map(sheet => ({ ...sheet, issues: [...(sheet.issues ?? []).filter(issue => !issue.message.toLowerCase().includes('duplicates row')), ...uniqueIssues(sheet.headers, sheet.rows, uniqueFields)] }))
    const issues = [...globalOnly, ...sheets.flatMap(sheet => (sheet.issues ?? []).map(issue => ({ ...issue, message: `${sheet.name}: ${issue.message}` })))]
    return { ...preview, rows: sheets.flatMap(sheet => sheet.rows), issues, sheets }
  }
  const issues = [...baseIssues, ...uniqueIssues(preview.headers, preview.rows, uniqueFields)]
  return { ...preview, issues }
}

function uniqueIssues(headers: string[], rows: string[][], uniqueFields: string[][]): ImportPreviewIssue[] {
  const issues: ImportPreviewIssue[] = []
  for (const fields of uniqueFields) {
    const indexes = fields.map(field => headers.findIndex(header => header.replace(/[\s_-]/g, '').toLowerCase() === field.replace(/[\s_-]/g, '').toLowerCase()))
    if (indexes.some(index => index < 0)) continue
    const seen = new Map<string, number>()
    rows.forEach((row, index) => {
      const key = indexes.map(column => (row[column] ?? '').trim().toLowerCase()).join('|')
      if (!key.replace(/\|/g, '')) return
      const rowNumber = index + 2
      const first = seen.get(key)
      if (first) issues.push({ rowNumber, column: fields[fields.length - 1], message: `${fields.join(' + ')} duplicates row ${first}.` })
      else seen.set(key, rowNumber)
    })
  }
  return issues
}
