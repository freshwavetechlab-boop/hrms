import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Drawer, Space, Spin, Tag } from 'antd'
import { getClients, getPayRun, getPayRuns } from '../services/payrollService'
import { getRegisterPayslip, sendRegisterPayslips, type PayslipDeliveryItem, type PayslipDocument } from '../services/payslipService'
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

export default function PayslipRegister() {
  const session = useAuthSession()
  const toast = useToast()
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
  const [selected, setSelected] = useState<RunEmployee | null>(null)
  const [document, setDocument] = useState<PayslipDocument | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [pdfBusy, setPdfBusy] = useState(false)
  const [sending, setSending] = useState(false)
  const [sendProgress, setSendProgress] = useState('')
  const [deliveryItems, setDeliveryItems] = useState<PayslipDeliveryItem[]>([])
  const [deliveryOpen, setDeliveryOpen] = useState(false)
  const previewSequence = useRef(0)
  const sendInFlight = useRef(false)

  useEffect(() => {
    let active = true
    setRunsReady(false)
    void getClients(true).then(clientRows => {
      if (!active) return
      const available = clientRows.filter(row => row.isActive)
      setClients(available)
      setClientId(current => clientScoped ? scopedClientId : available.some(client => client.id === current) ? current : available[0]?.id ?? 0)
    })
    void getPayRuns().then(runRows => { if (active) { setRuns(runRows); setRunsReady(true) } })
    return () => { active = false }
    // Preference setters are recreated by the shared hook; fetch only when user scope changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientScoped, scopedClientId])

  const clientRuns = useMemo(() => runs.filter(item => item.clientId === clientId && ['Approved', 'Partially Paid', 'Paid'].includes(item.status)), [runs, clientId])
  const rows = useMemo(() => run?.clientId === clientId && run.id === runId ? run.employees.filter(employee => !employee.isSkipped) : [], [run, clientId, runId])
  const validSelection = selectedIds.filter(id => rows.some(row => row.employeeId === id))

  useEffect(() => {
    if (!runsReady) return
    let active = true
    const available = clientRuns.some(item => item.id === runId) ? runId : clientRuns[0]?.id ?? 0
    previewSequence.current += 1
    void (available ? getPayRun(available) : Promise.resolve(null)).then(value => {
      if (!active) return
      setRunId(available)
      setRun(value)
      setSelected(null)
      setDocument(null)
      setPreviewLoading(false)
      setDeliveryItems([])
      setDeliveryOpen(false)
    })
    return () => { active = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- The request scope is keyed by clientRuns/runId.
  }, [clientRuns, runId, runsReady])

  const preview = async (row: RunEmployee) => {
    if (!run || run.clientId !== clientId || run.id !== runId) return
    const sequence = ++previewSequence.current
    setSelected(row)
    setDocument(null)
    setPreviewLoading(true)
    const result = await getRegisterPayslip(run.id, row.employeeId)
    if (sequence !== previewSequence.current) return
    setPreviewLoading(false)
    if (result.ok && result.data) setDocument(result.data)
    else { setSelected(null); toast(result.error || 'This payslip is unavailable.', 'error') }
  }
  const closePreview = () => { previewSequence.current += 1; setSelected(null); setDocument(null); setPreviewLoading(false) }
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
    setPdfBusy(true)
    try { await downloadHtmlPdf(document.html, document.fileName.replace(/\.html$/i, '')) }
    catch { toast('Unable to export the payslip PDF.', 'error') }
    finally { setPdfBusy(false) }
  }
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
    {!clientScoped && <PageHeaderPortal slot="payslips-client-controls"><SearchSelect value={clientId} disabled={sending} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></PageHeaderPortal>}
    <PageHeaderPortal slot="payslips-page-controls"><Space wrap className="payslip-toolbar">
      <label className="payslip-period-control"><span>Pay period</span><SearchSelect value={runId} disabled={sending} onChange={value => setRunId(Number(value))} options={selectOptions(clientRuns.map(item => ({ value: item.id, label: `${item.payPeriod} - ${item.status}` })), 'Select approved pay run', 0)} /></label>
      {canSend && <><Button disabled={!rows.length || sending} onClick={() => setSelectedIds(rows.map(row => row.employeeId))}>Select all ({rows.length})</Button><Button disabled={!validSelection.length || sending} onClick={() => setSelectedIds([])}>Clear selection</Button><Button type="primary" loading={sending} disabled={!validSelection.length} onClick={() => void send(validSelection)}>{sending ? `Queuing ${sendProgress}` : `Send selected (${validSelection.length})`}</Button></>}
      <Button href="/settings/notifications">Notification settings</Button>
    </Space></PageHeaderPortal>
    {run ? <section className="card payslip-list"><DataTable rows={rows} fillHeight getRowId={row => row.employeeId} exportFileName={`payslip-register-${run.payPeriod}`} rowSelection={canSend ? { selectedRowKeys: validSelection, onChange: keys => setSelectedIds(keys.map(Number)), getCheckboxProps: () => ({ disabled: sending }) } : undefined} columns={[
      { key: 'employee', label: 'Employee', value: row => row.employeeName, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
      { key: 'department', label: 'Department' }, { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay) },
      { key: 'deductions', label: 'Deductions', value: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
      { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) }, { key: 'paymentStatus', label: 'Payment' }
    ]} actionsWidth={canSend ? 180 : 100} actions={row => <Space size={6}><Button size="small" onClick={() => void preview(row)}>Preview</Button>{canSend && <Button size="small" disabled={sending} onClick={() => void send([row.employeeId])}>Send</Button>}</Space>} /></section> : <section className="card report-empty"><p>No approved pay run is available for this client.</p></section>}
    {selected && run && <div className="payslip-modal-backdrop" onClick={closePreview}><section className="payslip-preview-panel payslip-document-panel" role="dialog" aria-modal="true" aria-label="Payslip preview" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">{run.payPeriod}</span><h3>{selected.employeeName}</h3><p>{selected.employeeCode} - {selected.department}</p></div><button type="button" className="payslip-close" aria-label="Close payslip preview" onClick={closePreview}>x</button></header>{previewLoading ? <div className="payslip-preview-loading"><Spin tip="Loading configured payslip..." /></div> : document && <iframe title="Payslip document preview" sandbox="" srcDoc={document.html} />}<footer><small>Payment status: <b>{selected.paymentStatus}</b></small><div><button type="button" disabled={!document} onClick={printPreview}>Open printable</button><button type="button" disabled={!document} onClick={download}>Download HTML</button><button type="button" className="payslip-pdf-button" disabled={!document || pdfBusy} onClick={() => void downloadPdf()}>{pdfBusy ? 'Preparing PDF...' : 'Export PDF'}</button>{canSend && <button type="button" disabled={!document || sending} onClick={() => void send([selected.employeeId])}>{sending ? 'Queuing...' : 'Send'}</button>}</div></footer></section></div>}
    <Drawer title="Payslip delivery" open={deliveryOpen} width={720} onClose={() => setDeliveryOpen(false)}>
      <Alert type={excluded ? 'warning' : 'success'} showIcon message={`${queued} queued, ${deliveryItems.length - queued - excluded} already queued/sent, ${excluded} excluded`} description={<>Email templates, recipients, SMTP and delivery audit are managed in <a href="/settings/notifications">Settings &gt; Notifications</a>. Each email includes a printable HTML payslip attachment.</>} />
      <DataTable rows={deliveryItems} getRowId={row => row.employeeId} columns={[{ key: 'employeeCode', label: 'Employee' }, { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Excluded' ? 'orange' : 'blue'}>{row.status}</Tag> }, { key: 'message', label: 'Details', wrap: true }]} />
    </Drawer>
  </section>
}
