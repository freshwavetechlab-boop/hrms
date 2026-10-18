import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Form, Input, InputNumber, Select, Space, Spin, Tag } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { applyPayrollSetup, getPayrollSetupContext, previewPayrollSetup, savePayrollSetupDraft, savePayrollDefaultsDraft, verifyPayrollSetupEmployees, type PayrollSetupComponent, type PayrollSetupContext, type PayrollSetupPreview, type PayrollSetupRequest, type PayrollSetupResult, type PayrollSetupVerification } from '../services/excelPayrollSetupService'
import { proposeExcelPayrollSetup } from '../utils/excelPayrollSetup'
import { excelColumnLetter, payslipColumnKinds } from '../utils/excelPayslipImport'
import { employeeStatutoryTypes, employerStatutoryTypes } from '../utils/salaryComponentConfig'
import DataTable from './DataTable'
import './ExcelPayrollSetup.css'

type Props = { clientId: number; batch: ExcelPayslipBatch; rowIds: string[]; onClose: () => void; onApplied: (result: PayrollSetupResult) => void }
const money = (value?: number | null) => value == null ? '—' : value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const object = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
const Sections = ({ items, defaultActiveKey }: { items: { key: string; label: ReactNode; children: ReactNode }[]; defaultActiveKey?: string[] }) => <Collapse defaultActiveKey={defaultActiveKey}>{items.map(item => <Collapse.Panel key={item.key} header={item.label}>{item.children}</Collapse.Panel>)}</Collapse>

