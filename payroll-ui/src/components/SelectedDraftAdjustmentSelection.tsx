import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox } from 'antd'
import type { PayrollAdjustment } from '../types/payroll'
import type { CreatePayRunRequest } from '../services/payrollService'
import { reviewSelectedPayRunDraftSource, selectedDraftSourcePreviewError, type SelectedDraftSourceReviewPreview } from '../services/selectedPayRunService'
import { selectedDraftAdjustmentIssue, type SelectedDraftSourceReviewChoice } from '../utils/selectedPayRunDraft'
import DataTable from './DataTable'

type Props = { rows: PayrollAdjustment[]; selectedIds: number[]; disabled: boolean; onSelect: (ids: number[]) => void; sourceRequest?: CreatePayRunRequest | null; sourceRequestKey?: string; onSourceReviewChange?: (choice: SelectedDraftSourceReviewChoice) => void }
const amount = (value: number) => `Rs ${value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
export default function SelectedDraftAdjustmentSelection({ rows, selectedIds, disabled, onSelect, sourceRequest, sourceRequestKey, onSourceReviewChange }: Props) {
  if (!rows.length) return null
  const eligible = rows.filter(row => !selectedDraftAdjustmentIssue(row))
  return <section aria-label="Selected draft adjustment review">
    <h3>Existing approved period adjustments</h3>
    <p>Explicitly select the records for these employees and this month. The server verifies their saved Excel source before creating a held draft and reviews it again before calculation.</p>
    <div className="roster-actions"><Button disabled={disabled || !eligible.length} onClick={() => onSelect(eligible.map(row => row.id))}>Select all listed source adjustments</Button><Button disabled={disabled || !selectedIds.length} onClick={() => onSelect([])}>Clear adjustment selection</Button><span>{selectedIds.length} of {rows.length} selected</span></div>
    <DataTable title="Adjustments for selected employees" rows={rows} getRowId={row => row.id} pageSizeOptions={[10, 25, 50]} columns={[
      { key: 'selected', label: 'Include', sortable: false, filterable: false, render: row => <Checkbox aria-label={`Include adjustment ${row.id}`} disabled={disabled || !!selectedDraftAdjustmentIssue(row)} checked={selectedIds.includes(row.id)} onChange={event => onSelect(event.target.checked ? [...selectedIds.filter(id => id !== row.id), row.id] : selectedIds.filter(id => id !== row.id))} />, exportValue: row => selectedIds.includes(row.id) ? 'Yes' : 'No' },
      { key: 'id', label: 'Adjustment ID' }, { key: 'employeeCode', label: 'Employee code' }, { key: 'componentCode', label: 'Component' }, { key: 'adjustmentType', label: 'Category' },
      { key: 'amount', label: 'Amount', render: row => amount(row.amount) }, { key: 'taxable', label: 'Taxable', value: row => row.taxable ? 'Yes' : 'No' },
      { key: 'sourceReview', label: 'Source review', value: row => selectedDraftAdjustmentIssue(row) || 'Saved-source proof rechecked by server' },
    ]} />
    {sourceRequest && sourceRequest.includedEmployeeIds?.length === 1 && sourceRequest.adjustmentIds?.length === 1 && sourceRequestKey && onSourceReviewChange && <SourceReview key={sourceRequestKey} request={sourceRequest} requestKey={sourceRequestKey} adjustment={rows.find(row => row.id === sourceRequest.adjustmentIds?.[0])} disabled={disabled} onChange={onSourceReviewChange} />}
  </section>
}

function SourceReview({ request, requestKey, adjustment, disabled, onChange }: { request: CreatePayRunRequest; requestKey: string; adjustment: PayrollAdjustment | undefined; disabled: boolean; onChange: (choice: SelectedDraftSourceReviewChoice) => void }) {
  const [preview, setPreview] = useState<SelectedDraftSourceReviewPreview | null>(null), [error, setError] = useState(''), [pending, setPending] = useState(false), [confirmed, setConfirmed] = useState(false)
  const lifecycle = useRef({ active: true }), sequence = useRef(0), inFlight = useRef(false), changed = useRef(onChange)
  useEffect(() => { changed.current = onChange }, [onChange])
  useEffect(() => {
    const lifetime = { active: true }
    lifecycle.current = lifetime
    changed.current({ requestKey, started: false })
    return () => { lifetime.active = false; inFlight.current = false }
  }, [requestKey])
  const review = async () => {
    if (disabled || inFlight.current) return
    const current = ++sequence.current, lifetime = lifecycle.current
    inFlight.current = true; setPending(true); setPreview(null); setConfirmed(false); setError(''); changed.current({ requestKey, started: true })
    try {
      const result = await reviewSelectedPayRunDraftSource(request)
      if (!lifetime.active || sequence.current !== current) return
      if (!result.ok || !result.data) throw new Error(result.error || 'Unable to review the current saved source. No draft was created.')
      const issue = selectedDraftSourcePreviewError(result.data, request, adjustment)
      if (issue) throw new Error(issue)
      setPreview(result.data)
    } catch (failure) {
      if (!lifetime.active || sequence.current !== current) return
      setPreview(null); setConfirmed(false); changed.current({ requestKey, started: true }); setError(failure instanceof Error ? failure.message : 'Unable to review the current saved source.')
    } finally {
      if (lifetime.active && sequence.current === current) { inFlight.current = false; setPending(false) }
    }
  }
  const clear = () => {
    if (inFlight.current) return
    sequence.current++; setPreview(null); setConfirmed(false); setError(''); changed.current({ requestKey, started: false })
  }
  return <section aria-label="Current saved source review">
    <h3>Current source evidence for an older Applied receipt</h3>
    <p>Review this one selected employee and existing adjustment without changing the saved batch or historical receipt. Version 1 transfers keep their normal source-review path. No payroll is created by this review.</p>
    <div className="roster-actions"><Button disabled={disabled || pending || !adjustment} loading={pending} onClick={() => void review()}>Review current saved source</Button><Button disabled={disabled || pending || !preview && !error} onClick={clear}>Clear current-source review</Button></div>
    {error && <Alert type="error" showIcon message={error} />}
    {preview && <>
      {preview.blockers.map((blocker, index) => <Alert key={index} type="error" showIcon message={blocker} />)}
      {preview.canConfirm && <>
        <Alert type="info" showIcon message="Current source and core calculation reviewed separately from the historical receipt" description={`Source actual worked days: ${preview.sourceActualWorkedMeaning === 'NotMapped' ? 'Not mapped; this review does not certify source worked-day parity.' : 'Mapped and reviewed.'} Native attendance is shown separately below. No source or Master identifier is changed.`} />
        <DataTable title="Reviewed existing source adjustment" rows={preview.reviews} getRowId={row => row.adjustmentId} hideSearch exportDisabled columns={[
          { key: 'adjustmentId', label: 'Adjustment ID' }, { key: 'employeeId', label: 'Employee Master' }, { key: 'componentCode', label: 'Component' }, { key: 'category', label: 'Category' }, { key: 'amount', label: 'Amount', render: row => amount(row.amount) }, { key: 'taxable', label: 'Taxable', value: row => row.taxable ? 'Yes' : 'No' },
          { key: 'sourceBatchId', label: 'Saved batch', width: 220, wrap: true }, { key: 'sourceImportId', label: 'Original Apply receipt', width: 220, wrap: true }, { key: 'sourceRow', label: 'Source row' }, { key: 'sourceHash', label: 'Source fingerprint', width: 180, render: row => <span title={row.sourceHash}>{row.sourceHash.slice(0, 12)}…</span> },
        ]} />
        {preview.nativeAttendance && <dl aria-label="Reviewed native attendance">
          <dt>Native period / cycle</dt><dd>{preview.nativeAttendance.payPeriod} / {preview.nativeAttendance.cycleStart || 'Recorded monthly cycle'} – {preview.nativeAttendance.cycleEnd || 'Recorded monthly cycle'}</dd>
          <dt>Native working / actual present / payable days</dt><dd>{preview.nativeAttendance.workingDays} / {preview.nativeAttendance.presentDays === null ? 'Unknown' : preview.nativeAttendance.presentDays} / {preview.nativeAttendance.payableDays}</dd>
          <dt>Native attendance basis</dt><dd>{preview.nativeAttendance.sourceKind}: {preview.nativeAttendance.explanation}</dd>
        </dl>}
        <DataTable title="Current source and core financial evidence" rows={preview.lines} getRowId={row => row.code} exportDisabled columns={[
          { key: 'code', label: 'Component', width: 140 }, { key: 'category', label: 'Category', width: 130 }, { key: 'componentRole', label: 'Role', width: 170 }, { key: 'statutoryType', label: 'Statutory type', width: 140 }, { key: 'taxable', label: 'Taxable', value: row => row.taxable ? 'Yes' : 'No' }, { key: 'proRata', label: 'Pro-rata', value: row => row.proRata ? 'Yes' : 'No' },
          { key: 'sourceRaw', label: 'Source raw', value: row => String(row.sourceRaw) }, { key: 'sourceDisplay', label: 'Source display', render: row => amount(row.sourceDisplay) }, { key: 'coreAmount', label: 'Core amount', render: row => amount(row.coreAmount) }, { key: 'coreUnrounded', label: 'Core unrounded', value: row => row.coreUnrounded === null ? 'Not supplied' : String(row.coreUnrounded) },
          { key: 'sourceFormula', label: 'Source formula', width: 260, wrap: true }, { key: 'nativeFormula', label: 'Native formula', width: 260, wrap: true }, { key: 'formulaStatus', label: 'Comparison evidence', width: 260, wrap: true },
        ]} />
        <p>Source net {amount(preview.sourceNet!)}; core net {amount(preview.coreNet!)}; raw difference {String(preview.coreNet! - preview.sourceNet!)}.</p>
        <p>Current review fingerprint: <span title={preview.fingerprint}>{preview.fingerprint.slice(0, 16)}…</span></p>
        <Checkbox disabled={disabled || pending} checked={confirmed} onChange={event => {
          const checked = event.target.checked && !selectedDraftSourcePreviewError(preview, request, adjustment)
          setConfirmed(checked); changed.current({ requestKey, started: true, ...(checked ? { acknowledgement: { expectedFingerprint: preview.fingerprint, confirmed: true } } : {}) })
        }}>I reviewed these source amounts, current literal/monthly input meanings, effective formulas, classifications, tax treatment, native attendance and the stated worked-day limitation for this exact selected draft. The historical receipt remains unchanged.</Checkbox>
      </>}
    </>}
  </section>
}
