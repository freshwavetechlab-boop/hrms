import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, Empty, Space, Tag } from 'antd'
import { ArrowRightOutlined, CheckCircleOutlined, ExclamationCircleOutlined, SafetyCertificateOutlined, StopOutlined } from '@ant-design/icons'
import type {
  EmployeeImportCandidateEmployee,
  EmployeeImportDecision,
  EmployeeImportIdentityEvidence,
  EmployeeImportPreflight,
  EmployeeImportReviewChange,
  EmployeeImportReviewRow
} from '../services/settingsService'
import type { BulkImportOperation } from '../utils/smartBulkImport'
import './EmployeeImportReviewModal.css'

type DecisionAction = EmployeeImportDecision['action']
type FieldChoice = 'keepExisting' | 'useImported'

type Props = {
  open: boolean
  fileName: string
  operation: BulkImportOperation
  mappedSalaryImport?: boolean
  review: EmployeeImportPreflight | null
  busy?: boolean
  onCancel: () => void
  onConfirm: (decisions: EmployeeImportDecision[]) => void
}

export default function EmployeeImportReviewModal(p: Props) {
  const rows = useMemo(() => normalizeRows(p.review?.rows), [p.review?.rows])
  const reviewRows = useMemo(() => rows.filter(rowNeedsReview), [rows])
  const [decisions, setDecisions] = useState<Record<string, DecisionAction>>({})
  const [targets, setTargets] = useState<Record<string, number>>({})
  const [fieldChoices, setFieldChoices] = useState<Record<string, Record<string, FieldChoice>>>({})
  const [selectedRows, setSelectedRows] = useState<string[]>([])
  const [acknowledged, setAcknowledged] = useState(false)

  useEffect(() => {
    if (!p.open) return
    setDecisions(Object.fromEntries(reviewRows.filter(row => isConflict(row) && !canResolve(row)).map(row => [rowKey(row), 'skip' as DecisionAction])))
    setTargets(Object.fromEntries(reviewRows.flatMap(row => !isConflict(row) && positiveNumber(row.matchedEmployeeId) ? [[rowKey(row), Number(row.matchedEmployeeId)]] : [])))
    setFieldChoices({})
    setSelectedRows(reviewRows.map(rowKey))
    setAcknowledged(false)
  }, [p.open, p.review?.reviewToken])

  const pendingRows = reviewRows.filter(row => rowIsPending(row, decisions[rowKey(row)], targets[rowKey(row)], fieldChoices[rowKey(row)], p.operation))
  const skipped = reviewRows.filter(row => decisions[rowKey(row)] === 'skip').length
  const accepted = reviewRows.length - skipped - pendingRows.length
  const automaticRows = Math.max(0, Number(p.review?.totalRows || rows.length) - reviewRows.length)
  const errors = cleanStrings(p.review?.errors)
  const hasRowConflict = rows.some(isConflict)
  const globallyBlocked = p.review?.canImport === false && !hasRowConflict
  const hasImportableRows = accepted > 0 || automaticRows > 0
  const canContinue = Boolean(p.review?.reviewToken)
    && !globallyBlocked
    && !errors.length
    && !pendingRows.length
    && acknowledged
    && hasImportableRows
  const selectedReviewRows = reviewRows.filter(row => selectedRows.includes(rowKey(row)))
  const bulkDisabled = Boolean(p.busy || globallyBlocked || errors.length || !p.review?.reviewToken)
  const targetOccurrences = new Map<number, number>()
  for (const row of rows) {
    const targetId = resolvedTargetId(row, targets[rowKey(row)])
    if (targetId && decisions[rowKey(row)] !== 'skip') targetOccurrences.set(targetId, (targetOccurrences.get(targetId) ?? 0) + 1)
  }
  const pendingKeys = new Set(pendingRows.map(rowKey))
  const selectedPending = selectedReviewRows.filter(row => pendingKeys.has(rowKey(row)))
  const bulkUpdates = p.operation === 'insert' ? [] : selectedPending.filter(row => {
    const key = rowKey(row), targetId = resolvedTargetId(row, targets[key])
    return decisions[key] !== 'insert' && targetOccurrences.get(targetId) === 1 && safeForBulkImportedChoice(row, targetId)
  })
  const bulkKeepRows = selectedReviewRows.filter(row => decisions[rowKey(row)] !== 'skip'
    && resolvedTargetId(row, targets[rowKey(row)]) > 0 && criticalChanges(row, targets[rowKey(row)]).length > 0)
  const individualReviewCount = selectedPending.length - bulkUpdates.length
  const salaryUpdateMode = p.mappedSalaryImport && p.operation === 'update'
  const matchedSalaryRows = salaryUpdateMode ? selectedReviewRows.filter(row => {
    const key = rowKey(row), targetId = resolvedTargetId(row, targets[key])
    if (decisions[key] === 'skip' || targetOccurrences.get(targetId) !== 1 || !isExactSalaryUpdate(row, targetId)) return false
    const fields = changesFor(row, targetId)
    // Preserve an individual reviewer's decision to keep a current amount.
    return !fields.some(change => fieldChoices[key]?.[change.field] === 'keepExisting')
      && (decisions[key] !== 'update' || fields.some(change => !fieldChoices[key]?.[change.field]))
  }) : []

  const choose = (row: EmployeeImportReviewRow, action: DecisionAction) => {
    const key = rowKey(row)
    if (isConflict(row) && !canResolve(row) && action !== 'skip') return
    if (action === 'update' && resolvedTargetId(row, targets[key]) <= 0) return
    setDecisions(current => ({ ...current, [key]: action }))
  }

  const chooseTarget = (row: EmployeeImportReviewRow, employeeId: number) => {
    const key = rowKey(row)
    if (!candidateEmployees(row).some(candidate => candidate.employeeId === employeeId)) return
    setTargets(current => ({ ...current, [key]: employeeId }))
    setFieldChoices(current => {
      const next = { ...current }
      delete next[key]
      return next
    })
    setDecisions(current => ({ ...current, [key]: 'update' }))
  }

  const chooseField = (row: EmployeeImportReviewRow, field: string, choice: FieldChoice) => {
    const key = rowKey(row)
    setFieldChoices(current => ({ ...current, [key]: { ...(current[key] ?? {}), [field]: choice } }))
  }

  const selectAll = () => setSelectedRows(reviewRows.map(rowKey))
  const deselectAll = () => setSelectedRows([])
  const toggleRow = (row: EmployeeImportReviewRow, checked: boolean) => {
    const key = rowKey(row)
    setSelectedRows(current => checked ? Array.from(new Set([...current, key])) : current.filter(value => value !== key))
  }

  const applyBulkFields = (choice: FieldChoice) => {
    if (bulkDisabled) return
    const eligible = choice === 'useImported' ? bulkUpdates : bulkKeepRows
    setFieldChoices(current => {
      const next = { ...current }
      for (const row of eligible) {
        const key = rowKey(row)
        const defaults = Object.fromEntries(criticalChanges(row, targets[key]).map(change => [change.field, choice]))
        next[key] = choice === 'useImported' ? { ...defaults, ...current[key] } : { ...current[key], ...defaults }
      }
      return next
    })
    if (choice === 'useImported') setDecisions(current => ({ ...current, ...Object.fromEntries(eligible.map(row => [rowKey(row), 'update' as const])) }))
    setAcknowledged(false)
  }

  const skipSelected = () => setDecisions(current => ({
    ...current,
    ...Object.fromEntries(selectedReviewRows.map(row => [rowKey(row), 'skip']))
  }))

  const acceptSalaryUpdates = () => {
    const acceptedDecisions: Record<string, DecisionAction> = {}
    const acceptedFields: Record<string, Record<string, FieldChoice>> = {}
    for (const row of matchedSalaryRows) {
      const key = rowKey(row)
      acceptedDecisions[key] = 'update'
      acceptedFields[key] = Object.fromEntries(changesFor(row, resolvedTargetId(row, targets[key])).map(change => [change.field, 'useImported']))
    }
    setDecisions(current => ({ ...current, ...acceptedDecisions }))
    setFieldChoices(current => ({ ...current, ...acceptedFields }))
    setAcknowledged(false)
  }

  const submit = () => {
    if (!canContinue) return
    p.onConfirm(reviewRows.map(row => {
      const key = rowKey(row)
      const action = decisions[key] ?? 'skip'
      const employeeId = resolvedTargetId(row, targets[key])
      return {
        rowNumber: row.rowNumber,
        sheet: row.sheet,
        action,
        ...(action === 'update' && employeeId > 0 ? { employeeId } : {}),
        ...(action === 'update' && Object.keys(fieldChoices[key] ?? {}).length ? { fieldChoices: fieldChoices[key] } : {})
      }
    }))
  }

  return <Drawer
    className="employee-import-review-drawer"
    width="min(1180px, 98vw)"
    placement="right"
    open={p.open}
    closable={!p.busy}
    maskClosable={!p.busy}
    onClose={p.onCancel}
    title={<>
      <div className="employee-import-review-title">
        <span>EMPLOYEE IDENTITY REVIEW</span>
        <b>Resolve matches before bulk import</b>
        <small>{p.fileName} / {operationLabel(p.operation)}</small>
      </div>
      <section className="employee-import-review-actions">
        <div><b>{selectedReviewRows.length} selected</b><span>{bulkUpdates.length} matched updates ready to confirm{individualReviewCount > 0 ? ` · ${individualReviewCount} need individual employee or conflict review` : ''}.</span></div>
        <Space wrap>
          <Checkbox data-testid="employee-import-select-all"
            checked={reviewRows.length > 0 && selectedReviewRows.length === reviewRows.length}
            indeterminate={selectedReviewRows.length > 0 && selectedReviewRows.length < reviewRows.length}
            disabled={!reviewRows.length || p.busy}
            onChange={event => event.target.checked ? selectAll() : deselectAll()}>Select all</Checkbox>
          <Button data-testid="employee-import-deselect-all" onClick={deselectAll} disabled={!selectedRows.length}>Deselect all</Button>
          {salaryUpdateMode && reviewRows.some(row => isExactSalaryUpdate(row, resolvedTargetId(row, targets[rowKey(row)]))) && <Button data-testid="employee-import-accept-salary"
            disabled={!matchedSalaryRows.length || !!p.busy || globallyBlocked || !!errors.length || !p.review?.reviewToken}
            onClick={acceptSalaryUpdates}>Accept matched salary updates ({matchedSalaryRows.length})</Button>}
          <Button data-testid="employee-import-bulk-keep" disabled={bulkDisabled || !bulkKeepRows.length} onClick={() => applyBulkFields('keepExisting')}>Keep HRMS values</Button>
          <Button data-testid="employee-import-bulk-import" type="primary" disabled={bulkDisabled || !bulkUpdates.length} onClick={() => applyBulkFields('useImported')}>Use imported values · Confirm {bulkUpdates.length} {bulkUpdates.length === 1 ? 'update' : 'updates'}</Button>
          <Button data-testid="employee-import-bulk-skip" danger icon={<StopOutlined />} disabled={!selectedReviewRows.length} onClick={skipSelected}>Skip selected</Button>
        </Space>
      </section>
    </>}
    footer={<div className="employee-import-review-footer">
      <div>
        <b>{accepted} confirmed / {automaticRows} clear / {skipped} skipped</b>
        <span>{pendingRows.length ? `${pendingRows.length} row(s) still need a complete decision` : 'Every flagged row has an explicit decision.'}</span>
      </div>
      <label className={`employee-import-review-ack${globallyBlocked ? ' disabled' : ''}`}>
        <Checkbox data-testid="employee-import-review-acknowledge" checked={acknowledged} disabled={globallyBlocked || p.busy} onChange={event => setAcknowledged(event.target.checked)} />
        <span><b>I reviewed the employee targets and field-level choices shown above.</b><small>Saved Keep HRMS choices are retained when you confirm matched updates.</small></span>
      </label>
      <Space wrap>
        <Button disabled={p.busy} onClick={p.onCancel}>Cancel import</Button>
        <Button data-testid="employee-import-review-confirm" type="primary" loading={p.busy} disabled={!canContinue} onClick={submit}>Import reviewed rows</Button>
      </Space>
    </div>}
  >
    <div className="employee-import-review" data-testid="employee-import-review">
      <section className="employee-import-review-hero">
        <div className="employee-import-review-icon"><SafetyCertificateOutlined /></div>
        <div><h3>No employee will be silently duplicated</h3><p>Compare uploaded identifiers with HRMS candidates, choose the correct employee, and decide every critical field before importing.</p></div>
        <div className="employee-import-review-summary">
          <span><b>{p.review?.totalRows ?? rows.length}</b>Total rows</span>
          <span><b>{reviewRows.length}</b>Need review</span>
          <span><b>{rows.filter(isConflict).length}</b>Conflicts</span>
        </div>
      </section>

      {globallyBlocked && <Alert type="error" showIcon message="This import cannot continue" description="The API reported a fatal identity validation error. Correct the spreadsheet and run the review again." />}
      {!globallyBlocked && p.review?.canImport === false && hasRowConflict && <Alert type="warning" showIcon message="Conflicting rows need a safe decision" description="Choose one target employee only where HRMS allows resolution. Unresolvable rows must remain skipped." />}
      {!!errors.length && <Alert type="error" showIcon message="Preflight validation failed" description={errors.map(error => <div key={error}>{error}</div>)} />}
      {!p.review?.reviewToken && <Alert type="error" showIcon message="Review token is missing" description="Run employee preflight again. Import is blocked without a server-issued review token." />}

      <div className="employee-import-review-list">
        {reviewRows.length ? reviewRows.map(row => {
          const key = rowKey(row)
          return <ReviewRow
            key={key}
            row={row}
            operation={p.operation}
            selected={selectedRows.includes(key)}
            targetEmployeeId={targets[key]}
            decision={decisions[key]}
            fieldChoices={fieldChoices[key] ?? {}}
            onSelected={checked => toggleRow(row, checked)}
            onTarget={employeeId => chooseTarget(row, employeeId)}
            onChoose={action => choose(row, action)}
            onFieldChoice={(field, choice) => chooseField(row, field, choice)}
          />
        }) : <Empty description="No identity or sensitive-field review was returned." />}
      </div>
    </div>
  </Drawer>
}

