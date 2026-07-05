import { Alert, Button, Modal, Space, Tag } from 'antd'
import { hasCellIssue, type ImportPreviewIssue } from '../utils/importPreview'

export type BulkUploadPreviewState = {
  open: boolean
  title: string
  fileName: string
  headers: string[]
  rows: string[][]
  issues: ImportPreviewIssue[]
}

export const emptyBulkUploadPreview: BulkUploadPreviewState = { open: false, title: '', fileName: '', headers: [], rows: [], issues: [] }

export default function BulkUploadPreviewModal(p: { preview: BulkUploadPreviewState; importing?: boolean; onCancel: () => void; onConfirm: () => void }) {
  const visibleRows = p.preview.rows.slice(0, 100)
  const hasErrors = p.preview.issues.length > 0
  return <Modal className="bulk-preview-modal" width="min(1180px,96vw)" open={p.preview.open} title={p.preview.title} onCancel={p.onCancel} maskClosable={!p.importing} closable={!p.importing} footer={<Space><Button disabled={p.importing} onClick={p.onCancel}>Cancel</Button><Button type="primary" loading={p.importing} disabled={hasErrors || !p.preview.rows.length} onClick={p.onConfirm}>Import</Button></Space>}>
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap>
        <Tag>{p.preview.fileName || 'Selected file'}</Tag>
        <Tag color="blue">{p.preview.rows.length} rows</Tag>
        <Tag color={hasErrors ? 'red' : 'green'}>{hasErrors ? `${p.preview.issues.length} issue(s)` : 'Ready to import'}</Tag>
      </Space>
      {hasErrors && <Alert type="error" showIcon message="Review highlighted cells before import." description={p.preview.issues.slice(0, 8).map(issue => <div key={`${issue.rowNumber}-${issue.column}-${issue.message}`}>Row {issue.rowNumber}{issue.column ? `, ${issue.column}` : ''}: {issue.message}</div>)} />}
      {!hasErrors && <Alert type="info" showIcon message="Preview looks okay. Click Import to start upload." />}
      <div className="bulk-preview-table">
        <table>
          <thead><tr><th>#</th>{p.preview.headers.map(header => <th key={header}>{header}</th>)}</tr></thead>
          <tbody>{visibleRows.map((row, rowIndex) => {
            const rowNumber = rowIndex + 2
            const rowIssue = p.preview.issues.some(issue => issue.rowNumber === rowNumber)
            return <tr key={rowIndex} className={rowIssue ? 'has-error' : ''}><td>{rowNumber}</td>{p.preview.headers.map((header, colIndex) => <td key={header} className={hasCellIssue(p.preview.issues, rowNumber, header) ? 'cell-error' : ''}>{row[colIndex] || ''}</td>)}</tr>
          })}</tbody>
        </table>
      </div>
      {p.preview.rows.length > visibleRows.length && <small className="bulk-preview-note">Showing first {visibleRows.length} rows only.</small>}
    </Space>
  </Modal>
}
