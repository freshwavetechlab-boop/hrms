import { useMemo, useRef, useState } from 'react'
import { Alert, Button, Collapse, Drawer, Space } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { verifyPayrollSetupEmployees, type PayrollSetupContext, type PayrollSetupPreview, type PayrollSetupVerification } from '../services/excelPayrollSetupService'
import { compareCurrentPayroll, currentPayrollSelection, currentPayrollTargets } from '../utils/excelPayrollCurrentComparison'
import { payrollComparisonMoney as money, payrollDisplayEvidence, payrollRawAmount, payrollRawDifference } from '../utils/payrollRoundingEvidence'
import DataTable from './DataTable'

type Props = { clientId: number; batch: ExcelPayslipBatch; context: PayrollSetupContext; preview: PayrollSetupPreview; onClose: () => void }
export default function ExcelPayrollCurrentComparison({ clientId, batch, context, preview, onClose }: Props) {
  const targets = useMemo(() => currentPayrollTargets(clientId, batch, context, preview), [clientId, batch, context, preview])
  const selection = useMemo(() => currentPayrollSelection(batch, preview), [batch, preview])
  const [observations, setObservations] = useState<PayrollSetupVerification[]>([]), [ran, setRan] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const pending = useRef(false)
  const supported = context.calculationOnlySimulationSupported === true
  const eligible = targets.filter(target => target.eligible)
  const rows = ran ? compareCurrentPayroll(targets, preview, observations).filter(row => row.eligible) : []
  const run = async () => {
    if (pending.current || !eligible.length || !supported) return
    pending.current = true; setBusy(true); setError(''); setRan(false); setObservations([])
    try { setObservations(await verifyPayrollSetupEmployees(clientId, batch.month, eligible.map(target => target.employeeId!))); setRan(true) }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Current payroll comparison unavailable.') }
    finally { pending.current = false; setBusy(false) }
  }
  return <Drawer open title="Compare current saved Employee payroll" width="min(1160px, 98vw)" className="excel-payroll-setup" onClose={() => { if (!pending.current) onClose() }} closable={!busy} keyboard={!busy} maskClosable={!busy} footer={<Space wrap><Button disabled={busy} onClick={onClose}>Close comparison</Button><Button type="primary" aria-label={`Calculate current saved payroll (${eligible.length})`} aria-busy={busy} loading={busy} disabled={busy || !eligible.length || !supported} onClick={() => void run()}>Calculate current saved payroll ({eligible.length})</Button></Space>}>
    {!supported && <Alert type="error" showIcon message="Updated calculation-only API required" description="Restart the reviewed API build to enforce workflow/notification exclusion on simulation endpoints. No calculation request has been sent by this action." />}
    <Alert type="info" showIcon message={`${batch.clientName || `Client ${clientId}`} · ${batch.month} · ${selection.selectedCount === null ? 'Selected source scope unavailable' : `${selection.selectedCount} selected source rows`}`} description={`Saved batch ${batch.id}; source snapshot ${preview.transfer?.sourceHash || 'not recorded'}; comparison plan ${preview.planHash}. Reads current effective salary assignments, attendance and policies through Employee simulation. No input overrides, Apply, payroll creation or messages. Routine request auditing may occur. Results are current observations, not a saved parity certificate.`} />
    <Alert type="warning" showIcon message={`${eligible.length} eligible · ${targets.length - eligible.length} blocked identity rows${selection.blocked.length ? ` · ${selection.blocked.length} blocked source previews` : ''}`} description="Overview and setup identity checks must agree. Missing identities are not created. Source mapped days and raw/component differences require review; matching displayed net alone does not prove payroll parity." />
    {selection.selectedCount === null && <Alert type="warning" message="Selected source scope unavailable" description="The legacy preview has no calculated rows or selected-source metadata. Refresh the saved-batch preview; this does not mean zero employees." />}
    {!!selection.blocked.length && <DataTable title="Source configuration prerequisites" rows={selection.blocked} getRowId={row => row.rowId} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row', render: row => row.sourceRow ?? 'Unknown' }, { key: 'reasons', label: 'Required source review', wrap: true }]} />}
    {!!targets.filter(target => !target.eligible).length && <DataTable title="Identity prerequisites" rows={targets.filter(target => !target.eligible)} getRowId={row => row.rowId} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeCode', label: 'Employee code' }, { key: 'reasons', label: 'Required review', wrap: true, value: row => row.reasons.join(' ') }]} />}
    {error && <Alert type="error" showIcon message={error} />}
    {ran && <>
      <DataTable title="Current payroll comparison" rows={rows} getRowId={row => row.rowId} exportDisabled columns={[
        { key: 'sourceRow', label: 'Source row', width: 95 }, { key: 'employeeCode', label: 'Employee code', width: 130 }, { key: 'status', label: 'Status', width: 180 },
        { key: 'sourceNet', label: 'Source net (2dp)', width: 180, render: row => <span>{money(row.sourceNet)}<small style={{ display: 'block' }}>Raw: {payrollRawAmount(row.sourceNet)}</small></span> },
        { key: 'actualNet', label: 'Current payroll net (2dp)', width: 180, render: row => money(row.actualNet) }, { key: 'netDelta', label: 'Raw net delta', width: 150, render: row => payrollRawDifference(row.netDelta) },
        { key: 'display', label: 'Displayed net', width: 200, value: row => payrollDisplayEvidence(row.sourceNet, row.actualNet).displayStatus },
        { key: 'reasons', label: 'Reasons', width: 300, wrap: true, value: row => row.reasons.join(' ') },
      ]} />
      <Collapse>{rows.map(row => <Collapse.Panel key={row.rowId} header={`Source row ${row.sourceRow} · ${row.employeeCode} · current components and inputs`}>
        <p>Assigned template: {row.data?.templateId || 'Unavailable'}. {row.data?.coverage}</p>
        <p>Current gross {money(row.data?.gross)} · deductions {money(row.data?.deductions)} · employer benefits {money(row.data?.benefits)} · net {money(row.actualNet)}</p>
        {row.data?.attendanceEvidence ? <section aria-label="Native attendance input source">
          <Alert type={row.data.attendanceEvidence.policyValidationError || row.data.attendanceEvidence.sourceKind === 'Missing native attendance' ? 'warning' : 'info'} showIcon message={`Native attendance source: ${row.data.attendanceEvidence.sourceKind}`} description={<>
            <p>{row.data.attendanceEvidence.explanation}</p>
            <p>Period {row.data.attendanceEvidence.payPeriod}; resolved working {row.data.attendanceEvidence.workingDays ?? 'Unknown'}, present {row.data.attendanceEvidence.presentDays ?? 'Unknown'}, payable {row.data.attendanceEvidence.payableDays ?? 'Unknown'}.</p>
            <p>Weekly-off payable units: {row.data.attendanceEvidence.weeklyOffPayableUnits ?? 'Unknown / not loaded'}; holiday payable units: {row.data.attendanceEvidence.holidayPayableUnits ?? 'Unknown / not loaded'}. Dated cycle: {row.data.attendanceEvidence.cycleStart && row.data.attendanceEvidence.cycleEnd ? `${row.data.attendanceEvidence.cycleStart} through ${row.data.attendanceEvidence.cycleEnd}` : 'Not established by this input branch'}.</p>
            {row.data.attendanceEvidence.temporaryDayOverrides && <p>Temporary day overrides were supplied. The totals above describe the native inputs before those overrides.</p>}
            {row.data.attendanceEvidence.policyValidationError && <p>{row.data.attendanceEvidence.policyValidationError}</p>}
          </>} />
        </section> : <Alert type="warning" message="Native attendance source evidence unavailable" description="Refresh against the updated API. Equal day totals alone do not establish a monthly/daily source or CL allocation." />}
        <DataTable title="Source component totals versus current payroll" rows={row.totals} getRowId={item => item.field} exportDisabled hideSearch columns={[{ key: 'field', label: 'Total' }, { key: 'source', label: 'Source raw', render: item => payrollRawAmount(item.source) }, { key: 'actual', label: 'Current payroll', render: item => money(item.actual) }, { key: 'delta', label: 'Raw delta', render: item => payrollRawDifference(item.delta) }]} />
        <DataTable title="Saved attendance versus source-mapped inputs" rows={row.days} getRowId={item => item.token} exportDisabled hideSearch columns={[{ key: 'token', label: 'Input' }, { key: 'source', label: 'Source mapped', render: item => item.source === null ? 'Unverified' : String(item.source) }, { key: 'sourceEvidence', label: 'Source evidence / required review', wrap: true }, { key: 'calculationValue', label: 'Draft calculation input' }, { key: 'actual', label: 'Current saved', render: item => item.actual === null ? 'Missing' : String(item.actual) }, { key: 'origin', label: 'Current input source', wrap: true }]} />
        <DataTable title="Current component evidence" rows={row.lines.map((line, i) => ({ ...line, key: `${line.code}:${i}` }))} getRowId={line => line.key} exportDisabled hideSearch columns={[
          { key: 'code', label: 'Component' }, { key: 'category', label: 'Current type' }, { key: 'source', label: 'Source amount', render: line => payrollRawAmount(line.source) }, { key: 'actual', label: 'Current earned', render: line => money(line.actual) }, { key: 'delta', label: 'Raw delta', render: line => payrollRawDifference(line.delta) }, { key: 'formula', label: 'Current formula', wrap: true }, { key: 'raw', label: 'Before rounding', render: line => payrollRawAmount(line.raw) }, { key: 'rounding', label: 'Engine rounding', wrap: true },
        ]} />
      </Collapse.Panel>)}</Collapse>
    </>}
  </Drawer>
}