type ReviewRowProps = {
  row: EmployeeImportReviewRow
  operation: BulkImportOperation
  selected: boolean
  targetEmployeeId?: number
  decision?: DecisionAction
  fieldChoices: Record<string, FieldChoice>
  onSelected: (checked: boolean) => void
  onTarget: (employeeId: number) => void
  onChoose: (action: DecisionAction) => void
  onFieldChoice: (field: string, choice: FieldChoice) => void
}

function ReviewRow(p: ReviewRowProps) {
  const { row } = p
  const conflict = isConflict(row)
  const probable = isProbable(row)
  const candidates = candidateEmployees(row)
  const targetId = resolvedTargetId(row, p.targetEmployeeId)
  const target = candidates.find(candidate => candidate.employeeId === targetId)
  const matched = targetId > 0
  const resolvable = conflict && canResolve(row) && candidates.length > 0
  const canConfirm = !conflict || (resolvable && matched)
  const confirmAction: DecisionAction = p.operation === 'insert' && probable ? 'insert' : matched ? 'update' : 'insert'
  const confirmLabel = confirmAction === 'update' ? 'Use selected employee - Update' : probable ? 'Keep as separate new employee' : 'Confirm new employee'
  const reasons = Array.from(new Set([...cleanStrings(row.matchReasons), ...cleanStrings(row.blockingReasons)]))
  const changes = changesFor(row, targetId)
  return <article
    className={`employee-import-review-row${conflict ? ' conflict' : probable ? ' probable' : ' matched'}${p.selected ? ' selected' : ''}`}
    data-testid="employee-import-review-row"
    data-row-number={row.rowNumber}
    data-match-status={row.matchStatus}
  >
    <header>
      <Checkbox data-testid="employee-import-row-select" checked={p.selected} onChange={event => p.onSelected(event.target.checked)} aria-label={`Select review row ${row.rowNumber}`} />
      <div>
        <span>ROW {row.rowNumber} / {row.sheet || 'Employees'}</span>
        <h4>{row.proposedEmployeeCode || 'Employee code will be generated'}</h4>
        {matched && <p>Target employee: <b>{target?.employeeName || row.matchedEmployeeName || target?.employeeCode || row.matchedEmployeeCode || `Employee #${targetId}`}</b></p>}
      </div>
      <Tag color={conflict ? 'red' : probable ? 'gold' : 'green'} icon={conflict ? <StopOutlined /> : probable ? <ExclamationCircleOutlined /> : <CheckCircleOutlined />}>{statusLabel(row)}</Tag>
    </header>

    {!!reasons.length && <div className="employee-import-match-reasons">{reasons.map(reason => <Tag key={reason}>{reason}</Tag>)}</div>}
    {!!identityEvidence(row).length && <IdentityEvidence evidence={identityEvidence(row)} selectedTargetId={targetId} />}

    {conflict && !resolvable && <Alert type="error" showIcon message="Conflicting identity - this row cannot be resolved here" description="The identifiers cannot be safely assigned to one employee. This row can only be skipped." />}
    {resolvable && <CandidatePicker row={row} candidates={candidates} selectedTargetId={p.targetEmployeeId} onTarget={p.onTarget} />}
    {!conflict && (probable || isIdentifierMatch(row)) && !matched && <Alert type="warning" showIcon message="Select or correct the existing employee" description="This possible duplicate cannot update until one employee target is known." />}

    {!!changes.length && <div className="employee-import-change-table enhanced">
      <div className="employee-import-change-head"><span>Field</span><span>Current HRMS value</span><span>Imported value</span><span>Decision</span></div>
      {changes.map((change, index) => <ChangeRow
        key={`${change.field}-${index}`}
        change={change}
        choice={p.fieldChoices[change.field]}
        disabled={p.decision === 'skip' || (conflict && !matched)}
        importedBlocked={importedValueOwnedByAnotherEmployee(row, change.field, targetId)}
        onChoice={choice => p.onFieldChoice(change.field, choice)}
      />)}
    </div>}

    <footer>
      <div><b>Row decision</b><span>{conflict && !resolvable ? 'Unresolvable conflicts must be skipped.' : conflict && !matched ? 'Choose exactly one target employee first.' : matched ? 'Confirm the target and all critical field choices, or skip.' : 'Confirm the new employee or skip.'}</span></div>
      <Space wrap>
        {canConfirm && <Button data-testid="employee-import-decision-confirm" type={p.decision === confirmAction ? 'primary' : 'default'} disabled={conflict && !matched} onClick={() => p.onChoose(confirmAction)}>{confirmLabel}</Button>}
        <Button data-testid="employee-import-decision-skip" danger={p.decision !== 'skip'} type={p.decision === 'skip' ? 'primary' : 'default'} onClick={() => p.onChoose('skip')}>Skip this row</Button>
      </Space>
    </footer>
  </article>
}

