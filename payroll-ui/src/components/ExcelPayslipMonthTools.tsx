import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Form, Input, InputNumber, Select, Space, Tag } from 'antd'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import { calculateExcelPayslips, getExcelPayslipCalculation, getExcelPayslipVariance, saveExcelPayslipBatch, type ExcelPayslipBatch, type ExcelPayslipCalculationSource, type ExcelPayslipRow, type ExcelPayslipSummary, type ExcelPayslipVariance } from '../services/excelPayslipService'
import { excelColumnLetter, formatExcelPayslipAmount, parseExcelPayslipFile, parsePayslipAmount } from '../utils/excelPayslipImport'
import { downloadXlsx } from '../utils/xlsx'

const adjacentMonth = (month: string, delta: number) => {
  const [year, number] = month.split('-').map(Number), date = new Date(Date.UTC(year, number - 1 + delta, 1))
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`
}
const daysInMonth = (month: string) => { const [year, number] = month.split('-').map(Number); return new Date(Date.UTC(year, number, 0)).getUTCDate() }
const money = (value: number | null) => value == null ? '—' : formatExcelPayslipAmount(value, 2)
const location = (row: ExcelPayslipRow) => row.information.find(item => /work\s*location|location\s*name|^location$|tehsil/i.test(item.label))?.value || ''

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
  const active = useRef(true), inFlight = useRef(false), savedBatch = useRef<ExcelPayslipBatch | null>(null)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const previousMonth = adjacentMonth(batch.month, -1), previousBatches = history.filter(item => item.month === previousMonth)
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
      setSource(saved); setDays({}); setOverrides({}); setAttendanceFile('')
      setAttendanceColumn(saved.attendanceColumnIndex ?? saved.columns.find(column => /^(attendance|payable days|days attended|paid days)$/i.test(column.sourceHeader.trim()))?.columnIndex)
      const addresses = new Set(saved.rows.flatMap(row => row.cells.flatMap(cell => cell?.formulaText?.match(/\$[A-Z]+\$\d+/g) || [])))
      const candidate = [...addresses].map(address => address.replace(/\$/g, '')).find(address => {
        const [letters, row] = [address.match(/^[A-Z]+/)![0], Number(address.match(/\d+$/)![0])]
        const column = [...letters].reduce((total, char) => total * 26 + char.charCodeAt(0) - 64, 0) - 1
        const value = Number(saved.rows.find(item => item.sourceRow === row)?.cells[column]?.value)
        return row <= saved.headerRow && value >= 28 && value <= 31
      })
      setMonthDaysCell(saved.monthDaysCell ?? candidate ?? '')
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
    if (batch.rows.some(row => days[row.id] == null)) return setError('Enter attendance days for every employee, or upload the completed attendance template.')
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
      const sheets = await parseExcelPayslipFile(file), sheet = sheets[0], header = sheet.rows.find(row => row.cells.some(cell => cell?.value === 'Source row'))
      if (!header) throw new Error('Use the attendance template exported here: Batch ID, Source row, Employee and Days.')
      const index = (label: string) => header.cells.findIndex(cell => cell?.value === label)
      const batchColumn = index('Batch ID'), rowColumn = index('Source row'), dayColumn = index('Days')
      if (Math.min(batchColumn, rowColumn, dayColumn) < 0) throw new Error('The attendance template needs Batch ID, Source row and Days columns.')
      const values: Record<string, number> = {}
      for (const row of sheet.rows.filter(row => row.sourceRow > header.sourceRow && row.cells.some(cell => cell?.value.trim()))) {
        const employee = batch.rows.find(item => item.sourceRow === Number(row.cells[rowColumn]?.value))
        if (row.cells[batchColumn]?.value !== batch.id || !employee || employee.id in values) throw new Error(`Row ${row.sourceRow}: wrong batch, unknown employee row or duplicate attendance.`)
        for (const [label, expected] of [['Employee', employee.employeeName], ['Employee code', employee.employeeCode], ['Location', location(employee)]]) {
          const column = index(label)
          if (column >= 0 && (row.cells[column]?.value || '').trim() !== expected.trim()) throw new Error(`Row ${row.sourceRow}: ${label} does not match this employee's source row. Keep identity columns unchanged.`)
        }
        const value = parsePayslipAmount(row.cells[dayColumn]?.value || '')
        if (value == null || value < 0 || value > 31) throw new Error(`Row ${row.sourceRow}: enter days between 0 and 31; half days are allowed.`)
        values[employee.id] = value
      }
      if (!Object.keys(values).length) throw new Error('The attendance file has no employee rows.')
      if (active.current) { setDays(current => ({ ...current, ...values })); setAttendanceFile(file.name); changed() }
    } catch (failure) { if (active.current) setError(failure instanceof Error ? failure.message : 'Unable to read attendance.') }
    finally { unlock() }
  }
  return <>
    <Drawer title={open === 'calculate' ? `Create month from ${batch.month}` : `Variance: ${batch.month} vs ${previousMonth}`} open={!!open} width="min(1180px, 98vw)" className="excel-payslip-month-tools" afterOpenChange={afterDrawerChange} closable={!working} maskClosable={!working} onClose={onClose} footer={<Space wrap className="excel-payslip-footer"><Button disabled={working} onClick={onClose}>Close</Button>{open === 'calculate' ? <><Button loading={working} disabled={!source || !canManage} onClick={() => void calculate()}>Calculate preview</Button><Button type="primary" loading={working} disabled={!draft || !confirmed || !canManage} onClick={() => void save()}>Save new batch</Button></> : <Button type="primary" loading={working} disabled={!previousId} onClick={() => void compare()}>Compare</Button>}</Space>}>
      {error && <Alert type="error" showIcon message={error} />}
      {open === 'calculate' && source && <>
        <Alert type="info" showIcon message="Reuse this batch's saved Excel formulas and rates." description="Review rates and statutory inputs for the selected month. Values remain those of the source until you change an input or import a revised salary sheet." />
        <Form component="div" layout="vertical" disabled={working}><div className="excel-payslip-form-grid">
          <Form.Item label="Salary month" required><Input type="month" aria-label="Calculation salary month" value={month} onChange={event => { setMonth(event.target.value); if (/^\d{4}-\d{2}$/.test(event.target.value)) setPeriodDays(daysInMonth(event.target.value)); changed() }} /></Form.Item>
          <Form.Item label="Attendance column" required><Select aria-label="Attendance input column" value={attendanceColumn} options={source.columns.map(column => ({ value: column.columnIndex, label: `${excelColumnLetter(column.columnIndex)} · ${column.sourceHeader || column.label}` }))} onChange={value => { setAttendanceColumn(value); changed() }} /></Form.Item>
          <Form.Item label="Days to apply to all"><Space.Compact><InputNumber aria-label="Days for all employees" min={0} max={31} step={0.5} value={allDays} onChange={setAllDays} /><Button disabled={allDays == null} onClick={() => { setDays(Object.fromEntries(batch.rows.map(row => [row.id, allDays]))); changed() }}>Apply</Button></Space.Compact></Form.Item>
        </div>
        <Collapse><Collapse.Panel key="source" header="Saved components, formulas and input adjustments">
          <Space wrap><Form.Item label="Month-days input cell (optional)"><Input aria-label="Month days source cell" placeholder="e.g. J4" value={monthDaysCell} onChange={event => { setMonthDaysCell(event.target.value.toUpperCase().replace(/\$/g, '').trim()); changed() }} /></Form.Item><Form.Item label="Calculation days"><InputNumber aria-label="Calculation days" min={1} max={31} value={periodDays} disabled={!monthDaysCell} onChange={value => { setPeriodDays(value); changed() }} /></Form.Item></Space>
          <DataTable rows={source.columns} getRowId={column => column.columnIndex} title="Saved worksheet columns" pageSizeOptions={[5, 10, 25, 50]} columns={[{ key: 'columnIndex', label: 'Column', value: column => excelColumnLetter(column.columnIndex) }, { key: 'sourceHeader', label: 'Source heading' }, { key: 'kind', label: 'Category' }, { key: 'label', label: 'Payslip component' }, { key: 'formula', label: 'First employee formula / value', wrap: true, value: column => { const cell = source.rows.find(row => row.sourceRow === batch.rows[0]?.sourceRow)?.cells[column.columnIndex]; return cell?.formulaText || cell?.value || '' } }]} />
          <Space wrap><Input aria-label="Override source cell" placeholder="Input cell, e.g. I6" value={inputCell} disabled={working} onChange={event => setInputCell(event.target.value.toUpperCase().replace(/\$/g, '').trim())} /><InputNumber aria-label="Override source value" value={inputValue} disabled={working} onChange={setInputValue} /><Button disabled={working || !/^[A-Z]+[1-9]\d*$/.test(inputCell) || inputValue == null} onClick={() => { setOverrides(current => ({ ...current, [inputCell]: inputValue! })); setInputCell(''); setInputValue(null); changed() }}>Change input for new batch</Button></Space>
          {Object.entries(overrides).map(([cell, value]) => <Tag key={cell} closable={!working} onClose={() => { setOverrides(current => Object.fromEntries(Object.entries(current).filter(([key]) => key !== cell))); changed() }}>{cell} = {value}</Tag>)}
        </Collapse.Panel></Collapse>
        </Form>
        <Space wrap><Button disabled={working} onClick={() => downloadXlsx(`attendance-${month}`, [{ name: 'Attendance', rows: [['Batch ID', 'Source row', 'Employee code', 'Employee', 'Location', 'Days'], ...batch.rows.map(row => [batch.id, String(row.sourceRow), row.employeeCode, row.employeeName, location(row), days[row.id] == null ? '' : String(days[row.id])])] }])}>Download attendance template</Button><Tag>{batch.rows.filter(row => days[row.id] != null).length}/{batch.rows.length} attendance entered</Tag></Space>
        <FileDropZone accept=".xlsx,.csv" title="Upload completed attendance" hint="Use the template above; source rows identify employees in this batch." fileName={attendanceFile} onFile={file => { if (!working) void uploadDays(file) }} />
        <DataTable rows={batch.rows} getRowId={row => row.id} title="Attendance for new month" exportDisabled columns={[{ key: 'sourceRow', label: 'Source row', width: 100 }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Code' }, { key: 'location', label: 'Location', value: location }, { key: 'days', label: 'Days', filterable: false, sortable: false, render: row => <InputNumber aria-label={`Attendance days row ${row.sourceRow}`} min={0} max={31} step={0.5} value={days[row.id]} disabled={working} onChange={value => { setDays(current => ({ ...current, [row.id]: value })); changed() }} /> }]} />
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
