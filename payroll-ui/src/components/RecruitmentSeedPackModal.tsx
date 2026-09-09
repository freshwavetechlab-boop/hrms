import { DownloadOutlined, InboxOutlined, UploadOutlined } from '@ant-design/icons'
import { Alert, Button, Modal, Space, Table, Tag, Typography, Upload } from 'antd'
import type { UploadFile } from 'antd/es/upload/interface'
import { useEffect, useMemo, useState } from 'react'
import {
  downloadCandidateSeedTemplate, downloadHiringSeedTemplate, importRecruitmentSeedPack,
  type RecruitmentSeedPackImportItem, type RecruitmentSeedPackImportResult,
} from '../services/recruitmentSeedPackService'
import './RecruitmentSeedPackModal.css'

type Props = {
  open: boolean
  mode: 'hiring' | 'candidate'
  onClose: () => void
  onImported?: () => void | Promise<void>
}

export default function RecruitmentSeedPackModal({ open, mode, onClose, onImported }: Props) {
  const [files, setFiles] = useState<UploadFile[]>([])
  const [result, setResult] = useState<RecruitmentSeedPackImportResult | null>(null)
  const [error, setError] = useState('')
  const [uploading, setUploading] = useState(false)
  const workbook = useMemo(() => files.find(file => /\.xlsx?$/i.test(file.name))?.originFileObj as File | undefined, [files])
  const resumes = useMemo(() => files.filter(file => !/\.xlsx?$/i.test(file.name)).map(file => file.originFileObj as File).filter(Boolean), [files])

  useEffect(() => {
    if (!open) { setFiles([]); setResult(null); setError(''); setUploading(false) }
  }, [open])

  const runImport = async () => {
    if (!workbook) { setError('Select the completed Excel template.'); return }
    if (mode === 'hiring' && resumes.length) { setError('Hiring seed accepts one Excel workbook. Use Candidate seed for resume files.'); return }
    setUploading(true)
    setError('')
    const response = await importRecruitmentSeedPack(workbook, mode === 'candidate' ? resumes : [])
    setUploading(false)
    if (!response.ok) { setError(response.error); return }
    setResult(response.data)
    await onImported?.()
  }

  const candidateMode = mode === 'candidate'
  return <Modal
    className="recruitment-seed-pack-modal"
    open={open}
    width="min(980px, 96vw)"
    title={candidateMode ? 'Candidate + ATS seed pack' : 'Hiring request + JD + ATS seed pack'}
    onCancel={() => !uploading && onClose()}
    destroyOnClose
    footer={<Space>
      <Button onClick={onClose} disabled={uploading}>{result ? 'Close' : 'Cancel'}</Button>
      <Button data-testid="seed-pack-run-import" type="primary" icon={<UploadOutlined />} loading={uploading} disabled={!workbook} onClick={() => void runImport()}>Import seed pack</Button>
    </Space>}
  >
    <div className="seed-pack-intro">
      <div>
        <Typography.Title level={4}>{candidateMode ? 'Upload candidates after JD approval' : 'Seed one or many roles in one upload'}</Typography.Title>
        <Typography.Paragraph>{candidateMode
          ? 'Add the completed candidate workbook and all referenced resumes together. Contact identity is verified before ATS processing.'
          : 'Fill the downloaded workbook headings. Requests, structured JD sections and weighted ATS skills are saved together.'}</Typography.Paragraph>
      </div>
      <Button data-testid="seed-pack-download-template" icon={<DownloadOutlined />} onClick={candidateMode ? downloadCandidateSeedTemplate : downloadHiringSeedTemplate}>Download template</Button>
    </div>

    <Alert type="info" showIcon message={candidateMode
      ? 'Approved position + approved JD required. A safe rerun completes deferred candidates after approval.'
      : 'EXAMPLE rows are ignored. Change every Source Key you want to import; that key also prevents duplicates on rerun.'} />

    <Upload.Dragger
      data-testid="seed-pack-file-drop"
      multiple={candidateMode}
      accept={candidateMode ? '.xlsx,.xls,.pdf,.doc,.docx,.rtf,.txt' : '.xlsx,.xls'}
      beforeUpload={() => false}
      fileList={files}
      onChange={info => { setFiles(info.fileList); setResult(null); setError('') }}
      onRemove={file => { setFiles(current => current.filter(item => item.uid !== file.uid)); return true }}
    >
      <p className="ant-upload-drag-icon"><InboxOutlined /></p>
      <p className="ant-upload-text">{candidateMode ? 'Drop one workbook and its resume files here' : 'Drop the completed hiring seed workbook here'}</p>
      <p className="ant-upload-hint">{candidateMode ? 'The Resume File column must exactly match each selected filename.' : 'Accepted formats: .xlsx or .xls'}</p>
    </Upload.Dragger>

    {error && <Alert data-testid="seed-pack-error" className="seed-pack-result-alert" type="error" showIcon message="Import could not start" description={error} />}
    {result && <>
      <Alert data-testid="seed-pack-summary" className="seed-pack-result-alert" type={result.failed ? 'warning' : 'success'} showIcon
        message={`${result.created} created | ${result.updated} updated | ${result.reused} reused | ${result.deferred} deferred | ${result.failed} failed`}
        description={result.failed
          ? 'Review the failed row messages below, correct the workbook and safely upload the same file again.'
          : result.deferred
            ? 'Deferred rows are safe to retry with the same workbook after approvals/configuration are ready.'
            : 'Every processed row is duplicate-safe.'} />
      <Table<RecruitmentSeedPackImportItem>
        data-testid="seed-pack-results"
        size="small"
        rowKey={row => `${row.sheet}-${row.rowNumber}-${row.entity}`}
        dataSource={result.items}
        pagination={false}
        scroll={{ x: 860, y: 280 }}
        columns={[
          { title: 'Source key', dataIndex: 'sourceKey', width: 170, ellipsis: true },
          { title: 'Record', dataIndex: 'entity', width: 155 },
          { title: 'Result', dataIndex: 'outcome', width: 100, render: value => <Tag color={outcomeColor(value)}>{value}</Tag> },
          { title: 'Details', dataIndex: 'message', width: 410 },
        ]}
      />
    </>}
  </Modal>
}

function outcomeColor(value: string) {
  if (value === 'Created') return 'green'
  if (value === 'Updated') return 'blue'
  if (value === 'Reused') return 'purple'
  if (value === 'Deferred') return 'orange'
  return 'red'
}