function CandidatePicker({ row, candidates, selectedTargetId, onTarget }: { row: EmployeeImportReviewRow; candidates: EmployeeImportCandidateEmployee[]; selectedTargetId?: number; onTarget: (employeeId: number) => void }) {
  return <section className="employee-import-candidate-picker" data-testid="employee-import-candidate-picker">
    <header><div><b>Choose the employee this row belongs to</b><span>HRMS will not choose between different employees automatically.</span></div><Tag color={row.canResolveConflict ? 'blue' : 'red'}>{candidates.length} candidate(s)</Tag></header>
    <div>{candidates.map(candidate => <button
      type="button"
      key={candidate.employeeId}
      className={selectedTargetId === candidate.employeeId ? 'selected' : ''}
      data-testid="employee-import-candidate-select"
      data-employee-id={candidate.employeeId}
      onClick={() => onTarget(candidate.employeeId)}
    >
      <span>{selectedTargetId === candidate.employeeId ? <CheckCircleOutlined /> : <i />}</span>
      <b>{candidate.employeeName || candidate.employeeCode}</b>
      <small>{candidate.employeeCode} / {cleanStrings(candidate.matchReasons).join(', ') || 'Identity candidate'}</small>
    </button>)}</div>
  </section>
}

function IdentityEvidence({ evidence, selectedTargetId }: { evidence: EmployeeImportIdentityEvidence[]; selectedTargetId: number }) {
  return <section className="employee-import-evidence" data-testid="employee-import-identity-evidence">
    <header><b>Why HRMS found these employees</b><span>Uploaded identifiers are shown on the left and matching HRMS values on the right.</span></header>
    <div>{evidence.map((item, index) => <div className="employee-import-evidence-row" key={`${item.field}-${index}`}>
      <span className="employee-import-evidence-label">{item.label || humanize(item.field)}</span>
      <Tag className="employee-import-evidence-uploaded" data-testid="employee-import-uploaded-identifier" color="blue">{displayValue(item.uploadedValue)}</Tag>
      <ArrowRightOutlined />
      <div>{identityCandidates(item).map(candidate => <Tag
        key={`${item.field}-${candidate.employeeId}`}
        className={selectedTargetId === candidate.employeeId ? 'selected' : ''}
        data-testid="employee-import-candidate-identifier"
        data-employee-id={candidate.employeeId}
        color={selectedTargetId === candidate.employeeId ? 'purple' : 'default'}
      >{candidate.employeeCode || candidate.employeeName}: {displayValue(candidate.existingValue)}</Tag>)}</div>
    </div>)}</div>
  </section>
}

