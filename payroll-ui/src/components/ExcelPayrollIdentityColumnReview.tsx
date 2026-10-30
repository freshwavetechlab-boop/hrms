import { Alert, Checkbox, Form, Input, Select } from 'antd'
import type { PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'

export default function ExcelPayrollIdentityColumnReview({ context, request, disabled, onChange }: {
  context: PayrollSetupContext; request: PayrollSetupRequest; disabled: boolean
  onChange: (change: Partial<PayrollSetupRequest>) => void
}) {
  const candidates = context.identityColumnCandidates || []
  const review = request.identityColumnReview
  if (candidates.length < 2 && !review) return null
  const selected = candidates.find(candidate => candidate.columnIndex === review?.columnIndex)
  const canReview = !!selected && selected.invalidOrMissingRows === 0 && (review?.reason.trim().length || 0) >= 8
    && request.importAttendance === false && !request.locationAllocations && !request.employeeDecisions.some(decision => decision.allowCreate)
  return <>
    <Alert type="warning" showIcon message="Multiple UAN source columns need a reviewed meaning" description="Choose the column established by the source workbook evidence for this exact selection. This maps the saved source for existing-employee salary transfer; source cells and Master identifiers stay unchanged. Conflicting identifiers and duplicate employees still block transfer." />
    <div className="excel-payroll-setup-grid">
      <Form.Item label="Reviewed UAN source column">
        <Select aria-label="Reviewed UAN source column" allowClear disabled={disabled || context.identityColumnReviewSupported !== true} value={review?.columnIndex}
          options={candidates.map(candidate => ({ value: candidate.columnIndex, label: `${candidate.columnLetter}: ${candidate.sourceHeader} — ${candidate.canonicalLiteralRows}/${candidate.selectedRows} canonical literals` }))}
          onChange={(value: number | undefined) => {
            const candidate = candidates.find(item => item.columnIndex === value)
            onChange({ identityColumnReview: candidate ? { version: 1, role: 'UAN', columnIndex: candidate.columnIndex, sourceHash: candidate.sourceHash, selectionFingerprint: candidate.selectionFingerprint, reason: '', reviewed: false } : undefined, roundingReviews: {} })
          }} />
      </Form.Item>
      <Form.Item label="Source evidence for this UAN column">
        <Input.TextArea aria-label="UAN source evidence" maxLength={1200} disabled={disabled || !review} value={review?.reason || ''}
          onChange={event => review && onChange({ identityColumnReview: { ...review, reason: event.target.value, reviewed: false }, roundingReviews: {} })} />
      </Form.Item>
    </div>
    {selected && selected.invalidOrMissingRows > 0 && <Alert type="error" showIcon message={`${selected.invalidOrMissingRows} selected rows have missing, unreliable or noncanonical UAN literals in this column. Review those rows separately; no digits are inferred.`} />}
    <Checkbox disabled={disabled || !canReview} checked={review?.reviewed === true} onChange={event => review && onChange({ identityColumnReview: { ...review, reviewed: event.target.checked }, roundingReviews: {} })}>I verified this source column for the selected existing employees</Checkbox>
  </>
}
