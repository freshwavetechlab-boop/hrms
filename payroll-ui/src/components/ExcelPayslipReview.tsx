import { useMemo, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Descriptions, Drawer, Form, InputNumber, Space, Tag } from 'antd'
import { usePayslipCorrectionPreview } from '../hooks/usePayslipCorrectionPreview'
import DataTable from './DataTable'
import { buildExcelPayslipRows, excelColumnLetter, formatExcelPayslipAmount, isPayslipAmountKind, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from '../utils/excelPayslipImport'
import { correctPayslipAmounts, formatPayslipDifference, negativePayslipAmountWarning, payslipReconciliation, payslipWarningDetails, savedPayslipCorrectionInputs } from '../utils/excelPayslipReview'
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
      <Descriptions.Item label="Net difference">{formatPayslipDifference(values.difference)}</Descriptions.Item>
      {row.declaredGross != null && <Descriptions.Item label="Source gross / difference">{preciseAmount(row.declaredGross)} / {formatPayslipDifference(values.grossDifference!)}</Descriptions.Item>}
      {row.declaredDeductions != null && <Descriptions.Item label="Source deductions / difference">{preciseAmount(row.declaredDeductions)} / {formatPayslipDifference(values.deductionDifference!)}</Descriptions.Item>}
    </Descriptions>
    {values.wholeRupeeMatch && Math.abs(values.difference) > .01 && <Alert type="info" showIcon message="Whole-rupee net pay matches" description="The calculated and source net pay display the same in whole rupees. Check the original amounts before changing them." />}
  </div>
}

export function ExcelPayslipWarnings({ rows, open, busy, canManage, error, onClose, onReview, onCorrect, onMapping, onBulkCorrect, onReviewNegative, onClearWarnings }: {
  rows: ExcelPayslipRow[]; open: boolean; busy: boolean; canManage: boolean; error: string
  onClose: () => void; onReview: (row: ExcelPayslipRow) => void; onCorrect: (row: ExcelPayslipRow) => void; onMapping: () => void
  onBulkCorrect?: () => void
  onReviewNegative?: (row: ExcelPayslipRow) => void
  onClearWarnings?: (rows: ExcelPayslipRow[], action: 'review' | 'restore') => void
}) {
  const warnings = rows.filter(row => row.warnings.length)
  const reviewed = rows.filter(row => row.warningReview)
  const reviewReady = !!onClearWarnings && warnings.every(row => !!row.warningReviewToken)
  const differences = (row: ExcelPayslipRow) => {
    const values = payslipReconciliation(row)
    return [values.grossDifference == null ? '' : `Gross: ${formatPayslipDifference(values.grossDifference)}`,
      values.deductionDifference == null ? '' : `Deductions: ${formatPayslipDifference(values.deductionDifference)}`,
      `Net: ${formatPayslipDifference(values.difference)}`].filter(Boolean)
  }
  return <Drawer title={`Payslip warnings (${warnings.length})`} open={open} onClose={onClose} width="min(1080px, 98vw)" className="excel-payslip-warning-review" footer={<Space wrap className="excel-payslip-footer"><Button onClick={onClose} disabled={busy}>Close</Button>{canManage && <><Button type="primary" loading={busy} disabled={busy || !warnings.length || !reviewReady} onClick={() => onClearWarnings?.(warnings, 'review')}>Clear all warnings ({warnings.length})</Button><Button onClick={onMapping} disabled={busy}>Edit column mapping</Button>{onBulkCorrect && <Button onClick={onBulkCorrect} disabled={busy || !warnings.length}>Fix all warning rows</Button>}</>}</Space>}>
    <Alert type="info" showIcon message="Clear warnings after reviewing the source" description="Clear warning accepts the saved amounts as-is and records who reviewed them. Salary amounts and formulas stay unchanged. You can restore warnings below; changing source amounts requires a fresh review." />
    {error && <Alert type="error" showIcon message={error} />}
    {canManage && warnings.length > 0 && !reviewReady && <Alert type="warning" message="Refresh the saved batch to load warning review controls." />}
    <DataTable title="Employees with warnings" rows={warnings} getRowId={row => row.id} exportFileName="payslip-warnings" emptyText="No warnings in this batch." columns={[
      { key: 'employeeName', label: 'Employee', width: 175, render: row => <><strong>{row.employeeName}</strong><div className="excel-payslip-muted">{row.employeeCode}</div></> },
      { key: 'source', label: 'Source', width: 125, value: row => `${sourceValue(row, 'source workbook') || ''} · Row ${sourceValue(row, 'source excel row') || row.sourceRow}`, wrap: true },
      { key: 'warnings', label: 'Reason / what to check', width: 340, value: row => payslipWarningDetails(row).join('\n'), render: row => payslipWarningDetails(row).map((detail, index) => <div key={index}>{detail}</div>), wrap: true },
      { key: 'difference', label: 'Differences', width: 160, value: row => differences(row).join('; '), render: row => differences(row).map(detail => <div key={detail}>{detail}</div>) }
    ]} actionsWidth={225} actions={row => <Space wrap size={4}><Button size="small" onClick={() => onReview(row)} disabled={busy}>Details</Button>{canManage && <><Button size="small" type="primary" onClick={() => onClearWarnings?.([row], 'review')} disabled={busy || !onClearWarnings || !row.warningReviewToken}>Clear warning</Button><Button size="small" onClick={() => onCorrect(row)} disabled={busy}>Correct amounts</Button>{onReviewNegative && row.warnings.includes(negativePayslipAmountWarning) && <Button size="small" onClick={() => onReviewNegative(row)} disabled={busy}>Mark negative amount reviewed</Button>}</>}</Space>} />
    {!!reviewed.length && <Collapse style={{ marginTop: 16 }}><Collapse.Panel key="reviewed" header={`Reviewed as-is (${reviewed.length})`}><DataTable title="Reviewed warnings" rows={reviewed} getRowId={row => row.id} exportFileName="payslip-warning-reviews" columns={[
      { key: 'employeeName', label: 'Employee', width: 190, value: row => `${row.employeeName} · ${row.employeeCode}` },
      { key: 'review', label: 'Reviewed by / time', width: 240, value: row => `${row.warningReview!.reviewedBy} · ${new Date(row.warningReview!.reviewedAtUtc).toLocaleString()}`, wrap: true },
      { key: 'warnings', label: 'Accepted source warnings', width: 320, value: row => row.warningReview!.warnings.join('; '), wrap: true }
    ]} actions={canManage ? row => <Button size="small" disabled={busy || !onClearWarnings || !row.warningReviewToken} onClick={() => onClearWarnings?.([row], 'restore')}>Restore warning</Button> : undefined} /></Collapse.Panel></Collapse>}
  </Drawer>
}