function ChangeRow({ change, choice, disabled, importedBlocked, onChoice }: { change: EmployeeImportReviewChange; choice?: FieldChoice; disabled: boolean; importedBlocked: boolean; onChoice: (choice: FieldChoice) => void }) {
  const sensitive = Boolean(change.sensitive) || inferredSensitive(change.field || change.label)
  const payroll = Boolean(change.payrollImpact) || inferredPayrollImpact(change.field || change.label)
  const critical = sensitive || payroll
  return <div className="employee-import-change-row" data-testid="employee-import-review-change" data-field={change.field}>
    <div><b>{change.label || humanize(change.field)}</b><span>{sensitive && <Tag color="red">Sensitive</Tag>}{payroll && <Tag color="purple">Payroll impact</Tag>}</span></div>
    <code title={stringValue(change.oldValue)}>{displayValue(change.oldValue)}</code>
    <code className="new-value" title={stringValue(change.newValue)}>{displayValue(change.newValue)}</code>
    <div className="employee-import-field-decisions">
      {critical ? <>
        <Button data-testid="employee-import-field-choice-keep" className={choice === 'keepExisting' ? 'selected keep' : ''} type={choice === 'keepExisting' ? 'primary' : 'default'} disabled={disabled} onClick={() => onChoice('keepExisting')}>Keep HRMS</Button>
        <Button data-testid="employee-import-field-choice-import" className={choice === 'useImported' ? 'selected imported' : ''} type={choice === 'useImported' ? 'primary' : 'default'} disabled={disabled || importedBlocked} onClick={() => onChoice('useImported')}>Use imported</Button>
        {importedBlocked && <small data-testid="employee-import-field-choice-blocked">Already belongs to another employee. Keep HRMS.</small>}
      </> : <Tag color="blue">Standard update</Tag>}
    </div>
  </div>
}

