import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Checkbox, Collapse, Drawer, Form, Input, InputNumber, Select, Space, Spin, Tag } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { applyPayrollSetup, getPayrollSetupContext, previewPayrollSetup, savePayrollSetupDraft, savePayrollDefaultsDraft, verifyPayrollSetupEmployees, type PayrollSetupComponent, type PayrollSetupContext, type PayrollSetupPreview, type PayrollSetupRequest, type PayrollSetupResult, type PayrollSetupVerification } from '../services/excelPayrollSetupService'
import ExcelPayrollTransferReview from './ExcelPayrollTransferReview'
import ExcelPayrollEmployeeObservation from './ExcelPayrollEmployeeObservation'
import ExcelPayrollSourceEvidence from './ExcelPayrollSourceEvidence'
import ExcelPayrollSourceInputReview from './ExcelPayrollSourceInputReview'
import ExcelPayrollTemplateApplicability from './ExcelPayrollTemplateApplicability'
import ExcelPayrollCurrentComparison from './ExcelPayrollCurrentComparison'
import ExcelPayrollWhatIfSimulation from './ExcelPayrollWhatIfSimulation'
import ExcelPayrollLocationAllocations from './ExcelPayrollLocationAllocations'
import ExcelPayrollIdentityColumnReview from './ExcelPayrollIdentityColumnReview'
import { savedPayrollVerificationTargets } from '../utils/excelPayrollSavedVerification'
import { refreshAppliedPayrollSetupContext } from '../utils/excelPayrollAppliedContext'
import { compareTransferPayroll } from '../utils/excelPayrollTransfer'
import { payrollComparisonMoney, payrollDisplayEvidence, payrollRawAmount, payrollRawDifference } from '../utils/payrollRoundingEvidence'
import { payrollSetupMasterPriority, proposeExcelPayrollSetup, restoreExcelPayrollMasterMetadata, selectExcelPayrollMaster } from '../utils/excelPayrollSetup'
import { isLocationAllocationReviewOnly } from '../utils/excelPayrollLocationAllocations'
import { compareLocationTransferPayroll, locationPayrollVerificationTargets } from '../utils/excelPayrollLocationVerification'
import { excelColumnLetter, payslipColumnKinds } from '../utils/excelPayslipImport'
import { employeeStatutoryTypes, employerStatutoryTypes } from '../utils/salaryComponentConfig'
import DataTable from './DataTable'
import './ExcelPayrollSetup.css'

type Props = { clientId: number; batch: ExcelPayslipBatch; rowIds: string[]; onClose: () => void; onApplied: (result: PayrollSetupResult) => void }
const money = payrollComparisonMoney
const sourceMoney = (value?: number | null) => <span>{money(value)}<small style={{ display: 'block', overflowWrap: 'anywhere' }}>Raw snapshot: {payrollRawAmount(value)}</small></span>
const object = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
const Sections = ({ items, defaultActiveKey }: { items: { key: string; label: ReactNode; children: ReactNode }[]; defaultActiveKey?: string[] }) => <Collapse defaultActiveKey={defaultActiveKey}>{items.map(item => <Collapse.Panel key={item.key} header={item.label}>{item.children}</Collapse.Panel>)}</Collapse>

