import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Descriptions, Drawer, Select, Space, Tag, Tooltip } from 'antd'
import DataTable from './DataTable'
import { getExcelPayslipBatch, type ExcelPayslipBatch, type ExcelPayslipSummary } from '../services/excelPayslipService'
import { applyExcelPayslipConsolidation, getExcelPayslipConsolidation, previewExcelPayslipConsolidation, undoExcelPayslipConsolidation, type ExcelPayslipConsolidationPreview, type ExcelPayslipConsolidationStatus } from '../services/excelPayslipConsolidationService'

const label = (batch: ExcelPayslipSummary) => `${batch.month} · ${batch.sourceFileName} · ${batch.rowCount} rows · ${new Date(batch.createdAtUtc).toLocaleString()} · ${batch.id}`

export default function ExcelPayslipConsolidation({ open, clientId, batches, onClose, onChanged }: {
  open: boolean; clientId: number; batches: ExcelPayslipSummary[]; onClose: () => void; onChanged: (canonicalBatchId: string) => void
}) {
  const candidates = useMemo(() => batches.filter(batch => !batch.isConsolidatedSource).sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc) || b.id.localeCompare(a.id)), [batches])
  const [chosenCanonical, setChosenCanonical] = useState(''), [sourceBatchIds, setSourceBatchIds] = useState<string[]>([])
  const canonicalBatchId = candidates.some(batch => batch.id === chosenCanonical) ? chosenCanonical : candidates[0]?.id || ''
  const canonical = candidates.find(batch => batch.id === canonicalBatchId)
  const sources = candidates.filter(batch => batch.id !== canonicalBatchId && batch.month === canonical?.month)
  const [preview, setPreview] = useState<ExcelPayslipConsolidationPreview | null>(null)
  const [acknowledged, setAcknowledged] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const sequence = useRef(0), alive = useRef(true), pending = useRef<{ requestId: string; token: string } | null>(null)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const clearReview = () => { sequence.current++; setPreview(null); setAcknowledged(false); setError(''); pending.current = null }
  const close = () => { if (busy) return; setPreview(null); setAcknowledged(false); setError(''); onClose() }
  const inspect = async () => {
    if (!canonicalBatchId || !sourceBatchIds.length || busy) return
    const requestSequence = ++sequence.current
    setBusy(true); setPreview(null); setAcknowledged(false); setError(''); pending.current = null
    try {
      const result = await previewExcelPayslipConsolidation(clientId, { canonicalBatchId, sourceBatchIds })
      if (!alive.current || sequence.current !== requestSequence) return
      if (!result.ok || !result.data) throw new Error(result.error || 'Unable to compare these saved versions.')
      setPreview(result.data); pending.current = { requestId: crypto.randomUUID(), token: result.data.token }
    } catch (failure) { if (alive.current && sequence.current === requestSequence) setError(failure instanceof Error ? failure.message : 'Comparison failed.') }
    finally { if (alive.current && sequence.current === requestSequence) setBusy(false) }
  }
  const apply = async () => {
    if (!preview?.canConsolidate || !acknowledged || !pending.current || busy) return
    const requestSequence = ++sequence.current
    setBusy(true); setError('')
    try {
      const result = await applyExcelPayslipConsolidation(clientId, { requestId: pending.current.requestId, token: pending.current.token, canonicalBatchId: preview.canonicalBatchId, sourceBatchIds: preview.sourceBatchIds, acknowledged })
      if (!alive.current || sequence.current !== requestSequence) return
      if (!result.ok || !result.data?.active) throw new Error(result.error || 'Consolidation was not confirmed. Check its saved result before retrying.')
      pending.current = null; setPreview(null); setAcknowledged(false); onChanged(result.data.canonicalBatchId); onClose()
    } catch (failure) { if (alive.current && sequence.current === requestSequence) setError(failure instanceof Error ? failure.message : 'Consolidation failed. The same request ID is retained for an explicit retry.') }
    finally { if (alive.current && sequence.current === requestSequence) setBusy(false) }
  }
  return <Drawer title="Consolidate saved payslip versions" open={open} width="min(900px, 96vw)" onClose={close} closable={!busy} maskClosable={!busy}
    footer={<Space wrap><Button disabled={busy} onClick={close}>Cancel</Button><Button aria-label="Preview consolidation" loading={busy} disabled={busy || !canonical || !sourceBatchIds.length} onClick={() => void inspect()}>Preview consolidation</Button><Button aria-label="Consolidate into one working batch" type="primary" loading={busy} disabled={busy || !preview?.canConsolidate || !acknowledged} onClick={() => void apply()}>Consolidate into one working batch</Button></Space>}>
    <Space direction="vertical" size="middle" style={{ width: '100%', minWidth: 0 }}>
      <Alert type="info" showIcon message="Keep one working batch and its original formulas" description="Choose the full batch to retain. An earlier subset can be linked when every one of its source rows matches employee code/name and all financial amounts in the full batch. Earlier saved versions remain as read-only history for their mail and payroll references. No employee, attendance, formula, sent record or payroll run is rewritten, and no email is sent." />
      {error && <Alert type="error" showIcon message={error} />}
      <label>Working batch to retain<Select aria-label="Consolidation working batch" showSearch optionFilterProp="label" value={canonicalBatchId || undefined} disabled={busy} style={{ width: '100%' }} options={candidates.map(batch => ({ value: batch.id, label: label(batch) }))} onChange={id => { setChosenCanonical(id); setSourceBatchIds([]); clearReview() }} /></label>
      <label>Earlier saved versions to consolidate<Select aria-label="Consolidation source versions" mode="multiple" showSearch optionFilterProp="label" value={sourceBatchIds} disabled={busy || !canonical} style={{ width: '100%' }} options={sources.map(batch => ({ value: batch.id, label: label(batch) }))} onChange={ids => { setSourceBatchIds(ids); clearReview() }} /></label>
      {canonical && <Tag color="blue">{canonical.rowCount} rows in the retained working batch · {canonical.month}</Tag>}
      {preview && <>
        <Alert type={preview.canConsolidate ? 'success' : 'error'} showIcon message={preview.canConsolidate ? `${preview.canonicalRows} rows in one working batch` : 'These versions need review before consolidation'} description={preview.evidence} />
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Retained batch"><span style={{ overflowWrap: 'anywhere' }}>{preview.canonicalBatchId}</span></Descriptions.Item><Descriptions.Item label="Working-batch rows">{preview.canonicalRows}</Descriptions.Item><Descriptions.Item label="Source-row matches reviewed">{preview.matchedRows}</Descriptions.Item>
          <Descriptions.Item label="Formulas and captured source">Retained unchanged in the selected working batch; previous source versions remain linked to their original history.</Descriptions.Item>
        </Descriptions>
        {preview.blockers.map(blocker => <Alert key={blocker} type="error" showIcon message={blocker} />)}
        <DataTable rows={preview.sources} getRowId={row => row.batchId} title="Saved versions reviewed" exportDisabled columns={[
          { key: 'batchId', label: 'Source batch', wrap: true }, { key: 'rows', label: 'Rows' }, { key: 'matchedRows', label: 'Matched rows' },
          { key: 'identityMatch', label: 'Source identity', value: row => row.identityMatch ? 'Matched' : 'Review' }, { key: 'financialMatch', label: 'Every financial component', value: row => row.financialMatch ? 'Matched' : 'Review' },
          { key: 'formulaSourceUnchanged', label: 'Captured calculation source', value: row => !row.formulaSourcePresent ? 'Source unavailable' : row.formulaSourceUnchanged ? 'Identical captured source' : 'Captured source differs; working formulas unchanged', wrap: true },
        ]} />
        {!!preview.differences.length && <DataTable rows={preview.differences} getRowId={(row, index) => `${row.sourceBatchId}:${row.sourceRow}:${row.field}:${index}`} title="Differences requiring review" exportDisabled columns={[{ key: 'sourceBatchId', label: 'Source batch', wrap: true }, { key: 'sourceRow', label: 'Source row' }, { key: 'field', label: 'Field' }, { key: 'reason', label: 'Reason', wrap: true }]} />}
        {preview.canConsolidate && <Checkbox checked={acknowledged} disabled={busy} onChange={event => setAcknowledged(event.target.checked)}>I reviewed this exact client, month and saved versions. Keep the working formulas and all earlier mail/payroll history.</Checkbox>}
      </>}
    </Space>
  </Drawer>
}