function normalizeRows(rows: EmployeeImportReviewRow[] | undefined): EmployeeImportReviewRow[] {
  if (!Array.isArray(rows)) return []
  return rows.map((row, index) => ({
    ...row,
    rowNumber: Number(row?.rowNumber || index + 2),
    sheet: stringValue(row?.sheet) || 'Employees',
    proposedEmployeeCode: stringValue(row?.proposedEmployeeCode),
    matchStatus: stringValue(row?.matchStatus) || 'New',
    matchReasons: cleanStrings(row?.matchReasons),
    blockingReasons: cleanStrings(row?.blockingReasons),
    changes: normalizeChanges(row?.changes),
    candidateEmployees: Array.isArray(row?.candidateEmployees) ? row.candidateEmployees.map(candidate => ({
      employeeId: Number(candidate?.employeeId || 0),
      employeeCode: stringValue(candidate?.employeeCode),
      employeeName: stringValue(candidate?.employeeName),
      matchReasons: cleanStrings(candidate?.matchReasons),
      changes: normalizeChanges(candidate?.changes)
    })).filter(candidate => candidate.employeeId > 0) : [],
    identityEvidence: Array.isArray(row?.identityEvidence) ? row.identityEvidence.map(evidence => ({
      field: stringValue(evidence?.field),
      label: stringValue(evidence?.label || evidence?.field),
      uploadedValue: stringValue(evidence?.uploadedValue),
      sensitive: Boolean(evidence?.sensitive),
      candidates: identityCandidates(evidence).filter(candidate => candidate.employeeId > 0)
    })) : [],
    canResolveConflict: Boolean(row?.canResolveConflict)
  }))
}

