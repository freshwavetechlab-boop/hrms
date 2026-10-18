import { useMemo, useState } from 'react'
import { Alert, Button, Collapse, Descriptions, Drawer, Form, InputNumber, Space, Tag } from 'antd'
import DataTable from './DataTable'
import { buildExcelPayslipRows, excelColumnLetter, formatExcelPayslipAmount, isPayslipAmountKind, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from '../utils/excelPayslipImport'
import { correctPayslipAmounts, payslipReconciliation } from '../utils/excelPayslipReview'
import type { ExcelPayslipRow } from '../services/excelPayslipService'

const preciseAmount = (value: number) => formatExcelPayslipAmount(value, 2)
const sourceValue = (row: ExcelPayslipRow, label: string) => row.information.find(item => item.label.toLowerCase() === label)?.value

export function PayslipReconciliation({ row }: { row: ExcelPayslipRow }) {
  const values = payslipReconciliation(row)
  return <div className="excel-payslip-reconciliation">
    <Descriptions bordered column={1} size="small">
      <Descriptions.Item label="Mapped earnings">{preciseAmount(values.earnings)}</Descriptions.Item>
      <Descriptions.Item label="Mapped deductions">{preciseAmount(values.deductions)}</Descriptions.Item>
      <Descriptions.Item label="Earnings less deductions">{preciseAmount(values.calculatedNet)}</Descriptions.Item>
      <Descriptions.Item label="Source In Hand Salary">{preciseAmount(row.netPay)}</Descriptions.Item>
      <Descriptions.Item label="Difference">{preciseAmount(values.difference)}</Descriptions.Item>
      {row.declaredGross != null && <Descriptions.Item label="Source gross / difference">{preciseAmount(row.declaredGross)} / {preciseAmount(values.grossDifference!)}</Descriptions.Item>}
      {row.declaredDeductions != null && <Descriptions.Item label="Source deductions / difference">{preciseAmount(row.declaredDeductions)} / {preciseAmount(values.deductionDifference!)}</Descriptions.Item>}
    </Descriptions>
    {values.wholeRupeeMatch && Math.abs(values.difference) > .01 && <Alert type="info" showIcon message="Whole-rupee net pay matches" description="The calculated and source net pay display the same in whole rupees. Check the original amounts before changing them." />}
  </div>
}

export function ExcelPayslipWarnings({ rows, open, busy, canManage, error, onClose, onReview, onCorrect, onMapping }: {
  rows: ExcelPayslipRow[]; open: boolean; busy: boolean; canManage: boolean; error: string
  onClose: () => void; onReview: (row: ExcelPayslipRow) => void; onCorrect: (row: ExcelPayslipRow) => void; onMapping: () => void
}) {
  const warnings = rows.filter(row => row.warnings.length)
  return <Drawer title={`Payslip warnings (${warnings.length})`} open={open} onClose={onClose} width="min(1080px, 98vw)" className="excel-payslip-warning-review" footer={<Space wrap className="excel-payslip-footer"><Button onClick={onClose} disabled={busy}>Close</Button>{canManage && <Button onClick={onMapping} disabled={busy}>Edit column mapping</Button>}</Space>}>
    <Alert type="info" showIcon message="Review the reason before correcting salary" description="Details show the source amounts and differences. Corrections save the complete batch as a new version; the previous batch and its email history remain available." />
    {error && <Alert type="error" showIcon message={error} />}
    <DataTable title="Employees with warnings" rows={warnings} getRowId={row => row.id} exportFileName="payslip-warnings" emptyText="No warnings in this batch." columns={[
      { key: 'employeeName', label: 'Employee', width: 190, render: row => <><strong>{row.employeeName}</strong><div className="excel-payslip-muted">{row.employeeCode}</div></> },
      { key: 'source', label: 'Source', width: 210, value: row => `${sourceValue(row, 'source workbook') || ''} · Row ${sourceValue(row, 'source excel row') || row.sourceRow}`, wrap: true },
      { key: 'warnings', label: 'Issue', width: 320, value: row => row.warnings.join('; '), wrap: true },
      { key: 'difference', label: 'Net difference', width: 135, value: row => payslipReconciliation(row).difference, render: row => preciseAmount(payslipReconciliation(row).difference) }
    ]} actionsWidth={210} actions={row => <Space wrap size={4}><Button size="small" onClick={() => onReview(row)} disabled={busy}>Details</Button>{canManage && <Button size="small" onClick={() => onCorrect(row)} disabled={busy}>Correct amounts</Button>}</Space>} />
  </Drawer>
}

export default function ExcelPayslipAmountEditor({ sheet, columns, sourceRow, busy, ready, error, onClose, onSave }: {
  sheet: PayslipSheet; columns: PayslipColumn[]; sourceRow: number; busy: boolean; ready: boolean; error: string
  onClose: () => void; onSave: (edits: Record<number, string | null>) => void
}) {
  const [edits, setEdits] = useState<Record<number, string | null>>({})
  const source = sheet.rows.find(row => row.sourceRow === sourceRow)!
  const name = source?.cells[columns.find(column => column.kind === 'employeeName')?.columnIndex ?? -1]?.value || `Source row ${sourceRow}`
  const preview = useMemo(() => {
    try {
      const corrected = correctPayslipAmounts(sheet, sourceRow, columns, edits)
      const smallSheet = { ...corrected.sheet, rows: corrected.sheet.rows.filter(row => row.sourceRow === sourceRow || row.sourceRow === sheet.template!.headerRow) }
      const imported = buildExcelPayslipRows(smallSheet, sheet.template!.headerRow, columns)
      return { ...corrected, row: imported.rows[0], error: imported.issues.map(issue => issue.message).join(' ') }
    } catch (failure) { return { changed: 0, row: undefined, error: failure instanceof Error ? failure.message : 'Review the entered amounts.' } }
  }, [sheet, sourceRow, columns, edits])
  const groups = [
    { key: 'totals', label: 'Source totals', kinds: ['netPay', 'grossTotal', 'deductionTotal'] },
    { key: 'earning', label: 'Earnings', kinds: ['earning'] },
    { key: 'deduction', label: 'Deductions', kinds: ['deduction'] },
    { key: 'employer', label: 'Employer contributions', kinds: ['employer'] }
  ]
  return <Drawer open title={`Correct amounts · ${name}`} width="min(840px, 98vw)" className="excel-payslip-amount-editor" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space wrap className="excel-payslip-footer"><Button onClick={onClose} disabled={busy}>Cancel</Button><Button type="primary" loading={busy} disabled={!ready || !preview.changed || !!preview.error} onClick={() => onSave(edits)}>Save corrected batch</Button></Space>}>
    <Alert type="info" showIcon message="Save a new batch version" description="Other cells stay unchanged. A corrected formula amount applies to this batch; re-import a reviewed formula before using it to create another month. Use column mapping to change categories. The new batch has separate email tracking; no emails are sent by saving." />
    {preview.row && <PayslipReconciliation row={preview.row} />}
    {(preview.error || error) && <Alert type="error" showIcon message={preview.error || error} />}
    <Form layout="vertical" component="div" disabled={busy}>
      <Collapse defaultActiveKey={['totals', 'earning', 'deduction']}>{groups.filter(group => columns.some(column => group.kinds.includes(column.kind))).map(group => <Collapse.Panel key={group.key} header={group.label}><div className="excel-payslip-correction-grid">{columns.filter(column => isPayslipAmountKind(column.kind) && group.kinds.includes(column.kind)).map(column => {
        const cell = source.cells[column.columnIndex], letter = excelColumnLetter(column.columnIndex), value = Object.hasOwn(edits, column.columnIndex) ? edits[column.columnIndex] : parsePayslipAmount(cell?.value || '')?.toString() ?? null
        return <Form.Item key={column.columnIndex} label={`${column.label} · ${letter}`} extra={cell?.formulaText ? <Tag>Saved formula</Tag> : undefined}><InputNumber aria-label={`Correct ${column.label} (${letter})`} stringMode value={value} onChange={next => setEdits(current => ({ ...current, [column.columnIndex]: next === null ? null : String(next) }))} /></Form.Item>
      })}</div></Collapse.Panel>)}</Collapse>
    </Form>
  </Drawer>
}