export function ExcelPayslipConsolidationHistory({ clientId, batchId, rows, canManage, onChanged, onRetainedSource }: {
  clientId: number; batchId: string; rows: ExcelPayslipBatch['rows']; canManage: boolean; onChanged: (canonicalBatchId: string, restored?: boolean) => void; onRetainedSource: (batchId: string) => void
}) {
  const [status, setStatus] = useState<ExcelPayslipConsolidationStatus | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const pendingUndo = useRef<{ requestId: string; token: string } | null>(null)
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    let active = true
    void getExcelPayslipConsolidation(clientId, batchId).then(result => {
      if (!active) return
      if (result.ok) { setStatus(result.data); setError('') }
      else setError(result.error || 'Unable to verify retained version history.')
    })
    return () => { active = false; alive.current = false }
  }, [clientId, batchId])
  const undo = async () => {
    if (!status?.active || !status.canUndo || busy) return
    pendingUndo.current ||= { requestId: crypto.randomUUID(), token: status.token }
    setBusy(true); setError('')
    try {
      const result = await undoExcelPayslipConsolidation(clientId, status.groupId, pendingUndo.current)
      if (!alive.current) return
      if (!result.ok || !result.data || result.data.active) throw new Error(result.error || 'Restoring the versions was not confirmed. Refresh the saved history before retrying.')
      pendingUndo.current = null; setStatus(result.data); onChanged(status.canonicalBatchId, true)
    } catch (failure) { if (alive.current) setError(failure instanceof Error ? failure.message : 'Unable to restore the separate versions.') }
    finally { if (alive.current) setBusy(false) }
  }
  if (error && !status) return <Alert type="warning" showIcon message={error} />
  if (!status?.active) return status?.warnings?.length ? <Space direction="vertical" style={{ width: '100%', marginBottom: 10 }}>{status.warnings.map(warning => <Alert key={warning} type="warning" showIcon message={warning} />)}</Space> : null
  const byId = new Map(rows.map(row => [row.id, row]))
  const historicalRows = status.historicalDelivery.map(item => ({ ...item, sourceRow: byId.get(item.rowId)?.sourceRow ?? '', employeeCode: byId.get(item.rowId)?.employeeCode ?? '', employeeName: byId.get(item.rowId)?.employeeName ?? 'Historical row requires review' }))
  const historyExplanation = 'The saved formulas remain unchanged. Earlier versions retain their original mail and payroll evidence; prior-version mail is shown separately and is not a new send from this batch.'
  return <Space direction="vertical" className="excel-payslip-consolidation-history" style={{ width: '100%' }}>
    <Alert className="excel-payslip-consolidation-notice" type={status.warnings?.length ? 'warning' : 'success'} showIcon message={<Tooltip title={historyExplanation}><span>{status.warnings?.length ? `Consolidation needs review · ${status.canonicalRows} source rows` : `Consolidated working batch · ${status.canonicalRows} rows`}</span></Tooltip>} />
    {error && <Alert type="error" showIcon message={error} />}
    {status.warnings?.map(warning => <Alert key={warning} type="warning" showIcon message={warning} />)}
    <Collapse size="small" style={{ width: '100%', maxHeight: '45vh', overflow: 'auto' }}><Collapse.Panel key="retained-history" header={`Retained source history (${status.sourceBatchIds.length} versions · ${status.historicalDelivery.length} delivery records)`}><Space direction="vertical" style={{ width: '100%' }}>
      <p className="excel-payslip-muted">{historyExplanation}</p>
      <Space wrap>{status.sourceBatchIds.map(id => <Button key={id} size="small" onClick={() => onRetainedSource(id)}>View retained version {id.slice(0, 8)}</Button>)}</Space>
      <DataTable rows={historicalRows} getRowId={(row, index) => `${row.sourceBatchId}:${row.sourceRowId}:${row.mode}:${index}`} title="Mail history from retained versions" exportDisabled columns={[
        { key: 'sourceRow', label: 'Source row' }, { key: 'employeeCode', label: 'Employee code' }, { key: 'employeeName', label: 'Employee' }, { key: 'sourceBatchId', label: 'Sent from source version', wrap: true }, { key: 'status', label: 'Historical status', value: row => `${row.status} · earlier source version` }, { key: 'mode', label: 'Mode' },
        { key: 'sentAtUtc', label: 'Sent at', value: row => row.sentAtUtc ? new Date(row.sentAtUtc).toLocaleString() : '' }, { key: 'message', label: 'Evidence', wrap: true },
      ]} />
      {canManage && status.canUndo && <Button aria-label="Undo consolidation" disabled={busy} loading={busy} onClick={() => void undo()}>Undo consolidation</Button>}
    </Space></Collapse.Panel></Collapse>
  </Space>
}

