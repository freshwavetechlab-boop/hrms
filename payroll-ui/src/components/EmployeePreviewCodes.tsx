import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Input, Space } from 'antd'
import { preflightEmployeeImport, type EmployeeImportCodeSuggestion } from '../services/settingsService'
import { captureEmployeeCodeHints, employeeCodeColumn, employeeDraftPrefix, resolveDraftEmployeeCodes, type EmployeeCodeHintSnapshot } from '../utils/employeePreviewCodes'
import { inferEmployeeCodeDigits, nextEmployeeCodePreview, type BulkImportOperation } from '../utils/smartBulkImport'
import { buildXlsxBlob } from '../utils/xlsx'
import type { BulkUploadPreviewSheet } from './BulkUploadPreviewModal'
import './EmployeePreviewCodes.css'

const identityHeaders = new Set(['employeeid', 'employeecode', 'firstname', 'lastname', 'workemail', 'mobile', 'pan', 'aadhaar', 'bankaccountno', 'address', 'correspondenceaddress', 'permanentaddress'])

export default function EmployeePreviewCodes(p: { sheet: BulkUploadPreviewSheet; clientId: number; clientCode: string; reserved: string[]; operation: BulkImportOperation; onApply: (rows: string[][], mode: BulkImportOperation) => void; onHints?: (snapshot: EmployeeCodeHintSnapshot) => void }) {
  const column = employeeCodeColumn(p.sheet.headers)
  const codes = p.sheet.rows.map(row => row[column] ?? '')
  const [prefix, setPrefix] = useState(() => employeeDraftPrefix(codes, p.clientCode))
  const [working, setWorking] = useState(false)
  const [feedback, setFeedback] = useState('')
  const [switchRows, setSwitchRows] = useState<string[][] | null>(null)
  const current = useRef(p.sheet.rows)
  current.current = p.sheet.rows
  const scope = useRef({ clientId: p.clientId, headers: p.sheet.headers, name: p.sheet.name, operation: p.operation })
  scope.current = { clientId: p.clientId, headers: p.sheet.headers, name: p.sheet.name, operation: p.operation }
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const blanks = codes.filter(value => !value.trim()).length
  if (column < 0 || !blanks && !feedback) return null

  async function resolve(mode: BulkImportOperation) {
    const originalRows = p.sheet.rows
    const requestScope = scope.current
    const isCurrent = () => mounted.current && current.current === originalRows && scope.current.clientId === requestScope.clientId
      && scope.current.headers === requestScope.headers && scope.current.name === requestScope.name && scope.current.operation === requestScope.operation
    setWorking(true)
    try {
      const indexes = p.sheet.headers.flatMap((header, i) => identityHeaders.has(header.replace(/[\s_-]/g, '').toLowerCase()) ? [i] : [])
      const rowsForReview: string[][] = []
      const sourceNumbers: number[] = []
      originalRows.forEach((row, i) => {
        if (indexes.some(index => row[index]?.trim())) { rowsForReview.push(row); sourceNumbers.push(i + 2) }
      })
      const file = new File([buildXlsxBlob([{ name: 'Employees', rows: [p.sheet.headers, ...rowsForReview] }])], 'employee-code-review.xlsx')
      const result = await preflightEmployeeImport(p.clientId, file, mode === 'insert' ? 'insert' : 'upsert')
      if (!isCurrent()) return
      if (!result.ok) throw new Error(result.error || 'Could not check employee identities.')
      const review = result.data.rows.filter(row => row.sheet.toLowerCase() === 'employees' && sourceNumbers[row.rowNumber - 2] !== undefined)
        .map(row => ({ ...row, rowNumber: sourceNumbers[row.rowNumber - 2] }))
      const plan = resolveDraftEmployeeCodes(p.sheet, review, mode, prefix, p.reserved)
      p.onHints?.(captureEmployeeCodeHints(p.clientId, p.sheet, review))
      setSwitchRows(mode === 'update' && plan.newCount ? plan.rows : null)
      const messages = [`${plan.recovered} existing codes filled · ${plan.generated} new codes generated.`]
      if (plan.newCount && mode === 'update') messages.push(`${plan.newCount} unmatched rows need a new employee code.`)
      if (plan.unresolved.length) messages.push(`Review employee identity in rows ${plan.unresolved.join(', ')}.`)
      setFeedback(messages.join(' '))
      if (plan.rows !== originalRows) p.onApply(plan.rows, mode === 'upsert' && !plan.generated ? p.operation : mode)
    } catch (error) {
      if (isCurrent()) setFeedback(error instanceof Error ? error.message : 'Could not check employee identities.')
    } finally { if (mounted.current) setWorking(false) }
  }
  const reserved = [...p.reserved, ...codes]
  return <Alert type="info" message={`Missing employee codes · ${blanks}`} description={<Space direction="vertical">
    {blanks > 0 && <Space wrap>
      <label>Code prefix <Input size="small" aria-label="Employee code prefix" style={{ width: 110 }} disabled={working} value={prefix} onChange={event => setPrefix(event.target.value.trim().toUpperCase())} /></label>
      {prefix && <span>Next available · {nextEmployeeCodePreview(prefix, inferEmployeeCodeDigits(prefix, reserved), reserved)}</span>}
      <Button size="small" loading={working} disabled={!p.clientId || p.operation !== 'update' && !prefix} onClick={() => void resolve(p.operation)}>{p.operation === 'update' ? 'Resolve missing codes' : 'Resolve and generate missing codes'}</Button>
      {p.operation === 'update' && switchRows === p.sheet.rows && <Button size="small" disabled={working || !prefix} onClick={() => void resolve('upsert')}>Switch to add new + update existing and generate codes</Button>}
    </Space>}
    {feedback && <span role="status">{feedback} Import mode · {p.operation === 'upsert' ? 'Add new + update existing' : p.operation === 'update' ? 'Update existing only' : 'Add new only'}.</span>}
  </Space>} />
}

export function EmployeePreviewCodeCandidates(p: { candidates: EmployeeImportCodeSuggestion[]; rowNumber: number; disabled?: boolean; isUsed: (code: string) => boolean; onSelect: (code: string) => void }) {
  if (!p.candidates.length) return null
  return <section className="employee-preview-code-candidates" aria-label={`Existing employee suggestions row ${p.rowNumber}`}>
    <small>Existing employees · selected client</small>
    {p.candidates.map(candidate => {
      const used = p.isUsed(candidate.employeeCode)
      return <article key={candidate.employeeId}>
        <header><b>{candidate.employeeCode}</b><Button size="small" data-testid={`employee-code-suggestion-${p.rowNumber}-${candidate.employeeId}`}
          disabled={p.disabled || used} onClick={() => { if (!p.disabled && !p.isUsed(candidate.employeeCode)) p.onSelect(candidate.employeeCode.trim()) }}>Use code</Button></header>
        <strong>{candidate.employeeName}</strong>
        <div className="employee-preview-code-details">
          {candidate.mobile && <span>Mobile · {candidate.mobile}</span>}
          {candidate.bankAccountNo && <span>Account · {candidate.bankAccountNo}</span>}
        </div>
        <span className="employee-preview-code-reason">{candidate.matchReasons.join(' · ')}</span>
        {used && <span className="employee-preview-code-used">Code already used in another upload row</span>}
      </article>
    })}
    <small>Compare the details before choosing. Identity review still applies.</small>
  </section>
}