export default function ExcelPayrollSetup({ clientId, batch, rowIds, onClose, onApplied }: Props) {
  const [context, setContext] = useState<PayrollSetupContext | null>(null)
  const [request, setRequest] = useState<PayrollSetupRequest | null>(null)
  const [preview, setPreview] = useState<PayrollSetupPreview | null>(null)
  const [notes, setNotes] = useState<string[]>([])
  const [busy, setBusy] = useState('Loading setup')
  const [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [reviewed, setReviewed] = useState(false)
  const [applied, setApplied] = useState<PayrollSetupResult | null>(null)
  const [verification, setVerification] = useState<PayrollSetupVerification[]>([])
  const [defaultsDraft, setDefaultsDraft] = useState<Record<string, unknown>>({})
  const [defaultsRevision, setDefaultsRevision] = useState(0)
  const requestId = useRef<string>(crypto.randomUUID())
  const writePending = useRef(false)
  const selection = rowIds.join('|')
  useEffect(() => {
    let active = true
    void getPayrollSetupContext(clientId, batch.id, rowIds).then(result => {
      if (!active) return
      setBusy('')
      if (!result.ok || !result.data) return setError(result.error || 'Unable to load payroll setup.')
      const data = result.data
      setContext(data)
      const saved = data.lastImport
      if (saved && [...saved.savedRequest.rowIds].sort().join('|') === [...rowIds].sort().join('|')) {
        setRequest(saved.savedRequest); requestId.current = saved.requestId
        setDefaultsDraft(saved.editableDefaultsDraft || saved.savedRequest.editableDefaults); setDefaultsRevision(saved.defaultsRevision || 0)
        if (saved.status === 'Applied' && saved.result) setApplied(saved.result)
        setNotice(saved.status === 'Applied' ? 'This setup has already been applied. Its saved result is shown below.' : 'Your saved setup draft has been restored.')
      } else {
        const proposed = proposeExcelPayrollSetup(batch, data, rowIds)
        setRequest(proposed.request); setNotes(proposed.notes)
      }
    }).catch(failure => { if (active) { setBusy(''); setError(failure instanceof Error ? failure.message : 'Unable to load payroll setup.') } })
    return () => { active = false }
    // The parent holds the selected rows stable while this drawer is open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientId, batch.id, selection])
  const patch = (change: Partial<PayrollSetupRequest>) => { setRequest(current => current ? { ...current, ...change } : current); setPreview(null); setReviewed(false); setNotice('') }
  const patchComponent = (key: string, change: Partial<PayrollSetupComponent>) => request && patch({ components: request.components.map(item => item.key === key ? { ...item, ...change } : item) })
  const classifyColumn = (columnIndex: number, kind: string) => {
    if (!request || !context) return
    const roles = { ...request.sourceColumnRoles, [columnIndex]: kind }
    const proposed = proposeExcelPayrollSetup(batch, context, rowIds, roles)
    patch({ components: proposed.request.components, sourceColumnRoles: proposed.request.sourceColumnRoles })
    setNotes(proposed.notes)
  }
  const addComponent = () => request && patch({ components: [...request.components, { key: crypto.randomUUID(), code: `CUSTOM_${request.components.length + 1}`, name: 'Reviewed salary component', category: 'Earning', componentRole: 'Regular Earning', statutoryType: 'None', calculationType: 'Fixed Amount', value: '0', formula: '', baseComponent: '', proRata: true, taxable: true, priority: 1000 + request.components.length, isPeriodAdjustment: false, exemptWhenSourceZero: false, blankInputAsZero: false }] })
  const changeCalculation = (component: PayrollSetupComponent, calculationType: string) => patchComponent(component.key, {
    calculationType,
    ...(calculationType === 'Formula' ? { sourceColumnIndex: undefined, isPeriodAdjustment: false, proRata: false } : {}),
  })
  const patchDefault = (key: string, change: Record<string, unknown>) => {
    if (applied) { setDefaultsDraft(current => ({ ...current, [key]: { ...object(current[key]), ...change } })); setNotice('') }
    else if (request) patch({ editableDefaults: { ...request.editableDefaults, [key]: { ...object(request.editableDefaults[key]), ...change } } })
  }
  const run = async (kind: 'preview' | 'draft' | 'apply') => {
    if (!request || busy || writePending.current) return
    writePending.current = true; setBusy(kind === 'preview' ? 'Checking calculations' : kind === 'draft' ? 'Saving draft' : 'Applying setup'); setError(''); setNotice('')
    try {
      if (kind === 'preview') {
        const result = await previewPayrollSetup(clientId, request)
        if (!result.ok || !result.data) throw new Error(result.error || 'Unable to preview payroll setup.')
        setPreview(result.data); setReviewed(false)
      } else if (kind === 'draft') {
        const result = await savePayrollSetupDraft(clientId, request, requestId.current)
        if (!result.ok) throw new Error(result.error || 'Unable to save setup draft.')
        setNotice('Draft saved in the database. Reopen Configure payroll for these rows to continue editing. Leave and holiday drafts remain inactive.')
      } else if (preview?.canApply && !preview.migrationRequired && reviewed) {
        const result = await applyPayrollSetup(clientId, request, preview.planHash, requestId.current)
        if (!result.ok || !result.data) throw new Error(result.error || 'Setup could not be confirmed. Retry keeps the same request reference.')
        setApplied(result.data); setDefaultsDraft(request.editableDefaults); onApplied(result.data)
      }
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Payroll setup could not be completed.') }
    finally { writePending.current = false; setBusy('') }
  }
  const selected = batch.rows.filter(row => rowIds.includes(row.id))
  const saveDefaults = async () => {
    if (!applied || busy || writePending.current) return
    writePending.current = true; setBusy('Saving draft defaults'); setError(''); setNotice('')
    try {
      const result = await savePayrollDefaultsDraft(clientId, batch.id, applied.importId, defaultsRevision, defaultsDraft)
      if (!result.ok || !result.data) throw new Error(result.error || 'Unable to save inactive proposals.')
      setDefaultsDraft(result.data.editableDefaultsDraft); setDefaultsRevision(result.data.defaultsRevision)
      setNotice('Draft defaults saved. Salary configuration and the original import audit remain unchanged; leave, holiday and shift proposals remain inactive.')
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to save draft defaults.') }
    finally { writePending.current = false; setBusy('') }
  }
  const verify = async () => {
    if (!applied || busy || writePending.current) return
    writePending.current = true; setBusy('Verifying saved payroll'); setError('')
    try { setVerification(await verifyPayrollSetupEmployees(clientId, batch.month, applied.employeeIds)) }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to verify saved payroll.') }
    finally { writePending.current = false; setBusy('') }
  }
  const verifiedRows = verification.map(item => {
    const source = selected[applied?.employeeIds.indexOf(item.employeeId) ?? -1]
    const calculated = item.data
    return { ...item, employeeName: source?.employeeName || calculated?.employee || String(item.employeeId), excelNet: source?.netPay,
      payrollDays: calculated?.inputs.find(input => input.name === 'PAYROLL_DAYS')?.value,
      payableDays: calculated?.inputs.find(input => input.name === 'PAYABLE_DAYS')?.value,
      net: calculated?.net, difference: source && calculated?.net != null ? calculated.net - source.netPay : undefined,
      deductions: calculated?.lines.filter(line => line.category === 'Deduction' && line.earned !== 0).map(line => `${line.code}: ${money(line.earned)}`).join('; ') }
  })
  const columnOptions = context?.source.columns.map(column => ({ value: column.columnIndex, label: `${excelColumnLetter(column.columnIndex)} · ${column.sourceHeader || '(blank heading)'}` })) || []
  const field = (label: string, key: 'templateName' | 'effectiveFrom' | 'defaultDepartment' | 'defaultDesignation' | 'defaultGrade', type = 'text') => <Form.Item label={label}><Input aria-label={`Payroll setup ${label}`} type={type} value={request?.[key]} onChange={event => patch({ [key]: event.target.value })} /></Form.Item>
  const schemaMissing = context?.migrationRequired || preview?.migrationRequired
  const defaults = applied ? defaultsDraft : request?.editableDefaults || {}
  const workWeekOptions = [...context?.workWeeks || []]
  for (const value of [request?.workWeek, 'Sunday off']) if (value && !workWeekOptions.some(option => option.value === value)) workWeekOptions.push({ value, label: `${value} (reviewed default)` })
  return <Drawer open title="Configure payroll from Excel" width="min(1160px, 98vw)" className="excel-payroll-setup" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space wrap className="excel-payroll-setup-footer">
    <Button disabled={!!busy} onClick={onClose}>Close</Button>
    {applied && <Button disabled={!!busy} loading={busy === 'Saving draft defaults'} onClick={() => void saveDefaults()}>Save draft defaults</Button>}
    {applied && <Button type="primary" disabled={!!busy} loading={busy === 'Verifying saved payroll'} onClick={() => void verify()}>Verify saved payroll</Button>}
    {!applied && <><Button disabled={!request || !!busy || !!schemaMissing} loading={busy === 'Saving draft'} onClick={() => void run('draft')}>Save setup draft</Button><Button disabled={!request || !!busy} loading={busy === 'Checking calculations'} onClick={() => void run('preview')}>Preview calculations</Button><Button type="primary" disabled={!preview?.canApply || !!schemaMissing || !reviewed || !!busy} loading={busy === 'Applying setup'} onClick={() => void run('apply')}>Apply reviewed setup</Button></>}
  </Space>}>
    {error && <Alert type="error" showIcon message={error} />}
    {notice && <Alert type="success" showIcon message={notice} />}
    {schemaMissing && <Alert type="warning" showIcon message="Setup draft storage needs its database migration" description={<><p>Preview is available. To save and reopen setup drafts, run this command from the project folder:</p><code>dotnet run --project .\Payroll.API\Payroll.API.csproj --launch-profile http -- --migrate-excel-payroll-setup</code></>} />}
    {busy === 'Loading setup' && <Spin tip="Loading saved source and client configuration..." />}
    {applied && <Alert type="success" showIcon message={`Setup saved: ${applied.createdCount} employees created, ${applied.updatedCount} updated`} description={`Import ${applied.importId}. Run Employee simulation to check effective tax and policy rules before processing payroll. Leave, shift and holiday proposals remain saved as editable drafts.`} />}
    {!!verification.length && <div className="excel-payroll-setup-preview"><Alert type={verifiedRows.some(row => !row.ok || !row.data?.complete || row.difference == null || Math.abs(row.difference) > 0.0100001) ? 'warning' : 'success'} showIcon message="Saved payroll compared with Excel" description="Uses saved employee history, monthly attendance, approved adjustments and current statutory settings. Amounts use two decimals here so rounding differences remain visible. This verification does not create a pay run or send email." /><DataTable title="Saved payroll verification" rows={verifiedRows} getRowId={row => row.employeeId} exportFileName={`salary-setup-verification-${batch.month}`} columns={[
      { key: 'employeeName', label: 'Employee', width: 220 }, { key: 'payrollDays', label: 'Divisor', width: 85 }, { key: 'payableDays', label: 'Payable days', width: 110 },
      { key: 'excelNet', label: 'Excel in hand', render: row => money(row.excelNet) }, { key: 'net', label: 'Payroll in hand', render: row => money(row.net) }, { key: 'difference', label: 'Difference', render: row => money(row.difference) },
      { key: 'deductions', label: 'Calculated deductions', width: 250, wrap: true }, { key: 'status', label: 'Review', width: 220, wrap: true, value: row => !row.ok || !row.data?.complete ? row.error || row.data?.issues.filter(issue => issue.severity === 'Error').map(issue => issue.message).join('; ') || 'Verification failed' : row.difference == null ? 'Source comparison unavailable' : Math.abs(row.difference) > 0.0100001 ? 'Review effective tax, policy and adjustments' : Math.abs(row.difference) > 0.0000001 ? 'Matches within 1 paisa rounding' : 'Match' },
    ]} /></div>}
    {context?.defaults && <Alert type="info" showIcon message={context.defaults.attendanceSource} description="Defaults below come from this client or the monthly Excel workflow. Source values and reviewed choices take priority; existing employee details and tax settings stay unchanged." />}
    {request && context && <Form component="div" layout="vertical" disabled={!!busy || !!applied}>
      <Space wrap><Tag color="blue">{batch.clientName || `Client ${clientId}`}</Tag><Tag>{batch.month}</Tag><Tag>{selected.length} selected employees</Tag></Space>
      <Alert type="info" showIcon message="Review the source, employee matches and formulas before creating payroll configuration." description="Apply saves client templates, employee assignments and this month's attendance. Missing components are created only as reviewed below. Leave entitlements and holiday dates stay inactive in the saved setup draft until approved." />
      <Sections defaultActiveKey={['basis', 'employees', 'components']} items={[
        { key: 'basis', label: 'Salary month and attendance', children: <div className="excel-payroll-setup-grid">
          {field('Template name', 'templateName')}{field('Effective from', 'effectiveFrom', 'date')}
          <Form.Item label="Payroll divisor source"><Select aria-label="Payroll divisor source" value={request.fixedPayrollDays == null ? 'column' : 'fixed'} options={[{ value: 'column', label: 'Excel column (per employee)' }, { value: 'fixed', label: 'Fixed days (e.g. 26)' }]} onChange={value => patch(value === 'fixed' ? { payrollDaysColumnIndex: -1, fixedPayrollDays: 26 } : { fixedPayrollDays: undefined, payrollDaysColumnIndex: -1 })} /></Form.Item>
          {request.fixedPayrollDays == null ? <Form.Item label="Payroll divisor column"><Select aria-label="Payroll divisor column" value={request.payrollDaysColumnIndex < 0 ? undefined : request.payrollDaysColumnIndex} options={columnOptions} onChange={value => patch({ payrollDaysColumnIndex: value })} /></Form.Item> : <Form.Item label="Reviewed fixed divisor"><InputNumber aria-label="Reviewed fixed payroll days" min={1} max={31} value={request.fixedPayrollDays} onChange={value => patch({ fixedPayrollDays: value ?? undefined })} /></Form.Item>}
          <Form.Item label="Payable days column"><Select aria-label="Payable days column" value={request.payableDaysColumnIndex} options={columnOptions} onChange={value => patch({ payableDaysColumnIndex: value })} /></Form.Item>
          <Form.Item label="Attendance import" extra="Turn off to keep regular daily attendance and weekly-off rules. Source days are still used for this comparison."><Checkbox checked={request.importAttendance !== false} onChange={event => patch({ importAttendance: event.target.checked })}>Import source-month attendance</Checkbox></Form.Item>
          <Form.Item label="Work week"><Select aria-label="Payroll setup work week" value={request.workWeek || undefined} options={workWeekOptions} onChange={value => patch({ workWeek: value })} /></Form.Item>
          <Form.Item label="Work location column"><Select aria-label="Work location column" allowClear value={request.sourceWorkLocationColumnIndex} options={columnOptions} onChange={value => patch({ sourceWorkLocationColumnIndex: value })} /></Form.Item>
          <Form.Item label="Attendance cycle start day"><InputNumber aria-label="Attendance cycle start day" min={1} max={31} value={request.attendanceCycleStartDay} onChange={value => patch({ attendanceCycleStartDay: value || 1 })} /></Form.Item>
          <Form.Item label="Attendance cycle end day"><InputNumber aria-label="Attendance cycle end day" min={1} max={31} value={request.attendanceCycleEndDay} onChange={value => patch({ attendanceCycleEndDay: value || 30 })} /></Form.Item>
          <Form.Item label="Report generation day"><InputNumber aria-label="Report generation day" min={1} max={31} value={request.payrollReportGenerationDay} onChange={value => patch({ payrollReportGenerationDay: value || 1 })} /></Form.Item>
          <Form.Item label="State for new work locations"><Input aria-label="State for new work locations" value={request.defaultWorkLocationState || ''} placeholder="Review when creating a source location" onChange={event => patch({ defaultWorkLocationState: event.target.value })} /></Form.Item><Form.Item label="New work locations"><Checkbox checked={request.createMissingWorkLocations === true} onChange={event => patch({ createMissingWorkLocations: event.target.checked })}>Create reviewed source locations for this client</Checkbox></Form.Item>
        </div> },
        { key: 'employees', label: 'Employee Master matches', children: <><DataTable title="Selected employees" rows={selected} getRowId={row => row.id} exportDisabled hideSearch columns={[
          { key: 'sourceRow', label: 'Excel row', width: 85 }, { key: 'employeeName', label: 'Employee', width: 210 }, { key: 'location', label: 'Source location', value: row => request.sourceWorkLocationColumnIndex === undefined ? '' : context.source.rows.find(item => item.sourceRow === row.sourceRow)?.cells[request.sourceWorkLocationColumnIndex]?.value || '', width: 160 },
          { key: 'match', label: 'Employee Master record', width: 330, sortable: false, filterable: false, render: row => <Select aria-label={`Employee match for row ${row.sourceRow}`} showSearch optionFilterProp="label" allowClear value={request.employeeDecisions.find(item => item.rowId === row.id)?.matchedEmployeeId} options={context.employees.map(employee => ({ value: employee.id, label: `${employee.employeeCode} · ${employee.employeeName} · ${employee.workLocation || 'Location not set'}` }))} onChange={value => patch({ employeeDecisions: request.employeeDecisions.map(item => item.rowId === row.id ? { ...item, matchedEmployeeId: value, allowCreate: false } : item) })} /> },
          { key: 'create', label: 'Create if unmatched', width: 165, sortable: false, filterable: false, render: row => { const decision = request.employeeDecisions.find(item => item.rowId === row.id); return <Checkbox aria-label={`Create employee for row ${row.sourceRow}`} disabled={!!decision?.matchedEmployeeId || !!busy || !!applied} checked={decision?.allowCreate === true} onChange={event => patch({ employeeDecisions: request.employeeDecisions.map(item => item.rowId === row.id ? { ...item, allowCreate: event.target.checked } : item) })}>Reviewed</Checkbox> } },
        ]} /><p>Existing employees keep their identity and employee code. A new employee requires an explicit creation choice; ambiguous matches block Apply.</p><div className="excel-payroll-setup-grid">{field('Default department', 'defaultDepartment')}{field('Default designation', 'defaultDesignation')}{field('Default grade', 'defaultGrade')}<Form.Item label="Default work location"><Select aria-label="Default work location" allowClear value={request.defaultWorkLocationId} options={context.workLocations.map(location => ({ value: location.id, label: location.name }))} onChange={value => patch({ defaultWorkLocationId: value })} /></Form.Item></div></> },
        { key: 'columns', label: 'Payroll column mapping (independent of payslip layout)', children: <><p>Include base/earned components even when hidden on the payslip. Keep totals and billing fields out of earning/deduction components. Changing a column regenerates proposed components; review any formula edits again.</p><DataTable rows={context.source.columns} getRowId={column => column.columnIndex} exportDisabled title="Authoritative salary worksheet" columns={[
          { key: 'column', label: 'Column', value: column => excelColumnLetter(column.columnIndex) }, { key: 'sourceHeader', label: 'Excel heading' },
          { key: 'role', label: 'Payroll use', sortable: false, filterable: false, render: column => <Select aria-label={`Payroll column ${excelColumnLetter(column.columnIndex)}`} value={request.sourceColumnRoles?.[column.columnIndex] || column.kind} options={payslipColumnKinds} onChange={value => classifyColumn(column.columnIndex, value)} /> },
        ]} /></> },
        { key: 'components', label: `Salary components and formulas (${request.components.length})`, children: <><Button onClick={addComponent}>Add payroll component</Button>{notes.length > 0 && <Alert type="warning" showIcon message="Source formula review" description={<ul>{notes.map((note, index) => <li key={index}>{note}</li>)}</ul>} />}<Sections items={request.components.map(component => ({ key: component.key, label: <Space wrap><strong>{component.code}</strong><span>{component.name}</span><Tag color={component.existingComponentId ? 'default' : 'orange'}>{component.existingComponentId ? 'Reuse master' : 'Create master'}</Tag><Tag>{component.category}</Tag>{component.isPeriodAdjustment && <Tag color="blue">This month only</Tag>}</Space>, children: <div className="excel-payroll-setup-grid">
          <div className="excel-payroll-setup-wide"><Button danger onClick={() => patch({ components: request.components.filter(item => item.key !== component.key) })}>Remove this payroll component</Button></div>
          <Form.Item label="Component master"><Select aria-label={`Component master ${component.code}`} allowClear showSearch optionFilterProp="label" value={component.existingComponentId} options={context.components.map(master => ({ value: master.id, label: `${master.code} · ${master.name} (${master.category})` }))} placeholder="Create reviewed component" onChange={value => { const master = context.components.find(item => item.id === value); patchComponent(component.key, master ? { existingComponentId: value, code: master.code, name: master.name, category: master.category, componentRole: master.componentRole, statutoryType: master.statutoryType } : { existingComponentId: undefined }) }} /></Form.Item>
          <Form.Item label="Code" extra="If the code changes, review references to it in other component formulas before previewing."><Input aria-label={`Component code ${component.key}`} disabled={!!busy || !!applied || !!component.existingComponentId} value={component.code} onChange={event => patchComponent(component.key, { code: event.target.value })} /></Form.Item>
          <Form.Item label="Name"><Input value={component.name} disabled={!!busy || !!applied || !!component.existingComponentId} onChange={event => patchComponent(component.key, { name: event.target.value })} /></Form.Item>
          <Form.Item label="Category"><Select value={component.category} disabled={!!busy || !!applied || !!component.existingComponentId} options={['Earning', 'Deduction', 'Benefit', 'Reimbursement'].map(value => ({ value, label: value }))} onChange={value => patchComponent(component.key, { category: value })} /></Form.Item>
          <Form.Item label="Calculation"><Select aria-label={`Calculation ${component.code}`} value={component.calculationType} options={(component.isPeriodAdjustment ? ['Manual / Variable'] : ['Fixed Amount', 'Formula', 'Daily Rate']).map(value => ({ value, label: value }))} onChange={value => changeCalculation(component, value)} /></Form.Item>
          <Form.Item label="Component role"><Input aria-label={`Component role ${component.code}`} disabled={!!busy || !!applied || !!component.existingComponentId} value={component.componentRole} onChange={event => patchComponent(component.key, { componentRole: event.target.value })} /></Form.Item>
          <Form.Item label="Statutory type"><Select aria-label={`Statutory type ${component.code}`} disabled={!!busy || !!applied || !!component.existingComponentId} value={component.statutoryType} options={['None', ...(component.category === 'Benefit' ? employerStatutoryTypes : employeeStatutoryTypes)].map(value => ({ value, label: value }))} onChange={value => patchComponent(component.key, { statutoryType: value })} /></Form.Item>
          <Form.Item label="Source input column"><Select aria-label={`Input column ${component.code}`} allowClear value={component.sourceColumnIndex} options={columnOptions} onChange={value => patchComponent(component.key, { sourceColumnIndex: value })} /></Form.Item>
          <Form.Item label="Expected result column"><Select aria-label={`Expected column ${component.code}`} allowClear value={component.expectedColumnIndex} options={columnOptions} onChange={value => patchComponent(component.key, { expectedColumnIndex: value })} /></Form.Item>
          <Form.Item label="Amount or percentage"><Input aria-label={`Value ${component.code}`} value={component.value} onChange={event => patchComponent(component.key, { value: event.target.value })} /></Form.Item>
          <Form.Item label="Formula" className="excel-payroll-setup-wide"><Input.TextArea aria-label={`Formula ${component.code}`} value={component.formula} autoSize={{ minRows: 2, maxRows: 5 }} onChange={event => patchComponent(component.key, { formula: event.target.value, rowFormulas: {} })} /></Form.Item>
          {Object.keys(component.rowFormulas || {}).length > 0 && <div className="excel-payroll-setup-wide"><p>Source rows with different formulas use separate client template variants.</p>{Object.entries(component.rowFormulas || {}).map(([id, formula]) => <Form.Item key={id} label={`${batch.rows.find(row => row.id === id)?.employeeName || id} formula`}><Input aria-label={`Formula ${component.code} ${id}`} value={formula} onChange={event => patchComponent(component.key, { rowFormulas: { ...component.rowFormulas, [id]: event.target.value } })} /></Form.Item>)}</div>}
          <Space wrap className="excel-payroll-setup-wide"><Checkbox checked={component.proRata} onChange={event => patchComponent(component.key, { proRata: event.target.checked })}>Prorate using payable days</Checkbox><Checkbox checked={component.blankInputAsZero === true} onChange={event => patchComponent(component.key, { blankInputAsZero: event.target.checked })}>Treat blank source inputs as zero (reviewed)</Checkbox><Checkbox checked={component.exemptWhenSourceZero} onChange={event => patchComponent(component.key, { exemptWhenSourceZero: event.target.checked })}>Preserve explicit source zero exemptions</Checkbox><Checkbox disabled={!!busy || !!applied || component.category === 'Benefit'} checked={component.isPeriodAdjustment} onChange={event => patchComponent(component.key, { isPeriodAdjustment: event.target.checked, calculationType: event.target.checked ? 'Manual / Variable' : 'Fixed Amount', sourceColumnIndex: component.sourceColumnIndex ?? component.expectedColumnIndex, proRata: !event.target.checked, formula: '', rowFormulas: {} })}>This month only</Checkbox></Space>
        </div> }))} /></> },
        { key: 'master-defaults', label: 'Client defaults and master creation', children: <Space direction="vertical"><Checkbox disabled={!!busy || !!applied} checked={request.createMissingDropdownValues !== false} onChange={event => patch({ createMissingDropdownValues: event.target.checked })}>Create reviewed missing department, designation, grade and work-week values for this client</Checkbox><Checkbox disabled={!!busy || !!applied} checked={request.preserveExistingAttendancePolicies !== false} onChange={event => patch({ preserveExistingAttendancePolicies: event.target.checked })}>Preserve existing attendance policy memberships</Checkbox></Space> },
        { key: 'defaults', label: 'Saved leave, holiday and shift drafts', children: <><Alert type="info" showIcon message="These proposals are saved with this setup, including after Apply. They do not create active leave balances, holidays or shifts." /><div className="excel-payroll-setup-grid"><Form.Item label="Proposed yearly paid leave (inactive)"><InputNumber disabled={!!busy} aria-label="Proposed yearly paid leave" min={0} step={0.5} value={Number(object(defaults.leave).proposedAnnualPaidLeave || 0)} onChange={value => patchDefault('leave', { proposedAnnualPaidLeave: value ?? 0, status: 'Draft', active: false, quotaApproved: false })} /></Form.Item><Form.Item label="Holiday calendar notes"><Input.TextArea disabled={!!busy} aria-label="Holiday calendar notes" value={String(object(defaults.holiday).notes || '')} onChange={event => patchDefault('holiday', { notes: event.target.value, status: 'Draft' })} /></Form.Item><Form.Item label="Shift setup notes"><Input.TextArea disabled={!!busy} aria-label="Shift setup notes" value={String(object(defaults.shift).notes || '')} onChange={event => patchDefault('shift', { notes: event.target.value, status: 'Draft' })} /></Form.Item></div></> },
      ]} />
    </Form>}
    {preview && <div className="excel-payroll-setup-preview"><Alert type="info" showIcon message="Calculation coverage" description={preview.coverage} />{preview.issues.map((issue, index) => <Alert key={index} showIcon type={issue.severity === 'Error' ? 'error' : 'warning'} message={[issue.code, issue.message].filter(Boolean).join(': ')} />)}<DataTable title="Source comparison" rows={preview.rows} getRowId={row => row.rowId} exportDisabled columns={[
      { key: 'employeeName', label: 'Employee', width: 220 }, { key: 'action', label: 'Master action', width: 100 }, { key: 'payrollDays', label: 'Divisor', width: 85 }, { key: 'payableDays', label: 'Payable days', width: 110 }, { key: 'gross', label: 'Gross', render: row => money(row.gross) }, { key: 'deductions', label: 'Deductions', render: row => money(row.deductions) }, { key: 'net', label: 'Calculated in hand', render: row => money(row.net) }, { key: 'sourceNet', label: 'Excel in hand', render: row => money(batch.rows.find(item => item.id === row.rowId)?.netPay) }, { key: 'variance', label: 'Difference', render: row => money(row.net == null ? null : row.net - (batch.rows.find(item => item.id === row.rowId)?.netPay || 0)) },
    ]} /><Sections items={preview.rows.map(row => ({ key: row.rowId, label: `${row.employeeName} · component comparison`, children: <DataTable rows={row.lines} getRowId={line => line.code} exportDisabled hideSearch columns={[{ key: 'code', label: 'Component' }, { key: 'sourceAmount', label: 'Excel', render: line => money(line.sourceAmount) }, { key: 'calculatedAmount', label: 'Calculated', render: line => money(line.calculatedAmount) }, { key: 'variance', label: 'Difference', render: line => money(line.variance) }]} /> }))} />{!applied && <Checkbox aria-label="Review payroll setup" disabled={!preview.canApply || !!busy} checked={reviewed} onChange={event => setReviewed(event.target.checked)}>I reviewed employee matches, new masters, formulas, source blanks and comparison results for these {selected.length} employees.</Checkbox>}</div>}
  </Drawer>
}
