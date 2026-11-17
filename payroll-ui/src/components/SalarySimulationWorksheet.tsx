import { useEffect, useMemo, useRef, useState } from 'react'
import { Button, Input, Tag } from 'antd'
import type { SimulationResult } from '../services/payrollCalculationService'
import { alignSalaryWorksheetRows, resolveSalaryWorksheetSelection, salaryColumnLetter, salarySimulationWorksheet, type SalaryWorksheetSelection } from '../utils/salarySimulationWorksheet'
import './SalarySimulationWorksheet.css'

export type SalaryWorksheetEmployeeRow = { key: string; result: SimulationResult; employeeCode?: string; employeeName?: string; edits: Record<string, string>; onEdit: (key: string, value: string) => void; stale?: boolean; status?: string }
const display = (value: number | string | null) => value == null ? '' : typeof value === 'number' ? value.toLocaleString('en-IN', { maximumFractionDigits: 8 }) : value
export default function SalarySimulationWorksheet({ result, employeeCode, employeeName, month, clientName, edits, onEdit, onRuleEdit, stale = false, rows, presentOnly = false, onRowSelect }: {
  result: SimulationResult; employeeCode?: string; employeeName?: string; month: string; clientName: string;
  edits: Record<string, string>; onEdit: (key: string, value: string) => void; onRuleEdit?: (componentId: string, formula: string) => void; stale?: boolean;
  rows?: SalaryWorksheetEmployeeRow[]; presentOnly?: boolean; onRowSelect?: (key: string) => void;
}) {
  const sources: SalaryWorksheetEmployeeRow[] = rows ?? [{ key: String(result.employeeId || 'scenario'), result, employeeCode, employeeName, edits, onEdit, stale }]
  const models = useMemo(() => sources.map(source => salarySimulationWorksheet(source.result, { employeeCode: source.employeeCode, employeeName: source.employeeName, month, clientName })), [rows, result, employeeCode, employeeName, month, clientName]) // eslint-disable-line react-hooks/exhaustive-deps
  const aligned = useMemo(() => alignSalaryWorksheetRows(models), [models])
  const [selection, setSelection] = useState<SalaryWorksheetSelection>({ row: 0, column: 0 }), [formulaDraft, setFormulaDraft] = useState('')
  const [ruleEditing, setRuleEditing] = useState(false)
  const { row: rowIndex, column: columnIndex } = resolveSalaryWorksheetSelection(selection, sources.map(row => row.key), aligned.keys)
  const source = sources[rowIndex], cell = aligned.rows[rowIndex]?.[columnIndex]
  const refs = useRef(new Map<string, HTMLButtonElement | HTMLInputElement>())
  const canEdit = (inputKey?: string) => !!inputKey && (!presentOnly || inputKey === 'PRESENT_DAYS')
  useEffect(() => { setFormulaDraft(cell?.configuredFormula || cell?.formula || String(cell?.value ?? '')) }, [cell?.address, cell?.formula, cell?.configuredFormula, cell?.value])
  useEffect(() => { setRuleEditing(false) }, [cell?.address])
  const select = (row: number, column: number, focus = false) => {
    const next = { row: Math.max(0, Math.min(aligned.rows.length - 1, row)), column: Math.max(0, Math.min(aligned.labels.length - 1, column)) }
    setSelection({ ...next, rowKey: sources[next.row].key, columnKey: aligned.keys[next.column] }); onRowSelect?.(sources[next.row].key)
    if (focus) refs.current.get(`${next.row}:${next.column}`)?.focus()
  }
  const editableRule = !!onRuleEdit && !!cell?.ruleComponentId && !cell.inputKey
  const notice = [...new Set(models.map(model => model.notice).filter(Boolean))].join(' ')
  return <section className={`salary-worksheet${!rows && stale ? ' is-stale' : ''}`} aria-label="Excel-style salary simulation">
    <div className="salary-worksheet-caption"><span>Payroll worksheet</span><Tag>{sources.some(row => row.stale) ? 'Recalculating · previous amounts marked' : 'Native payroll result'}</Tag><span>Temporary what-if · no data saved</span></div>
    <div className="salary-worksheet-formula-bar"><span className="salary-worksheet-address">{cell?.address || 'A2'}</span><strong aria-hidden="true">fx</strong>
      {canEdit(cell?.inputKey) ? <Input aria-label={`Worksheet ${cell.label} input`} value={source.edits[cell.inputKey!] ?? String(cell.value ?? '')} placeholder={presentOnly ? 'Enter present days' : 'Use saved input'} onChange={event => source.onEdit(cell.inputKey!, event.target.value)} /> : <Input aria-label={ruleEditing ? 'Temporary payroll rule' : 'Selected cell formula'} readOnly={!ruleEditing} value={ruleEditing ? formulaDraft : cell?.formula || cell?.configuredFormula || String(cell?.value ?? '')} onChange={event => setFormulaDraft(event.target.value)} onPressEnter={() => { if (editableRule && ruleEditing && cell.ruleComponentId) { onRuleEdit?.(cell.ruleComponentId, formulaDraft); setRuleEditing(false) } }} />}
      {editableRule && <Button size="small" onClick={() => { if (ruleEditing) { onRuleEdit?.(cell.ruleComponentId!, formulaDraft); setRuleEditing(false) } else setRuleEditing(true) }}>{ruleEditing ? 'Apply temporary rule' : 'Edit payroll rule'}</Button>}
    </div>
    <div className="salary-worksheet-cell-detail"><b>{cell?.label}</b>{rows && <span>{source.employeeCode} · {source.employeeName}</span>}{cell?.source && <span>{cell.source}</span>}{cell?.configuredFormula && cell.formula && <code>Configured rule: {cell.configuredFormula}</code>}</div>
    <div className="salary-worksheet-scroll" tabIndex={0} aria-label="Scrollable salary worksheet">
      <table role="grid" aria-label="Salary calculation cells"><thead><tr><th aria-label="Row" /><th colSpan={aligned.labels.length} className="salary-worksheet-sheet-title">{clientName} · {month}</th></tr><tr><th />{aligned.labels.map((label, index) => <th key={`${index}:${label}`}>{salaryColumnLetter(index)}</th>)}</tr><tr><th>1</th>{aligned.labels.map((label, index) => <th key={`${index}:${label}`} className={`salary-worksheet-column ${aligned.rows.some(row => canEdit(row[index].inputKey)) ? 'is-input' : ''}`} title={label}>{label}</th>)}</tr></thead>
        <tbody>{aligned.rows.map((cells, row) => <tr key={sources[row].key} className={sources[row].stale ? 'salary-worksheet-stale-row' : ''}><th title={sources[row].status}>{row + 2}</th>{cells.map((item, index) => {
          const editable = canEdit(item.inputKey), selected = row === rowIndex && index === columnIndex
          return <td key={item.address} className={`${selected ? 'is-selected' : ''} ${editable ? 'is-input' : ''} ${item.label === 'Net pay' || item.label === 'Net pay earned' ? 'is-net' : ''}`}>
            {editable ? <input ref={element => { if (element) refs.current.set(`${row}:${index}`, element); else refs.current.delete(`${row}:${index}`) }} aria-label={`${sources[row].employeeCode || 'Scenario'} ${item.label}`} inputMode="decimal" value={sources[row].edits[item.inputKey!] ?? String(item.value ?? '')} placeholder={item.inputKey === 'PRESENT_DAYS' ? 'Enter days' : 'Not supplied'} onFocus={() => select(row, index)} onChange={event => sources[row].onEdit(item.inputKey!, event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); select(row + 1, index, true) } }} /> :
              <button type="button" ref={element => { if (element) refs.current.set(`${row}:${index}`, element); else refs.current.delete(`${row}:${index}`) }} tabIndex={selected ? 0 : -1} aria-label={`${item.address} ${item.label}: ${item.value ?? 'Not supplied'}`} aria-pressed={selected} onClick={() => select(row, index)} onKeyDown={event => { if (event.key === 'ArrowRight') { event.preventDefault(); select(row, index + 1, true) } if (event.key === 'ArrowLeft') { event.preventDefault(); select(row, index - 1, true) } if (event.key === 'ArrowUp') { event.preventDefault(); select(row - 1, index, true) } if (event.key === 'ArrowDown') { event.preventDefault(); select(row + 1, index, true) } }}>
                {display(item.value) || <span className="salary-worksheet-missing">Not supplied</span>}
              </button>}
          </td>
        })}</tr>)}</tbody></table>
    </div>
    <div className="salary-worksheet-footer"><span>{rows ? `${rows.length} employees` : 'Payroll'}</span><span>{presentOnly ? 'Edit yellow Present days cells · select an amount to inspect its formula' : 'Input cells are highlighted · select a cell to view its formula'}</span></div>
    {notice && <p className="salary-worksheet-notice">{notice}</p>}
  </section>
}