export default function ExcelPayrollSetup({ clientId, batch, rowIds, onClose, onApplied }: Props) {
  const [context, setContext] = useState<PayrollSetupContext | null>(null)
  const [request, setRequest] = useState<PayrollSetupRequest | null>(null)
  const [preview, setPreview] = useState<(PayrollSetupPreview & { reviewRefreshRequired?: boolean }) | null>(null)
  const [notes, setNotes] = useState<string[]>([])
  const [busy, setBusy] = useState('Loading setup')
  const [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [reviewed, setReviewed] = useState(false)
  const [divisorMode, setDivisorMode] = useState<'column' | 'fixed' | null>(null)
  const [transferReceiptId, setTransferReceiptId] = useState('')
  const [applied, setApplied] = useState<PayrollSetupResult | null>(null)
  const [verificationContextWarning, setVerificationContextWarning] = useState('')
  const [verification, setVerification] = useState<PayrollSetupVerification[]>([])
  const [compareCurrent, setCompareCurrent] = useState(false)
  const [defaultsDraft, setDefaultsDraft] = useState<Record<string, unknown>>({})
  const [defaultsRevision, setDefaultsRevision] = useState(0)
  const [transferRequestId, setTransferRequestId] = useState<string>(() => crypto.randomUUID())
  const requestId = useRef<string>(transferRequestId)
  const writePending = useRef(false)
  const contextScope = useRef({ version: 0 })
  const selection = rowIds.join('|')
  useEffect(() => {
    let active = true
    const scopeState = contextScope.current, scope = ++scopeState.version
    void getPayrollSetupContext(clientId, batch.id, rowIds).then(result => {
      if (!active) return
      setBusy('')
      if (!result.ok || !result.data) return setError(result.error || 'Unable to load payroll setup.')
      const data = result.data
      setDivisorMode(null)
      setContext(data)
      const saved = data.lastImport
      if (saved && [...saved.savedRequest.rowIds].sort().join('|') === [...rowIds].sort().join('|')) {
        setRequest(saved.status === 'Applied' ? saved.savedRequest : { ...restoreExcelPayrollMasterMetadata(saved.savedRequest, data), transferReviewVersion: 1, templateScopeReviewVersion: 1 }); requestId.current = saved.requestId; setTransferRequestId(saved.requestId); setTransferReceiptId(saved.importId)
        setDefaultsDraft(saved.editableDefaultsDraft || saved.savedRequest.editableDefaults); setDefaultsRevision(saved.defaultsRevision || 0)
        if (saved.status === 'Applied' && saved.result) setApplied(saved.result)
        setNotice(saved.status === 'Applied' ? 'This setup has already been applied. Its saved result is shown below.' : saved.status === 'Kept' ? 'Your keep decision was recorded. Existing assignments were unchanged. Edit and repreview to choose Excel-derived setup; parity remains unresolved.' : 'Your saved setup draft has been restored.')
      } else {
        const proposed = proposeExcelPayrollSetup(batch, data, rowIds)
        setRequest({ ...proposed.request, transferReviewVersion: 1, templateScopeReviewVersion: 1 }); setNotes(proposed.notes)
      }
    }).catch(failure => { if (active) { setBusy(''); setError(failure instanceof Error ? failure.message : 'Unable to load payroll setup.') } })
    return () => { active = false; if (scopeState.version === scope) ++scopeState.version }
    // The parent holds the selected rows stable while this drawer is open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientId, batch.id, selection])
  const patch = (change: Partial<PayrollSetupRequest>) => { setRequest(current => current ? { ...current, ...change, ...(change.templateScope || !Object.keys(change).some(key=>['effectiveFrom','components','employeeDecisions','fixedPayrollDays','payrollDaysCell','payrollDaysColumnIndex','payableDaysColumnIndex','presentDaysColumnIndex','workWeek','attendanceCycleStartDay','attendanceCycleEndDay','sourceWorkLocationColumnIndex'].includes(key)) ? {} : current.templateScope ? {templateScope:{...current.templateScope,reviewed:false}} : {}) } : current); const reviewOnly = Object.keys(change).length > 0 && Object.keys(change).every(key => (['reviewedFixedInputs', 'reviewedPolicyScope', 'roundingReviews', 'attendanceReview', 'templateDecisions'].includes(key) || key === 'locationAllocations' && isLocationAllocationReviewOnly(request?.locationAllocations, change.locationAllocations))); setPreview(current => reviewOnly && current ? { ...current, canApply: false, reviewRefreshRequired: true } : null); setReviewed(false); setNotice('') }
  const patchComponent = (key: string, change: Partial<PayrollSetupComponent>) => request && patch({ components: request.components.map(item => item.key === key ? { ...item, ...change } : item) })
  const startSalaryCorrection = (enabled: boolean) => {
    if (!request || rowIds.length !== 1 || context?.salaryCorrectionSupported !== true) return
    if (!enabled) { patch({ correctionOfImportId: undefined }); return }
    const parent = applied?.importId || (context.lastImport?.status === 'Applied' ? context.lastImport.importId : '')
    const nextId = crypto.randomUUID()
    requestId.current = nextId; setTransferRequestId(nextId); setTransferReceiptId(''); setApplied(null); setVerification([])
    patch({ correctionOfImportId: parent, rowIds: [...rowIds], importAttendance: false, importPayableOnlyAttendance: false,
      overrideSourcePayableAttendance: false, createMissingWorkLocations: false, createMissingDropdownValues: false,
      preserveExistingAttendancePolicies: true, presentDaysColumnIndex: undefined, templateDecisions: [] })
  }
  const persistedMasterMetadata = (component: PayrollSetupComponent) => {
    if (!component.existingComponentId) return null
    const master = context?.components.find(item => item.id === component.existingComponentId)
    return <section className="excel-payroll-setup-wide" aria-label={`Persisted master metadata ${component.key}`}><strong>Persisted shared component settings</strong><Space wrap><Tag>Taxable: {typeof master?.taxable === 'boolean' ? master.taxable ? 'Yes' : 'No' : 'Unavailable'}</Tag><Tag>Priority: {payrollSetupMasterPriority(master?.priority) ?? 'Unavailable'}</Tag><Tag>Pay type: {master?.payType?.trim() || 'Unavailable'}</Tag></Space><p>These settings are retained from the shared component. Source formulas and template overrides do not overwrite them. Choose a separate reviewed component when different settings are required.</p></section>
  }
  const classifyColumn = (columnIndex: number, kind: string) => {
    if (!request || !context) return
    const roles = { ...request.sourceColumnRoles, [columnIndex]: kind }
    const proposed = proposeExcelPayrollSetup(batch, context, rowIds, roles)
    patch({ components: proposed.request.components, sourceColumnRoles: proposed.request.sourceColumnRoles })
    setNotes(proposed.notes)
  }
  const addComponent = () => request && patch({ components: [...request.components, { key: crypto.randomUUID(), code: `CUSTOM_${request.components.length + 1}`, name: 'Reviewed salary component', category: 'Earning', componentRole: 'Regular Earning', statutoryType: 'None', calculationType: 'Fixed Amount', value: '0', formula: '', baseComponent: '', proRata: true, taxable: true, priority: 1000 + request.components.length, payType: 'Fixed Pay', isPeriodAdjustment: false, exemptWhenSourceZero: false, blankInputAsZero: false }] })
  const changeCalculation = (component: PayrollSetupComponent, calculationType: string) => patchComponent(component.key, {
    calculationType,
    ...(calculationType === 'Formula' ? { sourceColumnIndex: undefined, isPeriodAdjustment: false, proRata: false } : {}),
  })
  const patchDefault = (key: string, change: Record<string, unknown>) => {
    if (applied) { setDefaultsDraft(current => ({ ...current, [key]: { ...object(current[key]), ...change } })); setNotice('') }
    else if (request) patch({ editableDefaults: { ...request.editableDefaults, [key]: { ...object(request.editableDefaults[key]), ...change } } })
  }
  const masterMetadataBlocked = !applied && !!request?.components.some(component => {
    if (!component.existingComponentId) return false
    const master = context?.components.find(item => item.id === component.existingComponentId)
    return !master || typeof master.taxable !== 'boolean' || payrollSetupMasterPriority(master.priority) === null || !['Fixed Pay', 'Variable Pay'].includes(master.payType || '')
  })
  const run = async (kind: 'preview' | 'draft' | 'apply') => {
    if (!request || busy || writePending.current) return
    if (masterMetadataBlocked) { setError('Persisted component Taxable, Priority or Pay type is unavailable. Refresh the reviewed API context or choose a separate reviewed component; no master settings were inferred.'); return }
    const activeScope = contextScope.current.version
    writePending.current = true; setBusy(kind === 'preview' ? 'Checking calculations' : kind === 'draft' ? 'Saving draft' : 'Applying setup'); setError(''); setNotice('')
    try {
      if (kind === 'preview') {
        const result = await previewPayrollSetup(clientId, request)
        if (!result.ok || !result.data) throw new Error(result.error || 'Unable to preview payroll setup.')
        setPreview(result.data); setReviewed(false)
      } else if (kind === 'draft') {
        const result = await savePayrollSetupDraft(clientId, request, requestId.current)
        if (!result.ok) throw new Error(result.error || 'Unable to save setup draft.')
        setTransferReceiptId(result.data?.importId || '')
        setNotice('Transfer Batch draft saved in the existing setup receipt. Reopen this exact batch/cohort to resume. No masters or active policies were changed.')
      } else if (preview?.canApply && !preview.reviewRefreshRequired && !preview.migrationRequired && reviewed) {
        const result = await applyPayrollSetup(clientId, request, preview.planHash, requestId.current)
        if (!result.ok || !result.data) throw new Error(result.error || 'Setup could not be confirmed. Retry keeps the same request reference.')
        setApplied(result.data); setTransferReceiptId(result.data.importId); setDefaultsDraft(request.editableDefaults); setVerification([]); onApplied(result.data)
        setBusy('Refreshing saved verification context')
        const refreshed = await refreshAppliedPayrollSetupContext({ clientId, batch, rowIds: [...rowIds], requestId: requestId.current, applied: result.data }, getPayrollSetupContext)
        if (contextScope.current.version === activeScope) {
          if (refreshed.context) setContext(refreshed.context)
          setVerificationContextWarning(refreshed.warning)
        }
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
  const hasAppliedLocations = applied?.locationAllocations?.version === 1
  const locationTargets = hasAppliedLocations && applied && context ? locationPayrollVerificationTargets(clientId, batch, rowIds, context, applied) : []
  const savedTargets = applied && context && !hasAppliedLocations ? savedPayrollVerificationTargets(clientId, batch, rowIds, context, applied) : []
  const secureSavedTargets = savedTargets.filter(target => target.eligible)
  const verificationEmployeeIds = hasAppliedLocations ? locationTargets.filter(target => target.eligible).map(target => target.employeeId) : secureSavedTargets.map(target => target.employeeId!)
  const verify = async () => {
    if (!applied || busy || writePending.current || !verificationEmployeeIds.length) return
    writePending.current = true; setBusy('Verifying saved payroll'); setError('')
    try { setVerification(await verifyPayrollSetupEmployees(clientId, batch.month, verificationEmployeeIds)) }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to verify saved payroll.') }
    finally { writePending.current = false; setBusy('') }
  }
  const transferParity = applied && !hasAppliedLocations ? compareTransferPayroll(applied, verification, { clientId, saved: context?.lastImport }) : []
  const locationParity = applied && hasAppliedLocations ? compareLocationTransferPayroll(applied, verification, clientId) : []
  const verifiedRows = verification.map(item => {
    const target = secureSavedTargets.find(target => target.employeeId === item.employeeId)
    const source = target?.source
    const calculated = item.data
    return { ...item, sourceRow: source?.sourceRow, employeeName: source?.employeeName || calculated?.employee || String(item.employeeId), excelNet: source?.netPay,
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
  return <Drawer open title="Transfer Batch / Configure payroll from Excel" width="min(1160px, 98vw)" className="excel-payroll-setup" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space wrap className="excel-payroll-setup-footer">
    <Button disabled={!!busy} onClick={onClose}>Close</Button>
    {preview && context && !applied && !request?.locationAllocations && <Button disabled={!!busy} onClick={() => setCompareCurrent(true)}>Compare current saved payroll</Button>}
    {applied && <Button disabled={!!busy} loading={busy === 'Saving draft defaults'} onClick={() => void saveDefaults()}>Save draft defaults</Button>}
    {applied && <Button type="primary" disabled={!!busy || !verificationEmployeeIds.length} loading={busy === 'Verifying saved payroll'} onClick={() => void verify()}>Verify saved payroll</Button>}
    {!applied && <><Button disabled={!request || !!busy || !!schemaMissing || masterMetadataBlocked} loading={busy === 'Saving draft'} onClick={() => void run('draft')}>Save Transfer Batch draft</Button><Button disabled={!request || !!busy || masterMetadataBlocked || (!!request.payrollDaysCell && context?.sharedHeaderDivisorSupported !== true) || (request?.templateScopeReviewVersion === 1 && context?.templateApplicabilitySupported !== true)} loading={busy === 'Checking calculations'} onClick={() => void run('preview')}>Preview calculations</Button><Button type="primary" disabled={!preview?.canApply || !!preview?.reviewRefreshRequired || !!schemaMissing || !reviewed || !!busy || masterMetadataBlocked} loading={busy === 'Applying setup'} onClick={() => void run('apply')}>{preview?.transfer?.templates.every(t => t.action.startsWith('Keep current')) ? 'Record reviewed keep decisions' : 'Apply reviewed Transfer Batch'}</Button></>}
  </Space>}>
    <Alert type="info" showIcon message={`Transfer Batch request ${transferRequestId}`} description={`Saved receipt: ${transferReceiptId || applied?.importId || 'not saved yet'}. Draft → reviewed configuration Apply → saved Employee simulation. Template preparation and regular-payroll parity have separate statuses. Apply may create/update salary assignments, monthly attendance and approved period adjustments; no payroll run is created.`} />
    {error && <Alert type="error" showIcon message={error} />}
    {!applied && <Alert type="info" showIcon message="Simulate with the core payroll calculator" description="Preview calculations translates the reviewed source inputs and rules into the same core template calculator used by payroll. Compare each employee's earnings, deductions, employer costs and net before Apply. After Apply, Verify saved payroll checks the effective Employee settings and policies; an actual Regular Draft remains a separate persisted check. Unsupported formulas and missing dependencies stay visible for review." />}
    {notice && <Alert type="success" showIcon message={notice} />}
    {verificationContextWarning && <Alert type="warning" showIcon message="Setup saved; verification context unavailable" description={verificationContextWarning} />}
    {masterMetadataBlocked && <Alert type="warning" showIcon message="Persisted shared component settings are unavailable" description="Refresh the reviewed API context to load Taxable, Priority and Pay type, or choose a separate reviewed component. Preview, draft and Apply remain blocked instead of inventing shared master settings." />}
    {schemaMissing && <Alert type="warning" showIcon message="Setup draft storage needs its database migration" description={<><p>Preview is available. To save and reopen setup drafts, run this command from the project folder:</p><code>dotnet run --project .\Payroll.API\Payroll.API.csproj --launch-profile http -- --migrate-excel-payroll-setup</code></>} />}
    {busy === 'Loading setup' && <Spin tip="Loading saved source and client configuration..." />}
    {applied && !applied.employeeIds.length && !!applied.keptRows?.length && <Alert type="info" showIcon message={String(applied.keptRows.length)+' keep decisions recorded; existing assignments unchanged'} description="No source configuration was transferred. Excel parity remains unresolved. Reopen this cohort to edit and repreview." action={<Button disabled={!!busy} onClick={() => { setApplied(null); setPreview(null); setReviewed(false); setVerification([]) }}>Edit keep decision / repreview</Button>} />}
    {applied && !!applied.employeeIds.length && <Alert type="success" showIcon message={`Setup saved: ${applied.createdCount} employees created, ${applied.updatedCount} updated`} description={`Import ${applied.importId}. Run Employee simulation to check effective tax and policy rules before processing payroll. Leave, shift and holiday proposals remain saved as editable drafts.`} />}
    {!!applied?.reusedAdjustments?.length && <Alert type="info" showIcon message="Existing approved source adjustments retained" description={applied.reusedAdjustments.map(item => `${item.componentCode}: adjustment ${item.adjustmentId}, ${money(item.amount)} reused unchanged`).join('; ')} />}
    {preview?.reviewRefreshRequired && <Alert type="warning" showIcon message="Review choices changed - refresh preview before Apply" description="The displayed evidence is the last server preview. Complete your source-bound reviews, then refresh calculations. Apply stays disabled until a fresh successful preview. Changes to amounts, formulas, source or applicability clear this evidence." />}
    {preview?.transfer && request && <ExcelPayrollTransferReview key={`transfer:${preview.planHash}`} sourcePayableOverrideSupported={context?.sourcePayableOverrideSupported === true} payableOnlyAttendanceSupported={context?.payableOnlyAttendanceSupported === true} transfer={preview.transfer} request={request} disabled={!!busy || !!applied} onChange={patch} />}
    {!!applied?.dataQualityReviews?.length && <Alert type="warning" showIcon message={`${applied.dataQualityReviews.length} saved payment-data reviews remain`} description="Salary configuration preserved existing bank details and history. Bank account text still needs independent review before payment; this receipt does not certify payment readiness. No account digits were inferred or changed." />}
    {applied?.transfer && <Alert type="info" showIcon message={`Saved preparation: ${applied.transfer.preparationStatus}`} description={applied.transfer.parityStatus} />}
    {applied && hasAppliedLocations && <section aria-label="Saved monthly location identity preflight"><Alert type="warning" showIcon message={`${verificationEmployeeIds.length} of ${locationTargets.length} employee location groups eligible for current Employee simulation`} description="Every original source part, current Master identity, assignment/rules hash and saved receipt must agree. One calculation is issued per verified employee; blocked groups remain excluded. This does not approve or publish payroll." /><DataTable title="Saved employee location-group preflight" rows={locationTargets} getRowId={row => row.employeeId} exportDisabled columns={[
      { key: 'employeeId', label: 'Employee Master', width: 130 }, { key: 'sourceRows', label: 'Original source rows', width: 180, value: row => row.sourceRows.join(', ') }, { key: 'eligible', label: 'Calculation scope', value: row => row.eligible ? 'Eligible - complete saved group' : 'Blocked' }, { key: 'reasons', label: 'Required review', width: 420, wrap: true, value: row => row.reasons.join(' ') || 'Source parts, employee and versioned assignment receipts agree.' },
    ]} /></section>}
    {hasAppliedLocations && verification.length > 0 && <section aria-label="Saved monthly location payroll comparison"><Alert type="info" showIcon message="Actual Employee calculation versus all retained location parts" description="Monthly component, employer and attendance totals are compared through versioned source/assignment receipts. Explained rounding remains separate from a real mismatch; final payroll approval remains with the owner." />{locationParity.map(row => <details key={row.employeeId} open><summary>Employee {row.employeeId} - {row.status}</summary>{row.reasons.map(reason => <p key={reason}>{reason}</p>)}<p>Source net raw {payrollRawAmount(row.sourceNetRaw)}; source part displays {money(row.sourceNetDisplay)}; current payroll {money(row.actualNet)}; raw delta {payrollRawDifference(row.rawNetDelta)}; display delta {payrollRawDifference(row.displayNetDelta)}.</p><DataTable title="Monthly source parts and actual components" rows={row.lines} getRowId={line => line.code} hideSearch exportDisabled columns={[
      { key: 'code', label: 'Component', width: 150 }, { key: 'sourceRaw', label: 'Source raw total', render: line => payrollRawAmount(line.sourceRaw) }, { key: 'sourceDisplay', label: 'Source part displays', render: line => money(line.sourceDisplay) }, { key: 'actual', label: 'Actual Employee', render: line => money(line.actual) }, { key: 'rawDelta', label: 'Raw delta', render: line => payrollRawDifference(line.rawDelta) }, { key: 'displayDelta', label: 'Display delta', render: line => payrollRawDifference(line.displayDelta) }, { key: 'status', label: 'Comparison' }, { key: 'roundingBasis', label: 'Rounding evidence', width: 320, wrap: true },
    ]} /><ExcelPayrollEmployeeObservation employeeId={row.employeeId} observation={verification.filter(item => item.employeeId === row.employeeId).length === 1 ? verification.find(item => item.employeeId === row.employeeId) : undefined} /></details>)}</section>}
    {applied && !hasAppliedLocations && <section aria-label="Saved payroll identity preflight"><Alert type="warning" showIcon message={`${secureSavedTargets.length} of ${savedTargets.length} saved source rows eligible for current Employee simulation`} description="Current source employment link, master identity and saved receipt must agree. Blocked rows are excluded; no employee or attendance record is changed. This is a calculation target check, not a parity certificate." /><DataTable title="Saved source identity preflight" rows={savedTargets} getRowId={row => row.rowId} columns={[
      { key: 'sourceRow', label: 'Source row', width: 95 }, { key: 'employeeCode', label: 'Source code', width: 160 }, { key: 'eligible', label: 'Calculation scope', value: row => row.eligible ? 'Eligible — current identity verified' : 'Blocked' }, { key: 'reasons', label: 'Reason', width: 420, wrap: true, value: row => row.reasons.join(' ') || 'Current source, setup, receipt and master agree.' },
    ]} /></section>}
    {!!verification.length && applied?.transfer && !hasAppliedLocations && <section aria-label="Transfer Batch regular payroll reconciliation"><Alert type="info" showIcon message="Current saved Employee simulation observations - not a persisted parity certificate" description="Every captured component, employer amount, source-month divisor, payable days and net must match. Actual worked days are compared only when mapped. Explained rounding requires the acknowledged source-bound proof; the durable configuration receipt is retained unchanged." />{transferParity.length ? transferParity.map(row => <details key={row.rowId}><summary>Employee {row.employeeId}: {row.status}</summary>{row.reasons.map(reason => <p key={reason}>{reason}</p>)}{row.notes.map(note => <p key={note}>{note}</p>)}<p>Raw net difference (payroll - source): {payrollRawDifference(row.netVariance)}.</p>{row.lines.map(line => <p key={line.code}>{line.code}: Excel {money(line.sourceAmount)} (raw {payrollRawAmount(line.sourceAmount)}) / payroll {money(line.actual)} / raw difference {payrollRawDifference(line.variance)}</p>)}<ExcelPayrollEmployeeObservation employeeId={row.employeeId} observation={verification.filter(item => item.employeeId === row.employeeId).length === 1 ? verification.find(item => item.employeeId === row.employeeId) : undefined} /></details>) : <Alert type="warning" message="This old setup has no durable source-row/component receipt. Reconciliation is unverified." />}</section>}
    {!!verification.length && !applied?.transfer && <div className="excel-payroll-setup-preview"><Alert type="warning" showIcon message="Current Employee calculations — legacy source parity remains unverified" description="Results are paired by verified employee identity, never array order. This legacy receipt has no durable source-component classification or reviewed worked-days proof. A net match alone does not certify payroll parity; no payroll run or email is created." /><DataTable title="Saved payroll verification" rows={verifiedRows} getRowId={row => row.employeeId} exportFileName={`salary-setup-verification-${batch.month}`} columns={[
      { key: 'sourceRow', label: 'Source row', width: 95 }, { key: 'employeeName', label: 'Employee', width: 220 }, { key: 'payrollDays', label: 'Divisor', width: 85 }, { key: 'payableDays', label: 'Payable days', width: 110 },
      { key: 'excelNet', label: 'Excel in hand', render: row => money(row.excelNet) }, { key: 'net', label: 'Payroll in hand', render: row => money(row.net) }, { key: 'difference', label: 'Difference', render: row => money(row.difference) },
      { key: 'deductions', label: 'Calculated deductions', width: 250, wrap: true }, { key: 'status', label: 'Review', width: 220, wrap: true, value: row => !row.ok || !row.data?.complete ? row.error || 'Calculation incomplete; inspect issues' : 'Calculated; source component/attendance semantics require review' },
    ]} />{verifiedRows.map(row => <details key={row.employeeId}><summary>Source row {row.sourceRow}: current Employee component and attendance observations</summary>{row.data?.inputs.filter(input => ['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS'].includes(input.name)).map(input => <p key={input.name}>{input.name}: {payrollRawAmount(input.value)} — {input.source}</p>)}{row.data?.lines.map((line, index) => <p key={`${line.code}:${index}`}>{line.code} / {line.category}: {money(line.earned)}; raw {payrollRawAmount(line.raw)}; formula {line.formula || 'not supplied'}; rounding {line.rounding || 'not supplied'}</p>)}<p>Gross {money(row.data?.gross)}; deductions {money(row.data?.deductions)}; employer {money(row.data?.benefits)}; net {money(row.net)}. Source component-code mapping and worked/payable interpretation remain unverified.</p></details>)}</div>}
    {context?.defaults && <Alert type="info" showIcon message={context.defaults.attendanceSource} description="Defaults below come from this client or the monthly Excel workflow. Source values and reviewed choices take priority; existing employee details and tax settings stay unchanged." />}
    {request && context && <Form component="div" layout="vertical" disabled={!!busy || !!applied}>
      <Space wrap><Tag color="blue">{batch.clientName || `Client ${clientId}`}</Tag><Tag>{batch.month}</Tag><Tag>{selected.length} selected source rows</Tag></Space>
      <Alert type="info" showIcon message="Review the source, employee matches and formulas before creating payroll configuration." description="Apply saves client templates, employee assignments and this month's attendance. Missing components are created only as reviewed below. Leave entitlements and holiday dates stay inactive in the saved setup draft until approved." />
      {!!request.payrollDaysCell && context.sharedHeaderDivisorSupported !== true && <Alert type="warning" showIcon message="Running API has not advertised shared-header divisor binding" description="The reviewed source cell is shown, but this API build cannot validate that new mapping contract. The owner must start the reviewed API build before shared-header preview/apply. No fallback or source rewrite is used." />}
      {request.templateScopeReviewVersion === 1 && context.templateApplicabilitySupported !== true && <Alert type="warning" showIcon message="Running API has not advertised reviewed template applicability" description="The owner must start the reviewed API build before transferring scoped rules. No legacy fallback or global template overwrite is used." />}
      <Sections defaultActiveKey={['basis', 'employees', 'components']} items={[
        { key: 'location-allocations', label: 'Monthly location allocations from reviewed Settings', children: <ExcelPayrollLocationAllocations batch={batch} context={context} request={request} preview={preview} disabled={!!busy || !!applied} onChange={patch} /> },
        { key: 'basis', label: 'Salary month and attendance', children: <div className="excel-payroll-setup-grid">
          <Form.Item label="Previously applied salary rules" className="excel-payroll-setup-wide" extra={context.salaryCorrectionSupported === true ? 'One existing employee only. Previous receipts/history, recorded attendance and shared masters are retained; existing source adjustments are checked for reuse.' : 'The updated API must be running before a salary-only correction can be reviewed.'}>
            <Checkbox disabled={!!busy || selected.length !== 1 || context.salaryCorrectionSupported !== true} checked={request.correctionOfImportId !== undefined} onChange={event => startSalaryCorrection(event.target.checked)}>Correct a previously applied salary setup</Checkbox>
            {request.correctionOfImportId !== undefined && <Input aria-label="Original Applied receipt for salary correction" placeholder="Exact prior Applied receipt ID" value={request.correctionOfImportId} onChange={event => patch({ correctionOfImportId: event.target.value.trim() })} />}
          </Form.Item>
          {request.locationAllocations ? <Form.Item label="Salary templates"><Tag>Existing Settings template selected for each source part</Tag></Form.Item> : field('Template name', 'templateName')}{request.locationAllocations ? <Form.Item label="Configuration import / effective date"><Tag>{request.locationAllocations.configurationEffectiveFrom || 'Unavailable'}</Tag></Form.Item> : field('Effective from', 'effectiveFrom', 'date')}
          <Form.Item label="Payroll divisor source"><Select aria-label="Payroll divisor source" value={divisorMode || (request.fixedPayrollDays == null ? 'column' : 'fixed')} options={[{ value: 'column', label: 'Excel column (per employee)' }, { value: 'fixed', label: 'Fixed days (e.g. 26)' }]} onChange={value => { setDivisorMode(value === 'fixed' ? 'fixed' : 'column'); patch({ fixedPayrollDays: undefined, payrollDaysColumnIndex: -1, payrollDaysCell: undefined }) }} /></Form.Item>
          {(divisorMode || (request.fixedPayrollDays == null ? 'column' : 'fixed')) === 'column' ? <Form.Item label="Payroll divisor column"><Select aria-label="Payroll divisor column" value={request.payrollDaysColumnIndex < 0 ? undefined : request.payrollDaysColumnIndex} options={columnOptions} onChange={value => patch({ payrollDaysColumnIndex: value })} /></Form.Item> : <Form.Item label="Reviewed fixed divisor"><InputNumber aria-label="Reviewed fixed payroll days" min={1} max={31} value={request.fixedPayrollDays} onChange={value => patch({ fixedPayrollDays: value ?? undefined })} /></Form.Item>}
          {request.fixedPayrollDays != null && <Form.Item label="Shared captured header divisor" extra="Optional source cell such as L4. Its numeric literal and selected earning formulas are rechecked server-side; no original batch is edited."><Input aria-label="Reviewed payroll divisor source cell" value={request.payrollDaysCell || ''} onChange={event => patch({ payrollDaysCell: event.target.value.toUpperCase() })} /></Form.Item>}
          <Form.Item label="Payable days column"><Select aria-label="Payable days column" value={request.payableDaysColumnIndex} options={columnOptions} onChange={value => patch({ payableDaysColumnIndex: value })} /></Form.Item>
          <Form.Item label="Attendance import" extra="Turn off to keep regular daily attendance and weekly-off rules. Source days are still used for this comparison."><Checkbox checked={request.importAttendance !== false} onChange={event => patch({ importAttendance: event.target.checked, overrideSourcePayableAttendance: false, ...(!event.target.checked ? { importPayableOnlyAttendance: false } : {}) })}>Import source-month attendance</Checkbox></Form.Item>
          <Form.Item label="Work week"><Select aria-label="Payroll setup work week" value={request.workWeek || undefined} options={workWeekOptions} onChange={value => patch({ workWeek: value })} /></Form.Item>
          <Form.Item label="Work location column"><Select aria-label="Work location column" allowClear value={request.sourceWorkLocationColumnIndex} options={columnOptions} onChange={value => patch({ sourceWorkLocationColumnIndex: value })} /></Form.Item>
          <Form.Item label="Attendance cycle start day"><InputNumber aria-label="Attendance cycle start day" min={1} max={31} value={request.attendanceCycleStartDay} onChange={value => patch({ attendanceCycleStartDay: value || 1 })} /></Form.Item>
          <Form.Item label="Attendance cycle end day"><InputNumber aria-label="Attendance cycle end day" min={1} max={31} value={request.attendanceCycleEndDay} onChange={value => patch({ attendanceCycleEndDay: value || 30 })} /></Form.Item>
          <Form.Item label="Report generation day"><InputNumber aria-label="Report generation day" min={1} max={31} value={request.payrollReportGenerationDay} onChange={value => patch({ payrollReportGenerationDay: value || 1 })} /></Form.Item>
          <Form.Item label="State for new work locations"><Input aria-label="State for new work locations" value={request.defaultWorkLocationState || ''} placeholder="Review when creating a source location" onChange={event => patch({ defaultWorkLocationState: event.target.value })} /></Form.Item><Form.Item label="New work locations"><Checkbox checked={request.createMissingWorkLocations === true} onChange={event => patch({ createMissingWorkLocations: event.target.checked })}>Create reviewed source locations for this client</Checkbox></Form.Item>
        </div> },
        { key: 'employees', label: 'Employee Master matches', children: request.locationAllocations ? <Alert type="info" showIcon message="One existing Employee Master is reviewed for each monthly location group above" description="The per-source-row employee/create controls are inactive in this optional path. Source identity checks still run before Apply." /> : <><DataTable title="Selected employees" rows={selected} getRowId={row => row.id} exportDisabled hideSearch columns={[
          { key: 'sourceRow', label: 'Excel row', width: 85 }, { key: 'employeeName', label: 'Employee', width: 210 }, { key: 'location', label: 'Source location', value: row => request.sourceWorkLocationColumnIndex === undefined ? '' : context.source.rows.find(item => item.sourceRow === row.sourceRow)?.cells[request.sourceWorkLocationColumnIndex]?.value || '', width: 160 },
          { key: 'match', label: 'Employee Master record', width: 330, sortable: false, filterable: false, render: row => <Select aria-label={`Employee match for row ${row.sourceRow}`} showSearch optionFilterProp="label" allowClear value={request.employeeDecisions.find(item => item.rowId === row.id)?.matchedEmployeeId} options={context.employees.map(employee => ({ value: employee.id, label: `${employee.employeeCode} · ${employee.employeeName} · ${employee.workLocation || 'Location not set'}` }))} onChange={value => patch({ employeeDecisions: request.employeeDecisions.map(item => item.rowId === row.id ? { ...item, matchedEmployeeId: value, allowCreate: false } : item) })} /> },
          { key: 'create', label: 'Create if unmatched', width: 165, sortable: false, filterable: false, render: row => { const decision = request.employeeDecisions.find(item => item.rowId === row.id); return <Checkbox aria-label={`Create employee for row ${row.sourceRow}`} disabled={!!decision?.matchedEmployeeId || !!busy || !!applied} checked={decision?.allowCreate === true} onChange={event => patch({ employeeDecisions: request.employeeDecisions.map(item => item.rowId === row.id ? { ...item, allowCreate: event.target.checked } : item) })}>Reviewed</Checkbox> } },
        ]} /><p>Existing employees keep their identity and employee code. A new employee requires an explicit creation choice; ambiguous matches block Apply.</p><div className="excel-payroll-setup-grid">{field('Default department', 'defaultDepartment')}{field('Default designation', 'defaultDesignation')}{field('Default grade', 'defaultGrade')}<Form.Item label="Default work location"><Select aria-label="Default work location" allowClear value={request.defaultWorkLocationId} options={context.workLocations.map(location => ({ value: location.id, label: location.name }))} onChange={value => patch({ defaultWorkLocationId: value })} /></Form.Item></div></> },
        { key: 'applicability', label: 'Template applicability and effective period', children: request.locationAllocations ? <Alert type="info" showIcon message="Existing Settings applicability is reviewed per monthly location part above" description="This path uses the server import/effective date and the saved payroll period separately. It does not create a cohort-wide historical transfer date or replace other Settings variants." /> : <ExcelPayrollTemplateApplicability request={request} locations={context.workLocations} disabled={!!busy || !!applied} onChange={patch} /> },
        { key: 'identity-columns', label: 'UAN source-column meaning', children: <ExcelPayrollIdentityColumnReview context={context} request={request} disabled={!!busy || !!applied} onChange={patch} /> },
        { key: 'source-evidence', label: 'Saved batch financial source evidence', children: <ExcelPayrollSourceEvidence batch={batch} source={context.source} request={request} /> },
        { key: 'columns', label: 'Payroll column mapping (independent of payslip layout)', children: <><p>Include base/earned components even when hidden on the payslip. Keep totals and billing fields out of earning/deduction components. Changing a column regenerates proposed components; review any formula edits again.</p><DataTable rows={context.source.columns} getRowId={column => column.columnIndex} exportDisabled title="Authoritative salary worksheet" columns={[
          { key: 'column', label: 'Column', value: column => excelColumnLetter(column.columnIndex) }, { key: 'sourceHeader', label: 'Excel heading' },
          { key: 'role', label: 'Payroll use', sortable: false, filterable: false, render: column => <Select aria-label={`Payroll column ${excelColumnLetter(column.columnIndex)}`} value={request.sourceColumnRoles?.[column.columnIndex] || column.kind} options={payslipColumnKinds} onChange={value => classifyColumn(column.columnIndex, value)} /> },
        ]} /></> },
        { key: 'components', label: `Salary components and formulas (${request.components.length})`, children: <><ExcelPayrollSourceInputReview batch={batch} source={context.source} request={request} /><Button onClick={addComponent}>Add payroll component</Button>{notes.length > 0 && <Alert type="warning" showIcon message="Source formula review" description={<ul>{notes.map((note, index) => <li key={index}>{note}</li>)}</ul>} />}<Sections items={request.components.map(component => ({ key: component.key, label: <Space wrap><strong>{component.code}</strong><span>{component.name}</span><Tag color={component.existingComponentId ? 'default' : 'orange'}>{component.existingComponentId ? 'Reuse master' : 'Create master'}</Tag><Tag>{component.category}</Tag>{component.isPeriodAdjustment && <Tag color="blue">This month only</Tag>}</Space>, children: <div className="excel-payroll-setup-grid">
          <div className="excel-payroll-setup-wide"><Button danger onClick={() => patch({ components: request.components.filter(item => item.key !== component.key) })}>Remove this payroll component</Button></div>
          <Form.Item label="Component master"><Select aria-label={`Component master ${component.code}`} allowClear showSearch optionFilterProp="label" value={component.existingComponentId} options={context.components.map(master => ({ value: master.id, label: `${master.code} · ${master.name} (${master.category})` }))} placeholder="Create reviewed component" onChange={value => { const master = context.components.find(item => item.id === value); patchComponent(component.key, selectExcelPayrollMaster(component, master)) }} /></Form.Item>
          {persistedMasterMetadata(component)}
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
          <Space wrap className="excel-payroll-setup-wide"><Checkbox checked={component.proRata} onChange={event => patchComponent(component.key, { proRata: event.target.checked })}>Prorate using payable days</Checkbox><Checkbox checked={component.blankInputAsZero === true} onChange={event => patchComponent(component.key, { blankInputAsZero: event.target.checked })}>Treat blank source inputs as zero (reviewed)</Checkbox><Checkbox checked={component.exemptWhenSourceZero} onChange={event => patchComponent(component.key, { exemptWhenSourceZero: event.target.checked })}>Preserve explicit source zero exemptions</Checkbox><Checkbox disabled={!!busy || !!applied || component.category === 'Benefit'} checked={component.isPeriodAdjustment} onChange={event => patchComponent(component.key, { isPeriodAdjustment: event.target.checked, ...(component.existingComponentId ? {} : { payType: event.target.checked ? 'Variable Pay' : 'Fixed Pay' }), calculationType: event.target.checked ? 'Manual / Variable' : 'Fixed Amount', sourceColumnIndex: component.sourceColumnIndex ?? component.expectedColumnIndex, proRata: !event.target.checked, formula: '', rowFormulas: {} })}>This month only</Checkbox></Space>
        </div> }))} /></> },
        { key: 'master-defaults', label: 'Client defaults and master creation', children: <Space direction="vertical"><Checkbox disabled={!!busy || !!applied} checked={request.createMissingDropdownValues !== false} onChange={event => patch({ createMissingDropdownValues: event.target.checked })}>Create reviewed missing department, designation, grade and work-week values for this client</Checkbox><Checkbox disabled={!!busy || !!applied} checked={request.preserveExistingAttendancePolicies !== false} onChange={event => patch({ preserveExistingAttendancePolicies: event.target.checked })}>Preserve existing attendance policy memberships</Checkbox></Space> },
        { key: 'defaults', label: 'Saved leave, holiday and shift drafts', children: <><Alert type="info" showIcon message="These proposals are saved with this setup, including after Apply. They do not create active leave balances, holidays or shifts." /><div className="excel-payroll-setup-grid"><Form.Item label="Proposed yearly paid leave (inactive)"><InputNumber disabled={!!busy} aria-label="Proposed yearly paid leave" min={0} step={0.5} value={Number(object(defaults.leave).proposedAnnualPaidLeave || 0)} onChange={value => patchDefault('leave', { proposedAnnualPaidLeave: value ?? 0, status: 'Draft', active: false, quotaApproved: false })} /></Form.Item><Form.Item label="Holiday calendar notes"><Input.TextArea disabled={!!busy} aria-label="Holiday calendar notes" value={String(object(defaults.holiday).notes || '')} onChange={event => patchDefault('holiday', { notes: event.target.value, status: 'Draft' })} /></Form.Item><Form.Item label="Shift setup notes"><Input.TextArea disabled={!!busy} aria-label="Shift setup notes" value={String(object(defaults.shift).notes || '')} onChange={event => patchDefault('shift', { notes: event.target.value, status: 'Draft' })} /></Form.Item></div></> },
      ]} />
    </Form>}
    {compareCurrent && preview && context && <ExcelPayrollCurrentComparison key={`current:${preview.planHash}`} clientId={clientId} batch={batch} context={context} preview={preview} onClose={() => setCompareCurrent(false)} />}
    {preview && <div className="excel-payroll-setup-preview"><Alert type="info" showIcon message="Calculation coverage" description={preview.coverage} /><Alert type="info" showIcon message="Payslip display and raw calculation are separate" description="Comparison amounts use the existing standalone payslip formatter at 2 decimal places: 15 significant digits, then display rounding. Raw snapshot and Raw Δ remain visible; raw comparison precision is unchanged. A separately reviewed server proof may explain deterministic currency-rounding stages; other transfer blockers remain. Same displayed net does not certify component/Employee parity or establish the precision of a previously sent PDF. Display rounding is not a payroll policy." />{preview.issues.map((issue, index) => <Alert key={index} showIcon type={issue.severity === 'Error' ? 'error' : 'warning'} message={[issue.code, issue.message].filter(Boolean).join(': ')} />)}<DataTable title="Source comparison" rows={preview.rows} getRowId={row => row.rowId} exportDisabled columns={[
      { key: 'employeeName', label: 'Employee', width: 220 }, { key: 'simulation', label: 'Test inputs', width: 185, render: row => !request?.locationAllocations && <ExcelPayrollWhatIfSimulation key={`${preview.planHash}:${row.rowId}`} clientId={clientId} row={row} disabled={!!busy} /> }, { key: 'action', label: 'Master action', width: 100 }, { key: 'payrollDays', label: 'Divisor', width: 85 }, { key: 'payableDays', label: 'Payable days', width: 110 }, { key: 'gross', label: 'Gross', render: row => money(row.gross) }, { key: 'deductions', label: 'Deductions', render: row => money(row.deductions) }, { key: 'net', label: 'Calculated in hand', render: row => money(row.net) }, { key: 'sourceNet', label: 'Payslip in hand (2dp)', width: 200, wrap: true, render: row => sourceMoney(batch.rows.find(item => item.id === row.rowId)?.netPay) }, { key: 'variance', label: 'Raw Δ (calculated − source)', width: 180, render: row => payrollRawDifference(payrollDisplayEvidence(batch.rows.find(item => item.id === row.rowId)?.netPay, row.net).rawDifference) }, { key: 'displayMatch', label: 'Display comparison', width: 210, render: row => payrollDisplayEvidence(batch.rows.find(item => item.id === row.rowId)?.netPay, row.net).displayStatus },
    ]} /><Sections items={preview.rows.map(row => ({ key: row.rowId, label: `${row.employeeName} · component comparison`, children: <DataTable rows={row.lines} getRowId={line => line.code} exportDisabled hideSearch columns={[{ key: 'code', label: 'Component' }, { key: 'sourceAmount', label: 'Payslip display (2dp)', width: 190, wrap: true, render: line => sourceMoney(line.sourceAmount) }, { key: 'calculatedAmount', label: 'Calculated (2dp)', width: 150, wrap: true, render: line => money(line.calculatedAmount) }, { key: 'variance', label: 'Raw Δ', width: 160, wrap: true, render: line => payrollRawDifference(line.variance) }, { key: 'displayMatch', label: 'Display comparison', width: 210, wrap: true, render: line => payrollDisplayEvidence(line.sourceAmount, line.calculatedAmount).displayStatus }]} /> }))} />{!applied && <Checkbox aria-label="Review payroll setup" disabled={!preview.canApply || !!busy} checked={reviewed} onChange={event => setReviewed(event.target.checked)}>I reviewed employee matches, new masters, formulas, source blanks and comparison results for these {selected.length} employees.</Checkbox>}</div>}
  </Drawer>
}
