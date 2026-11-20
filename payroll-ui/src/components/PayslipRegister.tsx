import { useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Alert, Button, Checkbox, Drawer, Select, Space, Spin, Tag } from 'antd'
import { getClients, getPayRuns } from '../services/payrollService'
import { getRegisterPayRun, getRegisterPayslip, getRegisterPayslipPdf, queueRegisterPayslipMail, type PayslipDeliveryItem, type PayslipDocument, type PayslipMailJobRequest } from '../services/payslipService'
import { PayslipPreviewLease } from '../utils/payslipDesign'
import { payslipRegisterRunError } from '../utils/payslipRegister'
import type { Client, PayRun, RunEmployee } from '../types/payroll'
import { money } from '../utils/salary'
import { downloadHtmlPdf } from '../utils/htmlPdf'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { useAuthSession } from './AuthGate'
import { useToast } from './ToastProvider'
import './PayslipRegister.css'
import PayrollLocationPayslips from './PayrollLocationPayslips'
import PdfCanvasPreview from './PdfCanvasPreview'
import BulkMailProgress from './BulkMailProgress'
import { usePayslipRegisterMail } from '../hooks/usePayslipRegisterMail'
import { registerMailFilter, validRegisterMailSubmission } from '../utils/payslipRegisterMail'
import { payslipSentCopyLabel } from '../utils/excelPayslipDelivery'
import ExcelPayslipClientOption from './ExcelPayslipClientOption'

