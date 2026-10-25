import { useState } from 'react'
import { Alert, Button, Checkbox, Descriptions, Divider, Input, Select, Space, Table } from 'antd'
import { resolveCleanupBlocker, reviewLegacyVersion, type BatchDeletePreview, type LegacyVersionReview } from '../services/excelPayslipLifecycleService'

type Pending = { action: string; objectId: string; keeperId: string; token: string; target: string; label: string; requestId: string }
export default function ExcelPayslipCleanupReview({ clientId, preview, busy, setBusy, onPreview, onError, onInspectBatch }: {
  clientId: number; preview: BatchDeletePreview; busy: boolean; setBusy: (value: boolean) => void
  onPreview: (value: BatchDeletePreview) => void; onError: (value: string) => void; onInspectBatch?: (id: string) => void
}) {
  const [keeperId, setKeeperId] = useState(''), [comparison, setComparison] = useState<LegacyVersionReview | null>(null)
  const [pending, setPending] = useState<Pending | null>(null), [confirmation, setConfirmation] = useState(''), [acknowledged, setAcknowledged] = useState(false)
  const begin = (action: string, objectId: string, keeper: string, token: string, target: string, label: string) => {
    setPending({ action, objectId, keeperId: keeper, token, target, label, requestId: crypto.randomUUID() }); setConfirmation(''); setAcknowledged(false); onError('')
  }
  const compare = async () => {
    setBusy(true); onError(''); setComparison(null); setPending(null)
    try {
      const result = await reviewLegacyVersion(clientId, preview.batchId, keeperId)
      if (!result.ok || !result.data) throw new Error(result.error || 'Unable to compare these versions.')
      setComparison(result.data)
    } catch (failure) { onError(failure instanceof Error ? failure.message : 'Comparison failed.') }
    finally { setBusy(false) }
  }
  const resolve = async () => {
    if (!pending || confirmation !== pending.target || !acknowledged || busy) return
    setBusy(true); onError('')
    try {
      // Preserve requestId/token on an uncertain failure so an explicit retry remains idempotent.
      const result = await resolveCleanupBlocker(clientId, preview.batchId, {
        requestId: pending.requestId, action: pending.action, token: pending.token, confirmBatchId: preview.batchId,
        objectId: pending.objectId, confirmObjectId: confirmation, keeperBatchId: pending.keeperId, acknowledged,
      })
      if (!result.ok || !result.data.applied) throw new Error(result.error || 'Resolution was not confirmed. Refresh before another action.')
      if (!result.data.preview) throw new Error('Action was recorded, but this batch is no longer available. Refresh Batch History.')
      setPending(null); setComparison(null); setConfirmation(''); setAcknowledged(false); onPreview(result.data.preview)
    } catch (failure) { onError(failure instanceof Error ? failure.message : 'Resolution failed.') }
    finally { setBusy(false) }
  }
  return <Space direction="vertical" size="middle" style={{ width: '100%', minWidth: 0 }}>
    {preview.isLegacy && <>
      <Divider orientation="left">Legacy version review</Divider>
      <Alert type={preview.legacyReviewValid ? 'success' : 'warning'} showIcon message={preview.legacyReviewStatus} />
      {!preview.legacyReviewValid && <>
        <Alert type="info" showIcon message="Choose a retained final version after reviewing the finance source." description="The backend compares exact snapshots or requires matching client/month/source/component bindings and unique employee-code coverage. This records reviewed supersession; it does not invent missing creation ancestry. Known dependents and financial/delivery evidence remain protected." />
        <Select showSearch optionFilterProp="label" aria-label="Retained final version" placeholder="Choose a newer retained version" style={{ width: '100%' }} value={keeperId || undefined} disabled={busy}
          options={preview.candidates.map(candidate => ({ value: candidate.id, label: `${candidate.month} · ${candidate.fileName} · ${candidate.sheetName} · ${candidate.rowCount} rows · ${candidate.id}` }))}
          onChange={value => { setKeeperId(value); setComparison(null); setPending(null) }} />
        {preview.candidateCount > preview.candidates.length && <Alert type="info" showIcon message={`${preview.candidateCount} candidates exist; the newest ${preview.candidates.length} are listed. Enter another saved batch ID below if needed.`} />}
        <Input aria-label="Retained version batch ID" placeholder="Or enter a saved batch ID from this client" value={keeperId} disabled={busy} onChange={event => { setKeeperId(event.target.value.trim()); setComparison(null); setPending(null) }} />
        <Button disabled={busy || !/^[a-f0-9]{32}$/i.test(keeperId)} onClick={() => void compare()}>Preview version evidence</Button>
        {!preview.candidateCount && !preview.recordedParentId && <Alert type="warning" showIcon message="No newer same-month version or recorded parent was found." description="Restore verified historical/source evidence or retain this batch. A filename or name-only match cannot establish safe eligibility." />}
      </>}
      {comparison && <>
        <Alert type={comparison.eligible ? 'info' : 'error'} showIcon message={comparison.evidence} />
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Version to remove">{comparison.batchId}</Descriptions.Item><Descriptions.Item label="Retained version">{comparison.keeperBatchId}</Descriptions.Item>
          <Descriptions.Item label="Rows: old / retained">{comparison.oldRows} / {comparison.keeperRows}</Descriptions.Item><Descriptions.Item label="Changed rows">{comparison.changedRows}</Descriptions.Item>
          <Descriptions.Item label="Exact snapshot">{comparison.exactSnapshot ? 'Yes' : 'No — explicit finance-source review required'}</Descriptions.Item><Descriptions.Item label="Calculation source unchanged">{comparison.sameCalculationSource ? 'Yes' : 'No'}</Descriptions.Item>
        </Descriptions>
        {comparison.blockers.map(blocker => <Alert key={blocker} type="error" showIcon message={blocker} />)}
        {comparison.eligible && <Button disabled={busy} onClick={() => begin('supersede-legacy', '', comparison.keeperBatchId, comparison.token, preview.batchId, 'Record reviewed supersession')}>Review and confirm supersession</Button>}
      </>}
    </>}
    <Divider orientation="left">Affected and retained records</Divider>
    <Table size="small" rowKey={row => `${row.kind}:${row.id}`} dataSource={preview.objects} pagination={{ pageSize: 6, showSizeChanger: false }} scroll={{ x: 600 }} columns={[
      { title: 'Object', key: 'object', render: (_, row) => <><strong>{row.kind}</strong><div style={{ overflowWrap: 'anywhere' }}>{row.id}</div>{row.label && <div>{row.label}</div>}</> },
      { title: 'Status / reason', key: 'reason', render: (_, row) => <><strong>{row.status}</strong><div>{row.reason}</div></> },
      { title: 'Resolution', key: 'action', render: (_, row) => row.action === 'inspect-batch' ? <Button size="small" disabled={busy || !onInspectBatch} onClick={() => onInspectBatch?.(row.id)}>Inspect batch</Button>
        : row.action && <Button size="small" disabled={busy} danger={row.action.startsWith('discard')} onClick={() => begin(row.action, row.action.startsWith('discard') ? row.id : '', row.action === 'verify-recorded-parent' ? row.id : '', preview.token, row.action.startsWith('discard') ? row.id : preview.batchId, row.action === 'discard-draft' ? 'Discard unapplied draft' : row.action === 'discard-unqueued-job' ? 'Discard never-queued attempt' : 'Verify recorded parent')}>{row.action === 'discard-draft' ? 'Discard draft' : row.action === 'discard-unqueued-job' ? 'Discard unqueued job' : 'Verify parent'}</Button> },
    ]} />
    {pending && <>
      <Alert type="warning" showIcon message={pending.label} description="This is a separate confirmed action. It never removes applied masters, payroll or dispatched evidence. The backend recomputes dependencies; deleting the batch requires a new preview and a separate confirmation afterwards." />
      <label>Type this exact ID: <strong style={{ overflowWrap: 'anywhere' }}>{pending.target}</strong><Input aria-label="Cleanup action confirmation ID" autoComplete="off" value={confirmation} disabled={busy} onChange={event => setConfirmation(event.target.value)} /></label>
      <Checkbox checked={acknowledged} disabled={busy} onChange={event => setAcknowledged(event.target.checked)}>{pending.action === 'supersede-legacy' ? 'I reviewed the original finance source and differences, and approve keeping the selected final version.' : 'I reviewed the exact object and authorize only this resolution action.'}</Checkbox>
      <Space wrap><Button disabled={busy} onClick={() => setPending(null)}>Cancel action</Button><Button type="primary" danger={pending.action.startsWith('discard')} disabled={busy || confirmation !== pending.target || !acknowledged} onClick={() => void resolve()}>Confirm / retry this action</Button></Space>
    </>}
  </Space>
}