export function ExcelPayslipRetainedSource({ clientId, batchId, onClose }: { clientId: number; batchId: string; onClose: () => void }) {
  const [batch, setBatch] = useState<ExcelPayslipBatch | null>(null), [error, setError] = useState('')
  useEffect(() => {
    let active = true
    void getExcelPayslipBatch(clientId, batchId).then(result => { if (!active) return; if (result.ok && result.data) setBatch(result.data); else setError(result.error || 'Retained source unavailable.') })
    return () => { active = false }
  }, [clientId, batchId])
  const total = (items: { amount: number }[]) => items.reduce((sum, item) => sum + item.amount, 0)
  return <Drawer title="Retained source version · read only" open width="min(1000px, 96vw)" onClose={onClose} footer={<Button onClick={onClose}>Close retained version</Button>}>
    <Alert type="info" showIcon message="Original source retained for mail and payroll history" description="This version is not another working employee batch. Its captured formulas, delivery references and applied configuration have not been rewritten." />
    {error && <Alert type="error" showIcon message={error} />}
    {batch && <><Descriptions size="small" bordered column={1}><Descriptions.Item label="Batch ID">{batch.id}</Descriptions.Item><Descriptions.Item label="Source">{batch.sourceFileName}</Descriptions.Item><Descriptions.Item label="Period / rows">{batch.month} · {batch.rows.length}</Descriptions.Item></Descriptions>
      <DataTable rows={batch.rows} getRowId={row => row.id} title="Retained financial source rows" exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeCode', label: 'Employee code' }, { key: 'employeeName', label: 'Employee' }, { key: 'earnings', label: 'Earnings', value: row => total(row.earnings) }, { key: 'deductions', label: 'Deductions', value: row => total(row.deductions) }, { key: 'netPay', label: 'Net pay' }]} /></>}
  </Drawer>
}
