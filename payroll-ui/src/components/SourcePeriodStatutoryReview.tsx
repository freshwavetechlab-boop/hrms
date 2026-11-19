import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Input, Modal, Space, Tag } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { previewSourcePeriodStatutory, saveSourcePeriodStatutory, sourcePeriodStatutoryCapabilities, type SourcePeriodStatutoryPreview, type SourcePeriodStatutoryRow } from '../services/sourcePeriodStatutoryService'
import { excelColumnLetter } from '../utils/excelPayslipImport'
import DataTable from './DataTable'

type Group = { preview: SourcePeriodStatutoryPreview; requestId: string }
const statutoryRole = (category: string, type: string) => category === 'Deduction'
  ? ['PFEMPLOYEE', 'ESIEMPLOYEE', 'ESICEMPLOYEE'].includes(type.toUpperCase().replace(/[^A-Z]/g, ''))
  : category === 'Benefit' && ['PFEMPLOYER', 'ESIEMPLOYER', 'ESICEMPLOYER'].includes(type.toUpperCase().replace(/[^A-Z]/g, ''))

export default function SourcePeriodStatutoryReview({ clientId, batch, context, setup, disabled }: {
  clientId: number; batch: ExcelPayslipBatch; context: PayrollSetupContext; setup: PayrollSetupRequest; disabled: boolean
}) {
  const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [confirmed, setConfirmed] = useState(false), [groups, setGroups] = useState<Group[]>([])
  const [completed, setCompleted] = useState<number[]>([]), saved = useRef(new Set<number>())
  const [reason, setReason] = useState('Final source payroll reviewed: PT is zero and captured PF/ESIC zeros apply only to this client and source month.')
  const options = useMemo(() => setup.components.flatMap(component => {
    const master = context.components.find(value => value.id === component.existingComponentId), index = component.expectedColumnIndex
    return master && index != null && statutoryRole(master.category, master.statutoryType)
      ? [{ code: master.code, index, label: `${master.name} · ${excelColumnLetter(index)} · ${context.source.columns.find(column => column.columnIndex === index)?.sourceHeader || 'Source output'}` }] : []
  }), [setup.components, context.components, context.source.columns])
  const [selectedCodes, setSelectedCodes] = useState<string[]>([])
  const selectedRows = batch.rows.filter(row => setup.rowIds.includes(row.id))
  const scope = JSON.stringify({ clientId, batch: batch.id, month: batch.month, rows: setup.rowIds, options })
  useEffect(() => { setGroups([]); setCompleted([]); saved.current.clear(); setConfirmed(false); setSelectedCodes(options.map(option => option.code)) }, [scope]) // eslint-disable-line react-hooks/exhaustive-deps
  const resetReview = () => { setGroups([]); setCompleted([]); saved.current.clear() }
  const review = async () => {
    if (busy || !confirmed || clientId !== batch.clientId) return
    setBusy(true); setError(''); resetReview()
    try {
      const capability = await sourcePeriodStatutoryCapabilities(clientId)
      if (!capability.ok || capability.data?.version !== 2 || !capability.data.sourceZeroStatutorySupported) throw Error('The updated API must be running before source-month PF/ESIC/PT review. No legacy save is used.')
      const rows: SourcePeriodStatutoryRow[] = selectedRows.map(row => {
        const employeeId = row.employment?.employeeId ?? setup.employeeDecisions.find(decision => decision.rowId === row.id)?.matchedEmployeeId
        if (!employeeId) throw Error(`Source row ${row.sourceRow}: resolve the existing Employee Master match first.`)
        const cells = context.source.rows.find(captured => captured.sourceRow === row.sourceRow)?.cells
        const zeros = Object.fromEntries(options.filter(option => selectedCodes.includes(option.code)).flatMap(option => {
          const cell = cells?.[option.index]
          return cell && !cell.error && !cell.calculationError && !cell.missingCachedValue && cell.value.trim() !== '' && Number(cell.value) === 0 ? [[option.code, option.index]] : []
        }))
        return { employeeId, sourceRowId: row.id, mode: 'ReviewedAmount', amount: 0, ...(Object.keys(zeros).length ? { sourceZeroStatutoryColumns: zeros } : {}) }
      })
      if (!rows.length || rows.length > 1000) throw Error('Select between 1 and 1000 existing source employees.')
      const plans: Group[] = []
      for (let offset = 0; offset < rows.length; offset += 100) {
        const response = await previewSourcePeriodStatutory({ sourcePeriodReviewVersion: 2, clientId, payPeriod: batch.month, sourceBatchId: batch.id, sourceHash: '', reason, rows: rows.slice(offset, offset + 100) })
        if (!response.ok || !response.data) throw Error(response.error || 'Source-month statutory preview failed.')
        if (response.data.request.clientId !== clientId || response.data.request.payPeriod !== batch.month || response.data.request.sourceBatchId !== batch.id) throw Error('The statutory preview belongs to another scope.')
        plans.push({ preview: response.data, requestId: crypto.randomUUID().replaceAll('-', '') })
      }
      setGroups(plans)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Statutory review failed.') }
    finally { setBusy(false) }
  }
  const apply = async () => {
    if (busy || !groups.length || groups.some(group => !group.preview.canSave)) return
    setBusy(true); setError('')
    try {
      for (let index = 0; index < groups.length; index++) {
        if (saved.current.has(index)) continue
        const group = groups[index], response = await saveSourcePeriodStatutory(group.preview, group.requestId)
        if (!response.ok || !response.data) throw Error(response.error || 'Save could not be confirmed. Retry this same review; confirmed groups will be retained.')
        if (response.data.requestId !== group.requestId || response.data.savedEmployees !== group.preview.request.rows.length) throw Error('Saved statutory response does not match this exact group. Refresh before continuing.')
        saved.current.add(index); setCompleted([...saved.current])
      }
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Statutory save failed.') }
    finally { setBusy(false) }
  }
  const comparison = groups.flatMap(group => group.preview.rows.map(row => ({ ...row,
    sourceZeros: Object.keys(group.preview.request.rows.find(input => input.employeeId === row.employeeId)?.sourceZeroStatutoryColumns || {}).join(', ') || 'Keep PF/ESIC rules',
  })))
  const savedCount = completed.reduce((total, index) => total + groups[index].preview.request.rows.length, 0)
  return <>
    <Button disabled={disabled || !selectedRows.length} onClick={() => { setOpen(true); setError('') }}>Review source-month PF / ESIC / PT</Button>
    {open && <Modal open centered width={1000} title={`Source-month statutory inputs · ${batch.clientName} · ${batch.month}`} bodyStyle={{ maxHeight: '65dvh', overflowY: 'auto', minWidth: 0 }} maskClosable={!busy} closable={!busy} onCancel={() => { if (!busy) setOpen(false) }} footer={<Space wrap><Button disabled={busy} onClick={() => setOpen(false)}>Close</Button><Button loading={busy} disabled={!confirmed || reason.trim().length < 12 || savedCount > 0} onClick={() => void review()}>Check source-month inputs</Button><Button type="primary" loading={busy} disabled={!groups.length || groups.some(group => !group.preview.canSave) || savedCount === selectedRows.length} onClick={() => void apply()}>Save reviewed month inputs</Button></Space>}>
      <p>Only this client's completed source month changes. Saved formulas, permanent PF/ESIC eligibility, other months and saved payroll remain unchanged. Payroll approval is separate.</p>
      <Space direction="vertical">{options.map(option => <Checkbox key={option.code} disabled={busy || savedCount > 0} checked={selectedCodes.includes(option.code)} onChange={event => { setSelectedCodes(codes => event.target.checked ? [...codes, option.code] : codes.filter(code => code !== option.code)); resetReview() }}>{option.label}: use explicit source zeros for this month</Checkbox>)}</Space>
      <p><Checkbox disabled={busy || savedCount > 0} checked={confirmed} onChange={event => { setConfirmed(event.target.checked); resetReview() }}>I reviewed PT as zero in the final payroll for these {selectedRows.length} source employees. Positive PF/ESIC outputs retain their salary rules.</Checkbox></p>
      <Input aria-label="Source-month statutory review reason" value={reason} maxLength={500} disabled={busy || savedCount > 0} onChange={event => { setReason(event.target.value); resetReview() }} />
      {!!groups.length && <><p><Tag color={savedCount === selectedRows.length ? 'green' : 'blue'}>{savedCount} of {selectedRows.length} employees saved</Tag></p>{groups.flatMap(group => group.preview.blockers).map((message, index) => <Alert key={index} type="warning" message={message} />)}<DataTable title="Reviewed month inputs" rows={comparison} getRowId={row => String(row.employeeId)} exportDisabled hideSearch columns={[
        { key: 'employeeCode', label: 'Employee code' }, { key: 'employeeName', label: 'Name' }, { key: 'sourceZeros', label: 'Source-zero PF / ESIC' },
        { key: 'pt', label: 'PT', value: () => '0 for this month' }, { key: 'blockers', label: 'Review', value: row => row.blockers.join('; ') || 'Ready' },
      ]} /></>}
      {error && <Alert type="error" message={error} />}
    </Modal>}
  </>
}
