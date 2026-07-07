import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Drawer, Space, Tag } from 'antd'
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
}

export const emptyBulkUploadPreview: BulkUploadPreviewState = { open: false, title: '', fileName: '', headers: [], rows: [], issues: [] }

export default function BulkUploadPreviewModal(p: { preview: BulkUploadPreviewState; importing?: boolean; onCancel: () => void; onConfirm: (preview: BulkUploadPreviewState) => void; onResolveDuplicates?: (mode: 'skip' | 'replace' | 'replaceAll', sheetName: string) => void }) {
  const [activeSheet, setActiveSheet] = useState(0)
  const [draft, setDraft] = useState<BulkUploadPreviewState>(p.preview)
  const sheets = useMemo(() => draft.sheets?.length ? draft.sheets : [{ name: 'Preview', headers: draft.headers, rows: draft.rows, issues: draft.issues }], [draft])
  const current = sheets[Math.min(activeSheet, sheets.length - 1)] ?? sheets[0]
  const currentIssues = current?.issues ?? draft.issues
  const totalRows = draft.sheets?.length ? draft.sheets.reduce((sum, sheet) => sum + sheet.rows.length, 0) : draft.rows.length
  const visibleRows = current?.rows ?? []
  const hasErrors = draft.issues.length > 0
  const hasDuplicateIssues = draft.issues.some(issue => issue.message.toLowerCase().includes('duplicates row'))
  useEffect(() => {
    setDraft(p.preview)
    if (p.preview.open) setActiveSheet(0)
  }, [p.preview])
  const applyDraft = (next: BulkUploadPreviewState) => setDraft(rebuildDuplicateIssues(next))
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
  return <Drawer className="bulk-preview-modal" width="min(1280px,96vw)" placement="right" open={draft.open} title={draft.title} onClose={p.onCancel} maskClosable={!p.importing} closable={!p.importing} footer={<Space><Button disabled={p.importing} onClick={p.onCancel}>Cancel</Button><Button type="primary" loading={p.importing} disabled={hasErrors || !draft.rows.length} onClick={() => p.onConfirm(draft)}>Import</Button></Space>}>
    <div className="bulk-preview-content">
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
            <thead><tr><th>#</th><th>Action</th>{(current?.headers ?? []).map(header => <th key={header}>{header}</th>)}</tr></thead>
            <tbody>{visibleRows.map((row, rowIndex) => {
              const rowNumber = rowIndex + 2
              const rowIssue = currentIssues.some(issue => issue.rowNumber === rowNumber)
              return <tr key={rowIndex} className={rowIssue ? 'has-error' : ''}><td>{rowNumber}</td><td><button type="button" className="bulk-preview-row-delete" onClick={() => deleteRow(rowIndex)}>Skip</button></td>{(current?.headers ?? []).map((header, colIndex) => <td key={header} className={hasCellIssue(currentIssues, rowNumber, header) ? 'cell-error' : ''}><input value={row[colIndex] || ''} onChange={event => updateCell(rowIndex, colIndex, event.target.value)} aria-label={`${header} row ${rowNumber}`} /></td>)}</tr>
            })}</tbody>
          </table>
        </div>
        {sheets.length > 1 && <div className="bulk-preview-sheet-tabs">{sheets.map((sheet, index) => <button type="button" key={sheet.name} className={index === activeSheet ? 'active' : ''} onClick={() => setActiveSheet(index)}>{sheet.name}<span>{sheet.rows.length}</span>{(sheet.issues?.length ?? 0) > 0 && <b>{sheet.issues?.length}</b>}</button>)}</div>}
      </div>
    </div>
  </Drawer>
}

function rebuildDuplicateIssues(preview: BulkUploadPreviewState): BulkUploadPreviewState {
  const baseIssues = preview.issues.filter(issue => !issue.message.toLowerCase().includes('duplicates row'))
  if (preview.sheets?.length) {
    const globalOnly = baseIssues.filter(issue => issue.column === 'Client')
    const sheets = preview.sheets.map(sheet => ({ ...sheet, issues: [...(sheet.issues ?? []).filter(issue => !issue.message.toLowerCase().includes('duplicates row')), ...duplicateIssues(sheet.headers, sheet.rows)] }))
    const issues = [...globalOnly, ...sheets.flatMap(sheet => (sheet.issues ?? []).map(issue => ({ ...issue, message: `${sheet.name}: ${issue.message}` })))]
    return { ...preview, rows: sheets.flatMap(sheet => sheet.rows), issues, sheets }
  }
  const issues = [...baseIssues, ...duplicateIssues(preview.headers, preview.rows)]
  return { ...preview, issues }
}

function duplicateIssues(headers: string[], rows: string[][]): ImportPreviewIssue[] {
  const codeIndex = headers.findIndex(header => header.replace(/[\s_-]/g, '').toLowerCase() === 'employeecode')
  if (codeIndex < 0) return []
  const seen = new Map<string, number>()
  const issues: ImportPreviewIssue[] = []
  rows.forEach((row, index) => {
    const code = (row[codeIndex] ?? '').trim().toLowerCase()
    if (!code) return
    const rowNumber = index + 2
    const first = seen.get(code)
    if (first) issues.push({ rowNumber, column: headers[codeIndex], message: `Employee Code duplicates row ${first}.` })
    else seen.set(code, rowNumber)
  })
  return issues
}
