import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, Progress, Space, Spin, Tag } from 'antd'
import { useAuthSession } from './AuthGate'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { applyPayrollSetup, getPayrollSetupContext, previewPayrollSetup, type PayrollSetupContext, type PayrollSetupResult } from '../services/excelPayrollSetupService'
import { completedQuickPayroll, quickAttendanceConflict, quickPayrollGroups, quickPayrollRequest, sameQuickPayrollPreview, selectQuickPayrollTemplates, type QuickPayrollPlan } from '../utils/excelPayrollQuickPrepare'
import DataTable from './DataTable'
import { formatExcelPayslipAmount } from '../utils/excelPayslipImport'

type Props = { clientId: number; batch: ExcelPayslipBatch; rowIds: string[]; context: PayrollSetupContext; onClose: () => void; onAdvanced: (rowIds: string[]) => void; onApplied: (result: PayrollSetupResult) => void }
type Attempt = { rowIds: string[]; requestId: string; sourceHash: string; uncertain: boolean }

export default function ExcelPayrollQuickPrepare({ clientId, batch, rowIds, context, onClose, onAdvanced, onApplied }: Props) {
  const session = useAuthSession()
  const [attempts, setAttempts] = useSessionPreference<Attempt[]>(`excel-payroll.prepare:${session?.user.email || 'local'}:${clientId}:${batch.id}`, [])
  const [plans, setPlans] = useState<QuickPayrollPlan[]>([]), [error, setError] = useState(''), [busy, setBusy] = useState('')
  const [includeNew, setIncludeNew] = useState(false)
  const [importAttendance, setImportAttendance] = useState(context.clientConfiguration?.savedRequest.importAttendance === true)
  const [previewed, setPreviewed] = useState(false)
  const flight = useRef(false), active = useRef(true)
  const selection = rowIds.join('|')
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  const selected = context.roster?.rows.filter(row => rowIds.includes(row.rowId)) || []
  const newCount = selected.filter(row => row.status === 'New').length
  const done = plans.filter(plan => plan.completed).reduce((sum, plan) => sum + plan.rowIds.length, 0)
  const pending = plans.filter(plan => !plan.completed)
  const ready = previewed && plans.length > 0 && pending.every(plan => !!plan.request && !plan.error && !plan.uncertain && plan.preview.canApply)
  const canManage = !!session?.user.permissions && ['settings.manage', 'employees.manage', 'payroll.run'].every(p => session.user.permissions.includes(p))
  const remember = (attempt: Attempt) => setAttempts(current => [...current.filter(item => item.rowIds.join('|') !== attempt.rowIds.join('|')), attempt])
  const preview = async () => {
    if (flight.current || !canManage) return
    flight.current = true; setBusy('Preparing combined preview'); setError(''); setPreviewed(false)
    const next: QuickPayrollPlan[] = []
    try {
      if (!context.clientConfiguration) throw Error('Configure this client once in Advanced setup, or review its changed Excel layout. Later compatible imports will reuse the saved setup.')
      const groups = quickPayrollGroups(context, rowIds)
      for (const ids of groups) {
        if (!active.current) return
        setBusy(`Checking employees ${next.reduce((n,p) => n+p.rowIds.length,0)+1}–${next.reduce((n,p) => n+p.rowIds.length,0)+ids.length} of ${rowIds.length}`)
        const loaded = await getPayrollSetupContext(clientId, batch.id, ids)
        if (!active.current) return
        if (!loaded.ok || !loaded.data) throw Error(loaded.error || 'Unable to load saved client configuration.')
        const ctx = loaded.data, attempt = attempts.find(item => item.rowIds.join('|') === ids.join('|'))
        const completed = completedQuickPayroll(ctx, ids)
        if (completed) {
          next.push({ rowIds:ids, request:ctx.lastImport!.savedRequest, preview:{ canApply:false,planHash:'',migrationRequired:false,coverage:'Already prepared',issues:[],rows:[] }, requestId:ctx.lastImport!.requestId,completed })
          continue
        }
        const requestId = attempt?.requestId || crypto.randomUUID()
        // An unconfirmed write is checked against its exact receipt before another Apply.
        if (attempt?.uncertain) {
          next.push({ rowIds:ids,preview:{ canApply:false,planHash:'',migrationRequired:false,coverage:'Confirmation pending',issues:[],rows:[] }, requestId,uncertain:true,error:'The previous save could not be confirmed. Open Advanced setup and check its saved receipt before retrying this group.' })
          continue
        }
        try {
          let request = quickPayrollRequest(batch, ctx, ids, includeNew, importAttendance)
          const probe = await previewPayrollSetup(clientId, request)
          if (!probe.ok || !probe.data) throw Error(probe.error || 'Unable to inspect salary rules.')
          request = selectQuickPayrollTemplates(request, probe.data)
          const result = await previewPayrollSetup(clientId, request)
          if (!result.ok || !result.data) throw Error(result.error || 'Unable to prepare payroll preview.')
          const conflict = quickAttendanceConflict(ctx,request,result.data)
          next.push({ rowIds:ids,request,preview:result.data,requestId,error:conflict || result.data.issues.filter(issue => issue.severity === 'Error').map(issue => issue.message).join(' ') })
        } catch (failure) {
          next.push({ rowIds:ids,preview:{ canApply:false,planHash:'',migrationRequired:false,coverage:'Needs review',issues:[],rows:[] },requestId,error:failure instanceof Error ? failure.message : 'Review this source in Advanced setup.' })
        }
      }
      if (active.current) { setPlans(next); setPreviewed(true) }
    } catch (failure) { if (active.current) setError(failure instanceof Error ? failure.message : 'Unable to prepare payroll.') }
    finally { flight.current = false; if (active.current) setBusy('') }
  }
  useEffect(() => { void preview() }, [clientId, batch.id, selection, includeNew, importAttendance]) // eslint-disable-line react-hooks/exhaustive-deps

  const prepare = async () => {
    if (!ready || flight.current || !canManage || !pending.length) return
    flight.current = true; setError('')
    const next = [...plans]
    try {
      for (let index=0; index<next.length; index++) {
        const plan = next[index]
        if (plan.completed) continue
        if (!plan.request) throw Error('Refresh and review this employee group before saving.')
        if (!active.current) return
        setBusy(`Preparing employees: ${next.filter(p=>p.completed).reduce((n,p)=>n+p.rowIds.length,0)} / ${rowIds.length}`)
        // Recheck each group after preceding groups commit; never auto-accept a changed plan.
        const refreshed = await previewPayrollSetup(clientId,plan.request)
        if (!active.current) return
        if (!refreshed.ok || !refreshed.data?.canApply || !sameQuickPayrollPreview(plan.preview,refreshed.data))
          throw Error(refreshed.error || 'A source, employee, policy or calculation changed. Refresh the combined preview before continuing; completed groups are retained.')
        const attempt = { rowIds:plan.rowIds,requestId:plan.requestId,sourceHash:plan.request.quickPreparation!.sourceHash,uncertain:true }
        remember(attempt); plan.uncertain = true; setPlans([...next])
        const saved = await applyPayrollSetup(clientId,plan.request,refreshed.data.planHash,plan.requestId)
        if (!active.current) return
        if (!saved.ok || !saved.data) {
          // A failed response can follow a committed transaction. Resolve through the receipt, never resend blindly.
          const status = await getPayrollSetupContext(clientId,batch.id,plan.rowIds)
          const completed = status.ok && status.data ? completedQuickPayroll(status.data,plan.rowIds,plan.requestId) : null
          if (!completed) { plan.error = saved.error || 'Save could not be confirmed. Check saved status before continuing.'; throw Error(plan.error) }
          plan.completed = completed
        } else plan.completed = saved.data
        plan.uncertain = false; plan.error = ''; remember({ ...attempt,uncertain:false }); setPlans([...next]); onApplied(plan.completed)
      }
    } catch (failure) { if (active.current) { setPlans([...next]); setError(failure instanceof Error ? failure.message : 'Preparation stopped. Completed groups are retained.') } }
    finally { flight.current = false; if (active.current) setBusy('') }
  }
  const rows = plans.flatMap(plan => plan.preview.rows.map(row => ({ ...row, presentDays:plan.request?.presentDaysColumnIndex == null ? null : row.presentDays,
    template:plan.preview.transfer?.templates.find(template=>template.rowIds.includes(row.rowId))?.name || '',
    status:plan.completed ? 'Prepared' : plan.error ? 'Needs review' : 'Ready', attendance:plan.request?.importAttendance ? 'Import monthly totals' : 'Keep existing attendance' })))
  const reviewGroups = new Map<string,string[]>()
  for (const plan of plans) if (plan.error) reviewGroups.set(plan.error,[...(reviewGroups.get(plan.error)||[]),...plan.rowIds])
  return <Drawer open title={`Prepare payroll · ${batch.clientName} · ${batch.month}`} width="min(1160px, 98vw)" className="excel-payroll-setup excel-payroll-quick-prepare" closable={!busy} maskClosable={!busy} onClose={() => { if (!busy) onClose() }} footer={<Space wrap className="excel-payroll-setup-footer">
    <Button disabled={!!busy} onClick={onClose}>Close</Button>
    <Button disabled={!!busy} onClick={() => onAdvanced(rowIds)}>Advanced setup</Button>
    <Button disabled={!!busy || !canManage} title="Refresh calculations and check saved receipts before retrying" onClick={() => void preview()}>{plans.some(plan=>plan.uncertain) ? 'Check saved status' : 'Refresh preview'}</Button>
    <Button type="primary" loading={!!busy} disabled={!canManage || !ready || !!busy || !pending.length} onClick={() => void prepare()}>Save &amp; Prepare Payroll ({rowIds.length-done})</Button>
  </Space>}>
    <Alert type="info" showIcon message="Review once, prepare the selected employees together" description="Payslips are saved. Review salary setup and the selected month’s attendance below, then save once for all listed employees. Daily attendance and payroll execution stay separate." />
    <Space wrap className="excel-payroll-quick-summary"><Tag>{rowIds.length} selected employees</Tag><Tag>{newCount} new</Tag><Tag color={done === rowIds.length ? 'green' : 'blue'}>{done} prepared</Tag><Tag>{context.clientConfiguration ? 'Saved client setup' : 'First-time setup required'}</Tag></Space>
    {context.clientConfiguration?.savedRequest.reviewedExtraDutyUnits && <Alert type="info" message="Saved extra-duty salary units" description="The saved setup uses extra-duty salary units. They remain separate from actual Present days." />}
    <Space direction="vertical" className="excel-payroll-quick-options">
      <Checkbox checked={importAttendance} disabled={!!busy || done>0} onChange={event => { setImportAttendance(event.target.checked); setPreviewed(false) }}>Import source-month attendance ({batch.month})</Checkbox>
      {!!newCount && <Checkbox checked={includeNew} disabled={!!busy || done>0} onChange={event => { setIncludeNew(event.target.checked); setPreviewed(false) }}>Include {newCount} new employees shown in this source</Checkbox>}
      {context.quickPreparation && <span>Attendance cycle: {context.quickPreparation.cycleStart} → {context.quickPreparation.cycleEnd}. {importAttendance ? 'Actual Present and Payable totals from the reviewed source columns.' : 'Recorded attendance is retained.'}</span>}
    </Space>
    {busy && <div role="status" aria-live="polite"><Spin size="small" /> {busy}</div>}
    {error && <Alert type="error" showIcon message={error} />}
    {!previewed && !busy && !error && <Alert type="info" message="Refresh preview before saving." />}
    {!!done && <Progress percent={Math.round(done/rowIds.length*100)} status={done===rowIds.length ? 'success' : 'active'} />}
    {rowIds.length>0 && done===rowIds.length && <Alert type="success" showIcon message="Payroll preparation saved" description={importAttendance ? 'Reviewed employee configuration and monthly attendance are saved. Continue to Regular payroll for its policy checks and draft.' : 'Reviewed employee configuration is saved. Enter or review attendance before running Regular payroll.'} />}
    {[...reviewGroups].map(([reason,ids]) => <Alert key={reason} type="warning" showIcon message={`${ids.length} employees need review`} description={reason} action={<Button disabled={!!busy} onClick={() => onAdvanced(ids)}>Review these employees</Button>} />)}
    {!!rows.length && <DataTable title="Combined payroll preparation preview" rows={rows} getRowId={row=>row.rowId} exportFileName={`${batch.clientName || 'client'}-${batch.month}-payroll-preparation`} columns={[
      {key:'employeeCode',label:'Employee code',width:130},{key:'employeeName',label:'Employee',width:190},{key:'action',label:'Master action',width:140},
      {key:'template',label:'Salary template',width:175},{key:'presentDays',label:'Present days',width:105,render:row=>row.presentDays==null?'Not supplied':String(row.presentDays)},{key:'payableDays',label:'Payable days',width:105},{key:'annualCtc',label:'Annual CTC',width:140,render:row=>formatExcelPayslipAmount(row.annualCtc,2)},
      {key:'net',label:'Preview net',width:140,render:row=>row.net==null?'Needs review':formatExcelPayslipAmount(row.net,2)},{key:'attendance',label:'Attendance',width:180},{key:'status',label:'Status',width:130}
    ]} />}
  </Drawer>
}
