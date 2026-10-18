import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Form, Input, InputNumber, Select, Space, Tag } from 'antd'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import { calculateExcelPayslips, getExcelPayslipCalculation, getExcelPayslipVariance, saveExcelPayslipBatch, type ExcelPayslipBatch, type ExcelPayslipCalculationSource, type ExcelPayslipSummary, type ExcelPayslipVariance } from '../services/excelPayslipService'
import { excelColumnLetter, formatExcelPayslipAmount, parseExcelPayslipFile } from '../utils/excelPayslipImport'
import { attendanceLocation as location, payslipAttendanceBasis, payslipAttendanceTemplate, readPayslipAttendance } from '../utils/excelPayslipAttendance'
import { downloadXlsx } from '../utils/xlsx'

const adjacentMonth = (month: string, delta: number) => {
  const [year, number] = month.split('-').map(Number), date = new Date(Date.UTC(year, number - 1 + delta, 1))
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`
}
const daysInMonth = (month: string) => { const [year, number] = month.split('-').map(Number); return new Date(Date.UTC(year, number, 0)).getUTCDate() }
const money = (value: number | null) => value == null ? '—' : formatExcelPayslipAmount(value, 2)

export default function ExcelPayslipMonthTools({ clientId, batch, history, canManage, open, onClose, onBusy, onSaved }: {
  clientId: number; batch: ExcelPayslipBatch; history: ExcelPayslipSummary[]; canManage: boolean; open: 'calculate' | 'variance' | null; onClose: () => void
  onBusy: (value: boolean) => void; onSaved: (batch: ExcelPayslipBatch) => void
}) {
  const [working, setWorking] = useState(false), [error, setError] = useState('')
  const [source, setSource] = useState<ExcelPayslipCalculationSource | null>(null), [draft, setDraft] = useState<ExcelPayslipBatch | null>(null)
  const [month, setMonth] = useState(adjacentMonth(batch.month, 1)), [attendanceColumn, setAttendanceColumn] = useState<number | undefined>()
  const [monthDaysCell, setMonthDaysCell] = useState(''), [periodDays, setPeriodDays] = useState<number | null>(daysInMonth(month))
  const [days, setDays] = useState<Record<string, number | null>>({}), [allDays, setAllDays] = useState<number | null>(null)
  const [overrides, setOverrides] = useState<Record<string, number>>({}), [inputCell, setInputCell] = useState(''), [inputValue, setInputValue] = useState<number | null>(null)
  const [previousId, setPreviousId] = useState(''), [variance, setVariance] = useState<ExcelPayslipVariance | null>(null), [onlyChanges, setOnlyChanges] = useState(true)
  const [confirmed, setConfirmed] = useState(false), [attendanceFile, setAttendanceFile] = useState('')
  const [attendanceNotice, setAttendanceNotice] = useState(''), [missingOnly, setMissingOnly] = useState(false)
  const [missingFocusIds, setMissingFocusIds] = useState<string[]>([])
  const active = useRef(true), inFlight = useRef(false), savedBatch = useRef<ExcelPayslipBatch | null>(null)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const previousMonth = adjacentMonth(batch.month, -1), previousBatches = history.filter(item => item.month === previousMonth)
  const missingRows = batch.rows.filter(row => days[row.id] == null)
  const basis = useMemo(() => source ? payslipAttendanceBasis(source, attendanceColumn) : null, [source, attendanceColumn])
  const lock = () => { if (inFlight.current) return false; inFlight.current = true; setWorking(true); onBusy(true); setError(''); return true }
  const unlock = () => { inFlight.current = false; if (active.current) { setWorking(false); onBusy(false) } }
  const changed = () => { setDraft(null); setConfirmed(false); setError('') }
  const begin = async (mode: 'calculate' | 'variance') => {
    if (!lock()) return
    setDraft(null); setVariance(null); setConfirmed(false)
    try {
      if (mode === 'variance') { setPreviousId(previousBatches.length === 1 ? previousBatches[0].id : ''); return }
      const result = await getExcelPayslipCalculation(clientId, batch.id)
      if (!active.current) return
      if (!result.ok || !result.data) { setSource(null); setError(result.error || 'Saved calculation source is unavailable.'); return }
      const saved = result.data
      setSource(saved); setDays({}); setOverrides({}); setAttendanceFile(''); setAttendanceNotice(''); setMissingOnly(false)
      const column = saved.attendanceColumnIndex ?? saved.columns.find(column => /^(attendance|payable days|days attended|paid days)$/i.test(column.sourceHeader.trim()))?.columnIndex
      const inferred = payslipAttendanceBasis(saved, column)
      setAttendanceColumn(column); setMonthDaysCell(inferred.cell); setAllDays(inferred.fixedDays ?? daysInMonth(month))
    } finally { unlock() }
  }
  useEffect(() => {
    if (open) void begin(open)
    // Reinitialize only when the operator opens a tool; a parent busy update must not restart it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])
  const calculate = async () => {
    if (!source || !canManage || attendanceColumn == null) return setError('Select the attendance input column.')
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) return setError('Choose a valid salary month.')
    if (missingRows.length) { setMissingOnly(true); setMissingFocusIds(missingRows.map(row => row.id)); return setError(`Attendance is missing for ${missingRows.length} of ${batch.rows.length} employees. Enter their days below; imported values have been kept.`) }
    if (monthDaysCell && periodDays == null) return setError('Enter the calculation days for the selected input cell.')
    if (!lock()) return
    try {
      const result = await calculateExcelPayslips(clientId, batch.id, { month, attendanceColumnIndex: attendanceColumn, monthDaysCell: monthDaysCell || undefined, monthDays: monthDaysCell ? periodDays! : undefined, attendance: batch.rows.map(row => ({ rowId: row.id, days: days[row.id]! })), overrides })
      if (!active.current) return
      if (result.ok && result.data) { setDraft(result.data); setConfirmed(false) } else setError(result.error || 'Unable to calculate this month.')
    } finally { unlock() }
  }
  const save = async () => {
    if (!draft || !confirmed || !canManage || !lock()) return
    try {
      const result = await saveExcelPayslipBatch(clientId, draft)
      if (!active.current) return
      if (result.ok && result.data) { savedBatch.current = result.data; onClose() } else setError(result.error || 'Unable to save. Check batch history before retrying an interrupted save.')
    } finally { if (!savedBatch.current) unlock() }
  }
  const afterDrawerChange = (visible: boolean) => {
    // Finish closing the portal before the parent replaces this batch-keyed component.
    if (!visible && savedBatch.current) { const saved = savedBatch.current; savedBatch.current = null; unlock(); onSaved(saved) }
  }
  const compare = async () => {
    if (!previousId || !lock()) return
    setVariance(null)
    try {
      const result = await getExcelPayslipVariance(clientId, batch.id, previousId)
      if (!active.current) return
      if (result.ok && result.data) setVariance(result.data); else setError(result.error || 'Unable to compare these batches.')
    } finally { unlock() }
  }
  const uploadDays = async (file: File) => {
    if (!lock()) return
    try {
      const imported = readPayslipAttendance(await parseExcelPayslipFile(file), batch, month)
      if (active.current) {
        setDays(current => ({ ...current, ...imported.values })); setAttendanceFile(file.name); changed()
        setAttendanceNotice(`${imported.entered} attendance values imported.${imported.blankRows.length ? ` Excel rows ${imported.blankRows.join(', ')} have blank Days; enter them below. Blanks remain pending.` : ''}`)
        setMissingOnly(imported.blankRows.length > 0)
        setMissingFocusIds(batch.rows.filter(row => (row.id in imported.values ? imported.values[row.id] : days[row.id]) == null).map(row => row.id))
      }
    } catch (failure) { if (active.current) setError(failure instanceof Error ? failure.message : 'Unable to read attendance.') }
    finally { unlock() }
  }
  return <>
    <Drawer title={open === 'calculate' ? `Create month from ${batch.month}` : `Variance: ${batch.month} vs ${previousMonth}`} open={!!open} width="min(1180px, 98vw)" className="excel-payslip-month-tools" afterOpenChange={afterDrawerChange} closable={!working} maskClosable={!working} onClose={onClose} footer={<Space wrap className="excel-payslip-footer"><Button disabled={working} onClick={onClose}>Close</Button>{open === 'calculate' ? <><Button loading={working} disabled={!source || !canManage} onClick={() => void calculate()}>Calculate preview</Button><Button type="primary" loading={working} disabled={!draft || !confirmed || !canManage} onClick={() => void save()}>Save new batch</Button></> : <Button type="primary" loading={working} disabled={!previousId} onClick={() => void compare()}>Compare</Button>}</Space>}>
      {error && <Alert type="error" showIcon message={error} />}
      {open === 'calculate' && source && <>
        <Alert type="info" showIcon message="Reuse this batch's saved Excel formulas and rates." description="Review rates and statutory inputs for the selected month. Values remain those of the source until you change an input or import a revised salary sheet." />
        <Form component="div" layout="vertical" disabled={working}><div className="excel-payslip-form-grid">
          <Form.Item label="Salary month" required><Input type="month" aria-label="Calculation salary month" value={month} onChange={event => { setMonth(event.target.value); if (/^\d{4}-\d{2}$/.test(event.target.value)) { setPeriodDays(daysInMonth(event.target.value)); setAllDays(basis?.fixedDays ?? daysInMonth(event.target.value)) }; setDays({}); setAttendanceFile(''); setAttendanceNotice(''); setMissingOnly(false); changed() }} /></Form.Item>
          <Form.Item label="Attendance column" required><Select aria-label="Attendance input column" value={attendanceColumn} options={source.columns.map(column => ({ value: column.columnIndex, label: `${excelColumnLetter(column.columnIndex)} · ${column.sourceHeader || column.label}` }))} onChange={value => { const inferred = payslipAttendanceBasis(source, value); setAttendanceColumn(value); setMonthDaysCell(inferred.cell); setAllDays(inferred.fixedDays ?? daysInMonth(month)); changed() }} /></Form.Item>
          <Form.Item label="Fill empty days (optional)" extra="Only fills blank inputs when clicked. Imported days stay unchanged."><Space.Compact><InputNumber aria-label="Days for empty attendance" min={0} max={31} step={0.5} value={allDays} onChange={setAllDays} /><Button disabled={allDays == null || !missingRows.length} onClick={() => { setDays(current => Object.fromEntries(batch.rows.map(row => [row.id, current[row.id] ?? allDays]))); setAttendanceNotice('Empty attendance inputs filled. Review these days before calculating.'); setMissingOnly(false); changed() }}>Fill empty days</Button></Space.Compact></Form.Item>
        </div>
        <Collapse><Collapse.Panel key="source" header="Saved components, formulas and input adjustments">
          <p className="excel-payslip-muted">Advanced: change a separate day-count cell only if your Excel formulas use one. Leave blank to keep the saved formula basis{basis?.fixedDays ? ` (${basis.fixedDays} days)` : ''}. Fixed numbers inside formulas are not changed here.</p>
          <Space wrap><Form.Item label="Excel day-count cell"><Input aria-label="Month days source cell" placeholder="Leave blank unless the workbook uses a day-count cell" value={monthDaysCell} onChange={event => { setMonthDaysCell(event.target.value.toUpperCase().replace(/\$/g, '').trim()); changed() }} /></Form.Item><Form.Item label="Days in that cell"><InputNumber aria-label="Calculation days" min={1} max={31} value={periodDays} disabled={!monthDaysCell} onChange={value => { setPeriodDays(value); changed() }} /></Form.Item></Space>
          <DataTable rows={source.columns} getRowId={column => column.columnIndex} title="Saved worksheet columns" pageSizeOptions={[5, 10, 25, 50]} columns={[{ key: 'columnIndex', label: 'Column', value: column => excelColumnLetter(column.columnIndex) }, { key: 'sourceHeader', label: 'Source heading' }, { key: 'kind', label: 'Category' }, { key: 'label', label: 'Payslip component' }, { key: 'formula', label: 'First employee formula / value', wrap: true, value: column => { const cell = source.rows.find(row => row.sourceRow === batch.rows[0]?.sourceRow)?.cells[column.columnIndex]; return cell?.formulaText || cell?.value || '' } }]} />
          <Space wrap><Input aria-label="Override source cell" placeholder="Input cell, e.g. I6" value={inputCell} disabled={working} onChange={event => setInputCell(event.target.value.toUpperCase().replace(/\$/g, '').trim())} /><InputNumber aria-label="Override source value" value={inputValue} disabled={working} onChange={setInputValue} /><Button disabled={working || !/^[A-Z]+[1-9]\d*$/.test(inputCell) || inputValue == null} onClick={() => { setOverrides(current => ({ ...current, [inputCell]: inputValue! })); setInputCell(''); setInputValue(null); changed() }}>Change input for new batch</Button></Space>
          {Object.entries(overrides).map(([cell, value]) => <Tag key={cell} closable={!working} onClose={() => { setOverrides(current => Object.fromEntries(Object.entries(current).filter(([key]) => key !== cell))); changed() }}>{cell} = {value}</Tag>)}
        </Collapse.Panel></Collapse>
        </Form>
        <p className="excel-payslip-muted">This saved batch contains {batch.rows.length} employee payslips; the attendance template includes all of them. Blank and total rows from the salary sheet are excluded.{basis?.fixedDays && !monthDaysCell ? ` Saved formulas use a ${basis.fixedDays}-day basis; it stays unchanged.` : ''}</p>
        <Space wrap><Button disabled={working} onClick={() => downloadXlsx(`attendance-${month}`, [{ name: 'Attendance', rows: payslipAttendanceTemplate(batch, month, days) }])}>Download attendance template</Button><Tag>{batch.rows.length - missingRows.length}/{batch.rows.length} attendance entered</Tag><Checkbox checked={missingOnly} onChange={event => { setMissingOnly(event.target.checked); setMissingFocusIds(missingRows.map(row => row.id)) }}>Missing days only ({missingRows.length})</Checkbox></Space>
        {attendanceNotice && <Alert type={missingRows.length ? 'warning' : 'success'} showIcon message={attendanceNotice} />}
        <FileDropZone accept=".xlsx,.csv" title="Upload completed attendance" hint="Import filled days, then complete any blanks below. Keep employee identity columns unchanged." fileName={attendanceFile} onFile={file => { if (!working) void uploadDays(file) }} />
        <DataTable rows={missingOnly ? batch.rows.filter(row => missingFocusIds.includes(row.id)) : batch.rows} getRowId={row => row.id} title="Attendance for new month" exportDisabled columns={[{ key: 'sourceRow', label: 'Source row', width: 100 }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Code' }, { key: 'location', label: 'Location', value: location }, { key: 'days', label: 'Days', filterable: false, sortable: false, render: row => <InputNumber aria-label={`Attendance days row ${row.sourceRow}`} min={0} max={31} step={0.5} value={days[row.id]} disabled={working} onChange={value => { setDays(current => ({ ...current, [row.id]: value })); changed() }} /> }]} />
        {draft && <><DataTable rows={draft.rows} getRowId={row => row.id} title={`Calculated preview · ${draft.month}`} columns={[{ key: 'employeeName', label: 'Employee' }, { key: 'netPay', label: 'Net pay', render: row => money(row.netPay) }, { key: 'earnings', label: 'Earnings', value: row => row.earnings.map(item => `${item.label}: ${money(item.amount)}`).join('; '), wrap: true }, { key: 'deductions', label: 'Deductions', value: row => row.deductions.map(item => `${item.label}: ${money(item.amount)}`).join('; '), wrap: true }, { key: 'warnings', label: 'Review', value: row => row.warnings.join('; '), wrap: true }]} /><Checkbox checked={confirmed} disabled={working} onChange={event => setConfirmed(event.target.checked)}>I reviewed this month's attendance, rates, formulas and amounts before saving.</Checkbox></>}
      </>}
      {open === 'variance' && <>
        <Alert type="info" showIcon message="Compare with the previous calendar month." description="Differences can come from attendance, rates or component changes. They do not automatically mean an error. Unclear employee matches are marked for review." />
        <Form.Item label={`Baseline batch · ${previousMonth}`} required><Select aria-label="Previous month baseline batch" value={previousId || undefined} disabled={working} placeholder="Choose previous-month batch" options={previousBatches.map(item => ({ value: item.id, label: `${item.sourceFileName} · ${item.rowCount} payslips · ${new Date(item.createdAtUtc).toLocaleString()}` }))} onChange={value => { setPreviousId(value); setVariance(null); setError('') }} /></Form.Item>
        {!previousBatches.length && <Alert type="warning" message={`No saved batch for ${previousMonth}. Import/save that month to compare; another month will not be substituted.`} />}
        {variance && <><Space wrap><Tag>{variance.matchedEmployees} matched</Tag><Tag color="blue">{variance.newEmployees} new</Tag><Tag color="orange">{variance.missingEmployees} missing</Tag><Tag color="red">{variance.reviewEmployees} need review</Tag><Checkbox checked={onlyChanges} onChange={event => setOnlyChanges(event.target.checked)}>Changes only</Checkbox></Space><DataTable rows={variance.rows.filter(row => !onlyChanges || row.status !== 'Unchanged')} getRowId={row => row.id} title="Previous-month salary variance" exportFileName={`salary-variance-${batch.month}-vs-${previousMonth}`} columns={[{ key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Code' }, { key: 'location', label: 'Location' }, { key: 'status', label: 'Change', render: row => <Tag color={row.status === 'Review' ? 'orange' : undefined}>{row.status}</Tag> }, { key: 'category', label: 'Category' }, { key: 'component', label: 'Component' }, { key: 'previousAmount', label: previousMonth, render: row => money(row.previousAmount) }, { key: 'currentAmount', label: batch.month, render: row => money(row.currentAmount) }, { key: 'difference', label: 'Difference', render: row => money(row.difference) }, { key: 'message', label: 'Details', wrap: true }]} /></>}
      </>}
    </Drawer>
  </>
}
