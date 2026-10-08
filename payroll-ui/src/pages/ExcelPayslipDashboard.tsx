import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, Empty, Select, Tag } from 'antd'
import { ArrowRightOutlined, FileExcelOutlined, MinusCircleOutlined, ReloadOutlined, TeamOutlined, WalletOutlined } from '@ant-design/icons'
import { useAuthSession } from '../components/AuthGate'
import DataTable from '../components/DataTable'
import { PageHeaderPortal } from '../components/layout/AppPageHeader'
import { DashboardChartCard, DashboardChartGrid, DashboardEngine, DashboardKpiGrid, DashboardSectionCard, type DashboardChartSpec } from '../components/dashboard/DashboardEngine'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { getExcelPayslipClients, getExcelPayslipDashboard, type ExcelPayslipDashboard as DashboardSnapshot, type ExcelPayslipSummary } from '../services/excelPayslipService'
import './DashboardPage.css'
import './ExcelPayslipDashboard.css'

const money = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 })
const count = new Intl.NumberFormat('en-IN')
function monthLabel(value: string) {
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(value)) return value
  const [year, month] = value.split('-').map(Number)
  return new Date(year, month - 1, 1).toLocaleDateString('en-IN', { month: 'short', year: 'numeric' })
}
function savedAt(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}
function previousMonth(value: string) {
  const [year, month] = value.split('-').map(Number)
  return month === 1 ? `${year - 1}-12` : `${year}-${String(month - 1).padStart(2, '0')}`
}