export default function PayslipRegister() {
  const session = useAuthSession()
  const toast = useToast()
  const [searchParams] = useSearchParams()
  const requestedClientId = Number(searchParams.get('clientId')) || 0
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('security.manage')
  const canSend = session?.user.permissions.some(permission => ['payroll.run', 'payroll.approve', 'payroll.payments'].includes(permission)) ?? false
  const [clients, setClients] = useState<Client[]>([])
  const [runs, setRuns] = useState<PayRun[]>([])
  const [runsReady, setRunsReady] = useState(false)
  const [clientId, setClientId] = useSessionPreference(`payslips.client:${session?.user.id}`, 0)
  const [runId, setRunId] = useSessionPreference(`payslips.run:${session?.user.id}:${clientId}`, 0)
  const [selectedIds, setSelectedIds] = useSessionPreference<number[]>(`payslips.selection:${session?.user.id}:${clientId}:${runId}`, [])
  const [run, setRun] = useState<PayRun | null>(null)
  const [runLoading, setRunLoading] = useState(false)
  const [runError, setRunError] = useState('')
  const [runLoadRevision, setRunLoadRevision] = useState(0)
  const [selected, setSelected] = useState<RunEmployee | null>(null)
  const [document, setDocument] = useState<PayslipDocument | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [previewError, setPreviewError] = useState('')
  const [nativePdf, setNativePdf] = useState<{ url: string; blob: Blob; fileName: string } | null>(null)
  const [previewLease] = useState(() => new PayslipPreviewLease())
  const [pdfBusy, setPdfBusy] = useState(false)
  const [sending, setSending] = useState(false)
  const [deliveryItems, setDeliveryItems] = useState<PayslipDeliveryItem[]>([])
  const [deliveryOpen, setDeliveryOpen] = useState(false)
  const [mailFilter, setMailFilter] = useSessionPreference(`payslips.mail-filter:${session?.user.id}:${clientId}:${runId}`, 'All')
  const [pendingRequests, setPendingRequests] = useSessionPreference<PayslipMailJobRequest[]>(`payslips.mail-request:${session?.user.id}:${clientId}:${runId}`, [])
  const [resendIds, setResendIds] = useState<number[] | null>(null)
  const [resendConfirmed, setResendConfirmed] = useState(false)
  const [mailError, setMailError] = useState('')
  const [jobId, setJobId] = useState('')
  const previewAbort = useRef<AbortController | null>(null)
  const sendInFlight = useRef(false)

  useEffect(() => {
    let active = true
    // Scope changes invalidate the prior list before its asynchronous refresh.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setRunsReady(false)
    void getClients(true).then(clientRows => {
      if (!active) return
      const available = clientRows.filter(row => row.isActive)
      setClients(available)
      setClientId(current => clientScoped ? scopedClientId : available.some(client => client.id === requestedClientId) ? requestedClientId : available.some(client => client.id === current) ? current : available[0]?.id ?? 0)
    })
    void getPayRuns().then(runRows => { if (active) { setRuns(runRows); setRunsReady(true) } })
    return () => { active = false }
    // Preference setters are recreated by the shared hook; fetch only when user scope changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientScoped, scopedClientId, requestedClientId])

  const approvedRuns = useMemo(() => runs.filter(item => ['Approved', 'Partially Paid', 'Paid'].includes(item.status)), [runs])
  const clientsWithPayslips = useMemo(() => new Set(approvedRuns.map(item => item.clientId)), [approvedRuns])
  const clientRuns = useMemo(() => approvedRuns.filter(item => item.clientId === clientId), [approvedRuns, clientId])
  const rows = useMemo(() => run?.clientId === clientId && run.id === runId ? run.employees.filter(employee => !employee.isSkipped) : [], [run, clientId, runId])
  const validSelection = selectedIds.filter(id => rows.some(row => row.employeeId === id))
  const mail = usePayslipRegisterMail(clientId, run?.clientId === clientId && run.id === runId ? runId : 0)
  const mailStatuses = new Map(mail.data?.items.map(item => [item.employeeId, item]) ?? [])
  const visibleRows = rows.filter(row => registerMailFilter(mailStatuses.get(row.employeeId)?.status || 'Checking', mailFilter))
  const sendIds = validSelection.filter(id => mailStatuses.get(id)?.canSend)
  const selectedResendIds = validSelection.filter(id => mailStatuses.get(id)?.canResend)
  const withEmail = visibleRows.filter(row => mailFilter === 'Sent' || mailFilter === 'Needs attention'
    ? mailStatuses.get(row.employeeId)?.canResend : mailStatuses.get(row.employeeId)?.canSend)
  const pendingRequest = pendingRequests[0]

  useEffect(() => {
    if (!runsReady) return
    let active = true
    const controller = new AbortController()
    const available = clientRuns.some(item => item.id === runId) ? runId : clientRuns[0]?.id ?? 0
    previewAbort.current?.abort(); previewLease.clear()
    // Clear a scoped document immediately; a late request must never keep it visible.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setSelected(null); setDocument(null); setNativePdf(null); setPreviewError(''); setPreviewLoading(false)
    setRun(null); setRunError(''); setRunLoading(available > 0)
    setDeliveryItems([]); setDeliveryOpen(false)
    setResendIds(null); setResendConfirmed(false); setMailError(''); setJobId('')
    if (available !== runId) setRunId(available)
    else if (available > 0) void getRegisterPayRun(available, { signal: controller.signal }).then(result => {
      if (!active || controller.signal.aborted) return
      setRunLoading(false)
      const error = payslipRegisterRunError(result, clientId, available)
      if (error) { setRunError(error); return }
      setRun(result.data)
    })
    return () => { active = false; controller.abort() }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- The request scope is keyed by clientRuns/runId.
  }, [clientRuns, clientId, runId, runsReady, runLoadRevision, previewLease])

  useEffect(() => () => { previewAbort.current?.abort(); previewLease.clear() }, [previewLease])

  const preview = async (row: RunEmployee) => {
    if (!run || run.clientId !== clientId || run.id !== runId) return
    previewAbort.current?.abort()
    const controller = new AbortController()
    previewAbort.current = controller
    const scope = [clientId, run.id, row.employeeId].join(':')
    const sequence = previewLease.begin(scope)
    setSelected(row)
    setDocument(null)
    setNativePdf(null); setPreviewError('')
    setPreviewLoading(true)
    const result = await getRegisterPayslip(run.id, row.employeeId, { signal: controller.signal })
    if (!previewLease.current(scope, sequence)) return
    if (!result.ok || !result.data) { setPreviewLoading(false); setPreviewError(result.error || 'This payslip is unavailable.'); return }
    if (result.data.payRunId !== run.id || result.data.payPeriod !== run.payPeriod) { setPreviewLoading(false); setPreviewError('The payslip response did not match this pay run and period. Refresh and retry.'); return }
    setDocument(result.data)
    if (result.data.pdfPreferred) {
      const pdf = await getRegisterPayslipPdf(run.id, row.employeeId, { signal: controller.signal })
      if (!previewLease.current(scope, sequence)) return
      if (!pdf.ok || !pdf.data) setPreviewError(pdf.error || 'The configured payslip PDF is unavailable.')
      else {
        const url = previewLease.accept(scope, sequence, pdf.data.blob)
        if (url) setNativePdf({ url, blob: pdf.data.blob, fileName: pdf.data.fileName })
      }
    }
    setPreviewLoading(false)
  }
  const closePreview = () => { previewAbort.current?.abort(); previewLease.clear(); setSelected(null); setDocument(null); setNativePdf(null); setPreviewError(''); setPreviewLoading(false) }
  const download = () => {
    if (!document) return
    const link = window.document.createElement('a')
    link.href = URL.createObjectURL(new Blob([document.html], { type: 'text/html' }))
    link.download = document.fileName
    link.click()
    URL.revokeObjectURL(link.href)
  }
  const printPreview = () => {
    if (!document) return
    const popup = window.open('', '_blank')
    if (!popup) return
    popup.opener = null
    popup.document.write(document.html)
    popup.document.close()
    popup.focus()
  }
  const downloadPdf = async () => {
    if (!document) return
    if (document.pdfPreferred) {
      if (!nativePdf) return
      const link = window.document.createElement('a')
      link.href = nativePdf.url; link.download = nativePdf.fileName; link.click()
      return
    }
    setPdfBusy(true)
    try { await downloadHtmlPdf(document.html, document.fileName.replace(/\.html$/i, '')) }
    catch { toast('Unable to export the payslip PDF.', 'error') }
    finally { setPdfBusy(false) }
  }
  const openNativePdf = () => { if (nativePdf) window.open(nativePdf.url, '_blank', 'noopener,noreferrer') }
  const send = async (employeeIds: number[], resend = false) => {
    if (!run || run.clientId !== clientId || run.id !== runId || !canSend || sendInFlight.current || (!pendingRequest && (!mail.ready || !employeeIds.length))) return
    if (!pendingRequest && resend && !resendConfirmed) return
    const request = pendingRequest || { requestId: crypto.randomUUID().replaceAll('-', ''), employeeIds, resend, acknowledgeResend: resend }
    setPendingRequests([request])
    sendInFlight.current = true
    setSending(true); setMailError('')
    try {
      const result = await queueRegisterPayslipMail(run.id, request)
      if (!result.ok || !validRegisterMailSubmission(result.data, clientId, run.id, request)) {
        setMailError(result.error || 'The email request outcome could not be verified. Retry the same request to check it safely.')
        setResendIds(null); setDeliveryOpen(true)
        return
      }
      setPendingRequests([]); setDeliveryItems(result.data.items); setJobId(result.data.job.id)
      setResendIds(null); setResendConfirmed(false); setDeliveryOpen(true); mail.refresh()
      const handledIds = new Set(result.data.items.filter(item => item.status !== 'Excluded').map(item => item.employeeId))
      setSelectedIds(current => current.filter(id => !handledIds.has(id)))
      window.dispatchEvent(new Event('hrms:actions-changed'))
    } finally { sendInFlight.current = false; setSending(false) }
  }
  const queued = deliveryItems.filter(item => item.status === 'Queued').length
  const excluded = deliveryItems.filter(item => item.status === 'Excluded').length
  const deliveryRows: Array<{ employeeId: number; employeeCode: string; email?: string; status: string; message: string }> = deliveryItems.length ? deliveryItems : mail.data?.items ?? []

  return <section className="payslip-register recruitment-viewport">
    {!clientScoped && <PageHeaderPortal slot="payslips-client-controls"><Select aria-label="Payslip register client" className="app-search-select" showSearch optionFilterProp="label" optionLabelProp="label" value={clientId || undefined} loading={!runsReady} disabled={sending || pdfBusy} placeholder="Select client" onChange={value => { closePreview(); setClientId(Number(value)) }} style={{ width: 260, maxWidth: '100%' }}>{clients.map(client => <Select.Option key={client.id} value={client.id} label={client.name} className={clientsWithPayslips.has(client.id) ? 'excel-payslip-client-has-batches' : 'excel-payslip-client-no-batches'}><ExcelPayslipClientOption name={client.name} hasSavedBatch={clientsWithPayslips.has(client.id)} badgeLabel="Approved payroll" /></Select.Option>)}</Select></PageHeaderPortal>}
    <PageHeaderPortal slot="payslips-page-controls"><Space wrap className="payslip-toolbar">
      <label className="payslip-period-control"><span>Pay period</span><SearchSelect value={runId} disabled={sending || pdfBusy} onChange={value => { closePreview(); setRunId(Number(value)) }} options={selectOptions(clientRuns.map(item => ({ value: item.id, label: `${item.payPeriod} - ${item.status}` })), 'Select approved pay run', 0)} /></label>
      <Select aria-label="Payslip email status" className="payslip-email-filter" value={mailFilter} disabled={sending} onChange={setMailFilter} options={['All', 'Not sent', 'Sent', 'Queued', 'Email missing', 'Needs attention'].map(value => ({ value, label: `${value} (${rows.filter(row => registerMailFilter(mailStatuses.get(row.employeeId)?.status || 'Checking', value)).length})` }))} />
      {canSend && <><Button disabled={!mail.ready || sending || !!pendingRequest || !withEmail.length} onClick={() => setSelectedIds(withEmail.map(row => row.employeeId))}>Select with email ({withEmail.length})</Button><Button disabled={!validSelection.length || sending} onClick={() => setSelectedIds([])}>Clear selection ({validSelection.length})</Button><Button type="primary" loading={sending} disabled={sending || !pendingRequest && (!mail.ready || !sendIds.length)} onClick={() => void send(sendIds)}>{pendingRequest ? 'Retry same email request' : `Send selected (${sendIds.length})`}</Button><Button disabled={!mail.ready || sending || !!pendingRequest || !selectedResendIds.length} onClick={() => { setResendConfirmed(false); setResendIds(selectedResendIds) }}>Resend selected ({selectedResendIds.length})</Button></>}
      <Button onClick={() => setDeliveryOpen(true)}>Email progress</Button>
      <Button href="/security/apps/notifications">Notification settings</Button>
      {run && <PayrollLocationPayslips key={run.id} run={run} />}
    </Space></PageHeaderPortal>
    {!runsReady || runLoading ? <section className="card report-empty" role="status"><Space><Spin /><span>Loading approved pay run…</span></Space></section> : runError ? <section className="card report-empty"><Alert type="error" showIcon message={runError} /><Button onClick={() => setRunLoadRevision(current => current + 1)}>Retry loading pay run</Button></section> : run ? <section className="card payslip-list">
      {mail.error && <Alert type="warning" showIcon message={mail.error} action={<Button onClick={mail.refresh}>Refresh email status</Button>} />}
      <BulkMailProgress jobs={mail.data?.jobs ?? []} selectedId={jobId} onSelect={setJobId} loading={mail.loading} error="" onRefresh={mail.refresh} expanded={false} onToggle={() => setDeliveryOpen(true)} />
      <DataTable rows={visibleRows} fillHeight getRowId={row => row.employeeId} exportFileName={`payslip-register-${run.payPeriod}`} rowSelection={canSend ? { selectedRowKeys: validSelection, onChange: keys => setSelectedIds(keys.map(Number)), getCheckboxProps: row => ({ disabled: sending || !!pendingRequest || !mail.ready || !(mailStatuses.get(row.employeeId)?.canSend || mailStatuses.get(row.employeeId)?.canResend) }) } : undefined} columns={[
      { key: 'employee', label: 'Employee', value: row => row.employeeName, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
      { key: 'emailStatus', label: 'Email status', width: 230, value: row => mailStatuses.get(row.employeeId)?.status || 'Checking', render: row => {
        const item = mailStatuses.get(row.employeeId)
        return <Space direction="vertical" size={2}><Tag color={item?.status === 'Sent' || item?.status === 'Delivered' ? 'green' : item?.canSend ? 'default' : 'orange'}>{item?.status === 'Sent' ? 'SMTP accepted' : item?.status || 'Checking...'}</Tag>{item && ['Sent', 'Delivered'].includes(item.status) && <Tag color={item.sentCopyStatus === 'Saved' ? 'green' : 'orange'}>{payslipSentCopyLabel(item.sentCopyStatus)}</Tag>}{item?.lastRecipient && <small title={item.message}>To: {item.lastRecipient}</small>}</Space>
      } },
      { key: 'department', label: 'Department' }, { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay + row.oneTimeEarnings) },
      { key: 'deductions', label: 'Deductions', value: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
      { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) }, { key: 'paymentStatus', label: 'Payment' },
      { key: 'email', label: 'Work email', width: 230, value: row => mailStatuses.get(row.employeeId)?.email || 'Missing / invalid' }
    ]} actionsWidth={canSend ? 180 : 100} actions={row => <Space size={6}><Button size="small" disabled={pdfBusy} onClick={() => void preview(row)}>Preview</Button>{canSend && (mailStatuses.get(row.employeeId)?.canResend ? <Button size="small" disabled={sending || !!pendingRequest || !mail.ready} onClick={() => { setResendConfirmed(false); setResendIds([row.employeeId]) }}>Resend</Button> : <Button size="small" disabled={sending || !!pendingRequest || !mail.ready || !mailStatuses.get(row.employeeId)?.canSend} onClick={() => void send([row.employeeId])}>Send PDF</Button>)}</Space>} /></section> : <section className="card report-empty"><p>No approved pay run is available for this client.</p></section>}
    {selected && run?.clientId === clientId && run.id === runId && <div className="payslip-modal-backdrop" onClick={closePreview}>
      <section className="payslip-preview-panel payslip-document-panel" role="dialog" aria-modal="true" aria-label="Payslip preview" onClick={event => event.stopPropagation()}>
        <header><div><span className="eyebrow purple">{run.payPeriod}</span><h3>{selected.employeeName}</h3><p>{selected.employeeCode} - {selected.department}</p></div><button type="button" className="payslip-close" aria-label="Close payslip preview" onClick={closePreview}>x</button></header>
        {previewLoading
          ? <div className="payslip-preview-loading" role="status"><Spin /><span>Loading configured payslip…</span></div>
          : previewError
            ? <div className="payslip-preview-error"><Alert type="error" showIcon message={previewError} /><Button onClick={() => void preview(selected)}>Retry preview</Button></div>
            : document?.pdfPreferred
              ? nativePdf && <PdfCanvasPreview blob={nativePdf.blob} title="Payslip PDF preview" />
              : document && <iframe title="Payslip document preview" sandbox="" srcDoc={document.html} />}
        <footer><small>Payment status: <b>{selected.paymentStatus}</b></small><div>
          {document?.pdfPreferred
            ? <><button type="button" disabled={!nativePdf || previewLoading || !!previewError} onClick={openNativePdf}>Open PDF</button><button type="button" className="payslip-pdf-button" disabled={!nativePdf || previewLoading || !!previewError} onClick={() => void downloadPdf()}>Download PDF</button></>
            : <><button type="button" disabled={!document || previewLoading || !!previewError} onClick={printPreview}>Open printable</button><button type="button" disabled={!document || previewLoading || !!previewError} onClick={download}>Download HTML</button><button type="button" className="payslip-pdf-button" disabled={!document || pdfBusy || previewLoading || !!previewError} onClick={() => void downloadPdf()}>{pdfBusy ? 'Preparing PDF...' : 'Export PDF'}</button></>}
          {canSend && <button type="button" disabled={!document || sending || !!pendingRequest || !mail.ready || !mailStatuses.get(selected.employeeId)?.canSend || previewLoading || !!previewError} onClick={() => void send([selected.employeeId])}>{sending ? 'Queuing...' : 'Send PDF'}</button>}
        </div></footer>
      </section>
    </div>}
    <Drawer title="Payslip delivery" open={deliveryOpen} width="min(720px, 98vw)" rootClassName="payslip-register-mail-drawer" contentWrapperStyle={{ width: 'min(720px, 98vw)', maxWidth: '100vw' }} style={{ width: '100%', maxWidth: '100%', minWidth: 0 }} onClose={() => setDeliveryOpen(false)}>
      {mailError && <Alert type="error" showIcon message={mailError} action={pendingRequest && <Button loading={sending} onClick={() => void send([])}>Retry same email request</Button>} />}
      <BulkMailProgress jobs={mail.data?.jobs ?? []} selectedId={jobId} onSelect={setJobId} loading={mail.loading} error={mail.error} onRefresh={mail.refresh} />
      {!!deliveryItems.length && <Alert type={excluded ? 'warning' : 'info'} showIcon message={`${queued} queued, ${deliveryItems.length - queued - excluded} already handled, ${excluded} excluded`} />}
      <p>Each employee receives their own PDF, including their location slips. Sender, Sent-folder settings and delivery audit use <a href="/security/apps/notifications">Security &gt; Apps &gt; Notifications</a>.</p>
      <DataTable rows={deliveryRows} getRowId={row => row.employeeId} columns={[{ key: 'employeeCode', label: 'Employee' }, { key: 'email', label: 'Recipient' }, { key: 'status', label: 'Status' }, { key: 'message', label: 'Details', wrap: true }]} />
    </Drawer>
    <Drawer title="Resend payslips" open={resendIds !== null} width="min(800px, 98vw)" rootClassName="payslip-register-mail-drawer" contentWrapperStyle={{ width: 'min(800px, 98vw)', maxWidth: '100vw' }} style={{ width: '100%', maxWidth: '100%', minWidth: 0 }} onClose={() => !sending && setResendIds(null)} footer={<Space wrap><Button disabled={sending} onClick={() => setResendIds(null)}>Cancel</Button><Button type="primary" loading={sending} disabled={!resendConfirmed || !resendIds?.length || !mail.ready || !!pendingRequest} onClick={() => void send(resendIds || [], true)}>Resend PDF ({resendIds?.length || 0})</Button></Space>}>
      <Alert type="warning" showIcon message="These employees will receive another email for this pay run." description="The approved salary amounts stay unchanged. Queued, delayed or uncertain deliveries cannot be resent." />
      <DataTable rows={rows.filter(row => resendIds?.includes(row.employeeId))} getRowId={row => row.employeeId} exportDisabled columns={[{ key: 'employeeCode', label: 'Code' }, { key: 'employeeName', label: 'Employee' }, { key: 'email', label: 'Recipient', value: row => mailStatuses.get(row.employeeId)?.email || '' }]} />
      <Checkbox checked={resendConfirmed} disabled={sending} onChange={event => setResendConfirmed(event.target.checked)}>I confirm these {resendIds?.length || 0} employees should receive this payslip again.</Checkbox>
    </Drawer>
  </section>
}
