import { useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Alert, Button, Drawer, Space, Spin, Tag } from 'antd'
import { getClients, getPayRuns } from '../services/payrollService'
import { getRegisterPayRun, getRegisterPayslip, getRegisterPayslipPdf, sendRegisterPayslips, type PayslipDeliveryItem, type PayslipDocument } from '../services/payslipService'
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
  const [sendProgress, setSendProgress] = useState('')
  const [deliveryItems, setDeliveryItems] = useState<PayslipDeliveryItem[]>([])
  const [deliveryOpen, setDeliveryOpen] = useState(false)
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

  const clientRuns = useMemo(() => runs.filter(item => item.clientId === clientId && ['Approved', 'Partially Paid', 'Paid'].includes(item.status)), [runs, clientId])
  const rows = useMemo(() => run?.clientId === clientId && run.id === runId ? run.employees.filter(employee => !employee.isSkipped) : [], [run, clientId, runId])
  const validSelection = selectedIds.filter(id => rows.some(row => row.employeeId === id))

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
  const send = async (employeeIds: number[]) => {
    if (!run || run.clientId !== clientId || run.id !== runId || !canSend || sendInFlight.current || !employeeIds.length) return
    sendInFlight.current = true
    setSending(true)
    const items: PayslipDeliveryItem[] = []
    try {
      for (let index = 0; index < employeeIds.length; index += 25) {
        setSendProgress(`${Math.min(index + 25, employeeIds.length)}/${employeeIds.length}`)
        const result = await sendRegisterPayslips(run.id, employeeIds.slice(index, index + 25))
        if (!result.ok) { toast(result.error || 'Unable to queue payslips.', 'error'); break }
        items.push(...result.data.items)
      }
      setDeliveryItems(items)
      if (items.length) {
        setDeliveryOpen(true)
        const handledIds = new Set(items.filter(item => item.status !== 'Excluded').map(item => item.employeeId))
        setSelectedIds(current => current.filter(id => !handledIds.has(id)))
      }
    } finally { sendInFlight.current = false; setSending(false); setSendProgress('') }
  }
  const queued = deliveryItems.filter(item => item.status === 'Queued').length
  const excluded = deliveryItems.filter(item => item.status === 'Excluded').length

  return <section className="payslip-register recruitment-viewport">
    {!clientScoped && <PageHeaderPortal slot="payslips-client-controls"><SearchSelect value={clientId} disabled={sending || pdfBusy} onChange={value => { closePreview(); setClientId(Number(value)) }} options={clients.map(client => ({ value: client.id, label: client.name }))} /></PageHeaderPortal>}
    <PageHeaderPortal slot="payslips-page-controls"><Space wrap className="payslip-toolbar">
      <label className="payslip-period-control"><span>Pay period</span><SearchSelect value={runId} disabled={sending || pdfBusy} onChange={value => { closePreview(); setRunId(Number(value)) }} options={selectOptions(clientRuns.map(item => ({ value: item.id, label: `${item.payPeriod} - ${item.status}` })), 'Select approved pay run', 0)} /></label>
      {canSend && <><Button disabled={!rows.length || sending} onClick={() => setSelectedIds(rows.map(row => row.employeeId))}>Select all ({rows.length})</Button><Button disabled={!validSelection.length || sending} onClick={() => setSelectedIds([])}>Clear selection</Button><Button type="primary" loading={sending} disabled={!validSelection.length} onClick={() => void send(validSelection)}>{sending ? `Queuing ${sendProgress}` : `Send selected (${validSelection.length})`}</Button></>}
      <Button href="/security/apps/notifications">Notification settings</Button>
      {run && <PayrollLocationPayslips key={run.id} run={run} />}
    </Space></PageHeaderPortal>
    {!runsReady || runLoading ? <section className="card report-empty" role="status"><Space><Spin /><span>Loading approved pay run…</span></Space></section> : runError ? <section className="card report-empty"><Alert type="error" showIcon message={runError} /><Button onClick={() => setRunLoadRevision(current => current + 1)}>Retry loading pay run</Button></section> : run ? <section className="card payslip-list"><DataTable rows={rows} fillHeight getRowId={row => row.employeeId} exportFileName={`payslip-register-${run.payPeriod}`} rowSelection={canSend ? { selectedRowKeys: validSelection, onChange: keys => setSelectedIds(keys.map(Number)), getCheckboxProps: () => ({ disabled: sending }) } : undefined} columns={[
      { key: 'employee', label: 'Employee', value: row => row.employeeName, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
      { key: 'department', label: 'Department' }, { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay + row.oneTimeEarnings) },
      { key: 'deductions', label: 'Deductions', value: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
      { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) }, { key: 'paymentStatus', label: 'Payment' }
    ]} actionsWidth={canSend ? 180 : 100} actions={row => <Space size={6}><Button size="small" disabled={pdfBusy} onClick={() => void preview(row)}>Preview</Button>{canSend && <Button size="small" disabled={sending} onClick={() => void send([row.employeeId])}>Send</Button>}</Space>} /></section> : <section className="card report-empty"><p>No approved pay run is available for this client.</p></section>}
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
          {canSend && <button type="button" disabled={!document || sending || previewLoading || !!previewError} onClick={() => void send([selected.employeeId])}>{sending ? 'Queuing...' : 'Send'}</button>}
        </div></footer>
      </section>
    </div>}
    <Drawer title="Payslip delivery" open={deliveryOpen} width={720} onClose={() => setDeliveryOpen(false)}>
      <Alert type={excluded ? 'warning' : 'success'} showIcon message={`${queued} queued, ${deliveryItems.length - queued - excluded} already queued/sent, ${excluded} excluded`} description={<>Email templates, recipients, SMTP and delivery audit are managed in <a href="/security/apps/notifications">Security &gt; Apps &gt; Notifications</a>. Each email includes a printable HTML payslip attachment.</>} />
      <DataTable rows={deliveryItems} getRowId={row => row.employeeId} columns={[{ key: 'employeeCode', label: 'Employee' }, { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Excluded' ? 'orange' : 'blue'}>{row.status}</Tag> }, { key: 'message', label: 'Details', wrap: true }]} />
    </Drawer>
  </section>
}