export default function ExcelPayslipAmountEditor({ sheet, columns, sourceRow, busy, ready, error, onClose, onSave }: {
  sheet: PayslipSheet; columns: PayslipColumn[]; sourceRow: number; busy: boolean; ready: boolean; error: string
  onClose: () => void; onSave: (edits: Record<number, string | null>, calculated?: PayslipSheet) => void
}) {
  const [edits, setEdits] = useState<Record<number, string | null>>({})
  const [recalculate, setRecalculate] = useState(true)
  const calculation = usePayslipCorrectionPreview(sheet, columns, { [sourceRow]: edits }, recalculate)
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
  const calculatedRow = calculation.rows.find(row => row.sourceRow === sourceRow) || (!Object.keys(edits).length ? preview.row : undefined)
  const savedCorrections = savedPayslipCorrectionInputs(sheet, columns, [sourceRow])[sourceRow]
  return <Drawer open title={`Correct amounts · ${name}`} width="min(840px, 98vw)" className="excel-payslip-amount-editor" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space wrap className="excel-payslip-footer"><Button onClick={onClose} disabled={busy}>Cancel</Button><Button type="primary" loading={busy || calculation.loading} disabled={!ready || !preview.changed || !!preview.error || recalculate && (!calculation.sheet || !!calculation.error || calculation.loading)} onClick={() => onSave(edits, recalculate ? calculation.sheet : undefined)}>Save corrected batch</Button></Space>}>
    <Alert type="info" showIcon message="Save a new batch version" description="Linked amounts follow the saved Excel formulas. A manual formula override applies to this month and needs review before reuse. Turn recalculation off only to enter totals manually. The new batch has separate email tracking; saving sends no mail." />
    <Checkbox checked={recalculate} disabled={busy} onChange={event => setRecalculate(event.target.checked)}>Recalculate linked Excel formulas</Checkbox>
    {savedCorrections && !Object.keys(edits).length && <Button disabled={busy} onClick={() => { setRecalculate(true); setEdits(savedCorrections) }}>Recalculate saved corrections</Button>}
    {(recalculate ? calculatedRow : preview.row) && <PayslipReconciliation row={(recalculate ? calculatedRow : preview.row)!} />}
    {(preview.error || calculation.error || error) && <Alert type="error" showIcon message={preview.error || calculation.error || error} />}
    <Form layout="vertical" component="div" disabled={busy}>
      <Collapse defaultActiveKey={['totals', 'earning', 'deduction']}>{groups.filter(group => columns.some(column => group.kinds.includes(column.kind))).map(group => <Collapse.Panel key={group.key} header={group.label}><div className="excel-payslip-correction-grid">{columns.filter(column => isPayslipAmountKind(column.kind) && group.kinds.includes(column.kind)).map(column => {
        const cell = source.cells[column.columnIndex], calculated = recalculate ? calculation.sheet?.rows.find(row => row.sourceRow === sourceRow)?.cells[column.columnIndex] : undefined
        const letter = excelColumnLetter(column.columnIndex), value = Object.hasOwn(edits, column.columnIndex) ? edits[column.columnIndex] : parsePayslipAmount(calculated?.value ?? cell?.value ?? '')?.toString() ?? null
        return <Form.Item key={column.columnIndex} label={`${column.label} · ${letter}`} extra={cell?.formulaText ? <Tag>Saved formula</Tag> : undefined}><InputNumber aria-label={`Correct ${column.label} (${letter})`} stringMode value={value} onChange={next => setEdits(current => ({ ...current, [column.columnIndex]: next === null ? null : String(next) }))} /></Form.Item>
      })}</div></Collapse.Panel>)}</Collapse>
    </Form>
  </Drawer>
}
