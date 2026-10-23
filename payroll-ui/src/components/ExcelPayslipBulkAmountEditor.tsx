import { useMemo, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, InputNumber, Select, Space, Tag } from 'antd'
import { usePayslipCorrectionPreview } from '../hooks/usePayslipCorrectionPreview'
import DataTable from './DataTable'
import { buildExcelPayslipRows, excelColumnLetter, formatExcelPayslipAmount, isPayslipAmountKind, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from '../utils/excelPayslipImport'
import { correctPayslipBatchAmounts, payslipReconciliation, savedPayslipCorrectionInputs } from '../utils/excelPayslipReview'

export default function ExcelPayslipBulkAmountEditor({ sheet, columns, sourceRows, busy, ready, error, onClose, onSave }: {
  sheet: PayslipSheet; columns: PayslipColumn[]; sourceRows: number[]; busy: boolean; ready: boolean; error: string
  onClose: () => void; onSave: (edits: Record<number, Record<number, string | null>>, calculated?: PayslipSheet) => void
}) {
  const [edits, setEdits] = useState<Record<number, Record<number, string | null>>>({})
  const [recalculate, setRecalculate] = useState(true)
  const calculation = usePayslipCorrectionPreview(sheet, columns, edits, recalculate)
  const amounts = columns.filter(column => isPayslipAmountKind(column.kind))
  const [fields, setFields] = useState(() => amounts.filter(c => ['netPay', 'grossTotal', 'deductionTotal'].includes(c.kind)).map(c => c.columnIndex))
  const [selected, setSelected] = useState(sourceRows.map(String))
  const [commonColumn, setCommonColumn] = useState<number>(), [commonValue, setCommonValue] = useState<string | null>(null)
  const preview = useMemo(() => {
    try {
      const result = correctPayslipBatchAmounts(sheet, columns, edits)
      const parsed = buildExcelPayslipRows(result.sheet, sheet.template!.headerRow, columns, sheet.template!.excludedSourceRows)
      return { ...result, rows: parsed.rows, error: parsed.issues.map(issue => issue.message).join(' ') }
    } catch (failure) { return { changed: 0, rows: [], error: failure instanceof Error ? failure.message : 'Review the entered salary amounts.' } }
  }, [sheet, columns, edits])
  const currentRows = recalculate && calculation.sheet ? calculation.rows : preview.rows
  const savedCorrections = savedPayslipCorrectionInputs(sheet, columns, sourceRows)
  const rows = sourceRows.map(sourceRow => ({ id: String(sourceRow), sourceRow, row: currentRows.find(row => row.sourceRow === sourceRow) }))
  const patch = (sourceRow: number, columnIndex: number, value: string | null) => setEdits(old => ({ ...old, [sourceRow]: { ...old[sourceRow], [columnIndex]: value } }))
  const applyCommon = () => {
    if (commonColumn == null || commonValue == null || !selected.length) return
    setEdits(old => { const next = { ...old }; for (const row of selected) next[Number(row)] = { ...next[Number(row)], [commonColumn]: commonValue }; return next })
    setFields(old => old.includes(commonColumn) ? old : [...old, commonColumn])
  }
  return <Drawer open title={`Correct warning rows together (${sourceRows.length})`} width="min(1480px, 98vw)" className="excel-payslip-amount-editor" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy}
    footer={<Space wrap className="excel-payslip-footer"><span>{preview.changed} changed cell(s) · {currentRows.filter(row => sourceRows.includes(row.sourceRow) && row.warnings.length).length} rows still need review</span><Button disabled={busy} onClick={onClose}>Cancel</Button><Button type="primary" loading={busy || calculation.loading} disabled={!ready || !preview.changed || !!preview.error || recalculate && (!calculation.sheet || !!calculation.error || calculation.loading)} onClick={() => onSave(edits, recalculate ? calculation.sheet : undefined)}>Save all corrections as new batch</Button></Space>}>
    <Alert type="info" showIcon message="Review and edit all warning rows, then save once" description="Linked totals follow the saved Excel formulas. Review the results before saving one corrected version. The original batch and its email history remain available; saving sends no email." />
    <Checkbox checked={recalculate} disabled={busy} onChange={event => setRecalculate(event.target.checked)}>Recalculate linked Excel formulas</Checkbox>
    {!!Object.keys(savedCorrections).length && !Object.keys(edits).length && <Button disabled={busy} onClick={() => { setRecalculate(true); setEdits(savedCorrections) }}>Recalculate saved corrections</Button>}
    {(preview.error || calculation.error || error) && <Alert type="error" showIcon message={preview.error || calculation.error || error} />}
    <Space wrap style={{ margin: '16px 0', width: '100%' }}>
      <Select aria-label="Salary columns to edit" mode="multiple" value={fields} onChange={setFields} disabled={busy} style={{ minWidth: 260, maxWidth: '100%' }} options={amounts.map(c => ({ value: c.columnIndex, label: `${c.label} (${excelColumnLetter(c.columnIndex)})` }))} />
      <Select aria-label="Bulk correction column" value={commonColumn} onChange={setCommonColumn} placeholder="Apply a value to selected rows" disabled={busy} style={{ width: 260 }} options={amounts.map(c => ({ value: c.columnIndex, label: c.label }))} />
      <InputNumber aria-label="Bulk correction amount" stringMode value={commonValue} onChange={value => setCommonValue(value == null ? null : String(value))} disabled={busy} />
      <Button onClick={applyCommon} disabled={busy || commonColumn == null || commonValue == null || !selected.length}>Apply to selected ({selected.length})</Button>
    </Space>
    <DataTable rows={rows} getRowId={row => row.id} title="Warning rows and corrected preview" exportDisabled rowSelection={{ selectedRowKeys: selected, onChange: keys => setSelected(keys.map(String)), getCheckboxProps: () => ({ disabled: busy }) }} columns={[
      { key: 'sourceRow', label: 'Source row', width: 100 },
      { key: 'employeeName', label: 'Employee', width: 200, value: entry => entry.row?.employeeName || `Source row ${entry.sourceRow}` },
      ...amounts.filter(c => fields.includes(c.columnIndex)).map(column => ({ key: `amount-${column.columnIndex}`, label: `${column.label} (${excelColumnLetter(column.columnIndex)})`, width: 165, render: (entry: typeof rows[number]) => {
        const original = (recalculate && calculation.sheet ? calculation.sheet : sheet).rows.find(row => row.sourceRow === entry.sourceRow)?.cells[column.columnIndex]?.value || ''
        const value = Object.hasOwn(edits[entry.sourceRow] || {}, column.columnIndex) ? edits[entry.sourceRow][column.columnIndex] : parsePayslipAmount(original)?.toString() ?? null
        return <InputNumber aria-label={`Correct row ${entry.sourceRow} ${column.label}`} stringMode value={value} disabled={busy} onChange={next => patch(entry.sourceRow, column.columnIndex, next == null ? null : String(next))} />
      } })),
      { key: 'difference', label: 'Net difference after edits', width: 150, value: entry => entry.row ? payslipReconciliation(entry.row).difference : '', render: entry => entry.row ? formatExcelPayslipAmount(payslipReconciliation(entry.row).difference, 2) : 'Review amount' },
      { key: 'review', label: 'Remaining warnings', width: 140, value: entry => entry.row?.warnings.join('; ') || '', render: entry => <Tag color={entry.row?.warnings.length ? 'orange' : 'green'}>{entry.row?.warnings.length || 0} warnings</Tag> },
    ]} />
  </Drawer>
}
