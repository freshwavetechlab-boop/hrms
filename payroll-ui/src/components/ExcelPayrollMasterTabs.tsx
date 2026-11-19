import { useRef, useState, type ReactNode, type SyntheticEvent } from 'react'
import { Alert, Button, Form, Input, InputNumber, Select, Space, Switch, Tabs, Tag } from 'antd'
import { ApartmentOutlined, CalendarOutlined, CheckCircleOutlined, TeamOutlined, WalletOutlined } from '@ant-design/icons'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupPreview, PayrollSetupRequest, PayrollSetupResult } from '../services/excelPayrollSetupService'
import { excelColumnLetter } from '../utils/excelPayslipImport'
import { sourceMonthEnd } from '../utils/excelPayrollMasterWorkspace'
import { diagnoseSetup, type SetupDiagnosis } from '../utils/excelPayrollSetupDiagnostics'
import { explainedRoundingReviewRows } from '../utils/excelPayrollRoundingReview'
import ExcelPayrollSetupIssues from './ExcelPayrollSetupIssues'
import { masterReadySubset } from '../utils/excelPayrollReadySubset'
import { payrollComparisonMoney, payrollDisplayEvidence } from '../utils/payrollRoundingEvidence'
import DataTable from './DataTable'

export type MasterSetupSection = { key: string; label: ReactNode; children: ReactNode }
type Props = { batch: ExcelPayslipBatch; context: PayrollSetupContext; request: PayrollSetupRequest; preview: PayrollSetupPreview | null; applied: PayrollSetupResult | null;
  disabled: boolean; activeTab: string; onTabChange: (key: string) => void; onChange: (change: Partial<PayrollSetupRequest>) => void; items: MasterSetupSection[]; onRowsChange?: (rowIds: string[]) => void; sourceRowIds?: string[]; onShowCalculationDetails?: () => void; onReviewRounding?: () => void }

