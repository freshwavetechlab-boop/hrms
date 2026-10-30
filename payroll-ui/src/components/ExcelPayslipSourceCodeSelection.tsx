import { useState } from 'react'
import { Alert, Button, Drawer, Input, Space } from 'antd'
import { resolvePayslipSourceCodes, sourceCodeResolutionIsCurrent, type SourceCodeCandidate, type SourceCodeResolution, type SourceCodeScope } from '../utils/excelPayslipSourceCodeSelection'

export default function ExcelPayslipSourceCodeSelection({ scope, rows, visibleRowIds, disabled, onSelect }: {
  scope: SourceCodeScope; rows: SourceCodeCandidate[]; visibleRowIds: string[]; disabled: boolean; onSelect: (rowIds: string[]) => void
}) {
  const [open, setOpen] = useState(false), [text, setText] = useState(''), [reviewed, setReviewed] = useState<SourceCodeResolution | null>(null), [applied, setApplied] = useState(false)
  const current = sourceCodeResolutionIsCurrent(reviewed, text, scope, rows, visibleRowIds)
  const close = () => { setOpen(false); setText(''); setReviewed(null); setApplied(false) }
  return <>
    <Button size="small" disabled={disabled || !rows.length} aria-expanded={open} onClick={() => setOpen(true)}>Select source codes</Button>
    <Drawer title="Select source employee codes" open={open} width={560} onClose={disabled ? undefined : close} closable={!disabled} maskClosable={!disabled}>
      <section aria-label="Select exact source codes" style={{ minWidth: 0 }}>
        <p>Review up to 100 exact codes in this saved {scope.month} batch, across all table pages. Confirming replaces the source-row selection only. It does not save configuration, calculate payroll or send payslips.</p>
        <Input.TextArea aria-label="Source employee codes to select" rows={5} maxLength={10000} disabled={disabled} value={text} placeholder="Source employee codes, separated by commas or new lines" onChange={event => { setText(event.target.value); setReviewed(null); setApplied(false) }} />
        <Space wrap style={{ marginBlock: 8 }}>
          <Button disabled={disabled || !text.trim()} onClick={() => { setReviewed(resolvePayslipSourceCodes(text, scope, rows, visibleRowIds)); setApplied(false) }}>Review source codes</Button>
          <Button type="primary" disabled={disabled || !current || applied} onClick={() => {
            const fresh = resolvePayslipSourceCodes(text, scope, rows, visibleRowIds)
            if (!sourceCodeResolutionIsCurrent(reviewed, text, scope, rows, visibleRowIds)) { setReviewed(fresh); setApplied(false); return }
            onSelect([...fresh.rowIds]); setApplied(true)
          }}>Use source selection ({reviewed?.rowIds.length || 0})</Button>
          <Button disabled={disabled} onClick={close}>Close code selection</Button>
        </Space>
        {reviewed && <>
          {reviewed.errors.length > 0 ? <Alert type="error" showIcon message="Resolve all source codes before changing selection" description={<ul>{reviewed.errors.map(error => <li key={error}>{error}</li>)}</ul>} />
            : <p role="status">{reviewed.rowIds.length} source rows resolved{applied ? '; source selection updated.' : current ? '; confirm to replace the current selection.' : '; saved source rows changed-review the codes again.'}</p>}
          {reviewed.codes.length > 0 && <output aria-label="Resolved source employee codes" style={{ display: 'block', maxHeight: 160, overflowY: 'auto', overflowWrap: 'anywhere' }}>{reviewed.codes.join(', ')}</output>}
        </>}
      </section>
    </Drawer>
  </>
}
