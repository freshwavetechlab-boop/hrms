import { useState } from 'react'
import { Alert, Button, Input, Space } from 'antd'
import { employeeCodeResolutionIsCurrent, resolveRegularEmployeeCodes, type EmployeeCodeCandidate, type EmployeeCodeResolution, type EmployeeCodeScope } from '../utils/regularEmployeeCodeSelection'

export default function RegularEmployeeCodeSelection({ scope, employees, disabled, onSelect, onClose }: {
  scope: EmployeeCodeScope; employees: EmployeeCodeCandidate[]; disabled: boolean; onSelect: (employeeIds: number[]) => void; onClose: () => void
}) {
  const [text, setText] = useState(''), [reviewed, setReviewed] = useState<EmployeeCodeResolution | null>(null), [applied, setApplied] = useState(false)
  const current = employeeCodeResolutionIsCurrent(reviewed, text, scope, employees)
  return <section aria-label="Select exact employee codes" style={{ minWidth: 0, marginBlock: 12 }}>
    <p>Review up to 100 codes for the current client, attendance policy and payroll month {scope.period}. Confirming replaces the roster selection only.</p>
    <Input.TextArea aria-label="Employee codes to select" rows={4} maxLength={10000} disabled={disabled} value={text} placeholder="Employee codes, separated by commas or new lines" onChange={event => { setText(event.target.value); setReviewed(null); setApplied(false) }} />
    <Space wrap style={{ marginBlock: 8 }}>
      <Button disabled={disabled || !text.trim()} onClick={() => { setReviewed(resolveRegularEmployeeCodes(text, scope, employees)); setApplied(false) }}>Review employee codes</Button>
      <Button type="primary" disabled={disabled || !current || applied} onClick={() => {
        const fresh = resolveRegularEmployeeCodes(text, scope, employees)
        if (!employeeCodeResolutionIsCurrent(reviewed, text, scope, employees)) { setReviewed(fresh); setApplied(false); return }
        onSelect([...fresh.employeeIds]); setApplied(true)
      }}>Apply code selection ({reviewed?.employeeIds.length || 0})</Button>
      <Button disabled={disabled} onClick={onClose}>Close code selection</Button>
    </Space>
    {reviewed && <>
      {reviewed.errors.length > 0 ? <Alert type="error" showIcon message="Resolve all codes before changing selection" description={<ul>{reviewed.errors.map(error => <li key={error}>{error}</li>)}</ul>} />
        : <p role="status">{reviewed.employeeIds.length} employees resolved{applied ? '; roster selection updated.' : current ? '; confirm to replace the current selection.' : '; roster changed—review the codes again.'}</p>}
      {reviewed.codes.length > 0 && <output aria-label="Resolved employee codes" style={{ display: 'block', maxHeight: 140, overflowY: 'auto', overflowWrap: 'anywhere' }}>{reviewed.codes.join(', ')}</output>}
    </>}
  </section>
}
