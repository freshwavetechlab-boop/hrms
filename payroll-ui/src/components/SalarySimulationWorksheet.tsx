import { useEffect, useMemo, useRef, useState } from 'react'
import { Button, Input, Tag } from 'antd'
import type { SimulationResult } from '../services/payrollCalculationService'
import type { Component, Structure } from '../types/payroll'
import { alignSalaryWorksheetRows, resolveSalaryWorksheetSelection, salaryColumnLetter, salarySimulationSummary, type SalaryWorksheetCell, type SalaryWorksheetSelection } from '../utils/salarySimulationWorksheet'
import './SalarySimulationWorksheet.css'

export type SalaryWorksheetEmployeeRow = { key: string; result: SimulationResult; employeeCode?: string; employeeName?: string; defaultPayrollDays?: number | null; edits: Record<string, string>; onEdit: (key: string, value: string) => void; stale?: boolean; status?: string }
const display = (value: number | string | null | undefined) => value == null ? '' : typeof value === 'number' ? value.toLocaleString('en-IN', { maximumFractionDigits: 8 }) : value
function PayrollDivisorInput({ saved, edit, onEdit }: { saved: number | null | undefined; edit?: string; onEdit: (value: string) => void }) {
  const [clearing, setClearing] = useState(false)
  const fallback = saved == null ? '' : String(saved)
  return <Input aria-label="Simulation Payroll divisor" type="number" inputMode="numeric" min={1} max={31} step={1}
    value={edit || (clearing ? '' : fallback)} placeholder={fallback}
    onChange={event => { setClearing(!event.target.value); onEdit(event.target.value) }} onBlur={() => setClearing(false)} />
}
export default function SalarySimulationWorksheet({ result, employeeCode, employeeName, defaultPayrollDays, month, clientName, edits, onEdit, onRuleEdit, stale = false, rows, presentOnly = false, onRowSelect, template, components }: {
  result: SimulationResult; employeeCode?: string; employeeName?: string; month: string; clientName: string;
  defaultPayrollDays?: number | null;
  edits: Record<string, string>; onEdit: (key: string, value: string) => void; onRuleEdit?: (componentId: string, formula: string) => void; stale?: boolean;
  rows?: SalaryWorksheetEmployeeRow[]; presentOnly?: boolean; onRowSelect?: (key: string) => void;
  template?: Structure; components?: Component[];
}) {
  const sources: SalaryWorksheetEmployeeRow[] = rows ?? [{ key: String(result.employeeId || 'scenario'), result, employeeCode, employeeName, defaultPayrollDays, edits, onEdit, stale }]
  const models = useMemo(() => sources.map(source => salarySimulationSummary(source.result, { employeeCode: source.employeeCode, employeeName: source.employeeName, month, clientName, template, components })), [rows, result, employeeCode, employeeName, month, clientName, template, components]) // eslint-disable-line react-hooks/exhaustive-deps
  const aligned = useMemo(() => {
    if (!template) return alignSalaryWorksheetRows(models)
    // The saved template defines the compact schema. Input availability may
    // differ by employee without producing duplicate component columns.
    const labels = template.lines.map((line, index) => models[0]?.cells[index]?.label || components?.find(component => String(component.id) === String(line.componentId))?.name || `Component #${line.componentId}`)
    return { keys: template.lines.map((line, index) => `component:${line.componentId}:${index}`), labels,
      rows: models.map((item, row) => labels.map((label, index): SalaryWorksheetCell => ({ ...(item.cells[index] ?? { label, value: null, formula: '', pending: true }), label, address: `${salaryColumnLetter(index)}${row + 2}` }))) }
  }, [models, template, components])
  const [selection, setSelection] = useState<SalaryWorksheetSelection>({ row: 0, column: 0 }), [formulaDraft, setFormulaDraft] = useState('')
  const [ruleEditing, setRuleEditing] = useState(false)
  const { row: rowIndex, column: columnIndex } = resolveSalaryWorksheetSelection(selection, sources.map(row => row.key), aligned.keys)
  const source = sources[rowIndex], model = models[rowIndex], cell = aligned.rows[rowIndex]?.[columnIndex]
  const refs = useRef(new Map<string, HTMLButtonElement | HTMLInputElement>())
  const canEdit = (inputKey?: string) => !!inputKey && (!presentOnly || ['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS'].includes(inputKey))
  useEffect(() => { setFormulaDraft(cell?.configuredFormula || cell?.formula || String(cell?.value ?? '')) }, [cell?.address, cell?.formula, cell?.configuredFormula, cell?.value])
  useEffect(() => { setRuleEditing(false) }, [cell?.address])
  const select = (row: number, column: number, focus = false) => {
    if (!sources.length) return
    const next = { row: Math.max(0, Math.min(aligned.rows.length - 1, row)), column: Math.max(0, Math.min(aligned.labels.length - 1, column)) }
    setSelection({ ...next, rowKey: sources[next.row].key, columnKey: aligned.keys[next.column] }); onRowSelect?.(sources[next.row].key)
    if (focus) refs.current.get(`${next.row}:${next.column}`)?.focus()
  }
  const editableRule = !!onRuleEdit && !!cell?.ruleComponentId && !cell.inputKey
  const notice = [...new Set(models.map(item => item.notice).filter(Boolean))].join(' ')
  const inputValue = (item: SalaryWorksheetCell, row: SalaryWorksheetEmployeeRow) => row.edits[item.inputKey!] ?? String(item.inputValue ?? '')
  const inputUnit = (item: SalaryWorksheetCell) => item.inputUnit || (/daily rate/i.test(item.source || '') ? 'Daily rate' : 'Monthly amount')
  if (!source || !model) return null
  const currentResult = source.result
  const hasPreviousResult = source.stale && currentResult.lines.length > 0
  const status = hasPreviousResult ? 'Previous result · needs recalculation or review' : source.stale ? 'Calculation pending or needs review' : currentResult.complete ? 'Current native result' : 'Needs inputs or review'
  return <section className={`salary-worksheet${!rows && stale ? ' is-stale' : ''}`} aria-label="Salary template simulation">
    <div className="salary-worksheet-caption"><span>{template?.lines.length ?? aligned.labels.length} template components</span><Tag>{status}</Tag><span>{clientName} · {month}</span></div>
    <div className="salary-worksheet-identity" aria-label="Selected employee or scenario">
      {(model.identity ?? []).map(item => <label key={item.label}><span>{item.label}</span>{canEdit(item.inputKey)
        ? <Input size="small" aria-label={`Simulation ${item.label}`} value={source.edits[item.inputKey!] ?? String(item.value ?? '')} placeholder={item.label} onChange={event => source.onEdit(item.inputKey!, event.target.value)} onBlur={() => { if (item.inputKey === 'SCENARIO_EMPLOYEE_CODE' && !(source.edits[item.inputKey] ?? String(item.value ?? '')).trim()) source.onEdit(item.inputKey, 'EMPLOYEE-001') }} />
        : <strong>{display(item.value) || 'Not supplied'}</strong>}</label>)}
    </div>
    <div className="salary-worksheet-attendance" aria-label="Simulation attendance">
      {(model.attendance ?? []).map(item => <label key={item.inputKey || item.label}><span>{item.label}</span>{item.inputKey === 'PAYROLL_DAYS'
        ? <PayrollDivisorInput key={`${source.key}:${month}`} saved={source.defaultPayrollDays} edit={source.edits.PAYROLL_DAYS} onEdit={value => source.onEdit('PAYROLL_DAYS', value)} />
        : canEdit(item.inputKey)
        ? <Input aria-label={`Simulation ${item.label}`} inputMode="decimal" value={source.edits[item.inputKey!] ?? String(item.value ?? '')} placeholder="Enter days" onChange={event => source.onEdit(item.inputKey!, event.target.value)} />
        : <strong>{display(item.value) || 'Not supplied'}</strong>}</label>)}
      {rows && <span className="salary-worksheet-attendance-context">Attendance and totals for <b>{source.employeeCode} · {source.employeeName}</b>. Select another employee row to review it.</span>}
    </div>
    <div className="salary-worksheet-body">
      <div className="salary-worksheet-main">
        <div className="salary-worksheet-scroll" tabIndex={0} aria-label="Scrollable template components">
          <table role="grid" aria-label="Template component amounts"><thead><tr>
            {rows && <th className="salary-worksheet-employee-column" scope="col">Employee</th>}
            {aligned.labels.map((label, index) => {
              const configured = aligned.rows.map(row => row[index]).find(item => item?.configuredFormula)
              return <th scope="col" key={`${index}:${label}`} className={`salary-worksheet-column ${aligned.rows.some(row => canEdit(row[index]?.inputKey)) ? 'is-input' : ''}`} title={label}><strong>{label}</strong>{configured?.configuredFormula && <code>{configured.configuredFormula}</code>}</th>
            })}
          </tr></thead><tbody>{aligned.rows.map((cells, row) => <tr key={sources[row].key} className={`${sources[row].stale ? 'salary-worksheet-stale-row' : ''} ${row === rowIndex ? 'is-active-row' : ''}`}>
            {rows && <th scope="row" className="salary-worksheet-employee-column"><button type="button" title={sources[row].status} aria-pressed={row === rowIndex} onClick={() => select(row, 0)}><b>{sources[row].employeeCode || `Employee ${row + 1}`}</b><small>{sources[row].employeeName}</small></button></th>}
            {cells.map((item, index) => {
              const editable = canEdit(item.inputKey), selected = row === rowIndex && index === columnIndex
              const raw = editable ? inputValue(item, sources[row]) : ''
              const showPayable = item.value == null || !raw.trim() || !Number.isFinite(Number(raw)) || Number(raw) !== item.value
              return <td key={item.address} className={`${selected ? 'is-selected' : ''} ${editable ? 'is-input' : ''}`}>
                {editable ? <div className="salary-worksheet-component-input"><small>{inputUnit(item)}</small><input ref={element => { if (element) refs.current.set(`${row}:${index}`, element); else refs.current.delete(`${row}:${index}`) }} aria-label={`${sources[row].employeeCode || 'Scenario'} ${item.label} ${inputUnit(item)}`} inputMode="decimal" value={raw} placeholder="Enter amount" onFocus={() => select(row, index)} onChange={event => sources[row].onEdit(item.inputKey!, event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); select(row + 1, index, true) } }} />{showPayable && <small className="salary-worksheet-payable">{sources[row].stale && item.value != null ? 'Previous payable' : 'Payable'}: {display(item.value) || (item.pending ? 'Pending' : 'Not supplied')}</small>}</div> :
                  <button type="button" ref={element => { if (element) refs.current.set(`${row}:${index}`, element); else refs.current.delete(`${row}:${index}`) }} tabIndex={selected ? 0 : -1} aria-label={`${item.label}: ${item.value ?? (item.pending ? 'Pending' : 'Not supplied')}`} aria-pressed={selected} onClick={() => select(row, index)} onKeyDown={event => { if (event.key === 'ArrowRight') { event.preventDefault(); select(row, index + 1, true) } if (event.key === 'ArrowLeft') { event.preventDefault(); select(row, index - 1, true) } if (event.key === 'ArrowUp') { event.preventDefault(); select(row - 1, index, true) } if (event.key === 'ArrowDown') { event.preventDefault(); select(row + 1, index, true) } }}>
                    <small>Calculated payable</small>{display(item.value) || <span className="salary-worksheet-missing">{item.pending ? 'Pending' : 'Not supplied'}</span>}
                  </button>}
              </td>
            })}
          </tr>)}</tbody></table>
        </div>
        {cell && <details className="salary-worksheet-details"><summary>{cell.label} · Formula and source</summary>
          <div className="salary-worksheet-formula-bar"><span className="salary-worksheet-address">{cell.address}</span><strong aria-hidden="true">fx</strong>
            {canEdit(cell.inputKey) ? <Input aria-label={`Worksheet ${cell.label} input`} value={inputValue(cell, source)} placeholder={inputUnit(cell)} onChange={event => source.onEdit(cell.inputKey!, event.target.value)} /> : <Input aria-label={ruleEditing ? 'Temporary payroll rule' : 'Selected cell formula'} readOnly={!ruleEditing} value={ruleEditing ? formulaDraft : cell.formula || cell.configuredFormula || String(cell.value ?? '')} onChange={event => setFormulaDraft(event.target.value)} onPressEnter={() => { if (editableRule && ruleEditing && cell.ruleComponentId) { onRuleEdit?.(cell.ruleComponentId, formulaDraft); setRuleEditing(false) } }} />}
            {editableRule && <Button size="small" onClick={() => { if (ruleEditing) { onRuleEdit?.(cell.ruleComponentId!, formulaDraft); setRuleEditing(false) } else setRuleEditing(true) }}>{ruleEditing ? 'Apply temporary rule' : 'Edit payroll rule'}</Button>}
          </div>
          {cell.source && <p className="salary-worksheet-cell-detail">{cell.source}</p>}
        </details>}
      </div>
      <aside className={`salary-worksheet-totals${hasPreviousResult ? ' is-stale' : ''}`} aria-label="Selected simulation totals"><strong>Totals</strong><small>{hasPreviousResult ? 'Previous amounts · recalculation or review needed' : currentResult.complete ? 'Native payroll result' : 'Calculation pending'}</small>
        {([['Gross earnings', currentResult.gross], ['Employee deductions', currentResult.deductions], ['Employer contributions', currentResult.benefits], ['Monthly net pay', currentResult.net]] as const).map(([label, amount]) => <div key={label} className={label === 'Monthly net pay' ? 'is-net' : ''}><span>{label}</span><b>{display(amount) || 'Pending'}</b></div>)}
      </aside>
    </div>
    <div className="salary-worksheet-footer"><span>{rows ? `${rows.length} employees` : 'Temporary scenario'}</span><span>{presentOnly ? 'Edit Present / Payable days above · salary inputs remain read-only' : 'Edit highlighted monthly amounts / daily rates · formulas recalculate automatically'}</span></div>
    {notice && <p className="salary-worksheet-notice">{notice}</p>}
  </section>
}