function normalizeChanges(changes: EmployeeImportReviewChange[] | undefined) {
  return Array.isArray(changes) ? changes.map(change => ({
    ...change,
    field: stringValue(change?.field),
    label: stringValue(change?.label || change?.field),
    oldValue: stringValue(change?.oldValue),
    newValue: stringValue(change?.newValue),
    sensitive: Boolean(change?.sensitive),
    payrollImpact: Boolean(change?.payrollImpact)
  })) : []
}

function candidateEmployees(row: EmployeeImportReviewRow) {
  return Array.isArray(row.candidateEmployees) ? row.candidateEmployees : []
}

function identityEvidence(row: EmployeeImportReviewRow) {
  return Array.isArray(row.identityEvidence) ? row.identityEvidence : []
}

function identityCandidates(evidence: EmployeeImportIdentityEvidence) {
  return Array.isArray(evidence?.candidates) ? evidence.candidates.map(candidate => ({
    employeeId: Number(candidate?.employeeId || 0),
    employeeCode: stringValue(candidate?.employeeCode),
    employeeName: stringValue(candidate?.employeeName),
    existingValue: stringValue(candidate?.existingValue)
  })) : []
}

function changesFor(row: EmployeeImportReviewRow, employeeId: number) {
  const candidate = candidateEmployees(row).find(item => item.employeeId === employeeId)
  return candidate?.changes?.length ? candidate.changes : row.changes ?? []
}

