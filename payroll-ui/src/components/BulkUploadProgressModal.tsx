import { Alert, Button, Modal, Progress, Space, Tag } from 'antd'

export type BulkUploadState = 'uploading' | 'success' | 'error'
export type BulkUploadSummary = { totalRows: number; completedRows?: number; inserted?: number; updated?: number; savedRows?: number; errors?: string[] }
export type BulkUploadLabels = { total?: string; completed?: string; inserted?: string; updated?: string; saved?: string }

export default function BulkUploadProgressModal(p: { open: boolean; title: string; state: BulkUploadState; percent: number; summary: BulkUploadSummary; onClose: () => void; stage?: string; labels?: BulkUploadLabels; successMessage?: string; successDescription?: string; errorMessage?: string }) {
  const done = p.state === 'success'
  const failed = p.state === 'error'
  const completed = p.summary.completedRows ?? (done ? p.summary.totalRows : Math.floor((p.summary.totalRows || 0) * p.percent / 100))
  const savedRows = p.summary.savedRows ?? (p.summary.inserted ?? 0) + (p.summary.updated ?? 0)
  const labels = { total: 'Total', completed: 'Completed', inserted: 'Inserted', updated: 'Updated', saved: 'Saved', ...p.labels }
  return <Modal open={p.open} title={p.title} footer={<Button disabled={p.state === 'uploading'} onClick={p.onClose}>Close</Button>} onCancel={p.onClose} closable={p.state !== 'uploading'} maskClosable={p.state !== 'uploading'}>
    <Space data-testid="bulk-upload-progress" data-state={p.state} direction="vertical" size="middle" style={{ width: '100%' }}>
      <Progress percent={p.percent} status={failed ? 'exception' : done ? 'success' : 'active'} />
      {p.stage && <Tag color={failed ? 'red' : done ? 'green' : 'processing'}>{p.stage}</Tag>}
      <Space wrap>
        <Tag>{labels.total} {p.summary.totalRows || '-'}</Tag>
        <Tag color={done ? 'green' : 'blue'}>{labels.completed} {completed}</Tag>
        {p.summary.savedRows !== undefined
          ? <Tag color="green">{labels.saved} {p.summary.savedRows}</Tag>
          : <><Tag color="green">{labels.inserted} {p.summary.inserted ?? 0}</Tag><Tag color="cyan">{labels.updated} {p.summary.updated ?? 0}</Tag></>}
      </Space>
      {failed && <Alert type="error" showIcon message={p.errorMessage || (savedRows ? 'Upload completed with errors.' : 'Upload failed. No rows were saved.')} description={(p.summary.errors?.length ? p.summary.errors : ['Upload failed.']).slice(0, 8).map(error => <div key={error}>{error}</div>)} />}
      {done && <Alert type="success" showIcon message={p.successMessage || 'Upload completed successfully.'} description={p.successDescription || 'All rows were validated and saved.'} />}
    </Space>
  </Modal>
}