export default function ExcelPayrollMasterTabs({ batch, context, request, preview, applied, disabled, activeTab, onTabChange, onChange, items, onRowsChange, sourceRowIds, onShowCalculationDetails, onReviewRounding }: Props) {
  const [inspecting, setInspecting] = useState(false)
  const [sourceDetails, setSourceDetails] = useState(false)
  const content = useRef<HTMLDivElement>(null)
  const detail = (keys: string[], label = 'Advanced details') => <details className="excel-master-details" {...(keys.includes('source-evidence')?{open:sourceDetails,onToggle:(event:SyntheticEvent<HTMLDetailsElement>)=>setSourceDetails(event.currentTarget.open)}:{})}><summary>{label}</summary>{keys.map(key => {
    const section = items.find(item => item.key === key)
    return section ? <section key={key} className="excel-master-detail-section"><h3>{section.label}</h3>{section.children}</section> : null
  })}</details>
  const rows = batch.rows.filter(row => request.rowIds.includes(row.id))
  const newEmployees = request.employeeDecisions.filter(decision => decision.allowCreate && !decision.matchedEmployeeId).length
  const matched = request.employeeDecisions.filter(decision => !!decision.matchedEmployeeId).length
  const unresolved = rows.length - newEmployees - matched
  const newComponents = request.components.filter(component => !component.existingComponentId).length
  const needsFormula = (component: PayrollSetupRequest['components'][number]) => [component.formula, ...Object.values(component.rowFormulas || {})].some(formula => formula.includes('REVIEW_REQUIRED'))
  const formulaReviews = request.components.filter(needsFormula).length
  const formulaLabel = (component: PayrollSetupRequest['components'][number]) => component.sourceColumnIndex != null ? `${excelColumnLetter(component.sourceColumnIndex)} · ${context.source.columns.find(column => column.columnIndex === component.sourceColumnIndex)?.sourceHeader || 'Source input'}` : component.formula || component.value || 'Needs mapping'
  const columnOptions = context.source.columns.map(column => ({ value: column.columnIndex, label: `${excelColumnLetter(column.columnIndex)} · ${column.sourceHeader || column.label}` }))
  const locations = request.templateScope?.locationMode === 'Listed' ? [...request.templateScope.workLocationIds.map(id => context.workLocations.find(location => location.id === id)?.name || String(id)), ...request.templateScope.newLocationNames] : []
  const summary = <div className="excel-master-summary" aria-label="Master setup summary">
    <button type="button" onClick={() => onTabChange('salary')}><WalletOutlined /><span>Salary setup<strong>{request.components.length} components</strong><small>{newComponents} new · {request.components.length - newComponents} reuse Master{formulaReviews ? ` · ${formulaReviews} need mapping` : ''}</small></span></button>
    <button type="button" onClick={() => onTabChange('policies')}><CalendarOutlined /><span>Policies<strong>{request.workWeek || 'Choose work week'}</strong><small>Existing leave rules retained</small></span></button>
    <button type="button" onClick={() => onTabChange('location')}><ApartmentOutlined /><span>Location & month<strong>{batch.month}</strong><small>{locations.join(', ') || 'Location needs review'}</small></span></button>
    <button type="button" onClick={() => onTabChange('employees')}><TeamOutlined /><span>Employees<strong>{rows.length} selected rows</strong><small>{matched} matched · {newEmployees} new{unresolved ? ` · ${unresolved} need attention` : ''}</small></span></button>
  </div>
  const diagnoses = diagnoseSetup(batch,context,request,preview).map(item=>preview?.canApply?{...item,blocking:false}:item)
  const roundingCount=preview?.transfer?explainedRoundingReviewRows(request,preview.transfer).filter(row=>request.roundingReviews?.[row.rowId]!==row.proofHash).length:0
  const dayValues=[...new Set(rows.map(row=>context.source.rows.find(source=>source.sourceRow===row.sourceRow)?.cells[request.payrollDaysColumnIndex]?.value).map(value=>Number(value)).filter(value=>Number.isFinite(value)&&value>=1&&value<=31))]
  const dayBasis=request.fixedPayrollDays?`${request.fixedPayrollDays} fixed salary days` : request.payrollDaysColumnIndex>=0 ? `${excelColumnLetter(request.payrollDaysColumnIndex)} · ${context.source.columns.find(column=>column.columnIndex===request.payrollDaysColumnIndex)?.sourceHeader || 'Salary divisor'}${dayValues.length?` = ${dayValues.join(', ')}`:''}. Calendar days and salary days can differ.` : undefined
  const dayMeaning=request.templateScope?.divisorMeaning ? ({CalendarDays:'Calendar days',ContractFixedDays:'Source salary divisor',WorkingDays:'Working-day divisor'}[request.templateScope.divisorMeaning]) : 'Choose day meaning'
  const resolve=(item:SetupDiagnosis)=>{onTabChange(item.tab);if(['translation','master'].includes(item.category))setInspecting(true);if(item.category==='source')setSourceDetails(true);if(['rounding','salary','api','unknown'].includes(item.category))onShowCalculationDetails?.();content.current?.scrollIntoView({block:'start',behavior:'auto'})}
  const readySubset = masterReadySubset(context, request.rowIds)
  const originalRows = sourceRowIds || request.rowIds
  const changedSelection = originalRows.length !== request.rowIds.length || originalRows.some(id => !request.rowIds.includes(id))
  return <div className="excel-master-workspace">
    <div className="excel-master-context"><Space wrap><Tag color="purple">{batch.clientName}</Tag><Tag>{batch.month}</Tag><span>{rows.length} selected Excel rows</span></Space><Tag color={applied ? 'green' : 'blue'}>{applied ? 'Saved in Master' : 'Proposed setup'}</Tag></div>
    {!applied && onRowsChange && changedSelection && <Alert type="info" showIcon message={`${request.rowIds.length} of ${originalRows.length} selected source rows are open`} description="Other rows remain in the original saved Excel batch. Changing this review selection does not remove source rows or change their salary or mail history." action={<Button disabled={disabled} onClick={() => onRowsChange(originalRows)}>Return to all {originalRows.length} selected rows</Button>} />}
    {summary}
    {!applied&&<ExcelPayrollSetupIssues diagnoses={diagnoses} disabled={disabled} onResolve={resolve} dayBasis={dayBasis} readyCount={readySubset.readyRowIds.length} onContinueReady={onRowsChange&&readySubset.canContinue?()=>onRowsChange(readySubset.readyRowIds):undefined} roundingCount={roundingCount} onReviewRounding={onReviewRounding} sourceLabel={[batch.sourceFileName,batch.sheetName].filter(Boolean).join(' · ')}/>}
    <div ref={content}><Tabs activeKey={activeTab} onChange={onTabChange} className="excel-master-tabs" items={[
      { key: 'salary', label: 'Salary setup', children: <section>
        <div className="excel-master-section-heading"><div><h3>Salary template & components</h3><p>Your Excel formulas are carried into payroll. Existing Masters are reused; missing components are listed as New.</p></div><Tag>{request.components.length} components</Tag></div>
        <Form.Item label="Salary template"><Input aria-label="Payroll setup Template name" value={request.templateName} onChange={event => onChange({ templateName: event.target.value })} /></Form.Item>
        {request.recurringBlankInputReview?.version === 2 && <Alert type="info" showIcon message={`${request.recurringBlankInputReview.rowIds?.length || 0} blank formula inputs: ${request.recurringBlankInputReview.columnIndices.map(index=>context.source.columns.find(column=>column.columnIndex===index)?.sourceHeader || excelColumnLetter(index)).join(', ')}`} description={`Use Excel's zero arithmetic only for column ${request.recurringBlankInputReview.columnIndices.map(excelColumnLetter).join(', ')}, rows ${batch.rows.filter(row=>request.recurringBlankInputReview?.rowIds?.includes(row.id)).map(row=>row.sourceRow).join(', ')}. Check & preview confirms this scope; original cells and formulas stay unchanged.`} />}
        <DataTable title="Salary components" rows={request.components} getRowId={component => component.key} exportDisabled hideExport hideSearch columns={[
          { key: 'name', label: 'Component', width: 190 }, { key: 'category', label: 'Type', width: 110 },
          { key: 'source', label: 'Excel input / formula', width: 300, wrap: true, value: component => needsFormula(component) ? 'Needs formula mapping' : formulaLabel(component), render: component => needsFormula(component) ? <Tag color="red">Needs formula mapping</Tag> : formulaLabel(component) },
          { key: 'master', label: 'Master', width: 120, value:component=>component.existingComponentId?'Reuse existing':'New component', render: component => <Tag color={component.existingComponentId ? 'green' : 'gold'}>{component.existingComponentId ? 'Reuse existing' : 'New component'}</Tag> },
        ]} />
        <div className="excel-master-detail-toggle"><Button onClick={() => setInspecting(value => !value)}>{inspecting ? 'Hide component editor' : 'Edit components / formulas'}</Button></div>
        {inspecting && items.find(item => item.key === 'components')?.children}
        {detail(['columns', 'source-evidence', 'identity-columns'], 'Excel mapping & source details')}
      </section> },
      { key: 'policies', label: 'Policies', children: <section>
        <div className="excel-master-section-heading"><div><h3>Attendance & leave policies</h3><p>Use the saved work week and retain existing policy memberships.</p></div></div>
        <div className="excel-payroll-setup-grid"><Form.Item label="Work week"><Select aria-label="Payroll setup work week" value={request.workWeek || undefined} options={context.workWeeks.length ? context.workWeeks : [{value:'Sunday off',label:'Sunday off'}]} onChange={workWeek => onChange({ workWeek })} /></Form.Item><Form.Item label="Source-month attendance"><Space><Switch aria-label="Import source-month attendance" checked={request.importAttendance === true} disabled={disabled} onChange={importAttendance => onChange({ importAttendance, importPayableOnlyAttendance: false, overrideSourcePayableAttendance: false })} /><span>{request.importAttendance ? 'Import reviewed monthly totals' : 'Keep existing attendance'}</span></Space></Form.Item></div>
        <div className="excel-master-policy-note"><CheckCircleOutlined /><div><strong>Existing leave, holiday and shift rules stay in place</strong><p>Monthly Excel totals do not define daily presence or leave entitlements. Any new leave / holiday proposals remain inactive drafts.</p></div></div>
        {request.presentDaysColumnIndex == null && <Alert type="info" showIcon message="Actual present days are not supplied in this mapping" description="Excel payable days remain salary inputs. They are not converted into daily Present attendance." />}
        {detail(['defaults', 'master-defaults'], 'Leave / holiday proposals & policy options')}
      </section> },
      { key: 'location', label: 'Location & month', children: <section>
        <div className="excel-master-section-heading"><div><h3>Salary month & work location</h3><p>The source month and known client locations are filled for you.</p></div><Tag>{batch.month}</Tag></div>
        <div className="excel-payroll-setup-grid"><Form.Item label="Effective from"><Input aria-label="Payroll setup Effective from" type="date" value={request.effectiveFrom} onChange={event => onChange({ effectiveFrom: event.target.value })} /></Form.Item><Form.Item label="Applicable through"><Input aria-label="Template applicability through" type="date" value={request.templateScope?.effectiveTo || sourceMonthEnd(batch.month)} onChange={event => request.templateScope && onChange({ templateScope: { ...request.templateScope, effectiveTo: event.target.value, openEnded: false, reviewed: false } })} /></Form.Item>
        <Form.Item label="Work location"><Select aria-label="Default work location" allowClear value={request.defaultWorkLocationId} placeholder={locations.join(', ') || 'Choose location'} options={context.workLocations.map(location => ({value:location.id,label:location.name}))} onChange={defaultWorkLocationId => onChange({ defaultWorkLocationId, ...(request.templateScope && defaultWorkLocationId ? {templateScope:{...request.templateScope,locationMode:'Listed',workLocationIds:[defaultWorkLocationId],newLocationNames:[],reviewed:false}} : {}) })} /></Form.Item><Form.Item label="Location from Excel"><Select aria-label="Work location column" allowClear value={request.sourceWorkLocationColumnIndex} options={columnOptions} placeholder="Use selected work location" onChange={sourceWorkLocationColumnIndex => onChange({ sourceWorkLocationColumnIndex })} /></Form.Item>
        <Form.Item label="Salary divisor"><InputNumber aria-label="Reviewed fixed payroll days" min={1} max={31} value={request.fixedPayrollDays} placeholder={request.payrollDaysColumnIndex >= 0 ? `Excel ${excelColumnLetter(request.payrollDaysColumnIndex)}` : 'Choose days'} onChange={fixedPayrollDays => onChange({ fixedPayrollDays: fixedPayrollDays ?? undefined })} /></Form.Item><Form.Item label="Payable days from Excel"><Select aria-label="Payable days column" value={request.payableDaysColumnIndex < 0 ? undefined : request.payableDaysColumnIndex} options={columnOptions} onChange={payableDaysColumnIndex => onChange({ payableDaysColumnIndex })} /></Form.Item>
        <Form.Item label="Salary divisor meaning" required={!request.templateScope?.divisorMeaning}><Select aria-label="Template divisor meaning" value={request.templateScope?.divisorMeaning || undefined} placeholder="Confirm the source day basis" options={[{value:'CalendarDays',label:'Calendar days in this month'},{value:'ContractFixedDays',label:'Fixed salary divisor from Excel / contract'},{value:'WorkingDays',label:'Working days'}]} onChange={divisorMeaning=>request.templateScope&&onChange({templateScope:{...request.templateScope,divisorMeaning,reviewed:false}})}/></Form.Item></div>
        {detail(['basis', 'applicability', 'location-allocations'], 'Day meanings, effective scope & location rules')}
      </section> },
      { key: 'employees', label: 'Employees', children: <section>
        <div className="excel-master-section-heading"><div><h3>Employee matches</h3><p>Matched employees keep their existing ID. New source employees are included in the setup below.</p></div><Space><Tag color="green">{matched} matched</Tag><Tag color="gold">{newEmployees} new</Tag>{unresolved > 0 && <Tag color="red">{unresolved} need attention</Tag>}</Space></div>
        {!context.employees.length && <Alert type="info" showIcon message="No Employee Master records exist for this client / month yet" description="New employees shown below will be added when you save this setup. An empty employee dropdown is not required." />}
        <DataTable title="Employees included in this setup" rows={rows} getRowId={row => row.id} exportDisabled hideExport columns={[
          {key:'employeeCode',label:'Code',width:150},{key:'employeeName',label:'Employee',width:220},
          {key:'status',label:'Master action',width:145,value:row=>{const decision=request.employeeDecisions.find(item=>item.rowId===row.id);return decision?.matchedEmployeeId?'Matched':decision?.allowCreate?'Create new employee':'Needs matching'},render:row=>{const decision=request.employeeDecisions.find(item=>item.rowId===row.id);return <Tag color={decision?.matchedEmployeeId?'green':decision?.allowCreate?'gold':'red'}>{decision?.matchedEmployeeId?'Matched':decision?.allowCreate?'Create new employee':'Needs matching'}</Tag>}},
          {key:'reviewReason',label:'Review reason',width:260,wrap:true,value:row=>{const roster=context.roster?.rows.find(item=>item.rowId===row.id);return roster?.status==='Review'?roster.message:''}},
          {key:'matching',label:'Existing Employee Master',width:300,render:row=>{const decision=request.employeeDecisions.find(item=>item.rowId===row.id);const employee=context.employees.find(item=>item.id===decision?.matchedEmployeeId);if(employee)return `${employee.employeeCode} · ${employee.employeeName}`;if(decision?.allowCreate)return 'New record from this Excel';return context.employees.length?<Select aria-label={`Employee match for row ${row.sourceRow}`} showSearch optionFilterProp="label" placeholder="Resolve employee match" value={decision?.matchedEmployeeId} options={context.employees.map(employee=>({value:employee.id,label:`${employee.employeeCode} · ${employee.employeeName}`}))} onChange={matchedEmployeeId=>onChange({employeeDecisions:request.employeeDecisions.map(item=>item.rowId===row.id?{...item,matchedEmployeeId,allowCreate:false}:item)})}/>: 'Identity needs review; no employee is created automatically'},},
        ]}/>
        {detail(['employees'], 'Change a match or review employee defaults')}
      </section> },
      { key: 'review', label: 'Review & save', children: <section>
        <div className="excel-master-section-heading"><div><h3>{applied ? 'Master setup saved' : 'Check once, save together'}</h3><p>Check & preview confirms the displayed employee actions, component inputs, day meanings, policies and source-month scope together.</p></div></div>
        <div className="excel-master-review-list"><p><CheckCircleOutlined /> {matched} existing employees reused · {newEmployees} new employees</p><p><CheckCircleOutlined /> {request.components.length} salary components · {newComponents} missing Masters to create</p><p><CheckCircleOutlined /> {request.templateName} · {request.effectiveFrom} through {request.templateScope?.openEnded ? 'open-ended (saved choice)' : request.templateScope?.effectiveTo || 'choose end date'}</p><p><CheckCircleOutlined /> Locations: {locations.join(', ') || (request.templateScope?.locationMode === 'AllClientLocations' ? 'All client locations (saved choice)' : 'Choose location')}</p><p><CheckCircleOutlined /> Salary day basis: {dayMeaning}{dayValues.length?` (${dayValues.join(', ')})`:''} · source payable units already include paid credits</p><p><CheckCircleOutlined /> {request.importAttendance ? 'Import the reviewed source monthly totals' : 'Retain existing attendance'} · no leave or weekly-off credits added</p></div>
        {request.recurringBlankInputReview?.version === 2 && <p>{request.recurringBlankInputReview.rowIds?.length || 0} selected blank inputs are reviewed as zero in the saved source arithmetic. Other blanks and salary outputs are unchanged.</p>}
        {preview?.canApply ? <Alert type="success" showIcon message="Calculations checked — ready to save" description={`${preview.rows.length} source rows checked against the saved Excel. Save master setup applies this reviewed plan.`}/> : preview && <p>Resolve the items in Needs attention above, then check again. Exact API messages remain available in calculation details.</p>}
        {preview && <DataTable title="Salary comparison" rows={preview.rows} getRowId={row=>row.rowId} exportDisabled hideExport columns={[
          {key:'employeeName',label:'Employee',width:200},{key:'sourceNet',label:'Excel net pay',width:140,render:row=>payrollComparisonMoney(batch.rows.find(source=>source.id===row.rowId)?.netPay)},
          {key:'net',label:'Calculated net pay',width:150,render:row=>payrollComparisonMoney(row.net)},
          {key:'difference',label:'Difference (2dp)',width:140,render:row=>{const evidence=payrollDisplayEvidence(batch.rows.find(source=>source.id===row.rowId)?.netPay,row.net);return payrollComparisonMoney(evidence.displayStatus.startsWith('Same at 2dp')?0:evidence.rawDifference)}},
          {key:'comparison',label:'Comparison',width:190,value:row=>{const status=payrollDisplayEvidence(batch.rows.find(source=>source.id===row.rowId)?.netPay,row.net).displayStatus;return status.startsWith('Same at 2dp')?'Matches Excel display':status}},
        ]}/>}
        <p className="excel-master-save-note">Saving configures Master records and salary assignments. Payroll runs and approvals remain in Regular Run. Existing payslip amounts and sent-mail history are retained.</p>
        {detail(['transfer'], 'Resolve template / attendance exceptions')}
      </section> },
    ]}/></div>
  </div>
}
