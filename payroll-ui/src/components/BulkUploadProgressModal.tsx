import { Alert, Button, Modal, Progress, Space, Tag } from 'antd'

export type BulkUploadState = 'uploading' | 'success' | 'error'
export type BulkUploadSummary = { totalRows: number; completedRows?: number; inserted?: number; updated?: number; errors?: string[] }

export default function BulkUploadProgressModal(p: { open: boolean; title: string; state: BulkUploadState; percent: number; summary: BulkUploadSummary; onClose: () => void }) {
  const done = p.state === 'success'
  const failed = p.state === 'error'
  const completed = p.summary.completedRows ?? (done ? p.summary.totalRows : Math.floor((p.summary.totalRows || 0) * p.percent / 100))
  const savedRows = (p.summary.inserted ?? 0) + (p.summary.updated ?? 0)
  return <Modal open={p.open} title={p.title} footer={<Button disabled={p.state === 'uploading'} onClick={p.onClose}>Close</Button>} onCancel={p.onClose} closable={p.state !== 'uploading'} maskClosable={p.state !== 'uploading'}>
    <Space data-testid="bulk-upload-progress" data-state={p.state} direction="vertical" size="middle" style={{ width: '100%' }}>
      <Progress percent={p.percent} status={failed ? 'exception' : done ? 'success' : 'active'} />
      <Space wrap>
        <Tag>Total {p.summary.totalRows || '-'}</Tag>
        <Tag color={done ? 'green' : 'blue'}>Completed {completed}</Tag>
        <Tag color="green">Inserted {p.summary.inserted ?? 0}</Tag>
        <Tag color="cyan">Updated {p.summary.updated ?? 0}</Tag>
      </Space>
      {failed && <Alert type="error" showIcon message={savedRows ? 'Upload completed with errors.' : 'Upload failed. No rows were saved.'} description={(p.summary.errors?.length ? p.summary.errors : ['Upload failed.']).slice(0, 8).map(error => <div key={error}>{error}</div>)} />}
      {done && <Alert type="success" showIcon message="Upload completed successfully." description="All rows were validated and saved." />}
    </Space>
  </Modal>
}