function criticalChanges(row: EmployeeImportReviewRow, employeeId?: number) {
  return changesFor(row, Number(employeeId || row.matchedEmployeeId || 0)).filter(change =>
    Boolean(change.sensitive || change.payrollImpact) || inferredSensitive(change.field || change.label) || inferredPayrollImpact(change.field || change.label))
}

function resolvedTargetId(row: EmployeeImportReviewRow, selectedTargetId?: number) {
  if (positiveNumber(selectedTargetId)) return Number(selectedTargetId)
  return !isConflict(row) && positiveNumber(row.matchedEmployeeId) ? Number(row.matchedEmployeeId) : 0
}

function isExactSalaryUpdate(row: EmployeeImportReviewRow, targetId: number) {
  if (!safeForBulkImportedChoice(row, targetId)) return false
  if (!['MatchedByEmployeeCode', 'MatchedByEmployeeId'].includes(row.matchStatus)) return false
  const code = identityEvidence(row).find(evidence => evidence.field === 'employeeCode')
  if (!code?.uploadedValue.trim() || code.candidates.length !== 1 || code.candidates[0].employeeId !== targetId) return false
  if (code.uploadedValue.trim().toUpperCase() !== stringValue(row.matchedEmployeeCode).toUpperCase()) return false
  const changes = changesFor(row, targetId)
  return changes.length > 0 && changes.every(change => change.payrollImpact && !change.sensitive
    && (change.field === 'annualCtc' || change.field === 'salaryTemplateId' || /^salaryInput:[1-9]\d*$/.test(change.field)))
}

function canResolve(row: EmployeeImportReviewRow) {
  return Boolean(row.canResolveConflict) && candidateEmployees(row).length > 0
}

function safeForBulkImportedChoice(row: EmployeeImportReviewRow, targetId: number) {
  if (!targetId || targetId !== row.matchedEmployeeId || isConflict(row) || row.canResolveConflict) return false
  if (!['MatchedByEmployeeCode', 'MatchedByEmployeeId', 'MatchedByIdentifiers'].includes(row.matchStatus)) return false
  const candidates = candidateEmployees(row)
  if (candidates.length !== 1 || candidates[0].employeeId !== targetId) return false
  return !identityEvidence(row).some(evidence => evidence.candidates.some(candidate => candidate.employeeId !== targetId))
}

function importedValueOwnedByAnotherEmployee(row: EmployeeImportReviewRow, field: string, targetEmployeeId: number) {
  if (targetEmployeeId <= 0) return false
  const evidence = identityEvidence(row).find(item => normalizedStatus(item.field) === normalizedStatus(field))
  return Boolean(evidence?.candidates.some(candidate => candidate.employeeId > 0 && candidate.employeeId !== targetEmployeeId))
}

