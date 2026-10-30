import { useMemo, useState } from 'react'
import { Button, Checkbox, Input, Select, Space, Tag } from 'antd'
import SmartBulkSourcePreview from './SmartBulkSourcePreview'
import { bulkImportMappingColor, type BulkImportDefinition } from '../utils/smartBulkImport'
import { excelColumnLetter, isLiteralBlankPayslipCell, isPayslipAmountKind, payslipColumnKinds, type PayslipColumn, type PayslipColumnKind, type PayslipSheet } from '../utils/excelPayslipImport'
import type { ExcelPayslipTemplate } from '../services/excelPayslipService'
import './ExcelPayslipMappingPreview.css'
import { usePayslipCodePreview } from '../hooks/usePayslipCodePreview'
import { orderPayslipPreviewRows, payslipPreviewRowStatus } from '../utils/excelPayslipCodePreview'
import { PayslipCodeMatchLabel, PayslipCodePreviewTools } from './ExcelPayslipCodePreview'

const definition: BulkImportDefinition = { moduleCode: 'excel-payslips', moduleLabel: 'Excel Payslips', targetSheetName: 'Payslips', fields: [] }
type Props = { sheet: PayslipSheet; headerRow: number; columns: PayslipColumn[]; fileName: string; excludedRows: number[]; template?: ExcelPayslipTemplate; disabled: boolean; onPatch: (index: number, patch: Partial<PayslipColumn>) => void; onSuggest: () => void; onExclude?: (sourceRow: number) => void; onInclude?: (sourceRow: number) => void }

