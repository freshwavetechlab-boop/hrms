import { Alert, Checkbox, Form, Select, Space, Tag } from 'antd'
import type { ExcelPayslipBatch, ExcelPayslipRow } from '../services/excelPayslipService'
import type { PayrollLocationComponentScope, PayrollSetupContext, PayrollSetupPreview, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { newLocationAllocationReview, patchLocationAllocationGroup, patchLocationAllocationPart, selectedLocationAllocationGroups } from '../utils/excelPayrollLocationAllocations'
import { excelColumnLetter, formatExcelPayslipAmount, parsePayslipAmount } from '../utils/excelPayslipImport'
import DataTable from './DataTable'
import './ExcelPayrollLocationAllocations.css'

type Props = { batch: ExcelPayslipBatch; context: PayrollSetupContext; request: PayrollSetupRequest; preview?: PayrollSetupPreview | null; disabled: boolean; onChange: (change: Partial<PayrollSetupRequest>) => void }
const information = (row: ExcelPayslipRow, pattern: RegExp) => row.information.filter(item => pattern.test(item.label)).map(item => item.value.trim()).filter(Boolean).join(' / ') || 'Not supplied'
const privateHeader = /\b(?:pan|uan|aadh?aar|bank|account|ifsc|mobile|phone|identifier)\b/i

export default function ExcelPayrollLocationAllocations({ batch, context, request, preview, disabled, onChange }: Props) {
  const selection = selectedLocationAllocationGroups(batch, request.rowIds), review = request.locationAllocations
  const initial = newLocationAllocationReview(batch, context, request.rowIds)
  const supported = context.locationAllocationSupported === true
  const templates = (context.salaryTemplates || []).filter(template => template.active && String(template.clientId) === String(batch.clientId))
  const sourceCell = (row: ExcelPayslipRow, columnIndex: number | undefined, daysOnly = false) => {
    if (columnIndex == null || columnIndex < 0) return 'Not mapped'
    const column = context.source.columns.find(item => item.columnIndex === columnIndex)
    if (!column || privateHeader.test(column.sourceHeader)) return 'Review required'
    const cells = context.source.rows.filter(item => item.sourceRow === row.sourceRow)
    const cell = cells.length === 1 ? cells[0].cells[columnIndex] : undefined
    const value = cell && !cell.error && !cell.missingCachedValue && !cell.calculationError ? parsePayslipAmount(cell.value) : null
    if (value == null || !Number.isFinite(value) || (daysOnly && (value < 0 || value > 31))) return 'Not supplied / review required'
    return `${daysOnly ? value : formatExcelPayslipAmount(value, 2)} (${excelColumnLetter(columnIndex)}${row.sourceRow})`
  }
  const update = (value: typeof review) => onChange({ locationAllocations: value })
  return <section className="excel-payroll-location-allocations" aria-label="Monthly location allocation review">
    <Alert type="info" showIcon message="One employee, reviewed monthly location parts" description="Choose existing Employee Master, Location Master and Settings salary templates. Original Excel source parts and issued slips stay unchanged. Location attendance is allocated within the saved payroll month; no historical transfer dates are inferred." />
    <Space wrap><Tag>Payroll period: {batch.month}</Tag><Tag>Configuration import / effective date: {context.locationAllocationImportDate || 'Unavailable'}</Tag></Space>
    {!supported && <Alert type="warning" showIcon message="Monthly location allocation runtime is unavailable" description="Start the reviewed API build before using this optional transfer path. The existing setup path is unchanged." />}
    {!!selection.incomplete.length && <Alert type="warning" showIcon message="Select every original part for this employee" description={`The selected cohort omits source rows ${selection.incomplete.flatMap(group => group.rows.filter(row => !request.rowIds.includes(row.id)).map(row => row.sourceRow)).join(', ')}. Reopen setup with every part; no partial employee allocation is prepared.`} />}
    {!!selection.unresolved.length && <Alert type="warning" showIcon message="Source employee grouping needs review" description="Missing or conflicting source identity cannot become a monthly location allocation. Original source rows remain separate." />}
    <Checkbox aria-label="Use monthly location allocations" disabled={disabled || (!review && !initial)} checked={!!review} onChange={event => update(event.target.checked ? initial || undefined : undefined)}>Use reviewed Settings templates and monthly location allocations</Checkbox>
    {review && <>
      {(review.payrollPeriod !== batch.month || review.configurationEffectiveFrom !== context.locationAllocationImportDate) && <Alert type="warning" showIcon message="Saved allocation period or import date changed" description="Turn this optional review off and on to prepare the current server import date, then review again. Saved payroll history remains unchanged." />}
      <p>Review the mapped source day meanings in Source-month attendance / LWP mapping after preview. Payable units do not establish actual presence. Existing recurring salary inputs and captured formulas remain in the component mapping below.</p>
      {review.groups.map((group, groupIndex) => {
        const sourceGroup = selection.groups.find(item => item.rows.some(row => group.parts.some(part => part.rowId === row.id)))
        const observed = preview?.locationAllocations?.groups.find(item => item.employeeId === group.employeeId)
        const references = [...new Map((observed?.parts || []).map(part => [part.assignmentId, part])).values()]
        const groupName = sourceGroup?.employeeCode || `group ${groupIndex + 1}`
        return <section key={group.parts.map(part => part.rowId).join('|')} aria-label={`Location allocation for ${groupName}`}>
          <h4>{sourceGroup?.employeeCode} · {sourceGroup?.employeeName} · {group.parts.length} original source parts</h4>
          <div className="excel-payroll-setup-grid">
            <Form.Item label="One existing Employee Master"><Select aria-label={`Master employee for ${groupName}`} disabled={disabled} showSearch optionFilterProp="label" allowClear placeholder="Select one verified existing employee" value={group.employeeId || undefined} options={context.employees.map(employee => ({ value: employee.id, label: `${employee.employeeCode} · ${employee.employeeName}` }))} onChange={employeeId => onChange({ locationAllocations: patchLocationAllocationGroup(review, groupIndex, { employeeId: employeeId || 0 }), employeeDecisions: request.employeeDecisions.map(decision => group.parts.some(part => part.rowId === decision.rowId) ? { ...decision, matchedEmployeeId: employeeId || undefined, allowCreate: false } : decision) })} /></Form.Item>
            <Form.Item label="Monthly reference assignment (only when required)" extra="Preview first. Choose explicitly when source monthly inputs differ; the server validates whether a reference is needed."><Select aria-label={`Monthly reference assignment for ${groupName}`} disabled={disabled || !references.length} allowClear placeholder="Preview to inspect assignment choices" value={group.referenceAssignmentId} options={references.map(part => ({ value: part.assignmentId, label: `${part.workLocationName} · source row ${part.sourceRow} · ${part.assignmentId}` }))} onChange={referenceAssignmentId => update(patchLocationAllocationGroup(review, groupIndex, { referenceAssignmentId }))} /></Form.Item>
          </div>
          {group.parts.map(part => {
            const row = batch.rows.find(item => item.id === part.rowId), template = templates.find(item => item.id === part.templateId)
            if (!row) return <Alert key={part.rowId} type="error" message="Original source part is unavailable; refresh this saved scope." />
            const partPreview = observed?.parts.find(item => item.rowId === part.rowId && item.workLocationId === part.workLocationId && item.templateId === part.templateId)
            const patchPart = (change: Partial<typeof part>) => update(patchLocationAllocationPart(review, groupIndex, part.rowId, change))
            return <details key={part.rowId} open aria-label={`Monthly location source row ${row.sourceRow}`}>
              <summary>Source row {row.sourceRow} · {information(row, /^payroll\s*unit$/i)} · recorded location {information(row, /^(?:work\s*location|location)(?:\s*name)?$/i)}</summary>
              <div className="excel-payroll-setup-grid">
                <Form.Item label="Client Location Master"><Select aria-label={`Location Master for source row ${row.sourceRow}`} disabled={disabled} showSearch optionFilterProp="label" allowClear placeholder="Choose existing client location" value={part.workLocationId || undefined} options={context.workLocations.map(location => ({ value: location.id, label: location.name }))} onChange={workLocationId => patchPart({ workLocationId: workLocationId || 0 })} /></Form.Item>
                <Form.Item label="Existing Settings salary template"><Select aria-label={`Settings template for source row ${row.sourceRow}`} disabled={disabled} showSearch optionFilterProp="label" allowClear placeholder="Choose an exact reviewed Settings template" value={part.templateId || undefined} options={templates.map(item => ({ value: item.id, label: item.name }))} onChange={templateId => patchPart({ templateId: templateId || '', componentScopes: {} })} /></Form.Item>
              </div>
              <Space wrap><Tag>Recorded source state: {information(row, /^(?:state|state\s*name)$/i)}</Tag><Tag>Source divisor: {request.fixedPayrollDays != null ? `${request.fixedPayrollDays}${request.payrollDaysCell ? ` (${request.payrollDaysCell})` : ' (reviewed fixed)'}` : sourceCell(row, request.payrollDaysColumnIndex, true)}</Tag><Tag>Mapped source day units: {sourceCell(row, request.payableDaysColumnIndex, true)}</Tag><Tag>Actual present input: {sourceCell(row, request.presentDaysColumnIndex, true)}</Tag><Tag>Payable meaning: {request.attendanceReview?.payableMeaning || 'Not reviewed'}</Tag><Tag>Present meaning: {request.attendanceReview?.presentMeaning || 'Not reviewed'}</Tag></Space>
              {template && <DataTable title="Review each Settings component scope and source input" rows={template.lines} getRowId={line => line.componentId} scrollY={Math.min(280, Math.max(56, template.lines.length * 56))} hideSearch exportDisabled columns={[
                { key: 'componentId', label: 'Settings component', width: 180, value: line => { const master = context.components.find(item => item.id === line.componentId); return master ? `${master.code} · ${master.name}` : `Component ${line.componentId}` } },
                { key: 'formula', label: 'Settings rule', width: 240, wrap: true, value: line => line.formula || `${line.calculationType}: ${line.value}` },
                { key: 'sourceInput', label: 'Captured monthly / rate input', width: 190, value: line => sourceCell(row, request.components.find(component => component.existingComponentId === line.componentId)?.sourceColumnIndex) },
                { key: 'scope', label: 'Payroll calculation scope', width: 310, sortable: false, filterable: false, render: line => <Select aria-label={`Component scope ${line.componentId} source row ${row.sourceRow}`} disabled={disabled} placeholder="Choose explicitly" value={part.componentScopes[line.componentId]} options={[{ value: 'SourceFormulaPerPart', label: 'Source formula for each location part' }, { value: 'Monthly', label: 'Monthly employee scope' }]} onChange={(scope: PayrollLocationComponentScope) => patchPart({ componentScopes: { ...part.componentScopes, [line.componentId]: scope } })} /> },
              ]} />}
              {partPreview && <p>Preview allocation: divisor {partPreview.payrollDays}, present {partPreview.presentDays}, payable {partPreview.payableDays}; assignment {partPreview.assignmentId}. Reviewed salary inputs: {Object.entries(partPreview.salaryInputs).map(([code, value]) => `${code} ${formatExcelPayslipAmount(value, 2)}`).join('; ') || 'None returned'}</p>}
            </details>
          })}
        </section>
      })}
      <Space direction="vertical"><Checkbox aria-label="Review location Settings applicability" disabled={disabled} checked={review.reviewedSettingsApplicability} onChange={event => update({ ...review, reviewedSettingsApplicability: event.target.checked, reviewed: false })}>I reviewed these existing Settings templates, client locations and component scopes for the selected employee/month.</Checkbox><Checkbox aria-label="Review monthly location allocations" disabled={disabled} checked={review.reviewed} onChange={event => update({ ...review, reviewed: event.target.checked })}>I reviewed every original part, its source input/day meaning and the one Employee Master mapping. No source slip, personal or bank data is changed.</Checkbox></Space>
    </>}
  </section>
}