function rowIsPending(row: EmployeeImportReviewRow, action: DecisionAction | undefined, selectedTargetId: number | undefined, choices: Record<string, FieldChoice> | undefined, operation: BulkImportOperation) {
  if (action === 'skip') return false
  if (!action) return true
  const targetId = resolvedTargetId(row, selectedTargetId)
  if (action === 'update' && targetId <= 0) return true
  if (isConflict(row) && (!canResolve(row) || targetId <= 0)) return true
  if (action === 'update') {
    const critical = criticalChanges(row, targetId)
    if (critical.some(change => !choices?.[change.field])) return true
  }
  if (operation === 'update' && action === 'insert') return true
  return false
}

function rowNeedsReview(row: EmployeeImportReviewRow) {
  return isConflict(row)
    || isProbable(row)
    || isIdentifierMatch(row)
    || (row.changes ?? []).some(change => Boolean(change.sensitive || change.payrollImpact) || inferredSensitive(change.field || change.label) || inferredPayrollImpact(change.field || change.label))
}

function isConflict(row: EmployeeImportReviewRow) {
  const status = normalizedStatus(row.matchStatus)
  return cleanStrings(row.blockingReasons).length > 0 || ['conflict', 'blocked', 'ambiguous', 'multiple'].some(value => status.includes(value))
}

function isProbable(row: EmployeeImportReviewRow) {
  const status = normalizedStatus(row.matchStatus)
  return ['probable', 'possible', 'similar', 'nameaddress'].some(value => status.includes(value))
}

function isIdentifierMatch(row: EmployeeImportReviewRow) {
  const status = normalizedStatus(row.matchStatus)
  if (['matchedbyidentifier', 'identifiermatch', 'secondaryidentifier', 'identitymatch'].some(value => status.includes(value))) return true
  return cleanStrings(row.matchReasons).some(reason => {
    const key = normalizedStatus(reason)
    return ['mobile', 'phone', 'aadhaar', 'aadhar', 'pan', 'bankaccount', 'nameaddress'].some(value => key.includes(value))
  })
}

function isExisting(row: EmployeeImportReviewRow) {
  const status = normalizedStatus(row.matchStatus)
  return positiveNumber(row.matchedEmployeeId) || candidateEmployees(row).length > 0 || ['existing', 'exact', 'matched', 'update'].some(value => status.includes(value))
}

function statusLabel(row: EmployeeImportReviewRow) {
  if (isConflict(row)) return canResolve(row) ? 'Choose employee' : 'Blocked conflict'
  if (isProbable(row)) return 'Probable duplicate'
  if (isExisting(row)) return 'Existing employee'
  return 'Sensitive change'
}

function normalizedStatus(value: string) {
  return stringValue(value).toLowerCase().replace(/[^a-z0-9]/g, '')
}

function cleanStrings(values: string[] | undefined) {
  return Array.isArray(values) ? values.map(stringValue).filter(Boolean) : []
}

function stringValue(value: unknown) {
  return value === null || value === undefined ? '' : String(value).trim()
}

function displayValue(value: unknown) {
  return stringValue(value) || 'Blank'
}

function positiveNumber(value: unknown) {
  const number = Number(value)
  return Number.isFinite(number) && number > 0
}

function rowKey(row: EmployeeImportReviewRow) {
  return `${row.sheet || 'Employees'}:${row.rowNumber}`
}

function operationLabel(operation: BulkImportOperation) {
  if (operation === 'insert') return 'Add new employees only'
  if (operation === 'update') return 'Update existing employees only'
  return 'Add new + update existing'
}

function inferredSensitive(value: string) {
  const key = normalizedStatus(value)
  return ['mobile', 'phone', 'aadhaar', 'aadhar', 'pan', 'bankaccount', 'ifsc', 'workemail', 'portalaccess', 'active'].some(item => key.includes(item))
}

function inferredPayrollImpact(value: string) {
  const key = normalizedStatus(value)
  return ['salary', 'ctc', 'wage', 'bank', 'payment', 'active', 'portalaccess'].some(item => key.includes(item))
}

function humanize(value: string) {
  return stringValue(value).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ') || 'Field'
}