export default function ExcelPayslipMappingPreview(p: Props) {
  const [open, setOpen] = useState(false)
  const [selection, setSelection] = useState({ sourceRow: 0, columnIndex: 0 })
  const codePreview = usePayslipCodePreview()
  const codeColumn = p.columns.find(column => column.kind === 'employeeCode')
  const sourceRows = useMemo(() => p.sheet.rows.filter(row => row.sourceRow > p.headerRow && row.cells.some(cell => cell?.value || cell?.formula || cell?.formulaText || cell?.error)), [p.sheet, p.headerRow])
  const codeFor = (row: typeof sourceRows[number]) => codeColumn ? row.cells[codeColumn.columnIndex]?.value || '' : ''
  const rows = orderPayslipPreviewRows(sourceRows,row=>row.sourceRow,codeFor,codePreview,row => p.excludedRows.includes(row.sourceRow))
  const previewSheet = useMemo(() => ({ name: p.sheet.name, headers: p.columns.map(column => `${excelColumnLetter(column.columnIndex)} — ${column.sourceHeader || '(blank heading)'}`), rows: rows.map(row => p.columns.map(column => row.cells[column.columnIndex]?.value || '')) }), [p.sheet.name, p.columns, rows])
  const column = p.columns[selection.columnIndex] || p.columns[0]
  const row = rows.find(row => row.sourceRow === selection.sourceRow) || rows[0]
  const cell = row?.cells[column?.columnIndex || 0]
  const address = column && row ? `${excelColumnLetter(column.columnIndex)}${row.sourceRow}` : ''
  const formula = cell?.formulaText ? (cell.formulaText.startsWith('=') ? cell.formulaText : '=' + cell.formulaText) : cell?.calculationError || (cell?.formula ? 'Formula expression unavailable' : 'Literal value — no formula')
  const chooseColumn = (index: number) => setSelection({ sourceRow: row?.sourceRow || 0, columnIndex: index })
  const kindChanged = (index: number, kind: PayslipColumnKind) => { chooseColumn(index); p.onPatch(index, { kind, componentId: undefined, blankAsZero: isPayslipAmountKind(kind) ? p.columns[index].blankAsZero : undefined }) }
  const categoryKind = (category: string): PayslipColumnKind => category === 'Earning' ? 'earning' : category === 'Deduction' ? 'deduction' : category === 'Employer' ? 'employer' : 'info'
  return <>
    <Space wrap className="excel-payslip-grid-launch"><Button disabled={p.disabled} onClick={() => { setSelection({ sourceRow: rows[0]?.sourceRow || 0, columnIndex: 0 }); setOpen(true) }}>Excel preview &amp; map</Button><span>Use the same colored worksheet preview as Employee Master bulk mapping.</span></Space>
    <SmartBulkSourcePreview key={codePreview.orderKey} open={open} onClose={() => setOpen(false)} fileName={p.fileName} sheet={previewSheet} definition={definition} mappings={{}} adapter={{
      title: 'PAYSLIP WORKING PREVIEW', helpTitle: 'Map directly above each Excel column', helpDescription: 'Select a cell to inspect its original formula and saved result. Mapping changes update this import; source cells remain unchanged.',
      columns: p.columns.map(column => ({ mapped: column.kind !== 'ignore', color: column.kind === 'ignore' ? '#94a3b8' : bulkImportMappingColor(payslipColumnKinds.findIndex(kind => kind.value === column.kind)), label: `${payslipColumnKinds.find(kind => kind.value === column.kind)?.label}: ${column.label}` })),
      rowNumbers: rows.map(row => row.sourceRow), selectedColumn: column?.columnIndex, onSelectColumn: chooseColumn,
      onSelectCell: (rowIndex, columnIndex) => setSelection({ sourceRow: rows[rowIndex].sourceRow, columnIndex }),
      renderMapping: index => <Select className="smart-bulk-preview-map-chip" aria-label={`Preview column ${excelColumnLetter(index)} use as`} disabled={p.disabled} value={p.columns[index].kind} options={payslipColumnKinds} onClick={event => event.stopPropagation()} onChange={kind => kindChanged(index, kind)} />,
      cellTitle: (rowIndex, index, value) => [rows[rowIndex].cells[index]?.formulaText, rows[rowIndex].cells[index]?.calculationError, value].filter(Boolean).join('\n'),
      cellClassName: (rowIndex, index) => p.columns[index].kind !== 'employeeCode' || !codePreview.enabled ? '' : codePreview.status !== 'Ready' ? 'excel-code-pending-cell' : payslipPreviewRowStatus(codePreview,rows[rowIndex].sourceRow,codeFor(rows[rowIndex]))==='Existing' ? 'excel-code-existing-cell' : payslipPreviewRowStatus(codePreview,rows[rowIndex].sourceRow,codeFor(rows[rowIndex]))==='Review'?'excel-code-review-cell':'excel-code-new-cell',
      renderCell: (rowIndex, index, value) => {
        const source = rows[rowIndex].cells[index]
        if (source?.error || source?.missingCachedValue) return <span className="excel-payslip-grid-error">{source.error || 'Missing saved result'}</span>
        if (source?.calculationError) return <span className="excel-payslip-grid-error">⚠ {value || 'Formula needs review'}</span>
        const content = source?.formula || source?.formulaText ? <><span className="excel-payslip-grid-fx">ƒx</span>{value || 'Missing result'}</> : value || <span className="empty-cell">{p.columns[index].blankAsZero && isLiteralBlankPayslipCell(source) ? 'Blank → 0' : 'Blank'}</span>
        return <span className={p.excludedRows.includes(rows[rowIndex].sourceRow) ? 'excel-payslip-grid-excluded' : ''}>{content}{p.columns[index].kind === 'employeeCode' && <PayslipCodeMatchLabel code={value} sourceRow={rows[rowIndex].sourceRow} />}</span>
      },
      beforeTable: <div className="excel-payslip-grid-tools">
        <div className="excel-payslip-formula-bar"><Tag>{address || 'Select a cell'}</Tag><Input aria-label="Payslip source formula" readOnly value={formula} /><Input aria-label="Payslip saved cell result" readOnly value={cell?.error || (cell?.value ?? '')} placeholder="Blank in source" /></div>
        <PayslipCodePreviewTools compact />
        <Space wrap><Button size="small" disabled={p.disabled} onClick={p.onSuggest}>Use suggested mapping</Button>{row && p.excludedRows.includes(row.sourceRow) && <Tag>Excluded source row</Tag>}{cell?.calculationError && <Tag color="orange">Formula needs review</Tag>}
          {row && (p.excludedRows.includes(row.sourceRow) ? p.onInclude && <Button size="small" disabled={p.disabled} onClick={() => p.onInclude?.(row.sourceRow)}>Include source row {row.sourceRow}</Button> : p.onExclude && <Button size="small" danger disabled={p.disabled} onClick={() => p.onExclude?.(row.sourceRow)}>Exclude source row {row.sourceRow}</Button>)}
        </Space>
        {column && <details className="excel-payslip-grid-column-options"><summary>Column {excelColumnLetter(column.columnIndex)} options — {column.sourceHeader || '(blank heading)'}</summary><div>
          <label>Payslip label<Input aria-label="Preview payslip label" value={column.label} maxLength={80} disabled={p.disabled} onChange={event => p.onPatch(column.columnIndex, { label: event.target.value })} /></label>
          <label>Template component<Select aria-label="Preview template component" disabled={p.disabled || !p.template} value={column.componentId || ''} options={[{ value: '', label: 'Manual / not mapped' }, ...(p.template?.components || []).map(item => ({ value: String(item.id), label: `${item.code} — ${item.name}` }))]} onChange={id => { const component = p.template?.components.find(item => String(item.id) === id); p.onPatch(column.columnIndex, component ? { componentId: id, label: component.name, kind: categoryKind(component.category), blankAsZero: isPayslipAmountKind(categoryKind(component.category)) ? column.blankAsZero : undefined } : { componentId: undefined }) }} /></label>
          <Checkbox aria-label="Preview blank amounts as zero" checked={column.blankAsZero === true} disabled={p.disabled || !isPayslipAmountKind(column.kind)} onChange={event => p.onPatch(column.columnIndex, { blankAsZero: event.target.checked })}>Treat literal blanks as zero</Checkbox>
        </div></details>}
      </div>,
    }} />
  </>
}