export default function ExcelPayslipDashboard() {
  const session = useAuthSession(), navigate = useNavigate()
  const canRead = !!session?.user.permissions.includes('reports.view')
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('security.manage')
  const [clients, setClients] = useState<{ id: number; name: string }[]>([])
  const [preferredClient, setPreferredClient] = useSessionPreference(`excel-payslips:${session?.user.id}:client`, scopedClientId)
  const clientId = clientScoped ? clients.find(client => client.id === scopedClientId)?.id || 0 : clients.find(client => client.id === preferredClient)?.id || clients[0]?.id || 0
  const [preferredBatch, setPreferredBatch] = useSessionPreference(`excel-payslip-dashboard:${session?.user.id}:${clientId}:batch`, '')
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null)
  const [clientsLoading, setClientsLoading] = useState(true), [loading, setLoading] = useState(false)
  const [clientError, setClientError] = useState(''), [error, setError] = useState(''), [revision, setRevision] = useState(0)

  useEffect(() => {
    if (!canRead) { setClientsLoading(false); return }
    let active = true
    setClientsLoading(true); setClientError('')
    void getExcelPayslipClients().then(result => {
      if (!active) return
      if (result.ok) setClients(result.data)
      else { setClients([]); setClientError(result.error || 'Unable to load accessible clients.') }
      setClientsLoading(false)
    })
    return () => { active = false }
  }, [canRead, revision])

  useEffect(() => {
    let active = true
    setError('')
    if (!canRead || !clientId) { setSnapshot(null); setLoading(false); return }
    setLoading(true)
    void getExcelPayslipDashboard(clientId, preferredBatch).then(result => {
      if (!active) return
      if (result.ok && result.data?.clientId === clientId) setSnapshot(result.data)
      else { setSnapshot(null); setError(result.error || 'Unable to load Excel Payslips dashboard.') }
      setLoading(false)
    })
    return () => { active = false }
  }, [canRead, clientId, preferredBatch, revision])

  const dashboard = snapshot?.clientId === clientId ? snapshot : null
  const selected = dashboard?.selectedBatch, totals = dashboard?.summary
  const history = dashboard?.history ?? [], trend = dashboard?.trend ?? []
  const pendingSelection = history.find(item => item.id === preferredBatch) || selected
  const months = [...new Set(history.map(batch => batch.month))].sort().reverse()
  const monthBatches = history.filter(batch => batch.month === pendingSelection?.month)
  const currentClient = clients.find(client => client.id === clientId)
  const prior = selected ? trend.find(point => point.month === previousMonth(selected.month)) : undefined
  const priorBatch = selected ? history.find(batch => batch.month === previousMonth(selected.month)) : undefined
  const difference = totals && prior ? totals.netPay - prior.netPay : null
  const percentage = difference !== null && prior?.netPay ? difference / Math.abs(prior.netPay) * 100 : null
  const openBatch = (batchId?: string) => {
    const query = new URLSearchParams({ report: 'excel-payslips' })
    if (clientId) query.set('clientId', String(clientId))
    if (batchId) query.set('batchId', batchId)
    navigate(`/reports/payroll-reports?${query}`)
  }
  const batchLabel = (batch: ExcelPayslipSummary) => `${savedAt(batch.createdAtUtc)} · ${batch.rowCount} payslips · #${batch.id.slice(0, 8)}`
  const charts: DashboardChartSpec[] = totals ? [
    { id: 'excel-payslips-monthly-net', title: 'Monthly net pay', subtitle: 'Latest saved batch per month · last 12 available months. Separate uploads are not added together.',
      labels: trend.map(point => monthLabel(point.month)), series: [{ label: 'Net pay', values: trend.map(point => point.netPay) }], kind: 'area', compatibleKinds: ['area', 'line', 'bar'], valueFormat: value => money.format(value),
      onPointClick: label => { const point = trend.find(item => monthLabel(item.month) === label); if (point) setPreferredBatch(point.batchId) } },
    { id: 'excel-payslips-selected-amounts', title: 'Selected batch amounts', subtitle: 'Imported earnings, employee deductions and employer contributions.',
      labels: ['Earnings', 'Deductions', 'Employer contributions'], series: [{ label: 'Amount', values: [totals.earnings, totals.deductions, totals.employerContributions] }], kind: 'horizontalBar', compatibleKinds: ['horizontalBar', 'bar'], valueFormat: value => money.format(value) },
  ] : []

  if (!canRead) return <Alert type="warning" showIcon message="Excel Payslips dashboard is not available for your role." />
  return <section className="hrms-dashboard excel-payslip-dashboard" aria-label="Excel Payslips dashboard">
    <PageHeaderPortal slot="dashboard-client-controls">
      {!clientScoped && (clients.length === 1 ? <span className="scoped-client-chip">{currentClient?.name || (clientsLoading ? 'Loading client…' : 'No accessible client')}</span> : <Select aria-label="Excel dashboard client" showSearch optionFilterProp="label" placeholder="Search or select client" value={clientId || undefined} loading={clientsLoading} disabled={clientsLoading} options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={setPreferredClient} style={{ width: 250, maxWidth: '100%' }} />)}
    </PageHeaderPortal>
    <PageHeaderPortal slot="dashboard-page-controls"><div className="excel-dashboard-controls">
      <Select aria-label="Salary month" placeholder="Salary month" value={pendingSelection?.month} disabled={loading || !months.length} options={months.map(month => ({ value: month, label: monthLabel(month) }))} onChange={month => { const batch = history.find(item => item.month === month); if (batch) setPreferredBatch(batch.id) }} className="excel-dashboard-month" />
      <Select aria-label="Saved payslip batch" placeholder="Saved batch" showSearch optionFilterProp="label" value={pendingSelection?.id} disabled={loading || !monthBatches.length} options={monthBatches.map(batch => ({ value: batch.id, label: batchLabel(batch) }))} onChange={setPreferredBatch} className="excel-dashboard-batch" />
      <Button aria-label="Refresh Excel Payslips dashboard" icon={<ReloadOutlined spin={loading || clientsLoading} />} disabled={loading || clientsLoading} onClick={() => setRevision(value => value + 1)}>Refresh</Button>
      <Button type="primary" icon={<FileExcelOutlined />} disabled={!clientId || loading} onClick={() => openBatch(selected?.id)}>{selected ? 'Open selected batch' : 'Open Excel Payslips'}</Button>
    </div></PageHeaderPortal>

    {(clientError || error) && <Alert type="error" showIcon message={clientError || error} action={preferredBatch && !clientError ? <Button size="small" onClick={() => setPreferredBatch('')}>Load latest batch</Button> : undefined} />}
    <DashboardEngine loading={clientsLoading || loading}>
      {!clientId ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={clientError ? 'Client access could not be loaded.' : 'No active client is available for Excel Payslips.'} /> : !selected || !totals ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={error ? 'Dashboard data is unavailable. Use Refresh to try again.' : 'No saved Excel Payslip batches for this client.'}><Button onClick={() => openBatch()}>Open Excel Payslips<ArrowRightOutlined /></Button></Empty> : <>
        <div className="excel-dashboard-context"><span><b>{monthLabel(selected.month)}</b><span>Batch #{selected.id.slice(0, 8)}</span><span title={selected.sourceFileName}>{selected.sourceFileName}</span></span><Tag color="blue">Selected batch only</Tag></div>
        <DashboardKpiGrid label="Selected Excel Payslip batch totals" items={[
          { key: 'payslips', label: 'Payslips', value: count.format(totals.rowCount), helper: 'Employee rows in this batch', icon: <TeamOutlined />, onClick: () => openBatch(selected.id) },
          { key: 'earnings', label: 'Earnings', value: money.format(totals.earnings), helper: 'Mapped earning components', icon: <WalletOutlined />, tone: 'success' },
          { key: 'deductions', label: 'Deductions', value: money.format(totals.deductions), helper: 'Employee deductions', icon: <MinusCircleOutlined />, tone: 'warning' },
          { key: 'net-pay', label: 'Net pay', value: money.format(totals.netPay), helper: 'Net amounts saved from Excel', icon: <FileExcelOutlined />, tone: 'primary', onClick: () => openBatch(selected.id) },
        ]} />
        <div className="excel-dashboard-details">
          <article><span>Previous-month net change</span><strong>{difference === null ? priorBatch ? 'Outside displayed trend' : 'No previous-month batch' : `${difference > 0 ? '+' : difference < 0 ? '−' : ''}${money.format(Math.abs(difference))}`}</strong>{prior ? <small>{percentage !== null ? `${Math.abs(percentage).toFixed(1)}% ${percentage > 0 ? 'higher' : percentage < 0 ? 'lower' : 'change'} · ` : ''}vs latest {monthLabel(prior.month)} batch <button type="button" onClick={() => setPreferredBatch(prior.batchId)}>#{prior.batchId.slice(0, 8)}</button></small> : priorBatch ? <small>Previous month is outside the displayed trend. <button type="button" onClick={() => openBatch(priorBatch.id)}>Open {monthLabel(priorBatch.month)} batch</button></small> : <small>A comparison appears when the preceding calendar month has a saved batch.</small>}</article>
          <article><span>Employer contributions</span><strong>{money.format(totals.employerContributions)}</strong><small>Shown separately from employee deductions.</small></article>
          <article><span>Batch readiness</span><div className="excel-dashboard-status"><Tag color={totals.hasCalculationSource ? 'green' : 'default'}>{totals.hasCalculationSource ? 'Calculation source saved' : 'Imported amounts only'}</Tag><Button type="link" onClick={() => openBatch(selected.id)}>{count.format(totals.reviewCount)} payslips need review</Button></div><small>Saved {savedAt(selected.createdAtUtc)}</small></article>
        </div>
        <DashboardChartGrid label="Excel Payslip batch analytics">{charts.map(chart => <DashboardChartCard key={chart.id} spec={chart} />)}</DashboardChartGrid>
        <DashboardSectionCard title="Saved batches" subtitle="Every upload stays separate. Select a batch to view its totals or open its payslips.">
          <DataTable rows={history} getRowId={row => row.id} exportFileName={`excel-payslip-batches-${clientId}`} pageSizeOptions={[10, 25, 50]} scrollY={350} columns={[
            { key: 'month', label: 'Salary month', render: row => monthLabel(row.month), width: 140 },
            { key: 'id', label: 'Batch', render: row => <Tag color={row.id === selected.id ? 'blue' : 'default'}>#{row.id.slice(0, 8)}</Tag>, width: 110 },
            { key: 'sourceFileName', label: 'Source workbook', width: 240 },
            { key: 'rowCount', label: 'Payslips', width: 100 },
            { key: 'createdAtUtc', label: 'Saved at', render: row => savedAt(row.createdAtUtc), width: 185 },
            { key: 'createdBy', label: 'Saved by', width: 200 },
          ]} actionsWidth={170} actions={row => <><Button size="small" disabled={row.id === selected.id} onClick={() => setPreferredBatch(row.id)}>View totals</Button><Button size="small" onClick={() => openBatch(row.id)}>Open</Button></>} />
        </DashboardSectionCard>
      </>}
    </DashboardEngine>
  </section>
}
