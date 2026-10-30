import { useState } from 'react'
import { Alert, Button, Checkbox, Descriptions, Drawer, Input, Space, Spin } from 'antd'
import { deleteUnusedBatch, downloadOriginalWorkbook, getOriginalWorkbooks, previewBatchDelete, type BatchDeletePreview, type OriginalWorkbook } from '../services/excelPayslipLifecycleService'
import ExcelPayslipCleanupReview from './ExcelPayslipCleanupReview'

export default function ExcelPayslipBatchLifecycle({ clientId, batchId, canDelete, disabled, onDeleted, onInspectBatch }: {
  clientId: number; batchId: string; canDelete: boolean; disabled: boolean; onDeleted: () => void; onInspectBatch?: (id: string) => void
}) {
  const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [originals, setOriginals] = useState<OriginalWorkbook[]>([]), [preview, setPreview] = useState<BatchDeletePreview | null>(null)
  const [confirm, setConfirm] = useState(''), [acknowledged, setAcknowledged] = useState(false)
  const [discardUnusedLegacy, setDiscardUnusedLegacy] = useState(false)
  const deletionReady = !!preview && (preview.canDelete || (preview.canDiscardUnusedLegacy === true && discardUnusedLegacy))
  const show = async (deletion: boolean) => {
    setOpen(true); setBusy(true); setError(''); setPreview(null); setConfirm(''); setAcknowledged(false); setDiscardUnusedLegacy(false); setOriginals([])
    try {
      const files = await getOriginalWorkbooks(clientId, batchId)
      if (!files.ok) throw new Error(files.error || 'Unable to load original workbook references.')
      setOriginals(files.data)
      if (deletion) {
        const result = await previewBatchDelete(clientId, batchId)
        if (!result.ok || !result.data) throw new Error(result.error || 'Unable to inspect batch dependencies.')
        setPreview(result.data)
      }
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to inspect batch.') }
    finally { setBusy(false) }
  }
  const remove = async () => {
    if (!preview || !deletionReady || confirm !== batchId || !acknowledged || busy) return
    setBusy(true); setError('')
    try {
      const result = await deleteUnusedBatch(clientId, batchId, preview.token, confirm, discardUnusedLegacy)
      if (!result.ok || !result.data.deleted) throw new Error(result.error || 'Deletion was not confirmed. Refresh the preview before retrying.')
      setOpen(false); onDeleted()
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Deletion failed.'); setAcknowledged(false) }
    finally { setBusy(false) }
  }
  const counts = preview ? [
    ['Payslip rows to delete', preview.payslipRows], ['Calculation sources to delete', preview.calculationSources], ['Original links to remove', preview.originalLinks], ['Other batch links retained', preview.otherBatchOriginalLinks],
    ['Delivery records', preview.deliveryRecords], ['Mail jobs', preview.mailJobs], ['Request receipts', preview.requestReceipts],
    ['Notifications', preview.notifications], ['Notification audit records', preview.notificationLogs], ['Master setup receipts', preview.masterSetupReceipts], ['Derived batches', preview.derivedBatches],
    ['Unconverted legacy records', preview.legacyStorageRecords], ['Resolution audit receipts retained', preview.cleanupAuditRecords],
  ] as const : []
  return <>
    <Button size="small" disabled={disabled || busy} onClick={() => void show(false)}>Original workbooks</Button>
    {canDelete && <Button size="small" danger disabled={disabled || busy} onClick={() => void show(true)}>Review / delete batch</Button>}
    <Drawer title={preview ? 'Review batch cleanup' : 'Original workbooks'} open={open} width={840} contentWrapperStyle={{ maxWidth: '96vw' }} style={{ width: '100%', maxWidth: '100%', minWidth: 0 }} onClose={() => { if (!busy) setOpen(false) }} closable={!busy} maskClosable={!busy}
      footer={<Space wrap><Button disabled={busy} onClick={() => setOpen(false)}>Close</Button>{preview && <Button aria-label="Permanently delete batch" aria-busy={busy} danger type="primary" loading={busy} disabled={busy || !deletionReady || confirm !== batchId || !acknowledged} onClick={() => void remove()}>Permanently delete batch</Button>}</Space>}>
      {error && <Alert type="error" showIcon message={error} description={!preview && !originals.length ? "Original-workbook availability is unverified. Close and reopen to retry the metadata request." : undefined} />}
      <Spin spinning={busy}>
        {preview && <>
          <Alert type="warning" showIcon message="This permanently removes the unused batch and its saved calculation source." description="Any retained original attachment bytes and a minimal deletion audit receipt are kept. Without an original file, the deleted worksheet data and formulas cannot be recovered. Employee masters, salary templates, payroll runs and delivery evidence are never deleted." />
          <Descriptions bordered size="small" column={1}><Descriptions.Item label="Batch ID">{batchId}</Descriptions.Item>{counts.map(([label, count]) => <Descriptions.Item key={label} label={label}>{count}</Descriptions.Item>)}</Descriptions>
          {!preview.canDiscardUnusedLegacy && preview.blockers.map(blocker => <Alert key={blocker} type="error" showIcon message={blocker} />)}
          <Button disabled={busy} onClick={() => void show(true)}>Refresh dependency preview</Button>
          <ExcelPayslipCleanupReview key={preview.token} clientId={clientId} preview={preview} busy={busy} setBusy={setBusy}
            onError={setError} onPreview={updated => { setPreview(updated); setConfirm(''); setAcknowledged(false); setDiscardUnusedLegacy(false) }}
            onInspectBatch={onInspectBatch ? id => { setOpen(false); onInspectBatch(id) } : undefined} />
          {preview.canDiscardUnusedLegacy && <Alert type="warning" showIcon message="Discard this unused batch" description={<Space direction="vertical"><span>No replacement batch is required. This may be the latest or only version; deleting it permanently removes its saved worksheet data and formulas. An unavailable original file cannot be recreated. No delivery, payroll-setup or dependent-batch records may exist.</span><Checkbox checked={discardUnusedLegacy} disabled={busy} onChange={event => { setDiscardUnusedLegacy(event.target.checked); setConfirm(''); setAcknowledged(false) }}>I choose to permanently discard this unused batch, even if it is the only version or its original file is unavailable.</Checkbox></Space>} />}
          {(preview.canDelete || preview.canDiscardUnusedLegacy) && <Space direction="vertical" style={{ width: '100%' }}><label>Type the batch ID to confirm<Input aria-label="Batch deletion confirmation ID" value={confirm} onChange={event => setConfirm(event.target.value)} autoComplete="off" /></label><Checkbox checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)}>I reviewed the counts and want to permanently delete this batch.</Checkbox></Space>}
        </>}
        {!originals.length && !busy && !error && <Alert type="info" showIcon message="No retained original is linked to this batch." description="Older batches may contain parsed worksheet data only. Their original uploaded bytes cannot be reconstructed." />}
        {originals.map(original => <Descriptions key={original.id} bordered size="small" column={1} title={original.fileName}>
          <Descriptions.Item label="Size">{original.sizeBytes.toLocaleString()} bytes</Descriptions.Item>
          <Descriptions.Item label="SHA-256"><span style={{ overflowWrap: 'anywhere' }}>{original.sha256}</span></Descriptions.Item>
          <Descriptions.Item label="Uploaded">{new Date(original.uploadedAtUtc).toLocaleString()}</Descriptions.Item>
          <Descriptions.Item label="Original"><Button disabled={busy} onClick={async () => { setBusy(true); try { await downloadOriginalWorkbook(clientId, batchId, original) } catch (failure) { setError(failure instanceof Error ? failure.message : 'Download failed.') } finally { setBusy(false) } }}>Download original workbook</Button></Descriptions.Item>
        </Descriptions>)}
      </Spin>
    </Drawer>
  </>
}
