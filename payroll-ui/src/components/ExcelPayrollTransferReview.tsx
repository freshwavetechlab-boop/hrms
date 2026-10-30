import ExcelPayrollAttendanceReview from './ExcelPayrollAttendanceReview'
import { useState } from 'react'
import { payrollComparisonMoney as money, payrollRawAmount, payrollRawDifference } from '../utils/payrollRoundingEvidence'
import DataTable from './DataTable'
import { Alert, Button, Checkbox, Input, Pagination, Select, Space, Tag } from 'antd'
import type { PayrollSetupRequest, TransferReview, TransferTemplateDecision } from '../services/excelPayrollSetupService'
import { explainedRoundingReviewChange, explainedRoundingReviewRows } from '../utils/excelPayrollRoundingReview'

export default function ExcelPayrollTransferReview({ transfer, request, disabled, onChange, payableOnlyAttendanceSupported, sourcePayableOverrideSupported }: {
  sourcePayableOverrideSupported?: boolean
  payableOnlyAttendanceSupported?: boolean
  transfer: TransferReview; request: PayrollSetupRequest; disabled: boolean; onChange: (change: Partial<PayrollSetupRequest>) => void
}) {
  const [decisions, setDecisions] = useState<TransferTemplateDecision[]>(request.templateDecisions || [])
  const [roundingPage, setRoundingPage] = useState(1)
  const amountComparisons = transfer.amountComparisons || []
  const explainedReviewRows = explainedRoundingReviewRows(request, transfer)
  const proofPageSize = 10
  const currentProofPage = Math.min(roundingPage, Math.max(1, Math.ceil(amountComparisons.length / proofPageSize)))
  const proofPageRows = amountComparisons.slice((currentProofPage - 1) * proofPageSize, currentProofPage * proofPageSize)
  const proofCounts = amountComparisons.reduce<Record<string, number>>((counts, row) => { counts[row.status] = (counts[row.status] || 0) + 1; return counts }, {})
  return <section aria-label="Transfer Batch review" style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
    <h3>Transfer from this saved batch</h3>
    <Alert type={transfer.preparationStatus === 'Blocked' ? 'warning' : 'info'} showIcon message={`Template preparation: ${transfer.preparationStatus}`} description={<><p>{transfer.parityStatus}</p><p>Source batch {transfer.sourceBatchId}; effective {transfer.effectiveFrom}; source snapshot {transfer.sourceHash}. Raw comparison precision: {transfer.tolerance}. Explained rounding needs source/core evidence and a fingerprinted review; there is no blanket currency tolerance. Configuration Apply does not mean payroll parity passed.</p></>} />
    {!!transfer.amountComparisons?.length && <section aria-label="Saved batch amount and rounding review">
      <h4>Source amounts / deterministic rounding</h4>
      <Space wrap>{Object.entries(proofCounts).map(([status, count]) => <Tag key={status}>{status}: {count}</Tag>)}</Space>
      {explainedReviewRows.length > 0 && <section aria-label="Bulk source-bound rounding review" style={{ marginBlock: 12 }}>
        <p>Source batch {transfer.sourceBatchId}; {explainedReviewRows.length} unique selected source rows have current fingerprinted explained-rounding evidence. This currency-stage review does not approve identity, formulas, attendance or policy exceptions. Refresh preview before Apply.</p>
        <Button disabled={disabled} onClick={() => onChange(explainedRoundingReviewChange(request, transfer))}>Review explained rounding for these {explainedReviewRows.length} source rows</Button>
      </section>}
      {amountComparisons.length > proofPageSize && <nav aria-label="Saved rounding evidence pages" style={{ marginBlock: 12 }}><Pagination current={currentProofPage} pageSize={proofPageSize} total={amountComparisons.length} showSizeChanger={false} showTotal={(total, range) => range[0] + '-' + range[1] + ' of ' + total + ' source rows'} onChange={setRoundingPage} /></nav>}
      <div aria-label="Current rounding evidence page" style={{ maxHeight: '60vh', overflow: 'auto', minWidth: 0 }}>
      {proofPageRows.map(row => <details key={row.rowId} open={row.status !== 'Exact at raw precision'}>
        <summary>Source row {row.sourceRow}: {row.status}</summary>
        <p>{row.reason}</p>
        <p>Source net {money(row.sourceNetDisplay)} (raw {payrollRawAmount(row.sourceNetRaw)}); core net {money(row.coreNet)}; raw delta {payrollRawDifference(row.rawNetDelta)}; displayed payable delta {payrollRawDifference(row.displayNetDelta)}.</p>
        {!!row.lines.length && <DataTable title="Rounding stage evidence" rows={row.lines} getRowId={line => line.code} exportDisabled hideSearch columns={[
          { key: 'code', label: 'Component' }, { key: 'category', label: 'Type' }, { key: 'sourceRaw', label: 'Source raw', render: line => payrollRawAmount(line.sourceRaw) }, { key: 'coreUnrounded', label: 'Core before currency rounding', render: line => payrollRawAmount(line.coreUnrounded) },
          { key: 'sourceDisplay', label: 'Source display', render: line => money(line.sourceDisplay) }, { key: 'calculated', label: 'Core result', render: line => money(line.calculated) }, { key: 'displayDelta', label: 'Displayed delta', render: line => payrollRawDifference(line.displayDelta) },
        ]} />}
        {row.status === 'Explained rounding' && <Checkbox aria-label={`Review rounding source row ${row.sourceRow}`} disabled={disabled} checked={request.roundingReviews?.[row.rowId] === row.proofHash} onChange={event => { const roundingReviews = { ...request.roundingReviews }; if (event.target.checked) roundingReviews[row.rowId] = row.proofHash; else delete roundingReviews[row.rowId]; onChange({ roundingReviews }) }}>I reviewed this source/core rounding difference for this saved batch. Repreview before Apply; this does not approve identity, attendance, policy or formula mismatches.</Checkbox>}
        {row.acknowledged && <Tag color="blue">Rounding proof reviewed; other transfer checks still apply</Tag>}
      </details>)}
      </div>
    </section>}
    <ExcelPayrollAttendanceReview sourcePayableOverrideSupported={sourcePayableOverrideSupported} payableOnlyAttendanceSupported={payableOnlyAttendanceSupported} request={request} transfer={transfer} disabled={disabled} onChange={onChange} />
    <Space direction="vertical" style={{ width: '100%' }}>
      <Checkbox disabled={disabled} checked={request.reviewedFixedInputs === true} onChange={event => onChange({ reviewedFixedInputs: event.target.checked })}>I reviewed fixed/monthly/daily input semantics where no formula is captured. Unsupported captured formulas cannot be approved as fixed cached amounts.</Checkbox>
      <Checkbox disabled={disabled} checked={request.reviewedPolicyScope === true} onChange={event => onChange({ reviewedPolicyScope: event.target.checked })}>I reviewed this client's effective-period policy assumptions. This does not activate policies or certify statutory correctness.</Checkbox>
      <details><summary>Component create/reuse plan ({transfer.components.length})</summary>{transfer.components.map(c => <p key={c.code}><Tag>{c.action}</Tag>{c.code} / {c.category} / master {c.masterId}</p>)}</details>
      <details><summary>Captured formula provenance ({transfer.formulas.length})</summary>{transfer.formulas.map(f => <p key={`${f.rowId}:${f.code}`}><Tag color={f.status === 'Blocked' ? 'error' : 'blue'}>{f.status}</Tag>Source row {f.sourceRow}, column {f.sourceColumn == null ? 'unmapped' : f.sourceColumn + 1}, {f.code}: {f.reason}<small style={{ display: 'block', overflowWrap: 'anywhere' }}>Cell snapshot {f.sourceHash}</small></p>)}</details>
      {transfer.templates.map(t => <div key={t.variantKey}>
        <strong>Template variant {t.variantKey} ({t.rowIds.length} source rows): {t.action}</strong>
        <Select aria-label={`Transfer template ${t.variantKey}`} disabled={disabled} style={{ width: '100%' }} placeholder="Review existing templates and choose reuse or a new variant"
          value={(() => { const d = decisions.find(d => d.variantKey === t.variantKey); return d?.keepExisting ? '__keep__' : d?.existingTemplateId || (d?.createVariant ? '__create__' : undefined) })()}
          options={[{ value: '__keep__', label: 'Keep current assignments unchanged — record decision, Excel parity unresolved', disabled: (t.existingResults?.length || 0) !== t.rowIds.length }, ...t.candidates.filter(c => c.exact || c.configurationExact && c.scopeStatus !== 'Different policy/basis').map(c => ({ value: c.id, label: `${c.exact ? 'Reuse unchanged' : 'Review applicability before reuse'}: ${c.name} (${c.id})` })), ...(!t.candidates.some(c => c.exact) ? [{ value: '__create__', label: 'Create this reviewed variant — no exact existing template found' }] : [])]}
          onChange={value => setDecisions(current => [...current.filter(d => d.variantKey !== t.variantKey), { variantKey: t.variantKey, existingTemplateId: ['__create__', '__keep__'].includes(value) ? '' : value, createVariant: value === '__create__', keepExisting: value === '__keep__' }])} />
        {request.templateScopeReviewVersion === 1 && <div style={{paddingBlock:8}}>
          <Select aria-label={'Template scope action '+t.variantKey} disabled={disabled || !decisions.some(d=>d.variantKey===t.variantKey)} style={{width:'100%'}} value={decisions.find(d=>d.variantKey===t.variantKey)?.scopeAction || undefined} placeholder="Review applicability of the selected decision" options={[{value:'AttestLegacyScope',label:'I reviewed legacy applicability for this selected cohort'},{value:'ExtendReviewedScope',label:'I reviewed an extension of the recorded scope'},{value:'DifferentScope',label:'This is a distinct reviewed scope; create a separate variant'}]} onChange={scopeAction=>setDecisions(current=>current.map(d=>d.variantKey===t.variantKey?{...d,scopeAction}:d))}/>
          <Input aria-label={'Template scope reason '+t.variantKey} disabled={disabled || !decisions.some(d=>d.variantKey===t.variantKey)} maxLength={500} placeholder="Explain a distinct scoped variant when the arithmetic rules already exist" value={decisions.find(d=>d.variantKey===t.variantKey)?.scopeReason || ''} onChange={e=>setDecisions(current=>current.map(d=>d.variantKey===t.variantKey?{...d,scopeReason:e.target.value}:d))}/>
          {t.candidates.map(c=><div key={'scope:'+c.id}><p><Tag>{c.scopeStatus || 'Unknown applicability'}</Tag>{c.name}: {c.scopeReason || 'Review scope separately from formula equality.'}</p><details><summary>Recorded applicability evidence: {c.name}</summary>{c.recordedScopes?.length?c.recordedScopes.map((record,index)=><div key={index} style={{paddingBlock:8,overflowWrap:'anywhere'}}><p>Effective {record.effectiveFrom} through {record.scope.openEnded?'explicitly open-ended':record.scope.effectiveTo}; {record.scope.policyBasis}: {record.policyKey}</p><p>Locations: {record.scope.locationMode}; IDs {record.scope.workLocationIds.join(', ')||'none listed'}; new names {record.scope.newLocationNames.join('; ')||'none listed'}. Categories: {record.scope.categoryKind} {record.scope.categories.join('; ')}</p><p>Divisor: {record.scope.divisorMeaning}; payable: {record.scope.payableMeaning}. Source batch {record.sourceBatchId}; {record.sourceMonth}</p></div>):<p>No recorded applicability is available. Formula equality alone does not certify locations, category, policy or effective dates.</p>}</details></div>)} 
        </div>}
        <p>Keep existing preserves each matched employee's current salary assignment, attendance and adjustments. It does not approve Excel differences. Use Excel-derived creates/reuses a reviewed effective variant; shared templates stay unchanged. Edit and repreview at any time.</p>
        {(t.existingResults || []).map(current => <details key={current.rowId}><summary>Existing draft versus Excel — source row {transfer.formulas.find(f => f.rowId === current.rowId)?.sourceRow ?? current.rowId}</summary><p>{current.coverage}</p><p>Current template {current.templateId}; draft net {current.net ?? 'unavailable'}; Excel net {current.sourceNet}; difference {current.variance ?? 'unavailable'}. {current.complete ? 'Draft arithmetic only — not Employee payroll parity.' : 'Existing draft comparison is incomplete.'}</p>{current.lines.map(line => <p key={line.code}>{line.code}: existing draft {line.calculatedAmount}; Excel {line.sourceAmount ?? 'unmapped'}</p>)}</details>)}
        {t.candidates.map(c => <details key={'details:'+c.id}><summary>Side-by-side: {c.name} ({c.id})</summary><div style={{ maxHeight: 280, overflow: 'auto' }}><table><thead><tr><th>Component / field</th><th>Existing template</th><th>Excel-derived proposal</th></tr></thead><tbody>{(c.details || []).map((d,index) => <tr key={index}><td>{d.component} / {d.field}</td><td style={{ overflowWrap: 'anywhere' }}>{d.existing || '(blank)'}</td><td style={{ overflowWrap: 'anywhere' }}>{d.excelDerived || '(blank)'}</td></tr>)}</tbody></table></div></details>)}
        <details><summary>Compare all active client templates ({t.candidates.length})</summary>{t.candidates.length ? t.candidates.map(c => <p key={c.id}><Tag color={c.exact ? 'green' : 'orange'}>{c.exact ? 'Exact rules and reviewed applicability' : c.configurationExact ? 'Rules match; applicability review required' : 'Different configuration'}</Tag>{c.name} ({c.id}){c.differences.length > 0 && <span>: {c.differences.join('; ')}</span>}</p>) : <p>No active client template was returned by the current preview.</p>}</details>
      </div>)}
      <Button disabled={disabled || !transfer.templates.length || transfer.templates.some(t => !decisions.some(d => d.variantKey === t.variantKey))} onClick={() => onChange({ templateDecisions: decisions.filter(d => transfer.templates.some(t => t.variantKey === d.variantKey)) })}>Use reviewed template decisions, then refresh preview</Button>
    </Space>
  </section>
}
